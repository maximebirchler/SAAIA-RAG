using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void ResetLastTurnDiagnostics()
    {
        _lastRouterMs = 0;
        _lastToolsMs = 0;
        _lastWriterMs = 0;
        _lastTotalMs = 0;
        _lastCriticMs = 0;
        _lastCriticStatus = null;
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;
        _lastUsedGeneralChatPrompt = false;
        _lastUsedInventoryRendered = false;
        _lastUsedSummaryFlow = false;
        _lastResponseFormat = "auto";
        _lastEffectiveMode = "auto";
        _lastWriterToolNames = new List<string>();
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)>();
    }

    private Dictionary<string, object?> BuildAgentRuntimeSnapshot()
    {
        return new Dictionary<string, object?>
        {
            ["supported"] = true,
            ["routerMs"] = _lastRouterMs,
            ["toolsMs"] = _lastToolsMs,
            ["writerMs"] = _lastWriterMs,
            ["totalMs"] = _lastTotalMs,
            ["language"] = _mem.LastLanguage,
            ["critic"] = new Dictionary<string, object?>
            {
                ["enabled"] = _lastCriticMs > 0 || !string.IsNullOrWhiteSpace(_lastCriticStatus) || _lastCriticEligible,
                ["eligible"] = _lastCriticEligible,
                ["durationMs"] = _lastCriticMs,
                ["status"] = _lastCriticStatus,
                ["revisedAnswer"] = _lastCriticRevisedAnswer,
                ["warning"] = _lastCriticWarning,
                ["skipReason"] = _lastCriticSkipReason
            },
            ["tools"] = _lastToolDurations.Select(x => new Dictionary<string, object?>
            {
                ["tool"] = x.tool,
                ["durationMs"] = x.durationMs,
                ["ok"] = x.ok
            }).ToList(),
            ["turn"] = new Dictionary<string, object?>
            {
                ["intent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["writerToolNames"] = _lastWriterToolNames,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate,
                ["routerConfidence"] = _mem.LastRouterConfidence,
                ["riskFlags"] = _mem.LastRiskFlags,
                ["mode"] = _lastEffectiveMode,
                ["responseFormat"] = _lastResponseFormat
            },
            ["session"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["qa"] = new Dictionary<string, object?>
            {
                ["usedGeneralChatPrompt"] = _lastUsedGeneralChatPrompt,
                ["usedInventoryRendered"] = _lastUsedInventoryRendered,
                ["usedSummaryFlow"] = _lastUsedSummaryFlow
            },
            ["memory"] = new Dictionary<string, object?>
            {
                ["hasPendingClarification"] = _mem.PendingClarification is not null,
                ["pendingClarificationKind"] = _mem.PendingClarification?.Kind,
                ["lastMode"] = _mem.LastMode,
                ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
                {
                    ["docId"] = _mem.LastFocusedDocument.DocId,
                    ["docPath"] = _mem.LastFocusedDocument.DocPath,
                    ["docName"] = _mem.LastFocusedDocument.DocName,
                    ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                    ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
                },
                ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                ["pdfMapSize"] = _mem.PdfMap?.Count ?? 0,
                ["lastSourcesCount"] = _mem.LastSourcesUsed?.Count ?? 0
            }
        };
    }

    private sealed record PendingClarificationPreparation(string EffectiveUserMessage, DocumentRefResolver.AnalysisResult? AnalysisOverride, bool Consumed);

    private PendingClarificationPreparation PrepareUserMessageForPendingClarification(string? userMessage)
    {
        var safeUserMessage = userMessage ?? string.Empty;
        var pending = _mem.PendingClarification;
        if (pending is null || string.IsNullOrWhiteSpace(pending.OriginalUserMessage))
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        var current = safeUserMessage.Trim();
        if (current.Length == 0)
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        if (DocumentRefResolver.IsRepairMessage(current) || TryDetectExplicitLanguageSwitch(current, out _))
        {
            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var expectsDocumentAnswer = string.Equals(pending.Kind, "doc_reference", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pending.Kind, "document_reference", StringComparison.OrdinalIgnoreCase);
        var expectsTreeScope = string.Equals(pending.Kind, "tree_scope", StringComparison.OrdinalIgnoreCase);
        var expectsRagProbeRefinement = string.Equals(pending.Kind, "rag_probe", StringComparison.OrdinalIgnoreCase);

        var isExpectedAnswer = expectsDocumentAnswer
            ? DocumentRefResolver.LooksLikeDocumentReferenceAnswer(current, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef)
            : expectsTreeScope
                ? DocumentRefResolver.LooksLikeTreeScopeAnswer(current)
                : expectsRagProbeRefinement && current.Length >= 2;

        if (!isExpectedAnswer)
        {
            if (LooksLikeClarificationMetaAck(current))
                return new PendingClarificationPreparation(safeUserMessage, null, false);

            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var effectiveUserMessage = expectsRagProbeRefinement
            ? $@"PREVIOUS_DOCUMENTARY_REQUEST:
{pending.OriginalUserMessage}

RETRIEVAL_REFINEMENT:
{current}"
            : $@"PREVIOUS_AMBIGUOUS_REQUEST:
{pending.OriginalUserMessage}

CLARIFICATION_ANSWER:
{current}";

        var analysisOverride = expectsRagProbeRefinement
            ? null
            : DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        ClearPendingClarification();
        return new PendingClarificationPreparation(effectiveUserMessage, analysisOverride, true);
    }

    private void RememberPendingClarification(string kind, string originalUserMessage, string? hint, string? language)
    {
        _mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "generic" : kind.Trim(),
            OriginalUserMessage = originalUserMessage ?? string.Empty,
            Hint = hint,
            Language = language,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void ClearPendingClarification()
        => _mem.PendingClarification = null;

    private static bool LooksLikeClarificationMetaAck(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"^(?:yes|yeah|yep|oui|ok|okay|d['â€™]accord|exactly|exact|precisely|exactement|pr[Ã©e]cis[eÃ©]ment|correct|c['â€™]est\s+Ã§a|that['â€™]?s\s+right)$", RegexOptions.IgnoreCase);
    }

    private async Task<string> GenerateClarificationResponseAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        string clarificationKind,
        string? hint,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        var system = PromptCatalog.BuildClarificationSystemPrompt(language, clarificationKind, hint);
        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}
";

        return await CompleteTextResponseAsync(system, user, ct, onDelta).ConfigureAwait(false);
    }

    private async Task<string> EnsureAnswerMatchesRequestedLanguageAsync(string answer, string requestedLanguage, CancellationToken ct)
    {
        var trimmed = (answer ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var targetLanguage = NormalizeLanguageCode(requestedLanguage);
        var detectedLanguageRaw = DetectMessageLanguage(trimmed);
        if (string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrWhiteSpace(detectedLanguageRaw))
            return trimmed;

        var detectedLanguage = NormalizeLanguageCode(detectedLanguageRaw);
        if (string.Equals(detectedLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        try
        {
            var system = $@"You are SAAIA assistant.
Translate the provided assistant answer faithfully.
Target language: {targetLanguage}
Rules:
- Keep the same meaning and tone.
- Do not add or remove facts.
- Return plain text only.";

            var translated = await _llm.CompleteAsync(new[]
            {
                ("system", system),
                ("user", trimmed)
            }, forceJson: false, ct).ConfigureAwait(false);

            translated = (translated ?? string.Empty).Replace("**", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(translated) ? trimmed : translated;
        }
        catch
        {
            return trimmed;
        }
    }

    private async Task<string> GenerateRepairResponseAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        var system = PromptCatalog.BuildRepairSystemPrompt(language);
        var user = $@"
LAST_USER_MESSAGE:
{_mem.LastUserMessage}

LAST_ASSISTANT_ANSWER:
{_mem.LastAssistantAnswer}

LAST_ROUTER_INTENT:
{_mem.LastRouterIntent}

LAST_TOOLS:
{string.Join(", ", _mem.LastToolNames ?? new List<string>())}

LAST_ROUTER_CONFIDENCE:
{(_mem.LastRouterConfidence.HasValue ? _mem.LastRouterConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}

LAST_REASONING_TRACE_PUBLIC:
{string.Join(" | ", _mem.LastReasoningTracePublic ?? new List<string>())}

LAST_MEMORY_UPDATE:
{_mem.LastPlannerMemoryUpdate}

CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

CURRENT_USER_MESSAGE:
{userMessage}
";

        return await CompleteTextResponseAsync(system, user, ct, onDelta).ConfigureAwait(false);
    }

    private async Task<string> CompleteTextResponseAsync(string system, string user, CancellationToken ct, Action<string>? onDelta)
    {
        if (onDelta is not null)
        {
            var streamed = new StringBuilder();
            await _llm.StreamAsync(new[] { ("system", system), ("user", user) }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);

            var txt = streamed.ToString().Replace("**", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(txt))
                return txt;
        }

        var raw = await _llm.CompleteAsync(new[] { ("system", system), ("user", user) }, forceJson: false, ct).ConfigureAwait(false);
        return (raw ?? string.Empty).Replace("**", string.Empty).Trim();
    }

    private void RememberTurnState(string userMessage, string assistantAnswer, string? routerIntent, IEnumerable<string>? toolNames, IEnumerable<string>? reasoningTracePublic)
    {
        _mem.LastUserMessage = userMessage;
        _mem.LastAssistantAnswer = assistantAnswer;
        _mem.LastRouterIntent = routerIntent;
        _mem.LastToolNames = toolNames?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        _mem.LastReasoningTracePublic = reasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

        var hasExplicitLanguageSwitch = TryDetectExplicitLanguageSwitch(userMessage, out var requestedLanguage);
        var isOneShotTranslation = string.Equals(routerIntent, "meta.translate_last_answer", StringComparison.OrdinalIgnoreCase)
            || hasExplicitLanguageSwitch;

        var answerLanguage = isOneShotTranslation && !string.IsNullOrWhiteSpace(requestedLanguage)
            ? requestedLanguage
            : DetectMessageLanguage(assistantAnswer);

        if (!string.IsNullOrWhiteSpace(answerLanguage))
        {
            _mem.LastAnswerLanguage = answerLanguage;
            if (!isOneShotTranslation)
                _mem.LastLanguage = answerLanguage;
        }

        if (!isOneShotTranslation)
        {
            var userLanguage = DetectMessageLanguage(userMessage);
            if (string.IsNullOrWhiteSpace(userLanguage))
                userLanguage = "fr";

            _mem.LastUserDetectedLanguage = userLanguage;
        }
    }


    private static bool TryParseAnswerEnvelope(string json, out string? finalAnswer, out List<ToolMemory.SourceRef>? sources)
    {
        finalAnswer = null;
        sources = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("finalAnswer", out var a) && a.ValueKind == JsonValueKind.String)
                finalAnswer = a.GetString();
            else
                finalAnswer = "";

            if (root.TryGetProperty("sources", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ToolMemory.SourceRef>();
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    var docPath = s.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;

                    var ps = s.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = s.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
                    var label = s.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

                    list.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath,
                        PageStart = ps,
                        PageEnd = pe,
                        Label = label
                    });
                }

                sources = list;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceFromResolveResult(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "sources.resolve");
            if (item is null) return null;

            var res = item.Result;
            if (res.ValueKind != JsonValueKind.Object) return null;
            if (!res.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object) return null;

            var docPath = src.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(docPath)) return null;

            var ps = src.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
            var pe = src.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
            var label = src.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

            return new ToolMemory.SourceRef
            {
                DocPath = docPath,
                PageStart = ps,
                PageEnd = pe,
                Label = string.IsNullOrWhiteSpace(label) ? $"{docPath} (p.{ps})" : label
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromRagHits(ToolResults toolResults)
    {
        try
        {
            // Accept both rag.search and rag.multi_search results.
            var candidates = toolResults.Items
                .Where(x => x.ToolName is "rag.search" or "rag.multi_search")
                .Select(x => x.Result)
                .ToList();

            var sources = new List<ToolMemory.SourceRef>();
            foreach (var res in candidates)
            {
                if (res.ValueKind != JsonValueKind.Object) continue;
                if (!res.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;

                foreach (var h in hits.EnumerateArray().Take(8))
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var docPath = h.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;
                    var ps = h.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = h.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;

                    var label = h.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String ? (dn.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(label)) label = Path.GetFileName(docPath);

                    sources.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath.Replace('\\', '/'),
                        PageStart = ps,
                        PageEnd = pe,
                        Label = $"{label} (p.{ps}{(pe != ps ? $"â€“{pe}" : "")})"
                    });
                }
            }

            // Dedup by docPath + page
            return sources
                .GroupBy(s => $"{s.DocPath}|{s.PageStart}|{s.PageEnd}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            return new List<ToolMemory.SourceRef>();
        }
    }

    private static JsonElement NormalizeRagHits(JsonElement raw)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            // Already normalized
            if (raw.TryGetProperty("hits", out var hits0) && hits0.ValueKind == JsonValueKind.Array)
                return raw;

            // Backend often returns { items: [...] }
            if (!raw.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            var list = new List<object>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? "";
                docPath = (docPath ?? "").Trim().Replace('\\', '/').TrimStart('/');

                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);
                var score = TryGetDouble(it, "score") ?? 0.0;

                var ps = TryGetInt(it, "pageStart") ?? TryGetInt(it, "page") ?? 1;
                var pe = TryGetInt(it, "pageEnd") ?? ps;

                var text = TryGetString(it, "text") ?? TryGetString(it, "excerpt") ?? "";
                if (text.Length > 320) text = text.Substring(0, 320) + "â€¦";

                list.Add(new
                {
                    docPath,
                    docName,
                    pageStart = ps,
                    pageEnd = pe,
                    excerpt = text,
                    score
                });
            }

            var payload = new { hits = list };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return null;
    }

    private static string BuildStatsFallbackAnswer(JsonElement result, string language)
    {
        language = NormalizeLanguageCode(language);
        var totalDocuments = TryGetInt(result, "totalDocuments") ?? 0;
        var maxDepth = TryGetInt(result, "maxDepth") ?? 0;
        var totalFolders = TryGetInt(result, "totalNonEmptyFolders") ?? 0;
        var scopePath = NormalizeCategoryPathArg(TryGetString(result, "scopePath"));
        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.StatsTitle(language, scopePath));
        sb.AppendLine(DeterministicAgentText.StatsIndexedDocuments(totalDocuments, language));
        sb.AppendLine(DeterministicAgentText.StatsTotalFolders(totalFolders, language));
        var emptyFolders = TryGetInt(result, "totalEmptyFolders")
                           ?? TryGetInt(result, "emptyFolderCount")
                           ?? TryGetInt(result, "emptyFolders")
                           ?? TryGetInt(result, "emptyCount");
        if (emptyFolders.HasValue)
            sb.AppendLine(DeterministicAgentText.StatsEmptyFolders(emptyFolders.Value, language));
        AppendFolderDepthLines(sb, result, language);
        sb.AppendLine(DeterministicAgentText.StatsMainStructure(language));

        var appendedRootLine = false;
        if (result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in rf.EnumerateArray())
            {
                var name = TryGetString(x, "name") ?? TryGetString(x, "path") ?? string.Empty;
                var total = TryGetInt(x, "totalDocuments") ?? 0;
                var direct = TryGetInt(x, "directDocuments") ?? 0;
                var sub = TryGetInt(x, "subfolderCount") ?? 0;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                sb.AppendLine(DeterministicAgentText.RootFolderLine(name, total, direct, sub, language));
                appendedRootLine = true;
            }
        }

        if (!appendedRootLine)
            sb.AppendLine(DeterministicAgentText.NoSubfoldersInScope(language));

        return sb.ToString().TrimEnd();
    }

    private static void AppendFolderDepthLines(StringBuilder sb, JsonElement result, string language)
    {
        if (!result.TryGetProperty("foldersByDepth", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            var topLevel = TryGetInt(result, "topLevelFolderCount");
            if (topLevel.HasValue)
            {
                sb.AppendLine(DeterministicAgentText.FirstLevelFolders(topLevel.Value, language));
            }
            return;
        }

        var separator = NormalizeLanguageCode(language) == "fr" ? " : " : ": ";
        foreach (var item in arr.EnumerateArray().OrderBy(x => TryGetInt(x, "depth") ?? 0))
        {
            var depth = TryGetInt(item, "depth") ?? 0;
            var count = TryGetInt(item, "folderCount") ?? 0;
            if (depth <= 0)
                continue;

            sb.AppendLine($"- {DeterministicAgentText.FolderDepthLabel(depth, language)}{separator}{count}");
        }
    }


    private static bool? TryGetBoolProp(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

}
