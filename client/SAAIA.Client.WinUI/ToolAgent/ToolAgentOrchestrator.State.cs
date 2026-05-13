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
            ["rag"] = new Dictionary<string, object?>
            {
                ["queries"] = _mem.LastRagQueries?.Take(8).ToArray() ?? Array.Empty<string>(),
                ["hitLabels"] = _mem.LastRagHitLabels?.Take(30).ToArray() ?? Array.Empty<string>(),
                ["degradedRetrievers"] = _mem.LastRagDegradedRetrievers?.Take(16).ToArray() ?? Array.Empty<string>()
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
                    ["lastRagQueriesCount"] = _mem.LastRagQueries?.Count ?? 0,
                    ["lastRagHitLabelsCount"] = _mem.LastRagHitLabels?.Count ?? 0,
                    ["lastRagDegradedRetrieversCount"] = _mem.LastRagDegradedRetrievers?.Count ?? 0,
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
                ["lastRagQueriesCount"] = _mem.LastRagQueries?.Count ?? 0,
                ["lastRagHitLabelsCount"] = _mem.LastRagHitLabels?.Count ?? 0,
                ["lastRagDegradedRetrieversCount"] = _mem.LastRagDegradedRetrievers?.Count ?? 0,
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

                    var source = TryBuildSourceRefFromJsonElement(s);
                    if (source is not null)
                        list.Add(source);
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

            return TryBuildSourceRefFromJsonElement(src);
        }
        catch
        {
            return null;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceRefFromJsonElement(JsonElement src)
    {
        if (src.ValueKind != JsonValueKind.Object)
            return null;

        var docPath = TryGetString(src, "docPath") ?? TryGetString(src, "doc_path") ?? TryGetString(src, "path") ?? "";
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var ps = TryGetInt(src, "pageStart") ?? TryGetInt(src, "page_start") ?? TryGetInt(src, "page") ?? 1;
        var pe = TryGetInt(src, "pageEnd") ?? TryGetInt(src, "page_end") ?? ps;
        ps = Math.Max(1, ps);
        pe = Math.Max(ps, pe);

        var docName = NullIfWhiteSpace(TryGetString(src, "docName") ?? TryGetString(src, "doc_name") ?? TryGetString(src, "DocName"));
        var label = TryGetString(src, "label")
            ?? docName
            ?? Path.GetFileName(docPath);
        if (string.IsNullOrWhiteSpace(label))
            label = docPath;

        var quality = ExtractRagHitExtractionQualitySignals(src);
        var qualityElement = TryGetObject(src, "extractionQuality") ?? TryGetObject(src, "extraction_quality") ?? TryGetObject(src, "ExtractionQuality");
        var contentSignalsElement =
            TryGetObject(src, "contentSignals")
            ?? TryGetObject(src, "content_signals")
            ?? TryGetObject(src, "ContentSignals")
            ?? TryGetObject(src, "context")
            ?? TryGetObject(src, "Context");
        var diagnosticSummary = TryBuildSourceExtractionDiagnosticRef(src, qualityElement);
        var extractionSource = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "extractionSource") ?? TryGetString(qualityElement.Value, "extraction_source") ?? TryGetString(qualityElement.Value, "ExtractionSource")
            : TryGetString(src, "extractionSource") ?? TryGetString(src, "extraction_source") ?? TryGetString(src, "ExtractionSource");
        var documentQualityStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "documentQualityStatus") ?? TryGetString(qualityElement.Value, "document_quality_status") ?? TryGetString(qualityElement.Value, "DocumentQualityStatus")
            : TryGetString(src, "documentQualityStatus") ?? TryGetString(src, "document_quality_status") ?? TryGetString(src, "DocumentQualityStatus");
        var pageQualityStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "pageQualityStatus") ?? TryGetString(qualityElement.Value, "page_quality_status") ?? TryGetString(qualityElement.Value, "PageQualityStatus")
            : TryGetString(src, "pageQualityStatus") ?? TryGetString(src, "page_quality_status") ?? TryGetString(src, "PageQualityStatus");
        var textStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "textStatus") ?? TryGetString(qualityElement.Value, "text_status") ?? TryGetString(qualityElement.Value, "TextStatus")
            : TryGetString(src, "textStatus") ?? TryGetString(src, "text_status") ?? TryGetString(src, "TextStatus");
        var ocrAttempted = qualityElement.HasValue
            ? TryGetBool(qualityElement.Value, "ocrAttempted") ?? TryGetBool(qualityElement.Value, "ocr_attempted") ?? TryGetBool(qualityElement.Value, "OcrAttempted") ?? TryGetBool(qualityElement.Value, "OCRAttempted") ?? false
            : TryGetBool(src, "ocrAttempted") ?? TryGetBool(src, "ocr_attempted") ?? TryGetBool(src, "OcrAttempted") ?? TryGetBool(src, "OCRAttempted") ?? false;
        var ocrRecommended = qualityElement.HasValue
            ? TryGetBool(qualityElement.Value, "ocrRecommended") ?? TryGetBool(qualityElement.Value, "ocr_recommended") ?? TryGetBool(qualityElement.Value, "OcrRecommended") ?? false
            : TryGetBool(src, "ocrRecommended") ?? TryGetBool(src, "ocr_recommended") ?? TryGetBool(src, "OcrRecommended") ?? false;
        var signals = (quality.Signals ?? Array.Empty<string>())
            .Concat(qualityElement.HasValue ? ExtractCompactSignals(qualityElement.Value, "signals") : Array.Empty<string>())
            .Concat(qualityElement.HasValue ? ExtractCompactSignals(qualityElement.Value, "Signals") : Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var cards = ExtractRagHitMatchedContentCards(src)?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new ToolMemory.SourceContentCardRef
            {
                Title = card.Title,
                ContentCardId = NullIfWhiteSpace(card.ContentCardId),
                PageStart = card.PageStart,
                PageEnd = card.PageEnd,
                Kind = NullIfWhiteSpace(card.Kind),
                Signals = card.Signals?
                    .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                    .Select(static signal => signal.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList() ?? new List<string>(),
                Evidence = GetSourceContentCardEvidenceElement(card)
            })
            .Take(5)
            .ToList() ?? new List<ToolMemory.SourceContentCardRef>();
        var selectionHints = TryGetObject(src, "selectionHints") ?? TryGetObject(src, "selection_hints") ?? TryGetObject(src, "SelectionHints");
        var contentRole = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "contentRole") ?? TryGetString(contentSignalsElement.Value, "content_role") ?? TryGetString(contentSignalsElement.Value, "ContentRole")
            : null;
        contentRole ??= TryGetString(src, "contentRole") ?? TryGetString(src, "content_role") ?? TryGetString(src, "ContentRole");
        var navigationReason = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "navigationReason") ?? TryGetString(contentSignalsElement.Value, "navigation_reason") ?? TryGetString(contentSignalsElement.Value, "NavigationReason")
            : null;
        navigationReason ??= TryGetString(src, "navigationReason") ?? TryGetString(src, "navigation_reason") ?? TryGetString(src, "NavigationReason");
        var retrievalNavigationScore = contentSignalsElement.HasValue
            ? TryGetDouble(contentSignalsElement.Value, "retrievalNavigationScore") ?? TryGetDouble(contentSignalsElement.Value, "retrieval_navigation_score") ?? TryGetDouble(contentSignalsElement.Value, "RetrievalNavigationScore") ?? TryGetDouble(contentSignalsElement.Value, "navigationScore") ?? TryGetDouble(contentSignalsElement.Value, "navigation_score") ?? TryGetDouble(contentSignalsElement.Value, "NavigationScore")
            : null;
        retrievalNavigationScore ??= TryGetDouble(src, "retrievalNavigationScore") ?? TryGetDouble(src, "retrieval_navigation_score") ?? TryGetDouble(src, "RetrievalNavigationScore") ?? TryGetDouble(src, "navigationScore") ?? TryGetDouble(src, "navigation_score") ?? TryGetDouble(src, "NavigationScore");
        var contentDensityScore = contentSignalsElement.HasValue
            ? TryGetDouble(contentSignalsElement.Value, "contentDensityScore") ?? TryGetDouble(contentSignalsElement.Value, "content_density_score") ?? TryGetDouble(contentSignalsElement.Value, "ContentDensityScore")
            : null;
        contentDensityScore ??= TryGetDouble(src, "contentDensityScore") ?? TryGetDouble(src, "content_density_score") ?? TryGetDouble(src, "ContentDensityScore");

        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(TryGetString(src, "docId") ?? TryGetString(src, "doc_id") ?? TryGetString(src, "DocId")),
            DocPath = docPath.Replace('\\', '/'),
            DocName = docName,
            PageStart = ps,
            PageEnd = pe,
            Label = label,
            SourceHash = NullIfWhiteSpace(TryGetString(src, "sourceHash") ?? TryGetString(src, "source_hash") ?? TryGetString(src, "SourceHash")),
            DocLanguage = NullIfWhiteSpace(TryGetDocumentLanguage(src)),
            ProfileLanguage = NullIfWhiteSpace(TryGetString(src, "profileLanguage") ?? TryGetString(src, "profile_language") ?? TryGetString(src, "ProfileLanguage")),
            Category = NullIfWhiteSpace(TryGetString(src, "category") ?? TryGetString(src, "Category")),
            CategoryRef = NullIfWhiteSpace(TryGetString(src, "categoryRef") ?? TryGetString(src, "category_ref") ?? TryGetString(src, "CategoryRef")),
            CategoryPath = NullIfWhiteSpace(TryGetString(src, "categoryPath") ?? TryGetString(src, "category_path") ?? TryGetString(src, "CategoryPath")),
            ChunkId = NullIfWhiteSpace(TryGetString(src, "chunkId") ?? TryGetString(src, "chunk_id") ?? TryGetString(src, "ChunkId")),
            ExtractionSource = NullIfWhiteSpace(extractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(documentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(pageQualityStatus),
            TextStatus = NullIfWhiteSpace(textStatus),
            QualityStatus = NullIfWhiteSpace(quality.QualityStatus ?? pageQualityStatus ?? documentQualityStatus),
            ExtractionConfidence = quality.ExtractionConfidence,
            DocumentExtractionConfidence = quality.DocumentExtractionConfidence,
            PageExtractionConfidence = quality.PageExtractionConfidence,
            ManualReviewRecommended = quality.ManualReviewRecommended,
            DocumentManualReviewRecommended = quality.DocumentManualReviewRecommended,
            PageManualReviewRecommended = quality.PageManualReviewRecommended,
            OcrAttempted = ocrAttempted,
            OcrApplied = quality.OcrApplied,
            OcrRecommended = ocrRecommended,
            ExtractionDiagnosticSummary = diagnosticSummary,
            QualitySignals = signals,
            MatchedContentCards = cards,
            SelectionHintEvidenceRole = selectionHints.HasValue
                ? NullIfWhiteSpace(TryGetString(selectionHints.Value, "evidenceRole") ?? TryGetString(selectionHints.Value, "evidence_role") ?? TryGetString(selectionHints.Value, "EvidenceRole"))
                : null,
            SelectionHintActionabilityScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "actionabilityScore") ?? TryGetInt(selectionHints.Value, "actionability_score") ?? TryGetInt(selectionHints.Value, "ActionabilityScore")
                : null,
            SelectionHintSupportScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "supportScore") ?? TryGetInt(selectionHints.Value, "support_score") ?? TryGetInt(selectionHints.Value, "SupportScore")
                : null,
            SelectionHintFragmentScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "fragmentScore") ?? TryGetInt(selectionHints.Value, "fragment_score") ?? TryGetInt(selectionHints.Value, "FragmentScore")
                : null,
            SelectionHintNavigationScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "navigationScore") ?? TryGetInt(selectionHints.Value, "navigation_score") ?? TryGetInt(selectionHints.Value, "NavigationScore")
                : null,
            SelectionHintQualityPenalty = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "qualityPenalty") ?? TryGetInt(selectionHints.Value, "quality_penalty") ?? TryGetInt(selectionHints.Value, "QualityPenalty")
                : null,
            ContentRole = NullIfWhiteSpace(contentRole),
            NavigationReason = NullIfWhiteSpace(navigationReason),
            RetrievalNavigationScore = retrievalNavigationScore,
            ContentDensityScore = contentDensityScore
        };
    }

    private static string? TryGetDocumentLanguage(JsonElement value)
        => TryGetString(value, "docLanguage")
           ?? TryGetString(value, "doc_language")
           ?? TryGetString(value, "DocLanguage")
           ?? TryGetString(value, "documentLanguage")
           ?? TryGetString(value, "document_language")
           ?? TryGetString(value, "DocumentLanguage")
           ?? TryGetString(value, "sourceLanguage")
           ?? TryGetString(value, "source_language")
           ?? TryGetString(value, "SourceLanguage");

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

                foreach (var h in hits.EnumerateArray()
                    .Where(static h => h.ValueKind == JsonValueKind.Object)
                    .Select(static h => BuildRagHitSummary(h))
                    .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
                    .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit)))
                {
                    if (string.IsNullOrWhiteSpace(h.DocPath)) continue;
                    sources.Add(BuildSourceRefFromRagHit(h));
                }
            }

            return MergeSourceRefsByPage(sources)
                .Take(8)
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
        var planningSources = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                ResolveSourceBackedPlanningTargetItemCount(query))
            .Select(candidate => candidate.Hit)
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return planningSources.Count > 0
            ? planningSources
            : DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
    }

    private static string? TryInferDominantTopLevelCategoryScope(ToolResults toolResults, string? query = null)
    {
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return null;

        var grouped = hits
            .Select(hit => new
            {
                Hit = hit,
                TopLevel = ExtractTopLevelCategoryScope(hit)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.TopLevel))
            .GroupBy(static item => item.TopLevel!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Scope = group.Key,
                Count = group.Count(),
                MaxScore = group.Max(static item => item.Hit.Score),
                QueryRelevance = group.Max(item => ComputeRagHitLexicalRelevance(query ?? string.Empty, GetRagHitLookupText(item.Hit)))
            })
            .OrderByDescending(static group => group.Count)
            .ThenByDescending(static group => group.QueryRelevance)
            .ThenByDescending(static group => group.MaxScore)
            .ToList();

        var best = grouped.FirstOrDefault();
        if (best is null)
            return null;

        return best.Count >= 2 || grouped.Count == 1
            ? best.Scope
            : null;
    }

    private static string? ExtractTopLevelCategoryScope(RagHitSummary hit)
    {
        var path = CollapseWhitespace(hit.CategoryPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            path = CollapseWhitespace(hit.DocPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Replace('\\', '/').Trim('/');
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first) || first.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        return first;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromOptionHits(ToolResults toolResults, string? query = null)
    {
        var optionHits = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems: 1).Items
            .Select(candidate => candidate.Hit)
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return optionHits.Count > 0
            ? optionHits
            : DeriveSourcesFromPlanningHits(toolResults, query);
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromCountdownPlanningHits(ToolResults toolResults, string? query = null)
    {
        var countdownHits = SelectSourceBackedCountdownPlanningCandidates(toolResults, query)
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(candidate => candidate.VisibleMinutes!.Value)
            .ThenByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Hit)
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return countdownHits.Count > 0
            ? countdownHits
            : DeriveSourcesFromExtractiveHits(toolResults, query ?? string.Empty);
    }

    private static ToolMemory.SourceRef BuildSourceRefFromRagHit(RagHitSummary hit)
    {
        var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(hit.DocId),
            DocPath = hit.DocPath.Replace('\\', '/'),
            DocName = NullIfWhiteSpace(hit.DocName) ?? NullIfWhiteSpace(Path.GetFileName(hit.DocPath)),
            PageStart = hit.PageStart,
            PageEnd = hit.PageEnd,
            Label = label,
            SourceHash = NullIfWhiteSpace(hit.SourceHash),
            DocLanguage = NullIfWhiteSpace(hit.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(hit.ProfileLanguage),
            Category = NullIfWhiteSpace(hit.Category),
            CategoryRef = NullIfWhiteSpace(hit.CategoryRef),
            CategoryPath = NullIfWhiteSpace(hit.CategoryPath),
            ChunkId = NullIfWhiteSpace(hit.ChunkId),
            ExtractionSource = NullIfWhiteSpace(hit.ExtractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(hit.DocumentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(hit.PageQualityStatus),
            TextStatus = NullIfWhiteSpace(hit.TextStatus),
            QualityStatus = NullIfWhiteSpace(hit.QualityStatus),
            ExtractionConfidence = hit.ExtractionConfidence,
            DocumentExtractionConfidence = hit.DocumentExtractionConfidence,
            PageExtractionConfidence = hit.PageExtractionConfidence,
            ManualReviewRecommended = hit.ManualReviewRecommended,
            DocumentManualReviewRecommended = hit.DocumentManualReviewRecommended,
            PageManualReviewRecommended = hit.PageManualReviewRecommended,
            OcrAttempted = hit.OcrAttempted,
            OcrApplied = hit.OcrApplied,
            OcrRecommended = hit.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(hit.ExtractionDiagnosticSummary),
            QualitySignals = hit.QualitySignals?
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? new List<string>(),
            MatchedContentCards = hit.MatchedContentCards?
                .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                .Select(static card => new ToolMemory.SourceContentCardRef
                {
                    Title = card.Title,
                    ContentCardId = NullIfWhiteSpace(card.ContentCardId),
                    PageStart = card.PageStart,
                    PageEnd = card.PageEnd,
                    Kind = NullIfWhiteSpace(card.Kind),
                    Signals = card.Signals?
                        .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                        .Select(static signal => signal.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList() ?? new List<string>(),
                    Evidence = GetSourceContentCardEvidenceElement(card)
                })
                .Take(5)
                .ToList() ?? new List<ToolMemory.SourceContentCardRef>(),
            SelectionHintEvidenceRole = NullIfWhiteSpace(hit.SelectionHintRole),
            SelectionHintActionabilityScore = hit.SelectionHintActionabilityScore,
            SelectionHintSupportScore = hit.SelectionHintSupportScore,
            SelectionHintFragmentScore = hit.SelectionHintFragmentScore,
            SelectionHintNavigationScore = hit.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = hit.SelectionHintQualityPenalty,
            ContentRole = NullIfWhiteSpace(hit.ContentRole),
            NavigationReason = NullIfWhiteSpace(hit.NavigationReason),
            RetrievalNavigationScore = hit.NavigationScore,
            ContentDensityScore = hit.ContentDensityScore
        };
    }

    private static List<ToolMemory.SourceRef> MergeSourceRefsByPage(IEnumerable<ToolMemory.SourceRef> sources, int maxCardsPerSource = 5)
        => sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.DocPath))
            .GroupBy(
                static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeSourceRefGroup(group, maxCardsPerSource))
            .OrderByDescending(ComputeSourceRefRichness)
            .ToList();

    private static ToolMemory.SourceRef MergeSourceRefGroup(
        IEnumerable<ToolMemory.SourceRef> group,
        int maxCardsPerSource)
    {
        var sources = group
            .OrderByDescending(ComputeSourceRefRichness)
            .ToList();
        var primary = sources[0];

        return new ToolMemory.SourceRef
        {
            DocId = PickSourceString(sources, static source => source.DocId),
            DocPath = primary.DocPath,
            DocName = PickSourceString(sources, static source => source.DocName),
            PageStart = primary.PageStart,
            PageEnd = primary.PageEnd,
            Label = primary.Label,
            SourceHash = PickSourceString(sources, static source => source.SourceHash),
            DocLanguage = PickSourceString(sources, static source => source.DocLanguage),
            ProfileLanguage = PickSourceString(sources, static source => source.ProfileLanguage),
            Category = PickSourceString(sources, static source => source.Category),
            CategoryRef = PickSourceString(sources, static source => source.CategoryRef),
            CategoryPath = PickSourceString(sources, static source => source.CategoryPath),
            ChunkId = PickSourceString(sources, static source => source.ChunkId),
            ExtractionSource = PickSourceString(sources, static source => source.ExtractionSource),
            DocumentQualityStatus = PickSourceString(sources, static source => source.DocumentQualityStatus),
            PageQualityStatus = PickSourceString(sources, static source => source.PageQualityStatus),
            TextStatus = PickSourceString(sources, static source => source.TextStatus),
            QualityStatus = PickSourceString(sources, static source => source.QualityStatus),
            ExtractionConfidence = PickBestConfidence(sources, static source => source.ExtractionConfidence),
            DocumentExtractionConfidence = PickBestConfidence(sources, static source => source.DocumentExtractionConfidence),
            PageExtractionConfidence = PickBestConfidence(sources, static source => source.PageExtractionConfidence),
            ManualReviewRecommended = sources.Any(static source => source.ManualReviewRecommended),
            DocumentManualReviewRecommended = sources.Any(static source => source.DocumentManualReviewRecommended),
            PageManualReviewRecommended = sources.Any(static source => source.PageManualReviewRecommended),
            OcrAttempted = sources.Any(static source => source.OcrAttempted),
            OcrApplied = sources.Any(static source => source.OcrApplied),
            OcrRecommended = sources.Any(static source => source.OcrRecommended),
            ExtractionDiagnosticSummary = sources
                .Select(static source => CloneSourceExtractionDiagnostic(source.ExtractionDiagnosticSummary))
                .FirstOrDefault(static summary => summary is not null),
            QualitySignals = sources
                .SelectMany(static source => source.QualitySignals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            MatchedContentCards = MergeSourceContentCards(sources, maxCardsPerSource),
            SelectionHintEvidenceRole = PickSourceString(sources, static source => source.SelectionHintEvidenceRole),
            SelectionHintActionabilityScore = PickBestScore(sources, static source => source.SelectionHintActionabilityScore),
            SelectionHintSupportScore = PickBestScore(sources, static source => source.SelectionHintSupportScore),
            SelectionHintFragmentScore = PickBestScore(sources, static source => source.SelectionHintFragmentScore),
            SelectionHintNavigationScore = PickBestScore(sources, static source => source.SelectionHintNavigationScore),
            SelectionHintQualityPenalty = PickBestScore(sources, static source => source.SelectionHintQualityPenalty),
            ContentRole = PickBestContentRole(sources),
            NavigationReason = PickSourceString(sources, static source => source.NavigationReason),
            RetrievalNavigationScore = PickLowestDouble(sources, static source => source.RetrievalNavigationScore),
            ContentDensityScore = PickBestConfidence(sources, static source => source.ContentDensityScore)
        };
    }

    private static List<ToolMemory.SourceContentCardRef> MergeSourceContentCards(
        IEnumerable<ToolMemory.SourceRef> sources,
        int maxCards)
        => sources
            .SelectMany(static source => source.MatchedContentCards)
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .GroupBy(BuildSourceContentCardMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => CloneSourceContentCardRef(
                group
                    .OrderByDescending(ComputeSourceContentCardRichness)
                    .First()))
            .OrderByDescending(ComputeSourceContentCardRichness)
            .Take(maxCards)
            .ToList();

    private static ToolMemory.SourceContentCardRef CloneSourceContentCardRef(ToolMemory.SourceContentCardRef card)
        => new()
        {
            Title = card.Title,
            ContentCardId = NullIfWhiteSpace(card.ContentCardId),
            PageStart = card.PageStart,
            PageEnd = card.PageEnd,
            Kind = NullIfWhiteSpace(card.Kind),
            Signals = card.Signals
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            Evidence = CloneNullableJsonElement(card.Evidence)
        };

    private static JsonElement? GetSourceContentCardEvidenceElement(RagHitContentCardSummary card)
    {
        if (card.RawEvidence is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } raw)
            return raw.Clone();

        return card.Evidence is null
            ? null
            : JsonSerializer.SerializeToElement(card.Evidence, ClientJson.CamelCase);
    }

    private static JsonElement? CloneNullableJsonElement(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
            ? element.Clone()
            : null;

    private static string BuildSourceContentCardMergeKey(ToolMemory.SourceContentCardRef card)
    {
        var id = NullIfWhiteSpace(card.ContentCardId);
        if (!string.IsNullOrWhiteSpace(id))
            return $"id:{id}";

        return string.Join(
            "|",
            "shape",
            CollapseWhitespace(card.Title).ToLowerInvariant(),
            CollapseWhitespace(card.Kind ?? string.Empty).ToLowerInvariant(),
            card.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            card.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static int ComputeSourceRefRichness(ToolMemory.SourceRef source)
        => (source.MatchedContentCards.Count * 12)
           + (source.MatchedContentCards.Count(static card => card.Evidence is not null) * 6)
           + (source.QualitySignals.Count * 2)
           + (!string.IsNullOrWhiteSpace(source.SourceHash) ? 3 : 0)
           + (!string.IsNullOrWhiteSpace(source.DocLanguage) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.ProfileLanguage) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.Category) ? 1 : 0)
           + (!string.IsNullOrWhiteSpace(source.ChunkId) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.ContentRole) ? 2 : 0)
           + (source.ContentDensityScore.HasValue ? 1 : 0)
           + (source.ExtractionDiagnosticSummary is not null ? 2 : 0)
           + (source.SelectionHintActionabilityScore ?? 0)
           + (source.SelectionHintSupportScore ?? 0)
           - (source.SelectionHintNavigationScore ?? 0);

    private static int ComputeSourceContentCardRichness(ToolMemory.SourceContentCardRef card)
        => (!string.IsNullOrWhiteSpace(card.ContentCardId) ? 8 : 0)
           + (card.Evidence is not null ? 8 : 0)
           + (card.Signals.Count * 2)
           + (card.PageStart.HasValue ? 1 : 0)
           + (card.PageEnd.HasValue ? 1 : 0);

    private static string? PickSourceString(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, string?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static double? PickBestConfidence(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, double?> selector)
        => sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty()
            .Max() is var value && value > 0.0
                ? value
                : null;

    private static int? PickBestScore(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, int?> selector)
        => sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty()
            .Max() is var value && value != 0
                ? value
                : null;

    private static double? PickLowestDouble(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, double?> selector)
    {
        var values = sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static string? PickBestContentRole(IEnumerable<ToolMemory.SourceRef> sources)
    {
        var roles = sources
            .Select(static source => NullIfWhiteSpace(source.ContentRole))
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .ToArray();
        if (roles.Length == 0)
            return null;

        if (roles.Any(static role => string.Equals(role, "content", StringComparison.OrdinalIgnoreCase)))
            return "content";
        if (roles.Any(static role => string.Equals(role, "mixed_navigation_content", StringComparison.OrdinalIgnoreCase)))
            return "mixed_navigation_content";
        return roles[0];
    }

    private static object BuildSourcesPayload(List<ToolMemory.SourceRef> sources)
        => new
        {
            sources = sources.Select(x => new
            {
                docId = x.DocId,
                docPath = x.DocPath,
                docName = x.DocName,
                pageStart = x.PageStart,
                pageEnd = x.PageEnd,
                label = x.Label,
                sourceHash = x.SourceHash,
                docLanguage = x.DocLanguage,
                profileLanguage = x.ProfileLanguage,
                category = x.Category,
                categoryRef = x.CategoryRef,
                categoryPath = x.CategoryPath,
                chunkId = x.ChunkId,
                contentSignals = BuildSourceContentSignalsPayload(x),
                extractionQuality = BuildSourceExtractionQualityPayload(x),
                matchedContentCards = BuildSourceContentCardsPayload(x),
                selectionHints = BuildSourceSelectionHintsPayload(x)
            }).ToList()
        };

    private static object BuildSourcesPayload(string intent, List<ToolMemory.SourceRef> sources)
        => new
        {
            intent,
            sources = BuildSourcePayloadItems(sources)
        };

    private static List<object> BuildSourcePayloadItems(List<ToolMemory.SourceRef> sources)
        => sources.Select(x => new
        {
            docId = x.DocId,
            docPath = x.DocPath,
            docName = x.DocName,
            pageStart = x.PageStart,
            pageEnd = x.PageEnd,
            label = x.Label,
            sourceHash = x.SourceHash,
            docLanguage = x.DocLanguage,
            profileLanguage = x.ProfileLanguage,
            category = x.Category,
            categoryRef = x.CategoryRef,
            categoryPath = x.CategoryPath,
            chunkId = x.ChunkId,
            contentSignals = BuildSourceContentSignalsPayload(x),
            extractionQuality = BuildSourceExtractionQualityPayload(x),
            matchedContentCards = BuildSourceContentCardsPayload(x),
            selectionHints = BuildSourceSelectionHintsPayload(x)
        }).Cast<object>().ToList();

    private static object? BuildSourceContentSignalsPayload(ToolMemory.SourceRef source)
        => string.IsNullOrWhiteSpace(source.ContentRole)
           && string.IsNullOrWhiteSpace(source.NavigationReason)
           && source.RetrievalNavigationScore is null
           && source.ContentDensityScore is null
            ? null
            : new
            {
                contentRole = source.ContentRole,
                navigationReason = source.NavigationReason,
                navigationScore = source.RetrievalNavigationScore,
                contentDensityScore = source.ContentDensityScore
            };

    private static object? BuildSourceSelectionHintsPayload(ToolMemory.SourceRef source)
        => string.IsNullOrWhiteSpace(source.SelectionHintEvidenceRole)
           && source.SelectionHintActionabilityScore is null
           && source.SelectionHintSupportScore is null
           && source.SelectionHintFragmentScore is null
           && source.SelectionHintNavigationScore is null
           && source.SelectionHintQualityPenalty is null
            ? null
            : new
            {
                evidenceRole = source.SelectionHintEvidenceRole,
                actionabilityScore = source.SelectionHintActionabilityScore,
                supportScore = source.SelectionHintSupportScore,
                fragmentScore = source.SelectionHintFragmentScore,
                navigationScore = source.SelectionHintNavigationScore,
                qualityPenalty = source.SelectionHintQualityPenalty
            };

    private static object? BuildSourceExtractionQualityPayload(ToolMemory.SourceRef source)
    {
        var diagnosticSummary = BuildSourceExtractionDiagnosticPayload(source.ExtractionDiagnosticSummary);
        if (string.IsNullOrWhiteSpace(source.QualityStatus)
            && string.IsNullOrWhiteSpace(source.ExtractionSource)
            && string.IsNullOrWhiteSpace(source.DocumentQualityStatus)
            && string.IsNullOrWhiteSpace(source.PageQualityStatus)
            && string.IsNullOrWhiteSpace(source.TextStatus)
            && source.ExtractionConfidence is null
            && source.DocumentExtractionConfidence is null
            && source.PageExtractionConfidence is null
            && !source.ManualReviewRecommended
            && !source.DocumentManualReviewRecommended
            && !source.PageManualReviewRecommended
            && !source.OcrAttempted
            && !source.OcrApplied
            && !source.OcrRecommended
            && source.QualitySignals.Count == 0
            && diagnosticSummary is null)
        {
            return null;
        }

        return new
        {
            extractionSource = source.ExtractionSource,
            documentQualityStatus = source.DocumentQualityStatus,
            pageQualityStatus = source.PageQualityStatus,
            textStatus = source.TextStatus,
            qualityStatus = source.QualityStatus,
            extractionConfidence = source.ExtractionConfidence,
            documentExtractionConfidence = source.DocumentExtractionConfidence,
            pageExtractionConfidence = source.PageExtractionConfidence,
            manualReviewRecommended = source.ManualReviewRecommended,
            documentManualReviewRecommended = source.DocumentManualReviewRecommended,
            pageManualReviewRecommended = source.PageManualReviewRecommended,
            ocrAttempted = source.OcrAttempted,
            ocrApplied = source.OcrApplied,
            ocrRecommended = source.OcrRecommended,
            signals = source.QualitySignals.Count == 0 ? null : source.QualitySignals,
            diagnosticSummary
        };
    }

    private static object? BuildSourceExtractionDiagnosticPayload(ToolMemory.SourceExtractionDiagnosticRef? summary)
    {
        if (summary is null)
            return null;

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nativeTextStatus"] = NullIfWhiteSpace(summary.NativeTextStatus),
            ["nativeOcrRecommended"] = summary.NativeOcrRecommended,
            ["ocrMode"] = NullIfWhiteSpace(summary.OcrMode),
            ["ocrLanguages"] = NullIfWhiteSpace(summary.OcrLanguages),
            ["ocrDurationMs"] = summary.OcrDurationMs,
            ["ocrFailureReason"] = NullIfWhiteSpace(summary.OcrFailureReason),
            ["ocrAppliedReason"] = NullIfWhiteSpace(summary.OcrAppliedReason),
            ["ocrTimedOut"] = summary.OcrTimedOut,
            ["ocrAttemptedPageCount"] = summary.OcrAttemptedPageCount,
            ["ocrSkippedPageCount"] = summary.OcrSkippedPageCount,
            ["ocrPagesWithNovelTextCount"] = summary.OcrPagesWithNovelTextCount,
            ["pageCount"] = summary.PageCount,
            ["textPageCount"] = summary.TextPageCount,
            ["emptyPageCount"] = summary.EmptyPageCount,
            ["sparsePageCount"] = summary.SparsePageCount,
            ["imagePageCount"] = summary.ImagePageCount,
            ["pageWarningCount"] = summary.PageWarningCount,
            ["pageReviewRecommendedCount"] = summary.PageReviewRecommendedCount
        };

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? BuildSourceContentCardsPayload(ToolMemory.SourceRef source)
        => source.MatchedContentCards.Count == 0
            ? null
            : source.MatchedContentCards.Select(static card => new
            {
                title = card.Title,
                contentCardId = card.ContentCardId,
                pageStart = card.PageStart,
                pageEnd = card.PageEnd,
                kind = card.Kind,
                signals = card.Signals,
                evidence = card.Evidence
            }).ToList();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<ToolMemory.SourceRef> DeriveSourcesFromRankedRagHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .OrderByDescending(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)))
            .ToList();

        var requestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var exactTitleHits = hits
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!)
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .ToList();
            var anchoredExactTitleHits = exactTitleHits
                .Where(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .ToList();
            if (anchoredExactTitleHits.Count > 0)
                exactTitleHits = anchoredExactTitleHits;
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
            .Select(BuildSourceRefFromRagHit)
            .ToList();
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromExtractiveHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        var requestedTitle = TryExtractRequestedItemTitle(query);
        if (!string.IsNullOrWhiteSpace(requestedTitle)
            && allHits.Count > 0
            && (!RagHitsContainRequestedTitle(allHits, requestedTitle!)
                || !allHits.Any(hit => (RagHitContainsRequestedTitle(hit, requestedTitle!)
                        || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                    && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit))))
        {
            return DeriveSourcesFromMissingExactItemCloseLeads(requestedTitle!, allHits);
        }

        var sourceLimit = maxSources;
        if (LooksLikeRankingDocumentaryRequest(query))
            sourceLimit = Math.Min(sourceLimit, 4);
        else if (!string.IsNullOrWhiteSpace(requestedTitle))
            sourceLimit = Math.Min(sourceLimit, 4);
        else if (LooksLikeSourceBackedQuantityScalingRequest(query))
            sourceLimit = Math.Min(sourceLimit, 1);
        else if (LooksLikeSourceBackedAdaptationRequest(query))
            sourceLimit = Math.Min(sourceLimit, 5);

        List<RagHitSummary> selectedHits;
        if (LooksLikeSourceBackedQuantityScalingRequest(query) && TryExtractTargetScaleCount(query, out var targetCount))
        {
            selectedHits = SelectQuantityScalingCandidates(EnumerateRagHitSummaries(toolResults), query, targetCount)
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
        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var stronglyAnchoredHits = selectedHits
                .Where(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .ToList();
            if (stronglyAnchoredHits.Count > 0)
                selectedHits = stronglyAnchoredHits;

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
        else if (LooksLikeTechnicalRankingQuery(query))
        {
            var structuredEnoughHits = selectedHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                selectedHits = structuredEnoughHits;

            var nonVeryShortHits = selectedHits
                .Where(static hit => ExtractBestVisibleDurationMinutes(hit) is not int minutes || minutes > 3)
                .ToList();
            if (nonVeryShortHits.Count > 0)
                selectedHits = nonVeryShortHits;
        }

        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var timeCompatibleHits = selectedHits
                .Where(hit =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (timeCompatibleHits.Count > 0)
                selectedHits = timeCompatibleHits;
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
            .Select(BuildSourceRefFromRagHit)
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

    private static bool LooksLikeMissingExactItemWithoutSourceLeads(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var missingExact = Regex.IsMatch(
            normalized,
            @"\b(?:je\s+n['\s]?ai\s+pas\s+trouve\s+l['\s]?element\s+exact|i\s+did\s+not\s+find\s+the\s+exact\s+requested\s+item|no\s+he\s+encontrado\s+el\s+elemento\s+exacto|nao\s+encontrei\s+o\s+item\s+exato|ich\s+habe\s+den\s+exakt\s+angefragten\s+eintrag|non\s+ho\s+trovato\s+l['\s]?elemento\s+esatto)\b",
            RegexOptions.CultureInvariant);
        if (!missingExact)
            return false;

        return !Regex.IsMatch(
            normalized,
            @"\b(?:pistes\s+proches|closest\s+source-backed\s+leads|pistas\s+cercanas|pistas\s+proximas|naheliegende\s+belegte\s+hinweise|indicazioni\s+vicine)\b",
            RegexOptions.CultureInvariant);
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
            @"\b(?:fiche|fiches|card|cards|element|elements|item|items|etape|etapes|étape|étapes|step|steps|procedure|procedures|process|processus)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return LooksLikeSourceBackedQuantityScalingRequest(query)
            || LooksLikeSourceBackedAdaptationRequest(query)
            || LooksLikeRankingDocumentaryRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query);
    }

    internal static string BuildSourceBackedExtractiveAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var missingRequiredEvidence = TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(missingRequiredEvidence))
            return missingRequiredEvidence;

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
            && !allHits.Any(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit)))
        {
            var answer = BuildMissingExactItemAnswer(language, requestedTitle!, allHits);
            return LooksLikeSourceBypassOrUnsupportedInventionRequest(query)
                ? ApplySourcePolicyGuardPrefix(answer, language)
                : answer;
        }

        var hits = SelectSourceBackedExtractiveHits(toolResults, query, maxHits: 4).ToList();
        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var withinTimeHits = hits
                .Where(hit =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (withinTimeHits.Count > 0)
                hits = withinTimeHits;
        }

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
            if (hits.Count > 0 && hits.Any(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                    && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit)))
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
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
        return sb.ToString().TrimEnd();
    }

    private static string? TryExtractRequestedItemTitle(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        if (LooksLikeUnresolvedSourceBackedDeicticFollowup(s))
            return null;

        if (LooksLikeBroadSourceBackedCompositionRequest(s))
            return null;

        var availableItemRequest = Regex.Match(
            s,
            @"(?i)\b(?:j['\u2019]ai|je\s+dispose\s+de|i\s+have|tengo|tenho|ich\s+habe|ho)\s+(?:du|de\s+la|de\s+l['\u2019]|des|un|une|some|a|an|el|la|los|las|o|a|os|as|ein|eine|einen|del|della|dei|delle)?\s*(?<title>[\p{L}0-9'\u2019 \-]{3,70}?)(?:[,;?.!]|$).{0,100}\b(?:tu\s+as|vous\s+avez|as[-\s]?tu|avez[-\s]?vous|do\s+you\s+have|can\s+you|could\s+you|puedes|podes|pode|kannst|puoi|hai|hast)\b",
            RegexOptions.CultureInvariant);
        if (availableItemRequest.Success)
        {
            var title = CleanupRequestedItemTitle(availableItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var quoted = Regex.Match(s, "[\\u00ab\"'](?<title>[^\\u00bb\"']{3,90})[\\u00bb\"']", RegexOptions.CultureInvariant);
        if (quoted.Success)
        {
            var title = CleanupQuotedRequestedItemTitle(quoted.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var actionConnectorTarget = Regex.Match(
            s,
            @"(?i)(?:^|[,.!?;]\s*)(?:\b(?:peux(?:[-\s]+tu)?|pourrais(?:[-\s]+tu)?|tu\s+peux|vous\s+pouvez|can\s+you|could\s+you)\s+(?:me\s+|m['\u2019]|nous\s+)?)?(?:donner|donne|donnes|donnez|montrer|montre|montres|montrez|afficher|affiche|affiches|affichez|faire|fais|faites|give|show|make|mostrar|hacer|haz|fazer|mostra|machen|zeigen|fare)\b[^:?.!,;]{0,70}?\b(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|über|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (actionConnectorTarget.Success)
        {
            var title = CleanupRequestedItemTitle(actionConnectorTarget.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var metricForItemRequest = Regex.Match(
            s,
            @"(?i)\b(?:combien|quel|quelle|quels|quelles|what|which|how)\b.+?\b(?:pour|for|sur|about|on)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)?\s*(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (metricForItemRequest.Success)
        {
            var title = CleanupRequestedItemTitle(metricForItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var politeItemRequests = Regex.Matches(
            s,
            @"(?i)(?:^|[,.!?;]\s*)\b(?:peux(?:[-\s]+tu)?|pourrais(?:[-\s]+tu)?|tu\s+peux|vous\s+pouvez|can\s+you|could\s+you)\s+(?:me\s+|m['\u2019]|nous\s+)?(?:donner|montrer|afficher|chercher|trouver|faire|give|show|find|make)\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        foreach (Match politeItemRequest in politeItemRequests)
        {
            var title = CleanupRequestedItemTitle(politeItemRequest.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        var leadingPurposeItem = Regex.Match(
            s,
            @"(?i)^(?<title>[\p{L}0-9'\u2019 \-]{4,70}?)(?:\s+(?:pour|for|para|per)\s+[^:?.!,;]{2,90})(?:[:?.!,;]|$)",
            RegexOptions.CultureInvariant);
        if (leadingPurposeItem.Success)
        {
            var title = CleanupRequestedItemTitle(leadingPurposeItem.Groups["title"].Value);
            var normalizedTitle = NormalizeLexicalLookup(title);
            var tokenCount = ExtractQuerySignalTerms(normalizedTitle).Count();
            if (!string.IsNullOrWhiteSpace(title)
                && tokenCount is >= 2 and <= 6
                && !Regex.IsMatch(normalizedTitle, @"\b(?:aide|aider|faire|calcule|calculer|adapte|adapter|ajuste|ajuster|convertis|convertir|besoin|veux|voudrais|souhaite|cherche|trouve|donne|propose|what|which|need|want|find|give|suggest)\b", RegexOptions.CultureInvariant)
                && LooksLikeDirectRequestedItemTitle(title))
            {
                return title;
            }
        }

        var namedItem = Regex.Match(
            s,
            @"(?i)\b(?:fiche|card|document|doc|contrat|contract|procedure|proc[ée]dure|processus|process|manuel|manual|guide|rapport|report|notice|policy|politique)\s+(?:claire\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (namedItem.Success)
            return CleanupRequestedItemTitle(namedItem.Groups["title"].Value);

        var baseItem = Regex.Match(
            s,
            @"(?i)\b(?:element|item|objet|sujet|topic)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+(?<title>[^:?.!,;]{3,90})",
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
            @"(?i)\b(?:vitesses?|speeds?|temp[e\u00e9]ratures?|temperatures?|r[e\u00e9]glages?|settings?|param[e\u00e8]tres?|parameters?|quantit[e\u00e9]s?|quantites?|[e\u00e9]tapes?|steps?|temps|time|values?|valeurs?)\b",
                RegexOptions.CultureInvariant))
        {
            if (Regex.IsMatch(
                    s,
                    @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|amounts?|values?|valeurs?|counts?|units?|items?)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
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
            {
                var title = CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }

            parameterTarget = Regex.Match(
                s,
                @"(?i)\b(?:pour|for|de|du|de\s+la|des|d['\u2019]|sur|about|on)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>[^:?.!,;]{3,90})",
                RegexOptions.CultureInvariant);
            if (parameterTarget.Success)
            {
                var title = CleanupRequestedItemTitle(parameterTarget.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                    return title;
            }
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
            @"(?i)(?:^|[,.!?;]\s*)\b(?:je\s+(?:fais|prepare|prépare)|nous\s+(?:faisons|preparons|préparons)|i\s+(?:am\s+)?(?:making|preparing)|we\s+(?:are\s+)?(?:making|preparing))\s+(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+(?<title>[^:?.!,;]{3,90}?)(?:\s+(?:avec|pour|for|with)\b|[:?.!,;]|$)",
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
            @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|values?|valeurs?)\s+(?:pour|for)\s+\d+\s+(?:[\p{L}'\u2019-]+\s+){0,3}(?:de|d['\u2019]|of)\s+(?<title>[^:?.!,;]{3,90})",
            RegexOptions.CultureInvariant);
        if (quantitySubject.Success)
        {
            var title = CleanupRequestedItemTitle(quantitySubject.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title) && LooksLikeDirectRequestedItemTitle(title))
                return title;
        }

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:quantit[e\u00e9]s?|quantites?|organisation|amounts?|values?|valeurs?|counts?|units?|items?)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
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

    private static bool LooksLikeBroadSourceBackedCompositionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:document|fiche|card|procedure|process)\s+(?:de|du|des|pour|about|on)\b", RegexOptions.CultureInvariant))
            return false;

        var hasBroadIntent = Regex.IsMatch(
            normalized,
            @"\b(?:plan\s+complet|planning\s+complet|complete\s+plan|composition|compose|composer|propose|proposes|options?|idees?|suggestions?|selection|sélection|quoi\s+faire|what\s+to\s+use|which\s+option)\b",
            RegexOptions.CultureInvariant);

        return hasBroadIntent
            || Regex.IsMatch(
                normalized,
                @"\b(?:menu|plan|planning|programme|program|selection|sélection|composition)\s+(?:de|du|des|pour|for|about)\b.*\b(?:avec|with|con|mit)\b",
                RegexOptions.CultureInvariant);
    }

    private static string? CleanupRequestedItemTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';');
        if (title.Length == 0)
            return null;
        var originalTitle = title;

        title = Regex.Replace(
            title,
            @"(?i)^(?:le|la|les|l['\u2019]|un|une|des|du|de\s+la|de\s+l['\u2019]|the|a|an|some)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|methode|method)\s+(?:claire\s+|detaillee\s+|detaill[eé]e\s+|complete\s+|compl[eè]te\s+|sourcee\s+|sourc[eé]e\s+|clear\s+|detailed\s+)?(?:de\s+base\s+|base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();


        title = Regex.Replace(
            title,
            @"(?i)\s+(?:en\s+mode|mode|version|variante|pour\s+(?:\d+|un|une|des|le|la|les|l['\u2019]|the|a|an|some)\b|dans\s+(?:le|la|les|l['\u2019]|un|une|des|the|a|an)\b|du\s+(?:guide|pdf|document|manuel|livre|book|manual|file|document|corpus|dossier)\b|de\s+la\s+(?:base|page|fiche|notice|section)\b|des\s+(?:sources|documents|docs|fichiers|files)\b).*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        title = Regex.Replace(
            title,
            @"(?i)^(?:[\p{L}][\p{L}'\u2019-]{2,24}\s+){1,4}(?:de\s+base\s+|base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|of|for|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^(?:element|item|objet|sujet|topic)\s+(?:de\s+base\s+)?(?:pour|about|on|de|du|de\s+la|des|d['\u2019]|sur|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)^base\s+(?:des|du|de\s+la|de\s+l['\u2019]|de|pour|about|on|d['\u2019]|sur|sobre|ueber|über|su)\s+",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:etapes?|[ée]tapes?|steps?|temps|time|source|sources|quantites?|values?|valeurs?|reglages?|r[ée]glages?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        var preservedTitle = CleanupQuotedRequestedItemTitle(originalTitle);
        if (!string.IsNullOrWhiteSpace(preservedTitle)
            && LooksLikeConnectorInsideNaturalTitle(preservedTitle)
            && !LooksLikeGenericTitleDescriptorPrefix(preservedTitle)
            && LooksLikeDirectRequestedItemTitle(preservedTitle))
        {
            title = preservedTitle;
        }

        return title.Length >= 3 ? title : null;
    }

    private static string? CleanupQuotedRequestedItemTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', ':', '-', '.', '?', '!', ',', ';', '"', '\'', '\u00ab', '\u00bb');
        if (title.Length == 0)
            return null;

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:en\s+mode|mode|version|variante|pour\s+(?:\d+|un|une|des|le|la|les|l['\u2019]|the|a|an|some)\b|dans\s+(?:le|la|les|l['\u2019]|un|une|des|the|a|an)\b|du\s+(?:guide|pdf|document|manuel|livre|book|manual|file|document|corpus|dossier)\b|de\s+la\s+(?:base|page|fiche|notice|section)\b|des\s+(?:sources|documents|docs|fichiers|files)\b).*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        title = Regex.Replace(
            title,
            @"(?i)\s+(?:etapes?|[Ã©e]tapes?|steps?|temps|time|source|sources|quantites?|values?|valeurs?|reglages?|r[Ã©e]glages?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        return title.Length >= 3 ? title : null;
    }

    private static bool LooksLikeConnectorInsideNaturalTitle(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"^[\p{L}\p{N}'\u2019-]{3,}(?:\s+[\p{L}\p{N}'\u2019-]{2,}){0,4}\s+(?:de|du|des|d|of|with|a|au|aux|al|alla|di|con|mit)\s+[\p{L}\p{N}'\u2019-]{3,}",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeGenericTitleDescriptorPrefix(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"^(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|methode|method|option|idee|idea|preparation|rapport|report|guide|manuel|manual|base)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDirectRequestedItemTitle(string title)
    {
        var normalized = NormalizeLexicalLookup(title);
        if (normalized.Length < 3 || normalized.Length > 90)
            return false;

        if (Regex.IsMatch(normalized, @"^(?:corpus|documents?|sources?|fichiers?|files?|pdf)$", RegexOptions.CultureInvariant))
            return false;

        if (Regex.IsMatch(normalized, @"^\d+\s+(?:items?|elements?|units?|pieces?)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d+\s+[\p{L}'\u2019.\-]{2,30}$", RegexOptions.CultureInvariant))
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

        return ExtractQuerySignalTerms(normalized).Any()
            || ExtractRequestedTitleSignalTerms(normalized).Any();
    }

    private static bool RagHitsContainRequestedTitle(IReadOnlyList<RagHitSummary> hits, string requestedTitle)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        foreach (var hit in hits)
        {
            if (RagHitContainsRequestedTitle(hit, requestedTitle))
                return true;
        }

        return false;
    }

    private static bool RagHitContainsRequestedTitle(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);

        if (RagHitContainsRequestedTitle(hit, normalizedTitle, titleTerms))
            return true;

        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedTitle))
        {
            var normalizedVariant = NormalizeLexicalLookup(variant);
            if (string.IsNullOrWhiteSpace(normalizedVariant))
                continue;

            var variantTerms = ExtractRequestedTitleSignalTerms(normalizedVariant);

            if (RagHitContainsRequestedTitle(hit, normalizedVariant, variantTerms))
                return true;
        }

        return false;
    }

    private static bool RagHitHasUsableRequestedTitleAnchor(string requestedTitle, RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) || string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        if (ComputeBestSourceBackedDisplayTitleScore(requestedTitle, hit) >= 40)
            return true;

        if (RagHitContainsRequestedTitlePhraseAnchor(hit, requestedTitle))
            return true;

        if (RagHitHasCoherentRequestedTitleContent(hit, requestedTitle))
            return true;

        return ComputeExactItemAnchorStrengthScore(requestedTitle, hit) >= 80;
    }

    private static bool RagHitHasCoherentRequestedTitleContent(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return false;

        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (!titleTerms.All(term => content.Contains(term, StringComparison.Ordinal)))
            return false;

        if (titleTerms.Length <= 2)
        {
            var lead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
            if (lead.Length > 360)
                lead = lead[..360];
            var normalizedLead = NormalizeLexicalLookup(lead);
            return IndexOfRequestedTitlePhrase(normalizedLead, normalizedTitle) is >= 0 and <= 160
                && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40;
        }

        return ComputeExactItemCardCompletenessCueScore(hit) >= 4
            || ComputeProcedureCompletenessCueScore(hit) >= 4
            || CountProcedureStepMarkers(content) >= 2;
    }

    private static bool RagHitContainsRequestedTitlePhraseAnchor(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleAnchors = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>()),
            string.Join(' ', ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)),
            hit.SectionTitle ?? string.Empty,
            hit.HeadingPath ?? string.Empty
        }));

        if (ContainsRequestedTitlePhrase(titleAnchors, normalizedTitle))
            return true;

        var lead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        if (lead.Length > 360)
            lead = lead[..360];
        var normalizedLead = NormalizeLexicalLookup(lead);
        var leadIndex = IndexOfRequestedTitlePhrase(normalizedLead, normalizedTitle);
        if (leadIndex == 0 && TextStartsWithRequestedTitlePhrase(normalizedLead, normalizedTitle))
            return true;
        if (leadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40)
            return true;

        var contextualLead = CollapseWhitespace(StripContextualMetadataForEvidence(hit.ContextualSnippet ?? string.Empty));
        if (contextualLead.Length > 360)
            contextualLead = contextualLead[..360];
        var normalizedContextualLead = NormalizeLexicalLookup(contextualLead);
        var contextualLeadIndex = IndexOfRequestedTitlePhrase(normalizedContextualLead, normalizedTitle);
        if (contextualLeadIndex == 0 && TextStartsWithRequestedTitlePhrase(normalizedContextualLead, normalizedTitle))
            return true;
        if (contextualLeadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40)
            return true;

        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedTitle))
        {
            var normalizedVariant = NormalizeLexicalLookup(variant);
            if (ContainsRequestedTitlePhrase(titleAnchors, normalizedVariant))
            {
                return true;
            }

            leadIndex = IndexOfRequestedTitlePhrase(normalizedLead, normalizedVariant);
            if (leadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(normalizedVariant, hit) >= 40)
                return true;
        }

        return false;
    }

    private static bool TextStartsWithRequestedTitlePhrase(string normalizedText, string normalizedTitle)
        => !string.IsNullOrWhiteSpace(normalizedText)
           && !string.IsNullOrWhiteSpace(normalizedTitle)
           && (normalizedText.Equals(normalizedTitle, StringComparison.Ordinal)
               || normalizedText.StartsWith(normalizedTitle + " ", StringComparison.Ordinal));

    private static bool ContainsRequestedTitlePhrase(string haystack, string normalizedTitle)
        => IndexOfRequestedTitlePhrase(haystack, normalizedTitle) >= 0;

    private static int IndexOfRequestedTitlePhrase(string haystack, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(normalizedTitle))
            return -1;

        return haystack.IndexOf(normalizedTitle, StringComparison.Ordinal);
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

    private static string[] ExtractRequestedTitleSignalTerms(string normalizedTitle)
    {
        var normalized = NormalizeLexicalLookup(normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "and", "au", "aux", "avec", "con", "da", "das", "de", "dei", "del", "della",
            "der", "des", "di", "do", "dos", "du", "e", "el", "en", "et", "for", "il", "in", "la",
            "las", "le", "les", "lo", "of", "on", "os", "per", "pour", "sur", "the", "to", "un",
            "una", "une", "und", "y"
        };

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{N}]{1,}")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (stopWords.Contains(token))
                continue;

            if (token.Length >= 4 || token.Any(char.IsDigit))
            {
                terms.Add(token);
                continue;
            }

            var isShortDisambiguator = token.Length is 2 or 3
                && (i == tokens.Length - 1 || tokens.Length <= 3);
            if (isShortDisambiguator)
                terms.Add(token);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static bool LooksLikeNavigationOnlyHit(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return false;
        if (BackendSelectionHintsPreferNavigation(hit))
            return true;

        var text = hit.Excerpt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLexicalLookup(text);
        var hasStructuredEvidenceCue = Regex.IsMatch(
            normalized,
            @"\b(?:preparation|procedure|procedures?|process|instruction|instructions|etape|etapes|step|steps|quantity|quantite|value|valeur|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?|c|celsius))\b",
            RegexOptions.CultureInvariant);
        if (Regex.IsMatch(
                normalized,
                @"\b(?:sommaire|table des matieres|contents|index|inhaltsverzeichnis|indice|reperes de contenu|reperes contenus?|repere de contenu|sections principales|premiers extraits|table of contents|content overview)\b",
                RegexOptions.CultureInvariant))
        {
            return !hasStructuredEvidenceCue;
        }

        var leaderCount = Regex.Matches(text, @"\.{3,}\s*\d{1,4}\b", RegexOptions.CultureInvariant).Count;
        if (leaderCount >= 3)
            return true;

        var compactListCount = Regex.Matches(text, @"[\p{L}\)]\d{1,3}(?:[•\u2022]|\s*[A-Z\u00c0-\u017f])", RegexOptions.CultureInvariant).Count;
        var titlePageRefs = Regex.Matches(
            normalized,
            @"\b[\p{L}][\p{L}'\-\s]{4,48}\s+\d{1,3}\b",
            RegexOptions.CultureInvariant).Count;
        var shortTitlePageSequence = Regex.IsMatch(
            normalized,
            @"\b[\p{L}'\-]{4,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{2,3}\s+[\p{L}'\-]{4,}",
            RegexOptions.CultureInvariant);
        return (compactListCount >= 6 || titlePageRefs >= 8 || (shortTitlePageSequence && normalized.Length < 420)) && !hasStructuredEvidenceCue;
    }

    private static string BuildMissingExactItemAnswer(string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);
        if (closeLeads.Count == 0)
        {
            var fallbackCandidates = hits
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit))
                .Select(hit => new
                {
                    Hit = hit,
                    Relevance = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)),
                    Structure = ComputeExactItemCardCompletenessCueScore(hit)
                })
                .Where(item => !LooksLikePageReferenceOnlyHit(item.Hit) || item.Structure >= 4 || item.Hit.PageEnd > item.Hit.PageStart)
                .OrderByDescending(item => item.Structure >= 4)
                .ThenByDescending(item => item.Relevance)
                .Take(3)
                .Select(item => item.Hit)
                .ToList();
            closeLeads = fallbackCandidates;
        }
        else if (closeLeads.Any(LooksLikePageReferenceOnlyHit))
        {
            closeLeads = closeLeads
                .Concat(hits
                    .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                    .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                    .Where(hit => !LooksLikePageReferenceOnlyHit(hit))
                    .Where(hit => !closeLeads.Any(existing => SameRagHitRange(existing, hit))))
                .OrderBy(hit => LooksLikePageReferenceOnlyHit(hit) ? 1 : 0)
                .ThenByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(4)
                .ToList();
        }

        var typoResolutionLeads = closeLeads.Count > 0
            ? closeLeads
            : hits
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .OrderByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(4)
                .ToList();
        var correctedTitle = TryResolveTypoTolerantCloseTitle(requestedTitle, typoResolutionLeads);
        if (!string.IsNullOrWhiteSpace(correctedTitle))
        {
            var usableTypoResolutionLeads = typoResolutionLeads
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(correctedTitle!, hit))
                .ToList();
            if (usableTypoResolutionLeads.Count > 0)
            {
                return BuildSourceBackedExactItemAnswer(
                    language,
                    correctedTitle!,
                    usableTypoResolutionLeads,
                    $"{correctedTitle} details source");
            }

            closeLeads = closeLeads
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(correctedTitle!, hit))
                .ToList();
        }

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
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryResolveTypoTolerantCloseTitle(string requestedTitle, IReadOnlyList<RagHitSummary> closeLeads)
    {
        if (closeLeads.Count == 0)
            return null;

        var candidates = closeLeads
            .SelectMany(hit => ExtractSourceBackedTitleCandidates(hit).Concat(new[] { ExtractPlanItemTitleV2(GetPlanExtractionText(hit)) }))
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(title => IsUsefulSourceBackedDisplayTitle(title))
            .Where(title => !LooksLikePlanItemNoise(title))
            .Select(title => new
            {
                Title = title,
                Score = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, title)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Title.Length)
            .ToArray();

        var best = candidates.FirstOrDefault(static item => item.Score >= 8);
        if (best is null)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                var normalizedVariant = NormalizeLexicalLookup(variant);
                if (string.IsNullOrWhiteSpace(normalizedVariant))
                    continue;

                var variantTerms = ExtractRequestedTitleSignalTerms(normalizedVariant);
                if (variantTerms.Length == 0)
                    continue;

                var hasVariantEvidence = closeLeads.Any(hit =>
                {
                    var lookup = NormalizeLexicalLookup(GetRagHitLookupText(hit));
                    return variantTerms.All(term => lookup.Contains(term, StringComparison.Ordinal));
                });
                if (hasVariantEvidence)
                    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalizedVariant);
            }

            return null;
        }

        return string.Equals(NormalizeLexicalLookup(best.Title), NormalizeLexicalLookup(requestedTitle), StringComparison.Ordinal)
            ? null
            : best.Title;
    }

    private static bool LooksLikeSourceBackedPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (LooksLikeSourceBackedCountdownPlanningRequest(query))
            return false;

        var asksForPlan = LooksLikeWeeklyPlanningRequest(query)
            || Regex.IsMatch(
            s,
                @"\b(?:plan|planning|calendrier|programme|organisation|schedule|calendar|wochenplan|programm|piano|programma)\b",
                RegexOptions.CultureInvariant);

        return asksForPlan && LooksLikeSourceBackedActionRequest(query);
    }

    private static bool LooksLikeWeeklyPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|7\s+jours?|sept\s+jours?|week|weekly|semana|semanal|woche|wochen|wochenplan|settimana|settimanale)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDocumentaryPlanningRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(
            s,
            @"\b(?:semaine|hebdo|hebdomadaire|jours|journee|plan|planning|calendrier|programme|organisation|parallele|avance|preparation|preparer|week|weekly|schedule|calendar|parallel|advance|prepare|preparation|wochenplan|piano|programma)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSourceBackedOptionRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s) || LooksLikeSourceBackedPlanningRequest(query))
            return false;

        if (LooksLikeRankingDocumentaryRequest(query) || LooksLikeComparativeDocumentaryRequest(query))
            return false;

        if (!LooksLikeDocumentaryContentRequest(query) && !ExtractQuerySignalTerms(s).Any())
            return false;

        var hasNeedOrConstraintCue = Regex.IsMatch(
            s,
            @"\b(?:il\s+me\s+faut|j\s+ai\s+besoin|besoin|need|needed|necesito|preciso|brauche|serve|mi\s+serve|moins\s+de|sous|within|under|total)\b",
            RegexOptions.CultureInvariant);
        var hasCompositeCue = s.Contains('+', StringComparison.Ordinal)
            || Regex.IsMatch(
                s,
                @"\b(?:et|avec|plus|ensemble|combine|combiner|and|with|plus|together|total|complete|complet|completa|completo)\b",
                RegexOptions.CultureInvariant);
        if (hasNeedOrConstraintCue && hasCompositeCue)
            return true;

        if (Regex.IsMatch(s, @"\b(?:plan|planning|liste|list|options?|suggestions?|selection|sélection)\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(s, @"\b(?:complet|complete|sans|without|pas\s+de|uniquement|only|avec|with|pour|for|autour|around|total)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"\b(?:complet|complete|completa|completo|total)\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(s, @"\b(?:sans|without|pas\s+de|excluding|exclude|sauf|except|uniquement|only|avec|with)\b", RegexOptions.CultureInvariant)
            && LooksLikeSourceBackedActionRequest(query))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"\b(?:fais|faire|compose|composer|cree|creer|prepare|preparer|make|compose|create|prepare)\b.{0,80}\b(?:plan|planning|liste|list|options?|suggestions?)\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(s, @"\b(?:fais|faire|compose|composer|cree|creer|prepare|preparer|make|compose|create|prepare)\b.{0,100}\b(?:composition|selection|sélection)\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(s, @"\b(?:compose|composer|cree|creer|prepare|preparer|make|create|prepare)\b.{0,100}\b(?:options?|idees?|ideas?|items?|elements?|liste|list|plan|planning)\b", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            s,
            @"\b(?:propose|proposes|idee|idees|quoi|choisis|choisir|option|options|composition|suggest|suggestion|suggestions|which|what|cual|qual|welche|quale)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSourceBackedPairingRecommendationRequest(string? query)
    {
        var s = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var hasPairingCue = Regex.IsMatch(
            s,
            @"\b(?:irait\s+bien|va\s+bien|vont\s+bien|avec|accompagne|accompagner|associe|associer|compatible|compatibles|goes?\s+with|pair(?:ing)?|pairs?\s+with|compatible|recommends?|recommendation|con|acompanha|acompanhar|combina|combinar|passt\s+zu|kombinieren|abbinare|abbina|si\s+abbina)\b",
            RegexOptions.CultureInvariant);
        if (!hasPairingCue)
            return false;

        return Regex.IsMatch(
            s,
            @"\b(?:quel|quelle|quels|quelles|quoi|which|what|cual|cu[aá]l|qual|welche|welcher|welches|quale|propose|proposes|suggest|recommend|recommande|recommander|conseille|conseiller)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldUseSourceBackedOptionAnswer(string? requestedItemTitle, string query)
        => LooksLikeSourceBackedOptionRequest(query)
            && (string.IsNullOrWhiteSpace(requestedItemTitle)
                || LooksLikeBroadSourceBackedCompositionRequest(query));

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
            @"\b(?:apres|après|precedent|pr[eé]c[eé]dent|document|source|element|item|mets|mettre|adapte|adapter|pour|after|previous|put|scale|adjust|adapt)\b|\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}",
            RegexOptions.CultureInvariant);
    }

    private static string BuildUnresolvedSourceBackedDeicticFollowupAnswer(string language, string query)
    {
        language = NormalizeLanguageCode(language);
        var target = TryExtractTargetScaleCount(query, out var count)
            ? language switch
            {
                "en" => $" for {count}",
                "es" => $" para {count}",
                "pt" => $" para {count}",
                "de" => $" fuer {count}",
                "it" => $" per {count}",
                _ => $" pour {count}"
            }
            : string.Empty;
        return SourceBackedLabel(
            language,
            $"Je n'ai pas d'element precedent exploitable dans cette conversation. Donne-moi l'element, le document ou la source concernee, puis je pourrai l'adapter ou la citer{target} sans inventer.",
            $"I do not have an exploitable previous item in this conversation. Give me the item, document, or source involved, then I can adapt or cite it{target} without inventing details.",
            $"No tengo un elemento anterior explotable en esta conversacion. Dame el elemento, el documento o la fuente correspondiente y podre adaptarlo o citarlo{target} sin inventar detalles.",
            $"Nao tenho um elemento anterior utilizavel nesta conversa. Da-me o item, o documento ou a fonte em causa e poderei adapta-lo ou cita-lo{target} sem inventar detalhes.",
            $"Ich habe in dieser Unterhaltung kein nutzbares vorheriges Element. Gib mir den Eintrag, das Dokument oder die betroffene Quelle, dann kann ich ihn{target} anpassen oder zitieren, ohne Details zu erfinden.",
            $"Non ho un elemento precedente utilizzabile in questa conversazione. Dammi l'elemento, il documento o la fonte interessata e potro adattarlo o citarlo{target} senza inventare dettagli.");
    }

    private static bool LooksLikeDocumentaryContentRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
            return true;

        var mentionsCommonContentObject = Regex.IsMatch(
            normalized,
            @"\b(?:fiche|card|cards|document|documents|source|sources|element|elements|item|items|objet|objets|sujet|sujets|topic|topics|procedure|procedures|process|processus|method|methods|methode|methodes|notice|notices|instruction|instructions)\b",
            RegexOptions.CultureInvariant);
        if (mentionsCommonContentObject)
        {
            var hasCommonDirectiveNounPhrase = Regex.IsMatch(
                normalized,
                @"^(?:(?:un|une|des|du|de\s+la|the|a|an|some)\s+)?(?:fiche|card|document|source|element|item|objet|sujet|topic|procedure|process|method|methode|notice|instruction)\b",
                RegexOptions.CultureInvariant);
            var hasCommonRequestOperator = Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|quels|quelles|quoi|peux|pourrais|donne|donner|trouve|trouver|cherche|chercher|fais|faire|faut|besoin|veux|voudrais|souhaite|aimerais|liste|lister|propose|proposer|existe|existent|which|what|can|could|give|find|search|make|need|want|list|suggest|exists|exist)\b",
                RegexOptions.CultureInvariant);
            var mentionsCommonDocumentarySource = Regex.IsMatch(
                normalized,
                @"\b(?:pdf|document|documents|doc|docs|source|sources|base|connaissance|knowledge|corpus|file|files)\b",
                RegexOptions.CultureInvariant);

            if (hasCommonDirectiveNounPhrase || hasCommonRequestOperator || mentionsCommonDocumentarySource)
                return true;
        }

        var mentionsDocumentarySource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|extrait|extraits|page|pages|base|connaissance|knowledge|corpus|file|files|archivo|archivos|documento|documentos|fonte|fontes|quelle|quellen|dokument|dokumente|documento|documenti|fonte|fonti)\b",
            RegexOptions.CultureInvariant);
        var mentionsContentObject = Regex.IsMatch(
            normalized,
            @"\b(?:element|elements|item|items|objet|objets|sujet|sujets|topic|topics|section|sections|extrait|extraits|passage|passages|clause|clauses|table|tables|fiche|card|checklist|liste|list|plan|planning|procedure|proc[eé]dure|procedimento|procedura|process|processus|method|methode|instruction|instructions|etape|etapes|[eé]tape|[eé]tapes|step|steps|quantite|quantites|quantit[eé]|quantit[eé]s|amount|amounts|valeur|valeurs|value|values|temps|time|duration|duree|durees)\b",
            RegexOptions.CultureInvariant);
        var hasRequestOperator = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|quoi|qu est|qu est ce|peux|pourrais|donne|donner|trouve|trouver|cherche|chercher|fais|faire|faut|besoin|veux|voudrais|souhaite|aimerais|compare|comparer|liste|lister|resume|resumer|reponds|repondre|r[eé]ponds|r[eé]pondre|adapte|adapter|transforme|transformer|traduis|traduire|rends|rendre|existe|existent|which|what|can|could|give|find|search|make|need|want|compare|list|summarize|respond|answer|adapt|adjust|transform|translate|render|exists|exist|cual|cu[aá]l|que|puedes|podrias|dame|busca|encuentra|necesito|responde|traduce|existe|quero|pode|podes|procura|encontra|preciso|responde|traduz|existe|welche|was|kannst|suche|finde|brauche|antworte|uebersetze|ubersetze|existiert|quale|cosa|puoi|cerca|trova|bisogno|rispondi|traduci|esiste)\b",
            RegexOptions.CultureInvariant);
        var hasDirectiveNounPhrase = Regex.IsMatch(
            normalized,
            @"^(?:(?:un|une|des|du|de\s+la|the|a|an|some)\s+)?(?:liste|list|checklist|fiche|card|plan|planning|procedure|process|section|table)\b",
            RegexOptions.CultureInvariant);

        if (mentionsDocumentarySource && (mentionsContentObject || ExtractQuerySignalTerms(normalized).Any()))
            return true;

        if (LooksLikeShortStandaloneContentLookup(normalized))
            return true;

        return mentionsContentObject && (hasRequestOperator || hasDirectiveNounPhrase);
    }

    private static bool LooksLikeShortStandaloneContentLookup(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:bonjour|salut|hello|merci|thanks?|pourquoi|why|comment\s+ca\s+va|how\s+are\s+you)\b", RegexOptions.CultureInvariant))
            return false;

        var words = Regex.Matches(normalized, @"[\p{L}\p{N}]{2,}")
            .Select(match => match.Value)
            .ToArray();
        if (words.Length is < 2 or > 12)
            return false;

        var hasContentShape = Regex.IsMatch(
                normalized,
                @"^(?:un|une|des|du|de\s+la|the|a|an|some)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:options?|suggestions?|idees?|ideas?|classique|classic|simple|rapide|quick|technique|technical)\b",
                RegexOptions.CultureInvariant);

        return hasContentShape && ExtractQuerySignalTerms(normalized).Any();
    }

    private static string[] BuildPlanningRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = query;

        var raw = CollapseWhitespace(query);
        var queries = new List<string>();
        var requestedTitle = TryExtractRequestedItemTitle(query);
        var normalizedLookup = NormalizeLooseLookup(normalized);
        var signalTerms = ExtractPlanningRetrievalTerms(normalizedLookup)
            .Where(static term => term.Length >= 4)
            .Take(5)
            .ToArray();

        AddDistinctQuery(queries, normalized);
        if (!string.IsNullOrWhiteSpace(raw)
            && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(requestedTitle) || signalTerms.Length > 0))
        {
            AddDistinctQuery(queries, raw);
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            AddDistinctQuery(queries, requestedTitle);
            AddDistinctQuery(queries, $"\"{requestedTitle}\"");
        }

        if (signalTerms.Length > 0)
        {
            var signalQuery = string.Join(' ', signalTerms);
            AddDistinctQuery(queries, signalQuery);
            AddDistinctQuery(queries, signalQuery + " options");
            AddDistinctQuery(queries, signalQuery + " examples");
            AddDistinctQuery(queries, signalQuery + " suggestions");
            AddDistinctQuery(queries, signalQuery + " ideas");
            AddDistinctQuery(queries, signalQuery + " sources");
        }

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static IEnumerable<string> ExtractPlanningRetrievalTerms(string normalizedQuery)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "comment", "dans", "faire", "facile", "idee",
            "peux", "pour", "propose", "proposes", "quoi", "sais", "vais", "veux", "voudrais",
            "about", "find", "help", "make", "prepare", "recommend", "suggest", "what", "with",
            "can", "could", "give", "need", "want", "ayuda", "ayudar", "ayudame", "puede", "puedes",
            "podrias", "propone", "recomienda", "ajuda", "ajudar", "pode", "podes", "recomenda",
            "kannst", "konntest", "helfen", "vorschlag", "empfiehl", "aiuta", "aiutami", "puoi",
            "consiglia"
        };

        return Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]{4,}")
            .Select(m => m.Value)
            .Where(term => !stopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8);
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

        var raw = CollapseWhitespace(query);
        var queries = new List<string>();
        AddDistinctQuery(queries, normalized);
        if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
            AddDistinctQuery(queries, raw);

        foreach (var variant in BuildTypoTolerantQueryVariants(normalized))
            AddDistinctQuery(queries, variant);

        var normalizedLookup = NormalizeLexicalLookup(normalized);
        var termSeeds = new List<string> { normalizedLookup };
        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedLookup))
            termSeeds.Add(NormalizeLexicalLookup(variant));

        var terms = termSeeds
            .SelectMany(ExtractQuerySignalTerms)
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

        foreach (var term in subjectTerms.Take(6))
            AddDistinctQuery(queries, term);

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
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

    private static IEnumerable<string> BuildTypoTolerantQueryVariants(string? query)
    {
        var normalized = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var variants = new List<string>();
        void Add(string value)
        {
            value = CollapseWhitespace(value);
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            if (!variants.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                variants.Add(value);
        }

        Add(Regex.Replace(normalized, @"(?i)ngn", "gn", RegexOptions.CultureInvariant));
        Add(Regex.Replace(normalized, @"(?i)\b([\p{L}]{3,})tt([\p{L}]{2,})\b", "$1t$2", RegexOptions.CultureInvariant));
        Add(Regex.Replace(normalized, @"(?i)ze\b", "se", RegexOptions.CultureInvariant));

        foreach (var variant in variants.Take(3))
            yield return variant;
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
            "document", "documents", "adapt", "adapted", "derived", "please", "from", "retrouve", "retrouver", "retrouves"
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

        var raw = CollapseWhitespace(query);
        var queries = new List<string>();
        var focus = TryBuildComparativeFocusQuery(normalized);
        AddDistinctQuery(queries, focus);
        AddDistinctQuery(queries, normalized);
        if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
            AddDistinctQuery(queries, raw);

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

    private static string BuildSourceBackedPlanningAnswer(ToolResults toolResults, string language, int minItems = 1, string? query = null)
    {
        language = NormalizeLanguageCode(language);
        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var wantsWeeklyPlan = LooksLikeWeeklyPlanningRequest(query);
        var planItems = SelectSourceBackedPlanningCandidates(toolResults, query, targetItemCount, language)
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
        if (wantsWeeklyPlan && planItems.Count < targetItemCount)
        {
            var coverageNote = language switch
            {
                "en" => $"I found only {planItems.Count} reliable source-backed items for the requested weekly plan, so I present a partial plan instead of filling missing days by guessing.",
                "es" => $"He encontrado solo {planItems.Count} elementos fiables con fuente para el plan semanal solicitado, asi que doy un plan parcial en vez de completar dias con suposiciones.",
                "pt" => $"Encontrei apenas {planItems.Count} itens fiaveis com fonte para o plano semanal pedido, por isso apresento um plano parcial em vez de preencher dias por suposicao.",
                "de" => $"Ich habe nur {planItems.Count} verlaessliche quellenbasierte Eintraege fuer den Wochenplan gefunden und liefere daher einen Teilplan, statt fehlende Tage zu erraten.",
                "it" => $"Ho trovato solo {planItems.Count} elementi affidabili con fonte per il piano settimanale richiesto, quindi propongo un piano parziale invece di completare i giorni per supposizione.",
                _ => $"J'ai trouve seulement {planItems.Count} elements fiables et sources pour le planning hebdomadaire demande ; je propose donc un plan partiel au lieu de completer les jours en inventant."
            };
            sb.AppendLine(coverageNote);
        }

        var useDayLabels = wantsWeeklyPlan && planItems.Count >= targetItemCount;
        for (var i = 0; i < planItems.Count; i++)
        {
            var label = useDayLabels
                ? language switch
            {
                "en" => $"Day {i + 1}",
                "es" => $"Dia {i + 1}",
                "pt" => $"Dia {i + 1}",
                "de" => $"Tag {i + 1}",
                "it" => $"Giorno {i + 1}",
                _ => $"Jour {i + 1}"
            }
                : language switch
            {
                "es" => $"Opcion {i + 1}",
                "pt" => $"Opcao {i + 1}",
                "it" => $"Opzione {i + 1}",
                _ => $"Option {i + 1}"
            };

            var hit = planItems[i].Hit;
            sb.Append("- ");
            sb.Append(label);
            sb.Append(" : ");
            sb.Append(planItems[i].Title);
            sb.Append(" (");
            sb.Append(string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(")");
            sb.AppendLine();
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

    private static bool LooksLikeUnderusedSourceBackedPlanningAnswer(string? answer, ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || !LooksLikeSourceBackedPlanningRequest(query)
            || LooksLikeWeeklyPlanningRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query))
        {
            return false;
        }

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        var candidates = SelectSourceBackedPlanningCandidates(toolResults, query, targetItemCount)
            .Take(Math.Min(5, targetItemCount))
            .ToList();
        if (candidates.Count < 2)
            return false;

        var normalizedAnswer = NormalizeLexicalLookup(answer);
        var mentionedCandidates = candidates.Count(candidate => SourceBackedPlanningCandidateMentioned(normalizedAnswer, candidate));
        if (mentionedCandidates >= Math.Min(2, candidates.Count))
            return false;

        var sourceLinkCount = Regex.Matches(answer, @"\[\[open\|", RegexOptions.CultureInvariant).Count;
        if (sourceLinkCount >= Math.Min(2, candidates.Count))
            return false;

        var bulletLikeLineCount = Regex.Matches(
            answer,
            @"(?m)^\s*(?:[-*]|\d+[.)]|(?:jour|day|dia|tag|giorno|option|opcion|opcao|opzione)\s+\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

        return bulletLikeLineCount < Math.Min(2, candidates.Count);
    }

    private static bool SourceBackedPlanningCandidateMentioned(string normalizedAnswer, SourceBackedOptionCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        var title = NormalizeLexicalLookup(candidate.Title);
        if (!string.IsNullOrWhiteSpace(title) && title.Length >= 4 && normalizedAnswer.Contains(title, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractQuerySignalTerms(title)
            .Where(static term => term.Length >= 5)
            .Take(4)
            .ToArray();
        if (titleTerms.Length >= 2 && titleTerms.Count(term => normalizedAnswer.Contains(term, StringComparison.Ordinal)) >= 2)
            return true;

        var sourceLabel = NormalizeLexicalLookup(candidate.Hit.DocName ?? candidate.Hit.DocPath);
        if (string.IsNullOrWhiteSpace(sourceLabel))
            return false;

        return normalizedAnswer.Contains(sourceLabel, StringComparison.Ordinal);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedPlanningCandidates(ToolResults toolResults, string? query, int maxItems, string language = "")
    {
        var candidates = SelectSourceBackedOptionCandidates(toolResults, query, keepOverRequestedDuration: true, language: language)
            .Where(candidate => !LooksLikePageReferenceOnlyHit(candidate.Hit))
            .Where(IsUsableSourceBackedPlanningCandidate)
            .Where(candidate => string.IsNullOrWhiteSpace(query) || candidate.Score > 0)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(maxItems)
            .ToList();

        if (candidates.Count > 0)
            return candidates;

        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        return sourceHits
            .Select(hit =>
            {
                var title = ExtractSourceBackedOptionTitle(hit, query);
                var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                return new SourceBackedOptionCandidate(
                    hit,
                    title,
                    ComputeSourceBackedOptionHitScore(hit, title, query, requestedMaxMinutes: null, visibleMinutes),
                    visibleMinutes);
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Where(IsUsableSourceBackedPlanningCandidate)
            .Where(candidate => string.IsNullOrWhiteSpace(query) || candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(maxItems)
            .ToList();
    }

    private static bool IsUsableSourceBackedPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || LooksLikePlanItemNoise(candidate.Title)
            || LooksLikeProcedureSentenceTitle(normalizedTitle))
        {
            return false;
        }

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .ToArray();
        if (titleTerms.Length == 0)
            return false;

        return true;
    }

    private static int ResolveSourceBackedPlanningTargetItemCount(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var explicitCount = Regex.Match(
                normalized,
                @"\b(?<n>\d{1,2})\s+(?:jours?|days?|items?|elements?|options?|etapes?|steps?)\b",
                RegexOptions.CultureInvariant);
            if (explicitCount.Success
                && int.TryParse(explicitCount.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && count is >= 1 and <= 14)
            {
                return count;
            }
        }

        return LooksLikeWeeklyPlanningRequest(query) ? 7 : 5;
    }

    private sealed record SourceBackedOptionCandidate(RagHitSummary Hit, string Title, int Score, int? VisibleMinutes);

    private sealed record SourceBackedOptionAnswerSelection(
        IReadOnlyList<SourceBackedOptionCandidate> Items,
        bool WantsTotalPairing,
        bool HasCertifiedTotalPair,
        int? RequestedMaxMinutes,
        int? VisibleTotalMinutes);

    private sealed record SourceBackedCountdownCandidate(RagHitSummary Hit, string Title, int Score, int? VisibleMinutes);

    private static bool LooksLikeSourceBackedCountdownPlanningRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksCountdown = Regex.IsMatch(
            normalized,
            @"\b(?:retroplanning|rebours|a\s+rebours|countdown|reverse\s+plan|backward\s+plan|planning\s+inverse|plan\s+inverse)\b",
            RegexOptions.CultureInvariant);
        var asksSchedule = Regex.IsMatch(
            normalized,
            @"\b(?:planning|plan|programme|horaire|schedule)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                normalized,
                @"\b(?:preparation|preparer|procedure|process|operation|service|livraison|deliver|deadline|echeance|échéance|pret|prete|ready|start|finish|end)\b",
                RegexOptions.CultureInvariant);

        return (asksCountdown || asksSchedule)
            && (TryExtractRequestedClockTime(query).HasValue
                || Regex.IsMatch(normalized, @"\b\d{1,2}\s*h(?:\s*\d{2})?\b", RegexOptions.CultureInvariant));
    }

    private static string BuildSourceBackedCountdownPlanningAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var targetTime = TryExtractRequestedClockTime(query);
        if (!targetTime.HasValue)
            return string.Empty;

        var candidates = SelectSourceBackedCountdownPlanningCandidates(toolResults, query)
            .Take(6)
            .ToList();
        if (candidates.Count == 0)
            return string.Empty;

        var timed = candidates
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(candidate => candidate.VisibleMinutes!.Value)
            .ThenByDescending(candidate => candidate.Score)
            .Take(5)
            .ToList();
        if (timed.Count == 0)
            return string.Empty;

        var labels = language switch
        {
            "en" => (
                Header: "I do not have one selected item, so this reverse schedule only uses durations visible in the source excerpts:",
                Start: "start",
                Duration: "visible duration",
                Manual: "to place manually",
                Note: "If you choose the exact source items, I can rebuild a stricter schedule from those source pages only."),
            "es" => (
                Header: "No tengo un elemento unico seleccionado, asi que este plan inverso usa solo duraciones visibles en los extractos fuente:",
                Start: "iniciar",
                Duration: "duracion visible",
                Manual: "colocar manualmente",
                Note: "Si eliges los elementos exactos, puedo rehacer un planning mas estricto solo con esas paginas fuente."),
            "pt" => (
                Header: "Nao tenho um item unico selecionado, por isso este plano inverso usa apenas duracoes visiveis nos excertos fonte:",
                Start: "iniciar",
                Duration: "duracao visivel",
                Manual: "colocar manualmente",
                Note: "Se escolheres os itens exatos, posso refazer um plano mais rigoroso apenas com essas paginas fonte."),
            "de" => (
                Header: "Es ist kein einzelnes Element ausgewaehlt; dieser Rueckwaertsplan nutzt daher nur sichtbare Dauern aus den Quellenauszuegen:",
                Start: "starten",
                Duration: "sichtbare Dauer",
                Manual: "manuell einplanen",
                Note: "Wenn du die genauen Elemente auswaehlst, kann ich den Plan nur mit diesen Quellseiten strenger neu berechnen."),
            "it" => (
                Header: "Non ho un elemento unico selezionato, quindi questo piano a ritroso usa solo durate visibili negli estratti fonte:",
                Start: "avviare",
                Duration: "durata visibile",
                Manual: "da collocare manualmente",
                Note: "Se scegli gli elementi esatti, posso rifare un piano piu rigoroso solo da quelle pagine fonte."),
            _ => (
                Header: "Je n'ai pas un element unique selectionne ; ce retroplanning utilise donc uniquement les durees visibles dans les extraits sources :",
                Start: "lancer",
                Duration: "duree visible",
                Manual: "a caler manuellement",
                Note: "Si tu choisis les elements source exacts, je peux refaire un planning plus strict uniquement depuis ces pages source.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        foreach (var candidate in timed)
        {
            var startTime = targetTime.Value.Subtract(TimeSpan.FromMinutes(candidate.VisibleMinutes!.Value));
            while (startTime < TimeSpan.Zero)
                startTime += TimeSpan.FromDays(1);

            var hit = candidate.Hit;
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(FormatClockTime(startTime));
            sb.Append(" : ");
            sb.Append(labels.Start);
            sb.Append(' ');
            sb.Append(candidate.Title);
            sb.Append(" (");
            sb.Append(labels.Duration);
            sb.Append(" : ");
            sb.Append(candidate.VisibleMinutes.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append(" min, ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.AppendLine(")");
        }

        sb.AppendLine(labels.Note);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<SourceBackedCountdownCandidate> SelectSourceBackedCountdownPlanningCandidates(ToolResults toolResults, string? query)
    {
        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        return sourceHits
            .Select(hit =>
            {
                var title = ExtractSourceBackedOptionTitle(hit, query);
                if (string.IsNullOrWhiteSpace(title))
                    title = BuildCountdownFallbackTitle(hit);

                var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                var score = ComputeRagHitLexicalRelevance(query ?? string.Empty, GetRagHitLookupText(hit))
                    + ComputeProcedureCompletenessCueScore(hit)
                    + ComputeStructuredProcedureEvidenceCueScore(hit)
                    + ComputeStructuredProcedureVisibleEvidenceCueScore(hit)
                    + (visibleMinutes.HasValue ? 18 : 0);
                if (LooksLikePageReferenceOnlyHit(hit))
                    score -= 25;
                if (LooksLikeMidProcedureFragment(hit))
                    score -= 6;

                return new SourceBackedCountdownCandidate(hit, title, score, visibleMinutes);
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.VisibleMinutes.HasValue)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string BuildCountdownFallbackTitle(RagHitSummary hit)
    {
        foreach (var raw in new[] { hit.SectionTitle, hit.HeadingPath })
        {
            var title = CleanSourceBackedOptionTitle(raw);
            if (!string.IsNullOrWhiteSpace(title)
                && IsUsefulSourceBackedDisplayTitle(title)
                && !LooksLikePlanItemNoise(title))
            {
                return title;
            }
        }

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return string.IsNullOrWhiteSpace(docLabel) ? "Element source" : docLabel;
    }

    private static TimeSpan? TryExtractRequestedClockTime(string? query)
    {
        var text = query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var pattern in new[]
                 {
                     @"\b(?<h>[01]?\d|2[0-3])\s*h(?:\s*(?<m>[0-5]\d))?\b",
                     @"\b(?<h>[01]?\d|2[0-3])\s*[:.]\s*(?<m>[0-5]\d)\b",
                     @"\b(?<h>0?[1-9]|1[0-2])\s*(?<ampm>am|pm)\b"
                 })
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success
                ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            if (match.Groups["ampm"].Success)
            {
                var ampm = match.Groups["ampm"].Value.ToLowerInvariant();
                if (ampm == "pm" && hours < 12)
                    hours += 12;
                if (ampm == "am" && hours == 12)
                    hours = 0;
            }

            if (hours is >= 0 and <= 23 && minutes is >= 0 and <= 59)
                return new TimeSpan(hours, minutes, 0);
        }

        var normalized = NormalizeLexicalLookup(text);
        var looseMatch = Regex.Match(
            normalized,
            @"\b(?<h>[01]?\d|2[0-3])\s*h(?:\s*(?<m>[0-5]\d))?\b",
            RegexOptions.CultureInvariant);
        if (looseMatch.Success)
        {
            var hours = int.Parse(looseMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = looseMatch.Groups["m"].Success
                ? int.Parse(looseMatch.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            if (hours is >= 0 and <= 23 && minutes is >= 0 and <= 59)
                return new TimeSpan(hours, minutes, 0);
        }

        return null;
    }

    private static string FormatClockTime(TimeSpan value)
    {
        value = new TimeSpan((value.Hours + 24) % 24, value.Minutes, 0);
        return $"{value.Hours:00}h{value.Minutes:00}";
    }

    private static string BuildSourceBackedOptionAnswer(ToolResults toolResults, string language, int minItems = 1, string? query = null)
    {
        language = NormalizeLanguageCode(language);
        var missingPairingAnchor = TryBuildMissingPairingAnchorAnswer(toolResults, query ?? string.Empty, language);
        if (!string.IsNullOrWhiteSpace(missingPairingAnchor))
            return missingPairingAnchor;

        var selection = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems, language);
        var optionItems = selection.Items.ToList();
        var requestedMaxMinutes = selection.RequestedMaxMinutes;
        var wantsTotalPairing = selection.WantsTotalPairing;

        if (optionItems.Count == 0 || optionItems.Count < minItems)
            return string.Empty;

        var header = language switch
        {
            "en" => "Here are source-backed options found in the available documents:",
            "es" => "Aqui tienes opciones basadas en los documentos disponibles:",
            "pt" => "Aqui estao opcoes baseadas nos documentos disponiveis:",
            "de" => "Hier sind quellenbasierte Optionen aus den verfuegbaren Dokumenten:",
            "it" => "Ecco opzioni basate sui documenti disponibili:",
            _ => "Voici des options appuyees sur les documents disponibles :"
        };

        var sb = new StringBuilder();
        sb.AppendLine(header);
        if (ShouldWarnNoExplicitPairing(query ?? string.Empty, optionItems.Select(static item => item.Hit).ToList()))
        {
            var pairingCaveat = language switch
            {
                "en" => "I did not find a passage that explicitly connects every part of the request, so I list these as source-backed leads, not as certified compatible recommendations.",
                "es" => "No he encontrado un pasaje que conecte explicitamente todas las partes de la solicitud; las enumero como pistas con fuente, no como recomendaciones compatibles certificadas.",
                "pt" => "Nao encontrei uma passagem que ligue explicitamente todas as partes do pedido; listo-as como pistas com fonte, nao como recomendacoes compativeis certificadas.",
                "de" => "Ich habe keine Stelle gefunden, die alle Teile der Anfrage ausdruecklich verbindet; ich liste sie daher als belegte Hinweise, nicht als bestaetigte kompatible Empfehlungen.",
                "it" => "Non ho trovato un passaggio che colleghi esplicitamente tutte le parti della richiesta; le elenco quindi come indicazioni con fonte, non come raccomandazioni compatibili certificate.",
                _ => "Je n'ai pas trouve de passage qui relie explicitement tous les elements de la demande ; je liste donc ces elements comme pistes sourcees, pas comme recommandations compatibles certifiees."
            };
            sb.AppendLine(pairingCaveat);
        }

        if (wantsTotalPairing)
        {
            var requestedMaxMinutesValue = requestedMaxMinutes.GetValueOrDefault();
            var visibleCandidateTotal = !selection.HasCertifiedTotalPair
                && optionItems.Count >= 2
                && optionItems.All(static candidate => candidate.VisibleMinutes.HasValue)
                    ? optionItems.Sum(static candidate => candidate.VisibleMinutes!.Value)
                    : (int?)null;
            var pairingNote = (selection.HasCertifiedTotalPair, language) switch
            {
                (true, "en") => $"The visible durations fit the constraint: {selection.VisibleTotalMinutes} minutes total, within the requested {requestedMaxMinutesValue} minutes.",
                (true, "es") => $"Las duraciones visibles cumplen la restriccion: {selection.VisibleTotalMinutes} minutos en total, dentro de los {requestedMaxMinutesValue} minutos pedidos.",
                (true, "pt") => $"As duracoes visiveis respeitam a restricao: {selection.VisibleTotalMinutes} minutos no total, dentro dos {requestedMaxMinutesValue} minutos pedidos.",
                (true, "de") => $"Die sichtbaren Dauern erfuellen die Vorgabe: insgesamt {selection.VisibleTotalMinutes} Minuten, innerhalb der gewuenschten {requestedMaxMinutesValue} Minuten.",
                (true, "it") => $"Le durate visibili rispettano il vincolo: {selection.VisibleTotalMinutes} minuti totali, entro i {requestedMaxMinutesValue} minuti richiesti.",
                (true, _) => $"Les durees visibles respectent la contrainte : {selection.VisibleTotalMinutes} minutes au total, dans la limite demandee de {requestedMaxMinutesValue} minutes.",
                (false, "en") when visibleCandidateTotal.HasValue => $"I cannot certify the requested combined total under {requestedMaxMinutesValue} minutes: the visible total for the retained items is {visibleCandidateTotal.Value} minutes. I list them as source-backed leads, not as a compatible set.",
                (false, "es") when visibleCandidateTotal.HasValue => $"No puedo certificar el total combinado pedido en menos de {requestedMaxMinutesValue} minutos: el total visible de los elementos retenidos es {visibleCandidateTotal.Value} minutos. Los enumero como pistas con fuente, no como conjunto compatible.",
                (false, "pt") when visibleCandidateTotal.HasValue => $"Nao posso certificar o total combinado pedido em menos de {requestedMaxMinutesValue} minutos: o total visivel dos itens retidos e {visibleCandidateTotal.Value} minutos. Listo-os como pistas com fonte, nao como conjunto compativel.",
                (false, "de") when visibleCandidateTotal.HasValue => $"Ich kann die angefragte kombinierte Summe unter {requestedMaxMinutesValue} Minuten nicht bestaetigen: die sichtbare Summe der behaltenen Eintraege betraegt {visibleCandidateTotal.Value} Minuten. Ich liste sie als belegte Hinweise, nicht als kompatibles Set.",
                (false, "it") when visibleCandidateTotal.HasValue => $"Non posso certificare il totale combinato richiesto sotto {requestedMaxMinutesValue} minuti: il totale visibile degli elementi mantenuti e {visibleCandidateTotal.Value} minuti. Li elenco come piste con fonte, non come insieme compatibile.",
                (false, _) when visibleCandidateTotal.HasValue => $"Je ne peux pas certifier le total combine demande en moins de {requestedMaxMinutesValue} minutes : le total visible des elements retenus est de {visibleCandidateTotal.Value} minutes. Je les liste comme pistes sourcees, pas comme ensemble compatible.",
                (false, "en") => $"I do not have enough complete visible durations to guarantee the requested combined total under {requestedMaxMinutesValue} minutes; I only list source-backed components.",
                (false, "es") => $"No tengo suficientes duraciones completas visibles para garantizar el total combinado pedido en menos de {requestedMaxMinutesValue} minutos; solo enumero componentes con fuente.",
                (false, "pt") => $"Nao tenho duracoes completas visiveis suficientes para garantir o total combinado pedido em menos de {requestedMaxMinutesValue} minutos; listo apenas componentes com fonte.",
                (false, "de") => $"Ich habe nicht genug vollstaendige sichtbare Dauern, um die angefragte kombinierte Summe unter {requestedMaxMinutesValue} Minuten zu garantieren; ich liste nur belegte Bestandteile.",
                (false, "it") => $"Non ho durate complete visibili sufficienti per garantire il totale combinato richiesto sotto {requestedMaxMinutesValue} minuti; elenco solo componenti con fonte.",
                _ => $"Je n'ai pas assez de durees completes visibles pour garantir le total combine demande en moins de {requestedMaxMinutesValue} minutes ; je liste seulement des composants sources."
            };
            sb.AppendLine(pairingNote);
        }

        for (var i = 0; i < optionItems.Count; i++)
        {
            var hit = optionItems[i].Hit;
            var optionLabel = language switch
            {
                "es" => $"Opcion {i + 1}",
                "pt" => $"Opcao {i + 1}",
                "it" => $"Opzione {i + 1}",
                _ => $"Option {i + 1}"
            };

            sb.Append("- ");
            sb.Append(optionLabel);
            sb.Append(" : ");
            sb.Append(optionItems[i].Title);
            var visibleMinutes = optionItems[i].VisibleMinutes;
            if (visibleMinutes.HasValue)
            {
                sb.Append(" - ");
                sb.Append(visibleMinutes.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append(' ');
                sb.Append(SourceBackedVisibleMinutesSuffix(language));
            }
            sb.Append(" (");
            sb.Append(string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(")");

            var evidence = FormatSourceBackedOptionEvidence(hit);
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                sb.Append(" - ");
                sb.Append(evidence);
            }

            sb.AppendLine();
        }

        var note = language switch
        {
            "en" => "I keep quantities, timing, and substitutions tied to the source pages.",
            "es" => "Mantengo cantidades, tiempos y sustituciones ligados a las paginas fuente.",
            "pt" => "Mantenho quantidades, tempos e substituicoes ligados as paginas fonte.",
            "de" => "Mengen, Zeiten und Ersetzungen bleiben an die Quellseiten gebunden.",
            "it" => "Tengo quantita, tempi e sostituzioni legati alle pagine fonte.",
            _ => "Je garde les quantites, temps et substitutions rattaches aux pages source."
        };
        sb.AppendLine(note);

        return sb.ToString().TrimEnd();
    }

    private static SourceBackedOptionAnswerSelection SelectSourceBackedOptionAnswerCandidates(
        ToolResults toolResults,
        string? query,
        int minItems,
        string language = "")
    {
        var allOptionItems = SelectSourceBackedOptionCandidates(toolResults, query, keepOverRequestedDuration: true, language: language)
            .ToList();
        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        var wantsTotalPairing = LooksLikeTotalDurationConstraintRequest(query, requestedMaxMinutes);

        if (!wantsTotalPairing)
        {
            return new SourceBackedOptionAnswerSelection(
                allOptionItems.Take(5).ToList(),
                WantsTotalPairing: false,
                HasCertifiedTotalPair: false,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: null);
        }

        var timedItems = allOptionItems
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .Where(candidate => candidate.VisibleMinutes!.Value <= requestedMaxMinutes!.Value)
            .ToList();
        var requiredItems = wantsTotalPairing ? Math.Max(2, minItems) : Math.Max(1, minItems);
        var combined = SelectSourceBackedCombinedDurationSet(timedItems, requiredItems, requestedMaxMinutes!.Value);
        if (combined.Count >= requiredItems)
        {
            return new SourceBackedOptionAnswerSelection(
                combined,
                WantsTotalPairing: true,
                HasCertifiedTotalPair: true,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: combined.Sum(static candidate => candidate.VisibleMinutes!.Value));
        }

        var fallbackSet = allOptionItems.Take(requiredItems).ToList();
        if (fallbackSet.Count >= requiredItems)
        {
            return new SourceBackedOptionAnswerSelection(
                fallbackSet,
                WantsTotalPairing: true,
                HasCertifiedTotalPair: false,
                RequestedMaxMinutes: requestedMaxMinutes,
                VisibleTotalMinutes: null);
        }

        var fallbackItems = timedItems.Count >= requiredItems
            ? timedItems.Take(5).ToList()
            : allOptionItems.Take(5).ToList();
        return new SourceBackedOptionAnswerSelection(
            fallbackItems,
            WantsTotalPairing: true,
            HasCertifiedTotalPair: false,
            RequestedMaxMinutes: requestedMaxMinutes,
            VisibleTotalMinutes: null);
    }

    private static bool LooksLikeTotalDurationConstraintRequest(string? query, int? requestedMaxMinutes)
    {
        if (!requestedMaxMinutes.HasValue || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:total|combined|combine|combinee|combin[eé]e|ensemble|overall|cumul|cumulative|cumule|cumul[eé])\b",
            RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedCombinedDurationSet(
        IReadOnlyList<SourceBackedOptionCandidate> candidates,
        int minItems,
        int requestedMaxMinutes)
    {
        var selected = new List<SourceBackedOptionCandidate>();
        var total = 0;
        foreach (var candidate in candidates
            .Where(static candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(static candidate => candidate.Score))
        {
            if (total + candidate.VisibleMinutes!.Value > requestedMaxMinutes)
                continue;
            selected.Add(candidate);
            total += candidate.VisibleMinutes.Value;
            if (selected.Count >= minItems)
                break;
        }

        return selected.Count >= minItems ? selected : Array.Empty<SourceBackedOptionCandidate>();
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedOptionCandidates(
        ToolResults toolResults,
        string? query,
        bool keepOverRequestedDuration = false,
        string language = "")
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var namedEntityTerms = ExtractNamedEntityLikeQueryTerms(query);
        var requiresNamedEntityEvidence = namedEntityTerms.Count > 0
            && Regex.IsMatch(normalizedQuery, @"\b(?:uniquement|only|avec|with|using|utiliser|utilise|concernant|about|sur)\b", RegexOptions.CultureInvariant);
        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();

        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();

        var maxMinutes = TryExtractRequestedMaxMinutes(query);
        var queryAnchorTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 5)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var hasSourceBackedAnchorEvidence = queryAnchorTerms.Length > 0
            && !LooksLikeCompositeSourceBackedOptionAnchorRequest(normalizedQuery, maxMinutes.HasValue)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && sourceHits.Any(hit => QueryAnchorTermsMatchHit(queryAnchorTerms, hit));

        var candidates = sourceHits
            .Select(hit =>
            {
                var title = ExtractSourceBackedOptionTitle(hit, query);
                if (string.IsNullOrWhiteSpace(title)
                    && requiresNamedEntityEvidence
                    && QueryAnchorTermsMatchHit(namedEntityTerms, hit))
                {
                    title = BuildSourceBackedFallbackOptionTitle(hit, language);
                }
                var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                var scoreMaxMinutes = keepOverRequestedDuration ? null : maxMinutes;
                return new SourceBackedOptionCandidate(
                    hit,
                    title,
                    ComputeSourceBackedOptionHitScore(hit, title, query, scoreMaxMinutes, visibleMinutes),
                    visibleMinutes);
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Where(candidate => LooksLikeUsableSourceBackedOptionCandidate(candidate.Hit))
            .Where(candidate => keepOverRequestedDuration || !maxMinutes.HasValue || !candidate.VisibleMinutes.HasValue || candidate.VisibleMinutes.Value <= maxMinutes.Value)
            .Where(candidate => candidate.Score > -20)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            candidates = candidates
                .Where(candidate => !RagHitContainsAnyExcludedTerm(candidate.Hit, excludedTerms))
                .ToList();
        }

        var pairingOptionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        if (pairingOptionKindTerms.Count > 0 && candidates.Count > 1)
        {
            var kindMatchedCandidates = candidates
                .Where(candidate => PairingOptionKindMatchesCandidate(pairingOptionKindTerms, candidate))
                .ToList();
            if (kindMatchedCandidates.Count > 0)
                candidates = kindMatchedCandidates;
        }

        if (hasSourceBackedAnchorEvidence)
        {
            candidates = candidates
                .Where(candidate => QueryAnchorTermsMatchHit(queryAnchorTerms, candidate.Hit))
                .ToList();
        }

        if (requiresNamedEntityEvidence)
        {
            candidates = candidates
                .Where(candidate => QueryAnchorTermsMatchHit(namedEntityTerms, candidate.Hit))
                .ToList();
        }

        return candidates;
    }

    private static bool LooksLikeCompositeSourceBackedOptionAnchorRequest(string normalizedQuery, bool hasRequestedMaxMinutes)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;
        if (hasRequestedMaxMinutes || normalizedQuery.Contains('+', StringComparison.Ordinal))
            return true;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:total|combine|combiner|combined|ensemble|together|complet|complete|completa|completo)\b",
            RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<string> ExtractPairingRequestedOptionKindTerms(string? query)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|cu[aá]l|qual|welche|welcher|welches|quale)\s+(?<kind>[\p{L}][\p{L}'\u2019-]{2,30})",
            RegexOptions.CultureInvariant))
        {
            var kind = NormalizeLexicalLookup(match.Groups["kind"].Value);
            if (kind.Length >= 4 && !IsSourceBackedActionRetrievalNoiseTerm(kind))
                terms.Add(kind);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
    }

    private static bool PairingOptionKindMatchesCandidate(IReadOnlyList<string> optionKindTerms, SourceBackedOptionCandidate candidate)
    {
        if (optionKindTerms.Count == 0)
            return true;

        var haystack = NormalizeLexicalLookup($"{candidate.Title} {candidate.Hit.SectionTitle} {candidate.Hit.HeadingPath} {GetRagHitPrimaryEvidenceText(candidate.Hit)}");
        return optionKindTerms.Any(term =>
            Regex.IsMatch(
                haystack,
                $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:s|es)?([^\p{{L}}\p{{N}}]|$)",
                RegexOptions.CultureInvariant));
    }

    private static bool LooksLikeUsableSourceBackedOptionCandidate(RagHitSummary hit)
    {
        var profile = ClassifyRagHitEvidenceProfile(hit);
        var backendRole = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (!string.IsNullOrWhiteSpace(backendRole) && backendRole != "actionable_item")
            return false;

        if (profile.Role is "navigation" or "fragment" or "low_confidence")
            return false;

        var text = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var evidence = NormalizeLexicalLookup($"{GetRagHitPrimaryEvidenceText(hit)} {hit.ContextualSnippet}");
        if (Regex.IsMatch(evidence, @"\b(?:a\s*pplication|application|communaute|community|carnets?|noter|notez|commenter|partagez|partager|share|rating|account)\b", RegexOptions.CultureInvariant)
            && ComputeStructuredProcedureVisibleEvidenceCueScore(hit) <= 0
            && CountProcedureStepMarkers(text) <= 0)
        {
            return false;
        }

        if (ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 5)
            return true;
        if (ComputeProcedureCompletenessCueScore(hit) >= 7)
            return true;
        if (profile.ActionabilityScore >= 9)
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 3)
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && HasSourceBackedProfileTitle(hit))
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && CountProcedureStepMarkers(text) >= 2)
            return true;

        var title = ExtractSourceBackedOptionTitle(hit, query: null);
        if (!string.IsNullOrWhiteSpace(title)
            && IsUsefulSourceBackedDisplayTitle(title)
            && !LooksLikePlanItemNoise(title)
            && !LooksLikeProcedureSentenceTitle(NormalizeLexicalLookup(title))
            && (hit.Score >= 0.5
                || ComputeEvidenceShapeScore(hit, GetRagHitLookupText(hit), includeBackendHints: true) >= 3))
        {
            return true;
        }

        return false;
    }

    private static bool HasSourceBackedProfileTitle(RagHitSummary hit)
        => ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)
            .Select(CleanSourceBackedOptionTitle)
            .Any(title => !string.IsNullOrWhiteSpace(title) && IsUsefulSourceBackedDisplayTitle(title));

    private static bool QueryAnchorTermsMatchHit(IReadOnlyList<string> anchorTerms, RagHitSummary hit)
    {
        if (anchorTerms.Count == 0)
            return false;

        var evidence = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        var title = NormalizeLexicalLookup($"{hit.DocName} {hit.SectionTitle} {hit.HeadingPath} {ExtractSourceBackedOptionTitle(hit, null)}");
        return anchorTerms.Any(term =>
            evidence.Contains(term, StringComparison.Ordinal)
            || title.Contains(term, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> SourceBackedOptionConstraintTerms = new(StringComparer.Ordinal)
    {
        "total", "moins", "minutes", "minute", "rapide", "rapides", "complete", "complet",
        "combined", "overall", "option", "options", "suggestion", "suggestions", "selection",
        "facile", "faciles", "easy", "simple", "simples", "rapido", "rapida", "rapidas",
        "rapidos", "schnell", "einfach", "veloce", "veloci"
    };

    private static readonly HashSet<string> SourceBackedPairingAnchorNoiseTerms = new(StringComparer.Ordinal)
    {
        "quel", "quelle", "quels", "quelles", "quoi", "which", "what", "cual", "qual",
        "welche", "welcher", "welches", "quale", "propose", "proposes", "suggest",
        "suggestion", "suggestions", "recommend", "recommends", "recommendation",
        "recommande", "recommander", "conseille", "conseiller", "irait", "vont",
        "bien", "avec", "with", "goes", "pair", "pairs", "pairing", "accompagne",
        "accompagner", "associe", "associer", "compatible", "compatibles", "con",
        "acompanha", "acompanhar", "combina", "combinar", "passt", "kombinieren",
        "abbinare", "abbina"
    };

    private static string BuildSourceBackedFallbackOptionTitle(RagHitSummary hit, string language)
    {
        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return string.IsNullOrWhiteSpace(docLabel)
            ? $"Source {SourceBackedPagePrefix(language)}{hit.PageStart}"
            : $"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart}";
    }

    private static IReadOnlyList<string> ExtractNamedEntityLikeQueryTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return Regex.Matches(query, @"\b[\p{Lu}][\p{L}\p{N}]{4,}\b", RegexOptions.CultureInvariant)
            .Select(match => NormalizeLexicalLookup(match.Value))
            .Where(static term => term.Length >= 5)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }

    private static string ExtractSourceBackedOptionTitle(RagHitSummary hit, string? query)
    {
        var profileCandidates = ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Score = ComputeSourceBackedOptionTitleScore(title, hit, query)
            })
            .Where(item => item.Score > -30)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToArray();
        if (profileCandidates.Length > 0)
            return profileCandidates[0].Title;

        var candidates = ExtractSourceBackedTitleCandidates(hit)
            .Concat(new[] { ExtractPlanItemTitleV2(GetPlanExtractionText(hit)) })
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(title => IsUsefulSourceBackedDisplayTitle(title))
            .Where(title => !LooksLikePlanItemNoise(title))
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Score = ComputeSourceBackedOptionTitleScore(title, hit, query)
            })
            .Where(item => item.Score > -20)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToArray();

        return candidates.FirstOrDefault()?.Title ?? string.Empty;
    }

    private static string CleanSourceBackedOptionTitle(string? value)
    {
        var title = HumanizePlanItemTitleV2(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = Regex.Replace(
            title,
            @"^([\p{Lu}0-9 '&/\-,\u00c0-\u017f]{4,48})\s+(?:l['\u2019]|le|la|les|un|une)\b.+$",
            "$1",
            RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"^(?:D(?:E|[\u00c9])J)\s*(?=[\p{Lu}\u00c0-\u017f])", string.Empty, RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"^(?:de|du|des|d['\u2019])\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"(?i)\b([\p{L}]{5,})aux\b", "$1 aux", RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"^(.{4,70}?)(?:\s+(?:se|est|sont|peut|peuvent|permet|permettent|pour\s+obtenir|le\s+temps|observer)\b).*$",
            "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim(' ', '-', ':', '.', ',', ';');

        return title.Length <= 90 ? title : title[..90].TrimEnd();
    }

    private static int ComputeSourceBackedOptionTitleScore(string title, RagHitSummary hit, string? query)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        var normalizedSection = NormalizeLexicalLookup($"{hit.SectionTitle} {hit.HeadingPath}");
        var normalizedQuery = NormalizeLexicalLookup(query);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
            score += ComputeTypoTolerantRagHitLexicalRelevance(normalizedQuery, normalizedTitle) * 2;

        var wordCount = ExtractQuerySignalTerms(normalizedTitle).Count();
        if (wordCount is >= 2 and <= 7)
            score += 8;
        if (Regex.IsMatch(title, @"[\p{Lu}\u00c0-\u017f]", RegexOptions.CultureInvariant))
            score += 3;
        if (!string.IsNullOrWhiteSpace(normalizedSection) && normalizedSection.Contains(normalizedTitle, StringComparison.Ordinal))
            score -= 12;
        if (Regex.IsMatch(normalizedTitle, @"\b(?:document|section|categories?|modes?|preparation|pages?)\b", RegexOptions.CultureInvariant))
            score -= 16;

        return score;
    }

    private static int ComputeSourceBackedOptionHitScore(
        RagHitSummary hit,
        string title,
        string? query,
        int? requestedMaxMinutes,
        int? visibleMinutes)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var evidence = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        var score = ComputeTypoTolerantRagHitLexicalRelevance(normalizedQuery, evidence);
        score += ComputeProcedureCompletenessCueScore(hit);
        score += ComputeStructuredProcedureEvidenceCueScore(hit);
        score += ComputeStructuredProcedureVisibleEvidenceCueScore(hit);
        score += ComputeSourceBackedOptionTitleScore(title, hit, query);

        if (requestedMaxMinutes.HasValue)
        {
            if (visibleMinutes.HasValue && visibleMinutes.Value <= requestedMaxMinutes.Value)
                score += 18;
            else if (visibleMinutes.HasValue)
                score -= 60;
            else
                score -= 3;
        }

        var queryTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Take(8)
            .ToArray();
        if (queryTerms.Length > 0 && QueryAnchorTermsMatchHit(queryTerms, hit))
            score += 12;

        if (LooksLikeMidProcedureFragment(hit))
            score -= 8;

        return score;
    }

    private static string FormatSourceBackedOptionEvidence(RagHitSummary hit)
    {
        var evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 220));
        return string.IsNullOrWhiteSpace(evidence) ? string.Empty : evidence;
    }

    private static int? TryExtractRequestedMaxMinutes(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var match = Regex.Match(
            normalized,
            @"\b(?:moins\s+de|en\s+moins\s+de|sous|maximum|max|under|less\s+than|within)\s+(?<n>\d{1,3})\s*(?:min|minutes?)\b",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                normalized,
                @"\b(?:moins|maximum|max|sous|under|less|within)\b.{0,32}\b(?<n>\d{1,3})\s*(?:min|minutes?)\b",
                RegexOptions.CultureInvariant);
        }
        if (!match.Success)
            return null;

        return int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            ? minutes
            : null;
    }

    private static int? ExtractBestVisibleDurationMinutes(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var primaryDuration = ExtractBestVisibleDurationMinutesFromText(text);
        if (primaryDuration.HasValue)
            return primaryDuration;

        var lookupText = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        return string.Equals(lookupText, text, StringComparison.Ordinal)
            ? null
            : ExtractBestVisibleDurationMinutesFromText(lookupText);
    }

    private static int? ExtractBestVisibleDurationMinutesFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var labeledTotal = ExtractLabeledVisibleDurationTotalMinutes(text);
        if (labeledTotal.HasValue)
            return labeledTotal;

        var values = new List<int>();
        foreach (Match match in Regex.Matches(NormalizeDurationScanText(text), @"\b(?<h>\d{1,2})\s*(?:h\b|heures?\b)(?:\s*(?<m>\d{1,2})\s*min(?:utes?)?)?", RegexOptions.CultureInvariant))
        {
            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            values.Add(hours * 60 + minutes);
        }

        foreach (Match match in Regex.Matches(NormalizeDurationScanText(text), @"\b(?<m>\d{1,3})\s*min(?:utes?)?(?=\b|(?:preparation|prep|procedure|process|execution|repos|pause|wait|rest|total|temps|duree)\b)", RegexOptions.CultureInvariant))
            values.Add(int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture));

        return values
            .Where(value => value is > 0 and <= 1440)
            .OrderByDescending(static value => value)
            .Cast<int?>()
            .FirstOrDefault();
    }

    private static int? ExtractLabeledVisibleDurationTotalMinutes(string normalizedText)
    {
        var text = NormalizeDurationScanText(normalizedText);
        var durations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            text,
            @"\b(?<label>temps\s+total|duree\s+totale|total|preparation|prep|procedure|process|execution|operation|operations|repos|pause|wait|rest)\s*[:=\-]?\s*(?<duration>\d{1,2}\s*(?:h\b|heures?\b)(?:\s*\d{1,2}\s*min(?:utes?)?)?|\d{1,3}\s*min(?:utes?)?)",
            RegexOptions.CultureInvariant))
        {
            var label = NormalizeDurationLabel(match.Groups["label"].Value);
            var minutes = ParseVisibleDurationMinutes(match.Groups["duration"].Value);
            if (!minutes.HasValue)
                continue;

            if (label == "total")
                return minutes.Value;

            durations.TryAdd(label, minutes.Value);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"\b(?<label>[\p{L}][\p{L}\-]{2,30})\s*[:=\-]\s*(?<duration>\d{1,2}\s*(?:h\b|heures?\b)(?:\s*\d{1,2}\s*min(?:utes?)?)?|\d{1,3}\s*min(?:utes?)?)",
            RegexOptions.CultureInvariant))
        {
            var label = NormalizeDurationLabel(match.Groups["label"].Value);
            if (LooksLikeDurationLabelNoise(label))
                continue;

            var minutes = ParseVisibleDurationMinutes(match.Groups["duration"].Value);
            if (minutes.HasValue)
                durations.TryAdd(label, minutes.Value);
        }

        return durations.Count >= 2
            ? durations.Values.Sum()
            : durations.Values.Cast<int?>().FirstOrDefault();
    }

    private static string NormalizeDurationScanText(string text)
    {
        return NormalizeStructuredScanText(text);
    }

    private static string NormalizeStructuredScanText(string? text)
    {
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(normalized, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{Ll})(?=\p{Lu})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<=[\p{L}])(?=(?:preparation|prep|procedure|process|execution|repos|pause|wait|rest|total|temps|duree)\b)",
            " ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(normalized);
    }

    private static string NormalizeDurationLabel(string label)
    {
        var normalized = NormalizeLexicalLookup(label);
        if (Regex.IsMatch(normalized, @"\b(?:temps\s+total|duree\s+totale|total)\b", RegexOptions.CultureInvariant))
            return "total";
        if (Regex.IsMatch(normalized, @"\b(?:preparation|prep)\b", RegexOptions.CultureInvariant))
            return "prep";
        if (Regex.IsMatch(normalized, @"\b(?:procedure|process|execution|operation|operations)\b", RegexOptions.CultureInvariant))
            return "process";
        if (Regex.IsMatch(normalized, @"\b(?:repos|pause|wait|rest)\b", RegexOptions.CultureInvariant))
            return "rest";
        return normalized;
    }

    private static bool LooksLikeDurationLabelNoise(string label)
        => string.IsNullOrWhiteSpace(label)
            || label.Length < 3
            || Regex.IsMatch(label, @"\b(?:min|mins|minute|minutes|heure|heures|hour|hours|page|pages|source|sources|document|documents|pdf)\b", RegexOptions.CultureInvariant);

    private static int? ParseVisibleDurationMinutes(string value)
    {
        var text = NormalizeLexicalLookup(value);
        var hourMatch = Regex.Match(
            text,
            @"\b(?<h>\d{1,2})\s*(?:h\b|heures?\b)(?:\s*(?<m>\d{1,2})\s*min(?:utes?)?)?",
            RegexOptions.CultureInvariant);
        if (hourMatch.Success)
        {
            var hours = int.Parse(hourMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = hourMatch.Groups["m"].Success
                ? int.Parse(hourMatch.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            var total = hours * 60 + minutes;
            return total is > 0 and <= 1440 ? total : null;
        }

        var minuteMatch = Regex.Match(
            text,
            @"\b(?<m>\d{1,3})\s*min(?:utes?)?",
            RegexOptions.CultureInvariant);
        if (!minuteMatch.Success)
            return null;

        var minuteTotal = int.Parse(minuteMatch.Groups["m"].Value, CultureInfo.InvariantCulture);
        return minuteTotal is > 0 and <= 1440 ? minuteTotal : null;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);
        if (closeLeads.Count == 0)
            closeLeads = hits
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit))
                .OrderByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(3)
                .ToList();

        return closeLeads
            .Select(BuildSourceRefFromRagHit)
            .ToList();
    }

    private static List<RagHitSummary> SelectMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        return hits
            .Select(hit => new
            {
                Hit = hit,
                Score = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit))
            })
            .Where(item => !LooksLikeNavigationOnlyHit(item.Hit))
            .Where(item => !LooksLikeLowSignalContentCandidateHit(item.Hit))
            .Where(item => !LooksLikePageReferenceOnlyHit(item.Hit))
            .Where(item => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, item.Hit))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Hit)
            .Take(2)
            .ToList();
    }

    private static int ComputeTypoTolerantRagHitLexicalRelevance(string query, string text)
    {
        var best = ComputeRagHitLexicalRelevance(query, text);
        foreach (var variant in BuildTypoTolerantQueryVariants(query))
            best = Math.Max(best, ComputeRagHitLexicalRelevance(variant, text));
        return best;
    }

    private static IReadOnlyList<RagHitSummary> SelectSourceBackedExtractiveHits(ToolResults toolResults, string query, int maxHits)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
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
        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var timeCompatibleHits = allHits
                .Where(item =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(item.Hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (timeCompatibleHits.Count > 0)
                allHits = timeCompatibleHits;
        }
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
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!)
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit))
                .ToList();
            if (exactTitleHits.Count > 0)
            {
                var anchoredTitleHits = exactTitleHits
                    .Where(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                    .ToList();
                if (anchoredTitleHits.Count > 0)
                    exactTitleHits = anchoredTitleHits;

                if (LooksLikeStructuredItemCardRequest(query))
                {
                    exactTitleHits = AddComplementaryStructuredHitsForExactItem(
                            exactTitleHits,
                            allHits.Select(static item => item.Hit))
                        .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle!, hit))
                        .ThenByDescending(ComputeExactItemCardCompletenessCueScore)
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
        if (Regex.IsMatch(queryLookup, @"\b(?:liste\s+d['\u2019]elements|itemized\s+list|liste\s+detaillee|detailed\s+list)\b", RegexOptions.CultureInvariant))
            return false;

        var text = NormalizeLexicalLookup(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var looksLikeAppFeatureCopy = Regex.IsMatch(
            text,
            @"\b(?:liste\s+d['\u2019]elements\s+a\s+partir|itemized\s+list\s+from|application|appli|app|interface|workflow|fonctionnalite|feature)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                text,
                @"\b(?:documents?|sources?|corpus|base\s+de\s+connaissances?|knowledge\s+base|categories?|categories|dossiers?|folders?|fichiers?|files?)\b",
                RegexOptions.CultureInvariant);
        return looksLikeAppFeatureCopy
            && !Regex.IsMatch(text, @"\b(?:preparation|procedure|method|methode|etapes?|steps?|requirements?|values?|valeurs?|quantities?|quantites?|\d+\s*(?:g|kg|mg|ml|cl|l|min|h|mm|cm|m))\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeLowSignalContentCandidateHit(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return false;
        if (BackendSelectionHintsPreferLowSignal(hit))
            return true;

        var primaryText = GetRagHitPrimaryContentText(hit);
        var text = NormalizeStructuredScanText(primaryText);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (hit.PageStart <= 2 && LooksLikeGenericFrontMatterShape(primaryText))
            return true;

        var hasBodyStructure = Regex.IsMatch(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|procedure|etapes?|steps?|method|methode|(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?))\b",
            RegexOptions.CultureInvariant);
        var hasStrongStructuredEvidence = ComputeProcedureCompletenessCueScore(hit) >= 5
            || ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 3
            || CountProcedureStepMarkers(text) > 0;
        if (hasBodyStructure && hasStrongStructuredEvidence)
            return false;

        if (Regex.IsMatch(text, @"^(?:si|lorsque|quand|when|if)\b", RegexOptions.CultureInvariant))
            return true;

        var hasCoverOrCatalogCue = Regex.IsMatch(
            text,
            @"\b(?:sommaire|index|contents|table\s+of\s+contents|catalogue|catalog|guide|introduction|avant-propos|preface|foreword|copyright|isbn|edition|publisher|front\s+matter)\b",
            RegexOptions.CultureInvariant);
        if (hasCoverOrCatalogCue)
            return true;

        return false;
    }

    private static bool LooksLikeGenericFrontMatterShape(string? text)
    {
        var raw = CollapseWhitespace(text ?? string.Empty);
        if (raw.Length < 80)
            return false;

        var normalized = NormalizeLexicalLookup(raw);
        var lead = normalized.Length <= 700 ? normalized : normalized[..700];
        var hasMarketingLead = Regex.IsMatch(lead, @"\b\d{2,4}\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                lead,
                @"\b(?:conseils?|tips?|guides?|simple|simples|accessible|accessibles|abordable|abordables|budget|rapide|rapides|quick|easy|pratique|pratiques)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                lead,
                @"\b(?:preparation|procedure|etapes?|steps?|method|methode)\b",
                RegexOptions.CultureInvariant);
        if (raw.Length <= 2200 && hasMarketingLead)
            return true;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sommaire|index|contents|table\s+of\s+contents|catalogue|catalog|copyright|isbn|edition|publisher|preface|foreword|avant-propos|introduction|www\.)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var letters = raw.Where(char.IsLetter).ToArray();
        if (letters.Length < 24)
            return false;

        var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        var digitRatio = raw.Count(char.IsDigit) / (double)Math.Max(1, raw.Length);
        var punctuationRatio = raw.Count(ch => char.IsPunctuation(ch) || char.IsSymbol(ch)) / (double)Math.Max(1, raw.Length);
        var hasNumberedHeadline = Regex.IsMatch(
            raw,
            @"(?:^|\s)\d{2,4}\s+[\p{Lu}\p{Lt}][\p{Lu}\p{Lt}\s'\-]{6,}",
            RegexOptions.CultureInvariant);
        var hasDenseUppercaseHeadline = Regex.IsMatch(
            raw,
            @"(?:\b[\p{Lu}\p{Lt}]{4,}\b[\s+\-]*){3,}",
            RegexOptions.CultureInvariant);
        var hasGluedCaseBoundary = Regex.IsMatch(
            raw,
            @"[\p{Ll}]{4,}[\p{Lu}\p{Lt}]{4,}|[\p{Lu}\p{Lt}]{4,}[\p{Ll}]{4,}[\p{Lu}\p{Lt}]{4,}",
            RegexOptions.CultureInvariant);
        var hasMarketingCue = Regex.IsMatch(
            normalized,
            @"\b(?:conseils?|tips?|guide|guides?|simple|simples|accessible|accessibles|abordable|abordables|budget|rapide|rapides|quick|easy|pratique|pratiques)\b",
            RegexOptions.CultureInvariant);
        if (raw.Length <= 1600
            && (hasNumberedHeadline || hasDenseUppercaseHeadline || hasGluedCaseBoundary)
            && (hasMarketingCue || upperRatio >= 0.38))
        {
            return true;
        }

        return raw.Length <= 1400
            && upperRatio >= 0.48
            && digitRatio <= 0.18
            && punctuationRatio <= 0.22;
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
        if (ContainsStructuredItemHeading(GetRagHitPrimaryContentText(hit)))
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
        if (Regex.IsMatch(normalizedPrimary, @"\b(?:" + ExactItemStructureHeadingPattern + @")\b", RegexOptions.CultureInvariant))
            score += 2;

        if (LooksLikeMidProcedureFragment(hit))
            score -= 80;
        else if (HasSourceBackedProfileTitle(hit)
                 || !string.IsNullOrWhiteSpace(ExtractSourceBackedOptionTitle(hit, query: null)))
            score += 4;

        if (Regex.IsMatch(normalizedPrimary, @"\b(?:introduction|bienvenue|sommaire|contents|index|overview|presentation|nous avons reuni|nous sommes prets)\b", RegexOptions.CultureInvariant)
            && structuredProcedureScore < 4)
        {
            score -= 16;
        }

        if (string.Equals(hit.EmbeddingBasis, "document_profile_v1", StringComparison.OrdinalIgnoreCase))
            score -= 5;

        return score;
    }

    private sealed record QuantityScaleLine(string Original, string Scaled, bool IsNumeric);

    private sealed record QuantityScalingCandidate(RagHitSummary Hit, int SourceCount, IReadOnlyList<QuantityScaleLine> Lines, double Score);

    private static IReadOnlyList<string> ExtractSourceBackedExcludedTerms(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:sans|pas\s+d['\u2019]?|pas\s+de|without|no|sin|sem|ohne|senza)\s+(?<term>[\p{L}\p{N}'\u2019 \-]{2,60})",
                     RegexOptions.CultureInvariant))
        {
            foreach (var rawPart in Regex.Split(
                match.Groups["term"].Value,
                @"\s*(?:,|/|\bet\b|\bou\b|\bni\b|\band\b|\bor\b|\bnor\b|\by\b|\bo\b|\be\b|\boder\b|\bund\b)\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var term = Regex.Replace(
                        rawPart,
                        @"(?i)\b(?:avec|with|dans|from|pour|for|sur|de|du|des|document|documents|source|sources|element|elements|item|items|option|options)\b.*$",
                        string.Empty,
                        RegexOptions.CultureInvariant)
                    .Trim(' ', '.', ',', ':', ';', '?', '!', '"', '\'');

                var normalized = NormalizeLexicalLookup(term);
                if (normalized.Length >= 2)
                    terms.Add(normalized);
            }
        }

        foreach (Match match in Regex.Matches(
                     s,
                     @"(?i)\b(?:en\s+evitant|en\s+évitant|evitant|évitant|eviter|éviter|avoid(?:ing)?|except|sauf|salvo|sem|ohne|senza)\s+(?<terms>[^.?!;]{2,140})",
                     RegexOptions.CultureInvariant))
        {
            foreach (var rawPart in Regex.Split(match.Groups["terms"].Value, @"\s*(?:,|/|\bet\b|\bou\b|\bni\b|\band\b|\bor\b|\bnor\b|\by\b|\bo\b|\be\b|\boder\b|\bund\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var part = Regex.Replace(
                        rawPart,
                        @"(?i)\b(?:dans|from|pour|for|sur|avec|with|document|documents|source|sources|element|elements|item|items|option|options)\b.*$",
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
        return excludedTerms.Any(term =>
        {
            return Regex.IsMatch(
                haystack,
                $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:s|es)?([^\p{{L}}\p{{N}}]|$)",
                RegexOptions.CultureInvariant);
        });
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
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSourceBackedExtractionQualityCaveat(StringBuilder sb, IReadOnlyList<RagHitSummary> hits, string language)
    {
        if (!hits.Any(ShouldFlagExtractionQualityCaveat))
            return;

        var note = NormalizeLanguageCode(language) switch
        {
            "en" => "Note: at least one cited page has a low extraction/OCR confidence flag, so verify the source page if the detail is critical.",
            "es" => "Nota: al menos una pagina citada tiene una marca de baja confianza de extraccion/OCR; verifica la pagina fuente si el detalle es critico.",
            "pt" => "Nota: pelo menos uma pagina citada tem baixa confianca de extracao/OCR; verifica a pagina fonte se o detalhe for critico.",
            "de" => "Hinweis: Mindestens eine zitierte Seite hat eine niedrige Extraktions- oder OCR-Vertrauensbewertung. Pruefe die Quellseite, wenn das Detail kritisch ist.",
            "it" => "Nota: almeno una pagina citata ha un indicatore di bassa affidabilita di estrazione/OCR; verifica la pagina sorgente se il dettaglio e critico.",
            _ => "Note : au moins une page citee a un signal de confiance faible d'extraction/OCR ; verifie la page source si le detail est critique."
        };

        sb.AppendLine();
        sb.Append(note);
    }

    private static bool ShouldFlagExtractionQualityCaveat(RagHitSummary hit)
    {
        if (hit.ManualReviewRecommended)
            return true;

        if (hit.ExtractionConfidence is <= 0.5)
            return true;

        var status = (hit.QualityStatus ?? string.Empty).Trim().ToLowerInvariant();
        return status.Contains("manual_review", StringComparison.Ordinal)
            || status.Contains("low_text", StringComparison.Ordinal)
            || status.Contains("ocr_failed", StringComparison.Ordinal)
            || status.Contains("low_confidence", StringComparison.Ordinal);
    }

    private static bool LooksLikeSourceBackedQuantityScalingRequest(string? query)
    {
        var normalized = NormalizeLooseLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var asksExplicitScaling = Regex.IsMatch(
            normalized,
                @"\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|multiplie|multiplier|calcule|calculer|mets|mettre|quantites?|quantit[eé]s?|amounts?|values?|valeurs?|scale|resize|adjust|adapt|convert|multiply|counts?|units?|items?)\b",
            RegexOptions.CultureInvariant);
        if (LooksLikeDocumentaryPlanningRequest(query) && !asksExplicitScaling)
            return false;

        return asksExplicitScaling && TryExtractTargetScaleCount(query, out _);
    }

    private static string BuildSourceBackedQuantityScalingAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        if (!TryExtractTargetScaleCount(query, out var targetCount))
            return string.Empty;

        var candidates = SelectQuantityScalingCandidates(hits, query, targetCount);

        var selected = candidates.FirstOrDefault();
        if (selected is null)
            return string.Empty;

        var factor = targetCount / (double)selected.SourceCount;
        var labels = BuildQuantityScalingLabels(language);
        var docLabel = string.IsNullOrWhiteSpace(selected.Hit.DocName) ? selected.Hit.DocPath : selected.Hit.DocName;
        var factorText = FormatScaleNumber(factor);

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        sb.Append(labels.Base);
        sb.Append(' ');
        sb.Append(docLabel);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(selected.Hit.PageStart);
        sb.Append(" - ");
        sb.Append(selected.SourceCount);
        sb.Append(" -> ");
        sb.Append(targetCount);
        sb.Append(" (");
        sb.Append(labels.Factor);
        sb.Append(" x");
        sb.Append(factorText);
        sb.AppendLine(").");

        sb.AppendLine(labels.ItemizedList);
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

    private static List<QuantityScalingCandidate> SelectQuantityScalingCandidates(IEnumerable<RagHitSummary> hits, string query, int targetCount)
    {
        var subject = TryExtractQuantityScalingSubject(query);
        return hits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(hit =>
            {
                var text = GetBestRagEvidenceText(hit);
                int sourceCount;
                string? sourceCountLabel;
                List<QuantityScaleLine> lines;
                if (TryBuildQuantityScaleLinesFromCardEvidence(
                        hit,
                        query,
                        targetCount,
                        out sourceCount,
                        out sourceCountLabel,
                        out var evidenceLines))
                {
                    lines = evidenceLines;
                }
                else
                {
                    sourceCount = TryExtractSourceScaleBaseCount(text, out var baseCount, out sourceCountLabel) ? baseCount : 0;
                    lines = sourceCount > 0
                        ? ExtractQuantityScaleLines(text, targetCount / (double)sourceCount)
                        : new List<QuantityScaleLine>();
                }
                var isScalable = sourceCount > 0
                    && HasExplicitScalableQuantitySourceSignal(hit, query, text, sourceCountLabel, lines);
                var score = isScalable
                    ? ComputeRagHitLexicalRelevance(string.IsNullOrWhiteSpace(subject) ? query : subject!, GetRagHitLookupText(hit))
                      + 16
                      + Math.Min(lines.Count(static line => line.IsNumeric), 8)
                    : double.NegativeInfinity;
                return new QuantityScalingCandidate(hit, sourceCount, lines, score);
            })
            .Where(item => item.SourceCount > 0
                           && item.Lines.Count(static line => line.IsNumeric) >= 2
                           && !double.IsNegativeInfinity(item.Score))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Hit.Score)
            .ToList();
    }

    private static bool HasExplicitScalableQuantitySourceSignal(
        RagHitSummary hit,
        string query,
        string evidenceText,
        string? sourceCountLabel,
        IReadOnlyList<QuantityScaleLine> lines)
    {
        if (HasContentCardNonScalableEvidence(hit) && !HasContentCardScalableQuantityEvidence(hit))
            return false;

        if (LooksLikeSafetyOrComplianceQuantityContext(hit, query, evidenceText))
            return false;

        var numericLineCount = lines.Count(static line => line.IsNumeric);
        if (numericLineCount < 2)
            return false;

        TryExtractTargetScaleCount(query, out _, out var targetCountLabel);
        var labelsCompatible = ScaleCountLabelsLookCompatible(sourceCountLabel, targetCountLabel);
        var sourceLabelIsListHeading = IsQuantityScaleListHeadingLabel(sourceCountLabel);
        var sourceLabelLooksScalable = sourceLabelIsListHeading || IsLikelyScalableScaleCountLabel(sourceCountLabel);
        var cardHasScalableQuantityBasis = TryExtractScalableQuantityCardBasis(
            hit,
            out _,
            out var cardScaleCountLabel);
        var cardLabelsCompatible = ScaleCountLabelsLookCompatible(cardScaleCountLabel, targetCountLabel);

        var normalizedEvidence = NormalizeLooseLookup(evidenceText);
        var cardSignals = BuildQuantityScalingCardSignalText(hit);
        var sourceContext = NormalizeLooseLookup(
            $"{hit.SectionTitle} {hit.HeadingPath} {hit.CategoryPath} {normalizedEvidence} {cardSignals}");

        var hasExplicitBase = HasExplicitQuantityScaleBasePhrase(normalizedEvidence) || cardHasScalableQuantityBasis;
        var hasQuantityListSignal = HasQuantityListSignal(sourceContext)
                                    || HasQuantityListCardSignal(hit)
                                    || numericLineCount >= 3;
        var hasScalableSignal = HasScalableQuantitySignal(sourceContext) || cardHasScalableQuantityBasis;

        return hasExplicitBase
               && hasQuantityListSignal
               && (hasScalableSignal || labelsCompatible || cardLabelsCompatible || sourceLabelLooksScalable);
    }

    private static bool TryExtractScalableQuantityCardBasis(RagHitSummary hit, out int count, out string? label)
    {
        count = 0;
        label = null;
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        foreach (var card in hit.MatchedContentCards)
        {
            if (card.Evidence?.ScaleBasis is { Count: > 0 } basis
                && card.Evidence.NonScalableReasons.Count == 0
                && card.Evidence.QuantityFacts.Count >= 2)
            {
                count = basis.Count;
                label = basis.Label;
                return true;
            }

            var hasScaleBasis = false;
            var hasQuantityList = false;
            var hasScalableQuantities = false;
            var cardCount = 0;
            string? cardLabel = null;

            foreach (var signal in card.Signals ?? Array.Empty<string>())
            {
                var raw = CollapseWhitespace(signal).Trim();
                if (raw.Length == 0)
                    continue;

                var lower = raw.ToLowerInvariant();
                var normalized = NormalizeScaleSignalToken(raw);
                if (string.Equals(normalized, "scale_basis", StringComparison.Ordinal))
                {
                    hasScaleBasis = true;
                    continue;
                }

                if (string.Equals(normalized, "quantity_list", StringComparison.Ordinal))
                {
                    hasQuantityList = true;
                    continue;
                }

                if (string.Equals(normalized, "scalable_quantities", StringComparison.Ordinal))
                {
                    hasScalableQuantities = true;
                    continue;
                }

                if (lower.StartsWith("scale_basis_count:", StringComparison.Ordinal)
                    && int.TryParse(lower["scale_basis_count:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    && parsed is > 0 and <= 200)
                {
                    cardCount = parsed;
                    hasScaleBasis = true;
                    continue;
                }

                if (lower.StartsWith("scale_basis_label:", StringComparison.Ordinal))
                {
                    cardLabel = NormalizeScaleSignalToken(lower["scale_basis_label:".Length..]);
                }
            }

            if (cardCount > 0 && (hasScalableQuantities || (hasScaleBasis && hasQuantityList)))
            {
                count = cardCount;
                label = cardLabel;
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildQuantityScaleLinesFromCardEvidence(
        RagHitSummary hit,
        string query,
        int targetCount,
        out int sourceCount,
        out string? sourceCountLabel,
        out List<QuantityScaleLine> lines)
    {
        sourceCount = 0;
        sourceCountLabel = null;
        lines = new List<QuantityScaleLine>();

        if (hit.MatchedContentCards is not { Count: > 0 }
            || LooksLikeSafetyOrComplianceQuantityContext(hit, query, GetBestRagEvidenceText(hit)))
        {
            return false;
        }

        foreach (var card in hit.MatchedContentCards)
        {
            var evidence = card.Evidence;
            if (evidence?.ScaleBasis is not { Count: > 0 } basis
                || evidence.NonScalableReasons.Count > 0
                || evidence.QuantityFacts.Count < 2)
            {
                continue;
            }

            var factor = targetCount / (double)basis.Count;
            var scaledLines = evidence.QuantityFacts
                .Where(static fact => fact.Value > 0
                                      && !string.IsNullOrWhiteSpace(fact.Unit)
                                      && !string.IsNullOrWhiteSpace(fact.Label))
                .Take(18)
                .Select(fact =>
                {
                    var source = string.IsNullOrWhiteSpace(fact.SourceText)
                        ? $"{FormatScaleNumber(fact.Value)} {fact.Unit} {fact.Label}"
                        : CollapseWhitespace(fact.SourceText);
                    var scaled = $"{FormatScaleNumber(fact.Value * factor)} {fact.Unit} {fact.Label}";
                    return new QuantityScaleLine(source, scaled, IsNumeric: true);
                })
                .ToList();

            if (scaledLines.Count(static line => line.IsNumeric) < 2)
                continue;

            sourceCount = basis.Count;
            sourceCountLabel = basis.Label;
            lines = scaledLines;
            return true;
        }

        return false;
    }

    private static bool HasQuantityListCardSignal(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        return hit.MatchedContentCards.Any(static card =>
            (card.Signals ?? Array.Empty<string>()).Any(static signal =>
            {
                var normalized = NormalizeScaleSignalToken(signal);
                return string.Equals(normalized, "quantity_list", StringComparison.Ordinal)
                       || string.Equals(normalized, "scalable_quantities", StringComparison.Ordinal);
            }));
    }

    private static string NormalizeScaleSignalToken(string? value)
    {
        var normalized = Regex.Replace(
                (value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                "_",
                RegexOptions.CultureInvariant)
            .Trim('_');
        normalized = Regex.Replace(normalized, @"_+", "_", RegexOptions.CultureInvariant);
        return normalized;
    }

    private static bool LooksLikeSafetyOrComplianceQuantityContext(RagHitSummary hit, string query, string evidenceText)
    {
        var haystack = NormalizeLooseLookup(
            $"{query} {hit.DocName} {hit.DocPath} {hit.CategoryPath} {hit.SectionTitle} {hit.HeadingPath} {evidenceText} {BuildQuantityScalingCardSignalText(hit)}");

        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        return Regex.IsMatch(
                haystack,
                @"\b(?:safety|security|securite|hazard|danger|risk|risque|warning|caution|emergency|incident|injury|explosion|fire|flammable|toxic|toxicity|exposure|contamination|ppe|epi|lockout|loto|compliance|conformite|conformity|regulatory|reglementaire|reglementation|regulation|directive|legal|law|statutory|norme|standard|iso|iec|clause|article|shall|must|mandatory|required|obligatoire|exigence|interdit|prohibited|forbidden|limit|limite|threshold|seuil)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                haystack,
                @"\b(?:do\s+not|must\s+not|shall\s+not|ne\s+pas|ne\s+jamais|il\s+faut|il\s+ne\s+faut\s+pas)\b",
                RegexOptions.CultureInvariant);
    }

    private static string BuildQuantityScalingCardSignalText(RagHitSummary hit)
        => hit.MatchedContentCards is null
            ? string.Empty
            : string.Join(
                ' ',
                hit.MatchedContentCards.Select(static card =>
                    $"{card.Title} {card.Kind} {string.Join(' ', card.Signals ?? Array.Empty<string>())} {string.Join(' ', card.Evidence?.NonScalableReasons ?? Array.Empty<string>())}"));

    private static bool HasExplicitQuantityScaleBasePhrase(string normalizedEvidence)
        => Regex.IsMatch(
               normalizedEvidence,
               @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s*[\p{L}'\u2019.\-]{0,30}\b",
               RegexOptions.CultureInvariant)
           || Regex.IsMatch(
               normalizedEvidence,
               @"\b(?:base|basis|batch|lot|serie|set|per\s+unit|par\s+unite|par\s+element)\b",
               RegexOptions.CultureInvariant)
           || Regex.IsMatch(
               normalizedEvidence,
                @"\b\d{1,3}\s*(?:items?|elements?|quantities?|quantites?|amounts?|values?|valeurs?|requirements?|materials?|materiel|components?|composants?|supplies|entries?)\s*\d",
               RegexOptions.CultureInvariant);

    private static bool HasQuantityListSignal(string normalizedContext)
        => Regex.IsMatch(
            normalizedContext,
            @"\b(?:quantites?|quantit[eé]s?|quantities?|amounts?|values?|valeurs?|items?|elements?|components?|composants?|materials?|materiel|supplies|entries?|requirements?)\b",
            RegexOptions.CultureInvariant);

    private static bool HasScalableQuantitySignal(string normalizedContext)
        => Regex.IsMatch(
            normalizedContext,
            @"\b(?:scale|scaled|scalable|scaling|adapter|adapte|adapted|adjust|adjusted|proportion(?:al|nel|nelle)?|ratio|factor|facteur|multiply|multiplier|base|basis|batch|lot|serie|personnes?|persons?|people|units?|items?)\b",
            RegexOptions.CultureInvariant);

    private static bool ScaleCountLabelsLookCompatible(string? sourceLabel, string? targetLabel)
    {
        var source = NormalizeScaleCountLabel(sourceLabel);
        var target = NormalizeScaleCountLabel(targetLabel);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;

        if (source == target)
            return true;

        if (IsQuantityScaleListHeadingLabel(source))
            return true;

        return (source, target) switch
        {
            ("personne", "people") or ("people", "personne") => true,
            ("person", "personne") or ("personne", "person") => true,
            ("unit", "unite") or ("unite", "unit") => true,
            ("item", "element") or ("element", "item") => true,
            _ => false
        };
    }

    private static bool IsLikelyScalableScaleCountLabel(string? label)
    {
        var normalized = NormalizeScaleCountLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:personne|person|people|unit|unite|item|element|piece|part|batch|lot|set|serie)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool IsQuantityScaleListHeadingLabel(string? label)
    {
        var normalized = NormalizeScaleCountLabel(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:entry|item|element|quantity|quantite|amount|value|valeur|requirement|material|materiel|component|composant|supply)\b",
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeScaleCountLabel(string? label)
    {
        var normalized = NormalizeLooseLookup(label)
            .Trim(' ', '.', ',', ';', ':', '-', '/', '\\');
        if (normalized.EndsWith("ies", StringComparison.Ordinal) && normalized.Length > 4)
            normalized = normalized[..^3] + "y";
        else if (normalized.EndsWith("es", StringComparison.Ordinal) && normalized.Length > 4)
            normalized = normalized[..^2];
        else if (normalized.EndsWith('s') && normalized.Length > 3)
            normalized = normalized[..^1];

        return normalized;
    }

    private static (string Header, string Base, string Factor, string ItemizedList, string Source, string Caution) BuildQuantityScalingLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "Here is the deterministic quantity adaptation from the cited source.",
                "Source base:",
                "factor",
                "Adjusted quantities:",
                "source",
                "Quantities absent from the source stay unspecified; I do not invent missing steps."),
            "es" => (
                "Aqui tienes la adaptacion determinista de cantidades a partir de la fuente citada.",
                "Base fuente:",
                "factor",
                "Cantidades ajustadas:",
                "fuente",
                "Las cantidades ausentes de la fuente quedan sin especificar; no invento pasos que faltan."),
            "pt" => (
                "Aqui esta a adaptacao deterministica das quantidades a partir da fonte citada.",
                "Base da fonte:",
                "fator",
                "Quantidades ajustadas:",
                "fonte",
                "Quantidades ausentes da fonte ficam sem especificacao; nao invento passos em falta."),
            "de" => (
                "Hier ist die deterministische Mengenanpassung aus der zitierten Quelle.",
                "Quellbasis:",
                "Faktor",
                "Angepasste Mengen:",
                "Quelle",
                "Mengen, die in der Quelle fehlen, bleiben unspezifiziert; fehlende Schritte erfinde ich nicht."),
            "it" => (
                "Ecco l'adattamento deterministico delle quantita dalla fonte citata.",
                "Base fonte:",
                "fattore",
                "Quantita adattate:",
                "fonte",
                "Le quantita assenti dalla fonte restano non specificate; non invento passaggi mancanti."),
            _ => (
                "Voici l'adaptation deterministe des quantites a partir de la source citee.",
                "Base source :",
                "facteur",
                "Quantites adaptees :",
                "source",
                "Les quantites absentes de la source restent non specifiees ; je n'invente pas les etapes manquantes.")
        };
    }

    private static bool TryExtractTargetScaleCount(string? query, out int count)
        => TryExtractTargetScaleCount(query, out count, out _);

    private static bool TryExtractTargetScaleCount(string? query, out int count, out string? label)
        => TryExtractScaleCount(query, out count, out label);

    private static bool TryExtractSourceScaleBaseCount(string? text, out int count)
        => TryExtractSourceScaleBaseCount(text, out count, out _);

    private static bool TryExtractSourceScaleBaseCount(string? text, out int count, out string? label)
        => TryExtractScaleCount(text, out count, out label);

    private static bool TryExtractScaleCount(string? value, out int count, out string? label)
    {
        count = 0;
        label = null;
        var normalized = NormalizeLooseLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var patterns = new[]
        {
            @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+(?<n>\d{1,3})(?:\s+(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30}))?\b",
            @"\b(?<n>\d{1,3})\s*(?<label>[\p{L}][\p{L}'\u2019.\-]{1,30})(?=\d|\b)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(normalized, pattern, RegexOptions.CultureInvariant);
            if (match.Success
                && int.TryParse(match.Groups["n"].Value, out var n)
                && n is > 0 and <= 200
                && IsPlausibleScaleCountLabel(match.Groups["label"].Value))
            {
                count = n;
                label = NullIfWhiteSpace(match.Groups["label"].Value);
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausibleScaleCountLabel(string? label)
    {
        var normalized = NormalizeLooseLookup(label);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (!Regex.IsMatch(normalized, @"^[\p{L}][\p{L}'\u2019.\-]{1,30}$", RegexOptions.CultureInvariant))
            return false;

        return !Regex.IsMatch(
            normalized,
            @"^(?:g|kg|mg|ml|cl|l|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|%|s|sec|secs|secondes?|seconds?|min|mins|minutes?|h|hr|hrs|heures?|hours?|jour|jours|day|days|mois|month|months|annee|annees|year|years|c|celsius|fahrenheit)$",
            RegexOptions.CultureInvariant);
    }

    private static string? TryExtractQuantityScalingSubject(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (s.Length == 0)
            return null;

        var patterns = new[]
        {
            @"(?i)\b(?:adapte|adapter|ajuste|ajuster|convertis|convertir|calcule|calculer|mets|mettre|scale|resize|adjust|adapt|convert)\s+(?:le|la|les|l['\u2019]|the\s+)?(?<title>.+?)\s+(?:pour|for|para|per|fur|fuer|zu|a)\s+\d{1,3}\b",
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
            @"(?<!^)(?<![\d,.])(?=\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12})\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<!^)(?<![\d,.])(?=\d+\s+[\p{L}'\u2019\-]{3,}(?:\s+[\p{L}'\u2019\-]{3,}){0,3}\b)",
            "|",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var parts = Regex.Split(normalized, @"[|;\r\n]+", RegexOptions.CultureInvariant)
            .Select(CollapseWhitespace)
            .Where(part => part.Length > 0)
            .ToList();

        var lines = new List<QuantityScaleLine>();
        foreach (var part in parts.Skip(1))
        {
            if (LooksLikeQuantityListBoundary(part))
            {
                break;
            }

            if (TryScaleQuantitySegment(part, factor, out var scaled))
            {
                lines.Add(new QuantityScaleLine(part, scaled, IsNumeric: true));
                continue;
            }

            if (TryKeepQualitativeQuantitySegment(part, out var qualitative))
            {
                lines.Add(new QuantityScaleLine(part, qualitative, IsNumeric: false));
            }
        }

        return lines;
    }

    private static bool LooksLikeQuantityListBoundary(string text)
    {
        var normalized = NormalizeLooseLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
                normalized,
                @"\b(?:preparation|procedure|procedures?|process|execution|operation|operations|instructions?|method|methods?|methode|methodes|mode\s+operatoire|etape|etapes|steps?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d{1,3}\s*[\).\-]\s+\p{L}", RegexOptions.CultureInvariant);
    }

    private static bool TryKeepQualitativeQuantitySegment(string segment, out string scaled)
    {
        scaled = string.Empty;
        var cleaned = CollapseWhitespace(segment ?? string.Empty).Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        var normalized = NormalizeLexicalLookup(cleaned);
        if (!Regex.IsMatch(normalized, @"^[\p{L}'\-\s]{3,70}$", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"\b(?:preparation|procedure|method|methode|etapes?|steps?|instructions?|requirements?|values?|materiel|materials?|equipment|tools?|outils?)\b", RegexOptions.CultureInvariant))
            return false;

        scaled = $"{cleaned} (sans quantite sourcee)";
        return true;
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
        if (LooksLikeNonScalableQuantitySegment(rest))
            return false;

        var scaledNumber = number * factor;
        scaled = FormatScaleNumber(scaledNumber) + " " + rest;
        return true;
    }

    private static bool LooksLikeNonScalableQuantitySegment(string rest)
    {
        var normalized = NormalizeLooseLookup(rest);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"^(?:min|mins|minutes?|h|hr|hrs|heures?|hours?|s|sec|secs|secondes?|seconds?|jour|jours|days?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|%|mm|cm|m|km|nm|eur|euro|euros|chf|usd|gbp)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:celsius|fahrenheit|degrees?|degres?)\b|^°\s*c\b|^c\s*$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:items?|elements?|quantities?|quantites?|amounts?|values?|valeurs?|requirements?|materials?|materiel|components?|composants?|supplies|entries?)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:units?|unites?|personnes?|persons?|people)\s*:?$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:temperature|temp|pressure|pression|voltage|tension|current|courant|speed|vitesse|frequency|frequence|torque|couple|force|setting|reglage|parametre|parameter|limit|limite|threshold|seuil|tolerance|clearance|jeu|distance|dimension|diameter|diametre|angle|slope|pente|concentration|dosage|ph)\b",
            RegexOptions.CultureInvariant);
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
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        var targetFacts = ExtractSourceBackedAdaptationTargetFacts(selected, query, maxFacts: 5, language);
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
                "Ce qui vient des documents :",
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
                "Faire l'adaptation par petits paliers et verifier le resultat : si la cible participe a une contrainte fonctionnelle, reglementaire, de securite ou de performance, la validation devient obligatoire.",
                "Pour une version finale operationnelle, choisir une page source citee afin de garder visibles toutes les contraintes documentees."
            }
        };
    }

    private static IReadOnlyList<string> ExtractSourceBackedAdaptationTargetFacts(IReadOnlyList<RagHitSummary> hits, string query, int maxFacts, string language)
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
            facts.Add($"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart} : {string.Join("; ", segments)}");
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

        return terms
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 3)
            .Where(static term => !Regex.IsMatch(term, @"\b(?:document|documents|pdf|source|sources|adaptation|prudente|base)\b", RegexOptions.CultureInvariant))
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

        var previousWasItemizedLabel = false;
        foreach (var segment in Regex.Split(text, @"(?:[•\n\r]|(?<=[.;:])\s+)"))
        {
            var value = CollapseWhitespace(segment).Trim(' ', '-', ':', ';', ',');
            if (value.Length < 5)
                continue;
            if (value.Length > 140)
                value = value[..140].TrimEnd() + "...";

            var normalizedSegment = NormalizeLexicalLookup(value);
            var carriesItemizedContext = previousWasItemizedLabel;
            previousWasItemizedLabel = Regex.IsMatch(
                normalizedSegment,
                @"^\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|materials?|materiel|mat[eé]riel|components?|composants?)\b$",
                RegexOptions.CultureInvariant);
            if (!targetGroups.Any(group => group.Any(term => term.Length >= 3 && normalizedSegment.Contains(term, StringComparison.Ordinal))))
                continue;

            var hasSpecificSignal = Regex.IsMatch(
                normalizedSegment,
                @"\b\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12}|min|h)\b|\b(?:quantite|quantites|amount|quantity|constraint|contrainte)\b",
                RegexOptions.CultureInvariant);
            if (!hasSpecificSignal
                && (carriesItemizedContext || LooksLikeDelimitedTargetFact(value))
                && value.Contains(',', StringComparison.Ordinal))
            {
                hasSpecificSignal = true;
            }
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
        if (LooksLikeTechnicalRankingQuery(query))
        {
            var structuredEnoughRanked = ranked
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughRanked.Count > 0)
                ranked = structuredEnoughRanked;

            var nonVeryShortRanked = ranked
                .Where(static hit => ExtractBestVisibleDurationMinutes(hit) is not int minutes || minutes > 3)
                .ToList();
            if (nonVeryShortRanked.Count > 0)
                ranked = nonVeryShortRanked;
        }
        if (ranked.Count == 0)
            return string.Empty;

        var labels = BuildSourceBackedRankingLabels(language);
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);

        var winner = ranked[0];
        sb.Append("- ");
        sb.Append(labels.MainCandidate);
        sb.Append(" : ");
        sb.Append(FormatRankingHitReference(language, winner));
        sb.Append(" - ");
        sb.AppendLine(BuildSourceBackedRankingReason(language, winner, query));

        if (ranked.Count > 1)
        {
            sb.AppendLine(labels.OtherCandidates);
            foreach (var hit in ranked.Skip(1).Take(3))
            {
                sb.Append("- ");
                sb.Append(FormatRankingHitReference(language, hit));
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

        var technicalRanking = LooksLikeTechnicalRankingQuery(query);
        if (technicalRanking)
        {
            var nonGenericCandidates = candidateHits
                .Where(static hit => !LooksLikeGenericTechniqueDefinitionHit(hit))
                .ToList();
            if (nonGenericCandidates.Count > 0)
                candidateHits = nonGenericCandidates;
        }

        var allCandidateHits = candidateHits;
        var dominantHits = FilterHitsToDominantTopLevel(candidateHits, query).ToList();
        var dominantTargetHits = FilterRankingHitsToRequestedContentTarget(dominantHits, query).ToList();
        var allTargetHits = FilterRankingHitsToRequestedContentTarget(allCandidateHits, query).ToList();
        if (!TargetFilterHasEnoughStructuredRankingSupport(allCandidateHits, allTargetHits))
        {
            dominantTargetHits = dominantHits;
            allTargetHits = allCandidateHits;
        }

        candidateHits = dominantHits;
        var targetFilteredHits = dominantTargetHits;
        if (allTargetHits.Count < allCandidateHits.Count
            && dominantTargetHits.Count == dominantHits.Count)
        {
            candidateHits = allTargetHits;
            targetFilteredHits = allTargetHits;
        }
        if (technicalRanking)
        {
            var targetHasStructuredEvidence = targetFilteredHits.Any(static hit => !LooksLikeLowStructureShortProcedureHit(hit));
            var allHitsHaveStructuredEvidence = candidateHits.Any(static hit => !LooksLikeLowStructureShortProcedureHit(hit));
            if (targetHasStructuredEvidence || !allHitsHaveStructuredEvidence)
                candidateHits = targetFilteredHits;
        }
        else
        {
            candidateHits = targetFilteredHits;
        }
        if (technicalRanking)
        {
            var singlePageHits = candidateHits
                .Where(static hit => hit.PageEnd <= hit.PageStart)
                .ToList();
            if (singlePageHits.Count >= 2)
                candidateHits = singlePageHits;

            var structuredEnoughHits = candidateHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                candidateHits = structuredEnoughHits;

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

            var nonMidProcedureFragmentHits = candidateHits
                .Where(static hit => !LooksLikeMidProcedureFragment(hit))
                .ToList();
            if (nonMidProcedureFragmentHits.Count > 0)
                candidateHits = nonMidProcedureFragmentHits;

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

            structuredEnoughHits = candidateHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                candidateHits = structuredEnoughHits;
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

    private static bool TargetFilterHasEnoughStructuredRankingSupport(
        IReadOnlyList<RagHitSummary> allHits,
        IReadOnlyList<RagHitSummary> targetHits)
    {
        if (targetHits.Count == 0 || targetHits.Count == allHits.Count)
            return true;

        if (targetHits.All(LooksLikeMidProcedureFragment)
            && allHits.Any(hit => !LooksLikeMidProcedureFragment(hit) && HasStrongStructuredRankingEvidence(hit)))
        {
            return false;
        }

        if (targetHits.Any(HasStrongStructuredRankingEvidence))
            return true;

        var targetKeys = targetHits
            .Select(BuildRagHitIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !allHits
            .Where(hit => !targetKeys.Contains(BuildRagHitIdentityKey(hit)))
            .Any(HasStrongStructuredRankingEvidence);
    }

    private static bool HasStrongStructuredRankingEvidence(RagHitSummary hit)
        => ComputeProcedureCompletenessCueScore(hit) >= 7
            || ComputeStructuredProcedureEvidenceCueScore(hit) >= 7
            || ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 7;

    private static string BuildRagHitIdentityKey(RagHitSummary hit)
        => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}";

    private static IReadOnlyList<RagHitSummary> FilterRankingHitsToRequestedContentTarget(IReadOnlyList<RagHitSummary> hits, string query)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var targetTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Where(static term => !Regex.IsMatch(
                term,
                @"^(?:quel|quelle|quels|quelles|which|what|plus|moins|meilleur|meilleure|best|worst|most|least|technique|technical|complexe|complex|difficile|difficult|classement|ranking|candidat|candidate)$",
                RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (targetTerms.Length == 0)
            return hits;

        var matchingHits = hits
            .Where(hit =>
            {
                var lookup = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {GetRagHitPrimaryContentText(hit)}");
                return targetTerms.Any(term => lookup.Contains(term, StringComparison.Ordinal));
            })
            .ToList();

        return matchingHits.Count > 0 ? matchingHits : hits;
    }

    private static double ComputeSourceBackedRankingScore(RagHitSummary hit, string query, string evidenceQuery)
    {
        var score = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(hit)) * 3
            + ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit))
            + Math.Min(3.0, Math.Max(0.0, hit.Score));

        if (LooksLikeTechnicalRankingQuery(query))
            score += ComputeTechnicalEvidenceCueScore(hit);

        var structuredProcedureScore = 0;
        if (LooksLikeProcedureRankingQuery(query))
        {
            structuredProcedureScore = ComputeStructuredProcedureEvidenceCueScore(hit);
            score += structuredProcedureScore;
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
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitLookupText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static int ComputeTechnicalComplexityCueScore(RagHitSummary hit)
    {
        var rawText = GetRagHitPrimaryEvidenceText(hit);
        var text = NormalizeLexicalLookup(rawText);
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var labelCount = CountLabelValueMarkers(rawText);
        var stepCount = CountProcedureStepMarkers(text);
        var measuredCount = CountMeasuredValueMarkers(text);

        var score = 0;
        if (labelCount > 0 || stepCount >= 3)
            score += Math.Min(5, measuredCount);
        score += Math.Min(4, labelCount * 2);
        score += Math.Min(4, stepCount);
        score += Math.Min(3, CountBulletListMarkers(rawText));

        if (HasMeasurableConstraintEvidence(rawText, text))
            score += 3;
        if (labelCount >= 2 || stepCount >= 4)
            score += 2;
        if (ScoreMatchedContentCardShape(hit.MatchedContentCards) >= 3)
            score += 1;

        return score;
    }

    private static int ComputeProcedureCompletenessCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryContentText(hit), includeBackendHints: true),
            -4,
            14);
    }

    private static int CountProcedureStepMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var numeric = Regex.Matches(
            normalizedText,
            @"(?:^|[\r\n.;:]\s*|\s)[1-9]\d{0,2}[\.)]\s+\S",
            RegexOptions.CultureInvariant).Count;
        var bullets = Regex.Matches(
            normalizedText,
            @"[•\-]\s*(?:\p{L}{4,})(?:\s+\p{L}{2,}){2,}",
            RegexOptions.CultureInvariant).Count;

        return numeric + bullets;
    }

    private static int ComputeEvidenceShapeScore(
        RagHitSummary hit,
        string? evidenceText,
        bool includeBackendHints)
    {
        var rawText = evidenceText ?? string.Empty;
        var normalizedText = NormalizeStructuredScanText(rawText);
        if (string.IsNullOrWhiteSpace(normalizedText)
            && hit.MatchedContentCards is not { Count: > 0 }
            && string.IsNullOrWhiteSpace(hit.SelectionHintRole))
        {
            return 0;
        }

        var score = 0;
        if (includeBackendHints)
        {
            score += ScoreEvidenceRoleHint(hit);
            score += ScoreSelectionHintNumbers(hit);
        }

        if (hit.ExactMatchHit)
            score += 3;

        var retriever = NormalizeLexicalLookup(hit.Retriever);
        if (retriever.Contains("exact", StringComparison.Ordinal))
            score += 2;
        else if (retriever.Contains("sparse", StringComparison.Ordinal))
            score += 1;

        if (!string.IsNullOrWhiteSpace(hit.SectionTitle) || !string.IsNullOrWhiteSpace(hit.HeadingPath))
            score += 1;
        if (HasSourceBackedProfileTitle(hit))
            score += 2;

        score += ScoreMatchedContentCardShape(hit.MatchedContentCards);
        score += Math.Min(4, CountProcedureStepMarkers(rawText));
        score += Math.Min(3, CountBulletListMarkers(rawText));
        score += Math.Min(3, CountNumericFactMarkers(normalizedText));
        score += Math.Min(2, CountLabelValueMarkers(rawText));

        if (Regex.IsMatch(rawText, @"\|.+\|", RegexOptions.CultureInvariant))
            score += 1;

        if (hit.ManualReviewRecommended)
            score -= 1;
        if (hit.ExtractionConfidence.HasValue && hit.ExtractionConfidence.Value < 0.55)
            score -= 2;

        return Math.Clamp(score, -8, 18);
    }

    private static int ScoreEvidenceRoleHint(RagHitSummary hit)
    {
        return NormalizeRagEvidenceRole(hit.SelectionHintRole) switch
        {
            "actionable_item" => 4,
            "advisory" => 2,
            "supporting_context" => 1,
            "fragment" => -4,
            "navigation" => -5,
            "low_confidence" => -4,
            _ => 0
        };
    }

    private static int ScoreSelectionHintNumbers(RagHitSummary hit)
    {
        var score = 0;
        score += ScorePositiveHint(hit.SelectionHintActionabilityScore, high: 9, medium: 7, low: 5, highScore: 5, mediumScore: 3, lowScore: 1);
        score += ScorePositiveHint(hit.SelectionHintSupportScore, high: 8, medium: 5, low: 3, highScore: 2, mediumScore: 1, lowScore: 0);
        score -= ScorePositiveHint(hit.SelectionHintFragmentScore, high: 8, medium: 5, low: 3, highScore: 4, mediumScore: 2, lowScore: 1);
        score -= ScorePositiveHint(hit.SelectionHintNavigationScore, high: 8, medium: 5, low: 3, highScore: 4, mediumScore: 2, lowScore: 1);
        score -= ScorePositiveHint(hit.SelectionHintQualityPenalty, high: 8, medium: 5, low: 3, highScore: 3, mediumScore: 2, lowScore: 1);
        return score;
    }

    private static int ScorePositiveHint(
        int? value,
        int high,
        int medium,
        int low,
        int highScore,
        int mediumScore,
        int lowScore)
    {
        if (!value.HasValue)
            return 0;
        if (value.Value >= high)
            return highScore;
        if (value.Value >= medium)
            return mediumScore;
        return value.Value >= low ? lowScore : 0;
    }

    private static int ScoreMatchedContentCardShape(IReadOnlyList<RagHitContentCardSummary>? cards)
    {
        if (cards is not { Count: > 0 })
            return 0;

        var score = 0;
        foreach (var card in cards.Take(5))
        {
            if (!string.IsNullOrWhiteSpace(card.Title) && IsUsefulSourceBackedDisplayTitle(card.Title))
                score += 1;

            var kind = NormalizeLexicalLookup(card.Kind);
            if (kind.Contains("unit", StringComparison.Ordinal)
                || kind.Contains("exact", StringComparison.Ordinal)
                || kind.Contains("lead", StringComparison.Ordinal))
            {
                score += 2;
            }

            if (card.Signals is { Count: > 0 })
                score += 1;
        }

        return Math.Min(7, score);
    }

    private static int CountBulletListMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(
            text,
            @"(?:^|[\r\n]\s*)[-*+\u2022\u00b7]\s*\S+(?:\s+\S+){2,}",
            RegexOptions.CultureInvariant).Count;
    }

    private static int CountNumericFactMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return Regex.Matches(
            normalizedText,
            @"\b\d+(?:[,.]\d+)?(?:\s*(?:%|\p{L}{1,12}\.?|\p{Sc}))?\b",
            RegexOptions.CultureInvariant).Count;
    }

    private static int CountMeasuredValueMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return Regex.Matches(
            normalizedText,
            @"\b\d+(?:[,.]\d+)?\s*(?:%|\p{L}{1,12}\.?|\p{Sc})\b|(?:<=|>=|<|>|=)\s*\d",
            RegexOptions.CultureInvariant).Count;
    }

    private static bool HasMeasurableConstraintEvidence(string rawText, string normalizedText)
        => CountLabelValueMarkers(rawText) >= 1
           || (CountProcedureStepMarkers(normalizedText) >= 3 && CountMeasuredValueMarkers(normalizedText) >= 2)
           || Regex.IsMatch(normalizedText, @"(?:^|\s)(?:<=|>=|<|>|=)\s*\d", RegexOptions.CultureInvariant);

    private static bool HasExplicitConstraintEvidence(string rawText, string normalizedText)
        => CountLabelValueMarkers(rawText) >= 1
           || Regex.IsMatch(normalizedText, @"(?:^|\s)(?:<=|>=|<|>|=)\s*\d", RegexOptions.CultureInvariant);

    private static bool LooksLikeVeryShortTimedOperation(string normalizedText)
        => Regex.IsMatch(normalizedText, @"\b(?:1|2|3)\s*(?:s|sec|secs|min|h|hr|hrs)\b", RegexOptions.CultureInvariant);

    private static int CountLabelValueMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(
            text,
            @"(?:^|[\r\n.;]\s*)[\p{L}\p{N}][^:\r\n]{2,48}:\s+\S",
            RegexOptions.CultureInvariant).Count;
    }

    private static bool LooksLikeMidProcedureFragment(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasItemizedData = Regex.IsMatch(text, @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant);
        var hasEarlySteps = Regex.IsMatch(text, @"(?:^|\s|preparation|technique)[\s:]*[1-3][\.)]\s*", RegexOptions.CultureInvariant);
        var hasLateSteps = Regex.IsMatch(text, @"(?:^|\s)[4-9][\.)]\s*", RegexOptions.CultureInvariant);
        var startsMidSentence = Regex.IsMatch(
            text,
            @"^(?:arreter|ajouter|appliquer|verifier|controler|mesurer|regler|ajuster|retirer|transvider|lorsque|quand|stop|add|apply|verify|check|measure|set|adjust|when|remove)\b",
            RegexOptions.CultureInvariant);

        return (startsMidSentence && !hasItemizedData)
            || (hasLateSteps && !hasEarlySteps && !hasItemizedData);
    }

    private static bool LooksLikeIntroLeadInHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = text.Length <= 360 ? text : text[..360];
        var hasIntroLead = Regex.IsMatch(
            lead,
                @"\b(?:bienvenue|introduction|avant propos|preface|sommaire|table des matieres|reperes de contenu|premiers extraits)\b",
            RegexOptions.CultureInvariant);
        if (!hasIntroLead)
            return false;

        var firstStructure = Regex.Match(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|preparation|preparacion|preparacao|procedure|instructions?|(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}|\d+\s+(?:units?|items?))\b",
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
        var hasVeryShortDuration = LooksLikeVeryShortTimedOperation(text);
        var hasControlledParameter = HasExplicitConstraintEvidence(GetRagHitPrimaryContentText(hit), text)
            || CountProcedureStepMarkers(text) >= 4;
        var lacksControlledParameter = !hasControlledParameter;

        if (hasSimpleWording && hasVeryShortDuration && !hasControlledParameter)
            return true;
        if (hasVeryShortDuration && !hasControlledParameter && CountProcedureStepMarkers(text) <= 3)
            return true;

        return (hasSimpleWording || hasVeryShortDuration)
            && lacksControlledParameter
            && ComputeTechnicalComplexityCueScore(hit) < 9;
    }

    private static bool LooksLikeLowStructureShortProcedureHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup($"{GetRagHitPrimaryContentText(hit)} {GetRagHitLookupText(hit)}");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasControlledParameter = HasExplicitConstraintEvidence(GetRagHitPrimaryContentText(hit), text)
            || CountProcedureStepMarkers(text) >= 4;
        var hasVeryShortOperation = LooksLikeVeryShortTimedOperation(text);
        var hasLowStructure = CountProcedureStepMarkers(text) <= 3
            && Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|min|h)\b", RegexOptions.CultureInvariant).Count < 5;

        if (!hasControlledParameter && hasVeryShortOperation && hasLowStructure)
            return true;
        if (!hasControlledParameter && hasVeryShortOperation && ComputeTechnicalComplexityCueScore(hit) < 9)
            return true;
        if (!hasControlledParameter
            && hasVeryShortOperation
            && Regex.IsMatch(text, @"\b(?:immediat|immediate|instant|quick|rapide)\b", RegexOptions.CultureInvariant))
            return true;

        return !hasControlledParameter
            && hasLowStructure
            && ComputeTechnicalComplexityCueScore(hit) < 12;
    }

    private static bool LooksLikeGenericTechniqueDefinitionHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasDefinitionWording = Regex.IsMatch(
            text,
            @"\b(?:cette\s+technique\s+consiste|technique\s+consiste|mode\s+de\s+preparation|modes\s+de\s+preparation|preparation\s+method|process\s+method|procedure\s+method)\b",
            RegexOptions.CultureInvariant);
        var hasGenericTechniqueHeading = Regex.IsMatch(
            text,
            @"\b(?:technique|method|methode|procedure)\s*[:\-]",
            RegexOptions.CultureInvariant);
        var hasGenericTechniqueOverview = Regex.IsMatch(
            text,
            @"\b(?:technique|method|methode|procedure)\s+(?:generale|general|overview|vue\s+d\s+ensemble)\b",
            RegexOptions.CultureInvariant);
        if (!hasDefinitionWording && !hasGenericTechniqueHeading && !hasGenericTechniqueOverview)
            return false;

        var hasStructuredItemEvidence = Regex.IsMatch(
                text,
                @"\b(?:items?|elements?|requirements?|components?|composants?|materials?|materiel|equipment|preparation\s*[:•]|procedure\s*[:•]|method\s*[:•])\b",
                RegexOptions.CultureInvariant)
            || CountProcedureStepMarkers(text) >= 2
            || Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|min|h)\b", RegexOptions.CultureInvariant).Count >= 2;

        return !hasStructuredItemEvidence
            && (!hasGenericTechniqueHeading || ComputeProcedureCompletenessCueScore(hit) < 7);
    }

    private static bool LooksLikeProcedureRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:procedura|procedure|procedures|procedimento|process|processus|method|methode|instruction|instructions|preparation|preparacion|preparacao|etape|etapes|steps|pasos|passos)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeStructuredProcedureEvidenceCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryEvidenceText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static int ComputeStructuredProcedureVisibleEvidenceCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryContentText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static string FormatRankingHitReference(string language, RagHitSummary hit)
    {
        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        return $"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart}";
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

        var excerptLabel = SourceBackedLabel(language, "Extrait", "Excerpt", "Extracto", "Excerto", "Auszug", "Estratto");
        return $"{because} {string.Join(", ", reasons)}. {excerptLabel}: {excerpt}";
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

        if (HasMeasurableConstraintEvidence(GetRagHitLookupText(hit), text))
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

        if (ScoreEvidenceRoleHint(hit) > 0
            || CountLabelValueMarkers(GetRagHitLookupText(hit)) > 0
            || CountProcedureStepMarkers(text) >= 3)
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "it has structured or constraint-bearing evidence",
                "es" => "aporta evidencia estructurada o con restricciones",
                "pt" => "traz evidencia estruturada ou com restricoes",
                "de" => "er enthaelt strukturierte oder einschraenkende Nachweise",
                "it" => "contiene evidenza strutturata o vincolante",
                _ => "il contient des indices structures ou contraints"
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

        var hasStructuredExactItemEvidence = hits.Any(hit =>
            RagHitContainsRequestedTitle(hit, requestedTitle)
            && (ComputeExactItemCardCompletenessCueScore(hit) >= 8
                || HasContentCardEvidenceFacts(hit)));
        if (LooksLikeStructuredItemCardRequest(query) || hasStructuredExactItemEvidence)
            return BuildSourceBackedExactItemCardAnswer(language, requestedTitle, hits, header);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            if (ShouldIncludeRawSourceExcerptForAnswerLanguage(language, hit))
            {
                var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: SourceBackedEvidenceMaxChars);
                sb.AppendLine(excerpt);
            }
            else
            {
                sb.AppendLine(SourceBackedLabel(
                    language,
                    "extrait disponible dans la langue du document sur la page citee",
                    "excerpt available in the document language on the cited page",
                    "extracto disponible en el idioma del documento en la pagina citada",
                    "excerto disponivel na lingua do documento na pagina citada",
                    "Auszug in der Dokumentsprache auf der zitierten Seite verfuegbar",
                    "estratto disponibile nella lingua del documento nella pagina citata"));
            }
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
        AppendSourceBackedExtractionQualityCaveat(sb, hits, language);
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

        if (candidate.StartsWith(requested, StringComparison.Ordinal))
        {
            return !Regex.IsMatch(candidate[requested.Length..], @"^\s+(?:de|du|des|a|au|aux|with|and|et)\b", RegexOptions.CultureInvariant);
        }

        var index = candidate.IndexOf(requested, StringComparison.Ordinal);
        if (index is > 0 and <= 80)
        {
            var prefix = candidate[..index].Trim();
            return prefix.Length >= 12
                && Regex.IsMatch(prefix, @"\b(?:ajouter|add|apres|after|avant|before|faire|laisser|let|place|placer|prevoir|prevoyez|put|remuer|stir|verser)\b", RegexOptions.CultureInvariant);
        }

        return false;
    }

    private static IEnumerable<string> ExtractSourceBackedTitleCandidates(RagHitSummary hit)
    {
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                yield return card.Title;
        }

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
            @"(?i)\bMatched\s+(?:profile|quoted)\s+title\s*:\s*(?<titles>.+?)(?=\s*(?:\|\s*)?(?:Document|Section|HeadingPath|ChunkType|Pages|Evidence)\s*:|$)"))
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

        if (LooksLikeProcedureSentenceTitle(normalized))
            return false;

        return normalized is not "document" and not "document profile";
    }

    private static bool LooksLikeProcedureSentenceTitle(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an)?\s*(?:ajouter|add|appliquer|apply|check|close|control|controler|do|faire|fermer|lancer|launch|laisser|laissez|leave|make|mettre|occuper|occupez|organiser|organize|ouvrir|open|planifier|planifiez|put|schedule|set|utiliser|use|using|valider|validate|verifier|verify)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalizedTitle,
            @"\b(?:\d+\s*(?:min|h|hours?|minutes?|seconds?|secondes?|%|(?:\u00b0|deg|degres?)\s*c)|step\s+\d+|etape\s+\d+|page\s+\d+|section\s+\d+)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalizedTitle, @"[.;:!?]\s+\p{L}", RegexOptions.CultureInvariant);
    }

    private static int ComputeSourceBackedDisplayTitleScore(string requestedTitle, string candidateTitle)
    {
        var requested = NormalizeLexicalLookup(requestedTitle);
        var candidate = NormalizeLexicalLookup(candidateTitle);
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
            return 0;

        if (string.Equals(candidate, requested, StringComparison.Ordinal))
            return 100;

        var requestSeeds = new List<string> { requested };
        requestSeeds.AddRange(BuildTypoTolerantQueryVariants(requested).Select(NormalizeLexicalLookup));
        var requestTerms = requestSeeds
            .SelectMany(ExtractRequestedTitleSignalTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestTerms.Length == 0)
            return 0;

        var matchedIndexes = requestTerms
            .Select((term, index) => new { Term = term, Index = index })
            .Where(item => candidate.Contains(item.Term, StringComparison.Ordinal))
            .Select(static item => item.Index)
            .Distinct()
            .OrderBy(static index => index)
            .ToArray();
        if (matchedIndexes.Length == 0)
            return 0;

        var score = matchedIndexes.Length * 20;
        score += matchedIndexes.Sum(index => Math.Max(1, requestTerms.Length - index) * 3);
        if (matchedIndexes.SequenceEqual(Enumerable.Range(0, matchedIndexes.Length)) && matchedIndexes.Length >= 2)
            score += 20;
        if (matchedIndexes[0] > 0)
            score -= matchedIndexes[0] * 10;
        if (matchedIndexes.Length == requestTerms.Length)
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
        if (Regex.IsMatch(
                normalized,
                @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|components?|composants?|preparation|technique|materiel|material|materials|procedure|process|method|methode)\b",
                RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(
            normalized,
            @"\b(?:fiche|card|item|items|element|elements|etape|etapes|steps|temps|time|source|sources|procedure|process|values?|valeurs?)\b",
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
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(string.Join("; ", facts));
        }

        if (!Regex.IsMatch(sb.ToString(), @"\s(?:p\.|S\.)\d+", RegexOptions.CultureInvariant))
        {
            foreach (var hit in hits.Take(2))
            {
                var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
                sb.Append("- ");
                sb.Append(docLabel);
                sb.Append(' ');
                sb.Append(SourceBackedPagePrefix(language));
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
            .Where(hit => ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit)
                || RagHitContainsRequestedTitle(hit, requestedTitle)
                || RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit))
            .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
            .ThenByDescending(hit => hit.Score)
            .ToList();
        var structuredCompanions = hits
            .Where(hit => hit.PageEnd > hit.PageStart || !LooksLikeNavigationOnlyHit(hit))
            .Where(hit => ComputeExactItemCardCompletenessCueScore(hit) >= 8 || hit.PageEnd > hit.PageStart)
            .Where(hit => !cardHits.Any(existing => SameRagHitRange(existing, hit)))
            .ToList();
        if (structuredCompanions.Count > 0)
        {
            cardHits = cardHits
                .Concat(structuredCompanions)
                .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }

        if (cardHits.Count == 0)
        {
            cardHits = hits
                .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }

        var primary = cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && !LooksLikeNavigationOnlyHit(hit)
                && RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit)
                && ComputeExactItemCardCompletenessCueScore(hit) >= 4)
            ?? cardHits.FirstOrDefault(hit =>
                !LooksLikePageReferenceOnlyHit(hit)
                && !LooksLikeNavigationOnlyHit(hit)
                && RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit))
            ?? cardHits.First();
        var primaryDoc = string.IsNullOrWhiteSpace(primary.DocName) ? primary.DocPath : primary.DocName;
        var evidenceHits = SelectExactItemCardEvidenceHits(cardHits, primary);
        var evidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var itemizedEvidence = CollapseWhitespace(string.Join(' ', evidenceHits.Select(hit => GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit))));
        var procedureEvidence = CollapseWhitespace(string.Join(' ', evidenceHits
            .OrderByDescending(hit => ComputeExactItemProcedureEvidenceScore(requestedTitle, hit))
            .Select(hit => GetFocusedExactItemEvidenceText(requestedTitle, hit))));
        var visibleDurationsAndQuantities = ExtractVisibleDurationsAndQuantities(procedureEvidence).Take(8).ToArray();
        var itemizedFacts = ExtractItemizedQuantityFacts(itemizedEvidence).Take(10).ToArray();
        var steps = ExtractProcedureSteps(procedureEvidence).Take(10).ToArray();
        var cardEvidenceFacts = ExtractContentCardEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardQuantityEvidenceFacts = ExtractContentCardQuantityEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardGenericEvidenceFacts = ExtractContentCardGenericEvidenceFacts(evidenceHits).Take(12).ToArray();
        var cardNonScalableReasons = ExtractContentCardNonScalableReasons(evidenceHits).Take(8).ToArray();
        var hasCardQuantityEvidence = HasContentCardQuantityEvidence(evidenceHits);
        var hasAnyCardEvidence = HasContentCardEvidence(evidenceHits);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(SourceBackedLabel(language, "Fiche sourcee :", "Source-backed card:", "Ficha con fuente:", "Ficha com fonte:", "Belegte Karte:", "Scheda con fonte:"));
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Source principale", "Main source", "Fuente principal", "Fonte principal", "Hauptquelle", "Fonte principale"));
        sb.Append(" : ");
        sb.Append(primaryDoc);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.AppendLine(primary.PageStart.ToString(CultureInfo.InvariantCulture));

        if (!ShouldIncludeRawSourceExcerptForAnswerLanguage(language, primary))
        {
            sb.Append("- ");
            sb.Append(SourceBackedLabel(
                language,
                "Contenu visible",
                "Visible content",
                "Contenido visible",
                "Conteudo visivel",
                "Sichtbarer Inhalt",
                "Contenuto visibile"));
            sb.Append(" : ");
            sb.AppendLine(SourceBackedLabel(
                language,
                "les elements, valeurs, temps et etapes doivent etre lus sur la page citee dans la langue du document.",
                "items, values, timing and steps should be read on the cited page in the document language.",
                "los elementos, valores, tiempos y pasos deben leerse en la pagina citada, en el idioma del documento.",
                "os elementos, valores, tempos e etapas devem ser lidos na pagina citada, na lingua do documento.",
                "Elemente, Werte, Zeiten und Schritte sind auf der zitierten Seite in der Dokumentsprache zu lesen.",
                "elementi, valori, tempi e passaggi vanno letti nella pagina citata, nella lingua del documento."));
            return sb.ToString().TrimEnd();
        }

        if (hasAnyCardEvidence)
        {
            if (hasCardQuantityEvidence)
            {
                AppendFactList(sb, SourceBackedLabel(language, "Durees / quantites visibles", "Visible durations / quantities", "Duraciones / cantidades visibles", "Duracoes / quantidades visiveis", "Sichtbare Dauern / Mengen", "Durate / quantita visibili"), cardQuantityEvidenceFacts, language);
            }

            if (cardGenericEvidenceFacts.Length > 0 || !hasCardQuantityEvidence)
            {
                var genericFacts = cardGenericEvidenceFacts.Length > 0 ? cardGenericEvidenceFacts : cardEvidenceFacts;
                AppendFactList(sb, SourceBackedLabel(language, "Informations visibles", "Visible information", "Informacion visible", "Informacao visivel", "Sichtbare Informationen", "Informazioni visibili"), genericFacts, language);
            }

            if (cardNonScalableReasons.Length > 0)
            {
                AppendFactList(sb, SourceBackedLabel(language, "Contraintes non adaptables", "Non-scalable constraints", "Restricciones no adaptables", "Restricoes nao adaptaveis", "Nicht skalierbare Vorgaben", "Vincoli non scalabili"), cardNonScalableReasons, language);
            }
        }
        else if (primary.MatchedContentCards is { Count: > 0 }
                 || evidenceHits.Any(static hit => hit.MatchedContentCards is { Count: > 0 }))
        {
            var visibleInfo = visibleDurationsAndQuantities
                .Concat(itemizedFacts)
                .Concat(steps)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            AppendFactList(sb, SourceBackedLabel(language, "Informations visibles", "Visible information", "Informacion visible", "Informacao visivel", "Sichtbare Informationen", "Informazioni visibili"), visibleInfo, language);
        }
        else
        {
            AppendFactList(sb, SourceBackedLabel(language, "Durees / quantites visibles", "Visible durations / quantities", "Duraciones / cantidades visibles", "Duracoes / quantidades visiveis", "Sichtbare Dauern / Mengen", "Durate / quantita visibili"), visibleDurationsAndQuantities, language);
            AppendFactList(sb, SourceBackedLabel(language, "Elements / quantites visibles", "Visible items / quantities", "Elementos / cantidades visibles", "Elementos / quantidades visiveis", "Sichtbare Elemente / Mengen", "Elementi / quantita visibili"), itemizedFacts, language);
            AppendFactList(sb, SourceBackedLabel(language, "Etapes visibles", "Visible steps", "Pasos visibles", "Passos visiveis", "Sichtbare Schritte", "Passaggi visibili"), steps, language);
        }
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
        var itemizedEvidence = GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit);
        var displayTitleScore = ComputeBestSourceBackedDisplayTitleScore(requestedTitle, hit);
        var score = 0;
        if (ExactItemEvidenceTextMatchesRequestedTitle(requestedTitle, hit))
            score += 20;
        else if (RagHitContainsRequestedTitle(hit, requestedTitle))
            score += ComputeExactItemCardCompletenessCueScore(hit) >= 4 ? 10 : -12;
        if (displayTitleScore >= 40)
            score += 60 + displayTitleScore;
        else if (ExtractRequestedTitleSignalTerms(NormalizeLexicalLookup(requestedTitle)).Length >= 3 && displayTitleScore < 20)
            score -= 40;
        score += ComputeExactItemHeadTermEvidenceScore(requestedTitle, hit);
        score += ComputeExactItemAnchorStrengthScore(requestedTitle, hit);
        score += ComputeExactVisibleTitleMatchScore(requestedTitle, hit);
        score += ExtractItemizedQuantityFacts(itemizedEvidence).Length * 4;
        score += ExtractProcedureSteps(evidence).Length * 3;
        score += ExtractVisibleDurationsAndQuantities(evidence).Length;
        score += ExtractContentCardEvidenceFacts(new[] { hit }).Length * 4;
        if (LooksLikeNavigationOnlyHit(hit))
            score -= 120;
        if (LooksLikePageReferenceOnlyHit(hit))
            score -= 220;
        return score;
    }

    private static int ComputeExactItemHeadTermEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var titleTerms = ExtractRequestedTitleSignalTerms(NormalizeLexicalLookup(requestedTitle));
        if (titleTerms.Length < 2)
            return 0;

        var headTerms = titleTerms.Take(Math.Min(2, titleTerms.Length)).ToArray();
        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var titleText = NormalizeLexicalLookup(string.Join(' ', ExtractSourceBackedTitleCandidates(hit)));

        var matchedContentHeadTerms = headTerms.Count(term => content.Contains(term, StringComparison.Ordinal));
        var matchedTitleHeadTerms = headTerms.Count(term => titleText.Contains(term, StringComparison.Ordinal));

        var score = matchedContentHeadTerms * 45 + matchedTitleHeadTerms * 25;
        if (matchedContentHeadTerms == headTerms.Length)
            score += 70;
        if (matchedTitleHeadTerms == headTerms.Length)
            score += 50;
        if (titleTerms.Length >= 3 && matchedContentHeadTerms == 0 && matchedTitleHeadTerms == 0)
            score -= 160;

        return score;
    }

    private static int ComputeBestSourceBackedDisplayTitleScore(string requestedTitle, RagHitSummary hit)
        => ExtractSourceBackedTitleCandidates(hit)
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Select(title => ComputeSourceBackedDisplayTitleScore(requestedTitle, title))
            .DefaultIfEmpty(0)
            .Max();

    private static int ComputeExactItemAnchorStrengthScore(string requestedTitle, RagHitSummary hit)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return 0;

        static bool AllTermsIn(string text, IReadOnlyList<string> terms)
        {
            var normalized = NormalizeLexicalLookup(text);
            return !string.IsNullOrWhiteSpace(normalized)
                && terms.All(term => normalized.Contains(term, StringComparison.Ordinal));
        }

        var score = 0;
        var cardTitles = string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>());
        if (AllTermsIn(cardTitles, titleTerms))
            score += 90;

        var contextualTitleText = string.Join(' ', ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty));
        if (AllTermsIn(contextualTitleText, titleTerms))
            score += 80;

        if (AllTermsIn($"{hit.SectionTitle} {hit.HeadingPath}", titleTerms))
            score += 60;

        var excerptLead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        if (excerptLead.Length > 260)
            excerptLead = excerptLead[..260];
        if (AllTermsIn(excerptLead, titleTerms))
            score += 45;

        if (AllTermsIn(hit.Excerpt ?? string.Empty, titleTerms))
            score += 25;

        if (score == 0 && AllTermsIn(GetBestExactItemEvidenceText(requestedTitle, hit), titleTerms))
            score += 8;

        return score;
    }

    private static bool LooksLikePageReferenceOnlyHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasStructuredEvidence = Regex.IsMatch(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|procedure|etapes?|steps?|\d+\s*(?:g|kg|ml|cl|l))\b",
            RegexOptions.CultureInvariant);
        if (hasStructuredEvidence)
            return false;

        return Regex.IsMatch(
            text,
            @"\b[\p{L}'\-]{4,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{2,3}\s+[\p{L}'\-]{4,}",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeExactItemReferenceOnlyHit(string requestedTitle, RagHitSummary hit)
    {
        if (LooksLikeExactItemIndexAssetOnlyHit(requestedTitle, hit))
            return true;

        var evidence = NormalizeLexicalLookup(GetBestExactItemEvidenceText(requestedTitle, hit));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var hasStructuredDetails = Regex.IsMatch(
            evidence,
            @"\b(?:" + ExactItemStructureHeadingPattern + @")\b|\b\d+\s*(?:g|kg|mg|ml|cl|l)\b|(?:^|\s)[1-9][\.)]\s+",
            RegexOptions.CultureInvariant);
        if (hasStructuredDetails)
            return false;

        if (LooksLikeNavigationOnlyHit(hit) || LooksLikePageReferenceOnlyHit(hit))
            return true;

        var titleIndex = FindApproximateRequestedTitleIndex(requestedTitle, evidence);
        if (titleIndex < 0)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                titleIndex = FindApproximateRequestedTitleIndex(variant, evidence);
                if (titleIndex >= 0)
                    break;
            }
        }

        if (titleIndex < 0)
            return false;

        var titlePageReferenceCount = Regex.Matches(
            evidence,
            @"\b[\p{L}'\-]{3,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{1,3}\b",
            RegexOptions.CultureInvariant).Count;
        return titlePageReferenceCount >= 2;
    }

    private static bool LooksLikeExactItemIndexAssetOnlyHit(string requestedTitle, RagHitSummary hit)
    {
        var candidates = new[]
            {
                hit.Excerpt,
                hit.FullText,
                string.IsNullOrWhiteSpace(hit.ContextualSnippet) ? null : StripContextualMetadataForEvidence(hit.ContextualSnippet!),
                GetBestExactItemEvidenceText(requestedTitle, hit)
            }
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(static text => CollapseWhitespace(text!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return candidates.Any(candidate => LooksLikeExactItemIndexAssetOnlyText(requestedTitle, candidate));
    }

    private static bool LooksLikeExactItemIndexAssetOnlyText(string requestedTitle, string evidence)
    {
        if (evidence.Length < 80)
            return false;

        var titleIndex = FindApproximateRequestedTitleIndex(requestedTitle, evidence);
        if (titleIndex < 0)
        {
            foreach (var variant in BuildTypoTolerantQueryVariants(requestedTitle))
            {
                titleIndex = FindApproximateRequestedTitleIndex(variant, evidence);
                if (titleIndex >= 0)
                    break;
            }
        }
        if (titleIndex < 0)
            return false;

        var titleAtTail = titleIndex >= evidence.Length * 0.45 || evidence.Length - titleIndex <= 420;
        if (!titleAtTail)
            return false;

        var afterTitle = evidence[Math.Min(titleIndex, evidence.Length)..];
        var normalizedAfterTitle = NormalizeLexicalLookup(afterTitle);
        var hasUsableDetailsAfterTitle = Regex.IsMatch(
            normalizedAfterTitle,
            @"\b(?:" + ExactItemStructureHeadingPattern + @")\b|\b\d+\s*(?:g|kg|ml|cl|l)\b|(?:^|\s)[1-9][\.)]\s+",
            RegexOptions.CultureInvariant);
        if (hasUsableDetailsAfterTitle)
            return false;

        var hasIndexCue = Regex.IsMatch(
            normalizedAfterTitle,
            @"\b(?:index|sommaire|table\s+of\s+contents|indice|inhalt)\b",
            RegexOptions.CultureInvariant);
        var assetRefCount = Regex.Matches(
            afterTitle,
            @"(?i)\b(?:[a-z]{2,}\d{4,}[a-z0-9_-]*|[a-z0-9]{4,}[_-][a-z0-9][a-z0-9_-]{5,}|[a-z]{2,4}\d*[_-][a-z0-9_-]{6,})\b",
            RegexOptions.CultureInvariant).Count;
        var multiSeparatorTokenCount = Regex.Matches(
            afterTitle,
            @"\b[\p{L}\p{N}]+(?:[_-][\p{L}\p{N}]+){2,}\b",
            RegexOptions.CultureInvariant).Count;

        return (hasIndexCue && assetRefCount + multiSeparatorTokenCount >= 2) || assetRefCount >= 3;
    }

    private static int FindApproximateRequestedTitleIndex(string requestedTitle, string text)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedTitle))
            return -1;

        var directIndex = normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (directIndex >= 0)
            return Math.Min(directIndex, Math.Max(0, text.Length - 1));

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return -1;

        var loosePattern = string.Join(@"[\s_\-./]+", titleTerms.Select(Regex.Escape));
        var looseMatch = Regex.Match(normalizedText, loosePattern, RegexOptions.CultureInvariant);
        if (looseMatch.Success)
            return Math.Min(looseMatch.Index, Math.Max(0, text.Length - 1));

        var cursor = 0;
        var firstIndex = -1;
        foreach (var term in titleTerms)
        {
            var termIndex = normalizedText.IndexOf(term, cursor, StringComparison.Ordinal);
            if (termIndex < 0)
                return -1;
            if (firstIndex < 0)
                firstIndex = termIndex;
            cursor = termIndex + term.Length;
        }

        return Math.Min(firstIndex, Math.Max(0, text.Length - 1));
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
                $@"(?<![\p{{L}}\p{{N}}]){escapedTitle}(?:\s*(?:$|[\.:;\u2022\u00b7])|(?=\s*(?:pour|items?|elements?|quantities?|preparation|procedure|temps|time|repos|rest)\b)|(?=(?:pour|items?|elements?|quantities?|preparation|procedure|temps|time|repos|rest)\b))",
                RegexOptions.CultureInvariant))
        {
            return 40;
        }

        if (evidence.Equals(normalizedTitle, StringComparison.Ordinal)
            || evidence.StartsWith(normalizedTitle + " ", StringComparison.Ordinal))
        {
            return 40;
        }

        return evidence.Contains(normalizedTitle, StringComparison.Ordinal) ? 4 : 0;
    }

    private static int ComputeExactItemProcedureEvidenceScore(string requestedTitle, RagHitSummary hit)
    {
        var evidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var score = ExtractProcedureSteps(evidence).Length * 6
            + ExtractVisibleDurationsAndQuantities(evidence).Length * 2;
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

    private static string GetFocusedExactItemStructuredEvidenceText(string requestedTitle, RagHitSummary hit)
    {
        var text = GetBestExactItemEvidenceText(requestedTitle, hit);
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var idx = string.IsNullOrWhiteSpace(normalizedTitle)
            ? -1
            : normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var prefix = text[..Math.Min(idx, text.Length)];
            var prefixLooksLikePreviousStructuredItem =
                CountQuantityLikeSignals(prefix) >= 2
                && ContainsStructuredItemHeading(prefix);
            var hasPreTitleStructuredEvidence =
                !prefixLooksLikePreviousStructuredItem
                && (Regex.IsMatch(NormalizeLexicalLookup(prefix), @"\b(?:procedure|procedures?|instructions?|method|methode|etape|etapes|steps?|preparation|technique)\b", RegexOptions.CultureInvariant)
                    || Regex.IsMatch(prefix, @"[\u2022\u00b7]\s*\p{L}{3,}", RegexOptions.CultureInvariant));
            var start = hasPreTitleStructuredEvidence
                ? Math.Max(0, Math.Min(idx, text.Length) - 760)
                : Math.Min(idx, text.Length);
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
        var structuredEvidence = BuildRagHitContentCardEvidenceText(hit);
        if (contextual.Length > excerpt.Length + 40 && ExactItemTextMatchesRequestOrStructure(requestedTitle, contextual))
            return StripContextualMetadataForEvidence(contextual);
        if (fullText.Length > excerpt.Length + 120 && ExactItemTextMatchesRequestOrStructure(requestedTitle, fullText))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);

        if (excerpt.Length >= 40)
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        if (!string.IsNullOrWhiteSpace(fullText))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
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

    private const string ItemizedSectionHeadingPattern =
        @"items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|materials?|materiel|mat[eé]riel|components?|composants?";

    private const string ProcedureSectionHeadingPattern =
        @"preparation|pr(?:e|\u00e9)paration|preparacion|prepara(?:c|\u00e7)(?:a|\u00e3)o|preparazione|zubereitung|procedure|procedures?|instruction|instructions|method|methods?|methode|methodes|m(?:e|\u00e9)thode|m(?:e|\u00e9)thodes|mode\s+operatoire|technique|etape|etapes|(?:e|\u00e9)tapes?|steps?";

    private const string ExactItemStructureHeadingPattern =
        ItemizedSectionHeadingPattern + "|" + ProcedureSectionHeadingPattern;

    private const string NonItemizedSectionHeadingPattern =
        ItemizedSectionHeadingPattern + "|" + ProcedureSectionHeadingPattern;

    private static bool ExactItemTextMatchesRequestOrStructure(string requestedTitle, string text)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedText.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length > 0 && titleTerms.All(term => normalizedText.Contains(term, StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static string TrimAfterLikelyExactItemBoundary(string text)
    {
        var value = text ?? string.Empty;
        if (value.Length < 260)
            return value;

        var boundaries = new[]
        {
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

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        return titleTerms.Length > 0 && titleTerms.All(term => evidence.Contains(term, StringComparison.Ordinal));
    }

    private static void AppendFactList(StringBuilder sb, string title, IReadOnlyList<string> items, string language)
    {
        sb.Append("- ");
        sb.Append(title);
        sb.Append(" : ");
        sb.AppendLine(items.Count == 0 ? SourceBackedNotVisibleLabel(language) : string.Join("; ", items));
    }

    private static string[] ExtractContentCardEvidenceFacts(IEnumerable<RagHitSummary> hits)
        => ExtractContentCardQuantityEvidenceFacts(hits)
            .Concat(ExtractContentCardGenericEvidenceFacts(hits))
            .Concat(ExtractContentCardNonScalableReasons(hits))
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ExtractContentCardQuantityEvidenceFacts(IEnumerable<RagHitSummary> hits)
    {
        var facts = new List<string>();
        foreach (var card in hits
                     .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                     .Where(static card => card.Evidence is not null))
        {
            var evidence = card.Evidence!;
            if (evidence.ScaleBasis is { Count: > 0 } basis)
            {
                var label = CollapseWhitespace(basis.Label ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(label) && basis.Count is >= 1 and <= 50)
                {
                    facts.Add(CollapseWhitespace(string.Join(' ', new[]
                    {
                        basis.Count.ToString(CultureInfo.InvariantCulture),
                        label
                    })));
                }
            }

            foreach (var fact in evidence.QuantityFacts.Take(8))
            {
                facts.Add(BuildContentCardDisplayFact(
                    fact.SourceText,
                    fact.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    fact.Unit,
                    fact.Label));
            }
        }

        return facts
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Where(IsUsefulContentCardDisplayFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ExtractContentCardGenericEvidenceFacts(IEnumerable<RagHitSummary> hits)
    {
        var facts = new List<string>();
        foreach (var evidence in hits
                     .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                     .Select(static card => card.Evidence)
                     .Where(static evidence => evidence is not null))
        {
            foreach (var fact in (evidence!.Facts ?? []).Take(12))
            {
                facts.Add(BuildContentCardDisplayFact(
                    fact.SourceText,
                    fact.Label,
                    fact.Value,
                    fact.Unit));
            }
        }

        return facts
            .Where(static fact => !string.IsNullOrWhiteSpace(fact))
            .Where(IsUsefulContentCardDisplayFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildContentCardDisplayFact(string? sourceText, params string?[] structuredParts)
    {
        var structured = CollapseWhitespace(string.Join(' ', structuredParts.Where(static value => !string.IsNullOrWhiteSpace(value))));
        var source = CollapseWhitespace(sourceText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source))
            return CleanContentCardDisplayFactValue(structured);

        if (!string.IsNullOrWhiteSpace(structured)
            && (source.Length > 90 || LooksLikeNoisyContentCardSourceText(source)))
        {
            return CleanContentCardDisplayFactValue(structured);
        }

        var value = !string.IsNullOrWhiteSpace(structured) ? structured : source;
        return CleanContentCardDisplayFactValue(value);
    }

    private static string CleanContentCardDisplayFactValue(string value)
    {
        value = Regex.Replace(value, @"(?i)\bscale_basis\b", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\s+\+\s+.*$", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(?:preparation|pr[ée]paration|portez|prechauffez|pr[ée]chauffez|epluchez|[ée]pluchez)\b.*$", string.Empty, RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(?:pr\u00e9paration|pr\u00e9chauffez|\u00e9pluchez)\b.*$", string.Empty, RegexOptions.CultureInvariant);
        value = CollapseWhitespace(value.Trim(' ', ';', ',', ':', '-'));
        return FormatReadableEvidenceExcerpt(value, maxLength: 90);
    }

    private static bool IsUsefulContentCardDisplayFact(string fact)
    {
        fact = CollapseWhitespace(fact);
        if (fact.Length < 3)
            return false;

        var normalized = NormalizeLexicalLookup(fact);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"^\d+(?:[\.,]\d+)?$", RegexOptions.CultureInvariant))
            return false;

        return !normalized.Contains("scale_basis", StringComparison.Ordinal);
    }

    private static bool LooksLikeNoisyContentCardSourceText(string source)
    {
        var normalized = NormalizeLexicalLookup(source);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.Contains("scale_basis", StringComparison.Ordinal)
            || Regex.Matches(source, @"[;,+]").Count >= 4
            || Regex.Matches(source, @"\p{L}{2,}").Count > 18;
    }

    private static string[] ExtractContentCardNonScalableReasons(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Select(static card => card.Evidence)
            .Where(static evidence => evidence is not null)
            .SelectMany(static evidence => evidence!.NonScalableReasons)
            .Select(CollapseWhitespace)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool HasContentCardEvidenceFacts(RagHitSummary hit)
        => ExtractContentCardEvidenceFacts(new[] { hit }).Length > 0;

    private static bool HasContentCardEvidence(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(static card => card.Evidence is not null);

    private static bool HasContentCardQuantityEvidence(IEnumerable<RagHitSummary> hits)
        => hits
            .SelectMany(static hit => hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(static card => card.Evidence?.ScaleBasis is not null || card.Evidence?.QuantityFacts.Count > 0);

    private static bool HasContentCardNonScalableEvidence(RagHitSummary hit)
        => hit.MatchedContentCards?
            .Any(static card => card.Evidence?.NonScalableReasons.Count > 0) == true;

    private static bool HasContentCardScalableQuantityEvidence(RagHitSummary hit)
        => hit.MatchedContentCards?
            .Any(static card => card.Evidence?.ScaleBasis is { Count: > 0 }
                                && card.Evidence.NonScalableReasons.Count == 0
                                && card.Evidence.QuantityFacts.Count >= 2) == true;

    private static bool ContainsProcedureEvidenceCue(string text)
    {
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:" + ProcedureSectionHeadingPattern + @")\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(text, @"(?:^|\s)(?:[1-9][\.)]\s+|[\u2022\u00b7]\s+\p{L})", RegexOptions.CultureInvariant);
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
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 360)));
    }

    private static void AppendExactItemControlExcerpt(StringBuilder sb, string language, string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var hit = hits.FirstOrDefault();
        if (hit is null)
            return;
        if (!ShouldIncludeRawSourceExcerptForAnswerLanguage(language, hit))
            return;

        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        sb.Append("- ");
        sb.Append(SourceBackedLabel(language, "Extrait de controle", "Control excerpt", "Extracto de control", "Excerto de controlo", "Kontrollauszug", "Estratto di controllo"));
        sb.Append(" : ");
        sb.Append(docLabel);
        sb.Append(' ');
        sb.Append(SourceBackedPagePrefix(language));
        sb.Append(hit.PageStart);
        sb.Append(" - ");
        sb.AppendLine(CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit), maxLength: 360)));
    }

    private static bool ShouldIncludeRawSourceExcerptForAnswerLanguage(string language, RagHitSummary hit)
    {
        var answerLanguage = NormalizeLanguageCode(language);
        var docLanguage = NormalizeDocumentLanguageTag(hit.DocLanguage ?? hit.ProfileLanguage);
        if (string.Equals(docLanguage, "und", StringComparison.Ordinal))
            return string.Equals(answerLanguage, "fr", StringComparison.Ordinal);

        var docPrimary = docLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.Equals(answerLanguage, docPrimary, StringComparison.OrdinalIgnoreCase);
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

    private static string SourceBackedNotVisibleLabel(string language)
        => SourceBackedLabel(
            language,
            "non visible dans les extraits retenus",
            "not visible in the retained excerpts",
            "no visible en los extractos retenidos",
            "nao visivel nos excertos retidos",
            "in den behaltenen Auszuegen nicht sichtbar",
            "non visibile negli estratti mantenuti");

    private static string SourceBackedPagePrefix(string language)
        => SourceBackedLabel(language, "p.", "p.", "p.", "p.", "S.", "p.");

    private static string SourceBackedVisibleMinutesSuffix(string language)
        => SourceBackedLabel(
            language,
            "min visibles",
            "visible minutes",
            "min visibles",
            "min visiveis",
            "sichtbare Min.",
            "min visibili");

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

    private static string[] ExtractVisibleDurationsAndQuantities(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 900);
        return Regex.Matches(
                readable,
                @"(?i)\b(?:(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}|\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}|\d+\s*(?:min|minutes?|h|heures?|hours?))\b",
                RegexOptions.CultureInvariant)
            .Select(static match => CollapseWhitespace(match.Value))
            .Where(static value => LooksLikeVisibleDurationOrQuantityFact(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeVisibleDurationOrQuantityFact(string value)
    {
        var normalized = NormalizeLooseLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b\d+\s*(?:min|minutes?|h|heures?|hours?)\b", RegexOptions.CultureInvariant))
            return true;

        var labelMatch = Regex.Match(normalized, @"(?:^|\s)(?<n>\d{1,3})\s+(?<label>[\p{L}'\u2019.\-]{2,30})\b", RegexOptions.CultureInvariant);
        return labelMatch.Success && IsPlausibleScaleCountLabel(labelMatch.Groups["label"].Value);
    }

    private static string[] ExtractItemizedQuantityFacts(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1200);
        readable = Regex.Replace(readable, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);
        readable = Regex.Replace(readable, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        readable = RemoveMalformedFrenchFairePastParticipleFragments(readable);
        var compactItemizedSegments = ExtractCompactItemizedSegments(readable);
        var matches = Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:%|[a-zA-Z]{1,8}\.?|[\p{L}]{1,12})\s*(?:de|d['’‘`])?\s*[\p{L}'’‘`\-\s]{2,55}",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanItemizedQuantityFact(match.Value))
            .Where(IsLikelyItemizedSegment)
            .Where(static value => value.Length is >= 5 and <= 90 && !LooksLikeTruncatedItemizedFact(value) && !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        matches.AddRange(Regex.Matches(
                readable,
                @"(?i)\b\d+(?:[,.]\d+)?\s+[\p{L}'’‘`\-]{3,}(?:\s+(?:de|d['’‘`]|du|des)\s*[\p{L}'’‘`\-]{3,})?",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanItemizedSegment(match.Value))
            .Where(IsLikelyItemizedSegment));

        matches.AddRange(compactItemizedSegments);
        matches = matches
            .Where(static value => !LooksLikeMaterialOnlyFact(value))
            .Where(static value => !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        if (matches.Count == 0)
        {
            matches.AddRange(SplitEvidenceSegments(readable)
                .Where(static segment => ContainsStructuredItemHeading(segment) || CountQuantityLikeSignals(segment) >= 2)
                .Select(static segment => CleanItemizedSegment(segment))
                .Where(static segment => segment.Length is >= 5 and <= 160)
                .Where(static segment => !LooksLikeTruncatedItemizedFact(segment))
                .Where(static segment => !LooksLikeTruncatedProcedureSegment(segment))
                .Where(static segment => !LooksLikeMaterialOnlyFact(segment))
                .Take(6));
        }

        return matches.ToArray();
    }

    private static bool LooksLikeMaterialOnlyFact(string value)
    {
        if (Regex.IsMatch(
                value ?? string.Empty,
                @"(?i)\b(?:mat[eé]riel|mat(?:é|.)?riel|materials?|equipment)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var normalized = NormalizeLexicalLookup(value);
        return Regex.IsMatch(normalized, @"\b(?:materiel|materials?|equipment|tools?|outils?|devices?|instruments?)\b", RegexOptions.CultureInvariant);
    }

    private static string CleanItemizedQuantityFact(string value)
    {
        var cleaned = CollapseWhitespace((value ?? string.Empty).Trim(' ', '.', ',', ';', ':'));
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\s+\b(?:pour|for|para|per)\s+(?:la|le|les|l['’]|the|a|el|los|las|o|os|as|il|lo|gli|die|der|das)?\s*[\p{L}'’\-\s]{2,}$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':');
    }

    private static IEnumerable<string> ExtractCompactItemizedSegments(string readable)
    {
        var scoped = ExtractItemizedScope(readable);
        foreach (var segment in SplitEvidenceSegments(scoped))
        {
            var cleaned = CleanItemizedSegment(segment);
            if (IsLikelyItemizedSegment(cleaned))
                yield return cleaned;
        }
    }

    private static string ExtractItemizedScope(string readable)
    {
        var match = Regex.Match(
            readable,
            @"(?is)\b(?:" + ItemizedSectionHeadingPattern + @")\b\s*[:\-]?\s*(?<body>.+?)(?:\b(?:" + ProcedureSectionHeadingPattern + @")\b|$)",
            RegexOptions.CultureInvariant);
        return match.Success ? CutBeforeSectionHeading(match.Groups["body"].Value, NonItemizedSectionHeadingPattern) : readable;
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

    private static string CleanItemizedSegment(string segment)
    {
        var cleaned = CollapseWhitespace(segment)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:" + NonItemizedSectionHeadingPattern + @")\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)\b(?:" + ProcedureSectionHeadingPattern + @")\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)^\b(?:" + ItemizedSectionHeadingPattern + @")\b\s*[:\-]?\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
    }

    private static bool IsLikelyItemizedSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length is < 3 or > 100)
            return false;
        if (LooksLikeTruncatedItemizedFact(segment))
            return false;

        var normalized = NormalizeLexicalLookup(segment);
        if (Regex.IsMatch(normalized, @"\b(?:preparation|etape|etapes|temps|time|duration|duree|procedure|method|methode|instructions?|steps?)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:min|minutes?|h|heures?|hour|hours|c|celsius)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:dans|puis|quand|when|then|after|before|until|jusqu|pendant)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+(?:materials?|equipment|tools?|outils?|devices?|instruments?)\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|[a-z]{1,8}\.?|units?|items?|pieces?)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+\p{L}{3,}(?:\s+\p{L}{2,}){0,5}$", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(normalized, @"^[\p{L}'\-]{3,}(?:\s+[\p{L}'\-]{2,}){0,5}$", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(normalized, @"\b(?:items?|elements?|requirements?|preparation|procedure|method|methode|technique|materiel|materials?|equipment|tools?|outils?)\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeTruncatedItemizedFact(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        return Regex.IsMatch(normalized, @"\b(?:de|d)\s+\p{L}{1,2}$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeItemizedListSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:" + ItemizedSectionHeadingPattern + @")\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|[a-z]{1,8}\.?|units?|items?|pieces?)\b", RegexOptions.CultureInvariant);
    }

    private static string[] ExtractProcedureSteps(string text)
    {
        var readable = FormatReadableEvidenceExcerpt(text, maxLength: 1400);
        readable = Regex.Replace(readable, @"(?<=\p{L})\.(?=[1-9]\.)", ". ", RegexOptions.CultureInvariant);
        readable = Regex.Replace(readable, @"(?<=[1-9]\.)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        var proceduralScope = ExtractPreparationScope(readable);
        var numbered = Regex.Matches(
                proceduralScope,
                @"(?i)(?:^|\s)(?:[1-9][\.)]\s*)(?<step>.{18,190}?)(?=\s*[1-9][\.)]\s*|$)",
                RegexOptions.CultureInvariant)
            .Select(static match => CleanProcedureStep(match.Groups["step"].Value))
            .Where(static value => value.Length is >= 12 and <= 220 && !LooksLikeItemizedListSegment(value) && !LooksLikeTruncatedProcedureSegment(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        if (numbered.Length > 0)
            return numbered;

        var scopedCandidates = ExtractProcedureCandidateSegments(proceduralScope).ToArray();
        var allCandidates = ExtractProcedureCandidateSegments(readable).ToArray();
        if (allCandidates.Length > scopedCandidates.Length)
            return allCandidates.Take(10).ToArray();

        return scopedCandidates.Take(10).ToArray();
    }

    private static IEnumerable<string> ExtractProcedureCandidateSegments(string text)
    {
        return SplitEvidenceSegments(text)
            .Select(static segment => CleanProcedureStep(segment))
            .Where(static segment => !LooksLikeItemizedListSegment(segment))
            .Where(static segment => !LooksLikeTruncatedProcedureSegment(segment))
            .Where(static segment => LooksLikeProcedureCandidateSegment(segment));
    }

    private static bool LooksLikeProcedureCandidateSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(normalized, @"\b(?:procedure|procedures?|instructions?|method|methode|etape|etapes|steps?|process|processus)\b", RegexOptions.CultureInvariant))
            return true;

        var wordCount = Regex.Matches(normalized, @"\b\p{L}{3,}\b", RegexOptions.CultureInvariant).Count;
        if (wordCount < 3)
            return false;

        return wordCount >= 3
            || Regex.IsMatch(normalized, @"\b(?:puis|ensuite|avant|apres|lorsque|quand|when|then|after|before|until|jusqu|pendant|pendant\s+que|while)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b\d+(?:[,.]\d+)?\s*(?:s|sec|secs|secondes?|seconds?|min|h|hours?|mm|cm|m|g|kg|mg|ml|cl|l|%|(?:\u00b0|deg|degres?)\s*c)\b", RegexOptions.CultureInvariant)
            || segment.Contains(';', StringComparison.Ordinal)
            || segment.Contains(',', StringComparison.Ordinal);
    }

    private static string CleanProcedureStep(string value)
    {
        var cleaned = CollapseWhitespace(value ?? string.Empty).Trim(' ', '.', ';', ':');
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
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
        cleaned = RemoveMalformedFrenchFairePastParticipleFragments(cleaned);
        return CollapseWhitespace(cleaned).Trim(' ', '.', ';', ':');
    }

    private static string RemoveMalformedFrenchFairePastParticipleFragments(string value)
        => Regex.Replace(
            value ?? string.Empty,
            @"(?i)\bfaites?\s+\p{L}{4,}i\b\.?\s*(?=(?:\s*(?:[-*\u2022\u00b7]|\p{Lu})|$))",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static bool LooksLikeTruncatedProcedureSegment(string segment)
    {
        var normalized = NormalizeLexicalLookup(segment).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return Regex.IsMatch(normalized, @"\b(?:de|du|des|d|a|avec|sans|et|puis|jusqu|jusque|pendant|pour)$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\bfaites?\s+\p{L}{4,}i\b", RegexOptions.CultureInvariant)
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
        var structuredEvidence = BuildRagHitContentCardEvidenceText(hit);

        if (fullText.Length > excerpt.Length + 80 && LooksLikeTruncatedEvidenceLead(excerpt))
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
        if (excerpt.Length >= 40)
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        if (fullText.Length > excerpt.Length + 80)
            return AppendContentCardEvidenceText(fullText, structuredEvidence);
        if (!string.IsNullOrWhiteSpace(structuredEvidence))
            return string.IsNullOrWhiteSpace(excerpt)
                ? structuredEvidence
                : $"{excerpt} {structuredEvidence}";
        if (!string.IsNullOrWhiteSpace(excerpt))
            return AppendContentCardEvidenceText(excerpt, structuredEvidence);
        return fullText;
    }

    private static string AppendContentCardEvidenceText(string primary, string structuredEvidence)
    {
        primary = CollapseWhitespace(primary ?? string.Empty);
        structuredEvidence = CollapseWhitespace(structuredEvidence ?? string.Empty);
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(structuredEvidence))
            return primary;

        var normalizedPrimary = NormalizeLexicalLookup(primary);
        var normalizedEvidence = NormalizeLexicalLookup(structuredEvidence);
        if (!string.IsNullOrWhiteSpace(normalizedEvidence)
            && normalizedPrimary.Contains(normalizedEvidence, StringComparison.Ordinal))
            return primary;

        return CollapseWhitespace($"{primary} {structuredEvidence}");
    }

    private static string BuildRagHitContentCardEvidenceText(RagHitSummary hit)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return string.Empty;

        var parts = new List<string>();
        foreach (var evidence in hit.MatchedContentCards.Select(static card => card.Evidence).Where(static evidence => evidence is not null))
        {
            if (evidence!.ScaleBasis is { Count: > 0 } basis)
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    basis.Count.ToString(CultureInfo.InvariantCulture),
                    basis.Label
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            foreach (var fact in evidence.QuantityFacts.Take(8))
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    fact.Unit,
                    fact.Label,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            foreach (var fact in (evidence.Facts ?? []).Take(12))
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Kind,
                    fact.Label,
                    fact.Value,
                    fact.Unit,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            foreach (var reason in evidence.NonScalableReasons.Take(6))
            {
                parts.Add(CollapseWhitespace(reason));
            }
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static part => !string.IsNullOrWhiteSpace(part))));
    }

    private static bool LooksLikeTruncatedEvidenceLead(string? value)
    {
        var text = CollapseWhitespace(value ?? string.Empty).Trim();
        text = text.TrimStart('.', '\u2026', ' ', '-', ':');
        if (text.Length < 12)
            return false;

        var normalized = NormalizeLexicalLookup(text);
        if (Regex.IsMatch(normalized, @"^(?:nt|es|e|s|de|du|des|la|le|les|a|au|aux|et|ou)\b", RegexOptions.CultureInvariant))
            return true;

        return char.IsLower(text[0])
            && !Regex.IsMatch(normalized, @"^(?:preparation|procedure|pour|for|para|per)\b", RegexOptions.CultureInvariant);
    }

    private static string BuildSourceBackedPlanningOrExtractiveAnswer(ToolResults toolResults, string query, string language, int minPlanningItems = 1)
    {
        var missingRequiredEvidence = TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(missingRequiredEvidence))
            return missingRequiredEvidence;

        var missingPairingAnchor = TryBuildMissingPairingAnchorAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(missingPairingAnchor))
            return missingPairingAnchor;

        var countdownAnswer = BuildSourceBackedCountdownPlanningAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(countdownAnswer))
            return countdownAnswer;

        if (LooksLikeSourceBackedPlanningRequest(query))
        {
            var planningAnswer = BuildSourceBackedPlanningAnswer(toolResults, language, minItems: minPlanningItems, query: query);
            if (!string.IsNullOrWhiteSpace(planningAnswer))
                return planningAnswer;
        }

        if (ShouldPreferPartialEvidenceFallbackOverOptions(toolResults, query))
            return BuildRagEvidenceFallbackAnswer(toolResults, query, language);

        if (LooksLikeSourceBackedOptionRequest(query)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && !LooksLikeTotalDurationConstraintRequest(query, TryExtractRequestedMaxMinutes(query)))
        {
            return BuildRagEvidenceFallbackAnswer(toolResults, query, language);
        }

        var optionAnswer = BuildSourceBackedOptionAnswer(toolResults, language, minItems: 1, query: query);
        return !string.IsNullOrWhiteSpace(optionAnswer)
            ? optionAnswer
            : BuildSourceBackedExtractiveAnswer(toolResults, query, language);
    }

    private static bool ShouldPreferPartialEvidenceFallbackOverOptions(ToolResults toolResults, string query)
    {
        if (!LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query))
        {
            return false;
        }

        var normalized = NormalizeLexicalLookup(query);
        if (!Regex.IsMatch(
                normalized,
                @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var candidates = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems: 1).Items.ToList();
        if (candidates.Count == 0)
            return false;

        var titleAnchorTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (titleAnchorTerms.Length == 0)
            return false;

        var anyTitleAnchor = candidates.Any(candidate =>
        {
            var title = NormalizeLexicalLookup(
                $"{candidate.Title} {candidate.Hit.SectionTitle} {candidate.Hit.HeadingPath} {string.Join(' ', candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");
            return titleAnchorTerms.Any(term => title.Contains(term, StringComparison.Ordinal));
        });
        if (anyTitleAnchor)
            return false;

        return EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Any(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)) >= 4);
    }

    private static bool ShouldUseFallbackForBroadMethodOptionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeWeeklyPlanningRequest(query))
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeOverPromotedSourceBackedOptionAnswer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:option|opcion|opcao|opzione)\s+1\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b(?:voici|here\s+are|aqui|ecco).{0,40}\b(?:options?|suggestions?|pistes)\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeUnsupportedBroadOptionComposition(string? query, string? answer)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var normalizedAnswer = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        var broadComposition = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel|selection|composition)\b",
            RegexOptions.CultureInvariant);
        if (!broadComposition || LooksLikeSourceBackedPairingRecommendationRequest(query))
            return false;

        return Regex.IsMatch(
            normalizedAnswer,
            @"\b(?:pas\s+trouve\s+de\s+passage\s+qui\s+relie|did\s+not\s+find\s+a\s+passage\s+that\s+explicitly\s+connects|no\s+he\s+encontrado\s+un\s+pasaje|nao\s+encontrei\s+uma\s+passagem|keine\s+stelle\s+gefunden|non\s+ho\s+trovato\s+un\s+passaggio)\b",
            RegexOptions.CultureInvariant);
    }

    private static string TryBuildMissingRequiredEvidenceAnswer(ToolResults toolResults, string query, string language)
    {
        var requiredTerms = ExtractStrictRequiredEvidenceTerms(query);
        if (requiredTerms.Count == 0)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var missingTerms = requiredTerms
            .Where(term => !usableHits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingTerms.Length == 0)
            return string.Empty;

        var termList = string.Join(", ", missingTerms.Select(static term => $"\"{term}\""));
        return SourceBackedLabel(
            language,
            $"Je n'ai pas trouve de passage qui mentionne explicitement {termList} dans les sources disponibles. Je ne construis donc pas une reponse a partir de passages voisins.",
            $"I did not find a passage that explicitly mentions {termList} in the available sources, so I will not build an answer from nearby passages.",
            $"No he encontrado un pasaje que mencione explicitamente {termList} en las fuentes disponibles, asi que no construire una respuesta a partir de pasajes cercanos.",
            $"Nao encontrei uma passagem que mencione explicitamente {termList} nas fontes disponiveis, por isso nao vou construir uma resposta a partir de passagens proximas.",
            $"Ich habe keine Stelle gefunden, die {termList} in den verfuegbaren Quellen ausdruecklich erwaehnt, daher baue ich keine Antwort aus benachbarten Passagen.",
            $"Non ho trovato un passaggio che menzioni esplicitamente {termList} nelle fonti disponibili, quindi non costruisco una risposta da passaggi vicini.");
    }

    private static string TryBuildMissingBroadCompositionAnchorAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeWeeklyPlanningRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
        {
            return string.Empty;
        }

        var normalized = NormalizeLexicalLookup(query);
        if (!Regex.IsMatch(
                normalized,
                @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel|selection|composition)\b",
                RegexOptions.CultureInvariant))
        {
            return string.Empty;
        }

        var anchorTerms = ExtractBroadCompositionAnchorTerms(query);
        if (anchorTerms.Length < 2)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var missingTerms = anchorTerms
            .Where(term => !usableHits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)))
            .ToArray();
        var specificMissingTerms = missingTerms
            .Where(IsSpecificBroadCompositionAnchorTerm)
            .ToArray();
        if (missingTerms.Length < Math.Min(2, anchorTerms.Length) && specificMissingTerms.Length == 0)
            return string.Empty;

        var displayedTerms = specificMissingTerms.Length > 0 ? specificMissingTerms : missingTerms;
        var termList = string.Join(", ", displayedTerms.Select(static term => $"\"{term}\""));
        return SourceBackedLabel(
            language,
            $"Je n'ai pas trouve de passage qui couvre clairement {termList} dans les sources disponibles. Je peux citer des passages voisins, mais je ne construis pas une proposition comme si cette idee etait sourcee.",
            $"I did not find passages that clearly cover {termList} in the available sources. I can cite nearby passages, but I will not build a proposal as if that idea were sourced.",
            $"No he encontrado pasajes que cubran claramente {termList} en las fuentes disponibles. Puedo citar pasajes cercanos, pero no construire una propuesta como si esa idea estuviera documentada.",
            $"Nao encontrei passagens que cubram claramente {termList} nas fontes disponiveis. Posso citar passagens proximas, mas nao construirei uma proposta como se essa ideia estivesse documentada.",
            $"Ich habe keine Stellen gefunden, die {termList} in den verfuegbaren Quellen klar abdecken. Ich kann nahe Treffer nennen, baue daraus aber keinen belegten Vorschlag.",
            $"Non ho trovato passaggi che coprano chiaramente {termList} nelle fonti disponibili. Posso citare passaggi vicini, ma non costruisco una proposta come se l'idea fosse documentata.");
    }

    private static bool IsSpecificBroadCompositionAnchorTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (normalized.Length < 6)
            return false;

        return !BroadCompositionGenericAnchorTerms.Contains(normalized);
    }

    private static readonly HashSet<string> BroadCompositionGenericAnchorTerms = new(StringComparer.Ordinal)
    {
        "idee", "idees", "ideas", "technique", "techniques", "methode", "methodes", "method",
        "methods", "approche", "approaches", "organisation", "organization", "strategie",
        "strategy", "planning", "parallele", "parallel", "selection", "composition",
        "execution", "executer", "preparer", "prepare", "preparation", "organiser",
        "organize", "compatible", "compatibles", "sourcee", "sourcees", "source",
        "sources", "documents", "document"
    };

    private static string TryBuildMissingPairingAnchorAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return string.Empty;

        var targetTerms = ExtractPairingTargetAnchorTerms(query);
        if (targetTerms.Count == 0)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var hasTargetAnchor = usableHits.Any(hit => PairingTargetAnchorMatchesHit(targetTerms, hit));
        if (hasTargetAnchor)
            return string.Empty;

        var targetList = string.Join(", ", targetTerms.Select(static term => $"\"{term}\""));
        var optionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        var optionKindList = optionKindTerms.Count > 0
            ? string.Join(", ", optionKindTerms.Select(static term => $"\"{term}\""))
            : SourceBackedLabel(language, "la demande", "the request", "la solicitud", "o pedido", "die Anfrage", "la richiesta");

        return SourceBackedLabel(
            language,
            $"Je n'ai pas trouve de passage qui relie explicitement {targetList} a {optionKindList} dans les sources disponibles. Je ne transforme donc pas des passages voisins en recommandation compatible.",
            $"I did not find a passage that explicitly connects {targetList} to {optionKindList} in the available sources, so I will not turn nearby passages into a compatible recommendation.",
            $"No he encontrado un pasaje que conecte explicitamente {targetList} con {optionKindList} en las fuentes disponibles, asi que no convertire pasajes cercanos en una recomendacion compatible.",
            $"Nao encontrei uma passagem que ligue explicitamente {targetList} a {optionKindList} nas fontes disponiveis, por isso nao transformo passagens proximas numa recomendacao compativel.",
            $"Ich habe keine Stelle gefunden, die {targetList} in den verfuegbaren Quellen ausdruecklich mit {optionKindList} verbindet, daher mache ich aus benachbarten Passagen keine kompatible Empfehlung.",
            $"Non ho trovato un passaggio che colleghi esplicitamente {targetList} a {optionKindList} nelle fonti disponibili, quindi non trasformo passaggi vicini in una raccomandazione compatibile.");
    }

    private static IReadOnlyList<string> ExtractPairingTargetAnchorTerms(string? query)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var optionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        return ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 4)
            .Where(term => !optionKindTerms.Contains(term, StringComparer.Ordinal))
            .Where(static term => !SourceBackedPairingAnchorNoiseTerms.Contains(term))
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }

    private static bool PairingTargetAnchorMatchesHit(IReadOnlyList<string> targetTerms, RagHitSummary hit)
        => targetTerms.Count > 0 && targetTerms.All(term => RequiredEvidenceTermMatchesHit(term, hit));

    private static bool LooksLikeMissingPairingAnchorAnswer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:pas\s+trouve\s+de\s+passage\s+qui\s+relie|did\s+not\s+find\s+a\s+passage\s+that\s+explicitly\s+connects|no\s+he\s+encontrado\s+un\s+pasaje\s+que\s+conecte|nao\s+encontrei\s+uma\s+passagem\s+que\s+ligue|keine\s+stelle\s+gefunden.*verbindet|non\s+ho\s+trovato\s+un\s+passaggio\s+che\s+colleghi)\b",
            RegexOptions.CultureInvariant);
    }

    private static string[] ExtractBroadCompositionAnchorTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractStrictRequiredEvidenceTerms(string? query)
    {
        var text = CollapseWhitespace(query ?? string.Empty);
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var strictCue = Regex.IsMatch(
            normalized,
            @"\b(?:uniquement|only|solo|apenas|nur|obligatoire|required|mandatory|must|explicitement|explicitly|mentionne|mentionnes|mentions?|parle|parlent|contains?|contient|a\s+partir\s+des?\s+(?:pdf|documents?|sources?)|from\s+(?:the\s+)?(?:pdf|documents?|sources?))\b",
            RegexOptions.CultureInvariant);
        if (!strictCue)
            return Array.Empty<string>();

        var terms = new List<string>();
        terms.AddRange(ExtractNamedEntityLikeQueryTerms(text));

        foreach (Match match in Regex.Matches(
            text,
            @"[\u00ab""'](?<term>[^\u00bb""']{3,80})[\u00bb""']",
            RegexOptions.CultureInvariant))
        {
            AddStrictRequiredEvidenceTerm(terms, match.Groups["term"].Value);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?i)\b(?:parle(?:nt)?|mentionne(?:nt|s)?|mentions?|contient|contains?|about|sur|sobre|ueber|uber|su)\s+(?:du|de\s+la|de\s+l['\u2019]|des|d['\u2019]|the|le|la|les|l['\u2019])?\s*(?<term>[\p{L}\p{N}'\u2019 \-]{3,80})",
            RegexOptions.CultureInvariant))
        {
            AddStrictRequiredEvidenceTerm(terms, match.Groups["term"].Value);
        }

        return terms
            .Select(NormalizeStrictRequiredEvidenceTerm)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static void AddStrictRequiredEvidenceTerm(List<string> terms, string value)
    {
        var term = NormalizeStrictRequiredEvidenceTerm(value);
        if (!string.IsNullOrWhiteSpace(term))
            terms.Add(term);
    }

    private static string NormalizeStrictRequiredEvidenceTerm(string? value)
    {
        var term = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '?', '!', '"', '\'', '\u00ab', '\u00bb');
        term = Regex.Replace(
            term,
            @"(?i)\s+\b(?:avec|with|con|com|mit|per|pour|for|only|uniquement|a\s+partir|from|dans|in|des?\s+pdf|documents?|sources?|options?|ideas?|idees?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        term = Regex.Replace(
            term,
            @"(?i)^(?:de\s+l['\u2019]|d['\u2019]|de\s+la|du|des|de|the|le|la|les|l['\u2019])\s+",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(term).Trim(' ', '.', ',', ';', ':');
    }

    private static bool RequiredEvidenceTermMatchesHit(string term, RagHitSummary hit)
    {
        var normalizedTerm = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalizedTerm))
            return true;

        var haystack = NormalizeLexicalLookup(
            $"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {GetRagHitLookupText(hit)} {string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");
        if (haystack.Contains(normalizedTerm, StringComparison.Ordinal))
            return true;

        var terms = ExtractQuerySignalTerms(normalizedTerm)
            .Where(static item => item.Length >= 4)
            .ToArray();
        if (terms.Length == 0)
            return false;

        var matchedTerms = terms.Count(item => haystack.Contains(item, StringComparison.Ordinal));
        if (matchedTerms == terms.Length)
            return true;

        return terms.Length >= 4 && matchedTerms >= terms.Length - 1 && matchedTerms >= 3;
    }

    private static string GetPlanExtractionText(RagHitSummary hit)
        => string.IsNullOrWhiteSpace(hit.FullText) ? hit.Excerpt : hit.FullText!;

    private static string ExtractPlanItemTitleV2(string? excerpt)
    {
        var text = CollapseWhitespace(excerpt ?? string.Empty);
        if (text.Length == 0)
            return string.Empty;

        text = Regex.Replace(text, @"^\d+", string.Empty, RegexOptions.CultureInvariant).Trim();
        text = Regex.Replace(text, @"(?<=[\p{Ll}])(?=(?:Pour|For|Para|Per)\b)", " ", RegexOptions.CultureInvariant);
        var patterns = new[]
        {
            @"^(?:[\p{Lu}\p{Lt}][\p{Ll}]{2,24})?(?<title>[\p{Lu}\p{Lt}][\p{Lu}\p{Lt}0-9 '&/,\-]{5,90}?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*(?:eur|euros?|chf))",
            @"(?i)^(?<title>\p{Lu}[\p{L}'\u2019 \-/]{5,80}?)\s+(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}\b",
            @"(?i)\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+[\p{L}'\u2019.\-]{2,30}\s+(?<title>\p{Lu}[\p{L}'\u2019 \-/]{5,80}?)(?:\s+(?:Items?|Elements?|Requirements?|Quantities?|Values?|Materials?|Components?|Procedure|Instructions?|Method|Preparation|\d+\s*min))",
            @"(?i)(?:^|[\s:;])(?<title>\p{Lu}[\p{Lu}0-9 '&/,\-]{5,90}?)(?:\d+\s*min|\d+(?:[,.]\d+)?\s*(?:eur|euros?|chf)|Items?|Elements?|Requirements?|Quantities?|Values?|Materials?|Components?|Procedure|Instructions?|Method|Preparation|Temps\s+total|Total\s+time)",
            @"(?i)\b(?<title>\p{Lu}[\p{L}'\u2019 \-/]{5,80})\s+(?:\d+\s*(?:items?|elements?|units?|pieces?)|Items?|Elements?|Requirements?|Quantities?|Values?|Materials?|Components?|Procedure|Instructions?|Method|Preparation)"
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
        title = Regex.Replace(
            title,
            @"(?<=[\p{Lu}\p{Lt}]{4})(?=(?:WITHOUT|SENZA|SELON|AVEC|SANS|PARA|OHNE|WITH|POUR|AUX|DES|AND|FOR|CON|SIN|MIT|PER|DU|DE|AU|A|D['\u2019])\b)",
            " ",
            RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"\b(?:WITHOUT|SENZA|SELON|AVEC|SANS|PARA|OHNE|WITH|POUR|AUX|DES|AND|FOR|CON|SIN|MIT|PER|DU|DE|AU|A|D['\u2019])(?=[\p{Lu}\p{Lt}]{4})",
            "$0 ",
            RegexOptions.CultureInvariant);
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

        if (Regex.IsMatch(normalized, @"^(?:si|lorsque|quand|when|if)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(normalized, @"^(?:en\s+)?moins\s+de\b|^under\s+\d+\b|^less\s+than\s+\d+\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(
                normalized,
            @"\b(?:liste|source|sources|page|pages|sommaire|index|contents|catalogue|copyright|isbn|edition|preparation|organisation|planning|calendrier|modele|outil|conseils?|consiste|prendre|heures?|temps|documents?|disponibles?|materiel|service|utilisez|utiliser|choisissez|installation|lors|ouvrir|programmer|extraire|volonte|limiter|limit)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"(^|\s)\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|lbs)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:p\s*)?preparation\s*\d*$",
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

    private static RagHitEvidenceProfile ClassifyRagHitEvidenceProfile(RagHitSummary hit, string? query = null)
    {
        var lookup = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var titleText = NormalizeLexicalLookup(
            $"{hit.SectionTitle} {hit.HeadingPath} {string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");

        var navigationScore = 0;
        var backendContentRole = NormalizeLexicalLookup(hit.ContentRole);
        if (backendContentRole == "navigation") navigationScore += 10;
        if (backendContentRole == "mixed_navigation_content") navigationScore += 2;
        if (hit.NavigationScore.HasValue && hit.NavigationScore.Value >= 0.82) navigationScore += 8;
        if (hit.ContentDensityScore.HasValue && hit.ContentDensityScore.Value < 0.25) navigationScore += 2;
        if (LooksLikeNavigationOnlyHit(hit)) navigationScore += 10;
        if (LooksLikePageReferenceOnlyHit(hit)) navigationScore += 6;
        if (Regex.IsMatch(lookup, @"\b(?:sommaire|contents|index|table\s+of\s+contents|catalogue|copyright|isbn|edition)\b", RegexOptions.CultureInvariant))
            navigationScore += 4;

        var fragmentScore = 0;
        if (Regex.IsMatch(CollapseWhitespace(hit.Excerpt ?? string.Empty), @"^(?:\.{2,}|\u2026)", RegexOptions.CultureInvariant))
            fragmentScore += 5;
        if (LooksLikeMidProcedureFragment(hit)) fragmentScore += 7;
        if (LooksLikeTruncatedEvidenceLead(hit.Excerpt)) fragmentScore += 4;
        if (content.Length is > 0 and < 80) fragmentScore += 3;
        if (Regex.IsMatch(content, @"\b(?:de|du|des|d|a|avec|sans|et|puis|jusqu|pour)$", RegexOptions.CultureInvariant))
            fragmentScore += 3;

        var actionabilityScore = 0;
        actionabilityScore += Math.Max(0, ComputeStructuredProcedureVisibleEvidenceCueScore(hit));
        actionabilityScore += Math.Max(0, ComputeProcedureCompletenessCueScore(hit));
        if (ExtractBestVisibleDurationMinutes(hit).HasValue) actionabilityScore += 3;
        if (CountProcedureStepMarkers(lookup) >= 2) actionabilityScore += 4;
        if (hit.ExactMatchHit) actionabilityScore += 3;
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            var cardTitle = NormalizeLexicalLookup(card.Title);
            var kind = NormalizeLexicalLookup(card.Kind);
            if (!string.IsNullOrWhiteSpace(cardTitle) && IsUsefulSourceBackedDisplayTitle(card.Title))
                actionabilityScore += 2;
            if (kind.Contains("unit", StringComparison.Ordinal) || kind.Contains("exact", StringComparison.Ordinal) || kind.Contains("lead", StringComparison.Ordinal))
                actionabilityScore += 3;
        }

        var supportScore = ComputeRagHitLexicalRelevance(query ?? string.Empty, lookup);
        if (Regex.IsMatch(lookup, @"\b(?:conseil|conseils|note|notes|warning|attention|caution|recommendation|recommandation|guidance|astuce|tips?|hinweis|avviso)\b", RegexOptions.CultureInvariant))
            supportScore += 4;
        if (!string.IsNullOrWhiteSpace(hit.ContextualSnippet)) supportScore += 2;
        if (!string.IsNullOrWhiteSpace(titleText)) supportScore += 2;

        var qualityPenalty = 0;
        if (hit.ManualReviewRecommended || hit.OcrRecommended) qualityPenalty += 5;
        if (hit.ExtractionConfidence.HasValue && hit.ExtractionConfidence.Value < 0.55) qualityPenalty += 4;
        if (Regex.IsMatch(
                NormalizeLexicalLookup($"{hit.QualityStatus} {hit.DocumentQualityStatus} {hit.PageQualityStatus} {hit.TextStatus}"),
                @"\b(?:low|poor|empty|failed|manual|review)\b",
                RegexOptions.CultureInvariant))
        {
            qualityPenalty += 3;
        }

        var role = "supporting_context";
        if (navigationScore >= 4
            && hit.PageStart <= 2
            && hit.MatchedContentCards is not { Count: > 0 })
            role = "navigation";
        else if (navigationScore >= Math.Max(7, actionabilityScore + 2))
            role = "navigation";
        else if (fragmentScore >= Math.Max(7, actionabilityScore + 2))
            role = "fragment";
        else if (qualityPenalty >= 7 && actionabilityScore < 7)
            role = "low_confidence";
        else if (actionabilityScore >= 9 && actionabilityScore >= fragmentScore + 2)
            role = "actionable_item";
        else if (supportScore >= 5)
            role = "advisory";

        if (HasBackendSelectionHints(hit))
        {
            var backendRole = NormalizeRagEvidenceRole(hit.SelectionHintRole)
                              ?? InferBackendEvidenceRoleFromScores(hit)
                              ?? role;
            return new RagHitEvidenceProfile(
                backendRole,
                hit.SelectionHintActionabilityScore ?? actionabilityScore,
                hit.SelectionHintSupportScore ?? supportScore,
                hit.SelectionHintFragmentScore ?? fragmentScore,
                hit.SelectionHintNavigationScore ?? navigationScore,
                hit.SelectionHintQualityPenalty ?? qualityPenalty);
        }

        return new RagHitEvidenceProfile(role, actionabilityScore, supportScore, fragmentScore, navigationScore, qualityPenalty);
    }

    private static string? NormalizeRagEvidenceRole(string? value)
    {
        var role = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(role))
            return null;

        return role switch
        {
            "actionable" or "actionable_item" or "actionableitem" => "actionable_item",
            "support" or "supporting" or "supporting_context" or "supportingcontext" => "supporting_context",
            "advice" or "advisory" => "advisory",
            "fragment" or "partial_fragment" or "partialfragment" => "fragment",
            "navigation" or "nav" => "navigation",
            "low_confidence" or "lowconfidence" or "low_quality" or "lowquality" => "low_confidence",
            _ => role
        };
    }

    private static bool HasBackendSelectionHints(RagHitSummary hit)
        => !string.IsNullOrWhiteSpace(hit.SelectionHintRole)
           || hit.SelectionHintActionabilityScore.HasValue
           || hit.SelectionHintSupportScore.HasValue
           || hit.SelectionHintFragmentScore.HasValue
           || hit.SelectionHintNavigationScore.HasValue
           || hit.SelectionHintQualityPenalty.HasValue;

    private static bool BackendSelectionHintsPreferUsableEvidence(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role is "actionable_item" or "supporting_context" or "advisory")
            return true;
        if (role is "navigation" or "fragment")
            return false;

        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var fragment = hit.SelectionHintFragmentScore ?? 0;
        var qualityPenalty = hit.SelectionHintQualityPenalty ?? 0;
        var positive = Math.Max(actionability, support);

        return positive >= 8
               && navigation < 8
               && fragment < 8
               && qualityPenalty < 10;
    }

    private static bool BackendSelectionHintsPreferNavigation(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role == "navigation")
            return true;
        if (role is "actionable_item" or "supporting_context" or "advisory")
            return false;

        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        return navigation >= 8
               && navigation >= actionability + 2
               && navigation >= support + 2;
    }

    private static bool BackendSelectionHintsPreferLowSignal(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role is "navigation" or "fragment")
            return true;
        if (role is "actionable_item" or "supporting_context" or "advisory")
            return false;

        var fragment = hit.SelectionHintFragmentScore ?? 0;
        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        var qualityPenalty = hit.SelectionHintQualityPenalty ?? 0;
        var positive = Math.Max(actionability, support);

        return (fragment >= 8 && fragment >= positive + 2)
               || (navigation >= 8 && navigation >= positive + 2)
               || (qualityPenalty >= 12 && positive < 5);
    }

    private static string? InferBackendEvidenceRoleFromScores(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferNavigation(hit))
            return "navigation";
        if (BackendSelectionHintsPreferLowSignal(hit))
            return (hit.SelectionHintQualityPenalty ?? 0) >= 12 ? "low_confidence" : "fragment";

        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        if (actionability >= 8 && actionability >= support)
            return "actionable_item";
        if (support >= 8)
            return "supporting_context";
        if (support >= 5)
            return "advisory";

        return null;
    }

    private static int ComputeBackendSelectionPriority(RagHitSummary hit)
    {
        if (!HasBackendSelectionHints(hit))
            return 0;

        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        var roleScore = role switch
        {
            "actionable_item" => 100,
            "advisory" => 75,
            "supporting_context" => 65,
            "low_confidence" => 5,
            "fragment" => -65,
            "navigation" => -100,
            null => 0,
            _ => 35
        };

        var evidenceBonus = hit.MatchedContentCards?
            .Take(5)
            .Sum(static card => card.Evidence is not null ? 4 : 0) ?? 0;

        return roleScore
               + ((hit.SelectionHintActionabilityScore ?? 0) * 4)
               + ((hit.SelectionHintSupportScore ?? 0) * 3)
               - ((hit.SelectionHintFragmentScore ?? 0) * 4)
               - ((hit.SelectionHintNavigationScore ?? 0) * 5)
               - ((hit.SelectionHintQualityPenalty ?? 0) * 2)
               + evidenceBonus;
    }

    private static object BuildRagSelectionHintsPayload(RagHitSummary hit, string? query)
    {
        var profile = ClassifyRagHitEvidenceProfile(hit, query);
        return new
        {
            evidenceRole = profile.Role,
            actionabilityScore = profile.ActionabilityScore,
            supportScore = profile.SupportScore,
            fragmentScore = profile.FragmentScore,
            navigationScore = profile.NavigationScore,
            qualityPenalty = profile.QualityPenalty,
            contentRole = string.IsNullOrWhiteSpace(hit.ContentRole) ? null : hit.ContentRole,
            retrievalNavigationScore = hit.NavigationScore,
            contentDensityScore = hit.ContentDensityScore,
            navigationReason = string.IsNullOrWhiteSpace(hit.NavigationReason) ? null : hit.NavigationReason
        };
    }

    private static IEnumerable<string> ExtractQuerySignalTerms(string normalizedQuery)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "comment", "dans", "faire", "facile", "idee",
            "peux", "pour", "propose", "proposes", "quoi", "semaine", "vais", "veux", "voudrais",
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

    private static string TryBuildSourcePolicyGuardAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBypassOrUnsupportedInventionRequest(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return string.Empty;
        }

        language = NormalizeLanguageCode(language);
        var prefix = BuildSourcePolicyGuardPrefix(language);

        var evidence = BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, query, language, minPlanningItems: 1);
        if (string.IsNullOrWhiteSpace(evidence))
            evidence = BuildRagEvidenceFallbackAnswer(toolResults, query, language);

        return string.IsNullOrWhiteSpace(evidence)
            ? prefix
            : ApplySourcePolicyGuardPrefix(evidence, language);
    }

    private static string BuildSourcePolicyGuardPrefix(string language)
        => SourceBackedLabel(
            language,
            "Je ne peux pas ignorer les sources ni inventer une reponse documentaire. Je reste donc strictement sur ce que les sources disponibles permettent.",
            "I cannot ignore the sources or invent a documentary answer. I will stay strictly within what the available sources support.",
            "No puedo ignorar las fuentes ni inventar una respuesta documental. Me limito estrictamente a lo que permiten las fuentes disponibles.",
            "Nao posso ignorar as fontes nem inventar uma resposta documental. Vou limitar-me estritamente ao que as fontes disponiveis sustentam.",
            "Ich kann die Quellen nicht ignorieren und keine dokumentarische Antwort erfinden. Ich bleibe daher strikt bei dem, was die verfuegbaren Quellen belegen.",
            "Non posso ignorare le fonti ne inventare una risposta documentale. Mi limito quindi strettamente a cio che le fonti disponibili supportano.");

    private static string ApplySourcePolicyGuardPrefix(string answer, string language)
    {
        var prefix = BuildSourcePolicyGuardPrefix(language);
        if (string.IsNullOrWhiteSpace(answer))
            return prefix;

        return answer.Contains(prefix, StringComparison.OrdinalIgnoreCase)
            ? answer.Trim()
            : $"{prefix}{Environment.NewLine}{Environment.NewLine}{answer}".Trim();
    }

    private static bool LooksLikeSourceBypassOrUnsupportedInventionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans\s+oublier|n\s*oublie\s+pas|ne\s+pas\s+oublier|without\s+forgetting|do\s+not\s+forget|don\s*t\s+forget|sin\s+olvidar|sem\s+esquecer|ohne\s+zu\s+vergessen|senza\s+dimenticare)\b",
            " ",
            RegexOptions.CultureInvariant);

        var asksToIgnoreSources =
            Regex.IsMatch(
                normalized,
                @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignoren|ignorar|ignora|ignorar|ignora|ignora|ignori|ignora|ignori|ignori|ignori|ignorar|ignorare|ignoriere|ignorieren)\b.{0,60}\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b.{0,60}\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,24}\b(?:utiliser|using|use|usar|utilizar|nutzen|verwenden|usare|utilizzare)?\b.{0,16}\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b",
                RegexOptions.CultureInvariant);

        var asksToInvent =
            Regex.IsMatch(
                normalized,
                @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
                RegexOptions.CultureInvariant);

        return asksToIgnoreSources || asksToInvent;
    }

    private static string BuildSourcePolicyRetrievalQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:sans|without|sin|sem|ohne|senza)\b.{0,60}\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b.{0,60}\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:sources?|documents?|pdf|fuentes?|fontes?|quellen?|fonti)\b.{0,60}\b(?:ignore|ignorer|ignorez|oublie|oublier|disregard|ignora|ignorar|ignori|ignorare|ignoriere|ignorieren)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:invente|inventer|inventez|invent|invented|make\s+up|hallucinate|inventa|inventar|inventare|erfinde|erfinden|erfunden)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:une|un|an|a|una|uma|eine|uno|un)\s+(?:version|versao|versione|variante|variant)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:de|du|des|of|da|do|das|der|die|della|del|di)?\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:une|un|an|a|una|uma|eine|uno|un)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:version|versao|versione|variante|variant)\s+(?:de|du|des|of|da|do|das|der|die|della|del|di)?\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:version|versao|versione|variante|variant)\s+(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\b",
            " ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"\b(?:amelioree|improved|mejorada|melhorada|verbesserte|migliorata)\s+(?:version|versao|versione|variante|variant)\b",
            " ",
            RegexOptions.CultureInvariant);

        return NormalizeRagQueryForRetrieval(normalized);
    }

    private static string BuildRagEvidenceFallbackAnswer(ToolResults toolResults, string query, string language)
    {
        language = NormalizeLanguageCode(language);
        var missingRequiredEvidence = TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(missingRequiredEvidence))
            return missingRequiredEvidence;

        var missingPairingAnchor = TryBuildMissingPairingAnchorAnswer(toolResults, query, language);
        if (!string.IsNullOrWhiteSpace(missingPairingAnchor))
            return missingPairingAnchor;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();

        if ((LooksLikeSourceBackedActionRequest(query) || LooksLikeComparativeDocumentaryRequest(query))
            && !LooksLikeSourceBackedOptionRequest(query)
            && !LooksLikeSourceBackedPlanningRequest(query)
            && usableHits.Count > 0)
        {
            return BuildSourceBackedExtractiveAnswer(toolResults, query, language);
        }

        var hits = FilterHitsToDominantTopLevel(usableHits, query)
            .Take(3)
            .ToList();
        if (hits.Count == 0)
            return DeterministicAgentText.AnswerNotEnoughUsableInfo(language);
        if (!RagFallbackHasAtLeastOneQueryAnchor(hits, query))
            return BuildRagNeighborFallbackAnswer(hits, language);

        var topic = NormalizeRagQueryForRetrieval(query);
        topic = string.IsNullOrWhiteSpace(topic)
            ? SourceBackedLabel(language, "ce sujet", "this topic", "este tema", "este tema", "dieses Thema", "questo tema")
            : topic;

        var labels = language switch
        {
            "en" => (
                Header: $"I found partial document evidence about {topic}. I will keep the answer limited to these source-backed leads:",
                Caveat: "This does not fully prove every part of the request; use the cited pages to confirm the details."),
            "es" => (
                Header: $"He encontrado indicios documentales parciales sobre {topic}. Limito la respuesta a estas pistas con fuente:",
                Caveat: "Esto no demuestra por completo cada parte de la solicitud; consulta las paginas citadas para confirmar los detalles."),
            "pt" => (
                Header: $"Encontrei indicios documentais parciais sobre {topic}. Limito a resposta a estas pistas com fonte:",
                Caveat: "Isto nao prova totalmente todas as partes do pedido; consulta as paginas citadas para confirmar os detalhes."),
            "de" => (
                Header: $"Ich habe teilweise Dokumentbelege zu {topic} gefunden. Ich beschraenke die Antwort auf diese belegten Hinweise:",
                Caveat: "Das belegt nicht jeden Teil der Anfrage vollstaendig; pruefe die zitierten Seiten fuer die Details."),
            "it" => (
                Header: $"Ho trovato elementi documentali parziali su {topic}. Limito la risposta a queste indicazioni con fonte:",
                Caveat: "Questo non prova completamente ogni parte della richiesta; controlla le pagine citate per confermare i dettagli."),
            _ => (
                Header: $"Oui, j'ai trouve des elements documentaires partiels sur {topic}. Je limite la reponse a ces pistes sourcees :",
                Caveat: "Cela ne prouve pas completement chaque partie de la demande ; verifie les pages citees pour confirmer les details.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        foreach (var hit in hits)
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        sb.Append(labels.Caveat);
        return sb.ToString().TrimEnd();
    }

    private static string BuildRagNeighborFallbackAnswer(IReadOnlyList<RagHitSummary> hits, string language)
    {
        var labels = NormalizeLanguageCode(language) switch
        {
            "en" => (
                Header: "I did not find a passage that clearly covers the request. I can only show these nearby source-backed leads:",
                Caveat: "These passages should not be treated as a confirmed answer to the request."),
            "es" => (
                Header: "No he encontrado un pasaje que cubra claramente la solicitud. Solo puedo mostrar estas pistas cercanas con fuente:",
                Caveat: "Estos pasajes no deben tratarse como una respuesta confirmada a la solicitud."),
            "pt" => (
                Header: "Nao encontrei uma passagem que cubra claramente o pedido. Posso apenas mostrar estas pistas proximas com fonte:",
                Caveat: "Estas passagens nao devem ser tratadas como uma resposta confirmada ao pedido."),
            "de" => (
                Header: "Ich habe keine Stelle gefunden, die die Anfrage klar abdeckt. Ich kann nur diese nahen Quellenhinweise zeigen:",
                Caveat: "Diese Passagen sollten nicht als bestaetigte Antwort auf die Anfrage behandelt werden."),
            "it" => (
                Header: "Non ho trovato un passaggio che copra chiaramente la richiesta. Posso mostrare solo queste indicazioni vicine con fonte:",
                Caveat: "Questi passaggi non devono essere trattati come una risposta confermata alla richiesta."),
            _ => (
                Header: "Je n'ai pas trouve de passage qui couvre clairement la demande. Je peux seulement citer ces passages voisins sources :",
                Caveat: "Ces passages ne doivent pas etre traites comme une reponse confirmee a la demande.")
        };

        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);
        foreach (var hit in hits.Take(3))
        {
            var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
            var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
            sb.Append("- ");
            sb.Append(docLabel);
            sb.Append(' ');
            sb.Append(SourceBackedPagePrefix(language));
            sb.Append(hit.PageStart);
            sb.Append(" : ");
            sb.AppendLine(excerpt);
        }

        sb.Append(labels.Caveat);
        return sb.ToString().TrimEnd();
    }

    private static bool RagFallbackHasAtLeastOneQueryAnchor(IReadOnlyList<RagHitSummary> hits, string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var anchorTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !SourceBackedPairingAnchorNoiseTerms.Contains(term))
            .Where(static term => !BroadCompositionGenericAnchorTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (anchorTerms.Length == 0)
            return true;

        return anchorTerms.Any(term => hits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)));
    }

    private static string TryBuildBackendGuidanceClarificationAnswer(ToolResults toolResults, string language)
    {
        foreach (var item in toolResults.Items.Where(static x => x.ToolName is "rag.search" or "rag.multi_search"))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("guidance", out var guidance)
                || guidance.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var behavior = TryGetString(guidance, "behavior") ?? TryGetString(guidance, "Behavior");
            var responseShape = TryGetString(guidance, "responseShape") ?? TryGetString(guidance, "ResponseShape");
            var asksClarification =
                string.Equals(behavior, "ask_clarification", StringComparison.OrdinalIgnoreCase)
                || string.Equals(responseShape, "clarify", StringComparison.OrdinalIgnoreCase);
            if (!asksClarification)
                continue;

            var question =
                TryGetString(guidance, "clarifyingQuestion")
                ?? TryGetString(guidance, "ClarifyingQuestion")
                ?? TryGetString(guidance, "question")
                ?? TryGetString(guidance, "Question");
            if (!string.IsNullOrWhiteSpace(question))
                return CollapseWhitespace(question);

            return SourceBackedLabel(
                language,
                "J'ai besoin d'une precision pour chercher dans les bonnes sources. Peux-tu preciser le document, le standard ou le perimetre concerne ?",
                "I need one clarification to search the right sources. Could you specify the document, standard, or scope?",
                "Necesito una precision para buscar en las fuentes correctas. Puedes especificar el documento, la norma o el alcance?",
                "Preciso de uma clarificacao para procurar nas fontes certas. Podes especificar o documento, a norma ou o ambito?",
                "Ich brauche eine Praezisierung, um in den richtigen Quellen zu suchen. Kannst du Dokument, Norm oder Umfang nennen?",
                "Ho bisogno di una precisazione per cercare nelle fonti giuste. Puoi specificare documento, standard o ambito?");
        }

        return string.Empty;
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
        string? EmbeddingBasis = null,
        string? QualityStatus = null,
        double? ExtractionConfidence = null,
        double? DocumentExtractionConfidence = null,
        double? PageExtractionConfidence = null,
        bool ManualReviewRecommended = false,
        bool DocumentManualReviewRecommended = false,
        bool PageManualReviewRecommended = false,
        bool OcrAttempted = false,
        bool OcrApplied = false,
        string? DocLanguage = null,
        string? ProfileLanguage = null,
        IReadOnlyList<RagHitContentCardSummary>? MatchedContentCards = null,
        string? SourceHash = null,
        string? DocId = null,
        string? Category = null,
        string? CategoryRef = null,
        string? CategoryPath = null,
        string? ChunkId = null,
        string? ExtractionSource = null,
        string? DocumentQualityStatus = null,
        string? PageQualityStatus = null,
        string? TextStatus = null,
        bool OcrRecommended = false,
        IReadOnlyList<string>? QualitySignals = null,
        string? SelectionHintRole = null,
        int? SelectionHintActionabilityScore = null,
        int? SelectionHintSupportScore = null,
        int? SelectionHintFragmentScore = null,
        int? SelectionHintNavigationScore = null,
        int? SelectionHintQualityPenalty = null,
        ToolMemory.SourceExtractionDiagnosticRef? ExtractionDiagnosticSummary = null,
        string? ContentRole = null,
        string? NavigationReason = null,
        double? NavigationScore = null,
        double? ContentDensityScore = null);

    private sealed record RagHitContentCardSummary(
        string Title,
        string? ContentCardId = null,
        int? PageStart = null,
        int? PageEnd = null,
        string? Kind = null,
        IReadOnlyList<string>? Signals = null,
        RagHitContentCardEvidenceSummary? Evidence = null,
        JsonElement? RawEvidence = null);

    private sealed record RagHitContentCardEvidenceSummary(
        string? SchemaVersion,
        RagHitScaleBasisSummary? ScaleBasis,
        IReadOnlyList<RagHitQuantityFactSummary> QuantityFacts,
        IReadOnlyList<string> NonScalableReasons,
        double? Confidence = null,
        string? Language = null,
        IReadOnlyList<RagHitEvidenceFactSummary>? Facts = null);

    private sealed record RagHitScaleBasisSummary(int Count, string? Label);

    private sealed record RagHitQuantityFactSummary(
        double Value,
        string Unit,
        string Label,
        string? SourceText = null);

    private sealed record RagHitEvidenceFactSummary(
        string Kind,
        string Label,
        string? Value = null,
        string? Unit = null,
        string? SourceText = null,
        int? PageStart = null,
        int? PageEnd = null,
        double? Confidence = null);

    private sealed record RagHitEvidenceProfile(
        string Role,
        int ActionabilityScore,
        int SupportScore,
        int FragmentScore,
        int NavigationScore,
        int QualityPenalty);

    private static RagHitSummary BuildRagHitSummary(JsonElement h)
    {
        var docPath = TryGetString(h, "docPath") ?? TryGetString(h, "doc_path") ?? string.Empty;
        var docName = TryGetString(h, "docName") ?? TryGetString(h, "doc_name") ?? Path.GetFileName(docPath);
        var pageStart = TryGetInt(h, "pageStart") ?? TryGetInt(h, "page_start") ?? 1;
        var pageEnd = TryGetInt(h, "pageEnd") ?? TryGetInt(h, "page_end") ?? pageStart;
        var excerpt = TryGetString(h, "excerpt") ?? TryGetString(h, "text") ?? string.Empty;
        var fullText = TryGetString(h, "fullText") ?? TryGetString(h, "full_text");
        var contextualSnippet = TryGetString(h, "contextualSnippet") ?? TryGetString(h, "contextual_snippet") ?? TryGetString(h, "ContextualSnippet");
        var sectionTitle = TryGetString(h, "sectionTitle") ?? TryGetString(h, "section_title") ?? TryGetNestedString(h, "context", "sectionTitle");
        var headingPath = TryGetString(h, "headingPath") ?? TryGetString(h, "heading_path") ?? TryGetNestedString(h, "context", "headingPath");
        var retriever = TryGetString(h, "retriever");
        var embeddingBasis = TryGetString(h, "embeddingBasis") ?? TryGetString(h, "embedding_basis");
        var score = TryGetDouble(h, "score") ?? 0.0;
        var exactMatchHit = TryGetBool(h, "exactMatchHit") ?? TryGetBool(h, "exact_match_hit") ?? false;
        var docLanguage = TryGetDocumentLanguage(h);
        var profileLanguage = TryGetString(h, "profileLanguage") ?? TryGetString(h, "profile_language") ?? TryGetString(h, "ProfileLanguage");
        var sourceHash = TryGetString(h, "sourceHash") ?? TryGetString(h, "source_hash") ?? TryGetString(h, "SourceHash");
        var docId = TryGetString(h, "docId") ?? TryGetString(h, "doc_id") ?? TryGetString(h, "DocId");
        var category = TryGetString(h, "category") ?? TryGetString(h, "Category");
        var categoryRef = TryGetString(h, "categoryRef") ?? TryGetString(h, "category_ref") ?? TryGetString(h, "CategoryRef");
        var categoryPath = TryGetString(h, "categoryPath") ?? TryGetString(h, "category_path") ?? TryGetString(h, "CategoryPath");
        var chunkId = TryGetString(h, "chunkId") ?? TryGetString(h, "chunk_id") ?? TryGetString(h, "ChunkId");
        var quality = ExtractRagHitExtractionQualitySignals(h);
        var qualityElement = TryGetObject(h, "extractionQuality") ?? TryGetObject(h, "extraction_quality") ?? TryGetObject(h, "ExtractionQuality");
        var diagnosticSummary = TryBuildSourceExtractionDiagnosticRef(h, qualityElement);
        var matchedContentCards = ExtractRagHitMatchedContentCards(h);
        var selectionHints = TryGetObject(h, "selectionHints") ?? TryGetObject(h, "selection_hints") ?? TryGetObject(h, "SelectionHints");
        var selectionHintRole = selectionHints.HasValue
            ? TryGetString(selectionHints.Value, "evidenceRole") ?? TryGetString(selectionHints.Value, "evidence_role") ?? TryGetString(selectionHints.Value, "EvidenceRole")
            : null;
        var selectionHintActionabilityScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "actionabilityScore") ?? TryGetInt(selectionHints.Value, "actionability_score") ?? TryGetInt(selectionHints.Value, "ActionabilityScore")
            : null;
        var selectionHintSupportScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "supportScore") ?? TryGetInt(selectionHints.Value, "support_score") ?? TryGetInt(selectionHints.Value, "SupportScore")
            : null;
        var selectionHintFragmentScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "fragmentScore") ?? TryGetInt(selectionHints.Value, "fragment_score") ?? TryGetInt(selectionHints.Value, "FragmentScore")
            : null;
        var selectionHintNavigationScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "navigationScore") ?? TryGetInt(selectionHints.Value, "navigation_score") ?? TryGetInt(selectionHints.Value, "NavigationScore")
            : null;
        var selectionHintQualityPenalty = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "qualityPenalty") ?? TryGetInt(selectionHints.Value, "quality_penalty") ?? TryGetInt(selectionHints.Value, "QualityPenalty")
            : null;
        var contentRole = TryGetString(h, "contentRole")
                          ?? TryGetString(h, "ContentRole")
                          ?? TryGetNestedString(h, "context", "contentRole")
                          ?? TryGetNestedString(h, "Context", "ContentRole");
        var navigationReason = TryGetString(h, "navigationReason")
                               ?? TryGetString(h, "NavigationReason")
                               ?? TryGetNestedString(h, "context", "navigationReason")
                               ?? TryGetNestedString(h, "Context", "NavigationReason");
        var navigationScore = TryGetDouble(h, "navigationScore")
                              ?? TryGetDouble(h, "NavigationScore")
                              ?? TryGetNestedDouble(h, "context", "navigationScore")
                              ?? TryGetNestedDouble(h, "Context", "NavigationScore");
        var contentDensityScore = TryGetDouble(h, "contentDensityScore")
                                  ?? TryGetDouble(h, "ContentDensityScore")
                                  ?? TryGetNestedDouble(h, "context", "contentDensityScore")
                                  ?? TryGetNestedDouble(h, "Context", "ContentDensityScore");

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
            embeddingBasis,
            quality.QualityStatus,
            quality.ExtractionConfidence,
            quality.DocumentExtractionConfidence,
            quality.PageExtractionConfidence,
            quality.ManualReviewRecommended,
            quality.DocumentManualReviewRecommended,
            quality.PageManualReviewRecommended,
            quality.OcrAttempted,
            quality.OcrApplied,
            docLanguage,
            profileLanguage,
            matchedContentCards,
            sourceHash,
            docId,
            category,
            categoryRef,
            categoryPath,
            chunkId,
            quality.ExtractionSource,
            quality.DocumentQualityStatus,
            quality.PageQualityStatus,
            quality.TextStatus,
            quality.OcrRecommended,
            quality.Signals,
            selectionHintRole,
            selectionHintActionabilityScore,
            selectionHintSupportScore,
            selectionHintFragmentScore,
            selectionHintNavigationScore,
            selectionHintQualityPenalty,
            diagnosticSummary,
            contentRole,
            navigationReason,
            navigationScore,
            contentDensityScore);
    }

    private static IReadOnlyList<RagHitContentCardSummary>? ExtractRagHitMatchedContentCards(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object
            || (!h.TryGetProperty("matchedContentCards", out var cards)
                && !h.TryGetProperty("matched_content_cards", out cards)
                && !h.TryGetProperty("MatchedContentCards", out cards)
                && !h.TryGetProperty("contentCards", out cards)
                && !h.TryGetProperty("content_cards", out cards)
                && !h.TryGetProperty("ContentCards", out cards))
            || cards.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<RagHitContentCardSummary>();
        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = TryGetString(card, "title") ?? TryGetString(card, "Title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var signals = ExtractCompactSignals(card, "signals")
                .Concat(ExtractCompactSignals(card, "Signals"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            list.Add(new RagHitContentCardSummary(
                title.Trim(),
                TryGetString(card, "contentCardId") ?? TryGetString(card, "content_card_id") ?? TryGetString(card, "ContentCardId"),
                TryGetInt(card, "pageStart") ?? TryGetInt(card, "page_start") ?? TryGetInt(card, "PageStart"),
                TryGetInt(card, "pageEnd") ?? TryGetInt(card, "page_end") ?? TryGetInt(card, "PageEnd"),
                TryGetString(card, "kind") ?? TryGetString(card, "Kind"),
                signals.Length == 0 ? null : signals,
                TryBuildRagHitContentCardEvidence(card),
                TryGetRawContentCardEvidence(card)));
        }

        return list.Count == 0 ? null : list;
    }

    private static JsonElement? TryGetRawContentCardEvidence(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object
            || (!card.TryGetProperty("evidence", out var evidence)
                && !card.TryGetProperty("Evidence", out evidence)
                && !card.TryGetProperty("structuredEvidence", out evidence)
                && !card.TryGetProperty("structured_evidence", out evidence))
            || evidence.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return evidence.Clone();
    }

    private static RagHitContentCardEvidenceSummary? TryBuildRagHitContentCardEvidence(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object
            || (!card.TryGetProperty("evidence", out var evidence)
                && !card.TryGetProperty("Evidence", out evidence)
                && !card.TryGetProperty("structuredEvidence", out evidence)
                && !card.TryGetProperty("structured_evidence", out evidence))
            || evidence.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        RagHitScaleBasisSummary? scaleBasis = null;
        var scaleBasisElement = TryGetObject(evidence, "scaleBasis")
                                ?? TryGetObject(evidence, "scale_basis")
                                ?? TryGetObject(evidence, "ScaleBasis");
        if (scaleBasisElement is { ValueKind: JsonValueKind.Object } sb)
        {
            var count = TryGetInt(sb, "count") ?? TryGetInt(sb, "Count") ?? 0;
            if (count is > 0 and <= 200)
                scaleBasis = new RagHitScaleBasisSummary(count, TryGetString(sb, "label") ?? TryGetString(sb, "Label"));
        }

        var facts = new List<RagHitQuantityFactSummary>();
        var factsElement = TryGetArray(evidence, "quantityFacts")
                           ?? TryGetArray(evidence, "quantity_facts")
                           ?? TryGetArray(evidence, "QuantityFacts");
        if (factsElement is { ValueKind: JsonValueKind.Array } qf)
        {
            foreach (var fact in qf.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
                var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
                if (value is not > 0 || string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(label))
                    continue;

                facts.Add(new RagHitQuantityFactSummary(
                    value.Value,
                    unit.Trim(),
                    CollapseWhitespace(label),
                    TryGetString(fact, "sourceText") ?? TryGetString(fact, "source_text") ?? TryGetString(fact, "SourceText")));
            }
        }

        var genericFacts = new List<RagHitEvidenceFactSummary>();
        var genericFactsElement = TryGetArray(evidence, "facts")
                                  ?? TryGetArray(evidence, "Facts");
        if (genericFactsElement is { ValueKind: JsonValueKind.Array } gf)
        {
            foreach (var fact in gf.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label") ?? TryGetString(fact, "name") ?? TryGetString(fact, "Name");
                var value = TryGetString(fact, "value") ?? TryGetString(fact, "Value") ?? TryGetString(fact, "amount") ?? TryGetString(fact, "Amount");
                var sourceText = TryGetString(fact, "sourceText")
                                 ?? TryGetString(fact, "source_text")
                                 ?? TryGetString(fact, "SourceText")
                                 ?? TryGetString(fact, "quote")
                                 ?? TryGetString(fact, "Quote")
                                 ?? TryGetString(fact, "text")
                                 ?? TryGetString(fact, "Text");
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(sourceText))
                    continue;

                genericFacts.Add(new RagHitEvidenceFactSummary(
                    CollapseWhitespace(TryGetString(fact, "kind") ?? TryGetString(fact, "Kind") ?? TryGetString(fact, "type") ?? TryGetString(fact, "Type") ?? "fact"),
                    CollapseWhitespace(string.IsNullOrWhiteSpace(label) ? "fact" : label),
                    string.IsNullOrWhiteSpace(value) ? null : CollapseWhitespace(value),
                    TryGetString(fact, "unit") ?? TryGetString(fact, "Unit"),
                    string.IsNullOrWhiteSpace(sourceText) ? null : CollapseWhitespace(sourceText),
                    TryGetInt(fact, "pageStart") ?? TryGetInt(fact, "page_start") ?? TryGetInt(fact, "PageStart") ?? TryGetInt(fact, "page") ?? TryGetInt(fact, "Page"),
                    TryGetInt(fact, "pageEnd") ?? TryGetInt(fact, "page_end") ?? TryGetInt(fact, "PageEnd") ?? TryGetInt(fact, "page") ?? TryGetInt(fact, "Page"),
                    TryGetDouble(fact, "confidence") ?? TryGetDouble(fact, "Confidence")));
            }
        }

        var nonScalableReasons = ExtractCompactSignals(evidence, "nonScalableReasons")
            .Concat(ExtractCompactSignals(evidence, "non_scalable_reasons"))
            .Concat(ExtractCompactSignals(evidence, "NonScalableReasons"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schemaVersion = TryGetString(evidence, "schemaVersion")
                            ?? TryGetString(evidence, "schema_version")
                            ?? TryGetString(evidence, "SchemaVersion");
        var confidence = TryGetDouble(evidence, "confidence") ?? TryGetDouble(evidence, "Confidence");
        var language = TryGetString(evidence, "language") ?? TryGetString(evidence, "Language");
        if (scaleBasis is null
            && facts.Count == 0
            && genericFacts.Count == 0
            && nonScalableReasons.Length == 0
            && string.IsNullOrWhiteSpace(schemaVersion)
            && confidence is null
            && string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return new RagHitContentCardEvidenceSummary(
            schemaVersion,
            scaleBasis,
            facts,
            nonScalableReasons,
            confidence,
            language,
            genericFacts);
    }

    private static (string? QualityStatus, double? ExtractionConfidence, double? DocumentExtractionConfidence, double? PageExtractionConfidence, bool ManualReviewRecommended, bool DocumentManualReviewRecommended, bool PageManualReviewRecommended, bool OcrAttempted, bool OcrApplied, string? ExtractionSource, string? DocumentQualityStatus, string? PageQualityStatus, string? TextStatus, bool OcrRecommended, IReadOnlyList<string>? Signals) ExtractRagHitExtractionQualitySignals(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null, false, false, false, false, false, null, null, null, null, false, null);
        }

        var hasNestedQuality =
            (h.TryGetProperty("extractionQuality", out var quality)
                || h.TryGetProperty("extraction_quality", out quality)
                || h.TryGetProperty("ExtractionQuality", out quality))
            && quality.ValueKind == JsonValueKind.Object;
        if (!hasNestedQuality)
            quality = h;

        string? GetQualityString(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetString(quality, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetString(h, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        double? GetQualityDouble(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetDouble(quality, name);
                if (value.HasValue)
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetDouble(h, name);
                if (value.HasValue)
                    return value;
            }

            return null;
        }

        bool? GetQualityBool(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetBool(quality, name);
                if (value.HasValue)
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetBool(h, name);
                if (value.HasValue)
                    return value;
            }

            return null;
        }

        var extractionSource = GetQualityString("extractionSource", "extraction_source", "ExtractionSource");
        var documentQualityStatus = GetQualityString("documentQualityStatus", "document_quality_status", "DocumentQualityStatus");
        var pageQualityStatus = GetQualityString("pageQualityStatus", "page_quality_status", "PageQualityStatus");
        var textStatus = GetQualityString("textStatus", "text_status", "TextStatus");
        var qualityStatus =
            pageQualityStatus
            ?? documentQualityStatus;
        var pageExtractionConfidence = GetQualityDouble("pageExtractionConfidence", "page_extraction_confidence", "PageExtractionConfidence");
        var documentExtractionConfidence = GetQualityDouble("documentExtractionConfidence", "document_extraction_confidence", "DocumentExtractionConfidence");
        var confidence = pageExtractionConfidence ?? documentExtractionConfidence;
        var pageManualReview = GetQualityBool("pageManualReviewRecommended", "page_manual_review_recommended", "PageManualReviewRecommended") ?? false;
        var documentManualReview = GetQualityBool("documentManualReviewRecommended", "document_manual_review_recommended", "DocumentManualReviewRecommended") ?? false;
        var manualReview = pageManualReview || documentManualReview;
        var ocrApplied = GetQualityBool("ocrApplied", "ocr_applied", "OcrApplied") ?? false;
        var ocrAttempted = GetQualityBool("ocrAttempted", "ocr_attempted", "OcrAttempted", "OCRAttempted") ?? false;
        var ocrRecommended = GetQualityBool("ocrRecommended", "ocr_recommended", "OcrRecommended") ?? false;
        var signals = ExtractCompactSignals(quality, "signals")
            .Concat(ExtractCompactSignals(quality, "Signals"))
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "signals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "Signals") : Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return (
            qualityStatus,
            confidence,
            documentExtractionConfidence,
            pageExtractionConfidence,
            manualReview,
            documentManualReview,
            pageManualReview,
            ocrAttempted,
            ocrApplied,
            extractionSource,
            documentQualityStatus,
            pageQualityStatus,
            textStatus,
            ocrRecommended,
            signals.Count == 0 ? null : signals);
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
            foreach (var it in sourceHits.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docId = TryGetString(it, "docId") ?? TryGetString(it, "doc_id") ?? TryGetString(it, "DocId");
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
                if (!string.IsNullOrWhiteSpace(contextualSnippet))
                {
                    var contextualEvidence = StripContextualMetadataForEvidence(contextualSnippet);
                    if (!string.IsNullOrWhiteSpace(contextualEvidence)
                        && !fullText.Contains(contextualEvidence, StringComparison.OrdinalIgnoreCase))
                    {
                        fullText = TruncateForPrompt(
                            CollapseWhitespace($"{fullText} {contextualEvidence}"),
                            RagWriterMaxFullTextChars);
                    }
                }

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
                var category = TryGetString(it, "category") ?? TryGetString(it, "Category");
                var categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath");
                var categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef");
                var docLanguage = TryGetDocumentLanguage(it);
                var profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage");
                var provenanceInfo = DeserializePromptObject(it, "provenanceInfo") ?? DeserializePromptObject(it, "ProvenanceInfo");
                var context = DeserializePromptObject(it, "context") ?? DeserializePromptObject(it, "Context");
                var rerankScore = TryGetDouble(it, "rerankScore") ?? TryGetDouble(it, "RerankScore");
                var sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash");
                var embeddingBasis = TryGetString(it, "embeddingBasis") ?? TryGetString(it, "EmbeddingBasis");
                var chunkId = TryGetString(it, "chunkId") ?? TryGetString(it, "ChunkId") ?? TryGetString(it, "chunk_id");
                var prevChunkId = TryGetString(it, "prevChunkId") ?? TryGetString(it, "PrevChunkId") ?? TryGetNestedString(it, "context", "prevChunkId") ?? TryGetNestedString(it, "Context", "PrevChunkId");
                var nextChunkId = TryGetString(it, "nextChunkId") ?? TryGetString(it, "NextChunkId") ?? TryGetNestedString(it, "context", "nextChunkId") ?? TryGetNestedString(it, "Context", "NextChunkId");
                var sameSectionChunkId = TryGetString(it, "sameSectionChunkId") ?? TryGetString(it, "SameSectionChunkId") ?? TryGetNestedString(it, "context", "sameSectionChunkId") ?? TryGetNestedString(it, "Context", "SameSectionChunkId");
                var chunkType = TryGetString(it, "chunkType")
                    ?? TryGetString(it, "ChunkType")
                    ?? TryGetNestedString(it, "context", "chunkType")
                    ?? TryGetNestedString(it, "Context", "ChunkType");
                var hasTable = TryGetBool(it, "hasTable") ?? TryGetBool(it, "HasTable");
                var hasWarning = TryGetBool(it, "hasWarning") ?? TryGetBool(it, "HasWarning");
                var hypQuestionsMatched = TryGetBool(it, "hypQuestionsMatched") ?? TryGetBool(it, "HypQuestionsMatched");
                var extractionQuality = CompactExtractionQualityForPrompt(it);
                var matchedContentCards = CompactMatchedContentCardsForPrompt(it);
                var contentSignals = CompactRetrievalContentSignalsForPrompt(it);

                list.Add(new
                {
                    docId,
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
                    category,
                    categoryPath,
                    categoryRef,
                    docLanguage,
                    profileLanguage,
                    exactMatchHit,
                    provenanceInfo,
                    context,
                    rerankScore,
                    sourceHash,
                    embeddingBasis,
                    chunkId,
                    prevChunkId,
                    nextChunkId,
                    sameSectionChunkId,
                    chunkType,
                    hasTable,
                    hasWarning,
                    hypQuestionsMatched,
                    extractionQuality,
                    matchedContentCards,
                    contentSignals,
                    selectionHints = BuildRagSelectionHintsPayload(BuildRagHitSummary(it), query: null),
                    contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
                });
            }

            var guidance = DeserializePromptObject(raw, "guidance");
            var metrics = DeserializePromptObject(raw, "metrics");
            var meta = metrics is null ? null : new { metrics };
            var payload = new { hits = list, guidance, meta };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private static object? CompactMatchedContentCardsForPrompt(JsonElement item, int maxCards = 12, bool includeEvidence = true)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("matchedContentCards", out var cards)
                && !item.TryGetProperty("matched_content_cards", out cards)
                && !item.TryGetProperty("MatchedContentCards", out cards)
                && !item.TryGetProperty("contentCards", out cards)
                && !item.TryGetProperty("content_cards", out cards)
                && !item.TryGetProperty("ContentCards", out cards))
            || cards.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var compact = new List<object>();
        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = TryGetString(card, "title") ?? TryGetString(card, "Title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var signals = ExtractCompactSignals(card, "signals")
                .Concat(ExtractCompactSignals(card, "Signals"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            compact.Add(new
            {
                title = title.Trim(),
                contentCardId = TryGetString(card, "contentCardId") ?? TryGetString(card, "content_card_id") ?? TryGetString(card, "ContentCardId"),
                pageStart = TryGetInt(card, "pageStart") ?? TryGetInt(card, "page_start") ?? TryGetInt(card, "PageStart"),
                pageEnd = TryGetInt(card, "pageEnd") ?? TryGetInt(card, "page_end") ?? TryGetInt(card, "PageEnd"),
                kind = TryGetString(card, "kind") ?? TryGetString(card, "Kind"),
                signals = signals.Length == 0 ? null : signals,
                evidence = includeEvidence ? CompactContentCardEvidenceForPrompt(card) : null
            });

            if (compact.Count >= Math.Clamp(maxCards, 1, 12))
                break;
        }

        return compact.Count == 0 ? null : compact;
    }

    private static object? CompactRetrievalContentSignalsForPrompt(JsonElement item)
    {
        var contentRole = TryGetString(item, "contentRole")
                          ?? TryGetString(item, "ContentRole")
                          ?? TryGetNestedString(item, "context", "contentRole")
                          ?? TryGetNestedString(item, "Context", "ContentRole");
        var navigationReason = TryGetString(item, "navigationReason")
                               ?? TryGetString(item, "NavigationReason")
                               ?? TryGetNestedString(item, "context", "navigationReason")
                               ?? TryGetNestedString(item, "Context", "NavigationReason");
        var navigationScore = TryGetDouble(item, "navigationScore")
                              ?? TryGetDouble(item, "NavigationScore")
                              ?? TryGetNestedDouble(item, "context", "navigationScore")
                              ?? TryGetNestedDouble(item, "Context", "NavigationScore");
        var contentDensityScore = TryGetDouble(item, "contentDensityScore")
                                  ?? TryGetDouble(item, "ContentDensityScore")
                                  ?? TryGetNestedDouble(item, "context", "contentDensityScore")
                                  ?? TryGetNestedDouble(item, "Context", "ContentDensityScore");

        if (string.IsNullOrWhiteSpace(contentRole)
            && string.IsNullOrWhiteSpace(navigationReason)
            && !navigationScore.HasValue
            && !contentDensityScore.HasValue)
        {
            return null;
        }

        return new
        {
            contentRole,
            navigationReason,
            navigationScore,
            contentDensityScore
        };
    }

    private static object? CompactContentCardEvidenceForPrompt(JsonElement card)
    {
        var evidence = TryBuildRagHitContentCardEvidence(card);
        if (evidence is null)
            return null;

        return new
        {
            schemaVersion = evidence.SchemaVersion,
            scaleBasis = evidence.ScaleBasis is null
                ? null
                : new { count = evidence.ScaleBasis.Count, label = evidence.ScaleBasis.Label },
            quantityFacts = evidence.QuantityFacts.Count == 0
                ? null
                : evidence.QuantityFacts.Take(12).Select(static fact => new
                {
                    value = fact.Value,
                    unit = fact.Unit,
                    label = fact.Label,
                    sourceText = TruncateForPrompt(fact.SourceText, 180)
                }),
            facts = evidence.Facts is not { Count: > 0 }
                ? null
                : evidence.Facts.Take(16).Select(static fact => new
                {
                    kind = fact.Kind,
                    label = fact.Label,
                    value = fact.Value,
                    unit = fact.Unit,
                    sourceText = TruncateForPrompt(fact.SourceText, 180),
                    pageStart = fact.PageStart,
                    pageEnd = fact.PageEnd,
                    confidence = fact.Confidence
                }),
            nonScalableReasons = evidence.NonScalableReasons.Count == 0 ? null : evidence.NonScalableReasons,
            confidence = evidence.Confidence,
            language = evidence.Language
        };
    }

    private static object? CompactSelectionHintsForPrompt(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("selectionHints", out var hints)
                && !item.TryGetProperty("selection_hints", out hints)
                && !item.TryGetProperty("SelectionHints", out hints))
            || hints.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["evidenceRole"] = TryGetString(hints, "evidenceRole") ?? TryGetString(hints, "evidence_role") ?? TryGetString(hints, "EvidenceRole"),
            ["actionabilityScore"] = TryGetInt(hints, "actionabilityScore") ?? TryGetInt(hints, "actionability_score") ?? TryGetInt(hints, "ActionabilityScore"),
            ["supportScore"] = TryGetInt(hints, "supportScore") ?? TryGetInt(hints, "support_score") ?? TryGetInt(hints, "SupportScore"),
            ["fragmentScore"] = TryGetInt(hints, "fragmentScore") ?? TryGetInt(hints, "fragment_score") ?? TryGetInt(hints, "FragmentScore"),
            ["navigationScore"] = TryGetInt(hints, "navigationScore") ?? TryGetInt(hints, "navigation_score") ?? TryGetInt(hints, "NavigationScore"),
            ["qualityPenalty"] = TryGetInt(hints, "qualityPenalty") ?? TryGetInt(hints, "quality_penalty") ?? TryGetInt(hints, "QualityPenalty")
        };

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static ToolMemory.SourceExtractionDiagnosticRef? TryBuildSourceExtractionDiagnosticRef(JsonElement root, JsonElement? qualityElement)
    {
        var diagnostic = TryGetExtractionDiagnosticSummaryObject(root, qualityElement);
        if (!diagnostic.HasValue)
            return null;

        return BuildSourceExtractionDiagnosticRefFromElement(diagnostic.Value);
    }

    private static ToolMemory.SourceExtractionDiagnosticRef? BuildSourceExtractionDiagnosticRefFromElement(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var summary = new ToolMemory.SourceExtractionDiagnosticRef
        {
            NativeTextStatus = NullIfWhiteSpace(TryGetString(value, "nativeTextStatus") ?? TryGetString(value, "native_text_status") ?? TryGetString(value, "NativeTextStatus")),
            NativeOcrRecommended = TryGetBool(value, "nativeOcrRecommended") ?? TryGetBool(value, "native_ocr_recommended") ?? TryGetBool(value, "NativeOcrRecommended"),
            OcrMode = NullIfWhiteSpace(TryGetString(value, "ocrMode") ?? TryGetString(value, "ocr_mode") ?? TryGetString(value, "OcrMode") ?? TryGetString(value, "OCRMode")),
            OcrLanguages = NullIfWhiteSpace(TryGetString(value, "ocrLanguages") ?? TryGetString(value, "ocr_languages") ?? TryGetString(value, "OcrLanguages") ?? TryGetString(value, "OCRLanguages")),
            OcrDurationMs = TryGetLong(value, "ocrDurationMs") ?? TryGetLong(value, "ocr_duration_ms") ?? TryGetLong(value, "OcrDurationMs") ?? TryGetLong(value, "OCRDurationMs"),
            OcrFailureReason = NullIfWhiteSpace(TryGetString(value, "ocrFailureReason") ?? TryGetString(value, "ocr_failure_reason") ?? TryGetString(value, "OcrFailureReason") ?? TryGetString(value, "OCRFailureReason")),
            OcrAppliedReason = NullIfWhiteSpace(TryGetString(value, "ocrAppliedReason") ?? TryGetString(value, "ocr_applied_reason") ?? TryGetString(value, "OcrAppliedReason") ?? TryGetString(value, "OCRAppliedReason")),
            OcrTimedOut = TryGetBool(value, "ocrTimedOut") ?? TryGetBool(value, "ocr_timed_out") ?? TryGetBool(value, "OcrTimedOut") ?? TryGetBool(value, "OCRTimedOut"),
            OcrAttemptedPageCount = TryGetInt(value, "ocrAttemptedPageCount") ?? TryGetInt(value, "ocr_attempted_page_count") ?? TryGetInt(value, "OcrAttemptedPageCount") ?? TryGetInt(value, "OCRAttemptedPageCount"),
            OcrSkippedPageCount = TryGetInt(value, "ocrSkippedPageCount") ?? TryGetInt(value, "ocr_skipped_page_count") ?? TryGetInt(value, "OcrSkippedPageCount") ?? TryGetInt(value, "OCRSkippedPageCount"),
            OcrPagesWithNovelTextCount = TryGetInt(value, "ocrPagesWithNovelTextCount") ?? TryGetInt(value, "ocr_pages_with_novel_text_count") ?? TryGetInt(value, "OcrPagesWithNovelTextCount") ?? TryGetInt(value, "OCRPagesWithNovelTextCount"),
            PageCount = TryGetInt(value, "pageCount") ?? TryGetInt(value, "page_count") ?? TryGetInt(value, "PageCount"),
            TextPageCount = TryGetInt(value, "textPageCount") ?? TryGetInt(value, "text_page_count") ?? TryGetInt(value, "TextPageCount"),
            EmptyPageCount = TryGetInt(value, "emptyPageCount") ?? TryGetInt(value, "empty_page_count") ?? TryGetInt(value, "EmptyPageCount"),
            SparsePageCount = TryGetInt(value, "sparsePageCount") ?? TryGetInt(value, "sparse_page_count") ?? TryGetInt(value, "SparsePageCount"),
            ImagePageCount = TryGetInt(value, "imagePageCount") ?? TryGetInt(value, "image_page_count") ?? TryGetInt(value, "ImagePageCount"),
            PageWarningCount = TryGetInt(value, "pageWarningCount") ?? TryGetInt(value, "page_warning_count") ?? TryGetInt(value, "PageWarningCount"),
            PageReviewRecommendedCount = TryGetInt(value, "pageReviewRecommendedCount") ?? TryGetInt(value, "page_review_recommended_count") ?? TryGetInt(value, "PageReviewRecommendedCount")
        };

        return HasSourceExtractionDiagnosticValue(summary) ? summary : null;
    }

    private static ToolMemory.SourceExtractionDiagnosticRef? CloneSourceExtractionDiagnostic(ToolMemory.SourceExtractionDiagnosticRef? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceExtractionDiagnosticRef
            {
                NativeTextStatus = summary.NativeTextStatus,
                NativeOcrRecommended = summary.NativeOcrRecommended,
                OcrMode = summary.OcrMode,
                OcrLanguages = summary.OcrLanguages,
                OcrDurationMs = summary.OcrDurationMs,
                OcrFailureReason = summary.OcrFailureReason,
                OcrAppliedReason = summary.OcrAppliedReason,
                OcrTimedOut = summary.OcrTimedOut,
                OcrAttemptedPageCount = summary.OcrAttemptedPageCount,
                OcrSkippedPageCount = summary.OcrSkippedPageCount,
                OcrPagesWithNovelTextCount = summary.OcrPagesWithNovelTextCount,
                PageCount = summary.PageCount,
                TextPageCount = summary.TextPageCount,
                EmptyPageCount = summary.EmptyPageCount,
                SparsePageCount = summary.SparsePageCount,
                ImagePageCount = summary.ImagePageCount,
                PageWarningCount = summary.PageWarningCount,
                PageReviewRecommendedCount = summary.PageReviewRecommendedCount
            };

    private static ToolMemory.SourceExtractionDiagnosticRef? BuildSourceExtractionDiagnosticRef(SAAIA.Contracts.RagItemExtractionDiagnosticSummary? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceExtractionDiagnosticRef
            {
                NativeTextStatus = NullIfWhiteSpace(summary.NativeTextStatus),
                NativeOcrRecommended = summary.NativeOcrRecommended,
                OcrMode = NullIfWhiteSpace(summary.OcrMode),
                OcrLanguages = NullIfWhiteSpace(summary.OcrLanguages),
                OcrDurationMs = summary.OcrDurationMs,
                OcrFailureReason = NullIfWhiteSpace(summary.OcrFailureReason),
                OcrAppliedReason = NullIfWhiteSpace(summary.OcrAppliedReason),
                OcrTimedOut = summary.OcrTimedOut,
                OcrAttemptedPageCount = summary.OcrAttemptedPageCount,
                OcrSkippedPageCount = summary.OcrSkippedPageCount,
                OcrPagesWithNovelTextCount = summary.OcrPagesWithNovelTextCount,
                PageCount = summary.PageCount,
                TextPageCount = summary.TextPageCount,
                EmptyPageCount = summary.EmptyPageCount,
                SparsePageCount = summary.SparsePageCount,
                ImagePageCount = summary.ImagePageCount,
                PageWarningCount = summary.PageWarningCount,
                PageReviewRecommendedCount = summary.PageReviewRecommendedCount
            };

    private static bool HasSourceExtractionDiagnosticValue(ToolMemory.SourceExtractionDiagnosticRef summary)
        => !string.IsNullOrWhiteSpace(summary.NativeTextStatus)
           || summary.NativeOcrRecommended.HasValue
           || !string.IsNullOrWhiteSpace(summary.OcrMode)
           || !string.IsNullOrWhiteSpace(summary.OcrLanguages)
           || summary.OcrDurationMs.HasValue
           || !string.IsNullOrWhiteSpace(summary.OcrFailureReason)
           || !string.IsNullOrWhiteSpace(summary.OcrAppliedReason)
           || summary.OcrTimedOut.HasValue
           || summary.OcrAttemptedPageCount.HasValue
           || summary.OcrSkippedPageCount.HasValue
           || summary.OcrPagesWithNovelTextCount.HasValue
           || summary.PageCount.HasValue
           || summary.TextPageCount.HasValue
           || summary.EmptyPageCount.HasValue
           || summary.SparsePageCount.HasValue
           || summary.ImagePageCount.HasValue
           || summary.PageWarningCount.HasValue
           || summary.PageReviewRecommendedCount.HasValue;

    private static JsonElement? TryGetExtractionDiagnosticSummaryObject(JsonElement root, JsonElement? qualityElement)
    {
        var fromQuality = qualityElement.HasValue
            ? TryGetObject(qualityElement.Value, "diagnosticSummary")
              ?? TryGetObject(qualityElement.Value, "diagnostic_summary")
              ?? TryGetObject(qualityElement.Value, "DiagnosticSummary")
              ?? TryGetObject(qualityElement.Value, "diagnostics")
              ?? TryGetObject(qualityElement.Value, "Diagnostics")
            : null;
        if (fromQuality.HasValue)
            return fromQuality;

        return TryGetObject(root, "diagnosticSummary")
               ?? TryGetObject(root, "diagnostic_summary")
               ?? TryGetObject(root, "DiagnosticSummary")
               ?? TryGetObject(root, "diagnostics")
               ?? TryGetObject(root, "Diagnostics");
    }

    private static object? CompactExtractionDiagnosticSummaryForPrompt(JsonElement quality)
    {
        var diagnostic = TryGetExtractionDiagnosticSummaryObject(default, quality);
        if (!diagnostic.HasValue)
            return null;

        var summary = BuildSourceExtractionDiagnosticRefFromElement(diagnostic.Value);
        return BuildSourceExtractionDiagnosticPayload(summary);
    }

    private static object? CompactExtractionQualityForPrompt(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("extractionQuality", out var quality)
                && !item.TryGetProperty("extraction_quality", out quality)
                && !item.TryGetProperty("ExtractionQuality", out quality))
            || quality.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["extractionSource"] = TryGetString(quality, "extractionSource") ?? TryGetString(quality, "extraction_source") ?? TryGetString(quality, "ExtractionSource"),
            ["ocrAttempted"] = TryGetBool(quality, "ocrAttempted") ?? TryGetBool(quality, "ocr_attempted") ?? TryGetBool(quality, "OcrAttempted") ?? TryGetBool(quality, "OCRAttempted"),
            ["ocrApplied"] = TryGetBool(quality, "ocrApplied") ?? TryGetBool(quality, "ocr_applied") ?? TryGetBool(quality, "OcrApplied"),
            ["documentQualityStatus"] = TryGetString(quality, "documentQualityStatus") ?? TryGetString(quality, "document_quality_status") ?? TryGetString(quality, "DocumentQualityStatus"),
            ["documentExtractionConfidence"] = TryGetDouble(quality, "documentExtractionConfidence") ?? TryGetDouble(quality, "document_extraction_confidence") ?? TryGetDouble(quality, "DocumentExtractionConfidence"),
            ["documentManualReviewRecommended"] = TryGetBool(quality, "documentManualReviewRecommended") ?? TryGetBool(quality, "document_manual_review_recommended") ?? TryGetBool(quality, "DocumentManualReviewRecommended"),
            ["pageQualityStatus"] = TryGetString(quality, "pageQualityStatus") ?? TryGetString(quality, "page_quality_status") ?? TryGetString(quality, "PageQualityStatus"),
            ["pageExtractionConfidence"] = TryGetDouble(quality, "pageExtractionConfidence") ?? TryGetDouble(quality, "page_extraction_confidence") ?? TryGetDouble(quality, "PageExtractionConfidence"),
            ["pageManualReviewRecommended"] = TryGetBool(quality, "pageManualReviewRecommended") ?? TryGetBool(quality, "page_manual_review_recommended") ?? TryGetBool(quality, "PageManualReviewRecommended"),
            ["textStatus"] = TryGetString(quality, "textStatus") ?? TryGetString(quality, "text_status") ?? TryGetString(quality, "TextStatus"),
            ["ocrRecommended"] = TryGetBool(quality, "ocrRecommended") ?? TryGetBool(quality, "ocr_recommended") ?? TryGetBool(quality, "OcrRecommended")
        };

        var diagnosticSummary = CompactExtractionDiagnosticSummaryForPrompt(quality);
        if (diagnosticSummary is not null)
            compact["diagnosticSummary"] = diagnosticSummary;

        var signals = ExtractCompactSignals(quality, "signals")
            .Concat(ExtractCompactSignals(quality, "Signals"))
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        if (signals.Length > 0)
            compact["signals"] = signals;

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static IEnumerable<string> ExtractCompactSignals(JsonElement quality, string propertyName)
    {
        if (quality.ValueKind != JsonValueKind.Object
            || !quality.TryGetProperty(propertyName, out var signals)
            || signals.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var signal in signals.EnumerateArray())
        {
            if (signal.ValueKind == JsonValueKind.String)
            {
                var value = signal.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
            }
        }
    }

    private static object? DeserializePromptObject(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object>(value.GetRawText());
        }
        catch
        {
            return null;
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

        if (CountQuantityLikeSignals(next) >= 2
            && Regex.IsMatch(next, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (CountQuantityLikeSignals(next) >= 3 && LooksLikeDenseDelimitedEvidence(next))
            return true;

        if (Regex.IsMatch(next, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(current, @"(?i)\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsStructuredItemHeading(string value)
        => Regex.IsMatch(
            NormalizeLexicalLookup(value),
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|preparacion|technique|procedure|method|methode)\b",
            RegexOptions.CultureInvariant);

    private static bool LooksLikeDelimitedTargetFact(string value)
        => Regex.Matches(value, @"[,;]").Count >= 1
           && Regex.Matches(value, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant).Count >= 3;

    private static bool LooksLikeDenseDelimitedEvidence(string value)
        => Regex.Matches(value, @"[,;]").Count >= 2
           || Regex.Matches(
                   value,
                   @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|h|min|minutes?|seconds?|secondes?|%|\p{L}{4,})\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count >= 4;

    private static int CountQuantityLikeSignals(string value)
    {
        var normalized = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;
        normalized = Regex.Replace(normalized, @"(?<=\d)(?=\p{L})", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"(?<=\p{L})(?=\d)", " ", RegexOptions.CultureInvariant);

        return Regex.Matches(
                normalized,
                @"(?i)\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|h|min|minutes?|seconds?|secondes?|°?\s*c|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|\p{L}{4,})\b",
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

    private static double? TryGetNestedDouble(JsonElement obj, string parent, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return TryGetDouble(nested, prop);
    }

    private static JsonElement? TryGetObject(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return value;
    }

    private static JsonElement? TryGetArray(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        return value;
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static long? TryGetLong(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
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
