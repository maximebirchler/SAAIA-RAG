using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
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
                        Label = $"{label} (p.{ps}{(pe != ps ? $"-{pe}" : "")})"
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

    private static List<ToolMemory.SourceRef> DeriveSourcesFromPlanningHits(ToolResults toolResults, string? query = null)
    {
        var sourceHits = EnumerateRagHitSummaries(toolResults).ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        var planningSources = sourceHits
            .Select(hit => new
            {
                Hit = hit,
                Title = ExtractPlanItemTitleV2(GetPlanExtractionText(hit))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => $"{x.Hit.DocPath}|{x.Hit.PageStart}|{x.Hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Hit)
            .Take(8)
            .Select(hit =>
            {
                var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                return new ToolMemory.SourceRef
                {
                    DocPath = hit.DocPath.Replace('\\', '/'),
                    PageStart = hit.PageStart,
                    PageEnd = hit.PageEnd,
                    Label = $"{label} (p.{hit.PageStart}{(hit.PageEnd != hit.PageStart ? $"-{hit.PageEnd}" : "")})"
                };
            })
            .ToList();

        return planningSources.Count > 0
            ? planningSources
            : DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
    }

    private static object BuildSourcesPayload(List<ToolMemory.SourceRef> sources)
        => new
        {
            sources = sources.Select(x => new { docPath = x.DocPath, pageStart = x.PageStart, pageEnd = x.PageEnd, label = x.Label }).ToList()
        };

    private static List<ToolMemory.SourceRef> DeriveSourcesFromRankedRagHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .OrderByDescending(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)))
            .ToList();

        var requestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var exactTitleHits = hits
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!))
                .ToList();
            if (exactTitleHits.Count > 0)
                hits = exactTitleHits;
        }
        else
        {
            hits = FilterHitsToDominantTopLevel(hits, query).ToList();
        }

        return hits
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(maxSources)
            .Select(hit =>
            {
                var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                return new ToolMemory.SourceRef
                {
                    DocPath = hit.DocPath.Replace('\\', '/'),
                    PageStart = hit.PageStart,
                    PageEnd = hit.PageEnd,
                    Label = $"{label} (p.{hit.PageStart}{(hit.PageEnd != hit.PageStart ? $"-{hit.PageEnd}" : "")})"
                };
            })
            .ToList();
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromExtractiveHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        var allHits = EnumerateRagHitSummaries(toolResults).ToList();
        var requestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(requestedTitle)
            && allHits.Count > 0
            && !RagHitsContainRequestedTitle(allHits, requestedTitle!))
        {
            return DeriveSourcesFromMissingExactItemCloseLeads(requestedTitle!, allHits);
        }

        var sourceLimit = maxSources;
        if (LooksLikeRankingDocumentaryRequest(query))
            sourceLimit = Math.Min(sourceLimit, 4);
        else if (LooksLikeSourceBackedQuantityScalingRequest(query))
            sourceLimit = Math.Min(sourceLimit, 1);
        else if (LooksLikeSourceBackedAdaptationRequest(query))
            sourceLimit = Math.Min(sourceLimit, 5);

        List<RagHitSummary> selectedHits;
        if (LooksLikeSourceBackedQuantityScalingRequest(query) && TryExtractTargetServingCount(query, out var targetServings))
        {
            selectedHits = SelectQuantityScalingCandidates(EnumerateRagHitSummaries(toolResults), query, targetServings)
                .Select(candidate => candidate.Hit)
                .Take(sourceLimit)
                .ToList();
            if (selectedHits.Count == 0)
                selectedHits = SelectSourceBackedExtractiveHits(toolResults, query, sourceLimit).ToList();
        }
        else
        {
            selectedHits = SelectSourceBackedExtractiveHits(toolResults, query, sourceLimit).ToList();
        }
        if (!string.IsNullOrWhiteSpace(requestedTitle) && LooksLikeStructuredItemCardRequest(query))
        {
            selectedHits = selectedHits
                .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle!, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }
        else if (LooksLikeSourceBackedAdaptationRequest(query))
        {
            selectedHits = RankSourceBackedAdaptationHits(selectedHits, query)
                .Take(sourceLimit)
                .ToList();
        }

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            var compliantHits = selectedHits
                .Where(hit => !RagHitContainsAnyExcludedTerm(hit, excludedTerms))
                .ToList();
            if (compliantHits.Count > 0)
                selectedHits = compliantHits;
        }

        return selectedHits
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(sourceLimit)
            .Select(hit =>
            {
                var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                return new ToolMemory.SourceRef
                {
                    DocPath = hit.DocPath.Replace('\\', '/'),
                    PageStart = hit.PageStart,
                    PageEnd = hit.PageEnd,
                    Label = $"{label} (p.{hit.PageStart}{(hit.PageEnd != hit.PageStart ? $"-{hit.PageEnd}" : "")})"
                };
            })
            .ToList();
    }

    private static IReadOnlyList<RagHitSummary> FilterHitsToDominantTopLevel(IReadOnlyList<RagHitSummary> hits, string? query)
    {
        if (hits.Count < 4 || ShouldPreserveCrossCategoryRagHits(query))
            return hits;

        var window = hits
            .Take(Math.Min(8, hits.Count))
            .Select((hit, index) => new
            {
                Hit = hit,
                Rank = index,
                TopLevel = ExtractTopLevelDocPath(hit.DocPath)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.TopLevel))
            .ToList();
        if (window.Count < 4)
            return hits;

        var groups = window
            .GroupBy(item => item.TopLevel, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                TopLevel = group.Key,
                Count = group.Count(),
                BestRank = group.Min(item => item.Rank)
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.BestRank)
            .ToList();
        if (groups.Count < 2 || groups[0].Count < 3)
            return hits;

        var best = groups[0];
        var secondCount = groups.Count > 1 ? groups[1].Count : 0;
        var share = best.Count / (double)window.Count;
        if (share < 0.55 && best.Count < secondCount + 2)
            return hits;

        var filtered = hits
            .Where(hit => string.Equals(ExtractTopLevelDocPath(hit.DocPath), best.TopLevel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return filtered.Count >= 2 ? filtered : hits;
    }

    private static bool ShouldPreserveCrossCategoryRagHits(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:compare|comparer|comparaison|comparatif|comparative|versus|different(?:es?)?|plusieurs|toutes?|tous|global|ensemble|categories?|categories|dossiers?|catalogue|across|all|multiple|various|varios|varias|vários|verschiedene|alle|tutti|tutte)\b",
            RegexOptions.CultureInvariant);
    }

    private static string ExtractTopLevelDocPath(string? docPath)
    {
        var normalized = (docPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
            return string.Empty;

        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : string.Empty;
    }

    private static bool LooksLikeNoRagDataAnswer(string? answer)
    {
        var s = (answer ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:pas\s+assez\s+d['\u2019]informations?|informations?\s+n[ée]cessaires?.{0,100}(?:pas|non)\s+disponibles?|not\s+enough\s+information|insufficient\s+(?:data|information|sources)|(?:donn[ée]es?|sources?|informations?)\s+(?:insuffisantes?|non\s+disponibles?))\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var noDataPattern =
            @"(?i)\b(?:je\s+n['\u2019]ai\s+pas|aucun(?:e)?|pas\s+de|pas\s+assez\s+d['\u2019]informations?|informations?\s+n[ée]cessaires?.{0,80}(?:pas|non)\s+disponibles?|(?:donn[ée]es?|sources?|informations?)\s+(?:insuffisantes?|non\s+disponibles?)|no\s+(?:specific\s+)?(?:data|document|source|information)|not\s+enough\s+information|insufficient\s+(?:data|information|sources)|nothing\s+specific)\b.{0,160}\b(?:donn[ée]es?|documents?|sources?|information|data|disponibles?|available|suffisantes?)\b";

        return Regex.IsMatch(s, noDataPattern, RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDegenerateLlmOutput(string? answer)
    {
        var raw = answer ?? string.Empty;
        var s = CollapseWhitespace(raw);
        if (s.Length == 0)
            return false;

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:crit[e\u00e8]res?\s+attendus?|validation\s+points?|expected\s+answer|expected\s+criteria)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"https?://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            && Regex.IsMatch(s, @"(?i)\b(?:sources?|documents?|\.pdf|p\.\s*\d+)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (s.Length < 160)
            return false;

        var repeatedLine = Regex.Split(raw, @"\r?\n")
            .Select(CollapseWhitespace)
            .Where(line => line.Length >= 18)
            .GroupBy(line => line, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() >= 3);
        if (repeatedLine)
            return true;

        var words = Regex.Matches(NormalizeLexicalLookup(s), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => word.Length > 0)
            .ToList();

        if (words.Count < 32)
            return false;

        var uniqueRatio = words.Distinct(StringComparer.Ordinal).Count() / (double)words.Count;
        if (words.Count >= 70 && uniqueRatio < 0.34)
            return true;

        for (var n = 3; n <= 8; n++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i <= words.Count - n; i++)
            {
                var key = string.Join(' ', words.Skip(i).Take(n));
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            if (counts.Values.Any(count => count >= 4 && count * n >= Math.Max(18, words.Count / 4)))
                return true;
        }

        return false;
    }

    internal static bool ShouldUseSourceBackedExtractiveAnswer(string query, ToolResults toolResults)
    {
        var hits = EnumerateRagHitSummaries(toolResults).Take(8).ToList();
        if (hits.Count == 0)
            return false;

        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
            return true;

        if (Regex.IsMatch(
                normalizedQuery,
                @"\b(?:fiche|fiches|ingredient|ingredients|ingrédient|ingrédients|etape|etapes|étape|étapes|recipe|recipes|receta|recetas|receita|receitas|rezept|rezepte|ricetta|ricette)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return LooksLikeSourceBackedActionRequest(query)
            || ExtractQuerySignalTerms(normalizedQuery).Any();
    }

    internal static string BuildSourceBackedExtractiveAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .OrderBy(hit => LooksLikeNavigationOnlyHit(hit) ? 1 : 0)
            .ThenByDescending(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)))
            .ThenByDescending(hit => hit.Score)
            .ToList();

        if (allHits.Count == 0)
            return BuildRagEvidenceFallbackAnswer(toolResults, query, language);

        var requestedTitle = TryExtractRequestedItemTitle(query);
        var isQuantityScalingRequest = LooksLikeSourceBackedQuantityScalingRequest(query);
        if (!isQuantityScalingRequest
            && !string.IsNullOrWhiteSpace(requestedTitle)
            && !RagHitsContainRequestedTitle(allHits, requestedTitle))
        {
            return BuildMissingExactItemAnswer(language, requestedTitle!, allHits);
        }

        var hits = SelectSourceBackedExtractiveHits(toolResults, query, maxHits: 4).ToList();

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            var compliantHits = hits
                .Where(hit => !RagHitContainsAnyExcludedTerm(hit, excludedTerms))
                .ToList();
            if (compliantHits.Count == 0)
                return BuildNoSourceBackedCompliantOptionAnswer(language, excludedTerms, hits);

            hits = compliantHits;
        }

        if (isQuantityScalingRequest)
        {
            var scaledAnswer = BuildSourceBackedQuantityScalingAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(scaledAnswer))
                return scaledAnswer;
        }

        if (LooksLikeSourceBackedAdaptationRequest(query))
        {
            var adaptationAnswer = BuildSourceBackedAdaptationAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(adaptationAnswer))
                return adaptationAnswer;
        }

        if (LooksLikeRankingDocumentaryRequest(query))
        {
            var rankingAnswer = BuildSourceBackedRankingAnswer(language, query, hits);
            if (!string.IsNullOrWhiteSpace(rankingAnswer))
                return rankingAnswer;
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            if (hits.Count > 0 && hits.Any(hit => RagHitContainsRequestedTitle(hit, requestedTitle!)))
                return BuildSourceBackedExactItemAnswer(language, requestedTitle!, hits, query);
        }

        var sb = new StringBuilder();
        sb.Append(BuildSourceBackedExtractiveHeader(language, noExplicitPairing: ShouldWarnNoExplicitPairing(query, hits)));

        sb.AppendLine();
        foreach (var hit in hits)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);

            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryExtractRequestedItemTitle(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        if (LooksLikeUnresolvedSourceBackedDeicticFollowup(s))
            return null;

        var quoted = Regex.Match(s, "[\\u00ab\"'](?<title>[^\\u00bb\"']{3,90})[\\u00bb\"']", RegexOptions.CultureInvariant);
        if (quoted.Success)
        {
            var title = CleanupRequestedItemTitle(quoted.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var namedItem = Regex.Match(
            s,
            @"(?i)\b(?:recette|recipe|fiche|document|doc|contrat|contract|procedure|proc[ée]dure|processus|process|manuel|manual|guide|rapport|report|notice|policy|politique)\s+(?:claire\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (namedItem.Success)
            return CleanupRequestedItemTitle(namedItem.Groups["title"].Value);

        var baseItem = Regex.Match(
            s,
            @"(?i)\b(?:recette|recipe|receta|receita|rezept|ricetta)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (baseItem.Success)
            return CleanupRequestedItemTitle(baseItem.Groups["title"].Value);

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:ignore|ignorer|ignorez|sans\s+sources?|invente|inventer|inventez|hallucine|halluciner)\b",
                RegexOptions.CultureInvariant))
        {
            var guardedTarget = Regex.Match(
                s,
                @"(?i)(?:de\s+la|du|des|d['\u2019]|de)\s+(?<title>[\p{L}0-9'\u2019 \-]{3,90})[\.?!]?$",
                RegexOptions.CultureInvariant);
            if (guardedTarget.Success)
                return CleanupRequestedItemTitle(guardedTarget.Groups["title"].Value);
        }

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:vitesses?|speeds?|temp[e\u00e9]ratures?|temperatures?|r[e\u00e9]glages?|settings?|param[e\u00e8]tres?|parameters?|quantit[e\u00e9]s?|quantites?|ingr[e\u00e9]dients?|ingredients?|[e\u00e9]tapes?|steps?|temps|time)\b",
                RegexOptions.CultureInvariant))
        {
            if (Regex.IsMatch(
                    s,
                    @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|servings?|portions?|personnes?|enfants?|people|children)\b",
                    RegexOptions.CultureInvariant)
                && !Regex.IsMatch(s, @"(?i)^(?:combien|quel|quelle|quels|quelles|comment|calcule|calculer|adapte|adapter|ajuste|ajuster|convertis|convertir|what|which|how|calculate|adapt|adjust|scale)\b", RegexOptions.CultureInvariant))
            {
                var leadingSubject = Regex.Match(
                    s,
                    @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{3,90}?)(?:\s+(?:pour|for)\s+[^:?.!,;]{2,90}|:)",
                    RegexOptions.CultureInvariant);
                if (leadingSubject.Success)
                {
                    var title = CleanupRequestedItemTitle(leadingSubject.Groups["title"].Value);
                    if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                        return title;
                }
            }

            var parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for)\s+\d+\s+(?:[\p{L}'\u2019-]+\s+){0,3}(?:de|d['\u2019]|of)\s+(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
                return CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);

            parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for|sur|about|on)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
                return CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);

            parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for|de|du|de\s+la|des|d['\u2019]|sur|about|on)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
                return CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);
        }

        var directItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:donne(?:s)?(?:[-\s]+moi)?|montre(?:[-\s]+moi)?|affiche(?:[-\s]+moi)?|cherche|recherche|trouve|adapte|adapter|ajuste|ajuster|convertis|convertir|calcule|calculer|find|search|give(?:\s+me)?|show(?:\s+me)?|adapt|adjust|scale|resize)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match directItemRequest in directItemRequests)
        {
            var title = CleanupRequestedItemTitle(directItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var directSearchRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+cherche|je\s+recherche|i\s+(?:am\s+)?(?:looking\s+for|searching\s+for))\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match directSearchRequest in directSearchRequests)
        {
            var title = CleanupRequestedItemTitle(directSearchRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var wantedExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+(?:veux|voudrais|souhaite)|j['\u2019]aimerais|i\s+(?:want|would\s+like|need)|quiero|quisiera|quero|gostaria|ich\s+(?:will|moechte|möchte|brauche)|vorrei)\s+(?:trouver\s+|retrouver\s+|avoir\s+|voir\s+|find\s+|get\s+|see\s+|ver\s+|encontrar\s+)?(?:le|la|les|l['\u2019]|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match wantedExactItemRequest in wantedExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(wantedExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var needExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:il\s+me\s+faut|j['\u2019]ai\s+besoin\s+de|i\s+need|necesito|preciso\s+de|ich\s+brauche|ho\s+bisogno\s+di)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match needExactItemRequest in needExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(needExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var doingExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+(?:fais|prepare|prépare|cuisine)|nous\s+(?:faisons|preparons|préparons|cuisinons)|i\s+(?:am\s+)?(?:making|cooking|preparing)|we\s+(?:are\s+)?(?:making|cooking|preparing))\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90}?)(?:\s+(?:avec|pour|for|with)\b|[:?.!,;]|$)",
            RegexOptions.CultureInvariant);
        foreach (Match doingExactItemRequest in doingExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(doingExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var whatIsExactItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:c['\u2019]est\s+quoi|qu['\u2019]est\s+ce\s+que|qu['\u2019]est-ce\s+que|what\s+is|what['\u2019]?s|que\s+es|o\s+que\s+e|was\s+ist|cos['\u2019]e|cosa\s+e)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some|el|los|las|o|a|os|as|der|die|das|il|lo|gli)?\s*(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match whatIsExactItemRequest in whatIsExactItemRequests)
        {
            var title = CleanupRequestedItemTitle(whatIsExactItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var quantitySubject = Regex.Match(
            s,
            @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|ingredients?|ingr[e\u00e9]dients?)\s+(?:pour|for)\s+\d+\s+(?:[\p{L}'\u2019-]+\s+){0,3}(?:de|d['\u2019]|of)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (quantitySubject.Success)
        {
            var title = CleanupRequestedItemTitle(quantitySubject.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|servings?|portions?|personnes?|enfants?|people|children)\b",
                RegexOptions.CultureInvariant))
        {
            var leadingSubject = Regex.Match(
                s,
                @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{3,90}?)(?:\s+(?:pour|for)\s+[^:?.!,;]{2,90}|:)",
                RegexOptions.CultureInvariant);
            if (leadingSubject.Success)
            {
                var title = CleanupRequestedItemTitle(leadingSubject.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }
        }

        return null;
    }

    private static string? CleanupRequestedItemTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';');
        if (title.Length == 0)
            return null;

        title = Regex.Replace(
            title,
            @"(?i)^(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:en\s+mode|mode|version|variante|pour\s+(?:\d+|un|une|des|le|la|les|l['\u2019]|the|a|an|some)\b|dans\s+(?:le|la|les|l['\u2019]|un|une|des|the|a|an)\b|du\s+(?:guide|pdf|document|manuel|livre|book|manual|file|document|corpus|dossier)\b|de\s+la\s+(?:base|page|fiche|notice|section)\b|des\s+(?:sources|documents|docs|fichiers|files)\b).*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:recette|recipe|receta|receita|rezept|ricetta)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^base\s+(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:ingredients?|ingr[ée]dients?|etapes?|[ée]tapes?|temps|source|sources|portions?|materiel|mat[ée]riel|reglages?|r[ée]glages?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        return title.Length >= 3 ? title : null;
    }

    private static bool LooksLikeDirectRequestedItemTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (normalized.Length < 3 || normalized.Length > 90)
            return false;

        if (Regex.IsMatch(
                normalized,
                @"^(?:que|qu|tu|vous|me|moi|mets?|mettre|put|documents?|sources?|fichiers?|files?|what|which|how|comment)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:ca|cela|ceci|this|that|it|eso|esto|isso|isto|das|questo|quello)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:documents?|sources?|fichiers?|files?)\s+(?:qui|that)\s+(?:parle|parlent|mentionne|mentionnent|traite|traitent|talk|mentions?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return ExtractQuerySignalTerms(normalized).Any();
    }

    private static bool RagHitsContainRequestedTitle(IReadOnlyList<RagHitSummary> hits, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var hit in hits)
        {
            if (RagHitContainsRequestedTitle(hit, normalizedTitle, titleTerms))
                return true;
        }

        return false;
    }

    private static bool RagHitContainsRequestedTitle(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return RagHitContainsRequestedTitle(hit, normalizedTitle, titleTerms);
    }

    private static bool RagHitContainsRequestedTitle(RagHitSummary hit, string normalizedTitle, IReadOnlyList<string> titleTerms)
    {
        if (LooksLikeNavigationOnlyHit(hit))
            return false;

        var haystack = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {hit.ContextualSnippet}");
        if (haystack.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        return titleTerms.Count > 0 && titleTerms.All(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static bool LooksLikeNavigationOnlyHit(RagHitSummary hit)
    {
        var text = hit.Excerpt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLexicalLookup(text);
        if (Regex.IsMatch(
                normalized,
                @"\b(?:sommaire|table des matieres|contents|index|inhaltsverzeichnis|indice|reperes de contenu|reperes contenus?|repere de contenu|sections principales|premiers extraits|table of contents|content overview)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var leaderCount = Regex.Matches(text, @"\.{3,}\s*\d{1,4}\b", RegexOptions.CultureInvariant).Count;
        if (leaderCount >= 3)
            return true;

        var compactListCount = Regex.Matches(text, @"[\p{L}\)]\d{1,3}(?:[•\u2022]|\s*[A-Z\u00c0-\u017f])", RegexOptions.CultureInvariant).Count;
        var hasEvidenceCue = Regex.IsMatch(
            normalized,
            @"\b(?:ingredients?|ingredient|preparation|procedure|procedures?|etape|etapes|step|steps|pour\s+\d+\s+(?:personnes?|portions?)|\d+\s*(?:g|kg|mg|ml|cl|l|min|h|c|celsius))\b",
            RegexOptions.CultureInvariant);
        return compactListCount >= 6 && !hasEvidenceCue;
    }

    private static string BuildMissingExactItemAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);

        var header = (closeLeads.Count > 0, language) switch
        {
            (true, "en") => $"I did not find the exact requested item \"{requestedTitle}\" in the available excerpts. I will not invent missing facts, quantities, steps, or details. Closest source-backed leads:",
            (true, "es") => $"No he encontrado el elemento exacto solicitado \"{requestedTitle}\" en los extractos disponibles. No voy a inventar hechos, cantidades, pasos ni detalles. Pistas cercanas con fuente:",
            (true, "pt") => $"Nao encontrei o item exato solicitado \"{requestedTitle}\" nos excertos disponiveis. Nao vou inventar factos, quantidades, passos nem detalhes. Pistas proximas com fonte:",
            (true, "de") => $"Ich habe den exakt angefragten Eintrag \"{requestedTitle}\" in den verfuegbaren Auszuegen nicht gefunden. Ich erfinde keine Fakten, Mengen, Schritte oder Details. Naheliegende belegte Hinweise:",
            (true, "it") => $"Non ho trovato l'elemento esatto richiesto \"{requestedTitle}\" negli estratti disponibili. Non invento fatti, quantita, passaggi o dettagli. Indicazioni vicine con fonte:",
            (true, _) => $"Je n'ai pas trouve l'element exact demande \"{requestedTitle}\" dans les extraits disponibles. Je n'invente donc pas les faits, quantites, etapes ou details manquants. Pistes proches sourcees :",
            (false, "en") => $"I did not find the exact requested item \"{requestedTitle}\" in the available excerpts. I will not invent missing facts, quantities, steps, or details.",
            (false, "es") => $"No he encontrado el elemento exacto solicitado \"{requestedTitle}\" en los extractos disponibles. No voy a inventar hechos, cantidades, pasos ni detalles.",
            (false, "pt") => $"Nao encontrei o item exato solicitado \"{requestedTitle}\" nos excertos disponiveis. Nao vou inventar factos, quantidades, passos nem detalhes.",
            (false, "de") => $"Ich habe den exakt angefragten Eintrag \"{requestedTitle}\" in den verfuegbaren Auszuegen nicht gefunden. Ich erfinde keine Fakten, Mengen, Schritte oder Details.",
            (false, "it") => $"Non ho trovato l'elemento esatto richiesto \"{requestedTitle}\" negli estratti disponibili. Non invento fatti, quantita, passaggi o dettagli.",
            _ => $"Je n'ai pas trouve l'element exact demande \"{requestedTitle}\" dans les extraits disponibles. Je n'invente donc pas les faits, quantites, etapes ou details manquants."
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in closeLeads)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = CollapseWhitespace(hit.Excerpt);
            if (excerpt.Length > 240)
                excerpt = excerpt[..240].TrimEnd() + "...";

            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeSourceBackedPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var asksForPlan = Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|jours|repas|batch cooking|week|weekly|meal plan|meal prep|plan de repas|menu de la semaine|menus de la semaine|menu hebdo|menu hebdomadaire)\b",
            RegexOptions.CultureInvariant);

        return asksForPlan && LooksLikeSourceBackedActionRequest(query);
    }

    private static bool LooksLikeDocumentaryPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|jours|journee|diner|dejeuner|repas|menu|menus|batch cooking|meal prep|meal plan|week|weekly|lunch|dinner|meal|meals|plan de repas|menu de la semaine|menus de la semaine|menu hebdo|menu hebdomadaire|comida|cena|almuerzo|menu semanal|refeicao|refeicoes|jantar|almoco|wochenplan|essen|mittagessen|abendessen|pasti|pranzo|cena|menu settimanale)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeUnresolvedSourceBackedDeicticFollowup(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasDeicticReference = Regex.IsMatch(
            normalized,
            @"\b(?:ca|cela|ceci|this|that|it|eso|esto|isso|isto|das|questo|quello)\b",
            RegexOptions.CultureInvariant);
        if (!hasDeicticReference)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:apres|après|precedent|pr[eé]c[eé]dent|recette|document|source|mets|mettre|adapte|adapter|pour|personnes?|portions?|servings?|people|children|enfants?|after|previous|recipe|put|scale|adjust|adapt)\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildUnresolvedSourceBackedDeicticFollowupAnswer(string language, string query)
    {
        language = NormalizeLanguageCode(language);
        var target = TryExtractTargetServingCount(query, out var servings)
            ? language switch
            {
                "en" => $" for {servings} people",
                "es" => $" para {servings} personas",
                "pt" => $" para {servings} pessoas",
                "de" => $" fuer {servings} Personen",
                "it" => $" per {servings} persone",
                _ => $" pour {servings} personnes"
            }
            : string.Empty;
        return SourceBackedLabel(
            language,
            $"Je n'ai pas d'element precedent exploitable dans cette conversation. Donne-moi la recette, le document ou la source concernee, puis je pourrai l'adapter ou la citer{target} sans inventer.",
            $"I do not have an exploitable previous item in this conversation. Give me the recipe, document, or source involved, then I can adapt or cite it{target} without inventing details.",
            $"No tengo un elemento anterior explotable en esta conversacion. Dame la receta, el documento o la fuente correspondiente y podre adaptarlo o citarlo{target} sin inventar detalles.",
            $"Nao tenho um elemento anterior utilizavel nesta conversa. Da-me a receita, o documento ou a fonte em causa e poderei adapta-lo ou cita-lo{target} sem inventar detalhes.",
            $"Ich habe in dieser Unterhaltung kein nutzbares vorheriges Element. Gib mir das Rezept, das Dokument oder die betroffene Quelle, dann kann ich es{target} anpassen oder zitieren, ohne Details zu erfinden.",
            $"Non ho un elemento precedente utilizzabile in questa conversazione. Dammi la ricetta, il documento o la fonte interessata e potro adattarlo o citarlo{target} senza inventare dettagli.");
    }

    private static bool LooksLikeDocumentaryContentRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
            return true;

        var mentionsDocumentarySource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|extrait|extraits|page|pages|base|connaissance|knowledge|corpus|file|files|archivo|archivos|documento|documentos|fonte|fontes|quelle|quellen|dokument|dokumente|documento|documenti|fonte|fonti)\b",
            RegexOptions.CultureInvariant);
        var mentionsContentObject = Regex.IsMatch(
            normalized,
            @"\b(?:recette|recettes|recipe|recipes|receta|recetas|receita|receitas|rezept|rezepte|ricetta|ricette|ingredient|ingredients|ingrediente|ingredientes|ingr[eé]dient|ingr[eé]dients|etape|etapes|[eé]tape|[eé]tapes|step|steps|preparation|pr[eé]paration|procedure|proc[eé]dure|procedimento|procedura|fiche|card|checklist|liste|list|menu|menus|repas|plat|plats|dessert|desserts|sauce|sauces|quantite|quantites|quantit[eé]|quantit[eé]s|portion|portions|temps|time|cuisson|cooking|cook|cuisiner|cuisine|kochen|cocinar|cozinhar|cucinare)\b",
            RegexOptions.CultureInvariant);
        var hasRequestOperator = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|quoi|qu est|qu est ce|peux|pourrais|donne|donner|trouve|trouver|cherche|chercher|fais|faire|faut|besoin|veux|voudrais|souhaite|aimerais|compare|comparer|liste|lister|resume|resumer|reponds|repondre|r[eé]ponds|r[eé]pondre|adapte|adapter|transforme|transformer|traduis|traduire|rends|rendre|existe|existent|which|what|can|could|give|find|search|make|need|want|compare|list|summarize|respond|answer|adapt|adjust|transform|translate|render|exists|exist|cual|cu[aá]l|que|puedes|podrias|dame|busca|encuentra|necesito|responde|traduce|existe|quero|pode|podes|procura|encontra|preciso|responde|traduz|existe|welche|was|kannst|suche|finde|brauche|antworte|uebersetze|ubersetze|existiert|quale|cosa|puoi|cerca|trova|bisogno|rispondi|traduci|esiste)\b",
            RegexOptions.CultureInvariant);
        var hasDirectiveNounPhrase = Regex.IsMatch(
            normalized,
            @"^(?:(?:un|une|des|du|de\s+la|the|a|an|some)\s+)?(?:menu|menus|recette|recettes|recipe|recipes|liste|list|checklist|fiche|card|plan|planning)\b",
            RegexOptions.CultureInvariant);

        if (mentionsDocumentarySource && (mentionsContentObject || ExtractQuerySignalTerms(normalized).Any()))
            return true;

        return mentionsContentObject && (hasRequestOperator || hasDirectiveNounPhrase);
    }

    private static string[] BuildPlanningRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = query;

        var queries = new List<string> { normalized };

        var normalizedLookup = NormalizeLooseLookup(normalized);
        var signalTerms = ExtractQuerySignalTerms(normalizedLookup)
            .Where(static term => term.Length >= 4)
            .Take(5)
            .ToArray();
        if (signalTerms.Length > 0)
            queries.Add(string.Join(' ', signalTerms) + " sources documents");

        if (LooksLikeFoodPlanningRequest(normalizedLookup))
        {
            if (normalizedLookup.Contains("batch cooking", StringComparison.Ordinal)
                || normalizedLookup.Contains("meal prep", StringComparison.Ordinal))
            {
                queries.Add("batch cooking repas semaine recettes preparation avance cuisson");
                queries.Add("cuisinez a l avance repas semaine batch cooking");
            }

            queries.AddRange(new[]
            {
                "menu ingredients recette",
                "recette ingredients 4 personnes",
                "recette plat principal ingredients preparation",
                "plat complet poulet riz legumes recette",
                "recettes menu ingredients 30min 4",
                "recettes ingredients preparation pour personnes",
                "recettes faciles rapides repas semaine",
                "plats complets recettes simples",
                "menu semaine recettes sources",
                "batch cooking repas semaine recettes"
            });
        }

        queries.AddRange(new[]
        {
            "planning organisation preparation sources",
            "plan semaine organisation preparation",
            "weekly plan preparation source-backed",
            "planificacion semana organizacion preparacion",
            "planejamento semana organizacao preparacao",
            "wochenplan organisation vorbereitung",
                "piano settimana organizzazione preparazione"
            });

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static bool LooksLikeSourceBackedAdaptationRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var mentionsSource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|source|sources|extrait|extraits|base|connaissance|knowledge)\b",
            RegexOptions.CultureInvariant);
        var mentionsAdaptation = Regex.IsMatch(
            normalized,
            @"\b(?:adaptation|adaptations|adapte|adapter|adaptee|adaptees|adapted|adapt|extrapole|extrapoler|derive|derived)\b",
            RegexOptions.CultureInvariant);

        return mentionsSource && mentionsAdaptation;
    }

    private static bool LooksLikeRankingDocumentaryRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasRankingOperator = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|qual|welche|welcher|welches|quale)\b.{0,120}\b(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b|\b(?:le|la|les|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b",
            RegexOptions.CultureInvariant);
        if (!hasRankingOperator)
            return false;

        return ExtractQuerySignalTerms(normalized).Any()
            || Regex.IsMatch(normalized, @"\b(?:technique|technical|tecnico|tecnica|technisch|complique|complexe|complex|difficile|difficult|schwierig)\b", RegexOptions.CultureInvariant);
    }

    private static string[] BuildSourceBackedActionRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var queries = new List<string>();
        AddDistinctQuery(queries, normalized);

        var normalizedLookup = NormalizeLexicalLookup(normalized);
        var terms = ExtractQuerySignalTerms(normalizedLookup)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (terms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', terms));

        var subjectTerms = terms
            .Where(static term => term.Length >= 5)
            .Where(static term => !Regex.IsMatch(term, @"\b(?:alleger|all[eé]ger|lighten|reduce|reduire|adapter|adaptation)\b", RegexOptions.CultureInvariant))
            .Take(5)
            .ToArray();
        if (subjectTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', subjectTerms));

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static IEnumerable<string> BuildRetrievalTermVariants(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;

        if (normalized.Length > 5 && normalized.EndsWith("es", StringComparison.Ordinal))
            yield return normalized[..^2];
        if (normalized.Length > 4 && normalized.EndsWith("s", StringComparison.Ordinal))
            yield return normalized[..^1];
    }

    private static bool IsSourceBackedActionRetrievalNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "adaptation", "adaptations", "adapte", "adapter", "adaptee", "adaptees", "bien", "cela",
            "cette", "comment", "como", "dans", "document", "documents", "extrait", "extraits", "faire", "how", "peux",
            "pdf", "source", "sources", "vient", "viennent", "what", "which", "source", "sources",
            "document", "documents", "adapt", "adapted", "derived", "please", "from"
        };

        return stopWords.Contains(normalized);
    }

    private static bool LooksLikeComparativeDocumentaryRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasComparativeOperator = Regex.IsMatch(
                normalized,
                @"\b(?:compare|comparer|comparaison|comparatif|comparative|difference|differences|different|differents|differentes|versus|compara|comparar|comparacion|comparacao|vergleiche|vergleichen|vergleich|confronta|confrontare|confronto|paragona|paragonare)\b",
                RegexOptions.CultureInvariant);
        var hasRankingOperator = Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|quels|quelles|which|what|cual|cuál|qual|welche|welcher|welches|quale)\b.{0,120}\b(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b|\b(?:le|la|les|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b",
                RegexOptions.CultureInvariant);

        if (!hasComparativeOperator && !hasRankingOperator)
            return false;

        return BuildComparativeRetrievalQueries(query ?? string.Empty).Length > 1
            || ExtractQuerySignalTerms(normalized).Any();
    }

    private static string[] BuildComparativeRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var queries = new List<string>();
        var focus = TryBuildComparativeFocusQuery(normalized);
        AddDistinctQuery(queries, focus);
        AddDistinctQuery(queries, normalized);
        if (!string.IsNullOrWhiteSpace(focus))
        {
            AddDistinctQuery(queries, $"{focus} details");
            AddDistinctQuery(queries, $"{focus} procedure");
        }

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(normalized))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .Take(5)
            .ToArray();
        if (signalTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', signalTerms));

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static string NormalizeComparativeSupplementalRetrievalQuery(string? query, string comparativeQuery)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var focus = TryBuildComparativeFocusQuery(comparativeQuery);
        if (string.IsNullOrWhiteSpace(focus))
            return normalized;

        var normalizedQuery = NormalizeLexicalLookup(normalized);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(focus))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        if (focusTerms.Length == 0 || focusTerms.Any(term => normalizedQuery.Contains(term, StringComparison.Ordinal)))
            return normalized;

        var supplementalTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        if (supplementalTerms.Length == 0)
            return CollapseWhitespace($"{focus} {normalized}");

        return CollapseWhitespace($"{focus} {normalized}");
    }

    private static string? TryBuildComparativeFocusQuery(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var candidate = normalized;
        var afterCompareVerb = Regex.Match(
            candidate,
            @"\b(?:compare|comparer|comparaison|comparatif|comparative|difference|differences|different|differents|differentes|versus|compara|comparar|comparacion|comparacao|vergleiche|vergleichen|vergleich|confronta|confrontare|confronto|paragona|paragonare)\b\s+(?<rest>.+)$",
            RegexOptions.CultureInvariant);
        if (afterCompareVerb.Success)
            candidate = afterCompareVerb.Groups["rest"].Value;

        candidate = Regex.Split(
                candidate,
                @"\b(?:et|and|y|e|und)\s+(?:celle|celui|celles|ceux|celui-ci|celle-ci|celui-la|celle-la|the\s+one|that|quella|quello|quellas|quellos|diese|dieser|dieses)\b",
                RegexOptions.CultureInvariant)
            .FirstOrDefault()
            ?? candidate;

        var terms = Regex.Matches(candidate, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Where(term => term.Length >= 3 || term.Any(char.IsDigit))
            .Where(term => !(term.All(char.IsDigit) && term.Length <= 3))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        if (terms.Length == 0)
            return null;

        return string.Join(' ', terms);
    }

    private static void AddDistinctQuery(List<string> queries, string? query)
    {
        var value = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!queries.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            queries.Add(value);
    }

    private static bool IsComparativeRetrievalNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "celles", "celle", "celui", "ceux", "comment",
            "dans", "des", "document", "documents", "est", "etre", "faire", "fiche", "fichier", "fichiers",
            "la", "le", "les", "livre", "livres", "manuel", "moins", "page", "pages", "peux", "plus",
            "pour", "quel", "quelle", "quelles", "quels", "quoi", "source", "sources", "sur", "un", "une",
            "the", "that", "this", "those", "these", "book", "books", "document", "documents", "file",
            "files", "manual", "page", "pages", "source", "sources", "about", "with", "from", "is",
            "are", "which", "what", "most", "least", "best", "worst",
            "comparaison", "comparatif", "comparative", "compare", "comparer", "difference", "differences",
            "different", "differents", "differentes", "versus", "entre", "against", "between", "francais",
            "francaise", "francaises", "french", "international", "internationale", "top", "best",
            "meilleur", "meilleure", "meilleurs", "meilleures", "compara", "comparar", "comparacion",
            "comparacao", "libro", "libros", "documento", "documentos", "fonte", "fontes", "quelle",
            "quello", "quella", "confronta", "confrontare", "confronto", "paragona", "paragonare",
            "vergleiche", "vergleichen", "vergleich", "buch", "buecher", "dokument", "dokumente",
            "quelle", "quellen", "seite", "seiten"
        };

        return stopWords.Contains(normalized);
    }

    private static bool LooksLikeFoodPlanningRequest(string normalizedQuery)
    {
        return Regex.IsMatch(
            normalizedQuery ?? string.Empty,
            @"\b(?:repas|menu|menus|recette|recettes|ingredient|ingredients|cuisine|cuisiner|plat|plats|sauce|sauces|dessert|entree|entrees|viande|poisson|poulet|batch cooking|meal|meals|recipe|recipes|dinner|food|cook|cooking|cocina|receta|recetas|comida|salsa|carne|cozinha|receita|receitas|refeicao|molho|kuche|kueche|rezept|rezepte|essen|fleisch|cucina|ricetta|ricette|pasto)\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildSourceBackedPlanningAnswer(ToolResults toolResults, string language, int minItems = 1, string? query = null)
    {
        language = NormalizeLanguageCode(language);
        var sourceHits = EnumerateRagHitSummaries(toolResults).ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        var planItems = sourceHits
            .Select(hit => new
            {
                Hit = hit,
                Title = ExtractPlanItemTitleV2(GetPlanExtractionText(hit))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .GroupBy(x => $"{x.Hit.DocPath}|{x.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(7)
            .ToList();

        if (planItems.Count == 0 || planItems.Count < minItems)
            return string.Empty;

        var header = language switch
        {
            "en" => "Here is a source-backed plan from the available documents. I only list items found in the excerpts:",
            "es" => "Aqui tienes una propuesta basada en los documentos disponibles. Solo incluyo elementos encontrados en los extractos:",
            "pt" => "Aqui esta uma proposta baseada nos documentos disponiveis. Incluo apenas itens encontrados nos excertos:",
            "de" => "Hier ist ein quellenbasierter Plan aus den verfuegbaren Dokumenten. Ich nenne nur Elemente aus den Auszuegen:",
            "it" => "Ecco una proposta basata sui documenti disponibili. Includo solo elementi trovati negli estratti:",
            _ => "Voici une proposition appuyee sur les documents disponibles. Je liste uniquement des elements retrouves dans les extraits :"
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        if (planItems.Count < 7)
        {
            var coverageNote = language switch
            {
                "en" => "I found fewer than seven reliable source-backed items, so I do not fill the missing days by guessing.",
                "es" => "He encontrado menos de siete elementos fiables con fuente, asi que no completo los dias que faltan con suposiciones.",
                "pt" => "Encontrei menos de sete itens fiaveis com fonte, por isso nao preencho os dias em falta por suposicao.",
                "de" => "Ich habe weniger als sieben verlaessliche quellenbasierte Eintraege gefunden und fuelle fehlende Tage daher nicht geraten auf.",
                "it" => "Ho trovato meno di sette elementi affidabili con fonte, quindi non completo i giorni mancanti per supposizione.",
                _ => "J'ai trouve moins de sept elements fiables et sources, donc je ne complete pas les jours manquants en inventant."
            };
            sb.AppendLine(coverageNote);
        }
        for (var i = 0; i < planItems.Count; i++)
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

            var hit = planItems[i].Hit;
            sb.Append("- ");
            sb.Append(day);
            sb.Append(" : ");
            sb.Append(planItems[i].Title);
            sb.Append(" (");
            sb.Append(string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.AppendLine(")");
        }

        var note = language switch
        {
            "en" => "Note: adapt quantities, timing, and constraints from the source pages before acting.",
            "es" => "Nota: adapta cantidades, tiempos y restricciones a partir de las paginas fuente antes de actuar.",
            "pt" => "Nota: adapta quantidades, tempos e restricoes a partir das paginas fonte antes de agir.",
            "de" => "Hinweis: Mengen, Zeiten und Einschraenkungen vor der Umsetzung anhand der Quellseiten anpassen.",
            "it" => "Nota: adatta quantita, tempi e vincoli dalle pagine fonte prima di agire.",
            _ => "Note : adapte les quantites, delais et contraintes a partir des pages source avant d'agir."
        };
        sb.AppendLine(note);

        return sb.ToString().TrimEnd();
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);
        if (closeLeads.Count == 0)
            closeLeads = hits
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .OrderByDescending(hit => ComputeRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(3)
                .ToList();

        return closeLeads
            .Select(hit =>
            {
                var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                return new ToolMemory.SourceRef
                {
                    DocPath = hit.DocPath.Replace('\\', '/'),
                    PageStart = hit.PageStart,
                    PageEnd = hit.PageEnd,
                    Label = $"{label} (p.{hit.PageStart}{(hit.PageEnd != hit.PageStart ? $"-{hit.PageEnd}" : "")})"
                };
            })
            .ToList();
    }

    private static List<RagHitSummary> SelectMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        return hits
            .Select(hit => new
            {
                Hit = hit,
                Score = ComputeRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit))
            })
            .Where(item => !LooksLikeNavigationOnlyHit(item.Hit))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Hit)
            .Take(2)
            .ToList();
    }

    private static IReadOnlyList<RagHitSummary> SelectSourceBackedExtractiveHits(ToolResults toolResults, string query, int maxHits)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query))
            .Select(hit => new
            {
                Hit = hit,
                PrimaryRelevance = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(hit)),
                Relevance = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit))
            })
            .OrderByDescending(item => item.PrimaryRelevance)
            .ThenByDescending(item => item.Relevance)
            .ThenByDescending(item => item.Hit.Score)
            .ToList();
        if (allHits.Count == 0)
            return Array.Empty<RagHitSummary>();

        if (LooksLikeRankingDocumentaryRequest(query))
        {
            var rankedHits = RankSourceBackedRankingHits(allHits.Select(item => item.Hit), query)
                .Take(maxHits)
                .ToList();
            if (rankedHits.Count > 0)
                return rankedHits;
        }

        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            var comparativeHits = SelectComparativeDocumentaryHits(allHits.Select(item => item.Hit), query, maxHits);
            if (comparativeHits.Count > 0)
                return comparativeHits;
        }

        var requestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var exactTitleHits = allHits
                .Select(item => item.Hit)
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!))
                .ToList();
            if (exactTitleHits.Count > 0)
            {
                if (LooksLikeStructuredItemCardRequest(query))
                {
                    exactTitleHits = AddComplementaryStructuredHitsForExactItem(
                            exactTitleHits,
                            allHits.Select(static item => item.Hit))
                        .OrderByDescending(ComputeExactItemCardCompletenessCueScore)
                        .ThenByDescending(hit => ComputeRagHitLexicalRelevance(requestedTitle!, GetRagHitLookupText(hit)))
                        .ThenByDescending(hit => hit.Score)
                        .Take(maxHits)
                        .ToList();
                }
                else
                {
                    exactTitleHits = exactTitleHits
                        .Take(maxHits)
                        .ToList();
                }

                return exactTitleHits;
            }
        }
        else
        {
            var primaryRelevantHits = allHits
                .Where(item => item.PrimaryRelevance > 0)
                .Select(item => item.Hit)
                .ToList();
            if (primaryRelevantHits.Count > 0)
                return FilterHitsToDominantTopLevel(primaryRelevantHits, query).Take(maxHits).ToList();

            var relevantHits = allHits
                .Where(item => item.Relevance > 0)
                .Select(item => item.Hit)
                .ToList();
            if (relevantHits.Count > 0)
                return FilterHitsToDominantTopLevel(relevantHits, query).Take(maxHits).ToList();

            var fallbackHits = allHits.Select(item => item.Hit).ToList();
            return FilterHitsToDominantTopLevel(fallbackHits, query).Take(maxHits).ToList();
        }

        return allHits.Select(item => item.Hit).Take(maxHits).ToList();
    }

    private static bool LooksLikeLowSignalAppFeatureHit(RagHitSummary hit, string query)
    {
        var queryLookup = NormalizeLexicalLookup(query);
        if (Regex.IsMatch(queryLookup, @"\b(?:liste\s+de\s+courses|courses|shopping\s+list|epicerie|grocery)\b", RegexOptions.CultureInvariant))
            return false;

        var text = NormalizeLexicalLookup(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"\b(?:liste\s+de\s+courses\s+a\s+partir|shopping\s+list\s+from|application|appli|app)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(text, @"\b(?:recettes?\s+adaptees?|ingredients?\s+disponibles?|guides?\s+de\s+cuisson|univers)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(text, @"\b(?:preparation|etapes?|pour\s+\d+\s+personnes?|temps\s+total|\d+\s*(?:g|kg|ml|cl|l|min|h))\b", RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<RagHitSummary> AddComplementaryStructuredHitsForExactItem(
        IReadOnlyList<RagHitSummary> exactHits,
        IEnumerable<RagHitSummary> allHits)
    {
        if (exactHits.Count == 0)
            return exactHits;

        var merged = exactHits.ToList();
        foreach (var candidate in allHits)
        {
            if (merged.Any(hit => SameRagHitRange(hit, candidate)))
                continue;

            var isAdjacentOrOverlapping = exactHits.Any(anchor =>
                string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase)
                && candidate.PageStart <= anchor.PageEnd + 2
                && candidate.PageEnd >= Math.Max(1, anchor.PageStart - 2));
            if (!isAdjacentOrOverlapping)
                continue;

            if (ComputeExactItemCardCompletenessCueScore(candidate) < 8)
                continue;

            merged.Add(candidate);
        }

        return merged;
    }

    private static bool SameRagHitRange(RagHitSummary left, RagHitSummary right)
        => string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase)
            && left.PageStart == right.PageStart
            && left.PageEnd == right.PageEnd;

    private static int ComputeExactItemCardCompletenessCueScore(RagHitSummary hit)
    {
        var score = ComputeProcedureCompletenessCueScore(hit)
            + ComputeStructuredProcedureEvidenceCueScore(hit)
            + ComputeStructuredProcedureVisibleEvidenceCueScore(hit);
        if (Regex.IsMatch(NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit)), @"\b(?:ingredient|ingredients)\b", RegexOptions.CultureInvariant))
            score += 4;
        if (LooksLikeMidProcedureFragment(hit))
            score -= 8;
        return score;
    }

    private static IReadOnlyList<RagHitSummary> SelectComparativeDocumentaryHits(IEnumerable<RagHitSummary> hits, string query, int maxHits)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();

        var scored = hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Score = ComputeComparativeDocumentaryEvidenceScore(hit, evidenceQuery, focusTerms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToList();
        if (scored.Count == 0)
            return Array.Empty<RagHitSummary>();

        var bestByDocument = scored
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Hit.DocPath) ? item.Hit.DocName : item.Hit.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToList();

        if (bestByDocument.Count >= 2)
            return bestByDocument.Take(maxHits).Select(item => item.Hit).ToList();

        return scored.Take(maxHits).Select(item => item.Hit).ToList();
    }

    private static double ComputeComparativeDocumentaryEvidenceScore(RagHitSummary hit, string evidenceQuery, IReadOnlyList<string> focusTerms)
    {
        var primaryText = GetRagHitPrimaryEvidenceText(hit);
        var lookupText = GetRagHitLookupText(hit);
        var normalizedPrimary = NormalizeLexicalLookup(primaryText);
        var normalizedLookup = NormalizeLexicalLookup(lookupText);
        if (string.IsNullOrWhiteSpace(normalizedPrimary) && string.IsNullOrWhiteSpace(normalizedLookup))
            return 0;

        if (focusTerms.Count > 0
            && !focusTerms.Any(term =>
                normalizedPrimary.Contains(term, StringComparison.Ordinal)
                || normalizedLookup.Contains(term, StringComparison.Ordinal)))
        {
            return 0;
        }

        var score = ComputeRagHitLexicalRelevance(evidenceQuery, primaryText) * 8
            + ComputeRagHitLexicalRelevance(evidenceQuery, lookupText) * 2
            + Math.Min(2.0, Math.Max(0.0, hit.Score));

        var structuredProcedureScore = ComputeStructuredProcedureEvidenceCueScore(hit);
        score += structuredProcedureScore * 3.0;
        if (structuredProcedureScore >= 6
            && focusTerms.Count > 0
            && focusTerms.Any(term => normalizedLookup.Contains(term, StringComparison.Ordinal)))
        {
            score += 6;
        }

        if (Regex.IsMatch(normalizedPrimary, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|min|h)\b", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(normalizedPrimary, @"(?:^|\s)[1-9][\.)]\s+", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(normalizedPrimary, @"\b(?:ingredient|ingredients|preparation|preparacion|preparacao|zubereitung|preparazione|etape|etapes|steps?|pasos|passos|method|methode)\b", RegexOptions.CultureInvariant))
            score += 2;

        if (Regex.IsMatch(normalizedPrimary, @"\b(?:introduction|bienvenue|sommaire|contents|index|overview|presentation|nous avons reuni|nous sommes prets)\b", RegexOptions.CultureInvariant)
            && structuredProcedureScore < 4)
        {
            score -= 16;
        }

        if (string.Equals(hit.EmbeddingBasis, "document_profile_v1", StringComparison.OrdinalIgnoreCase))
            score -= 5;

        return score;
    }

    private sealed record QuantityScaleLine(string Original, string Scaled);

    private sealed record QuantityScalingCandidate(RagHitSummary Hit, int SourceServings, IReadOnlyList<QuantityScaleLine> Lines, double Score);

    private static IReadOnlyList<string> ExtractSourceBackedExcludedTerms(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:sans|without|sin|sem|ohne|senza)\s+(?<term>[\p{L}\p{N}'\u2019 \-]{2,40})",
                     RegexOptions.CultureInvariant))
        {
            var term = Regex.Replace(
                    match.Groups["term"].Value,
                    @"(?i)\b(?:et|ou|and|or|avec|with|dans|from|pour|for|sur|de|du|des)\b.*$",
                    string.Empty,
                    RegexOptions.CultureInvariant)
                .Trim(' ', '.', ',', ':', ';', '?', '!', '"', '\'');

            var normalized = NormalizeLexicalLookup(term);
            if (normalized.Length >= 2)
                terms.Add(normalized);
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:en\s+evitant|en\s+évitant|evitant|évitant|eviter|éviter|avoid(?:ing)?|except|sauf|salvo|sem|ohne|senza)\s+(?<terms>[^.?!;]{2,140})",
                     RegexOptions.CultureInvariant))
        {
            foreach (var rawPart in Regex.Split(match.Groups["terms"].Value, @"\s*(?:,|/|\bet\b|\bou\b|\band\b|\bor\b|\by\b|\bo\b|\be\b|\boder\b|\bund\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var part = Regex.Replace(
                        rawPart,
                        @"(?i)\b(?:dans|from|pour|for|sur|avec|with|menu|recette|recettes|recipe|recipes)\b.*$",
                        string.Empty,
                        RegexOptions.CultureInvariant)
                    .Trim(' ', '.', ',', ':', ';', '?', '!', '"', '\'');
                var normalized = NormalizeLexicalLookup(part);
                if (normalized.Length >= 2)
                    terms.Add(normalized);
            }
        }

        return terms.Distinct(StringComparer.Ordinal).Take(5).ToArray();
    }

    private static bool RagHitContainsAnyExcludedTerm(RagHitSummary hit, IReadOnlyList<string> excludedTerms)
    {
        if (excludedTerms.Count == 0)
            return false;

        var haystack = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        return excludedTerms.Any(term => Regex.IsMatch(
            haystack,
            $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}([^\p{{L}}\p{{N}}]|$)",
            RegexOptions.CultureInvariant));
    }

    private static string BuildNoSourceBackedCompliantOptionAnswer(string language, IReadOnlyList<string> excludedTerms, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var excluded = string.Join(", ", excludedTerms);
        var labels = language switch
        {
            "en" => (
                Header: $"I did not find a source-backed option that respects the exclusion: {excluded}.",
                Detail: "The retrieved source excerpts still contain the excluded element, so I will not invent a compliant variant.",
                Nearby: "Retrieved conflicting sources:"
            ),
            "es" => (
                Header: $"No he encontrado una opcion con fuente que respete la exclusion: {excluded}.",
                Detail: "Los extractos recuperados siguen conteniendo el elemento excluido, asi que no invento una variante conforme.",
                Nearby: "Fuentes recuperadas en conflicto:"
            ),
            "pt" => (
                Header: $"Nao encontrei uma opcao com fonte que respeite a exclusao: {excluded}.",
                Detail: "Os excertos recuperados ainda contem o elemento excluido, por isso nao invento uma variante conforme.",
                Nearby: "Fontes recuperadas em conflito:"
            ),
            "de" => (
                Header: $"Ich habe keine quellenbasierte Option gefunden, die den Ausschluss erfuellt: {excluded}.",
                Detail: "Die gefundenen Auszuege enthalten das ausgeschlossene Element weiterhin, daher erfinde ich keine passende Variante.",
                Nearby: "Gefundene widersprechende Quellen:"
            ),
            "it" => (
                Header: $"Non ho trovato un'opzione supportata dalle fonti che rispetti l'esclusione: {excluded}.",
                Detail: "Gli estratti recuperati contengono ancora l'elemento escluso, quindi non invento una variante conforme.",
                Nearby: "Fonti recuperate in conflitto:"
            ),
            _ => (
                Header: $"Je n'ai pas trouve d'option sourcee qui respecte l'exclusion : {excluded}.",
                Detail: "Les extraits retrouves contiennent encore l'element exclu, donc je n'invente pas une variante conforme.",
                Nearby: "Sources retrouvees en conflit :"
            )
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.Detail);
        sb.AppendLine(labels.Nearby);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 240);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeSourceBackedQuantityScalingRequest(string? query)
    {
        var normalized = NormalizeLooseLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!TryExtractTargetServingCount(query, out _))
            return false;

        var asksExplicitScaling = Regex.IsMatch(
            normalized,
            @"\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|multiplie|multiplier|quantites?|quantit[eé]s?|liste\s+de\s+courses?|shopping\s+list|scale|resize|adjust|servings?|portions?)\b",
            RegexOptions.CultureInvariant);
        if (LooksLikeDocumentaryPlanningRequest(query) && !asksExplicitScaling)
            return false;

        return asksExplicitScaling
            || Regex.IsMatch(
                normalized,
                @"\b(?:personnes?|personas?|pessoas?|personen|persone)\b",
                RegexOptions.CultureInvariant);
    }

    private static string BuildSourceBackedQuantityScalingAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        if (!TryExtractTargetServingCount(query, out var targetServings))
            return string.Empty;

        var candidates = SelectQuantityScalingCandidates(hits, query, targetServings);

        var selected = candidates.FirstOrDefault();
        if (selected is null)
            return string.Empty;

        var factor = targetServings / (double)selected.SourceServings;
        var labels = BuildQuantityScalingLabels(language);
        var docLabel = string.IsNullOrWhiteSpace(selected.Hit.DocName) ? selected.Hit.DocPath : selected.Hit.DocName;
        var factorText = FormatScaleNumber(factor);

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.Append(labels.Base);
        sb.Append(' ');
        sb.Append(docLabel);
        sb.Append(" p.");
        sb.Append(selected.Hit.PageStart);
        sb.Append(" - ");
        sb.Append(selected.SourceServings);
        sb.Append(" -> ");
        sb.Append(targetServings);
        sb.Append(" (");
        sb.Append(labels.Factor);
        sb.Append(" x");
        sb.Append(factorText);
        sb.AppendLine(").");

        sb.AppendLine(labels.ShoppingList);
        foreach (var line in selected.Lines.Take(18))
        {
            sb.Append("- ");
            sb.Append(line.Scaled);
            sb.Append(" (");
            sb.Append(labels.Source);
            sb.Append(": ");
            sb.Append(line.Original);
            sb.AppendLine(")");
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static List<QuantityScalingCandidate> SelectQuantityScalingCandidates(IEnumerable<RagHitSummary> hits, string query, int targetServings)
    {
        var subject = TryExtractQuantityScalingSubject(query);
        return hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(hit =>
            {
                var text = GetBestRagEvidenceText(hit);
                var sourceServings = TryExtractSourceServingCount(text, out var servings) ? servings : 0;
                var lines = sourceServings > 0
                    ? ExtractQuantityScaleLines(text, targetServings / (double)sourceServings)
                    : new List<QuantityScaleLine>();
                var score = ComputeRagHitLexicalRelevance(string.IsNullOrWhiteSpace(subject) ? query : subject!, GetRagHitLookupText(hit))
                    + (sourceServings > 0 ? 8 : 0)
                    + Math.Min(lines.Count, 8);
                return new QuantityScalingCandidate(hit, sourceServings, lines, score);
            })
            .Where(item => item.SourceServings > 0 && item.Lines.Count >= 2)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Hit.Score)
            .ToList();
    }

    private static (string Header, string Base, string Factor, string ShoppingList, string Source, string Caution) BuildQuantityScalingLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "Here is the deterministic quantity adaptation from the cited source.",
                "Source base:",
                "factor",
                "Adjusted quantities:",
                "source",
                "Seasoning or quantities absent from the source stay to taste; I do not invent missing steps."),
            "es" => (
                "Aqui tienes la adaptacion determinista de cantidades a partir de la fuente citada.",
                "Base fuente:",
                "factor",
                "Cantidades ajustadas:",
                "fuente",
                "Los condimentos o cantidades ausentes de la fuente quedan al gusto; no invento pasos que faltan."),
            "pt" => (
                "Aqui esta a adaptacao deterministica das quantidades a partir da fonte citada.",
                "Base da fonte:",
                "fator",
                "Quantidades ajustadas:",
                "fonte",
                "Temperos ou quantidades ausentes da fonte ficam a gosto; nao invento passos em falta."),
            "de" => (
                "Hier ist die deterministische Mengenanpassung aus der zitierten Quelle.",
                "Quellbasis:",
                "Faktor",
                "Angepasste Mengen:",
                "Quelle",
                "Wuerzung oder Mengen, die in der Quelle fehlen, bleiben nach Geschmack; fehlende Schritte erfinde ich nicht."),
            "it" => (
                "Ecco l'adattamento deterministico delle quantita dalla fonte citata.",
                "Base fonte:",
                "fattore",
                "Quantita adattate:",
                "fonte",
                "Condimenti o quantita assenti dalla fonte restano a gusto; non invento passaggi mancanti."),
            _ => (
                "Voici l'adaptation deterministe des quantites a partir de la source citee.",
                "Base source :",
                "facteur",
                "Quantites adaptees :",
                "source",
                "Les assaisonnements ou quantites absents de la source restent au gout ; je n'invente pas les etapes manquantes.")
        };
    }

    private static bool TryExtractTargetServingCount(string? query, out int servings)
    {
        servings = 0;
        var normalized = NormalizeLooseLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var patterns = new[]
        {
            @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+(?<n>\d{1,3})\s*(?:personnes?|pers\.?|portions?|servings?|people|children|kids|enfants?|personas?|porciones?|pessoas?|porcoes?|personen|persone|bols?|bowls?|assiettes?|plates?)\b",
            @"\b(?<n>\d{1,3})\s*(?:personnes?|pers\.?|portions?|servings?|people|children|kids|enfants?|personas?|porciones?|pessoas?|porcoes?|personen|persone|bols?|bowls?|assiettes?|plates?)\b"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out var n) && n is > 0 and <= 200)
            {
                servings = n;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSourceServingCount(string? text, out int servings)
    {
        servings = 0;
        var normalized = NormalizeLooseLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var patterns = new[]
        {
            @"\bpour\s+(?<n>\d{1,3})\s*(?:personnes?|pers\.?|portions?)\b",
            @"\b(?:personnes?|pers\.?|portions?|servings?)\.?\s*(?<n>\d{1,3})\s*(?:ingredients?|ingr[eé]dients?)(?=\d|\b)",
            @"\bserves?\s+(?<n>\d{1,3})\b",
            @"\bfor\s+(?<n>\d{1,3})\s*(?:servings?|people|persons?)\b",
            @"\bpara\s+(?<n>\d{1,3})\s*(?:personas?|porciones?)\b",
            @"\bfuer\s+(?<n>\d{1,3})\s*(?:personen|portionen)\b",
            @"\bper\s+(?<n>\d{1,3})\s*(?:persone|porzioni)\b"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out var n) && n is > 0 and <= 200)
            {
                servings = n;
                return true;
            }
        }

        return false;
    }

    private static string? TryExtractQuantityScalingSubject(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        var patterns = new[]
        {
            @"(?i)\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|scale|resize|adjust)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>.+?)\s+(?:pour|for|para|per|fur|fuer|zu|a)\s+\d{1,3}\b",
            @"(?i)\b(?:liste\s+de\s+courses?|shopping\s+list)\s+(?:pour|for)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>.+?)\b"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(s, pattern, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var title = CleanupRequestedItemTitle(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        return TryExtractRequestedItemTitle(query);
    }

    private static List<QuantityScaleLine> ExtractQuantityScaleLines(string text, double factor)
    {
        var normalized = text
            .Replace('\u2022', '|')
            .Replace('\u00b7', '|');
        normalized = Regex.Replace(
            normalized,
            @"(?<!^)(?<![\d,.])(?=\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.|cuill[eè]res?|cuill[eè]re|cups?|tbsp|tsp)\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<!^)(?<![\d,.])(?=\d+\s+(?:gros|grosse|petit|petite|gousse|gousses|bouquet|bouquets|oignon|oignons|oeuf|oeufs|egg|eggs)\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var parts = Regex.Split(normalized, @"[|;\r\n]+", RegexOptions.CultureInvariant)
            .Select(CollapseWhitespace)
            .Where(part => part.Length > 0)
            .ToList();

        var lines = new List<QuantityScaleLine>();
        foreach (var part in parts.Skip(1))
        {
            if (Regex.IsMatch(
                    NormalizeLooseLookup(part),
                    @"\b(?:preparation|instructions?|degraissez|faites|ajoutez|incorporez|laissez|mijoter|cuire|coupez|lavez|melangez|etape|steps?)\b",
                    RegexOptions.CultureInvariant))
            {
                break;
            }

            if (TryScaleQuantitySegment(part, factor, out var scaled))
            {
                lines.Add(new QuantityScaleLine(part, scaled));
            }
            else if (Regex.IsMatch(NormalizeLooseLookup(part), @"\b(?:sel|poivre|salt|pepper|sal|pfeffer|sale|pepe)\b", RegexOptions.CultureInvariant))
            {
                lines.Add(new QuantityScaleLine(part, part + " (au gout)"));
            }
        }

        return lines;
    }

    private static bool TryScaleQuantitySegment(string segment, double factor, out string scaled)
    {
        scaled = string.Empty;
        var match = Regex.Match(
            segment,
            @"^\s*(?<num>\d+(?:[,.]\d+)?|[1-9]\d*\s*/\s*[1-9]\d*|\u00bd|\u00bc|\u00be)\s*(?<rest>.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        if (!TryParseFlexibleNumber(match.Groups["num"].Value, out var number))
            return false;

        var rest = CollapseWhitespace(match.Groups["rest"].Value);
        if (string.IsNullOrWhiteSpace(rest))
            return false;

        var scaledNumber = number * factor;
        scaled = FormatScaleNumber(scaledNumber) + " " + rest;
        return true;
    }

    private static bool TryParseFlexibleNumber(string value, out double number)
    {
        number = 0;
        var s = CollapseWhitespace(value)
            .Replace(',', '.');

        if (s == "\u00bd")
        {
            number = 0.5;
            return true;
        }
        if (s == "\u00bc")
        {
            number = 0.25;
            return true;
        }
        if (s == "\u00be")
        {
            number = 0.75;
            return true;
        }

        var fraction = Regex.Match(s, @"^(?<a>\d+)\s*/\s*(?<b>\d+)$", RegexOptions.CultureInvariant);
        if (fraction.Success
            && double.TryParse(fraction.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(fraction.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && b != 0)
        {
            number = a / b;
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string FormatScaleNumber(double value)
    {
        var rounded = Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value)
            : Math.Round(value, 2);

        return rounded.ToString(rounded % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)
            .Replace('.', ',');
    }

    private static string BuildSourceBackedAdaptationAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var selected = RankSourceBackedAdaptationHits(hits, query)
            .Take(5)
            .ToList();
        if (selected.Count == 0)
            return string.Empty;

        var labels = BuildSourceBackedAdaptationLabels(language);
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.AppendLine(labels.SourceSection);
        foreach (var hit in selected.Take(4))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        var targetFacts = ExtractSourceBackedAdaptationTargetFacts(selected, query, maxFacts: 5);
        if (targetFacts.Count > 0)
        {
            sb.AppendLine(SourceBackedLabel(
                language,
                "Cibles visibles a adapter :",
                "Visible targets to adapt:",
                "Objetivos visibles que adaptar:",
                "Alvos visiveis a adaptar:",
                "Sichtbare anzupassende Ziele:",
                "Obiettivi visibili da adattare:"));
            foreach (var fact in targetFacts)
            {
                sb.Append("- ");
                sb.AppendLine(fact);
            }
        }

        sb.AppendLine(labels.AdaptationSection);
        foreach (var note in BuildSourceBackedAdaptationNotes(language, query))
        {
            sb.Append("- ");
            sb.AppendLine(note);
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<RagHitSummary> RankSourceBackedAdaptationHits(IEnumerable<RagHitSummary> hits, string query)
    {
        var focusGroups = BuildSourceBackedAdaptationFocusGroups(query);
        var focusQuery = string.Join(' ', focusGroups.SelectMany(static group => group).Distinct(StringComparer.Ordinal));
        var scored = hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(hit =>
            {
                var primaryText = GetRagHitPrimaryEvidenceText(hit);
                var lookupText = GetRagHitLookupText(hit);
                var primaryMatches = CountMatchedFocusGroups(focusGroups, primaryText);
                var lookupMatches = CountMatchedFocusGroups(focusGroups, lookupText);
                return new
                {
                    Hit = hit,
                    MatchedFocusGroups = primaryMatches,
                    LookupMatchedFocusGroups = lookupMatches,
                    TargetFactScore = CountSourceBackedAdaptationTargetFacts(hit, query),
                    PrimaryRelevance = ComputeRagHitLexicalRelevance(focusQuery, primaryText),
                    Relevance = ComputeRagHitLexicalRelevance(focusQuery, lookupText),
                    IsIntroLead = LooksLikeIntroLeadInHit(hit)
                };
            })
            .ToList();

        if (scored.Count == 0)
            return Array.Empty<RagHitSummary>();

        var bestMatchCount = scored.Max(static item => item.MatchedFocusGroups);
        if (bestMatchCount >= 2)
        {
            scored = scored
                .Where(item => item.MatchedFocusGroups >= 2)
                .ToList();
        }

        var bestTargetFactScore = scored.Max(static item => item.TargetFactScore);
        if (bestTargetFactScore > 0)
        {
            scored = scored
                .Where(static item => item.TargetFactScore > 0)
                .ToList();
        }

        var nonIntroScored = scored
            .Where(static item => !item.IsIntroLead)
            .ToList();
        if (nonIntroScored.Count > 0)
            scored = nonIntroScored;

        return scored
            .OrderByDescending(static item => item.MatchedFocusGroups)
            .ThenByDescending(static item => item.TargetFactScore)
            .ThenByDescending(static item => item.LookupMatchedFocusGroups)
            .ThenByDescending(static item => item.PrimaryRelevance)
            .ThenByDescending(static item => item.Relevance)
            .ThenByDescending(static item => item.Hit.Score)
            .Select(static item => item.Hit)
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildSourceBackedAdaptationFocusGroups(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<IReadOnlyList<string>>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !Regex.IsMatch(
                term,
                @"\b(?:alleger|all[e\u00e9]ger|lighten|reduce|reduire|adapter|adaptation|adapte|changer|modifier|change|modify|prudente|prudent)\b",
                RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .Select(static term => (IReadOnlyList<string>)BuildRetrievalTermVariants(term)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .Where(static group => group.Count > 0)
            .ToArray();
    }

    private static int CountMatchedFocusGroups(IReadOnlyList<IReadOnlyList<string>> focusGroups, string? text)
    {
        if (focusGroups.Count == 0 || string.IsNullOrWhiteSpace(text))
            return 0;

        var normalizedText = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return focusGroups.Count(group => group.Any(term =>
            term.Length >= 3 && normalizedText.Contains(term, StringComparison.Ordinal)));
    }

    private static (string Header, string SourceSection, string AdaptationSection, string Caution) BuildSourceBackedAdaptationLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "I separate what is directly supported by the documents from the cautious adaptation.",
                "From the documents:",
                "Cautious adaptation:",
                "I do not invent exact quantities or steps that are not visible in the cited excerpts."),
            "es" => (
                "Separo lo que esta directamente respaldado por los documentos de la adaptacion prudente.",
                "Lo que viene de los documentos:",
                "Adaptacion prudente:",
                "No invento cantidades ni pasos exactos que no aparezcan en los extractos citados."),
            "pt" => (
                "Separo o que esta diretamente apoiado pelos documentos da adaptacao prudente.",
                "O que vem dos documentos:",
                "Adaptacao prudente:",
                "Nao invento quantidades nem passos exatos que nao estejam visiveis nos excertos citados."),
            "de" => (
                "Ich trenne, was direkt aus den Dokumenten belegt ist, von der vorsichtigen Anpassung.",
                "Aus den Dokumenten:",
                "Vorsichtige Anpassung:",
                "Ich erfinde keine exakten Mengen oder Schritte, die in den zitierten Auszuegen nicht sichtbar sind."),
            "it" => (
                "Separo cio che e direttamente supportato dai documenti dall'adattamento prudente.",
                "Dai documenti:",
                "Adattamento prudente:",
                "Non invento quantita o passaggi esatti che non sono visibili negli estratti citati."),
            _ => (
                "Je separe ce qui est directement appuye par les documents de l'adaptation prudente.",
                "Ce qui vient des PDF :",
                "Adaptation prudente :",
                "Je n'invente pas de quantites ni d'etapes exactes absentes des extraits cites.")
        };
    }

    private static IReadOnlyList<string> BuildSourceBackedAdaptationNotes(string language, string query)
    {
        var objective = ExtractSourceBackedAdaptationObjective(query);
        var focusTerms = ExtractSourceBackedAdaptationFocusTerms(query);
        var focusSuffix = focusTerms.Count == 0 ? string.Empty : string.Join(", ", focusTerms);
        return NormalizeLanguageCode(language) switch
        {
            "en" => new[]
            {
                $"Use the cited excerpts as the base; the adaptation target is: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioritize the passages where the requested target appears explicitly." : $"Prioritize the passages where these requested targets appear explicitly: {focusSuffix}.",
                "Apply the requested change only to elements directly concerned by the question, then validate the result step by step.",
                "For a final operational version, choose one cited source page so the adapted answer can keep every source-backed constraint visible."
            },
            "es" => new[]
            {
                $"Usa los extractos citados como base; el objetivo de adaptacion es: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioriza los pasajes donde el objetivo solicitado aparece explicitamente." : $"Prioriza los pasajes donde estos objetivos solicitados aparecen explicitamente: {focusSuffix}.",
                "Aplica el cambio pedido solo a los elementos directamente afectados por la pregunta y valida el resultado paso a paso.",
                "Para una version operativa final, elige una pagina fuente citada para mantener visibles todas las restricciones documentadas."
            },
            "pt" => new[]
            {
                $"Usa os excertos citados como base; o objetivo da adaptacao e: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Da prioridade aos excertos onde o alvo pedido aparece explicitamente." : $"Da prioridade aos excertos onde estes alvos pedidos aparecem explicitamente: {focusSuffix}.",
                "Aplica a alteracao pedida apenas aos elementos diretamente ligados a pergunta e valida o resultado passo a passo.",
                "Para uma versao operacional final, escolhe uma pagina fonte citada para manter visiveis todas as restricoes documentadas."
            },
            "de" => new[]
            {
                $"Nutze die zitierten Auszuege als Grundlage; das Anpassungsziel ist: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Bevorzuge die Stellen, in denen das angefragte Ziel ausdruecklich vorkommt." : $"Bevorzuge die Stellen, in denen diese angefragten Ziele ausdruecklich vorkommen: {focusSuffix}.",
                "Wende die angefragte Aenderung nur auf Elemente an, die direkt von der Frage betroffen sind, und pruefe das Ergebnis schrittweise.",
                "Fuer eine endgueltige Arbeitsversion waehle eine zitierte Quellseite, damit alle belegten Einschraenkungen sichtbar bleiben."
            },
            "it" => new[]
            {
                $"Usa gli estratti citati come base; l'obiettivo di adattamento e: {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Dai priorita ai passaggi in cui l'obiettivo richiesto appare esplicitamente." : $"Dai priorita ai passaggi in cui questi obiettivi richiesti appaiono esplicitamente: {focusSuffix}.",
                "Applica il cambiamento richiesto solo agli elementi direttamente coinvolti dalla domanda e valida il risultato passo passo.",
                "Per una versione operativa finale, scegli una pagina fonte citata cosi da mantenere visibili tutti i vincoli documentati."
            },
            _ => new[]
            {
                $"Prendre les extraits cites comme base ; l'objectif d'adaptation est : {objective}.",
                string.IsNullOrWhiteSpace(focusSuffix) ? "Prioriser les passages ou la cible demandee apparait explicitement." : $"Prioriser les passages ou ces cibles demandees apparaissent explicitement : {focusSuffix}.",
                "Isoler les quantites ou contraintes visibles liees a la cible, puis modifier seulement cette partie en gardant les autres contraintes sourcees inchangees.",
                "Faire l'adaptation par petits paliers et verifier le resultat : si la cible participe a la texture, a la cuisson, a la conservation ou a la securite, la validation devient obligatoire.",
                "Pour une version finale operationnelle, choisir une page source citee afin de garder visibles toutes les contraintes documentees."
            }
        };
    }

    private static IReadOnlyList<string> ExtractSourceBackedAdaptationTargetFacts(IReadOnlyList<RagHitSummary> hits, string query, int maxFacts)
    {
        var targetGroups = BuildSourceBackedAdaptationTargetGroups(query);
        if (targetGroups.Count == 0)
            targetGroups = BuildSourceBackedAdaptationFocusGroups(query);
        if (targetGroups.Count == 0)
            return Array.Empty<string>();

        var facts = new List<string>();
        foreach (var hit in hits)
        {
            var segments = ExtractAdaptationEvidenceSegments(hit, targetGroups)
                .Take(3)
                .ToList();
            if (segments.Count == 0)
                continue;

            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            facts.Add($"{docLabel} p.{hit.PageStart} : {string.Join("; ", segments)}");
            if (facts.Count >= maxFacts)
                break;
        }

        return facts;
    }

    private static int CountSourceBackedAdaptationTargetFacts(RagHitSummary hit, string query)
    {
        var targetGroups = BuildSourceBackedAdaptationTargetGroups(query);
        if (targetGroups.Count == 0)
            targetGroups = BuildSourceBackedAdaptationFocusGroups(query);
        if (targetGroups.Count == 0)
            return 0;

        return ExtractAdaptationEvidenceSegments(hit, targetGroups).Take(4).Count();
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildSourceBackedAdaptationTargetGroups(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<IReadOnlyList<string>>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:moins\s+de|less|reduce(?:d)?|reduire|reduit|diminuer|diminution|alleger|all[e\u00e9]ger|lighten|sans|without|en|in|de|du|des)\s+(?:la\s+|le\s+|les\s+|l\s+|d\s+|the\s+)?(?<target>[\p{L}][\p{L}\p{N}_-]{2,30})",
            RegexOptions.CultureInvariant))
        {
            var term = match.Groups["target"].Value;
            if (!IsSourceBackedActionRetrievalNoiseTerm(term))
                terms.Add(term);
        }

        if (Regex.IsMatch(normalized, @"\b(?:sucre|sucres|sugar|azucar|azucarado|zucker|zucchero)\b", RegexOptions.CultureInvariant))
            terms.Insert(0, "sucre");
        if (Regex.IsMatch(normalized, @"\b(?:sel|salt|sal|salz|sale)\b", RegexOptions.CultureInvariant))
            terms.Insert(0, "sel");
        if (Regex.IsMatch(normalized, @"\b(?:gras|graisse|beurre|huile|fat|butter|oil|grasa|mantequilla|aceite|fett|butter|olio)\b", RegexOptions.CultureInvariant))
            terms.Insert(0, "gras");

        return terms
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 3)
            .Where(static term => !Regex.IsMatch(term, @"\b(?:dessert|desserts|document|documents|pdf|source|sources|adaptation|prudente|base)\b", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .Select(static term => (IReadOnlyList<string>)BuildRetrievalTermVariants(term)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .Where(static group => group.Count > 0)
            .ToArray();
    }

    private static IEnumerable<string> ExtractAdaptationEvidenceSegments(RagHitSummary hit, IReadOnlyList<IReadOnlyList<string>> targetGroups)
    {
        var text = CollapseWhitespace(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var previousWasIngredientLabel = false;
        foreach (var segment in Regex.Split(text, @"(?:[•\n\r]|(?<=[.;:])\s+)"))
        {
            var value = CollapseWhitespace(segment).Trim(' ', '-', ':', ';', ',');
            if (value.Length < 5)
                continue;
            if (value.Length > 140)
                value = value[..140].TrimEnd() + "...";

            var normalizedSegment = NormalizeLexicalLookup(value);
            var carriesIngredientContext = previousWasIngredientLabel;
            previousWasIngredientLabel = Regex.IsMatch(
                normalizedSegment,
                @"^\b(?:ingredient|ingredients|zutat|zutaten|ingrediente|ingredientes|ingredienti)\b$",
                RegexOptions.CultureInvariant);
            if (!targetGroups.Any(group => group.Any(term => term.Length >= 3 && normalizedSegment.Contains(term, StringComparison.Ordinal))))
                continue;

            var hasSpecificSignal = Regex.IsMatch(
                normalizedSegment,
                @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.?\s*a|c\.?\s*c|cuill[eè]res?|tbsp|tsp|%|min|h)\b|\b(?:ingredient|ingredients|quantite|quantites|amount|quantity|constraint|contrainte)\b",
                RegexOptions.CultureInvariant);
            if (!hasSpecificSignal && carriesIngredientContext && value.Contains(',', StringComparison.Ordinal))
                hasSpecificSignal = true;
            if (!hasSpecificSignal)
                continue;

            yield return value;
        }
    }

    private static string ExtractSourceBackedAdaptationObjective(string query)
    {
        var objective = CollapseWhitespace(query);
        objective = Regex.Replace(
            objective,
            @"(?i)\b(?:dis\s+bien|separe|separer|distingue|distinguer|indique|indiquer|precise|preciser|tell|separate|distinguish|show|state)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        objective = objective.Trim(' ', '.', '?', '!', ':', ';', ',');
        if (objective.Length == 0)
            objective = CollapseWhitespace(query);

        return objective.Length <= 140
            ? objective
            : objective[..140].TrimEnd() + "...";
    }

    private static IReadOnlyList<string> ExtractSourceBackedAdaptationFocusTerms(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !Regex.IsMatch(
                term,
                @"\b(?:alleger|all[e\u00e9]ger|lighten|reduce|reduire|adapter|adaptation|adapte|changer|modifier|change|modify|prudente|prudent)\b",
                RegexOptions.CultureInvariant))
            .SelectMany(BuildRetrievalTermVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static string BuildSourceBackedRankingAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var ranked = RankSourceBackedRankingHits(hits, query)
            .Take(4)
            .ToList();
        if (ranked.Count == 0)
            return string.Empty;

        var labels = BuildSourceBackedRankingLabels(language);
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);

        var winner = ranked[0];
        sb.Append("- ");
        sb.Append(labels.MainCandidate);
        sb.Append(" : ");
        sb.Append(FormatRankingHitReference(winner));
        sb.Append(" - ");
        sb.AppendLine(BuildSourceBackedRankingReason(language, winner, query));

        if (ranked.Count > 1)
        {
            sb.AppendLine(labels.OtherCandidates);
            foreach (var hit in ranked.Skip(1).Take(3))
            {
                sb.Append("- ");
                sb.Append(FormatRankingHitReference(hit));
                sb.Append(" - ");
                sb.AppendLine(BuildSourceBackedRankingReason(language, hit, query));
            }
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static (string Header, string MainCandidate, string OtherCandidates, string Caution) BuildSourceBackedRankingLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "Based only on the available excerpts, here is the most defensible ranking:",
                "Main candidate",
                "Other source-backed candidates:",
                "This is not an absolute ranking of the whole knowledge base; it only reflects the retrieved excerpts."),
            "es" => (
                "Basandome solo en los extractos disponibles, este es el ranking mas defendible:",
                "Candidato principal",
                "Otros candidatos con fuente:",
                "No es un ranking absoluto de toda la base de conocimiento; refleja solo los extractos recuperados."),
            "pt" => (
                "Com base apenas nos excertos disponiveis, este e o ranking mais defensavel:",
                "Candidato principal",
                "Outros candidatos com fonte:",
                "Nao e um ranking absoluto de toda a base de conhecimento; reflete apenas os excertos recuperados."),
            "de" => (
                "Nur auf Basis der verfuegbaren Auszuege ist dies die belastbarste Rangfolge:",
                "Hauptkandidat",
                "Weitere belegte Kandidaten:",
                "Das ist keine absolute Rangfolge der gesamten Wissensbasis; sie spiegelt nur die gefundenen Auszuege wider."),
            "it" => (
                "Basandomi solo sugli estratti disponibili, questa e la classifica piu difendibile:",
                "Candidato principale",
                "Altri candidati con fonte:",
                "Non e una classifica assoluta di tutta la base di conoscenza; riflette solo gli estratti recuperati."),
            _ => (
                "D'apres les extraits disponibles uniquement, voici le classement le plus defendable :",
                "Candidat principal",
                "Autres candidats sources :",
                "Ce n'est pas un classement absolu de toute la base de connaissance ; il reflete seulement les extraits retrouves.")
        };
    }

    private static IReadOnlyList<RagHitSummary> RankSourceBackedRankingHits(IEnumerable<RagHitSummary> hits, string query)
    {
        var candidateHits = hits.ToList();
        if (candidateHits.Count == 0)
            return Array.Empty<RagHitSummary>();

        candidateHits = FilterHitsToDominantTopLevel(candidateHits, query).ToList();
        var technicalRanking = LooksLikeTechnicalRankingQuery(query);
        if (LooksLikeDessertRankingQuery(query))
        {
            if (!technicalRanking)
            {
                var structuredDessertHits = candidateHits
                    .Where(static hit => ComputeStructuredProcedureEvidenceCueScore(hit) >= 4)
                    .Where(static hit => ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 4)
                    .ToList();
                if (structuredDessertHits.Count > 0)
                    candidateHits = structuredDessertHits;
            }

            var nonIntroHits = candidateHits
                .Where(static hit => !LooksLikeIntroLeadInHit(hit))
                .ToList();
            if (nonIntroHits.Count >= 2)
                candidateHits = nonIntroHits;
        }

        if (technicalRanking)
        {
            var singlePageHits = candidateHits
                .Where(static hit => hit.PageEnd <= hit.PageStart)
                .ToList();
            if (singlePageHits.Count >= 2)
                candidateHits = singlePageHits;

            var nonColdAssemblyHits = candidateHits
                .Where(static hit => !LooksLikeColdAssemblyProcedureHit(hit))
                .ToList();
            if (nonColdAssemblyHits.Count > 0)
                candidateHits = nonColdAssemblyHits;

            var nonGenericTechniqueHits = candidateHits
                .Where(static hit => !LooksLikeGenericTechniqueDefinitionHit(hit))
                .ToList();
            if (nonGenericTechniqueHits.Count > 0)
                candidateHits = nonGenericTechniqueHits;

            var completeProcedureHits = candidateHits
                .Where(static hit => ComputeProcedureCompletenessCueScore(hit) >= 7)
                .ToList();
            if (completeProcedureHits.Count > 0)
                candidateHits = completeProcedureHits;

            var technicallyConstrainedHits = candidateHits
                .Where(static hit => ComputeTechnicalComplexityCueScore(hit) >= 5)
                .ToList();
            if (technicallyConstrainedHits.Count > 0)
                candidateHits = technicallyConstrainedHits;

            var nonSimpleHits = candidateHits
                .Where(static hit => !LooksLikeVerySimpleProcedureHit(hit))
                .ToList();
            if (nonSimpleHits.Count >= 2)
                candidateHits = nonSimpleHits;

            nonColdAssemblyHits = candidateHits
                .Where(static hit => !LooksLikeColdAssemblyProcedureHit(hit))
                .ToList();
            if (nonColdAssemblyHits.Count > 0)
                candidateHits = nonColdAssemblyHits;
        }

        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        return candidateHits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Score = ComputeSourceBackedRankingScore(hit, query, evidenceQuery)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .GroupBy(item => $"{item.Hit.DocPath}|{item.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(item => item.Hit)
            .ToList();
    }

    private static double ComputeSourceBackedRankingScore(RagHitSummary hit, string query, string evidenceQuery)
    {
        var score = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(hit)) * 3
            + ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit))
            + Math.Min(3.0, Math.Max(0.0, hit.Score));

        if (LooksLikeTechnicalRankingQuery(query))
            score += ComputeTechnicalEvidenceCueScore(hit);

        var dessertRanking = LooksLikeDessertRankingQuery(query);
        var structuredProcedureScore = 0;
        var technicalComplexityScore = LooksLikeTechnicalRankingQuery(query)
            ? ComputeTechnicalComplexityCueScore(hit)
            : 0;
        if (LooksLikeRecipeOrProcedureRankingQuery(query) || dessertRanking)
        {
            structuredProcedureScore = ComputeStructuredProcedureEvidenceCueScore(hit);
            score += structuredProcedureScore;
        }

        if (dessertRanking)
        {
            score += ComputeDessertEvidenceCueScore(hit);
            score += structuredProcedureScore;
            score += technicalComplexityScore * 2;
            score += ComputeProcedureCompletenessCueScore(hit) * 3;
            if (structuredProcedureScore < 5)
                score -= 24;
            if (LooksLikeTechnicalRankingQuery(query) && technicalComplexityScore < 5)
                score -= 30;
            if (LooksLikeMidProcedureFragment(hit))
                score -= 28;
            if (LooksLikeIntroLeadInHit(hit))
                score -= 32;
            if (LooksLikeVerySimpleProcedureHit(hit))
                score -= 26;
        }

        return score;
    }

    private static bool LooksLikeTechnicalRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:technique|technical|tecnico|tecnica|technisch|complexe|complex|complique|difficile|difficult|schwierig|avance|advanced)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeTechnicalEvidenceCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        score += Math.Min(4, Regex.Matches(text, @"\b(?:etape|etapes|step|steps|procedure|procedures|preparation|mode operatoire|instruction|instructions|method|methode)\b", RegexOptions.CultureInvariant).Count);
        score += Math.Min(4, Regex.Matches(text, @"\b(?:temperature|temperatures|vitesse|vitesses|parametre|parametres|setting|settings|temps|time|duration|duree|min|minutes|c|celsius)\b", RegexOptions.CultureInvariant).Count);
        score += Math.Min(2, Regex.Matches(text, @"\b(?:materiel|outil|outils|ustensile|ustensiles|equipement|equipment|device|instrument|thermometre|control|controle|verification)\b", RegexOptions.CultureInvariant).Count);
        score += Math.Min(3, Regex.Matches(text, @"\b(?:chauffer|melanger|tamiser|remuer|plier|monter|incorporer|calibrer|mesurer|verifier|adjust|mix|measure|check)\b", RegexOptions.CultureInvariant).Count);
        return score;
    }

    private static int ComputeTechnicalComplexityCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        score += Math.Min(8, Regex.Matches(
            text,
            @"\b(?:temperature|temperatures|cuisson|cuire|four|prechauff|oven|bake|frire|friture|fryer|bain\s+marie|repos|reposer|refroidir|minutes?|heures?|vitesse|vitesses|programme|program)\b|\b\d+\s*(?:(?:\u00b0|deg|degres?)\s*c|celsius)\b|\b\d+\s*(?:min|h)\b",
            RegexOptions.CultureInvariant).Count * 2);
        score += Math.Min(4, Regex.Matches(
            text,
            @"\b(?:fondre|fremir|fremisse|fouetter|incorporer|tamiser|monter|emulsion|caramel|carameliser|dorer|egoutter|temperer)\b",
            RegexOptions.CultureInvariant).Count);
        score += Math.Min(5, Regex.Matches(
            text,
            @"\b(?:materiel|outil|outils|ustensile|ustensiles|equipement|equipment|saladier|casserole|moule|plat|balance|verre\s+mesureur|fouet|spatule|robot|thermometre|maillet|torchon|ramequin|poele)\b",
            RegexOptions.CultureInvariant).Count);
        if (Regex.IsMatch(
                text,
                @"\b(?:maillet|souffle|bain\s+marie|thermometre|temperer|emulsion|monter\s+en\s+neige|separer\s+les\s+blancs|plier|incorporer\s+delicatement)\b",
                RegexOptions.CultureInvariant))
        {
            score += 3;
        }
        score += Math.Min(3, Regex.Matches(text, @"(?:^|\s)[1-9][\.)]\s+", RegexOptions.CultureInvariant).Count);
        return score;
    }

    private static int ComputeProcedureCompletenessCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        var hasIngredients = Regex.IsMatch(text, @"\b(?:ingredient|ingredients|zutat|zutaten|ingrediente|ingredientes|ingredienti)\b", RegexOptions.CultureInvariant);
        var hasPreparation = Regex.IsMatch(text, @"\b(?:preparation|preparacion|preparacao|zubereitung|preparazione|procedure|procedures|method|methode|etape|etapes|steps?|pasos|passos|technique)\b", RegexOptions.CultureInvariant);
        var hasPortionsOrTime = Regex.IsMatch(text, @"\b(?:pour\s+\d+|\d+\s+(?:personnes?|portions?|porciones|porcoes|persone|personen)|temps|time|dauer|tempo|tiempo|repos|rest|ruhezeit|\d+\s*(?:min|h|heures?))\b", RegexOptions.CultureInvariant);
        var quantityCount = Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.?\s*a|c\.?\s*c|cuill[eè]res?|tbsp|tsp|min|h)\b", RegexOptions.CultureInvariant).Count;
        var stepCount = CountProcedureStepMarkers(text);

        if (hasIngredients) score += 3;
        if (hasPreparation) score += 3;
        if (hasPortionsOrTime) score += 2;
        score += Math.Min(3, quantityCount);
        score += Math.Min(4, stepCount);
        if (hasIngredients && hasPreparation) score += 3;
        if (stepCount >= 3 && quantityCount >= 3) score += 2;
        if (LooksLikeMidProcedureFragment(hit)) score -= 6;

        return score;
    }

    private static int CountProcedureStepMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var numeric = Regex.Matches(
            normalizedText,
            @"(?:^|\s|preparation|preparacion|preparacao|zubereitung|preparazione|technique)[\s:]*[1-9][\.)]\s*",
            RegexOptions.CultureInvariant).Count;
        var bullets = Regex.Matches(
            normalizedText,
            @"[•\-]\s*(?:prechauff|chauff|faites?|melang|fouett|incorpor|ajout|verse|coupez?|mettez?|laissez?|cuire|enfournez?|mixez?|tamiser|separer|battre|egoutter|retirer)",
            RegexOptions.CultureInvariant).Count;

        return numeric + bullets;
    }

    private static bool LooksLikeMidProcedureFragment(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasIngredients = Regex.IsMatch(text, @"\b(?:ingredient|ingredients|zutat|zutaten|ingrediente|ingredientes|ingredienti)\b", RegexOptions.CultureInvariant);
        var hasEarlySteps = Regex.IsMatch(text, @"(?:^|\s|preparation|technique)[\s:]*[1-3][\.)]\s*", RegexOptions.CultureInvariant);
        var hasLateSteps = Regex.IsMatch(text, @"(?:^|\s)[4-9][\.)]\s*", RegexOptions.CultureInvariant);
        var startsMidSentence = Regex.IsMatch(
            text,
            @"^(?:arreter|ajouter|melanger|lorsque|repartir|cuire|laisser|verser|retirer|incorporer|transvider|stop|add|mix|when|bake|let|pour)\b",
            RegexOptions.CultureInvariant);

        return (startsMidSentence && !hasIngredients)
            || (hasLateSteps && !hasEarlySteps && !hasIngredients);
    }

    private static bool LooksLikeIntroLeadInHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = text.Length <= 360 ? text : text[..360];
        var hasIntroLead = Regex.IsMatch(
            lead,
            @"\b(?:l heure du dessert|recettes sucrees|bienvenue|introduction|avant propos|preface|sommaire|table des matieres|reperes de contenu|premiers extraits)\b",
            RegexOptions.CultureInvariant);
        if (!hasIntroLead)
            return false;

        var firstStructure = Regex.Match(
            text,
            @"\b(?:ingredient|ingredients|preparation|preparacion|preparacao|zubereitung|preparazione|pour\s+\d+|\d+\s+(?:personnes?|portions?))\b",
            RegexOptions.CultureInvariant);
        return !firstStructure.Success || firstStructure.Index > 280;
    }

    private static bool LooksLikeVerySimpleProcedureHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lookupText = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var hasSimpleWording = Regex.IsMatch(
            text,
            @"\b(?:facile|faciles|simple|simples|rapide|rapides|plus simple|very simple|easy|quick)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                lookupText,
                @"\b(?:facile|faciles|simple|simples|rapide|rapides|plus simple|very simple|easy|quick)\b",
            RegexOptions.CultureInvariant);
        var hasVeryShortDuration = Regex.IsMatch(
            text,
            @"\b(?:1|2|3)\s*min\b",
            RegexOptions.CultureInvariant);
        var hasHeatOrControlledCooking = Regex.IsMatch(
            text,
            @"\b(?:four|cuire|cuisson|frire|friture|poele|bain marie|caramel|carameliser|prechauff|fremir|mijoter|temperature|temperatures)\b|\b\d+\s*(?:(?:\u00b0|deg|degres?)\s*c|celsius)\b",
            RegexOptions.CultureInvariant);
        var lacksCookingOrRest = !Regex.IsMatch(
            text,
            @"\b(?:four|cuire|cuisson|frire|friture|bain marie|repos|reposer|refroidir|caramel|carameliser|prechauff|fremir|mijoter)\b",
            RegexOptions.CultureInvariant);

        if (hasSimpleWording && hasVeryShortDuration && !hasHeatOrControlledCooking)
            return true;
        if (hasVeryShortDuration && !hasHeatOrControlledCooking && CountProcedureStepMarkers(text) <= 3)
            return true;

        return (hasSimpleWording || hasVeryShortDuration)
            && lacksCookingOrRest
            && ComputeTechnicalComplexityCueScore(hit) < 9;
    }

    private static bool LooksLikeColdAssemblyProcedureHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup($"{GetRagHitPrimaryContentText(hit)} {GetRagHitLookupText(hit)}");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasColdOrRawIngredients = Regex.IsMatch(
            text,
            @"\b(?:fruit|fruits|fraise|fraises|framboise|framboises|yaourt|yogurt|yoghurt|miel|creme\s+fleurette|surgel|congele|congelee|frozen)\b",
            RegexOptions.CultureInvariant);
        var hasAssemblyOnlyVerbs = Regex.IsMatch(
            text,
            @"\b(?:laver|equeuter|couper|melanger|mixer|mixez|mix|blend|retirez|servez)\b",
            RegexOptions.CultureInvariant);
        var hasControlledCooking = Regex.IsMatch(
            text,
            @"\b(?:four|cuire|cuisson|frire|friture|poele|bain\s+marie|caramel|carameliser|prechauff|fremir|mijoter|temperature|temperatures|dorer|enfourner|repos|reposer)\b|\b\d+\s*(?:(?:\u00b0|deg|degres?)\s*c|celsius)\b",
            RegexOptions.CultureInvariant);
        var hasVeryShortBlend = Regex.IsMatch(
            text,
            @"\b(?:mixez|mixer|mix|blend|melanger)\b.{0,140}\b(?:1|2|3)\s*min\b|\b(?:1|2|3)\s*min\b.{0,140}\b(?:mixez|mixer|mix|blend|melanger)\b",
            RegexOptions.CultureInvariant);

        if (!hasControlledCooking && hasVeryShortBlend)
            return true;

        return !hasControlledCooking
            && hasColdOrRawIngredients
            && hasAssemblyOnlyVerbs
            && ComputeTechnicalComplexityCueScore(hit) < 12;
    }

    private static bool LooksLikeGenericTechniqueDefinitionHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasDefinitionWording = Regex.IsMatch(
            text,
            @"\b(?:cette\s+technique\s+consiste|technique\s+consiste|mode\s+de\s+preparation|modes\s+de\s+preparation|preparation\s+method|cooking\s+technique)\b",
            RegexOptions.CultureInvariant);
        if (!hasDefinitionWording)
            return false;

        var hasRecipeEvidence = Regex.IsMatch(
                text,
                @"\b(?:ingredient|ingredients|pour\s+\d+|\d+\s+(?:personnes?|portions?)|materiel|ustensile|ustensiles|equipment|preparation\s*[:•]|technique\s*[:•])\b",
                RegexOptions.CultureInvariant)
            || CountProcedureStepMarkers(text) >= 2
            || Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.?\s*a|c\.?\s*c|cuill[eè]res?|tbsp|tsp|min|h)\b", RegexOptions.CultureInvariant).Count >= 2;

        return !hasRecipeEvidence;
    }

    private static bool LooksLikeRecipeOrProcedureRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:recette|recettes|recipe|recipes|ricetta|ricette|rezept|rezepte|procedura|procedure|procedures|procedimento|preparation|preparacion|preparacao|zubereitung|etape|etapes|steps|pasos|passos)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDessertRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:dessert|desserts|gateau|gateaux|gato|bolo|bolos|kuchen|torta|torte|tarte|tartes|creme|chocolat|chocolate|sucre|sucree|sucrees|sweet|dulce|dolce|suss|suess)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeStructuredProcedureEvidenceCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        if (Regex.IsMatch(text, @"\b(?:ingredient|ingredients|zutat|zutaten|ingrediente|ingredientes|ingredienti)\b", RegexOptions.CultureInvariant))
            score += 3;
        if (Regex.IsMatch(text, @"\b(?:preparation|preparacion|preparacao|zubereitung|preparazione|etape|etapes|steps?|pasos|passos)\b", RegexOptions.CultureInvariant))
            score += 3;
        if (Regex.IsMatch(text, @"\b(?:pour\s+\d+|\d+\s+(?:personnes?|portions?|porciones|porcoes|persone|personen)|temps|time|dauer|tempo|tiempo|temperature|temperatures|temperatura|temperaturen|\d+\s*(?:°\s*)?c|celsius)\b", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|min|h)\b", RegexOptions.CultureInvariant))
            score += 2;

        if (score == 0 && Regex.IsMatch(text, @"\b(?:menu|sommaire|contents?|index|glossaire|lexique)\b", RegexOptions.CultureInvariant))
            score -= 2;

        return score;
    }

    private static int ComputeDessertEvidenceCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        score += Math.Min(4, Regex.Matches(text, @"\b(?:dessert|desserts|gateau|gateaux|gato|bolo|bolos|kuchen|torta|tarte|tartes|creme|chocolat|chocolate|sucre|sucree|sucrees|cassonade|vanille|vanilla|oeuf|oeufs|jaune|jaunes)\b", RegexOptions.CultureInvariant).Count);
        if (Regex.IsMatch(text, @"\b(?:ingredient|ingredients|preparation|etape|etapes)\b", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(text, @"\b(?:glossaire|lexique|index|sommaire|technique\s+generale|general technique)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(text, @"\b(?:ingredient|ingredients|preparation|etape|etapes|\d+\s*(?:g|kg|mg|ml|cl|l))\b", RegexOptions.CultureInvariant))
        {
            score -= 4;
        }

        return score;
    }

    private static int ComputeDessertVisibleEvidenceCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Math.Min(4, Regex.Matches(text, @"\b(?:dessert|desserts|gateau|gateaux|gato|bolo|bolos|kuchen|torta|tarte|tartes|creme|chocolat|chocolate|sucre|sucree|sucrees|cassonade|vanille|vanilla|oeuf|oeufs|jaune|jaunes)\b", RegexOptions.CultureInvariant).Count);
    }

    private static int ComputeStructuredProcedureVisibleEvidenceCueScore(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var score = 0;
        if (Regex.IsMatch(text, @"\b(?:ingredient|ingredients|zutat|zutaten|ingrediente|ingredientes|ingredienti)\b", RegexOptions.CultureInvariant))
            score += 3;
        if (Regex.IsMatch(text, @"\b(?:preparation|preparacion|preparacao|zubereitung|preparazione|etape|etapes|steps?|pasos|passos)\b", RegexOptions.CultureInvariant))
            score += 3;
        if (Regex.IsMatch(text, @"\b(?:pour\s+\d+|\d+\s+(?:personnes?|portions?|porciones|porcoes|persone|personen)|temps|time|dauer|tempo|tiempo|temperature|temperatures|temperatura|temperaturen|\d+\s*(?:°\s*)?c|celsius)\b", RegexOptions.CultureInvariant))
            score += 2;
        if (Regex.IsMatch(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|min|h)\b", RegexOptions.CultureInvariant))
            score += 2;

        return score;
    }

    private static string FormatRankingHitReference(RagHitSummary hit)
    {
        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        return $"{docLabel} p.{hit.PageStart}";
    }

    private static string BuildSourceBackedRankingReason(string language, RagHitSummary hit, string query)
    {
        var reasons = BuildSourceBackedRankingReasonFragments(language, hit, query);
        var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
        if (reasons.Count == 0)
            reasons.Add(NormalizeLanguageCode(language) switch
            {
                "en" => "it is one of the strongest retrieved matches",
                "es" => "es una de las coincidencias recuperadas mas fuertes",
                "pt" => "e uma das correspondencias recuperadas mais fortes",
                "de" => "es ist einer der staerksten gefundenen Treffer",
                "it" => "e una delle corrispondenze recuperate piu forti",
                _ => "c'est une des correspondances retrouvees les plus fortes"
            });

        var because = NormalizeLanguageCode(language) switch
        {
            "en" => "because",
            "es" => "porque",
            "pt" => "porque",
            "de" => "weil",
            "it" => "perche",
            _ => "car"
        };

        return $"{because} {string.Join(", ", reasons)}. Extrait: {excerpt}";
    }

    private static List<string> BuildSourceBackedRankingReasonFragments(string language, RagHitSummary hit, string query)
    {
        var normalizedLanguage = NormalizeLanguageCode(language);
        var text = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var fragments = new List<string>();

        if (LooksLikeTechnicalRankingQuery(query)
            && Regex.IsMatch(text, @"\b(?:technique|procedure|procedures|mode operatoire|instruction|instructions|etape|etapes|step|steps|preparation|method|methode)\b", RegexOptions.CultureInvariant))
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "the excerpt contains explicit procedural or technical wording",
                "es" => "el extracto contiene vocabulario procedimental o tecnico explicito",
                "pt" => "o excerto contem vocabulario procedural ou tecnico explicito",
                "de" => "der Auszug enthaelt ausdrueckliche Verfahrens- oder Technikbegriffe",
                "it" => "l'estratto contiene lessico procedurale o tecnico esplicito",
                _ => "l'extrait contient du vocabulaire procedural ou technique explicite"
            });
        }

        if (Regex.IsMatch(text, @"\b(?:temperature|temperatures|vitesse|vitesses|parametre|parametres|setting|settings|temps|time|duration|duree|min|minutes|c|celsius)\b", RegexOptions.CultureInvariant))
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "it includes parameters, timing, or measurable constraints",
                "es" => "incluye parametros, tiempos o restricciones medibles",
                "pt" => "inclui parametros, tempos ou restricoes mensuraveis",
                "de" => "er enthaelt Parameter, Zeiten oder messbare Vorgaben",
                "it" => "include parametri, tempi o vincoli misurabili",
                _ => "il contient des parametres, temps ou contraintes mesurables"
            });
        }

        if (Regex.IsMatch(text, @"\b(?:materiel|outil|outils|ustensile|ustensiles|equipement|equipment|device|instrument|control|controle|verification|balance)\b", RegexOptions.CultureInvariant))
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "it mentions tools or equipment",
                "es" => "menciona herramientas o equipo",
                "pt" => "menciona ferramentas ou equipamento",
                "de" => "er nennt Werkzeuge oder Ausstattung",
                "it" => "menziona strumenti o attrezzatura",
                _ => "il mentionne du materiel ou des outils"
            });
        }

        return fragments;
    }

    private static string BuildRagEvidenceSelectionQuery(string query)
    {
        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            var focused = TryBuildComparativeFocusQuery(query);
            if (!string.IsNullOrWhiteSpace(focused))
                return focused!;
        }

        return query;
    }

    private static string BuildSourceBackedExactItemAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string query)
    {
        language = NormalizeLanguageCode(language);
        var displayTitle = ResolveSourceBackedExactItemDisplayTitle(requestedTitle, hits);
        var header = language switch
        {
            "en" => $"I found the requested item \"{displayTitle}\" in the available sources. I keep the answer limited to the cited excerpts:",
            "es" => $"He encontrado el elemento solicitado \"{displayTitle}\" en las fuentes disponibles. Limito la respuesta a los extractos citados:",
            "pt" => $"Encontrei o item solicitado \"{displayTitle}\" nas fontes disponiveis. Limito a resposta aos excertos citados:",
            "de" => $"Ich habe den angefragten Eintrag \"{displayTitle}\" in den verfuegbaren Quellen gefunden. Die Antwort bleibt auf die zitierten Auszuege begrenzt:",
            "it" => $"Ho trovato l'elemento richiesto \"{displayTitle}\" nelle fonti disponibili. Limito la risposta agli estratti citati:",
            _ => $"J'ai trouve l'element demande \"{displayTitle}\" dans les sources disponibles. Je limite la reponse aux extraits cites :"
        };

        if (LooksLikeParameterLookupRequest(query))
            return BuildSourceBackedExactItemParameterAnswer(language, requestedTitle, hits, header);

        if (LooksLikeStructuredItemCardRequest(query))
            return BuildSourceBackedExactItemCardAnswer(language, requestedTitle, hits, header);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        var note = language switch
        {
            "en" => "If you need a step-by-step card, I can only expand the parts visible in these excerpts.",
            "es" => "Si necesitas una ficha paso a paso, solo puedo desarrollar las partes visibles en estos extractos.",
            "pt" => "Se precisares de uma ficha passo a passo, so posso desenvolver as partes visiveis nestes excertos.",
            "de" => "Falls du eine Schritt-fuer-Schritt-Karte brauchst, kann ich nur die in diesen Auszuegen sichtbaren Teile ausarbeiten.",
            "it" => "Se ti serve una scheda passo passo, posso sviluppare solo le parti visibili in questi estratti.",
            _ => "Si tu veux une fiche pas a pas, je ne peux developper que les elements visibles dans ces extraits."
        };
        sb.AppendLine(note);
        return sb.ToString().TrimEnd();
    }

    private static string ResolveSourceBackedExactItemDisplayTitle(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var candidates = hits
            .SelectMany(ExtractSourceBackedTitleCandidates)
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(title => new
            {
                Title = title,
                Score = ComputeSourceBackedDisplayTitleScore(requestedTitle, title)
            })
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Title.Any(char.IsUpper))
            .ThenBy(static item => item.Title.Length)
            .ToArray();

        var best = candidates.FirstOrDefault(static item => item.Score > 0);
        if (best is not null && LooksLikeOcrTitleRunOn(requestedTitle, best.Title))
            return requestedTitle;

        return best?.Title ?? requestedTitle;
    }

    private static bool LooksLikeOcrTitleRunOn(string requestedTitle, string candidateTitle)
    {
        var requested = NormalizeLexicalLookup(requestedTitle);
        var candidate = NormalizeLexicalLookup(candidateTitle);
        if (requested.Length < 4 || candidate.Length <= requested.Length + 14)
            return false;

        return candidate.StartsWith(requested, StringComparison.Ordinal)
            && !Regex.IsMatch(candidate[requested.Length..], @"^\s+(?:de|du|des|a|au|aux|with|and|et)\b", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> ExtractSourceBackedTitleCandidates(RagHitSummary hit)
    {
        foreach (var source in new[] { hit.ContextualSnippet, hit.SectionTitle, hit.HeadingPath })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            foreach (var title in ExtractProfileTitleCandidates(source))
                yield return title;
        }
    }

    private static IEnumerable<string> ExtractProfileTitleCandidates(string value)
    {
        foreach (Match match in Regex.Matches(
            value,
            @"(?i)\bMatched\s+(?:profile|quoted)\s+title\s*:\s*(?<titles>.+?)(?=\s*\|\s*(?:Document|Section|HeadingPath|ChunkType|Pages|Evidence)\s*:|$)"))
        {
            var titles = match.Groups["titles"].Value;
            foreach (var title in SplitSourceBackedTitleList(titles))
                yield return title;
        }

        var trimmed = CollapseWhitespace(value);
        if (!trimmed.Contains(':', StringComparison.Ordinal)
            && trimmed.Length is >= 3 and <= 90)
        {
            yield return trimmed;
        }
    }

    private static IEnumerable<string> SplitSourceBackedTitleList(string value)
    {
        foreach (var part in Regex.Split(value ?? string.Empty, @"\s*;\s*"))
        {
            var title = CollapseWhitespace(part)
                .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7');
            if (!string.IsNullOrWhiteSpace(title))
                yield return title;
        }
    }

    private static bool IsUsefulSourceBackedDisplayTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (normalized.Length is < 3 or > 90)
            return false;

        return normalized is not "document" and not "document profile" and not "recettes" and not "recipes" and not "recipe";
    }

    private static int ComputeSourceBackedDisplayTitleScore(string requestedTitle, string candidateTitle)
    {
        var requested = NormalizeLexicalLookup(requestedTitle);
        var candidate = NormalizeLexicalLookup(candidateTitle);
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
            return 0;

        if (string.Equals(candidate, requested, StringComparison.Ordinal))
            return 100;

        var requestTerms = ExtractQuerySignalTerms(requested)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestTerms.Length == 0)
            return 0;

        var matchedTerms = requestTerms.Count(term => candidate.Contains(term, StringComparison.Ordinal));
        if (matchedTerms == 0)
            return 0;

        var score = matchedTerms * 20;
        if (matchedTerms == requestTerms.Length)
            score += 30;
        if (candidate.Contains(requested, StringComparison.Ordinal) || requested.Contains(candidate, StringComparison.Ordinal))
            score += 20;

        return score;
    }

    private static bool LooksLikeParameterLookupRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:vitesse|vitesses|speed|speeds|temperature|temperatures|reglage|reglages|setting|settings|parametre|parametres|parameter|parameters|programme|program)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeStructuredItemCardRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:fiche|card|ingredients|ingredient|etape|etapes|steps|temps|time|source|sources|materiel|material|procedure|recette|recipe)\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildSourceBackedExactItemParameterAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(SourceBackedLabel(language, "Parametres visibles dans les extraits :", "Visible parameters in the excerpts:", "Parametros visibles en los extractos:", "Parametros visiveis nos excertos:", "Sichtbare Parameter in den Auszuegen:", "Parametri visibili negli estratti:"));

        foreach (var hit in hits.Take(3))
        {
            var facts = ExtractParameterFacts(GetBestRagEvidenceText(hit)).Take(8).ToArray();
            if (facts.Length == 0)
                continue;

            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(" p.");
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(string.Join("; ", facts));
        }

        if (!sb.ToString().Contains(" p.", StringComparison.Ordinal))
        {
            foreach (var hit in hits.Take(2))
            {
                var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
                sb.Append("- ");
                sb.Append(docLabel);
                sb.Append(" p.");
                sb.Append(hit.PageStart);
                sb.Append(" : ");
                sb.AppendLine(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260));
            }
        }

        AppendControlExcerpt(sb, language, hits);
        return sb.ToString().TrimEnd();
    }

    private static string BuildSourceBackedExactItemCardAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits, string header)
    {
        var cardHits = hits
            .Where(hit => ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit))
            .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
            .ThenByDescending(hit => hit.Score)
            .ToList();
        if (cardHits.Count == 0)
        {
            cardHits = hits
                .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }

        var primary = cardHits.First();
        var primaryDoc = string.IsNullOrWhiteSpace(primary.DocName) ? primary.DocPath : primary.DocName;
        var evidenceHits = SelectExactItemCardEvidenceHits(cardHits, primary);
        var evidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var ingredientEvidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemIngredientEvidenceText(requestedTitle, hit))));
        var procedureEvidence = CollapseWhitespace(string.Join(' ', evidenceHits
            .OrderByDescending(hit => ComputeExactItemProcedureEvidenceScore(requestedTitle, hit))
            .Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var portionsAndTimes = ExtractPortionsAndTimes(procedureEvidence).Take(8).ToArray();
        var ingredients = ExtractIngredientLikeFacts(ingredientEvidence).Take(10).ToArray();
        var steps = ExtractProcedureSteps(procedureEvidence).Take(6).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(SourceBackedLabel(language, "Fiche sourcee :", "Source-backed card:", "Ficha con fuente:", "Ficha com fonte:", "Belegte Karte:", "Scheda con fonte:"));
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Source principale", "Main source", "Fuente principal", "Fonte principal", "Hauptquelle", "Fonte principale"));
        sb.Append(" : ");
        sb.Append(primaryDoc);
        sb.Append(" p.");
        sb.AppendLine(primary.PageStart.ToString(CultureInfo.InvariantCulture));

        AppendFactList(sb, SourceBackedLabel(language, "Temps / portions visibles", "Visible time / portions", "Tiempo / raciones visibles", "Tempo / porcoes visiveis", "Sichtbare Zeit / Portionen", "Tempo / porzioni visibili"), portionsAndTimes);
        AppendFactList(sb, SourceBackedLabel(language, "Ingredients / elements visibles", "Visible ingredients / items", "Ingredientes / elementos visibles", "Ingredientes / elementos visiveis", "Sichtbare Zutaten / Elemente", "Ingredienti / elementi visibili"), ingredients);
        AppendFactList(sb, SourceBackedLabel(language, "Etapes visibles", "Visible steps", "Pasos visibles", "Passos visiveis", "Sichtbare Schritte", "Passaggi visibili"), steps);
        AppendExactItemControlExcerpt(sb, language, requestedTitle, cardHits);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<RagHitSummary> SelectExactItemCardEvidenceHits(IReadOnlyList<RagHitSummary> cardHits, RagHitSummary primary)
    {
        var primaryDocPath = (primary.DocPath ?? string.Empty).Replace('\\', '/');
        var samePageHits = cardHits
            .Where(hit => string.Equals((hit.DocPath ?? string.Empty).Replace('\\', '/'), primaryDocPath, StringComparison.OrdinalIgnoreCase)
                && hit.PageStart == primary.PageStart)
            .Take(4)
            .ToArray();

        return samePageHits.Length > 0
            ? samePageHits
            : cardHits.Take(1).ToArray();
    }

    private static int ComputeExactItemCardEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var evidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var ingredientEvidence = GetFocusedExactItemIngredientEvidenceText(requestedTitle, hit);
        var score = 0;
        if (ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit))
            score += 20;
        else if (RagHitContainsRequestedTitle(hit, requestedTitle))
            score -= 12;
        score += ComputeExactVisibleTitleMatchScore(requestedTitle, hit);
        score += ExtractIngredientLikeFacts(ingredientEvidence).Length * 4;
        score += ExtractProcedureSteps(evidence).Length * 3;
        score += ExtractPortionsAndTimes(evidence).Length;
        if (LooksLikeNavigationOnlyHit(hit))
            score -= 25;
        return score;
    }

    private static int ComputeExactVisibleTitleMatchScore(string requestedTitle, RagHitSummary hit)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return 0;

        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return 0;

        var escapedTitle = Regex.Escape(normalizedTitle);
        if (Regex.IsMatch(
                evidence,
                $@"(?<![\p{{L}}\p{{N}}]){escapedTitle}(?:\s*(?:$|[\.:;\u2022\u00b7])|(?=\s*(?:pour|ingredients?|ingredient|ingredients|preparation|temps|cuisson|repos)\b)|(?=(?:pour|ingredients?|ingredient|ingredients|preparation|temps|cuisson|repos)\b))",
                RegexOptions.CultureInvariant))
        {
            return 40;
        }

        return evidence.Contains(normalizedTitle, StringComparison.Ordinal) ? 4 : 0;
    }

    private static int ComputeExactItemProcedureEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var evidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var score = ExtractProcedureSteps(evidence).Length * 6
            + ExtractPortionsAndTimes(evidence).Length * 2;
        if (Regex.IsMatch(evidence, @"(?i)(?:^|\s)[1-9][\.)]\s+", RegexOptions.CultureInvariant))
            score += 18;
        score += ComputeExactVisibleTitleMatchScore(requestedTitle, hit);
        return score;
    }

    private static string GetFocusedExactItemEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var text = GetBestExactItemEvidenceText(requestedTitle, hit);
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var idx = string.IsNullOrWhiteSpace(normalizedTitle)
            ? -1
            : normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (idx < 0)
            return text;

        var start = Math.Max(0, Math.Min(idx, text.Length) - 760);
        var length = Math.Min(1800, text.Length - start);
        return TrimAfterLikelyExactItemBoundary(text.Substring(start, length));
    }

    private static string GetFocusedExactItemIngredientEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var text = GetBestExactItemEvidenceText(requestedTitle, hit);
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var idx = string.IsNullOrWhiteSpace(normalizedTitle)
            ? -1
            : normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var start = Math.Min(idx, text.Length);
            var length = Math.Min(1800, text.Length - start);
            return TrimAfterLikelyExactItemBoundary(text.Substring(start, length));
        }

        return TrimAfterLikelyExactItemBoundary(text);
    }

    private static string GetBestExactItemEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);
        var contextual = CollapseWhitespace(hit.ContextualSnippet ?? string.Empty);
        if (contextual.Length > excerpt.Length + 40 && ExactItemTextMatchesRequestOrStructure(requestedTitle, contextual))
            return StripContextualMetadataForEvidence(contextual);
        if (fullText.Length > excerpt.Length + 120 && ExactItemTextMatchesRequestOrStructure(requestedTitle, fullText))
            return fullText;

        if (excerpt.Length >= 40)
            return excerpt;
        if (!string.IsNullOrWhiteSpace(fullText))
            return fullText;
        return GetBestRagEvidenceText(hit);
    }

    private static string StripContextualMetadataForEvidence(string contextual)
    {
        var value = CollapseWhitespace(contextual);
        value = Regex.Replace(
            value,
            @"(?i)\bMatched\s+(?:profile|quoted)\s+title\s*:\s*.+?(?=\s*\|\s*(?:Document|Section|HeadingPath|ChunkType|Pages|PreviousEvidence|NextEvidence|Evidence)\s*:|$)",
            string.Empty,
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:Matched\s+(?:profile|quoted)\s+title|Document|Section|HeadingPath|ChunkType|Pages)\s*:\s*.+?(?=\s*\b(?:Matched\s+(?:profile|quoted)\s+title|Document|Section|HeadingPath|ChunkType|Pages|Context|PreviousContext|NextContext|Excerpt|PreviousEvidence|NextEvidence|Evidence)\s*:|$)",
            " ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\s*\|\s*(?:Document|Section|HeadingPath|ChunkType|Pages)\s*:[^|]+",
            " ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\s*\|\s*(?:PreviousEvidence|NextEvidence|Evidence)\s*:\s*",
            ". ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            @"(?i)\b(?:Context|PreviousContext|NextContext|Excerpt|PreviousEvidence|NextEvidence|Evidence)\s*:\s*",
            ". ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(value).Trim(' ', '.', ',', ';', ':');
    }

    private const string ExactItemStructureHeadingPattern =
        @"ingredients?|ingredient|ingr(?:e|\u00e9)dients?|ingredientes?|ingredienti|zutaten|preparation|pr(?:e|\u00e9)paration|preparacion|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|procedure|procedures?|instruction|instructions|method|methods?|methode|methodes|m(?:e|\u00e9)thode|m(?:e|\u00e9)thodes|mode\s+operatoire|technique|etape|etapes|(?:e|\u00e9)tapes?|steps?|material|equipment|materiel|mat(?:e|\u00e9)riel|ustensiles?";

    private const string ProcedureSectionHeadingPattern =
        @"preparation|pr(?:e|\u00e9)paration|preparacion|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|procedure|procedures?|instruction|instructions|method|methods?|methode|methodes|m(?:e|\u00e9)thode|m(?:e|\u00e9)thodes|mode\s+operatoire|technique|etape|etapes|(?:e|\u00e9)tapes?|steps?";

    private const string NonIngredientSectionHeadingPattern =
        @"material|equipment|tools?|materiel|mat(?:e|\u00e9)riel|ustensiles?|materiais|materiale|werkzeug|geraete|ger(?:a|\u00e4)te|" + ProcedureSectionHeadingPattern;

    private static bool ExactItemTextMatchesRequestOrStructure(string requestedTitle, string text)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedText.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length > 0 && titleTerms.All(term => normalizedText.Contains(term, StringComparison.Ordinal)))
            return true;

        if (Regex.IsMatch(normalizedText, @"\b(?:" + ExactItemStructureHeadingPattern + @")\b", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            normalizedText,
            @"\b(?:ingredients?|ingredient|preparation|procedure|procedures?|etape|etapes|steps?|zutaten|zubereitung|ingredientes?|ingredienti)\b",
            RegexOptions.CultureInvariant);
    }

    private static string TrimAfterLikelyExactItemBoundary(string text)
    {
        var value = text ?? string.Empty;
        if (value.Length < 260)
            return value;

        var boundaries = new[]
        {
            @"(?i)\b(?:pas\s+cher|bon\s+marche|facile|moyen|difficile|easy|medium|hard|einfach|mittel|schwer)\s*(?:\b(?:pas\s+cher|bon\s+marche|facile|moyen|difficile|easy|medium|hard|einfach|mittel|schwer)\s*){0,3}\d{1,4}\s*[\u2022\u00b7]",
            @"\b\d{1,4}\s*[\u2022\u00b7]\s*(?:[A-Z\u00c0-\u017f]|\p{Lu})",
            @"\b\d{1,4}\s*\|\s*[\p{L} ]{3,50}"
        };

        var cutAt = value.Length;
        foreach (var pattern in boundaries)
        {
            var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
            if (match.Success && match.Index >= 220)
                cutAt = Math.Min(cutAt, match.Index);
        }

        return cutAt < value.Length
            ? value[..cutAt].TrimEnd(' ', '.', ',', ';', ':')
            : value;
    }

    private static bool ExactItemEvidenceTextMatchesRequestedTitle(string requestedTitle, RagHitSummary hit)
    {
        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) && evidence.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return titleTerms.Length > 0 && titleTerms.All(term => evidence.Contains(term, StringComparison.Ordinal));
    }

    private static void AppendFactList(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.Append("- ");
        sb.Append(title);
        sb.Append(" : ");
        sb.AppendLine(items.Count == 0 ? "non visible dans les extraits retenus" : string.Join("; ", items));
    }

    private static void AppendControlExcerpt(StringBuilder sb, string language, IReadOnlyList<RagHitSummary> hits)
    {
        var hit = hits.FirstOrDefault();
        if (hit is null)
            return;

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Extrait de controle", "Control excerpt", "Extracto de control", "Excerto de controlo", "Kontrollauszug", "Estratto di controllo"));
        sb.Append(" : ");
        sb.Append(docLabel);
        sb.Append(" p.");
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 360)));
    }

    private static void AppendExactItemControlExcerpt(StringBuilder sb, string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var hit = hits.FirstOrDefault();
        if (hit is null)
            return;

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Extrait de controle", "Control excerpt", "Extracto de control", "Excerto de controlo", "Kontrollauszug", "Estratto di controllo"));
        sb.Append(" : ");
        sb.Append(docLabel);
        sb.Append(" p.");
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetFocusedExactItemIngredientEvidenceText(requestedTitle, hit), maxLength: 360)));
    }

    private static string SourceBackedLabel(string language, string fr, string en, string es, string pt, string de, string it)
        => NormalizeLanguageCode(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };

    private static string[] ExtractParameterFacts(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 900);
        var matches = Regex.Matches(
                readable,
                @"(?i)\b(?:programme\s+[A-Z][\p{L}\-]*|mode\s+manuel|fonction\s+[A-Z][\p{L}\-]*|vitesse\s*\d+|speed\s*\d+|\d+\s*(?:°\s*)?C|\d+\s*(?:min|minutes?|h|heures?))\b",
                RegexOptions.CultureInvariant)
            .Select(static match => CollapseWhitespace(match.Value))
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches;
    }

    private static string[] ExtractPortionsAndTimes(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 900);
        return Regex.Matches(
                readable,
                @"(?i)\b(?:pour\s+\d+\s+(?:personnes?|portions?|churros)|\d+\s+(?:personnes?|portions?|churros)|\d+\s*(?:min|minutes?|h|heures?))\b",
                RegexOptions.CultureInvariant)
            .Select(static match => CollapseWhitespace(match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ExtractIngredientLikeFacts(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1200);
        readable = Regex.Replace(readable, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        readable = Regex.Replace(readable, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        var compactIngredients = ExtractCompactIngredientSegments(readable);
        var matches = Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.\s*(?:a|à)\s*(?:[cs]\.|soupe|caf[eé])|cuill[eè]res?\s+a\s+(?:soupe|cafe)|cuill[eè]res?\s+à\s+(?:soupe|café))\s*(?:de|d['’‘`])?\s*[\p{L}'’‘`\-\s]{2,55}",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanIngredientQuantityFact(match.Value))
            .Where(static value => value.Length is >= 5 and <= 90 && !LooksLikeTruncatedIngredientFact(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        matches.AddRange(Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s+[\p{L}'â€™â€˜`\-]{3,}(?:\s+(?:de|d['â€™â€˜`]|du|des)\s*[\p{L}'â€™â€˜`\-]{3,})?",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanIngredientSegment(match.Value))
            .Where(IsLikelyIngredientSegment));

        matches.AddRange(compactIngredients);
        matches = matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (matches.Count == 0)
        {
            matches.AddRange(SplitEvidenceSegments(readable)
                .Where(static segment => Regex.IsMatch(segment, @"(?i)\b(?:ingredient|ingredients|chocolat|beurre|farine|sucre|creme|crème|oeuf|œuf|huile|sel|poivre)\b", RegexOptions.CultureInvariant))
                .Take(6));
        }

        return matches.ToArray();
    }

    private static string CleanIngredientQuantityFact(string value)
    {
        var cleaned = CollapseWhitespace((value ?? string.Empty).Trim(' ', '.', ',', ';', ':'));
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\s+\b(?:pour|for|para|per)\s+(?:la|le|les|l['’]|the|a|el|los|las|o|os|as|il|lo|gli|die|der|das)?\s*[\p{L}'’\-\s]{2,}$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':');
    }

    private static IEnumerable<string> ExtractCompactIngredientSegments(string readable)
    {
        var scoped = ExtractIngredientScope(readable);
        foreach (var segment in SplitEvidenceSegments(scoped))
        {
            var cleaned = CleanIngredientSegment(segment);
            if (IsLikelyIngredientSegment(cleaned))
                yield return cleaned;
        }
    }

    private static string ExtractIngredientScope(string readable)
    {
        var match = Regex.Match(
            readable,
            @"(?is)\b(?:ingredients?|ingr[eé]dients?|ingredientes?|ingredienti|zutaten)\b\s*[:\-]?\s*(?<body>.+?)(?:\b(?:preparation|pr[eé]paration|preparacion|prepara[cç][aã]o|preparazione|zubereitung|etapes?|[eé]tapes?|steps?)\b|$)",
            RegexOptions.CultureInvariant);
        return match.Success ? CutBeforeSectionHeading(match.Groups["body"].Value, NonIngredientSectionHeadingPattern) : readable;
    }

    private static string ExtractPreparationScope(string readable)
    {
        var proceduralMatch = Regex.Match(
            readable,
            @"(?is)\b(?:" + ProcedureSectionHeadingPattern + @")\b\s*[:\-]?\s*(?<body>.+)$",
            RegexOptions.CultureInvariant);
        if (proceduralMatch.Success)
            return proceduralMatch.Groups["body"].Value;

        var match = Regex.Match(
            readable,
            @"(?is)\b(?:preparation|pr[eé]paration|preparacion|prepara[cç][aã]o|preparazione|zubereitung|etapes?|[eé]tapes?|steps?)\b\s*[:\-]?\s*(?<body>.+)$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["body"].Value : readable;
    }

    private static string CutBeforeSectionHeading(string text, string headingPattern)
    {
        var value = text ?? string.Empty;
        var match = Regex.Match(
            value,
            @"(?is)\b(?:" + headingPattern + @")\b\s*[:\-]?",
            RegexOptions.CultureInvariant);
        return match.Success ? value[..match.Index] : value;
    }

    private static string CleanIngredientSegment(string segment)
    {
        var cleaned = CollapseWhitespace(segment)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:" + NonIngredientSectionHeadingPattern + @")\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:preparation|pr[eé]paration|preparacion|prepara[cç][aã]o|preparazione|zubereitung|etapes?|[eé]tapes?|steps?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)^\b(?:ingredients?|ingr[eé]dients?|ingredientes?|ingredienti|zutaten)\b\s*[:\-]?\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
    }

    private static bool IsLikelyIngredientSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length is < 3 or > 100)
            return false;
        if (LooksLikeTruncatedIngredientFact(segment))
            return false;

        var normalized = NormalizeLexicalLookup(segment);
        if (Regex.IsMatch(normalized, @"\b(?:personnes?|portions?|preparation|etape|etapes|temps|time|duration|duree|servez|serve|faites|faire|ajoutez|versez|melangez|cuire|chauffez|enfournez|laissez|retirez)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:min|minutes?|h|heures?|hour|hours|c|celsius)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:dans|lancez|mettez|remplacez|ajoutez|puis|quand|salez|poivrez|faites|versez|melangez|mixer|mixez)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:saladier|saladiers|couteau|couteaux|planche|planches|ustensile|ustensiles|bol|bols|ramequin|ramequins|moule|moules|robot|robots|four|fours|poele|poeles|poêle|poêles|casserole|casseroles)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+cuilleres?$", RegexOptions.CultureInvariant))
            return false;

        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.|cuillere|cuilleres|sachet|sachets|pincee|pincees|jaune|jaunes|oeuf|oeufs)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+\p{L}{3,}(?:\s+\p{L}{2,}){0,5}$", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(normalized, @"^(?:sel|poivre|huile|beurre|farine|sucre|cassonade|vanille|chocolat|creme|lait|eau|levure|maizena|oeuf|oeufs)(?:\s+\p{L}+){0,5}$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeTruncatedIngredientFact(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        return Regex.IsMatch(normalized, @"\b(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeIngredientListSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:ingredients?|ingredient|ingr[eé]dients?|ingredientes?|ingredienti|zutaten)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^pour\s+\d+\s+(?:personnes?|portions?|porciones|porcoes|persone|personen)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|c\.|cuillere|cuilleres|sachet|sachets|pincee|pincees|jaune|jaunes|oeuf|oeufs)\b", RegexOptions.CultureInvariant);
    }

    private static string[] ExtractProcedureSteps(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1400);
        var proceduralScope = ExtractPreparationScope(readable);
        var numbered = Regex.Matches(
                proceduralScope,
                @"(?i)(?:^|\s)(?:[1-9][\.)]\s*)(?<step>.{18,190}?)(?=\s*[1-9][\.)]\s*|$)",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanProcedureStep(match.Groups["step"].Value))
            .Where(static value => value.Length is >= 12 and <= 220 && !LooksLikeIngredientListSegment(value) && !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        if (numbered.Length > 0)
            return numbered;

        var scopedCandidates = ExtractProcedureCandidateSegments(proceduralScope).ToArray();
        var allCandidates = ExtractProcedureCandidateSegments(readable).ToArray();
        if (allCandidates.Length > scopedCandidates.Length)
            return allCandidates.Take(5).ToArray();

        return scopedCandidates.Take(5).ToArray();
    }

    private static IEnumerable<string> ExtractProcedureCandidateSegments(string text)
    {
        return SplitEvidenceSegments(text)
            .Select(static segment => CleanProcedureStep(segment))
            .Where(static segment => !LooksLikeIngredientListSegment(segment))
            .Where(static segment => !LooksLikeTruncatedProcedureSegment(segment))
            .Where(static segment => LooksLikeProcedureCandidateSegment(segment));
    }

    private static bool LooksLikeProcedureCandidateSegment(string segment)
        => Regex.IsMatch(
            segment,
            @"(?i)\b(?:ajoutez|ajouter|versez|verser|mettez|mettre|faites|faire|m(?:e|\u00e9)langez|m(?:e|\u00e9)langer|hachez|hacher|coupez|couper|(?:e|\u00e9)pluchez|(?:e|\u00e9)plucher|(?:e|\u00e9)gouttez|(?:e|\u00e9)goutter|assaisonnez|assaisonner|cuire|chauffez|chauffer|servez|servir|mixez|mixer|retirez|retirer|laissez|laisser|tamisez|tamiser|fouettez|fouetter|incorporez|incorporer|enfournez|enfourner|battez|battre|remuez|remuer|portez|porter|pr(?:e|\u00e9)chauffez|pr(?:e|\u00e9)chauffer|d(?:e|\u00e9)posez|d(?:e|\u00e9)poser|recouvrez|recouvrir|add|mix|chop|cut|peel|drain|season|cook|heat|serve|remove|let|whisk|stir|fold|bake|preheat|place|cover)\b",
            RegexOptions.CultureInvariant);

    private static string CleanProcedureStep(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty).Trim(' ', '.', ';', ':');
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\bFaites\s+refroidi\s+(?=(?:Ajoutez|Versez|Mettez|Tamisez|Creusez|Servez|Retirez|Laissez|Faites)\b)",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\bFaites\s+refroidi\b",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?<=[\p{Ll}])\s+(?=(?:Ajoutez|Versez|Mettez|Tamisez|Creusez|Servez|Retirez|Laissez|Faites|Pr[e\u00e9]chauffez|Enfournez|Fouettez|Incorporez|M[e\u00e9]langez|Coupez|Hachez|Epluchez|[Ee]\u0301pluchez|D[e\u00e9]posez)\b)",
            "; ",
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?<=\p{L})\.\s*[1-9]\.?\s*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ';', ':');
    }

    private static string CleanReadableProcedureArtifacts(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\bFaites\s+refroidi\s+(?=(?:Ajoutez|Versez|Mettez|Tamisez|Creusez|Servez|Retirez|Laissez|Faites)\b)",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\bFaites\s+refroidi\b",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?<=[\p{Ll}])\s+(?=(?:Ajoutez|Versez|Mettez|Tamisez|Creusez|Servez|Retirez|Laissez|Faites|Pr[e\u00e9]chauffez|Enfournez|Fouettez|Incorporez|M[e\u00e9]langez|Coupez|Hachez|Epluchez|[Ee]\u0301pluchez|D[e\u00e9]posez)\b)",
            "; ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ';', ':');
    }

    private static bool LooksLikeTruncatedProcedureSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return Regex.IsMatch(normalized, @"\b(?:de|du|des|d|a|avec|sans|et|puis|jusqu|jusque|pendant|pour)$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\bfaites\s+refroidi\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l)\s+(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> SplitEvidenceSegments(string text)
    {
        foreach (var segment in Regex.Split(text, @"(?:\s*[\u2022\u00b7]\s*|(?<=[\.;!?])\s+)", RegexOptions.CultureInvariant))
        {
            var cleaned = CollapseWhitespace(segment).Trim(' ', '.', ',', ';', ':');
            if (cleaned.Length is >= 8 and <= 220)
                yield return cleaned;
        }
    }

    private static string FormatReadableEvidenceExcerpt(string? excerpt, int maxLength)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\d)(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?<=\p{L})(?=\d+\s*min\b)", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text.Length <= maxLength
            ? text
            : text[..maxLength].TrimEnd() + "...";
    }

    private static string GetBestRagEvidenceText(RagHitSummary hit)
    {
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);

        if (excerpt.Length >= 40)
            return excerpt;
        if (fullText.Length > excerpt.Length + 80)
            return fullText;
        if (!string.IsNullOrWhiteSpace(excerpt))
            return excerpt;
        return fullText;
    }

    private static string BuildSourceBackedPlanningOrExtractiveAnswer(ToolResults toolResults, string query, string language, int minPlanningItems = 1)
    {
        var planningAnswer = BuildSourceBackedPlanningAnswer(toolResults, language, minItems: minPlanningItems, query: query);
        return !string.IsNullOrWhiteSpace(planningAnswer)
            ? planningAnswer
            : BuildSourceBackedExtractiveAnswer(toolResults, query, language);
    }

    private static string GetPlanExtractionText(RagHitSummary hit)
        => string.IsNullOrWhiteSpace(hit.FullText) ? hit.Excerpt : hit.FullText!;

    private static string ExtractPlanItemTitleV2(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"^\d+", string.Empty, RegexOptions.CultureInvariant).Trim();
        var patterns = new[]
        {
            @"(?i)\bMenu\s*(?<title>.+?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*(?:eur|euros?|chf)|Ingr[e\u00e9]dients|Ingredients)",
            @"(?i)(?:^|[\s:;])(?<title>\p{Lu}[\p{Lu}0-9 '&/,\-]{5,90}?)(?:\d+\s*min|Pour\s+\d|Ingr[e\u00e9]dients|Ingredients|PR[\u00c9E]PARATION|PREPARATION|Temps\s+total)",
            @"(?i)\b(?<title>\p{Lu}[\p{L}'\u2019 \-/]{5,80})\s+(?:Pour\s+\d+\s+personnes|Ingr[e\u00e9]dients|Ingredients|Pr[\u00e9e]paration|Preparation)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var title = HumanizePlanItemTitleV2(match.Groups["title"].Value);
            if (!LooksLikePlanPageHeading(text, title) && !LooksLikePlanItemNoise(title))
                return title;
        }

        return string.Empty;
    }

    private static bool LooksLikePlanPageHeading(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
            return false;

        var escapedTitle = Regex.Escape(CollapseWhitespace(title));
        return Regex.IsMatch(
            CollapseWhitespace(text),
            $@"(?:^|\s)(?:\d+\s*)?\|\s*{escapedTitle}\s*(?:Pour|For|Para|Per)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string HumanizePlanItemTitleV2(string value)
    {
        var title = CollapseWhitespace(value);
        title = Regex.Replace(title, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ", RegexOptions.CultureInvariant);
        title = title
            .Replace("ESCALOPEDE", "ESCALOPE DE", StringComparison.OrdinalIgnoreCase)
            .Replace("CARNEHEALTHY", "CARNE HEALTHY", StringComparison.OrdinalIgnoreCase)
            .Replace("EQUILIBRESELON", "EQUILIBRE SELON", StringComparison.OrdinalIgnoreCase)
            .Replace("VEGANAU", "VEGAN AU", StringComparison.OrdinalIgnoreCase)
            .Replace("GÂTEAUCHOCOLAT", "GÂTEAU CHOCOLAT", StringComparison.OrdinalIgnoreCase)
            .Replace("GATEAUCHOCOLAT", "GATEAU CHOCOLAT", StringComparison.OrdinalIgnoreCase);
        title = Regex.Replace(title, @"(?:E|[\u00c9\u00c8])QUILIBR(?:E|[\u00c9\u00c8])SELON", "EQUILIBRE SELON", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', ':');
        return title;
    }

    private static bool LooksLikePlanItemNoise(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (normalized.Length < 4)
            return true;
        if (normalized.Length > 90)
            return true;

        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && char.IsLower(trimmed[0]))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:semaine|liste|epicerie|courses|source|sources|page|pages|sommaire|index|ingredients|preparation|organisation|planning|calendrier|modele|outil|conseils?|consiste|prendre|heures?|temps|documents?|disponibles?|ustensiles?|materiel|service|table|utilisez|utiliser|choisissez|installation|lieux|occupez|marmaille|lors|chauffer|doucement|melangeur|reduire|puree|repaset|budget|econom|sel|poivre)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:de|du|des|d|a|au|aux|of|the|for|with|con|por|para|per|di|da|von|zu)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:le|la|les|un|une|der|die|das|il|lo|gli|el|los|las)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var meaningfulTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 4)
            .ToArray();
        if (meaningfulTerms.Length == 0)
            return true;

        var letterCount = normalized.Count(char.IsLetter);
        return letterCount < Math.Max(4, normalized.Length / 3);
    }

    internal static string BuildSourceBackedExtractiveHeader(string language, bool noExplicitPairing)
    {
        language = NormalizeLanguageCode(language);
        if (noExplicitPairing)
        {
            return language switch
            {
                "en" => "I did not find a passage that explicitly connects every part of the request. Here are the source-backed leads actually present in the available documents, without adding facts outside the sources:",
                "es" => "No he encontrado un pasaje que conecte explicitamente todas las partes de la solicitud. Estas son las pistas con fuente que si aparecen en los documentos disponibles, sin anadir hechos fuera de las fuentes:",
                "pt" => "Nao encontrei uma passagem que ligue explicitamente todas as partes do pedido. Estas sao as pistas com fonte que aparecem nos documentos disponiveis, sem acrescentar factos fora das fontes:",
                "de" => "Ich habe keine Stelle gefunden, die alle Teile der Anfrage ausdruecklich verbindet. Hier sind die belegten Hinweise aus den verfuegbaren Dokumenten, ohne Fakten ausserhalb der Quellen hinzuzufuegen:",
                "it" => "Non ho trovato un passaggio che colleghi esplicitamente tutte le parti della richiesta. Ecco le indicazioni documentate presenti nei documenti disponibili, senza aggiungere fatti fuori dalle fonti:",
                _ => "Je n'ai pas trouve de passage qui relie explicitement tous les elements de la demande. Voici les pistes reellement presentes dans les documents disponibles, sans ajout de faits hors source :"
            };
        }

        return language switch
        {
            "en" => "Here are the leads found in the available documents, without adding facts, quantities, or steps outside the sources:",
            "es" => "Estas son las pistas encontradas en los documentos disponibles, sin anadir hechos, cantidades ni pasos fuera de las fuentes:",
            "pt" => "Estas sao as pistas encontradas nos documentos disponiveis, sem acrescentar factos, quantidades nem passos fora das fontes:",
            "de" => "Hier sind die Hinweise aus den verfuegbaren Dokumenten, ohne Fakten, Mengen oder Schritte ausserhalb der Quellen hinzuzufuegen:",
            "it" => "Ecco le indicazioni trovate nei documenti disponibili, senza aggiungere fatti, quantita o passaggi non presenti nelle fonti:",
            _ => "Voici les pistes trouvees dans les documents disponibles, sans ajout de faits, quantites ni etapes hors source :"
        };
    }

    private static int ComputeRagHitLexicalRelevance(string query, string? excerpt)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var normalizedExcerpt = NormalizeLexicalLookup(excerpt);
        var score = 0;

        foreach (var term in ExtractQuerySignalTerms(normalizedQuery))
        {
            if (normalizedExcerpt.Contains(term, StringComparison.Ordinal))
                score += term.Length >= 7 ? 4 : 2;
        }

        return score;
    }

    private static bool ShouldWarnNoExplicitPairing(string query, IReadOnlyList<RagHitSummary> hits)
    {
        if (hits.Count == 0)
            return false;

        var terms = ExtractQuerySignalTerms(NormalizeLexicalLookup(query))
            .Where(static term => term.Length >= 5)
            .Take(5)
            .ToArray();
        if (terms.Length < 2)
            return false;

        var evidence = NormalizeLexicalLookup(string.Join(' ', hits.Take(3).Select(GetRagHitLookupText)));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var covered = terms.Count(term => evidence.Contains(term, StringComparison.Ordinal));
        return covered < Math.Min(2, terms.Length);
    }

    private static string GetRagHitLookupText(RagHitSummary hit)
        => $"{hit.Excerpt} {hit.FullText} {hit.ContextualSnippet}";

    private static string GetRagHitPrimaryEvidenceText(RagHitSummary hit)
        => $"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText}";

    private static string GetRagHitPrimaryContentText(RagHitSummary hit)
        => $"{hit.Excerpt} {hit.FullText}";

    private static IEnumerable<string> ExtractQuerySignalTerms(string normalizedQuery)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "comment", "dans", "faire", "facile", "idee",
            "menu", "peux", "pour", "propose", "proposes", "quoi", "semaine", "vais", "veux", "voudrais",
            "about", "find", "help", "make", "plan", "prepare", "recommend", "suggest", "what", "with",
            "can", "could", "give", "ayuda", "ayudar", "ayudame", "puede", "puedes", "podrias", "propone",
            "recomienda", "ajuda", "ajudar", "pode", "podes", "recomenda", "kannst", "konntest", "helfen",
            "vorschlag", "empfiehl", "aiuta", "aiutami", "puoi", "consiglia", "planejamento",
            "planificacion", "organise", "organize"
        };

        return Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]{4,}")
            .Select(m => m.Value)
            .Where(term => !stopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8);
    }

    private static string NormalizeLexicalLookup(string? value)
    {
        var text = CollapseWhitespace(value ?? string.Empty)
            .ToLowerInvariant()
            .Replace("\u0153", "oe", StringComparison.Ordinal)
            .Replace("\u00e6", "ae", StringComparison.Ordinal)
            .Replace("\u00df", "ss", StringComparison.Ordinal);

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeLooseLookup(string? value)
        => NormalizeLexicalLookup(value);

    private static string BuildRagEvidenceFallbackAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        if ((LooksLikeSourceBackedActionRequest(query) || LooksLikeComparativeDocumentaryRequest(query))
            && EnumerateRagHitSummaries(toolResults).Any())
        {
            return BuildSourceBackedExtractiveAnswer(toolResults, query, language);
        }

        var hits = FilterHitsToDominantTopLevel(EnumerateRagHitSummaries(toolResults).ToList(), query)
            .Take(3)
            .ToList();
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

    private sealed record RagHitSummary(
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        string Excerpt,
        string? SectionTitle = null,
        string? HeadingPath = null,
        string? Retriever = null,
        double Score = 0.0,
        bool ExactMatchHit = false,
        string? FullText = null,
        string? ContextualSnippet = null,
        string? EmbeddingBasis = null);

    private static RagHitSummary BuildRagHitSummary(JsonElement h)
    {
        var docPath = TryGetString(h, "docPath") ?? string.Empty;
        var docName = TryGetString(h, "docName") ?? Path.GetFileName(docPath);
        var pageStart = TryGetInt(h, "pageStart") ?? 1;
        var pageEnd = TryGetInt(h, "pageEnd") ?? pageStart;
        var excerpt = TryGetString(h, "excerpt") ?? TryGetString(h, "text") ?? string.Empty;
        var fullText = TryGetString(h, "fullText");
        var contextualSnippet = TryGetString(h, "contextualSnippet") ?? TryGetString(h, "ContextualSnippet");
        var sectionTitle = TryGetString(h, "sectionTitle") ?? TryGetNestedString(h, "context", "sectionTitle");
        var headingPath = TryGetString(h, "headingPath") ?? TryGetNestedString(h, "context", "headingPath");
        var retriever = TryGetString(h, "retriever");
        var embeddingBasis = TryGetString(h, "embeddingBasis");
        var score = TryGetDouble(h, "score") ?? 0.0;
        var exactMatchHit = TryGetBool(h, "exactMatchHit") ?? false;

        return new RagHitSummary(
            docPath,
            docName,
            pageStart,
            pageEnd,
            excerpt,
            sectionTitle,
            headingPath,
            retriever,
            score,
            exactMatchHit,
            fullText,
            contextualSnippet,
            embeddingBasis);
    }

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

                yield return BuildRagHitSummary(h);
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
                    TryGetString(it, "excerpt")
                    ?? TryGetString(it, "snippet")
                    ?? TryGetString(it, "Snippet")
                    ?? TryGetString(it, "text")
                    ?? TryGetString(it, "Text")
                    ?? "",
                    RagWriterMaxExcerptChars);

                var fullText = TruncateForPrompt(
                    TryGetString(it, "text")
                    ?? TryGetString(it, "Text")
                    ?? text,
                    RagWriterMaxFullTextChars);
                var contextualSnippet = CompactContextualSnippetForPrompt(
                    TryGetString(it, "contextualSnippet")
                    ?? TryGetString(it, "ContextualSnippet"));

                var sectionTitle = TryGetString(it, "sectionTitle")
                    ?? TryGetString(it, "SectionTitle")
                    ?? TryGetNestedString(it, "context", "sectionTitle")
                    ?? TryGetNestedString(it, "Context", "SectionTitle");
                var headingPath = TryGetString(it, "headingPath")
                    ?? TryGetString(it, "HeadingPath")
                    ?? TryGetNestedString(it, "context", "headingPath")
                    ?? TryGetNestedString(it, "Context", "HeadingPath");
                var retriever = TryGetString(it, "retriever") ?? TryGetString(it, "Retriever");
                var exactMatchHit = TryGetBool(it, "exactMatchHit") ?? TryGetBool(it, "ExactMatchHit") ?? false;

                list.Add(new
                {
                    docPath,
                    docName,
                    pageStart = ps,
                    pageEnd = pe,
                    excerpt = text,
                    fullText,
                    score,
                    sectionTitle,
                    headingPath,
                    retriever,
                    exactMatchHit,
                    contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
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

    private static string CompactContextualSnippetForPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var raw = value.Trim();
        var lines = Regex.Split(raw, @"\r?\n")
            .Select(CollapseWhitespace)
            .Where(static line => line.Length > 0)
            .ToList();

        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Matched profile title:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Document:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Section:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HeadingPath:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("ChunkType:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(line);
            }
        }

        var excerpt = ExtractContextualSnippetBlock(raw, "Excerpt:")
            ?? ExtractContextualSnippetBlock(raw, "Context:");
        if (!string.IsNullOrWhiteSpace(excerpt))
            kept.Add("Evidence: " + TruncateForPrompt(excerpt, RagWriterContextualEvidenceChars));

        var hasMatchedProfileTitle = raw.Contains("Matched profile title:", StringComparison.OrdinalIgnoreCase);
        var previous = ExtractContextualSnippetBlock(raw, "PreviousContext:");
        if (hasMatchedProfileTitle
            && !string.IsNullOrWhiteSpace(previous)
            && ShouldIncludePreviousContextualEvidence(excerpt, previous))
        {
            kept.Add("PreviousEvidence: " + TruncateForPrompt(previous, RagWriterContextualEvidenceChars));
        }

        var next = ExtractContextualSnippetBlock(raw, "NextContext:");
        if (!string.IsNullOrWhiteSpace(next) && ShouldIncludeNextContextualEvidence(excerpt, next))
        {
            kept.Add("NextEvidence: " + TruncateForPrompt(next, RagWriterContextualEvidenceChars));
        }

        if (kept.Count == 0)
            return TruncateForPrompt(raw, RagWriterContextualRawChars);

        return TruncateForPrompt(string.Join(" | ", kept.Distinct(StringComparer.OrdinalIgnoreCase)), RagWriterContextualTotalChars);
    }

    private static bool ShouldIncludePreviousContextualEvidence(string? currentEvidence, string? previousEvidence)
    {
        if (string.IsNullOrWhiteSpace(previousEvidence))
            return false;

        var current = CollapseWhitespace(currentEvidence ?? string.Empty);
        var previous = CollapseWhitespace(previousEvidence);
        if (previous.Length < 40)
            return false;

        if (ContainsStructuredItemHeading(current))
            return false;

        var previousQuantitySignals = CountQuantityLikeSignals(previous);
        return previousQuantitySignals >= 3;
    }

    private static bool ShouldIncludeNextContextualEvidence(string? currentEvidence, string? nextEvidence)
    {
        if (string.IsNullOrWhiteSpace(nextEvidence))
            return false;

        var current = CollapseWhitespace(currentEvidence ?? string.Empty);
        var next = CollapseWhitespace(nextEvidence);
        if (next.Length < 40)
            return false;

        if (ContainsStructuredItemHeading(next) && !ContainsStructuredItemHeading(current))
            return true;

        if (Regex.IsMatch(next, @"(?i)\b(?:ingredients?|ingr[e\u00e9]dients?|zutaten|ingredientes?|ingredienti)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(current, @"(?i)\b(?:ingredients?|ingr[e\u00e9]dients?|zutaten|ingredientes?|ingredienti)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsStructuredItemHeading(string value)
        => Regex.IsMatch(
            NormalizeLexicalLookup(value),
            @"\b(?:ingredients?|ingredientes?|ingredienti|zutaten|preparation|preparacion|preparazione|zubereitung|materiel|material|equipment|technique|procedure|method|methode)\b",
            RegexOptions.CultureInvariant);

    private static int CountQuantityLikeSignals(string value)
    {
        var normalized = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;
        normalized = Regex.Replace(normalized, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);

        return Regex.Matches(
                normalized,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|h|min|minutes?|°?\s*c|c\.|cuill(?:e|è)res?|feuilles?|jaunes?|oeufs?|œufs?|sachets?|pincees?|pincées?|\p{L}{4,})\b",
                RegexOptions.CultureInvariant)
            .Count;
    }

    private static string? ExtractContextualSnippetBlock(string raw, string marker)
    {
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var start = idx + marker.Length;
        var rest = raw[start..];
        var next = Regex.Match(rest, @"\r?\n\r?\n(?:PreviousContext|NextContext|Excerpt|Context):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (next.Success && next.Index > 0)
            rest = rest[..next.Index];

        return CollapseWhitespace(rest);
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static string? TryGetNestedString(JsonElement obj, string parent, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return TryGetString(nested, prop);
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

    private static bool? TryGetBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var parsed)) return parsed;
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
