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
        _lastAnswerSource = "unknown";
    }

    private Dictionary<string, object?> BuildAgentRuntimeSnapshot()
    {
        var memorySummary = BuildAgentMemorySummary();

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
                ["usedSummaryFlow"] = _lastUsedSummaryFlow,
                ["answerSource"] = _lastAnswerSource
            },
            ["memorySummary"] = memorySummary,
            ["memory"] = new Dictionary<string, object?>
            {
                ["profile"] = _mem.MemoryProfile,
                ["schemaVersion"] = _mem.SchemaVersion,
                ["m1Lite"] = new Dictionary<string, object?>
                {
                ["hasCatalogSnapshot"] = _mem.CatalogSnapshotCache is not null,
                ["catalogCategoriesCount"] = _mem.CatalogSnapshotCache?.Categories?.Count ?? 0,
                ["hasCapabilitiesSnapshot"] = _mem.CapabilitiesCache is not null,
                ["isAdmin"] = _mem.CapabilitiesCache?.IsAdmin,
                ["knownDocumentsCount"] = _mem.WorkspaceKnownDocuments?.Count ?? 0
                },
                ["m3"] = new Dictionary<string, object?>
                {
                    ["hasPendingClarification"] = _mem.PendingClarification is not null,
                    ["pendingClarificationKind"] = _mem.PendingClarification?.Kind,
                    ["hasFocusedDocument"] = _mem.LastFocusedDocument is not null,
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
                    ["lastSourcesCount"] = _mem.LastSourcesUsed?.Count ?? 0,
                    ["hasResolvedCategory"] = _mem.LastResolvedCategory is not null,
                    ["presentedCategoriesCount"] = _mem.LastPresentedCategories?.Count ?? 0
                },
                ["m6"] = new Dictionary<string, object?>
                {
                    ["lastMode"] = _mem.LastMode,
                    ["lastRouterIntent"] = _mem.LastRouterIntent,
                    ["lastToolNamesCount"] = _mem.LastToolNames?.Count ?? 0,
                    ["lastRiskFlagsCount"] = _mem.LastRiskFlags?.Count ?? 0,
                    ["hasPlannerMemoryUpdate"] = !string.IsNullOrWhiteSpace(_mem.LastPlannerMemoryUpdate),
                    ["routerConfidence"] = _mem.LastRouterConfidence,
                    ["hasAdminOperation"] = _mem.LastAdminOperation is not null,
                    ["hasStagedDirectCommand"] = _mem.StagedDirectCommand is not null
                },
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

    private Dictionary<string, object?> BuildAgentMemorySummary()
    {
        return new Dictionary<string, object?>
        {
            ["profile"] = _mem.MemoryProfile,
            ["schemaVersion"] = _mem.SchemaVersion,
            ["cdcAlignment"] = "v3.1",
            ["persistence"] = new Dictionary<string, object?>
            {
                ["language"] = true,
                ["style"] = true,
                ["mode"] = false,
                ["focusedDocument"] = false,
                ["resolvedCategory"] = false
            },
            ["resetPolicy"] = new Dictionary<string, object?>
            {
                ["preservesM1Lite"] = true,
                ["preservesPreferences"] = true,
                ["clearsM3"] = true,
                ["clearsM6"] = true,
                ["resetsModeToAuto"] = true
            },
            ["workspace"] = new Dictionary<string, object?>
            {
                ["catalogCategoriesCount"] = _mem.CatalogSnapshotCache?.Categories?.Count ?? 0,
                ["knownDocumentsCount"] = _mem.WorkspaceKnownDocuments?.Count ?? 0,
                ["hasCapabilitiesSnapshot"] = _mem.CapabilitiesCache is not null
            },
            ["session"] = new Dictionary<string, object?>
            {
                ["hasFocusedDocument"] = _mem.LastFocusedDocument is not null,
                ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                ["hasResolvedCategory"] = _mem.LastResolvedCategory is not null,
                ["hasPendingClarification"] = _mem.PendingClarification is not null
            },
            ["execution"] = new Dictionary<string, object?>
            {
                ["mode"] = _mem.LastMode,
                ["hasRouterIntent"] = !string.IsNullOrWhiteSpace(_mem.LastRouterIntent),
                ["toolNamesCount"] = _mem.LastToolNames?.Count ?? 0,
                ["hasAdminOperation"] = _mem.LastAdminOperation is not null
            }
        };
    }

    private sealed record PendingClarificationPreparation(string EffectiveUserMessage, DocumentRefResolver.AnalysisResult? AnalysisOverride, bool Consumed);

    private PendingClarificationPreparation PrepareUserMessageForPendingClarification(
        IReadOnlyList<(string role, string content)> chatHistory,
        string? userMessage)
    {
        var safeUserMessage = userMessage ?? string.Empty;
        var pending = _mem.PendingClarification;
        if ((pending is null || string.IsNullOrWhiteSpace(pending.OriginalUserMessage))
            && TryInferPendingClarificationFromHistory(chatHistory, out var inferredPending))
        {
            pending = inferredPending;
        }

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
        var expectsGenericClarification = IsGenericClarificationKind(pending.Kind);

        var isExpectedAnswer = expectsDocumentAnswer
            ? DocumentRefResolver.LooksLikeDocumentReferenceAnswer(current, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef)
            : expectsTreeScope
                ? DocumentRefResolver.LooksLikeTreeScopeAnswer(current)
                : expectsRagProbeRefinement
                    ? current.Length >= 2
                    : expectsGenericClarification && LooksLikeGenericClarificationAnswer(current);

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
            : expectsGenericClarification
                ? $@"PREVIOUS_USER_REQUEST:
{pending.OriginalUserMessage}

USER_CLARIFICATION:
{current}

RESOLVED_REQUEST:
Continue the previous request using the clarification as the intended topic or scope."
            : $@"PREVIOUS_AMBIGUOUS_REQUEST:
{pending.OriginalUserMessage}

CLARIFICATION_ANSWER:
{current}";

        var analysisOverride = expectsRagProbeRefinement || expectsGenericClarification
            ? null
            : DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        ClearPendingClarification();
        return new PendingClarificationPreparation(effectiveUserMessage, analysisOverride, true);
    }

    private static bool IsGenericClarificationKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return true;

        return string.Equals(kind, "generic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "clarification", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic_scope", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "topic_refinement", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeGenericClarificationAnswer(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length < 2)
            return false;

        return s.Length <= 800;
    }

    private static bool TryInferPendingClarificationFromHistory(
        IReadOnlyList<(string role, string content)> chatHistory,
        out ToolMemory.PendingClarificationState? pending)
    {
        pending = null;
        if (chatHistory is null || chatHistory.Count < 2)
            return false;

        for (var assistantIndex = chatHistory.Count - 1; assistantIndex >= 1; assistantIndex--)
        {
            var assistantTurn = chatHistory[assistantIndex];
            if (!string.Equals(assistantTurn.role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(assistantTurn.content))
            {
                continue;
            }

            if (!LooksLikeAssistantClarificationQuestion(assistantTurn.content))
                return false;

            for (var userIndex = assistantIndex - 1; userIndex >= 0; userIndex--)
            {
                var userTurn = chatHistory[userIndex];
                if (!string.Equals(userTurn.role, "user", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(userTurn.content))
                {
                    continue;
                }

                pending = new ToolMemory.PendingClarificationState
                {
                    Kind = InferClarificationKindFromQuestion(assistantTurn.content),
                    OriginalUserMessage = userTurn.content.Trim(),
                    Hint = "recovered_from_chat_history",
                    Language = DetectMessageLanguage(assistantTurn.content),
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool LooksLikeAssistantClarificationQuestion(string? assistantMessage)
    {
        var s = (assistantMessage ?? string.Empty).Trim();
        if (s.Length == 0 || !s.Contains('?', StringComparison.Ordinal))
            return false;

        return Regex.IsMatch(
            s,
            @"(?i)(clarif|precis|pr[ée]cis|quel\s+sujet|quelle\s+sujet|which\s+topic|what\s+topic|quel\s+document|which\s+document|scope|perimetre|p[ée]rim[èe]tre|category|cat[ée]gorie)");
    }

    private static string InferClarificationKindFromQuestion(string? assistantMessage)
    {
        var s = assistantMessage ?? string.Empty;
        if (Regex.IsMatch(s, @"(?i)(document|doc\b|pdf|file|fichier)"))
            return "doc_reference";
        if (Regex.IsMatch(s, @"(?i)(scope|perimetre|p[ée]rim[èe]tre|category|cat[ée]gorie|dossier|folder|arborescence)"))
            return "tree_scope";

        return "generic";
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

        return Regex.IsMatch(s, @"^(?:yes|yeah|yep|oui|ok|okay|d['\u2019]accord|exactly|exact|precisely|exactement|pr[ée]cis[eé]ment|correct|c['\u2019]est\s+ça|that['\u2019]?s\s+right)$", RegexOptions.IgnoreCase);
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
                        Label = $"{label} (p.{ps}{(pe != ps ? $"–{pe}" : "")})"
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

    private static bool HasRagHits(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array
            && hits.GetArrayLength() > 0;
    }

    private static object BuildSourcesPayload(List<ToolMemory.SourceRef> sources)
        => new
        {
            sources = sources.Select(x => new { docPath = x.DocPath, pageStart = x.PageStart, pageEnd = x.PageEnd, label = x.Label }).ToList()
        };

    private static bool LooksLikeNoRagDataAnswer(string? answer)
    {
        var s = (answer ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(
            s,
            @"(?i)\b(?:je\s+n['’]ai\s+pas|aucun(?:e)?|pas\s+de|no\s+(?:specific\s+)?(?:data|document|source|information)|nothing\s+specific)\b.{0,120}\b(?:donn[ée]es?|documents?|sources?|information|data)\b");
    }

    internal static bool ShouldUseCuisineExtractiveAnswer(string query, ToolResults toolResults)
    {
        var hits = EnumerateRagHitSummaries(toolResults).Take(8).ToList();
        if (hits.Count == 0)
            return false;

        if (!hits.Any(static hit => IsCuisineDocPath(hit.DocPath)))
            return false;

        var normalizedQuery = NormalizeCuisineLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:recette|recettes|cuisine|cuisiner|repas|menu|menus|sauce|sauces|trempette|marinade|dessert|gateau|gâteau|chocolat|entrecote|entrecôte|steak|poulet|riz|courgette|courgettes|vegetarien|vegetarienne|végétarien|végétarienne|enfant|enfants|gouter|goûter)\b",
            RegexOptions.CultureInvariant);
    }

    internal static string BuildCuisineExtractiveAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => IsCuisineDocPath(hit.DocPath))
            .OrderByDescending(hit => ComputeCuisineHitRelevance(query, hit.Excerpt))
            .Take(4)
            .ToList();

        if (hits.Count == 0)
            return BuildRagEvidenceFallbackAnswer(toolResults, query, language);

        var normalizedQuery = NormalizeCuisineLookup(query);
        var isMeatSauceQuestion =
            (normalizedQuery.Contains("sauce", StringComparison.Ordinal) || normalizedQuery.Contains("sauces", StringComparison.Ordinal))
            && (normalizedQuery.Contains("entrecote", StringComparison.Ordinal)
                || normalizedQuery.Contains("entrecôte", StringComparison.Ordinal)
                || normalizedQuery.Contains("steak", StringComparison.Ordinal)
                || normalizedQuery.Contains("viande", StringComparison.Ordinal));

        var hasExactPairing = hits.Any(hit =>
        {
            var excerpt = NormalizeCuisineLookup(hit.Excerpt);
            return (excerpt.Contains("entrecote", StringComparison.Ordinal)
                    || excerpt.Contains("entrecôte", StringComparison.Ordinal)
                    || excerpt.Contains("steak", StringComparison.Ordinal))
                   && (excerpt.Contains("sauce", StringComparison.Ordinal)
                       || excerpt.Contains("trempette", StringComparison.Ordinal)
                       || excerpt.Contains("marinade", StringComparison.Ordinal));
        });

        var sb = new StringBuilder();
        sb.Append(BuildCuisineExtractiveHeader(language, isMeatSauceQuestion && !hasExactPairing));

        sb.AppendLine();
        foreach (var hit in hits)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = CollapseWhitespace(hit.Excerpt);
            if (excerpt.Length > 320)
                excerpt = excerpt[..320].TrimEnd() + "...";

            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeCuisineMealPlanningRequest(string? query)
    {
        var s = NormalizeCuisineLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var asksForPlan = Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|jours|repas|menu|menus|batch cooking|week|weekly|meal plan|meal prep|plan de repas)\b",
            RegexOptions.CultureInvariant);

        return asksForPlan && LooksLikeCuisineActionRequest(query);
    }

    private static string[] BuildCuisineMealPlanningQueries(string query)
        => new[]
        {
            "Menu Ingredients volaille champignons pommes darphin",
            "Menu Ingredients filet cabillaud crumble chorizo parmesan",
            "Menu Ingredients salade pates thon tomates",
            "Menu Ingredients chili con carne healthy",
            "Menu Ingredients veloute lentilles corail coco",
            "Menu Ingredients curry legumes pois chiches champignons"
        };

    private static string BuildCuisineMealPlanningAnswer(ToolResults toolResults, string language)
    {
        language = NormalizeLanguageCode(language);
        var recipes = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => IsCuisineDocPath(hit.DocPath))
            .Select(hit => new
            {
                Hit = hit,
                Title = ExtractCuisineRecipeTitle(hit.Excerpt)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => $"{x.Hit.DocPath}|{x.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(7)
            .ToList();

        if (recipes.Count == 0)
            return string.Empty;

        var header = language switch
        {
            "en" => "Here is a source-backed meal plan from the Cuisine documents. I only list recipes found in the excerpts:",
            "es" => "Aqui tienes una propuesta de comidas basada en las fuentes de Cocina. Solo incluyo recetas encontradas en los extractos:",
            "pt" => "Aqui esta uma proposta de refeicoes baseada nas fontes de Cozinha. Incluo apenas receitas encontradas nos excertos:",
            "de" => "Hier ist ein quellenbasierter Essensplan aus den Kuechendokumenten. Ich nenne nur Rezepte aus den Auszuegen:",
            "it" => "Ecco una proposta di pasti basata sulle fonti di Cucina. Includo solo ricette trovate negli estratti:",
            _ => "Voici une proposition de repas appuyee sur les documents Cuisine. Je liste uniquement des recettes retrouvees dans les extraits :"
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        for (var i = 0; i < recipes.Count; i++)
        {
            var day = language switch
            {
                "en" => $"Day {i + 1}",
                "es" => $"Dia {i + 1}",
                "pt" => $"Dia {i + 1}",
                "de" => $"Tag {i + 1}",
                "it" => $"Giorno {i + 1}",
                _ => $"Jour {i + 1}"
            };

            var hit = recipes[i].Hit;
            sb.Append("- ");
            sb.Append(day);
            sb.Append(" : ");
            sb.Append(recipes[i].Title);
            sb.Append(" (");
            sb.Append(string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.AppendLine(")");
        }

        var note = language switch
        {
            "en" => "Note: adapt quantities and shopping list from the source pages before cooking.",
            "es" => "Nota: adapta las cantidades y la lista de compra a partir de las paginas fuente antes de cocinar.",
            "pt" => "Nota: adapta as quantidades e a lista de compras a partir das paginas fonte antes de cozinhar.",
            "de" => "Hinweis: Mengen und Einkaufsliste vor dem Kochen anhand der Quellseiten anpassen.",
            "it" => "Nota: adatta quantita e lista della spesa dalle pagine fonte prima di cucinare.",
            _ => "Note : adapte les quantites et la liste de courses a partir des pages source avant de cuisiner."
        };
        sb.AppendLine(note);

        return sb.ToString().TrimEnd();
    }

    private static string ExtractCuisineRecipeTitle(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"^\d+", string.Empty, RegexOptions.CultureInvariant).Trim();
        var match = Regex.Match(
            text,
            @"(?i)\bMenu\s*(?<title>.+?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*€|Ingr[eé]dients)",
            RegexOptions.CultureInvariant);
        if (match.Success)
            return HumanizeRecipeTitle(match.Groups["title"].Value);

        return string.Empty;
    }

    private static string HumanizeRecipeTitle(string value)
    {
        var title = CollapseWhitespace(value);
        title = Regex.Replace(title, @"(?<=[a-zà-ÿ])(?=[A-ZÀ-Þ])", " ", RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"(?<=[A-ZÀ-Þ])(?=[A-ZÀ-Þ][a-zà-ÿ])", " ", RegexOptions.CultureInvariant);
        title = title
            .Replace("ESCALOPEDE", "ESCALOPE DE", StringComparison.OrdinalIgnoreCase)
            .Replace("CARNEHEALTHY", "CARNE HEALTHY", StringComparison.OrdinalIgnoreCase)
            .Replace("EQUILIBRESELON", "EQUILIBRE SELON", StringComparison.OrdinalIgnoreCase);
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', ':');
        return title;
    }

    internal static string BuildCuisineExtractiveHeader(string language, bool noExplicitPairing)
    {
        language = NormalizeLanguageCode(language);
        return (language, noExplicitPairing) switch
        {
            ("en", true) => "I did not find an explicit pairing with entrecote in the available excerpts. The Cuisine sources provide these documented leads:",
            ("en", false) => "Here are the leads found in the Cuisine documents, without adding ingredients or steps outside the sources:",
            ("es", true) => "No he encontrado una asociacion explicita con la entrecote en los extractos disponibles. Las fuentes de Cocina ofrecen estas pistas documentadas:",
            ("es", false) => "Estas son las pistas encontradas en los documentos de Cocina, sin anadir ingredientes ni pasos fuera de las fuentes:",
            ("pt", true) => "Nao encontrei uma associacao explicita com entrecote nos excertos disponiveis. As fontes de Cozinha fornecem estas pistas documentadas:",
            ("pt", false) => "Estas sao as pistas encontradas nos documentos de Cozinha, sem acrescentar ingredientes nem passos fora das fontes:",
            ("de", true) => "Ich habe in den verfuegbaren Auszuegen keine ausdrueckliche Kombination mit Entrecote gefunden. Die Kuechenquellen liefern diese belegten Hinweise:",
            ("de", false) => "Hier sind die Hinweise aus den Kuechendokumenten, ohne Zutaten oder Schritte ausserhalb der Quellen hinzuzufuegen:",
            ("it", true) => "Non ho trovato un abbinamento esplicito con l'entrecote negli estratti disponibili. Le fonti di Cucina forniscono queste indicazioni documentate:",
            ("it", false) => "Ecco le indicazioni trovate nei documenti di Cucina, senza aggiungere ingredienti o passaggi non presenti nelle fonti:",
            (_, true) => "Je n'ai pas trouve d'association explicite avec l'entrecote dans les extraits disponibles. Les sources Cuisine donnent plutot ces pistes documentees :",
            _ => "Voici les pistes trouvees dans les documents Cuisine, sans ajout d'ingredients ni d'etapes hors source :"
        };
    }

    private static bool IsCuisineDocPath(string? docPath)
        => (docPath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/')
            .StartsWith("Cuisine/", StringComparison.OrdinalIgnoreCase);

    private static int ComputeCuisineHitRelevance(string query, string? excerpt)
    {
        var normalizedQuery = NormalizeCuisineLookup(query);
        var normalizedExcerpt = NormalizeCuisineLookup(excerpt);
        var score = 0;

        if ((normalizedQuery.Contains("sauce", StringComparison.Ordinal) || normalizedQuery.Contains("sauces", StringComparison.Ordinal))
            && (normalizedExcerpt.Contains("sauce", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("sauces", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("trempette", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("trempettes", StringComparison.Ordinal)))
        {
            score += 4;
        }

        if ((normalizedQuery.Contains("entrecote", StringComparison.Ordinal)
             || normalizedQuery.Contains("steak", StringComparison.Ordinal)
             || normalizedQuery.Contains("viande", StringComparison.Ordinal))
            && (normalizedExcerpt.Contains("viande", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("viandes", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("boeuf", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("bœuf", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("steak", StringComparison.Ordinal)))
        {
            score += 3;
        }

        if ((normalizedQuery.Contains("chocolat", StringComparison.Ordinal) || normalizedQuery.Contains("dessert", StringComparison.Ordinal))
            && (normalizedExcerpt.Contains("chocolat", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("dessert", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("gateau", StringComparison.Ordinal)))
        {
            score += 3;
        }

        if ((normalizedQuery.Contains("enfant", StringComparison.Ordinal) || normalizedQuery.Contains("enfants", StringComparison.Ordinal))
            && (normalizedExcerpt.Contains("enfant", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("enfants", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("animateur", StringComparison.Ordinal)
                || normalizedExcerpt.Contains("animateurs", StringComparison.Ordinal)))
        {
            score += 3;
        }

        return score;
    }

    private static string NormalizeCuisineLookup(string? value)
        => CollapseWhitespace(value ?? string.Empty)
            .ToLowerInvariant()
            .Replace('à', 'a')
            .Replace('â', 'a')
            .Replace('ä', 'a')
            .Replace('ç', 'c')
            .Replace('é', 'e')
            .Replace('è', 'e')
            .Replace('ê', 'e')
            .Replace('ë', 'e')
            .Replace('î', 'i')
            .Replace('ï', 'i')
            .Replace('ô', 'o')
            .Replace('ö', 'o')
            .Replace('ù', 'u')
            .Replace('û', 'u')
            .Replace('ü', 'u')
            .Replace("œ", "oe", StringComparison.Ordinal);

    private static string BuildRagEvidenceFallbackAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        if (LooksLikeCuisineActionRequest(query)
            && EnumerateRagHitSummaries(toolResults).Any(static hit => IsCuisineDocPath(hit.DocPath)))
        {
            return BuildCuisineExtractiveAnswer(toolResults, query, language);
        }

        var hits = EnumerateRagHitSummaries(toolResults).Take(3).ToList();
        if (hits.Count == 0)
            return DeterministicAgentText.AnswerNotEnoughUsableInfo(language);

        var first = hits[0];
        var topic = NormalizeRagQueryForRetrieval(query);
        topic = string.IsNullOrWhiteSpace(topic) ? "ce sujet" : topic;
        var docLabel = string.IsNullOrWhiteSpace(first.DocName) ? first.DocPath : first.DocName;
        var excerpt = CollapseWhitespace(first.Excerpt);
        if (excerpt.Length > 260)
            excerpt = excerpt[..260].TrimEnd() + "...";

        return language switch
        {
            "en" => $"I did find document evidence about {topic}. The strongest hit is {docLabel}, page {first.PageStart}; its excerpt mentions {excerpt}",
            "es" => $"Sí, he encontrado elementos documentales sobre {topic}. El mejor resultado es {docLabel}, página {first.PageStart}; el fragmento menciona {excerpt}",
            "pt" => $"Sim, encontrei elementos documentais sobre {topic}. O melhor resultado é {docLabel}, página {first.PageStart}; o excerto menciona {excerpt}",
            "de" => $"Ja, ich habe Dokumentbelege zu {topic} gefunden. Der stärkste Treffer ist {docLabel}, Seite {first.PageStart}; der Auszug erwähnt {excerpt}",
            "it" => $"Sì, ho trovato elementi documentali su {topic}. Il risultato più forte è {docLabel}, pagina {first.PageStart}; l'estratto menziona {excerpt}",
            _ => $"Oui, j’ai bien trouvé des éléments documentaires sur {topic}. Le meilleur résultat est {docLabel}, page {first.PageStart}; l’extrait mentionne {excerpt}"
        };
    }

    private sealed record RagHitSummary(string DocPath, string DocName, int PageStart, string Excerpt);

    private static IEnumerable<RagHitSummary> EnumerateRagHitSummaries(ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(x => x.ToolName is "rag.search" or "rag.multi_search"))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("hits", out var hits)
                || hits.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var h in hits.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object)
                    continue;

                var docPath = TryGetString(h, "docPath") ?? string.Empty;
                var docName = TryGetString(h, "docName") ?? Path.GetFileName(docPath);
                var pageStart = TryGetInt(h, "pageStart") ?? 1;
                var excerpt = TryGetString(h, "excerpt") ?? TryGetString(h, "text") ?? string.Empty;
                yield return new RagHitSummary(docPath, docName, pageStart, excerpt);
            }
        }
    }

    private static string NormalizeRagQueryForRetrieval(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return string.Empty;

        var clarification = Regex.Match(s, @"(?is)\bUSER_CLARIFICATION:\s*(?<topic>.+?)(?:\s+RESOLVED_REQUEST:|$)");
        if (clarification.Success)
            s = clarification.Groups["topic"].Value.Trim();

        var topicPatterns = new[]
        {
            @"(?i)\b(?:document|documents?|source|sources?)\s+(?:qui\s+)?(?:parle|parlent|mentionne|mentionnent|traite|traitent)\s+(?:de|du|des|d['’])?\s*(?<topic>[^?.!;]+)",
            @"(?i)\b(?:parle|parlent|mentionne|mentionnent|traite|traitent)\s+(?:de|du|des|d['’])?\s*(?<topic>[^?.!;]+)",
            @"(?i)\b(?:about|regarding|concerning)\s+(?<topic>[^?.!;]+)"
        };

        foreach (var pattern in topicPatterns)
        {
            var match = Regex.Match(s, pattern);
            if (match.Success)
            {
                var topic = CleanupStandaloneTopic(match.Groups["topic"].Value);
                if (!string.IsNullOrWhiteSpace(topic))
                    return topic;
            }
        }

        return CleanupStandaloneTopic(s);
    }

    private static bool LooksLikeStandaloneDocumentaryTopic(string? userMessage)
    {
        var s = NormalizeRagQueryForRetrieval(userMessage);
        if (s.Length < 3 || s.Length > 80)
            return false;
        if (s.Contains('?', StringComparison.Ordinal))
            return false;
        if (Regex.IsMatch(s, @"(?i)^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay|oui|non|qui\s+es[- ]?tu|comment\s+vas[- ]?tu)$"))
            return false;
        if (!Regex.IsMatch(s, @"\p{L}"))
            return false;

        var tokenCount = Regex.Matches(s, @"[\p{L}\p{N}]+").Count;
        if (tokenCount is < 1 or > 6)
            return false;

        return true;
    }

    private static string CleanupStandaloneTopic(string? value)
    {
        var s = CollapseWhitespace(value ?? string.Empty).Trim(' ', '.', '?', '!', ':', ';', '"', '\'');
        s = Regex.Replace(s, @"(?i)^(?:l['’]|d['’]|de\s+l['’]|de\s+la\s+|du\s+|des\s+|le\s+|la\s+|les\s+|un\s+|une\s+)", string.Empty).Trim();
        s = Regex.Replace(s, @"(?i)\s+(?:pr[ée]cis[ée]ment|exactement)$", string.Empty).Trim();
        return s;
    }

    private static string CollapseWhitespace(string value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static JsonElement NormalizeRagHits(JsonElement raw)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            var sourceHits = default(JsonElement);
            if (raw.TryGetProperty("hits", out var hits0) && hits0.ValueKind == JsonValueKind.Array)
            {
                sourceHits = hits0;
            }
            else if (raw.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                sourceHits = items;
            }
            else
            {
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;
            }

            var list = new List<object>();
            foreach (var it in sourceHits.EnumerateArray().Take(RagWriterMaxHits))
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? "";
                docPath = (docPath ?? "").Trim().Replace('\\', '/').TrimStart('/');

                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);
                var score = TryGetDouble(it, "score") ?? 0.0;

                var ps = TryGetInt(it, "pageStart") ?? TryGetInt(it, "page") ?? 1;
                var pe = TryGetInt(it, "pageEnd") ?? ps;

                var text = TruncateForPrompt(
                    TryGetString(it, "excerpt") ?? TryGetString(it, "text") ?? "",
                    RagWriterMaxExcerptChars);

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
