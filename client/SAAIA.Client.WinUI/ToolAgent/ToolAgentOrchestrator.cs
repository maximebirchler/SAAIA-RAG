using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const int RouterCanonicalHintsLimit = 6;
    private const int SerializedTailContentMaxChars = 420;
    private const int RagWriterMaxHits = 4;
    private const int RagWriterComparativeMaxHits = 8;
    private const int RagWriterMaxExcerptChars = 320;
    private const int RagWriterMaxFullTextChars = 420;
    private const int RagWriterContextualEvidenceChars = 420;
    private const int RagWriterContextualRawChars = 420;
    private const int RagWriterContextualTotalChars = 900;
    private const int RagWriterBroadMaxHits = 8;
    private const int RagWriterPlanningMaxHits = 10;
    private const int RagWriterMergedBroadMaxHits = 10;
    private const int RagWriterMergedPlanningMaxHits = 10;
    private const int RagWriterBroadExcerptChars = 280;
    private const int RagWriterBroadFullTextChars = 520;
    private const int RagWriterBroadContextualChars = 520;
    private const int RagWriterMaxContentCards = 4;
    private const int RagWriterMaxCardQuantityFacts = 4;
    private const int RagWriterMaxCardFacts = 6;
    private const int RagWriterMaxCardEvidenceTextChars = 120;
    private const int SourceBackedEvidenceMaxChars = 620;
    private const int WriterPromptCharsPerTokenEstimate = 4;
    private const int WriterPromptMinimumContextTokens = 2048;
    private const int WriterPromptMinimumToolResultsChars = 2400;
    private const int WriterPromptMaximumToolResultsChars = 26000;
    private const string SourceReferenceExtensionRegex = @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv|json|ya?ml|html?|png|jpe?g|tiff?|bmp)\b";
    private readonly ApiClient _api;
    private readonly ILlmClient _llm;
    private readonly ToolMemory _mem;
    private readonly AppSettings? _settings;
    private long _lastRouterMs;
    private long _lastToolsMs;
    private long _lastWriterMs;
    private long _lastTotalMs;
    private long _lastCriticMs;
    private string? _lastCriticStatus;
    private string? _lastCriticWarning;
    private bool _lastCriticRevisedAnswer;
    private bool _lastCriticEligible;
    private string? _lastCriticSkipReason;
    private bool _lastUsedGeneralChatPrompt;
    private bool _lastUsedInventoryRendered;
    private bool _lastUsedSummaryFlow;
    private string _lastResponseFormat = "auto";
    private string _lastEffectiveMode = "auto";
    private List<string> _lastWriterToolNames = new();
    private List<(string tool, long durationMs, bool ok)> _lastToolDurations = new();
    private string _lastAnswerSource = "unknown";

    // limite "sécurité perf" (spec : max 5 RAG/calls par requête)
    private const int MaxToolCalls = 8;
    private const int MaxRagToolCalls = 5;
    private const int MaxBroadExplorationRagToolCalls = 16;
    private const int MaxInitialSourceBackedPlanningProbeQueries = 6;
    private const int InitialSourceBackedPlanningProbeTopK = 12;
    private const int InitialSourceBackedPlanningProbeMaxPerDoc = 8;
    private const int InitialSourceBackedPlanningProbeMaxPerPage = 4;
    private const int RouterLlmTimeoutMs = 8000;
    private const int SourceBackedRouterLlmTimeoutMs = 180000;
    private const int SourceBackedEvidenceExplorationTimeoutMs = 180000;
    private const int SourceBackedLlmEvidencePlannerTimeoutMs = 240000;
    private const int SourceBackedPlanningWriterTimeoutMs = 240000;
    private const int MaxSourceBackedLlmEvidencePlannerSourceLeadLines = 2;
    private const int MaxSourceBackedLlmEvidencePlannerStructureHintLines = 3;
    private const int MaxSourceBackedLlmEvidencePlannerCategoryHints = 4;
    private const int MaxSourceBackedLlmEvidencePlannerDeterministicSeeds = 6;
    private const int MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries = 4;
    private const int MaxSourceBackedLlmEvidencePlannerCoverageTraceLines = 14;
    private const int MaxSourceBackedLlmEvidencePlannerWorkingNoteLines = 8;
    private const int SourceBackedCandidateAdjudicationToolResultsChars = 12000;
    private const int MaxSourceBackedNavigationOrientationQueries = 5;
    private const int MaxSourceBackedSummaryOrientationQueries = 5;

    private enum DocumentSummaryRequestKind
    {
        About,
        SummaryReadOrLive,
        SummaryReadStoredExact,
        SummaryCheckOnly,
        SummaryStore
    }

    internal ToolAgentOrchestrator(ApiClient api, ILlmClient llm, ToolMemory mem, AppSettings? settings = null)
    {
        _api = api;
        _llm = llm;
        _mem = mem;
        _settings = settings;
    }

    private async Task<string> CompleteWithRetryAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chars = messages.Sum(m => m.content?.Length ?? 0);
        ClientLog.Info(
            "ToolAgent llm complete start: " +
            $"forceJson={forceJson}|messages={messages.Count}|chars={chars}");
        try
        {
            var result = await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm complete end: " +
                $"forceJson={forceJson}|chars={chars}|answerChars={result?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            ClientLog.Info(
                "ToolAgent llm complete cancelled: " +
                $"forceJson={forceJson}|chars={chars}|ms={sw.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Info(
                "ToolAgent llm complete retry: " +
                $"forceJson={forceJson}|chars={chars}|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 220)}");
            await Task.Delay(150, ct).ConfigureAwait(false);
            var retry = await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm complete retry end: " +
                $"forceJson={forceJson}|chars={chars}|answerChars={retry?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return retry ?? string.Empty;
        }
    }

    private async Task<string> StreamOrCompleteWithRetryAsync(
        IReadOnlyList<(string role, string content)> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chars = messages.Sum(m => m.content?.Length ?? 0);
        ClientLog.Info(
            "ToolAgent llm writer start: " +
            $"stream={onDelta is not null}|messages={messages.Count}|chars={chars}");
        if (onDelta is null)
        {
            var completed = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent llm writer end: " +
                $"stream=false|chars={chars}|answerChars={completed?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
            return completed ?? string.Empty;
        }

        var streamed = new StringBuilder();
        try
        {
            await _llm.StreamAsync(messages, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ClientLog.Info(
                "ToolAgent llm writer cancelled: " +
                $"stream=true|chars={chars}|partialChars={streamed.Length}|ms={sw.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Info(
                "ToolAgent llm writer stream fallback: " +
                $"chars={chars}|partialChars={streamed.Length}|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 220)}");
            if (streamed.Length == 0)
            {
                var completed = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
                ClientLog.Info(
                    "ToolAgent llm writer fallback end: " +
                    $"chars={chars}|answerChars={completed?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
                return completed ?? string.Empty;
            }
        }

        var finalAnswer = streamed.ToString();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

        ClientLog.Info(
            "ToolAgent llm writer end: " +
            $"stream=true|chars={chars}|answerChars={finalAnswer.Length}|ms={sw.ElapsedMilliseconds}");
        return finalAnswer;
    }

    /// <summary>
    /// Exécute le pipeline Router → Tools → Answer.
    /// </summary>
    /// <param name="onPhase">Callback UX (status bar) : "Routeur…", "Recherche documents…", "Rédaction…", etc.</param>
    public async Task<(string finalAnswer, object? sourcesPayload)> RunAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        Action<string>? onPhase = null,
        Action<string>? onDelta = null,
        Action<string>? onProgress = null)
    {
        var swTotalPipeline = Stopwatch.StartNew();
        ResetLastTurnDiagnostics();
        chatHistory ??= Array.Empty<(string role, string content)>();
        userMessage ??= string.Empty;
        BeginRagTraceTurn(userMessage, chatHistory);
        ClientLog.Info(
            "ToolAgent turn begin: " +
            $"planning={FormatPlanningTraceBool(LooksLikeAnyDocumentaryPlanningRequest(userMessage))}|" +
            $"history={chatHistory.Count}|" +
            $"chars={userMessage.Length}|" +
            $"query={TruncateForPrompt(userMessage, 180)}");

        if (TryDetectExplicitLanguageSwitch(userMessage, out var requestedLanguage))
        {
            if (LocalizedStrings.TryDetectStylePreferenceChange(userMessage, out var requestedStyle))
            {
                _mem.LastStyle = LocalizedStrings.NormalizeStyle(requestedStyle);
                UserPrefsStore.SaveStyle(_mem.LastStyle);
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(requestedLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(requestedLanguage));

            var translated = await TryTranslateLastAnswerOneShotAsync(requestedLanguage, ct, onDelta).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                onProgress?.Invoke(string.Empty);
                EmitRagTrace(
                    "turn.branch",
                    ("path", "meta.translate_last_answer"),
                    ("result", "translated"),
                    ("language", requestedLanguage));
                return FinalizeAndReturn(
                    swTotalPipeline,
                    userMessage,
                    translated.Trim(),
                    null,
                    "meta.translate_last_answer",
                    new[] { "meta.translate_last_answer" },
                    Array.Empty<string>());
            }

            var ack = LocalizedStrings.NoPreviousAnswerToTranslate(requestedLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "meta.translate_last_answer"),
                ("result", "no_previous_answer"),
                ("language", requestedLanguage));
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.translate_last_answer", Array.Empty<string>(), Array.Empty<string>());
        }

        var pendingClarification = PrepareUserMessageForPendingClarification(chatHistory, userMessage);
        var effectiveUserMessage = pendingClarification.EffectiveUserMessage;
        var interactionLanguage = ResolveInteractionLanguage(effectiveUserMessage);
        if (LocalizedStrings.TryDetectStylePreferenceChange(effectiveUserMessage, out var explicitStyle))
        {
            _mem.LastStyle = LocalizedStrings.NormalizeStyle(explicitStyle);
            UserPrefsStore.SaveStyle(_mem.LastStyle);

            var ack = LocalizedStrings.StyleChanged(_mem.LastStyle, interactionLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "meta.set_style"),
                ("style", _mem.LastStyle));
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_style", Array.Empty<string>(), Array.Empty<string>());
        }

        if (LocalizedStrings.TryDetectModePreferenceChange(effectiveUserMessage, out var explicitMode))
        {
            var normalizedMode = AppSettings.NormalizeActiveMode(explicitMode);
            _mem.LastMode = normalizedMode;
            if (_settings is not null)
                _settings.ActiveMode = normalizedMode;

            var ack = LocalizedStrings.ModeChanged(normalizedMode, interactionLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "meta.set_mode"),
                ("mode", normalizedMode));
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_mode", Array.Empty<string>(), Array.Empty<string>());
        }

        var docResolution = pendingClarification.AnalysisOverride
            ?? DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        var repairMessage = DocumentRefResolver.IsRepairMessage(effectiveUserMessage);

        var shortcut = await TryHandleDeterministicShortcutAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            interactionLanguage,
            docResolution,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (shortcut.handled)
        {
            _lastAnswerSource = $"shortcut:{shortcut.routerIntent}";
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "deterministic_shortcut"),
                ("intent", shortcut.routerIntent),
                ("answer_source", _lastAnswerSource),
                ("tools", shortcut.toolNames));
            return FinalizeAndReturn(swTotalPipeline, userMessage, shortcut.finalAnswer, shortcut.sourcesPayload, shortcut.routerIntent, shortcut.toolNames, Array.Empty<string>());
        }

        var sourcePolicyShortcut = await TryHandleSourcePolicyShortcutAsync(
            effectiveUserMessage,
            interactionLanguage,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (sourcePolicyShortcut.handled)
        {
            _lastAnswerSource = "shortcut:source_policy";
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "source_policy_shortcut"),
                ("answer_source", _lastAnswerSource),
                ("tools", sourcePolicyShortcut.toolNames));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                sourcePolicyShortcut.finalAnswer,
                sourcePolicyShortcut.sourcesPayload,
                "rag.answer",
                sourcePolicyShortcut.toolNames,
                Array.Empty<string>());
        }

        var versionTraceabilityShortcut = await TryHandleDocumentVersionTraceabilityShortcutAsync(
            effectiveUserMessage,
            interactionLanguage,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (versionTraceabilityShortcut.handled)
        {
            _lastAnswerSource = "shortcut:document_version_traceability";
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "document_version_traceability_shortcut"),
                ("answer_source", _lastAnswerSource),
                ("tools", versionTraceabilityShortcut.toolNames));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                versionTraceabilityShortcut.finalAnswer,
                versionTraceabilityShortcut.sourcesPayload,
                "rag.answer",
                versionTraceabilityShortcut.toolNames,
                Array.Empty<string>());
        }

        var exactItemShortcut = await TryHandleExactItemPreRouterShortcutAsync(
            effectiveUserMessage,
            interactionLanguage,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (exactItemShortcut.handled)
        {
            _lastAnswerSource = "shortcut:rag.exact_item";
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "exact_item_prerouter_shortcut"),
                ("answer_source", _lastAnswerSource),
                ("tools", exactItemShortcut.toolNames));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                exactItemShortcut.finalAnswer,
                exactItemShortcut.sourcesPayload,
                "rag.answer",
                exactItemShortcut.toolNames,
                Array.Empty<string>());
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseRouter(interactionLanguage));
        onProgress?.Invoke(LocalizedStrings.Get("phase.interpreting", interactionLanguage));

        if (ShouldUseCompactSourceBackedRouterPrompt(effectiveUserMessage))
        {
            var catalogContextSw = Stopwatch.StartNew();
            EmitRagTrace(
                "router.catalog_context.start",
                ("reason", "source_backed_router"),
                ("has_catalog_snapshot", _mem.CatalogSnapshotCache is not null),
                ("catalog_categories", _mem.CatalogSnapshotCache?.Categories?.Count ?? 0));
            try
            {
                await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
                EmitRagTrace(
                    "router.catalog_context.end",
                    ("catalog_categories", _mem.CatalogSnapshotCache?.Categories?.Count ?? 0),
                    ("ms", catalogContextSw.ElapsedMilliseconds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                EmitRagTrace(
                    "router.catalog_context.failed",
                    ("error", TruncateForPrompt(ex.Message, 220)),
                    ("ms", catalogContextSw.ElapsedMilliseconds));
            }
        }

        var swRouter = Stopwatch.StartNew();
        EmitRagTrace(
            "router.start",
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)),
            ("chars", effectiveUserMessage.Length));
        ClientLog.Info(
            "ToolAgent router stage start: " +
            $"planning={LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)}|chars={effectiveUserMessage.Length}");
        var plan = await RouterAsync(chatHistory, effectiveUserMessage, ct, disallowMetaSetLanguage: false).ConfigureAwait(false);
        if (string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase)
            && !TryDetectExplicitLanguageSwitch(userMessage, out _))
        {
            plan = await RouterAsync(chatHistory, effectiveUserMessage, ct, disallowMetaSetLanguage: true).ConfigureAwait(false);
        }
        swRouter.Stop();
        _lastRouterMs = swRouter.ElapsedMilliseconds;

        plan.Language = ResolveTurnLanguage(effectiveUserMessage, plan.Language, interactionLanguage);
        if (string.Equals(AppSettings.NormalizeActiveMode(_settings?.ActiveMode), "strict", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase))
            plan.Mode = "strict";
        _mem.LastLanguage = plan.Language;
        _mem.LastMode = _settings is null ? plan.Mode : AppSettings.NormalizeActiveMode(_settings.ActiveMode);
        _mem.LastRouterIntent = plan.Intent;
        _mem.LastReasoningTracePublic = plan.ReasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        _mem.LastRiskFlags = plan.RiskFlags?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        _mem.LastPlannerMemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        _mem.LastRouterConfidence = plan.RouterConfidence;
        _lastResponseFormat = string.IsNullOrWhiteSpace(plan.ResponseFormat) ? "auto" : plan.ResponseFormat.Trim().ToLowerInvariant();
        _lastEffectiveMode = string.IsNullOrWhiteSpace(plan.Mode) ? "auto" : plan.Mode.Trim().ToLowerInvariant();
        ClientLog.Info(
            "ToolAgent router stage end: " +
            $"intent={plan.Intent}|mode={plan.Mode}|lang={plan.Language}|confidence={plan.RouterConfidence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}|" +
            $"clarification={plan.NeedClarification}|tools={plan.ToolCalls.Count}|toolNames={string.Join(",", plan.ToolCalls.Select(x => x.Name))}|ms={_lastRouterMs}");
        EmitRagTrace(
            "router.end",
            ("intent", plan.Intent),
            ("mode", plan.Mode),
            ("language", plan.Language),
            ("confidence", plan.RouterConfidence),
            ("clarification", plan.NeedClarification),
            ("tools", plan.ToolCalls.Count),
            ("tool_names", plan.ToolCalls.Select(static x => x.Name).ToArray()),
            ("ms", _lastRouterMs));

        // Clear stale deterministic render when the current turn is NOT inventory.
        // Prevents old inventory data from leaking into non-inventory turns.
        if (!IsInventoryIntent(plan.Intent))
            _mem.LastDeterministicRender = null;

        if (string.Equals(plan.Intent, "meta.set_style", StringComparison.OrdinalIgnoreCase)
            && LocalizedStrings.TryDetectStylePreferenceChange(effectiveUserMessage, out var routedStyle))
        {
            _mem.LastStyle = LocalizedStrings.NormalizeStyle(routedStyle);
            UserPrefsStore.SaveStyle(_mem.LastStyle);

            var ack = LocalizedStrings.StyleChanged(_mem.LastStyle, plan.Language);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "router.meta.set_style"),
                ("style", _mem.LastStyle));
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_style", Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        if (string.Equals(plan.Intent, "meta.set_mode", StringComparison.OrdinalIgnoreCase)
            && LocalizedStrings.TryDetectModePreferenceChange(effectiveUserMessage, out var routedMode))
        {
            var normalizedMode = AppSettings.NormalizeActiveMode(routedMode);
            _mem.LastMode = normalizedMode;
            if (_settings is not null)
                _settings.ActiveMode = normalizedMode;

            var ack = LocalizedStrings.ModeChanged(normalizedMode, plan.Language);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "router.meta.set_mode"),
                ("mode", normalizedMode));
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_mode", Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        var routerTrace = _mem.LastReasoningTracePublic.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(routerTrace))
            onProgress?.Invoke(routerTrace);

        if (LooksLikeUnresolvedSourceBackedDeicticFollowup(effectiveUserMessage)
            && (_mem.LastSourcesUsed is null || _mem.LastSourcesUsed.Count == 0))
        {
            var clarification = BuildUnresolvedSourceBackedDeicticFollowupAnswer(plan.Language, effectiveUserMessage);
            await EmitDeterministicTextAsync(clarification, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification("source_backed_followup", userMessage, null, plan.Language);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "clarification.unresolved_source_followup"),
                ("intent", plan.Intent));
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

        if (plan.Origin == RouterPlanOrigin.LocalFallback)
            ApplyDocumentaryRagDefaults(plan, effectiveUserMessage);

        var standaloneTopicRag = await TryHandleStandaloneTopicRagAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            plan,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (standaloneTopicRag.handled)
            return (standaloneTopicRag.finalAnswer, standaloneTopicRag.sourcesPayload);

        if (repairMessage && string.Equals(plan.Intent, "meta.rewrite_last", StringComparison.OrdinalIgnoreCase) && !plan.NeedClarification && plan.ToolCalls.Count == 0)
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCorrectPreviousInterpretation(plan.Language));

            var repairAnswer = await GenerateRepairResponseAsync(chatHistory, effectiveUserMessage, plan.Language, ct, onDelta).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "meta.rewrite_last"),
                ("intent", plan.Intent));
            return FinalizeAndReturn(swTotalPipeline, userMessage, repairAnswer, null, plan.Intent, Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        ApplySourceBackedClarificationOverride(plan, effectiveUserMessage);
        ApplyDocumentaryRagDefaults(plan, effectiveUserMessage);

        var suppressDocumentClarificationForSourceBackedRequest =
            LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            || !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(effectiveUserMessage))
            || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
            || LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
            || LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage)
            || LooksLikeDocumentaryContentRequest(effectiveUserMessage)
            || LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);

        if ((plan.NeedClarification && plan.ClarificationQuestions.Count == 0)
            || (!suppressDocumentClarificationForSourceBackedRequest
                && plan.ToolCalls.Count == 0
                && !plan.NeedClarification
                && docResolution.NeedsClarification
                && !string.IsNullOrWhiteSpace(docResolution.ClarificationKind)))
        {
            var clarification = await GenerateClarificationResponseAsync(
                chatHistory,
                userMessage,
                plan.Language,
                docResolution.ClarificationKind ?? "generic",
                docResolution.ClarificationHint,
                ct,
                onDelta).ConfigureAwait(false);

            RememberPendingClarification(docResolution.ClarificationKind ?? "generic", userMessage, docResolution.ClarificationHint, plan.Language);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "clarification.document_resolution"),
                ("intent", plan.Intent),
                ("kind", docResolution.ClarificationKind),
                ("hint", docResolution.ClarificationHint));
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

        var documentaryProbe = await TryHandleDocumentaryProbeAsync(
            effectiveUserMessage,
            plan,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (documentaryProbe.handled)
        {
            _lastAnswerSource = $"documentary_probe:{documentaryProbe.routerIntent}";
            EmitRagTrace(
                "turn.branch",
                ("path", "documentary_probe"),
                ("intent", documentaryProbe.routerIntent),
                ("answer_source", _lastAnswerSource),
                ("tools", documentaryProbe.toolNames));
            return FinalizeAndReturn(swTotalPipeline, userMessage, documentaryProbe.finalAnswer, documentaryProbe.sourcesPayload, documentaryProbe.routerIntent, documentaryProbe.toolNames, _mem.LastReasoningTracePublic, clearPendingClarification: documentaryProbe.clearPendingClarification);
        }

        if (plan.NeedClarification && plan.ClarificationQuestions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var q in plan.ClarificationQuestions.Take(2))
                sb.AppendLine($"- {q}");
            var clarification = sb.ToString().Trim();
            await EmitDeterministicTextAsync(clarification, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification(docResolution.ClarificationKind ?? "generic", userMessage, docResolution.ClarificationHint, plan.Language);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "clarification.router_questions"),
                ("intent", plan.Intent),
                ("questions", plan.ClarificationQuestions.Take(2).ToArray()));
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, plan.Intent, Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

        var summaryHandled = await TryHandleRouterDrivenDocumentSummaryFlowAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            plan,
            docResolution,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (summaryHandled.handled)
            return (summaryHandled.finalAnswer, summaryHandled.sourcesPayload);

        onPhase?.Invoke(DeterministicAgentText.PhaseTools(plan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));

        var localItems = ExecuteLocalTools(plan, chatHistory);
        if (localItems.Count > 0)
        {
            EmitRagTrace(
                "tools.local",
                ("items", localItems.Count),
                ("tool_names", localItems.Select(static item => item.ToolName).ToArray()));
            plan.ToolCalls = plan.ToolCalls.Where(c => !string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        ApplyDocumentaryRagDefaults(plan, effectiveUserMessage);
        plan = await TryRepairStructuredRouterSearchPlanAsync(plan, effectiveUserMessage, ct).ConfigureAwait(false);

        var swTools = Stopwatch.StartNew();
        EmitRagTrace(
            "tools.stage.start",
            ("intent", plan.Intent),
            ("tools", plan.ToolCalls.Count),
            ("tool_names", plan.ToolCalls.Select(static x => x.Name).ToArray()));
        ClientLog.Info(
            "ToolAgent tools stage start: " +
            $"intent={plan.Intent}|tools={plan.ToolCalls.Count}|toolNames={string.Join(",", plan.ToolCalls.Select(x => x.Name))}");
        var toolResults = await ExecuteToolsAsync(plan, effectiveUserMessage, ct, onPhase, onProgress).ConfigureAwait(false);
        swTools.Stop();
        _lastToolsMs = swTools.ElapsedMilliseconds;
        _lastToolDurations = toolResults.Items.Select(x => (x.ToolName, x.DurationMs, string.IsNullOrWhiteSpace(x.Error))).ToList();
        _mem.LastToolNames = toolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ClientLog.Info(
            "ToolAgent tools stage end: " +
            $"intent={plan.Intent}|items={toolResults.Items.Count}|ok={toolResults.Items.Count(x => string.IsNullOrWhiteSpace(x.Error))}|" +
            $"errors={toolResults.Items.Count(x => !string.IsNullOrWhiteSpace(x.Error))}|ms={_lastToolsMs}|itemsByTool={string.Join(",", toolResults.Items.GroupBy(x => x.ToolName).Select(g => $"{g.Key}:{g.Count()}"))}");
        EmitRagTrace(
            "tools.stage.end",
            ("intent", plan.Intent),
            ("items", toolResults.Items.Count),
            ("ok", toolResults.Items.Count(static x => string.IsNullOrWhiteSpace(x.Error))),
            ("errors", toolResults.Items.Count(static x => !string.IsNullOrWhiteSpace(x.Error))),
            ("items_by_tool", toolResults.Items.GroupBy(static x => x.ToolName).Select(static g => $"{g.Key}:{g.Count()}").ToArray()),
            ("ms", _lastToolsMs));
        if (localItems.Count > 0)
            toolResults.Items.InsertRange(0, localItems);

        CaptureStructuredConversationState(plan, toolResults);

        var inventoryRendered = TryBuildInventoryRenderedItem(toolResults, plan.Language, ct);
        _lastUsedInventoryRendered = inventoryRendered is not null;
        if (inventoryRendered is not null)
        {
            toolResults.Items.Add(inventoryRendered);
            EmitRagTrace(
                "inventory.rendered",
                ("language", plan.Language),
                ("tool_items", toolResults.Items.Count));
        }

        var deterministicToolFailure = TryBuildToolFailureAnswer(plan, toolResults, plan.Language);
        if (!string.IsNullOrWhiteSpace(deterministicToolFailure))
        {
            await EmitDeterministicTextAsync(deterministicToolFailure, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "deterministic_tool_failure"),
                ("intent", plan.Intent),
                ("answer_chars", deterministicToolFailure.Length));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                deterministicToolFailure,
                null,
                plan.Intent,
                _mem.LastToolNames,
                _mem.LastReasoningTracePublic);
        }

        await TryExpandSourceBackedEvidenceRetrievalAsync(
            toolResults,
            plan,
            effectiveUserMessage,
            ct,
            onPhase,
            onProgress).ConfigureAwait(false);

        await TryExpandBackendGuidanceClarificationRetrievalAsync(
            toolResults,
            plan,
            effectiveUserMessage,
            ct,
            onPhase,
            onProgress).ConfigureAwait(false);

        var noRagEvidenceAnswer = TryBuildNoRagEvidenceAnswerForEmptySearch(toolResults, plan.Language, effectiveUserMessage);
        if (!string.IsNullOrWhiteSpace(noRagEvidenceAnswer))
        {
            await EmitDeterministicTextAsync(noRagEvidenceAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            _lastAnswerSource = "deterministic:rag.no_evidence";
            EmitRagTrace(
                "turn.branch",
                ("path", "deterministic.rag.no_evidence"),
                ("intent", plan.Intent),
                ("answer_source", _lastAnswerSource),
                ("rag_items", toolResults.Items.Count(static item => item.ToolName is "rag.search" or "rag.multi_search")));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                noRagEvidenceAnswer,
                null,
                plan.Intent,
                _mem.LastToolNames,
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
        }

        var ambiguousBareAnswer = TryBuildAmbiguousBareDocumentaryFragmentAnswer(toolResults, effectiveUserMessage, plan.Language, out var ambiguousBareSources);
        if (!string.IsNullOrWhiteSpace(ambiguousBareAnswer))
        {
            object? ambiguousSourcesPayload = null;
            if (ambiguousBareSources.Count > 0)
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(ambiguousBareSources);
                ambiguousBareAnswer = InjectInlineSources(ambiguousBareAnswer, ambiguousBareSources, plan.Language);
                ambiguousSourcesPayload = BuildSourcesPayload(plan.Intent, ambiguousBareSources);
            }

            await EmitDeterministicTextAsync(ambiguousBareAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            _lastAnswerSource = "deterministic:rag.ambiguous_fragment";
            EmitRagTrace(
                "turn.branch",
                ("path", "deterministic.rag.ambiguous_fragment"),
                ("intent", plan.Intent),
                ("answer_source", _lastAnswerSource),
                ("sources", ambiguousBareSources.Count));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                ambiguousBareAnswer,
                ambiguousSourcesPayload,
                plan.Intent,
                _mem.LastToolNames,
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
        }

        var preWriterExactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var preWriterShouldRouteThroughWriter =
            ShouldRouteSourceBackedAnswerThroughWriter(toolResults, effectiveUserMessage, plan.Language);
        var preWriterShouldRequireWriterForBroadFinal =
            ShouldRequireWriterForBroadDocumentaryFinal(toolResults, effectiveUserMessage, plan.Language);
        var preWriterShouldUseBroadSynthesis =
            ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, effectiveUserMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage)
            || preWriterShouldRouteThroughWriter
            || preWriterShouldRequireWriterForBroadFinal;
        var preWriterShouldAvoidRawSourceBackedFallback =
            ShouldAvoidRawSourceBackedFallback(effectiveUserMessage)
            || preWriterShouldRouteThroughWriter
            || preWriterShouldRequireWriterForBroadFinal;
        var shouldSuppressPreWriterSourceBackedForRouterGeneralNoTools =
            ShouldRespectLlmRouterGeneralWithoutTools(plan);
        var preWriterSourceBackedAnswer = string.Empty;
        List<ToolMemory.SourceRef>? preWriterSourceBackedSources = null;
        if (LooksLikeCategoryOverviewOrDocumentOrientationRequest(effectiveUserMessage)
            && !preWriterShouldRouteThroughWriter)
        {
            preWriterSourceBackedAnswer = BuildCategoryOverviewAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromRagHits(toolResults).Take(6).ToList();
        }
        else if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage)
            && !preWriterShouldUseBroadSynthesis
            && !preWriterShouldAvoidRawSourceBackedFallback)
        {
            preWriterSourceBackedAnswer = BuildSourceBackedCountdownPlanningAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromCountdownPlanningHits(toolResults, effectiveUserMessage);
            if (string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer))
            {
                preWriterSourceBackedAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }
        }
        else if (LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
        {
            if (!preWriterShouldUseBroadSynthesis
                && !preWriterShouldAvoidRawSourceBackedFallback
                && !ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language))
            {
                if (ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
                {
                    if (TryBuildSupportedStructuredPlanningAnswer(
                            toolResults,
                            plan.Language,
                            effectiveUserMessage,
                            out var supportedPlanningAnswer,
                            out var supportedPlanningSources,
                            out _))
                    {
                        preWriterSourceBackedAnswer = supportedPlanningAnswer;
                        preWriterSourceBackedSources = supportedPlanningSources;
                    }
                }
                else
                {
                    preWriterSourceBackedAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                    preWriterSourceBackedSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
                    if (preWriterSourceBackedSources.Count == 0)
                        preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }
            }
        }
        else if (ShouldUseSourceBackedOptionAnswer(preWriterExactItemTitle, effectiveUserMessage)
            && !preWriterShouldUseBroadSynthesis
            && !ShouldAvoidDeterministicSourceBackedOptionFallback(effectiveUserMessage))
        {
            preWriterSourceBackedAnswer = BuildSourceBackedOptionAnswer(toolResults, plan.Language, minItems: 1, query: effectiveUserMessage);
            preWriterSourceBackedSources = DeriveSourcesFromOptionHits(toolResults, effectiveUserMessage);
            if (string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer))
            {
                preWriterSourceBackedAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }
        }
        else if (!preWriterShouldUseBroadSynthesis
            && !preWriterShouldAvoidRawSourceBackedFallback
            && (LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults)))
        {
            preWriterSourceBackedAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
        }

        if (string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer)
            && !preWriterShouldUseBroadSynthesis
            && !preWriterShouldAvoidRawSourceBackedFallback
            && !ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage))
        {
            preWriterSourceBackedAnswer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
            if (preWriterSourceBackedSources.Count == 0)
                preWriterSourceBackedSources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
        }

        if (shouldSuppressPreWriterSourceBackedForRouterGeneralNoTools
            && !string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer))
        {
            EmitRagTrace(
                "pre_writer_source_backed.skipped",
                ("reason", "router_general_no_tools"),
                ("suppressed_answer_chars", preWriterSourceBackedAnswer.Length),
                ("intent", plan.Intent));
            preWriterSourceBackedAnswer = string.Empty;
            preWriterSourceBackedSources = null;
        }

        if (!string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer))
        {
            object? preWriterSourcesPayload = null;
            if (LooksLikePoorPlanningFallbackAnswer(preWriterSourceBackedAnswer, effectiveUserMessage)
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    effectiveUserMessage,
                    plan,
                    toolResults,
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage))
                {
                    preWriterSourceBackedAnswer = repairAnswer;
                    preWriterSourceBackedSources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
                }
                else
                {
                    if (preWriterShouldAvoidRawSourceBackedFallback)
                    {
                        preWriterSourceBackedAnswer = BuildSourceBackedSafeFallbackAnswer(
                            toolResults,
                            effectiveUserMessage,
                            plan.Language,
                            shouldAvoidRaw: true);
                        preWriterSourceBackedSources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
                    }
                    else
                    {
                        var readableFallback = BuildReadablePartialPlanningEvidenceAnswer(
                            SelectSourceBackedExtractiveHits(toolResults, effectiveUserMessage, maxHits: 8).ToList(),
                            effectiveUserMessage,
                            plan.Language);
                        if (!string.IsNullOrWhiteSpace(readableFallback))
                        {
                            preWriterSourceBackedAnswer = readableFallback;
                            preWriterSourceBackedSources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
                        }
                    }
                }
            }

            if (LooksLikeMissingExactItemWithoutSourceLeads(preWriterSourceBackedAnswer))
            {
                preWriterSourceBackedSources?.Clear();
                _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            }

            if (TryFinalizeSourceBackedPlanningResponse(
                    preWriterSourceBackedAnswer,
                    toolResults,
                    effectiveUserMessage,
                    plan.Language,
                    out var finalizedPlanningAnswer,
                    out var finalizedPlanningSources,
                    out var finalizedPlanningAnalysis,
                    out var finalizedPlanningResolution))
            {
                ClientLog.Info(
                    "ToolAgent planning pre-writer finalizer: " +
                    $"resolution={finalizedPlanningResolution} " +
                    $"items={finalizedPlanningAnalysis.ItemCount} " +
                    $"supported={finalizedPlanningAnalysis.SupportedItemCount} " +
                    $"unsupported={finalizedPlanningAnalysis.UnsupportedItemCount} " +
                    $"candidates={finalizedPlanningAnalysis.CandidateCount} " +
                    $"sources={finalizedPlanningSources.Count}");
                LogSourceBackedPlanningTrace(
                    "pre-writer-finalizer",
                    toolResults,
                    effectiveUserMessage,
                    plan.Language);
                EmitPlanningFinalizerDecisionTrace(
                    "pre-writer-finalizer",
                    plan.Intent,
                    finalizedPlanningResolution,
                    finalizedPlanningAnalysis,
                    finalizedPlanningSources.Count,
                    $"router+tools_{finalizedPlanningResolution}:{plan.Intent}");
                preWriterSourceBackedAnswer = finalizedPlanningAnswer;
                preWriterSourceBackedSources = finalizedPlanningSources;
                _lastAnswerSource = $"router+tools_{finalizedPlanningResolution}:{plan.Intent}";
            }

            if (ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(preWriterSourceBackedAnswer, toolResults, effectiveUserMessage, plan.Language))
            {
                preWriterSourceBackedAnswer = RemoveTrailingModelEmittedSourceList(preWriterSourceBackedAnswer).Trim();
                preWriterSourceBackedSources?.Clear();
                _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            }

            if (preWriterSourceBackedSources is { Count: > 0 })
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(preWriterSourceBackedSources);
                preWriterSourceBackedAnswer = InjectInlineSources(preWriterSourceBackedAnswer, preWriterSourceBackedSources, plan.Language);
                preWriterSourcesPayload = BuildSourcesPayload(plan.Intent, preWriterSourceBackedSources);
            }

            await EmitDeterministicTextAsync(preWriterSourceBackedAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            _lastAnswerSource = $"router+tools_pre_writer_source_backed:{plan.Intent}";
            EmitRagTrace(
                "turn.branch",
                ("path", "pre_writer_source_backed"),
                ("intent", plan.Intent),
                ("answer_source", _lastAnswerSource),
                ("answer_chars", preWriterSourceBackedAnswer.Length),
                ("sources", preWriterSourceBackedSources?.Count ?? 0),
                ("sources_payload", preWriterSourcesPayload is not null));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                preWriterSourceBackedAnswer,
                preWriterSourcesPayload,
                plan.Intent,
                _mem.LastToolNames,
                _mem.LastReasoningTracePublic);
        }

        var shouldBufferWriterOutputForSourceBackedGuard =
            toolResults.Items.Any(static x => x.ToolName is "rag.search" or "rag.multi_search")
            && (preWriterShouldUseBroadSynthesis
                || preWriterShouldAvoidRawSourceBackedFallback
                || ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
                || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage)
                || ShouldRequireWriterForBroadDocumentaryFinal(toolResults, effectiveUserMessage, plan.Language));
        EmitRagTrace(
            "writer.stage.start",
            ("intent", plan.Intent),
            ("buffer_output", shouldBufferWriterOutputForSourceBackedGuard),
            ("use_broad_synthesis", preWriterShouldUseBroadSynthesis),
            ("avoid_raw_fallback", preWriterShouldAvoidRawSourceBackedFallback),
            ("route_through_writer", preWriterShouldRouteThroughWriter),
            ("require_broad_final", preWriterShouldRequireWriterForBroadFinal));

        onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));
        var swWriter = Stopwatch.StartNew();
        string answer;
        List<ToolMemory.SourceRef>? sources;
        var usedSourceBackedWriterTimeoutFallback = false;
        try
        {
            using var sourceBackedWriterTimeoutCts = shouldBufferWriterOutputForSourceBackedGuard
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (sourceBackedWriterTimeoutCts is not null)
                sourceBackedWriterTimeoutCts.CancelAfter(SourceBackedPlanningWriterTimeoutMs);

            (answer, sources) = await AnswerAsync(
                    chatHistory,
                    effectiveUserMessage,
                    plan,
                    toolResults,
                    sourceBackedWriterTimeoutCts?.Token ?? ct,
                    shouldBufferWriterOutputForSourceBackedGuard ? null : onDelta,
                    onProgress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shouldBufferWriterOutputForSourceBackedGuard && !ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "writer.stage.timeout",
                ("intent", plan.Intent),
                ("timeout_ms", SourceBackedPlanningWriterTimeoutMs),
                ("tool_items", toolResults.Items.Count));
            (answer, sources) = BuildSourceBackedWriterTimeoutFallback(
                toolResults,
                effectiveUserMessage,
                plan.Language);
            usedSourceBackedWriterTimeoutFallback = true;
            _lastAnswerSource = $"router+tools_source_backed_writer_timeout_fallback:{plan.Intent}";
        }
        swWriter.Stop();
        _lastWriterMs = swWriter.ElapsedMilliseconds;
        EmitRagTrace(
            "writer.stage.end",
            ("intent", plan.Intent),
            ("answer_chars", answer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("answer_source", _lastAnswerSource),
            ("ms", _lastWriterMs));

        answer = (answer ?? string.Empty).Trim();
        if (toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search")
            && !_lastAnswerSource.StartsWith("backend_guidance_ask_clarification:", StringComparison.Ordinal)
            && !LooksLikeMissingExactItemWithoutSourceLeads(answer)
            && (sources is null || sources.Count == 0 || ShouldFallbackFromNoRagDataAnswer(answer)))
        {
            var repairedSources = (LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                    || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
                    || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults))
                ? DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage)
                : DeriveSourcesFromRankedRagHits(toolResults, effectiveUserMessage);
            if (repairedSources.Count == 0 && LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
                repairedSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
            if (repairedSources.Count == 0)
                repairedSources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

            if (repairedSources.Count > 0)
            {
                sources = repairedSources;
                if (ShouldFallbackFromNoRagDataAnswer(answer))
                {
                    if (preWriterShouldUseBroadSynthesis || preWriterShouldAvoidRawSourceBackedFallback)
                    {
                        var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                                chatHistory,
                                effectiveUserMessage,
                                plan,
                                toolResults,
                                ct)
                            .ConfigureAwait(false);
                        answer = !string.IsNullOrWhiteSpace(repairAnswer)
                            && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage)
                            && !LooksLikeWriterControlLeak(repairAnswer)
                                ? repairAnswer
                                : BuildSourceBackedSafeFallbackAnswer(
                                    toolResults,
                                    effectiveUserMessage,
                                    plan.Language,
                                    shouldAvoidRaw: true);
                    }
                    else
                    {
                        answer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
                    }
                }
            }
        }

        if (LooksLikeMissingExactItemWithoutSourceLeads(answer))
        {
            sources?.Clear();
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        }

        if (!usedSourceBackedWriterTimeoutFallback
            && TryFinalizeSourceBackedPlanningResponse(
                answer,
                toolResults,
                effectiveUserMessage,
                plan.Language,
                out var outerFinalizedPlanningAnswer,
                out var outerFinalizedPlanningSources,
                out var outerFinalizedPlanningAnalysis,
                out var outerFinalizedPlanningResolution))
        {
            ClientLog.Info(
                "ToolAgent planning outer finalizer: " +
                $"resolution={outerFinalizedPlanningResolution} " +
                $"items={outerFinalizedPlanningAnalysis.ItemCount} " +
                $"supported={outerFinalizedPlanningAnalysis.SupportedItemCount} " +
                $"unsupported={outerFinalizedPlanningAnalysis.UnsupportedItemCount} " +
                $"candidates={outerFinalizedPlanningAnalysis.CandidateCount} " +
                $"sources={outerFinalizedPlanningSources.Count}");
            LogSourceBackedPlanningTrace(
                "outer-finalizer",
                toolResults,
                effectiveUserMessage,
                plan.Language);
            EmitPlanningFinalizerDecisionTrace(
                "outer-finalizer",
                plan.Intent,
                outerFinalizedPlanningResolution,
                outerFinalizedPlanningAnalysis,
                outerFinalizedPlanningSources.Count,
                $"router+tools_{outerFinalizedPlanningResolution}:{plan.Intent}");
            answer = outerFinalizedPlanningAnswer;
            sources = outerFinalizedPlanningSources;
            _lastAnswerSource = $"router+tools_{outerFinalizedPlanningResolution}:{plan.Intent}";
        }

        if (!usedSourceBackedWriterTimeoutFallback
            && shouldBufferWriterOutputForSourceBackedGuard
            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage)
            && toolResults.Items.Any(static x => x.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(x.Result))
            && (LooksLikeWriterControlLeak(answer)
                || LooksLikePoorPlanningFallbackAnswer(answer, effectiveUserMessage)))
        {
            var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    effectiveUserMessage,
                    plan,
                    toolResults,
                    ct)
                .ConfigureAwait(false);
            answer = !string.IsNullOrWhiteSpace(repairAnswer)
                     && !LooksLikeWriterControlLeak(repairAnswer)
                     && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage)
                ? repairAnswer
                : BuildSourceBackedSafeFallbackAnswer(
                    toolResults,
                    effectiveUserMessage,
                    plan.Language,
                    shouldAvoidRaw: true);

            sources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
            if (sources.Count == 0 && LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
                sources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
            if (sources.Count == 0)
                sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
        }

        if (!usedSourceBackedWriterTimeoutFallback
            && shouldBufferWriterOutputForSourceBackedGuard
            && ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            && toolResults.Items.Any(static x => x.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(x.Result)))
        {
            var structuredSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                answer,
                toolResults,
                effectiveUserMessage,
                plan.Language);
            if (ShouldRejectUnsupportedPlanningAnswerForFinal(structuredSupport, effectiveUserMessage)
                || structuredSupport.Sources.Count == 0)
            {
                if (TryBuildSupportedStructuredPlanningAnswer(
                        toolResults,
                        plan.Language,
                        effectiveUserMessage,
                        out var supportedStructuredAnswer,
                        out var supportedStructuredSources,
                        out var supportedStructuredSupport))
                {
                    answer = supportedStructuredAnswer;
                    sources = supportedStructuredSources;
                    _lastAnswerSource = $"router+tools_structured_planning_supported_rebuild:{plan.Intent}";
                }
                else
                {
                    var searchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(toolResults);
                    EmitPlanningInsufficientFallbackTrace(
                        "router-structured-guard",
                        plan.Intent,
                        "supported_rebuild_unavailable",
                        Math.Max(structuredSupport.CandidateCount, supportedStructuredSupport.CandidateCount),
                        searchAlreadyExpanded);
                    answer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        effectiveUserMessage,
                        effectiveUserMessage,
                        Math.Max(structuredSupport.CandidateCount, supportedStructuredSupport.CandidateCount),
                        searchAlreadyExpanded: searchAlreadyExpanded);
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"router+tools_structured_planning_rejected_unsupported:{plan.Intent}";
                    LogSourceBackedPlanningTrace(
                        "structured-planning-rejected-unsupported",
                        toolResults,
                        effectiveUserMessage,
                        plan.Language);
                }
            }
            else
            {
                sources = structuredSupport.Sources.ToList();
            }
        }

        if (!usedSourceBackedWriterTimeoutFallback
            && TryFinalizeSourceBackedPlanningResponse(
                answer,
                toolResults,
                effectiveUserMessage,
                plan.Language,
                out var finalOuterPlanningAnswer,
                out var finalOuterPlanningSources,
                out var finalOuterPlanningAnalysis,
                out var finalOuterPlanningResolution))
        {
            ClientLog.Info(
                "ToolAgent planning outer last-mile finalizer: " +
                $"resolution={finalOuterPlanningResolution} " +
                $"items={finalOuterPlanningAnalysis.ItemCount} " +
                $"supported={finalOuterPlanningAnalysis.SupportedItemCount} " +
                $"unsupported={finalOuterPlanningAnalysis.UnsupportedItemCount} " +
                $"candidates={finalOuterPlanningAnalysis.CandidateCount} " +
                $"sources={finalOuterPlanningSources.Count}");
            LogSourceBackedPlanningTrace(
                "outer-last-mile-finalizer",
                toolResults,
                effectiveUserMessage,
                plan.Language);
            EmitPlanningFinalizerDecisionTrace(
                "outer-last-mile-finalizer",
                plan.Intent,
                finalOuterPlanningResolution,
                finalOuterPlanningAnalysis,
                finalOuterPlanningSources.Count,
                $"router+tools_{finalOuterPlanningResolution}:{plan.Intent}");
            answer = finalOuterPlanningAnswer;
            sources = finalOuterPlanningSources;
            _lastAnswerSource = $"router+tools_{finalOuterPlanningResolution}:{plan.Intent}";
        }

        if (!usedSourceBackedWriterTimeoutFallback
            && ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(answer, toolResults, effectiveUserMessage, plan.Language))
        {
            sources?.Clear();
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        }

        if (sources is { Count: > 0 })
            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);

        if (sources is { Count: > 0 })
            answer = InjectInlineSources(answer, sources, plan.Language);

        if (shouldBufferWriterOutputForSourceBackedGuard && onDelta is not null && !string.IsNullOrWhiteSpace(answer))
            onDelta(answer);

        object? sourcesPayload = null;
        if (sources is { Count: > 0 })
            sourcesPayload = BuildSourcesPayload(plan.Intent, sources);

        if (_lastUsedInventoryRendered)
        {
            _lastAnswerSource = $"router+tools+inventory_bypass:{plan.Intent}";
        }
        else if (string.IsNullOrWhiteSpace(_lastAnswerSource)
            || string.Equals(_lastAnswerSource, "unknown", StringComparison.OrdinalIgnoreCase)
            || (!_lastAnswerSource.Contains("planning", StringComparison.OrdinalIgnoreCase)
                && !_lastAnswerSource.Contains("source_alignment", StringComparison.OrdinalIgnoreCase)
                && !_lastAnswerSource.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
        {
            _lastAnswerSource = $"router+tools+writer:{plan.Intent}";
        }
        onProgress?.Invoke(string.Empty);
        ClientLog.Info(
            "ToolAgent answer stage end: " +
            $"intent={plan.Intent}|answerSource={_lastAnswerSource}|answerChars={answer?.Length ?? 0}|sources={(sources?.Count ?? 0)}|" +
            $"sourcesPayload={sourcesPayload is not null}|writerTools={string.Join(",", _lastWriterToolNames)}");
        EmitRagTrace(
            "answer.stage.end",
            ("intent", plan.Intent),
            ("answer_source", _lastAnswerSource),
            ("answer_chars", answer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("sources_payload", sourcesPayload is not null),
            ("writer_tools", _lastWriterToolNames.ToArray()));
        return FinalizeAndReturn(swTotalPipeline, userMessage, answer ?? string.Empty, sourcesPayload, plan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
    }

    private void EmitPlanningFinalizerDecisionTrace(
        string context,
        string intent,
        string resolution,
        PlanningAnswerSupportAnalysis analysis,
        int sourceCount,
        string answerSource)
        => EmitRagTrace(
            "planning.finalizer.decision",
            ("context", context),
            ("intent", intent),
            ("handled", true),
            ("resolution", resolution),
            ("items", analysis.ItemCount),
            ("supported", analysis.SupportedItemCount),
            ("unsupported", analysis.UnsupportedItemCount),
            ("candidates", analysis.CandidateCount),
            ("sources", sourceCount),
            ("answer_source", answerSource));

    private void EmitPlanningInsufficientFallbackTrace(
        string context,
        string intent,
        string reason,
        int candidateCount,
        bool searchAlreadyExpanded)
        => EmitRagTrace(
            "planning.insufficient_fallback",
            ("context", context),
            ("intent", intent),
            ("reason", reason),
            ("candidate_count", candidateCount),
            ("search_expanded", searchAlreadyExpanded));

    private static (string answer, List<ToolMemory.SourceRef> sources) BuildSourceBackedWriterTimeoutFallback(
        ToolResults toolResults,
        string query,
        string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        if (ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
            && TryBuildSupportedStructuredPlanningAnswer(
                toolResults,
                language,
                intentQuery,
                out var supportedAnswer,
                out var supportedSources,
                out _))
        {
            return (RemoveTrailingModelEmittedSourceList(supportedAnswer).Trim(), supportedSources);
        }

        var answer = BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, intentQuery, language);
        if (string.IsNullOrWhiteSpace(answer) && LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
        {
            answer = BuildReadablePartialPlanningEvidenceAnswer(
                SelectSourceBackedExtractiveHits(toolResults, intentQuery, maxHits: 8).ToList(),
                intentQuery,
                language);
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            var planningOrExtractive = BuildSourceBackedPlanningOrExtractiveAnswer(
                toolResults,
                intentQuery,
                language,
                minPlanningItems: 1);
            if (!LooksLikeBroadEvidenceStillInsufficientAnswer(planningOrExtractive))
                answer = planningOrExtractive;
            else if (string.IsNullOrWhiteSpace(answer))
                answer = planningOrExtractive;
        }

        if (string.IsNullOrWhiteSpace(answer) || LooksLikeBroadEvidenceStillInsufficientAnswer(answer))
        {
            var readableFallback = BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, intentQuery, language);
            if (!string.IsNullOrWhiteSpace(readableFallback))
                answer = readableFallback;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = BuildSourceBackedSafeFallbackAnswer(
                toolResults,
                query,
                language,
                shouldAvoidRaw: true);
        }

        var sources = LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
            ? DeriveSourcesFromPlanningHits(toolResults, intentQuery)
            : DeriveSourcesForSourceBackedFallback(toolResults, intentQuery);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRankedRagHits(toolResults, intentQuery);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return ((answer ?? string.Empty).Trim(), sources);
    }

    private static string TryBuildNoRagEvidenceAnswerForEmptySearch(ToolResults toolResults, string language, string? query)
    {
        var ragItems = toolResults.Items
            .Where(static x => x.ToolName is "rag.search" or "rag.multi_search")
            .ToList();
        if (ragItems.Count == 0)
            return string.Empty;

        if (ragItems.Any(static x => HasRagBusyResult(x.Result) || IsRagBusyError(TryGetStructuredToolError(x))))
            return DeterministicAgentText.RagSearchBusy(language);

        if (ragItems.Any(static x => HasRagHits(x.Result)))
            return string.Empty;

        if (IsBroadenedSourceSearchConfirmationEnvelope(query))
        {
            var intentQuery = ResolveSourceBackedFallbackIntentQuery(query ?? string.Empty);
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query ?? string.Empty,
                intentQuery,
                nearbyHitCount: 0,
                searchAlreadyExpanded: true);
        }

        if (ShouldOfferBroadenedSourceSearch(query) || LooksLikeBroadEmptySourceSearchRequest(query))
        {
            if (HasAttemptedBroadenedSourceBackedRetrieval(toolResults))
            {
                return BuildBroadEvidenceStillInsufficientAnswer(
                    language,
                    query ?? string.Empty,
                    query ?? string.Empty,
                    nearbyHitCount: 0,
                    searchAlreadyExpanded: true);
            }

            return DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
                + " "
                + DeterministicAgentText.SourceBackedExpandedSearchOffer(language);
        }

        return DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
            + " "
            + DeterministicAgentText.SourceBackedClarificationRequest(language);
    }

    private static bool HasRagBusyResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetBool(result, "busy") is true || TryGetBool(result, "Busy") is true)
            return true;

        return IsRagBusyError(TryGetString(result, "error") ?? TryGetString(result, "Error"));
    }

    private static string TryBuildAmbiguousBareDocumentaryFragmentAnswer(
        ToolResults toolResults,
        string userMessage,
        string language,
        out List<ToolMemory.SourceRef> sources)
    {
        sources = new List<ToolMemory.SourceRef>();
        if (!LooksLikeAmbiguousBareDocumentaryFragment(userMessage))
            return string.Empty;

        var hasRagHits = toolResults.Items
            .Where(static x => x.ToolName is "rag.search" or "rag.multi_search")
            .Any(static x => HasRagHits(x.Result));
        if (!hasRagHits)
            return string.Empty;

        var answer = BuildRagEvidenceFallbackAnswer(toolResults, userMessage, language);
        if (string.IsNullOrWhiteSpace(answer))
            answer = DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
                + " "
                + DeterministicAgentText.SourceBackedClarificationRequest(language);

        sources = DeriveSourcesFromRankedRagHits(toolResults, userMessage);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return answer;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesForSourceBackedFallback(ToolResults toolResults, string query)
    {
        List<ToolMemory.SourceRef> sources;
        var requiresStrictStructuredPlanningSources = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            sources = DeriveSourcesFromPlanningHits(toolResults, query);
            if (requiresStrictStructuredPlanningSources)
                return new List<ToolMemory.SourceRef>();
        }
        else if (LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query))
        {
            sources = DeriveSourcesFromOptionHits(toolResults, query);
        }
        else if (LooksLikeSourceBackedActionRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query)
            || ShouldUseSourceBackedExtractiveAnswer(query, toolResults))
        {
            sources = DeriveSourcesFromExtractiveHits(toolResults, query);
        }
        else
        {
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
        }

        if (requiresStrictStructuredPlanningSources)
            return new List<ToolMemory.SourceRef>();

        if (sources.Count == 0)
            sources = DeriveSourcesFromExtractiveHits(toolResults, query);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return sources;
    }

    private static bool LooksLikeAmbiguousBareDocumentaryFragment(string? query)
    {
        var raw = CollapseWhitespace(query ?? string.Empty).Trim();
        if (raw.Length < 8 || raw.Length > 90 || raw.Contains('?'))
            return false;

        raw = raw.Trim(' ', '.', '!', ':', ';');
        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 8)
            return false;

        if (!Regex.IsMatch(
                normalized,
                @"^(?:un|une|des|du|de la|de l|a|an|some|una|unos|unas|um|uma|ein|eine|einen|einem|uno|dei|delle|del)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:donne|donner|trouve|trouver|cherche|chercher|liste|lister|montre|montrer|explique|expliquer|resume|resumer|propose|proposer|recommande|recommander|compare|comparer|give|find|search|list|show|explain|summari[sz]e|propose|recommend|compare|buscar|encontrar|listar|mostrar|explicar|resumir|proponer|recomendar|comparar|procurar|listar|mostrar|explicar|resumir|propor|recomendar|comparar|finden|suchen|auflisten|zeigen|erklaren|zusammenfassen|vorschlagen|empfehlen|vergleichen|trovare|cercare|elencare|mostrare|spiegare|riassumere|proporre|raccomandare|confrontare)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:pour|avec|sans|selon|for|with|without|against|para|con|sin|sem|com|mit|ohne|gegen|per|con|senza)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return true;
    }

    private static List<ToolResults.Item> ExecuteLocalTools(RouterPlan plan, IReadOnlyList<(string role, string content)> chatHistory)
    {
        var items = new List<ToolResults.Item>();

        if (plan.ToolCalls.Any(c => string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)))
        {
            var questions = chatHistory
                .Where(m => string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.content ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .TakeLast(50)
                .ToList();

            var payload = new { questions };
            items.Add(new ToolResults.Item
            {
                ToolName = "meta.list_questions",
                Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
                DurationMs = 0
            });
        }

        return items;
    }

    private string BuildDocumentsListAnswer(ToolResults toolResults, string language)
    {
        // We always format deterministically from tool results to avoid hallucinated paths.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (item is null)
            return LocalizedStrings.NoDocumentsFound(language);

        try
        {
            var (docs, _, _, _, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            var list = DocumentListHelper.BuildUserText(docs, endOfList, dropped);
            if (!string.IsNullOrWhiteSpace(list))
            {
                var scopePath = TryInferDocumentsScopePath(item.Result);
                return $"{DeterministicAgentText.DocumentsListHeader(language, scopePath)}{Environment.NewLine}{list}".TrimEnd();
            }

            return LocalizedStrings.NoDocumentsFound(language);
        }
        catch
        {
            return LocalizedStrings.DocumentListError(language);
        }
    }

    private string BuildDocumentsTreeAnswer(ToolResults toolResults, string language)
    {
        var treeItem = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree");
        if (treeItem is not null)
        {
            try
            {
                if (treeItem.Result.ValueKind == JsonValueKind.Object
                    && treeItem.Result.TryGetProperty("markdown", out var md)
                    && md.ValueKind == JsonValueKind.String)
                {
                    var markdown = (md.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(markdown))
                        return markdown;
                }
            }
            catch
            {
            }
        }

        // Fallback for older results built from list/search.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (item is null)
            return LocalizedStrings.NoDocumentsFound(language);

        try
        {
            var (docs, _, _, _, _, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);

            if (docs.Count == 0)
                return LocalizedStrings.NoDocumentsFound(language);

            var tree = DocumentTreeHelper.BuildMarkdownTree(docs);

            // optionnel : petite note si certains fichiers n'ont pas pu être résolus
            if (dropped > 0)
            {
                tree += $"\n\n{DeterministicAgentText.TreeSkippedLocalUnresolved(language, dropped)}";
            }

            return tree;
        }
        catch
        {
            return LocalizedStrings.DocumentTreeError(language);
        }
    }

    private ToolResults.Item? TryBuildInventoryRenderedItem(ToolResults toolResults, string language, CancellationToken ct)
    {
        _ = ct;

        string kind = string.Empty;
        object? data = null;

        if (toolResults.Items.Any(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "tree";
            data = BuildTreeInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "list";
            data = BuildListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "stats";
            data = BuildStatsInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "categories";
            data = BuildCategoriesInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_list";
            data = BuildSummaryStatusListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_count";
            data = BuildSummaryStatusCountInventoryData(toolResults);
        }
        else if (toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_quality" && string.IsNullOrWhiteSpace(x.Error)) is { } extractionQualityItem)
        {
            kind = "extraction_quality";
            data = BuildExtractionQualityInventoryData(toolResults);
            UpdateLastListedDocumentsFromExtractionQualityResult(extractionQualityItem.Result);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.extraction_pages" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "extraction_pages";
            data = BuildExtractionPagesInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "count";
            data = BuildCountInventoryData(toolResults, "documents.count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_count";
            data = BuildCountInventoryData(toolResults, "documents.empty_count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_list";
            data = BuildEmptyFoldersInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "diagnostic_performance";
            data = BuildDiagnosticPerformanceInventoryData(toolResults);
        }

        if (string.IsNullOrWhiteSpace(kind) || data is null)
            return null;

        var payload = new
        {
            kind,
            language,
            authoritative = true,
            data
        };

        if (data is not null)
        {
            _mem.LastDeterministicRender = new ToolMemory.DeterministicRenderState
            {
                Kind = kind,
                DataJson = JsonSerializer.Serialize(data),
                RouterIntent = _mem.LastRouterIntent,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return new ToolResults.Item
        {
            ToolName = "inventory.rendered",
            Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
            DurationMs = 0
        };
    }

    private async Task<bool> TryExpandSourceBackedEvidenceRetrievalAsync(
        ToolResults toolResults,
        RouterPlan plan,
        string effectiveUserMessage,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onProgress,
        string? categoryScopeOverride = null)
    {
        if (ShouldRespectLlmRouterGeneralWithoutTools(plan))
        {
            EmitRagTrace(
                "evidence.exploration.skipped",
                ("query", effectiveUserMessage),
                ("kind", "chat"),
                ("reason", "router_general_no_tools"),
                ("score", 0),
                ("usable_hits", 0),
                ("candidates", 0));
            return false;
        }

        var explorationQuery = ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage);
        var currentAnalysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, explorationQuery, plan.Language);
        var forceBroadenedExploration = IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage);
        var allowBroadResearchPass = ShouldAllowSourceBackedBroadResearchPass(
            explorationQuery,
            currentAnalysis,
            forceBroadenedExploration);
        if (!currentAnalysis.ShouldExplore && !forceBroadenedExploration && !allowBroadResearchPass)
        {
            EmitRagTrace(
                "evidence.exploration.skipped",
                ("query", explorationQuery),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("score", currentAnalysis.Score),
                ("usable_hits", currentAnalysis.UsableHitCount),
                ("candidates", currentAnalysis.CandidateCount));
            return false;
        }

        EmitRagTrace(
            "evidence.exploration.start",
            ("query", explorationQuery),
            ("kind", currentAnalysis.Kind),
            ("reason", currentAnalysis.Reason),
            ("score", currentAnalysis.Score),
            ("usable_hits", currentAnalysis.UsableHitCount),
            ("candidates", currentAnalysis.CandidateCount),
            ("distinct_docs", currentAnalysis.DistinctDocumentCount),
            ("distinct_pages", currentAnalysis.DistinctSourcePageCount),
            ("force_broadened", forceBroadenedExploration),
            ("allow_broad_research", allowBroadResearchPass));

        try
        {
            await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Category hints are helpful for broad exploration, but retrieval must still work without them.
        }

        var acceptedAny = false;
        var anchorFollowupAttempts = 0;
        var anchorFollowupSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchorFollowupRoundLimit = ResolveSourceBackedAnchorFollowupRoundLimit(explorationQuery);
        var categoryScope = string.IsNullOrWhiteSpace(categoryScopeOverride)
            ? ResolveRagCategoryScope(explorationQuery)
            : categoryScopeOverride;
        var categoryScopeTrustedByCurrentEvidence = false;
        var seededNavigationScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ShouldSeedSourceBackedNavigationStructure(explorationQuery, currentAnalysis, forceBroadenedExploration))
        {
            await TrySeedSourceBackedNavigationStructureAsync(
                    toolResults,
                    categoryScope,
                    explorationQuery,
                    plan.Language,
                    ct,
                    onProgress,
                    seededNavigationScopes)
                .ConfigureAwait(false);
        }

        var ragCallBudget = ResolveSourceBackedEvidenceExplorationRagCallBudget(explorationQuery, currentAnalysis, forceBroadenedExploration);
        var remainingRagCalls = Math.Max(0, ragCallBudget - CountRagRetrievalToolCalls(toolResults));
        var reservedDeterministicPasses = new List<SourceBackedEvidenceExplorationPass>();
        var attemptedLlmPlanner = false;
        var llmPlannerProducedPass = false;
        var plannerFirstEligible =
            forceBroadenedExploration
            || allowBroadResearchPass
            || LooksLikeGenericCollectionOrListRequest(explorationQuery)
            || LooksLikeAnyDocumentaryPlanningRequest(explorationQuery)
            || LooksLikeSourceBackedBroadResearchRequest(explorationQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(explorationQuery)
            || LooksLikeSoftChoiceRecommendationRequest(explorationQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(explorationQuery)
            || LooksLikeBroadSourceBackedCompositionRequest(explorationQuery)
            || LooksLikeBroadSynthesisRequestShape(explorationQuery)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(explorationQuery);
        var broadSearchNeedsStrategicPlanner =
            plannerFirstEligible
            || LooksLikeSourceBackedBroadResearchRequest(explorationQuery);
        var structuredPlanningTargetCoverageReached = false;
        var explorationSw = Stopwatch.StartNew();
        var explorationTimeBudgetStopEmitted = false;

        bool ShouldStopForExplorationTimeBudget(string stage)
        {
            if (explorationSw.ElapsedMilliseconds < SourceBackedEvidenceExplorationTimeoutMs)
                return false;

            if (!explorationTimeBudgetStopEmitted)
            {
                explorationTimeBudgetStopEmitted = true;
                EmitRagTrace(
                    "evidence.exploration.time_budget.stop",
                    ("stage", stage),
                    ("elapsed_ms", explorationSw.ElapsedMilliseconds),
                    ("timeout_ms", SourceBackedEvidenceExplorationTimeoutMs),
                    ("kind", currentAnalysis.Kind),
                    ("reason", currentAnalysis.Reason),
                    ("score", currentAnalysis.Score),
                    ("usable_hits", currentAnalysis.UsableHitCount),
                    ("candidates", currentAnalysis.CandidateCount),
                    ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                    ("target_slots", currentAnalysis.TargetSlotCount),
                    ("distinct_pages", currentAnalysis.DistinctSourcePageCount),
                    ("remaining_rag_calls", remainingRagCalls));
            }

            return true;
        }

        bool ShouldContinueExploring()
        {
            if (structuredPlanningTargetCoverageReached)
                return false;
            if (ShouldStopForExplorationTimeBudget("continue_check"))
                return false;

            if (currentAnalysis.ShouldExplore
                || (forceBroadenedExploration && !acceptedAny)
                || (allowBroadResearchPass && !acceptedAny))
            {
                return true;
            }

            return broadSearchNeedsStrategicPlanner
                   && remainingRagCalls > 0
                   && ShouldUseLlmSourceBackedEvidencePlanner(explorationQuery, currentAnalysis)
                   && (!attemptedLlmPlanner || !acceptedAny);
        }

        bool HasHighConfidenceSourceBackedCoverageForAnchorStop()
        {
            if (!currentAnalysis.IsSufficient)
                return false;

            if (UsesSourceBackedPlanningCoverage(explorationQuery) || currentAnalysis.Kind == "planning")
            {
                var requiredCandidateTarget = Math.Max(
                    currentAnalysis.MinimumCandidateCount,
                    Math.Max(1, currentAnalysis.TargetSlotCount));
                var requiredPageTarget = Math.Min(requiredCandidateTarget, Math.Max(3, currentAnalysis.TargetSlotCount));
                return currentAnalysis.CandidateCount >= requiredCandidateTarget
                       && currentAnalysis.DistinctSourcePageCount >= requiredPageTarget
                       && currentAnalysis.Score >= 90;
            }

            return !currentAnalysis.ShouldExplore || currentAnalysis.Score >= 80;
        }

        bool ShouldDeferAnchorFollowupAfterAcceptedPass(SourceBackedEvidenceExplorationPass pass)
        {
            if (!UsesSourceBackedPlanningCoverage(explorationQuery)
                || !string.Equals(pass.Origin, "deterministic_seed", StringComparison.OrdinalIgnoreCase)
                || HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(currentAnalysis, explorationQuery))
            {
                return false;
            }

            EmitRagTrace(
                "evidence.exploration.anchor_followup.deferred",
                ("reason", "structured_planning_complete_deterministic_passes_first"),
                ("label", pass.Label),
                ("origin", pass.Origin),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("distinct_pages", currentAnalysis.DistinctSourcePageCount),
                ("remaining_rag_calls", remainingRagCalls));
            return true;
        }

        void MarkStructuredPlanningTargetCoverageIfReached(string stage, string? label)
        {
            if (structuredPlanningTargetCoverageReached
                || !HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(currentAnalysis, explorationQuery))
            {
                return;
            }

            structuredPlanningTargetCoverageReached = true;
            EmitRagTrace(
                "evidence.exploration.structured_target_met",
                ("stage", stage),
                ("label", label),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("distinct_pages", currentAnalysis.DistinctSourcePageCount),
                ("score", currentAnalysis.Score),
                ("remaining_rag_calls", remainingRagCalls));
        }

        bool ShouldReserveLlmPlannerRetrievalCall()
            => !attemptedLlmPlanner
               && remainingRagCalls > 0
               && MaxSourceBackedLlmEvidenceExplorationPasses > 0
               && ShouldContinueExploring()
               && ShouldUseLlmSourceBackedEvidencePlanner(explorationQuery, currentAnalysis);

        bool ShouldPrioritizeLlmPlannerBeforeDeterministicPasses()
            => !attemptedLlmPlanner
               && remainingRagCalls > 0
               && plannerFirstEligible
               && ShouldReserveLlmPlannerRetrievalCall()
               && ShouldUseLlmSourceBackedEvidencePlanner(explorationQuery, currentAnalysis);

        async Task<bool> TryExecuteExplorationPassAsync(
            SourceBackedEvidenceExplorationPass pass,
            bool allowReservedLlmPlannerCall = false)
        {
            if (!ShouldContinueExploring() || remainingRagCalls <= 0)
                return false;

            var passToolName = NormalizeToolName(pass.ToolName);
            if (string.Equals(passToolName, "documents.context", StringComparison.OrdinalIgnoreCase))
            {
                var hasContextTarget = !string.IsNullOrWhiteSpace(pass.DocRef)
                                       || !string.IsNullOrWhiteSpace(pass.DocId)
                                       || !string.IsNullOrWhiteSpace(pass.DocPath)
                                       || !string.IsNullOrWhiteSpace(pass.ChunkId);
                if (!hasContextTarget)
                    return false;

                var contextBeforeAnalysis = currentAnalysis;
                var contextArgs = CreateJsonArgs(new
                {
                    docRef = pass.DocRef,
                    docId = pass.DocId,
                    docPath = pass.DocPath,
                    chunkId = pass.ChunkId,
                    pageStart = pass.PageStart,
                    pageEnd = pass.PageEnd,
                    before = 2,
                    after = 4,
                    limit = 12,
                    offset = 0
                });

                var contextStopwatch = Stopwatch.StartNew();
                try
                {
                    onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
                    onProgress?.Invoke(DeterministicAgentText.ProgressExploreFollowupSources(plan.Language));
                    EmitRagTrace(
                        "evidence.exploration.context.start",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("doc_ref", pass.DocRef),
                        ("doc_id", pass.DocId),
                        ("doc_path", pass.DocPath),
                        ("chunk_id", pass.ChunkId),
                        ("page_start", pass.PageStart),
                        ("page_end", pass.PageEnd));
                    var contextResult = await ExecDocumentsContextAsync(contextArgs, ct).ConfigureAwait(false);
                    contextStopwatch.Stop();
                    if (!HasDocumentContextItems(contextResult))
                    {
                        _lastToolDurations.Add(("documents.context", contextStopwatch.ElapsedMilliseconds, true));
                        RememberSourceBackedEvidenceExplorationPass(pass, contextBeforeAnalysis, null, contextStopwatch.ElapsedMilliseconds, accepted: false, rejectReason: "no_context_items", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                        EmitRagTrace(
                            "evidence.exploration.context.end",
                            ("label", pass.Label),
                            ("origin", pass.Origin),
                            ("accepted", false),
                            ("reason", "no_context_items"),
                            ("elapsed_ms", contextStopwatch.ElapsedMilliseconds));
                        return false;
                    }

                    var candidate = new ToolResults();
                    candidate.Items.AddRange(toolResults.Items);
                    candidate.Items.Add(new ToolResults.Item
                    {
                        ToolName = "documents.context",
                        Result = contextResult,
                        DurationMs = contextStopwatch.ElapsedMilliseconds
                    });

                    var candidateAnalysis = AnalyzeSourceBackedEvidenceSufficiency(candidate, explorationQuery, plan.Language);
                    toolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "documents.context",
                        Result = contextResult,
                        DurationMs = contextStopwatch.ElapsedMilliseconds
                    });
                    currentAnalysis = candidateAnalysis;
                    _lastToolDurations.Add(("documents.context", contextStopwatch.ElapsedMilliseconds, true));
                    RememberSourceBackedEvidenceExplorationPass(pass, contextBeforeAnalysis, candidateAnalysis, contextStopwatch.ElapsedMilliseconds, accepted: true, rejectReason: null, effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    EmitRagTrace(
                        "evidence.exploration.context.end",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("accepted", true),
                        ("elapsed_ms", contextStopwatch.ElapsedMilliseconds),
                        ("score", candidateAnalysis.Score),
                        ("usable_hits", candidateAnalysis.UsableHitCount),
                        ("candidates", candidateAnalysis.CandidateCount));
                    return true;
                }
                catch (Exception ex)
                {
                    contextStopwatch.Stop();
                    _lastToolDurations.Add(("documents.context", contextStopwatch.ElapsedMilliseconds, false));
                    RememberSourceBackedEvidenceExplorationPass(pass, contextBeforeAnalysis, null, contextStopwatch.ElapsedMilliseconds, accepted: false, rejectReason: "error", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    EmitRagTrace(
                        "evidence.exploration.context.error",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("elapsed_ms", contextStopwatch.ElapsedMilliseconds),
                        ("error", ex.Message));
                    return false;
                }
            }

            if (pass.Queries.Length == 0)
            {
                var scopeOnlyHasDocumentScope = !string.IsNullOrWhiteSpace(pass.DocId) || !string.IsNullOrWhiteSpace(pass.DocPath);
                var resolvedScopeOnlyCategoryScope = scopeOnlyHasDocumentScope
                    ? NullIfWhiteSpace(pass.CategoryScope)
                    : ResolveLlmPlannedRagCategoryScope(pass.CategoryScope);
                var (scopeOnlyCategoryScope, scopeOnlyCategoryScopeReusedFromInference) =
                    ResolveSourceBackedExplorationPassCategoryScope(
                        resolvedScopeOnlyCategoryScope,
                        categoryScope,
                        _mem.Execution.LastRagInferredCategoryScope,
                        pass.Origin,
                        scopeOnlyHasDocumentScope);
                var trustScopeOnlyCategoryScope = ShouldTrustSourceBackedExplorationPassCategoryScope(
                    pass.Origin,
                    scopeOnlyHasDocumentScope,
                    resolvedScopeOnlyCategoryScope,
                    scopeOnlyCategoryScope,
                    categoryScopeTrustedByCurrentEvidence,
                    scopeOnlyCategoryScopeReusedFromInference);
                EmitRagTrace(
                    "evidence.exploration.pass.scope_only",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("category", scopeOnlyCategoryScope),
                    ("resolved_category", resolvedScopeOnlyCategoryScope),
                    ("trusted", trustScopeOnlyCategoryScope),
                    ("remaining_rag_calls", remainingRagCalls));
                if (trustScopeOnlyCategoryScope
                    && !scopeOnlyHasDocumentScope
                    && !string.IsNullOrWhiteSpace(scopeOnlyCategoryScope))
                {
                    if (!categoryScopeTrustedByCurrentEvidence && !scopeOnlyCategoryScopeReusedFromInference)
                    {
                        EmitRagTrace(
                            "evidence.exploration.pass.category_scope.trusted",
                            ("label", pass.Label),
                            ("origin", pass.Origin),
                            ("category", scopeOnlyCategoryScope),
                            ("reason", "llm_planned_catalog_scope_without_retrieval_queries"));
                    }

                    var previousCategoryScope = categoryScope;
                    categoryScope = scopeOnlyCategoryScope;
                    categoryScopeTrustedByCurrentEvidence = true;
                    if (!string.Equals(
                            NormalizeCategoryPathArg(previousCategoryScope),
                            NormalizeCategoryPathArg(categoryScope),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        EmitRagTrace(
                            "evidence.exploration.category_scope.selected",
                            ("label", pass.Label),
                            ("origin", pass.Origin),
                            ("category", categoryScope),
                            ("previous_category", previousCategoryScope),
                            ("reason", "trusted_llm_scope_without_retrieval_queries"));
                    }
                }

                return false;
            }

            if (!allowReservedLlmPlannerCall
                && (ShouldPrioritizeLlmPlannerBeforeDeterministicPasses()
                    || (ShouldReserveLlmPlannerRetrievalCall()
                        && remainingRagCalls <= MaxSourceBackedLlmEvidenceExplorationPasses)))
            {
                if (!reservedDeterministicPasses.Any(existing =>
                        string.Equals(existing.Label, pass.Label, StringComparison.OrdinalIgnoreCase)))
                {
                    reservedDeterministicPasses.Add(pass);
                    EmitRagTrace(
                        "evidence.exploration.pass_reserved",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("purpose", pass.Purpose),
                        ("queries", pass.Queries),
                        ("remaining_rag_calls", remainingRagCalls),
                        ("reason", "reserved_for_llm_planner"));
                }

                return false;
            }

            var beforeAnalysis = currentAnalysis;
            remainingRagCalls--;
            var passDocId = NullIfWhiteSpace(pass.DocId);
            var passDocPath = NullIfWhiteSpace(pass.DocPath);
            var passTopK = ResolveSourceBackedEvidenceExplorationTopK(explorationQuery, pass.Label);
            var passHasDocumentScope = !string.IsNullOrWhiteSpace(passDocId) || !string.IsNullOrWhiteSpace(passDocPath);
            var usesStructuredInventoryExploration = UsesSourceBackedPlanningCoverage(explorationQuery);
            var passMaxPerDoc = passHasDocumentScope
                ? Math.Max(passTopK, 8)
                : usesStructuredInventoryExploration
                    ? Math.Max(passTopK, Math.Max(currentAnalysis.MinimumCandidateCount, currentAnalysis.TargetSlotCount))
                    : (int?)null;
            var passMaxPerPage = passHasDocumentScope
                ? ResolveSourceBackedDocumentScopedExplorationMaxPerPage(explorationQuery, pass.Label)
                : usesStructuredInventoryExploration
                    ? Math.Min(12, Math.Max(4, Math.Max(1, currentAnalysis.TargetSlotCount) / 2))
                    : (int?)null;
            var resolvedPassCategoryScope = passHasDocumentScope
                ? NullIfWhiteSpace(pass.CategoryScope)
                : ResolveLlmPlannedRagCategoryScope(pass.CategoryScope);
            var (passCategoryScope, passCategoryScopeReusedFromInference) =
                ResolveSourceBackedExplorationPassCategoryScope(
                    resolvedPassCategoryScope,
                    categoryScope,
                    _mem.Execution.LastRagInferredCategoryScope,
                    pass.Origin,
                    passHasDocumentScope);
            if (passCategoryScopeReusedFromInference)
            {
                EmitRagTrace(
                    "evidence.exploration.pass.category_scope.reused",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("category", passCategoryScope),
                    ("reason", _mem.Execution.LastRagInferredCategoryReason ?? "previous_inferred_category"));
            }
            if (!string.IsNullOrWhiteSpace(resolvedPassCategoryScope))
            {
                await TrySeedSourceBackedNavigationStructureAsync(
                        toolResults,
                        resolvedPassCategoryScope,
                        explorationQuery,
                        plan.Language,
                        ct,
                        onProgress,
                        seededNavigationScopes)
                    .ConfigureAwait(false);
            }
            var passTrustCategoryScope = ShouldTrustSourceBackedExplorationPassCategoryScope(
                pass.Origin,
                passHasDocumentScope,
                resolvedPassCategoryScope,
                passCategoryScope,
                categoryScopeTrustedByCurrentEvidence,
                passCategoryScopeReusedFromInference);
            if (passTrustCategoryScope
                && !categoryScopeTrustedByCurrentEvidence
                && !passCategoryScopeReusedFromInference
                && !string.IsNullOrWhiteSpace(resolvedPassCategoryScope))
            {
                EmitRagTrace(
                    "evidence.exploration.pass.category_scope.trusted",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("category", passCategoryScope),
                    ("reason", "llm_planned_catalog_scope"));
            }
            if (passTrustCategoryScope
                && !passHasDocumentScope
                && !string.IsNullOrWhiteSpace(passCategoryScope))
            {
                var previousCategoryScope = categoryScope;
                categoryScope = passCategoryScope;
                categoryScopeTrustedByCurrentEvidence = true;
                if (!string.Equals(
                        NormalizeCategoryPathArg(previousCategoryScope),
                        NormalizeCategoryPathArg(categoryScope),
                        StringComparison.OrdinalIgnoreCase))
                {
                    EmitRagTrace(
                        "evidence.exploration.category_scope.selected",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("category", categoryScope),
                        ("previous_category", previousCategoryScope),
                        ("reason", "trusted_llm_scope_for_following_passes"));
                }
            }

            var args = CreateJsonArgs(new
            {
                queries = pass.Queries,
                topK = passTopK,
                category = passCategoryScope,
                docId = passDocId,
                docPath = passDocPath,
                pageStart = pass.PageStart,
                pageEnd = pass.PageEnd,
                maxPerDoc = passMaxPerDoc,
                maxPerPage = passMaxPerPage,
                mode = "broad",
                researchMode = "source_exploration",
                includeResearchSurfaces = true,
                trustCategoryScope = passTrustCategoryScope
            });

            var sw = Stopwatch.StartNew();
            try
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
                onProgress?.Invoke(string.Equals(pass.Label, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pass.Label, "anchor_followup", StringComparison.OrdinalIgnoreCase)
                        ? DeterministicAgentText.ProgressExploreFollowupSources(plan.Language)
                        : DeterministicAgentText.ProgressSearchSourceBackedCandidates(plan.Language));
                EmitRagTrace(
                    "evidence.exploration.pass.start",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("purpose", pass.Purpose),
                    ("queries", pass.Queries),
                    ("top_k", passTopK),
                    ("category", passCategoryScope),
                    ("doc_id", passDocId),
                    ("doc_path", passDocPath),
                    ("page_start", pass.PageStart),
                    ("page_end", pass.PageEnd),
                    ("max_per_doc", passMaxPerDoc),
                    ("max_per_page", passMaxPerPage),
                    ("remaining_rag_calls", remainingRagCalls));
                var expandedResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                var expandedCategoryScope = passHasDocumentScope ? null : TryGetRagMultiSearchResultCategoryPath(expandedResult);
                if (!HasRagHits(expandedResult))
                {
                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, null, sw.ElapsedMilliseconds, accepted: false, rejectReason: "no_hits", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    return false;
                }

                var candidate = new ToolResults();
                candidate.Items.AddRange(toolResults.Items);
                candidate.Items.Add(new ToolResults.Item
                {
                    ToolName = "rag.multi_search",
                    Result = expandedResult,
                    DurationMs = sw.ElapsedMilliseconds
                });

                onProgress?.Invoke(DeterministicAgentText.ProgressVerifyCandidateCoverage(plan.Language));
                var analysisSw = Stopwatch.StartNew();
                EmitRagTrace(
                    "evidence.exploration.pass.analysis.start",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("query_count", pass.Queries.Length),
                    ("category", passCategoryScope),
                    ("doc_id", passDocId),
                    ("doc_path", passDocPath),
                    ("page_start", pass.PageStart),
                    ("page_end", pass.PageEnd));
                var candidateAnalysis = AnalyzeSourceBackedEvidenceSufficiency(candidate, explorationQuery, plan.Language);
                analysisSw.Stop();
                EmitRagTrace(
                    "evidence.exploration.pass.analysis.end",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("elapsed_ms", analysisSw.ElapsedMilliseconds),
                    ("kind", candidateAnalysis.Kind),
                    ("reason", candidateAnalysis.Reason),
                    ("score", candidateAnalysis.Score),
                    ("usable_hits", candidateAnalysis.UsableHitCount),
                    ("candidates", candidateAnalysis.CandidateCount),
                    ("distinct_docs", candidateAnalysis.DistinctDocumentCount),
                    ("distinct_pages", candidateAnalysis.DistinctSourcePageCount));
                if (UsesSourceBackedPlanningCoverage(explorationQuery)
                    && SourceBackedPlanningCoverageRegresses(currentAnalysis, candidateAnalysis))
                {
                    if (string.Equals(pass.Label, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pass.Label, "anchor_followup", StringComparison.OrdinalIgnoreCase))
                    {
                        anchorFollowupAttempts = anchorFollowupRoundLimit;
                    }

                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: false, rejectReason: "planning_coverage_regression", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    return false;
                }

                var isDocumentScopedAnchorFollowupPass =
                    string.Equals(pass.Label, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase);
                var hasFollowableRouteAnchors = false;
                if (isDocumentScopedAnchorFollowupPass)
                {
                    EmitRagTrace(
                        "evidence.exploration.pass.followup_probe.skipped",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("reason", "document_scoped_sequence_already_built"),
                        ("remaining_rag_calls", remainingRagCalls));
                }
                else if (ShouldStopForExplorationTimeBudget("after_pass_analysis"))
                {
                    EmitRagTrace(
                        "evidence.exploration.pass.followup_probe.skipped",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("reason", "time_budget_reached"),
                        ("remaining_rag_calls", remainingRagCalls));
                }
                else
                {
                    var followupSw = Stopwatch.StartNew();
                    EmitRagTrace(
                        "evidence.exploration.pass.followup_probe.start",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("remaining_rag_calls", remainingRagCalls));
                    hasFollowableRouteAnchors = HasNewSourceBackedAnchorFollowupOpportunity(candidate, candidateAnalysis);
                    followupSw.Stop();
                    EmitRagTrace(
                        "evidence.exploration.pass.followup_probe.end",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("elapsed_ms", followupSw.ElapsedMilliseconds),
                        ("has_followup", hasFollowableRouteAnchors));
                }
                bool EvaluateFinalDecisionMetric(string metric, Func<bool> evaluate)
                {
                    var metricSw = Stopwatch.StartNew();
                    EmitRagTrace(
                        "evidence.exploration.pass.final_decision.metric.start",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("metric", metric));
                    try
                    {
                        var result = evaluate();
                        metricSw.Stop();
                        EmitRagTrace(
                            "evidence.exploration.pass.final_decision.metric.end",
                            ("label", pass.Label),
                            ("origin", pass.Origin),
                            ("metric", metric),
                            ("result", result),
                            ("elapsed_ms", metricSw.ElapsedMilliseconds));
                        return result;
                    }
                    catch (Exception ex)
                    {
                        metricSw.Stop();
                        EmitRagTrace(
                            "evidence.exploration.pass.final_decision.metric.error",
                            ("label", pass.Label),
                            ("origin", pass.Origin),
                            ("metric", metric),
                            ("exception", ex.GetType().Name),
                            ("elapsed_ms", metricSw.ElapsedMilliseconds));
                        throw;
                    }
                }

                bool SkipFinalDecisionMetric(string metric, string reason)
                {
                    EmitRagTrace(
                        "evidence.exploration.pass.final_decision.metric.skipped",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("metric", metric),
                        ("reason", reason));
                    return false;
                }

                var finalDecisionSw = Stopwatch.StartNew();
                EmitRagTrace(
                    "evidence.exploration.pass.final_decision.start",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("current_score", currentAnalysis.Score),
                    ("candidate_score", candidateAnalysis.Score),
                    ("candidate_hits", candidateAnalysis.UsableHitCount),
                    ("candidate_pages", candidateAnalysis.DistinctSourcePageCount),
                    ("has_followup", hasFollowableRouteAnchors));
                var betterCoverage = EvaluateFinalDecisionMetric(
                    "better_coverage",
                    () => candidateAnalysis.Score > currentAnalysis.Score);
                var addsDiversity = EvaluateFinalDecisionMetric(
                    "adds_diversity",
                    () => CandidateSourceBackedEvidenceAddsUsefulDiversity(currentAnalysis, candidateAnalysis));
                var improvesScore = EvaluateFinalDecisionMetric(
                    "improves_score",
                    () => candidateAnalysis.Score > currentAnalysis.Score);
                var alreadyImprovesCoverage = betterCoverage || addsDiversity || improvesScore;
                var addsOrientation = alreadyImprovesCoverage
                    ? SkipFinalDecisionMetric("adds_orientation", "already_improves_coverage")
                    : EvaluateFinalDecisionMetric(
                        "adds_orientation",
                        () => CandidateSourceBackedEvidenceAddsUsefulOrientation(toolResults, candidate, currentAnalysis, candidateAnalysis, explorationQuery, plan.Language));
                var addsMaterial = alreadyImprovesCoverage || addsOrientation
                    ? SkipFinalDecisionMetric("adds_material", alreadyImprovesCoverage ? "already_improves_coverage" : "orientation_improves_coverage")
                    : EvaluateFinalDecisionMetric(
                        "adds_material",
                        () => CandidateSourceBackedEvidenceAddsExplorationMaterial(
                            toolResults,
                            candidate,
                            explorationQuery,
                            plan.Language,
                            forceBroadenedExploration));
                var planningAnchorFollowupRequiresCandidateGain =
                    UsesSourceBackedPlanningCoverage(explorationQuery)
                    && (string.Equals(pass.Label, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pass.Label, "anchor_followup", StringComparison.OrdinalIgnoreCase));
                var planningNeedsMoreCandidates =
                    UsesSourceBackedPlanningCoverage(explorationQuery)
                    && currentAnalysis.CandidateCount < Math.Max(1, currentAnalysis.MinimumCandidateCount);
                var planningPassRequiresCandidateGain =
                    planningAnchorFollowupRequiresCandidateGain
                    || planningNeedsMoreCandidates;
                var planningPassHasConcreteCandidateGain =
                    betterCoverage
                    || improvesScore
                    || candidateAnalysis.CandidateCount > currentAnalysis.CandidateCount
                    || candidateAnalysis.UsableHitCount > currentAnalysis.UsableHitCount;
                if (planningPassRequiresCandidateGain
                    && !planningPassHasConcreteCandidateGain)
                {
                    var planningNoGainRejectReason = planningAnchorFollowupRequiresCandidateGain
                        ? "planning_anchor_followup_without_candidate_gain"
                        : "planning_pass_without_candidate_gain";
                    finalDecisionSw.Stop();
                    EmitRagTrace(
                        "evidence.exploration.pass.final_decision.end",
                        ("label", pass.Label),
                        ("origin", pass.Origin),
                        ("better_coverage", betterCoverage),
                        ("adds_diversity", addsDiversity),
                        ("adds_orientation", addsOrientation),
                        ("adds_material", addsMaterial),
                        ("improves_score", improvesScore),
                        ("improves_coverage", false),
                        ("candidate_gain", false),
                        ("has_followup", hasFollowableRouteAnchors),
                        ("reject_reason", planningNoGainRejectReason),
                        ("elapsed_ms", finalDecisionSw.ElapsedMilliseconds));
                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: false, rejectReason: planningNoGainRejectReason, effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    return false;
                }
                var improvesCoverage = betterCoverage
                    || addsDiversity
                    || addsOrientation
                    || addsMaterial
                    || improvesScore;
                finalDecisionSw.Stop();
                EmitRagTrace(
                    "evidence.exploration.pass.final_decision.end",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("better_coverage", betterCoverage),
                    ("adds_diversity", addsDiversity),
                    ("adds_orientation", addsOrientation),
                    ("adds_material", addsMaterial),
                    ("improves_score", improvesScore),
                    ("improves_coverage", improvesCoverage),
                    ("has_followup", hasFollowableRouteAnchors),
                    ("elapsed_ms", finalDecisionSw.ElapsedMilliseconds));
                if (!improvesCoverage && !hasFollowableRouteAnchors)
                {
                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: false, rejectReason: "no_coverage_gain", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                    return false;
                }

                var commitSw = Stopwatch.StartNew();
                EmitRagTrace(
                    "evidence.exploration.pass.commit.start",
                    ("label", pass.Label),
                    ("origin", pass.Origin));
                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "rag.multi_search",
                    Result = expandedResult,
                    DurationMs = sw.ElapsedMilliseconds
                });
                _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                if (!_mem.LastToolNames.Contains("rag.multi_search", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("rag.multi_search");
                currentAnalysis = candidateAnalysis;
                var committedCategoryScope = ChooseCommittedSourceBackedCategoryScope(resolvedPassCategoryScope, expandedCategoryScope);
                if (!string.IsNullOrWhiteSpace(committedCategoryScope))
                    categoryScope = committedCategoryScope;
                if (!string.IsNullOrWhiteSpace(committedCategoryScope))
                    categoryScopeTrustedByCurrentEvidence = true;
                MarkStructuredPlanningTargetCoverageIfReached("pass_commit", pass.Label);
                RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: true, rejectReason: null, effectiveUserMessage: effectiveUserMessage, language: plan.Language);
                commitSw.Stop();
                EmitRagTrace(
                    "evidence.exploration.pass.commit.end",
                    ("label", pass.Label),
                    ("origin", pass.Origin),
                    ("committed_category", committedCategoryScope),
                    ("tool_items", toolResults.Items.Count),
                    ("elapsed_ms", commitSw.ElapsedMilliseconds));
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, false));
                RememberSourceBackedEvidenceExplorationPass(pass, beforeAnalysis, null, sw.ElapsedMilliseconds, accepted: false, rejectReason: "error", effectiveUserMessage: effectiveUserMessage, language: plan.Language);
            }

            return false;
        }

        static bool SourceBackedPlanningCoverageRegresses(
            SourceBackedEvidenceSufficiency current,
            SourceBackedEvidenceSufficiency candidate)
        {
            if (current.CandidateCount <= 0)
                return false;

            if (candidate.CandidateCount < current.CandidateCount
                && candidate.Score <= current.Score)
            {
                return true;
            }

            if (candidate.CandidateCount <= current.CandidateCount
                && candidate.DistinctSourcePageCount < current.DistinctSourcePageCount
                && candidate.Score + 10 < current.Score)
            {
                return true;
            }

            return false;
        }

        async Task<bool> TryExecuteAnchorFollowupPassAsync()
        {
            if (remainingRagCalls <= 0
                || anchorFollowupAttempts >= anchorFollowupRoundLimit
                || ShouldStopForExplorationTimeBudget("anchor_followup_start"))
            {
                return false;
            }

            if (UsesSourceBackedPlanningCoverage(explorationQuery)
                && currentAnalysis.UsableHitCount == 0
                && currentAnalysis.CandidateCount == 0)
            {
                EmitRagTrace(
                    "evidence.exploration.anchor_followup.skipped",
                    ("reason", "planning_without_current_evidence"),
                    ("usable_hits", currentAnalysis.UsableHitCount),
                    ("candidates", currentAnalysis.CandidateCount),
                    ("score", currentAnalysis.Score));
                return false;
            }

            if (ShouldDeferSparseSourceBackedPlanningAnchorFollowup(currentAnalysis, explorationQuery))
            {
                EmitRagTrace(
                    "evidence.exploration.anchor_followup.deferred",
                    ("reason", "planning_candidate_bank_still_sparse"),
                    ("candidates", currentAnalysis.CandidateCount),
                    ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                    ("target_slots", currentAnalysis.TargetSlotCount),
                    ("distinct_pages", currentAnalysis.DistinctSourcePageCount),
                    ("score", currentAnalysis.Score),
                    ("remaining_rag_calls", remainingRagCalls));
                return false;
            }

            var anchorPassBuildSw = Stopwatch.StartNew();
            EmitRagTrace(
                "evidence.exploration.pass_build.start",
                ("label", "anchor_followup"),
                ("origin", "anchor_followup"),
                ("remaining_rag_calls", remainingRagCalls),
                ("attempt", anchorFollowupAttempts + 1));
            var navigationSeedLimit = forceBroadenedExploration || UsesSourceBackedPlanningCoverage(explorationQuery)
                ? 5
                : 3;
            await TrySeedSourceBackedDocumentNavigationFromEvidenceAsync(
                    toolResults,
                    plan.Language,
                    ct,
                    onProgress,
                    seededNavigationScopes,
                    navigationSeedLimit)
                .ConfigureAwait(false);

            var hasPlanningCandidateDeficit =
                UsesSourceBackedPlanningCoverage(explorationQuery)
                && currentAnalysis.CandidateCount < Math.Max(1, currentAnalysis.MinimumCandidateCount);
            var documentScopedPasses = SelectNewDocumentScopedAnchorFollowupPasses(toolResults);
            var pass = BuildSourceBackedRouteAnchorFollowupExplorationPass(
                toolResults,
                explorationQuery,
                plan.Language);
            var globalSignature = BuildSourceBackedAnchorFollowupSignature(
                Enumerable.Empty<SourceBackedEvidenceExplorationPass>(),
                pass);
            var hasNewGlobalPass = pass is not null
                                   && pass.Queries.Length > 0
                                   && !string.IsNullOrWhiteSpace(globalSignature)
                                   && !anchorFollowupSignatures.Contains(globalSignature);
            anchorPassBuildSw.Stop();
            EmitRagTrace(
                "evidence.exploration.pass_build.end",
                ("label", "anchor_followup"),
                ("origin", "anchor_followup"),
                ("document_scoped_passes", documentScopedPasses.Length),
                ("document_scoped_skipped", false),
                ("document_scoped_skip_reason", null),
                ("planning_candidate_deficit", hasPlanningCandidateDeficit),
                ("has_global_pass", hasNewGlobalPass),
                ("global_queries", pass?.Queries.Length ?? 0),
                ("elapsed_ms", anchorPassBuildSw.ElapsedMilliseconds));
            if (documentScopedPasses.Length == 0 && !hasNewGlobalPass)
                return false;
            if (!currentAnalysis.ShouldExplore
                && !forceBroadenedExploration
                && documentScopedPasses.Length == 0
                && !hasNewGlobalPass)
            {
                return false;
            }

            anchorFollowupAttempts++;
            var accepted = false;
            foreach (var documentScopedPass in documentScopedPasses)
            {
                if (accepted && HasHighConfidenceSourceBackedCoverageForAnchorStop())
                    break;
                if (!ShouldContinueExploring())
                    break;

                var documentScopedSignature = BuildSourceBackedAnchorFollowupSignature(
                    new[] { documentScopedPass },
                    null);
                if (string.IsNullOrWhiteSpace(documentScopedSignature)
                    || !anchorFollowupSignatures.Add(documentScopedSignature))
                {
                    continue;
                }

                var documentScopedAccepted = await TryExecuteExplorationPassAsync(documentScopedPass).ConfigureAwait(false);
                if (documentScopedAccepted)
                {
                    accepted = true;
                    if (HasHighConfidenceSourceBackedCoverageForAnchorStop() || !ShouldContinueExploring())
                        break;

                    if (UsesSourceBackedPlanningCoverage(explorationQuery))
                    {
                        EmitRagTrace(
                            "evidence.exploration.anchor_followup.continue",
                            ("reason", "planning_doc_scope_partial"),
                            ("label", documentScopedPass.Label),
                            ("doc_id", NullIfWhiteSpace(documentScopedPass.DocId)),
                            ("doc_path", NullIfWhiteSpace(documentScopedPass.DocPath)),
                            ("candidates", currentAnalysis.CandidateCount),
                            ("score", currentAnalysis.Score),
                            ("distinct_pages", currentAnalysis.DistinctSourcePageCount));
                    }
                }

                if (anchorFollowupAttempts >= anchorFollowupRoundLimit)
                {
                    EmitRagTrace(
                        "evidence.exploration.anchor_followup.stop",
                        ("reason", "attempt_limit_reached"),
                        ("label", documentScopedPass.Label),
                        ("candidates", currentAnalysis.CandidateCount),
                        ("score", currentAnalysis.Score),
                        ("distinct_pages", currentAnalysis.DistinctSourcePageCount));
                    break;
                }
            }

            var shouldRunGlobalPass = hasNewGlobalPass
                && pass is not null
                && remainingRagCalls > 0
                && ShouldContinueExploring()
                && (!accepted
                    || (UsesSourceBackedPlanningCoverage(explorationQuery)
                        && currentAnalysis.ShouldExplore
                        && !HasHighConfidenceSourceBackedCoverageForAnchorStop()));

            if (shouldRunGlobalPass && pass is not null)
            {
                anchorFollowupSignatures.Add(globalSignature);
                accepted = await TryExecuteExplorationPassAsync(pass).ConfigureAwait(false);
            }

            return accepted;
        }

        bool HasNewSourceBackedAnchorFollowupOpportunity(
            ToolResults candidate,
            SourceBackedEvidenceSufficiency candidateAnalysis)
        {
            if (remainingRagCalls <= 0 || anchorFollowupAttempts >= anchorFollowupRoundLimit)
                return false;

            if (ShouldDeferSparseSourceBackedPlanningAnchorFollowup(candidateAnalysis, explorationQuery))
            {
                EmitRagTrace(
                    "evidence.exploration.anchor_followup.deferred",
                    ("reason", "candidate_planning_bank_still_sparse"),
                    ("candidates", candidateAnalysis.CandidateCount),
                    ("minimum_candidates", candidateAnalysis.MinimumCandidateCount),
                    ("target_slots", candidateAnalysis.TargetSlotCount),
                    ("distinct_pages", candidateAnalysis.DistinctSourcePageCount),
                    ("score", candidateAnalysis.Score),
                    ("remaining_rag_calls", remainingRagCalls));
                return false;
            }

            var documentScopedPasses = SelectNewDocumentScopedAnchorFollowupPasses(candidate);
            var pass = BuildSourceBackedRouteAnchorFollowupExplorationPass(
                candidate,
                explorationQuery,
                plan.Language);
            var globalSignature = BuildSourceBackedAnchorFollowupSignature(
                Enumerable.Empty<SourceBackedEvidenceExplorationPass>(),
                pass);
            var hasNewGlobalPass = pass is not null
                                   && pass.Queries.Length > 0
                                   && !string.IsNullOrWhiteSpace(globalSignature)
                                   && !anchorFollowupSignatures.Contains(globalSignature);
            return documentScopedPasses.Length > 0 || hasNewGlobalPass;
        }

        SourceBackedEvidenceExplorationPass[] SelectNewDocumentScopedAnchorFollowupPasses(ToolResults sourceResults)
        {
            return BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(
                    sourceResults,
                    explorationQuery,
                    plan.Language)
                .Where(pass =>
                {
                    var signature = BuildSourceBackedAnchorFollowupSignature(new[] { pass }, null);
                    return !string.IsNullOrWhiteSpace(signature)
                           && !anchorFollowupSignatures.Contains(signature);
                })
                .Take(Math.Min(
                    ResolveSourceBackedDocumentScopedAnchorFollowupLimit(explorationQuery),
                    Math.Max(1, remainingRagCalls)))
                .ToArray();
        }

        async Task<bool> TryRunLlmPlannerRoundsAsync(string phase)
        {
            var acceptedAnyPlannerPass = false;
            for (var plannerRound = 0;
                 plannerRound < MaxSourceBackedLlmEvidenceExplorationRounds
                 && ShouldContinueExploring()
                 && remainingRagCalls > 0
                 && ShouldUseLlmSourceBackedEvidencePlanner(explorationQuery, currentAnalysis);
                 plannerRound++)
            {
                attemptedLlmPlanner = true;
                onProgress?.Invoke(DeterministicAgentText.ProgressPlanRetrievalStrategy(plan.Language));
                EmitRagTrace(
                    "evidence.llm_planner.round.start",
                    ("phase", phase),
                    ("round", plannerRound + 1),
                    ("query", explorationQuery),
                    ("kind", currentAnalysis.Kind),
                    ("reason", currentAnalysis.Reason),
                    ("score", currentAnalysis.Score),
                    ("usable_hits", currentAnalysis.UsableHitCount),
                    ("candidates", currentAnalysis.CandidateCount),
                    ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                    ("target_slots", currentAnalysis.TargetSlotCount),
                    ("remaining_rag_calls", remainingRagCalls));

                var plannedPasses = await TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
                        toolResults,
                        currentAnalysis,
                        explorationQuery,
                        plan.Language,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
                llmPlannerProducedPass |= plannedPasses.Count > 0;
                EmitRagTrace(
                    "evidence.llm_planner.round.end",
                    ("phase", phase),
                    ("round", plannerRound + 1),
                    ("passes", plannedPasses.Count),
                    ("labels", plannedPasses.Select(static pass => pass.Label).ToArray()),
                    ("query_count", plannedPasses.Sum(static pass => pass.Queries.Length)),
                    ("remaining_rag_calls", remainingRagCalls));
                if (plannedPasses.Count == 0)
                    break;

                var acceptedInRound = false;

                foreach (var pass in plannedPasses.Take(Math.Min(MaxSourceBackedLlmEvidenceExplorationPasses, remainingRagCalls)))
                {
                    if (await TryExecuteExplorationPassAsync(pass, allowReservedLlmPlannerCall: true).ConfigureAwait(false))
                    {
                        acceptedAny = true;
                        acceptedAnyPlannerPass = true;
                        acceptedInRound = true;
                        var remainingPlannerRounds = Math.Max(0, MaxSourceBackedLlmEvidenceExplorationRounds - (plannerRound + 1));
                        if (ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPass(
                                currentAnalysis,
                                explorationQuery,
                                remainingPlannerRounds))
                        {
                            EmitRagTrace(
                                "evidence.exploration.anchor_followup.deferred",
                                ("reason", "next_llm_planner_round_can_target_structured_gap"),
                                ("label", pass.Label),
                                ("origin", pass.Origin),
                                ("candidates", currentAnalysis.CandidateCount),
                                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                                ("target_slots", currentAnalysis.TargetSlotCount),
                                ("remaining_planner_rounds", remainingPlannerRounds),
                                ("remaining_rag_calls", remainingRagCalls));
                        }
                        else if (await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
                        {
                            acceptedAny = true;
                            acceptedAnyPlannerPass = true;
                            acceptedInRound = true;
                        }
                    }
                }

                if (!acceptedInRound)
                    break;
            }

            return acceptedAnyPlannerPass;
        }

        MarkStructuredPlanningTargetCoverageIfReached("initial", null);

        if (ShouldPrioritizeLlmPlannerBeforeDeterministicPasses()
            && await TryRunLlmPlannerRoundsAsync("pre_deterministic").ConfigureAwait(false))
        {
            acceptedAny = true;
        }

        if (ShouldContinueExploring()
            && ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPass(currentAnalysis, explorationQuery, acceptedAny)
            && await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
            acceptedAny = true;

        if (!ShouldContinueExploring())
        {
            EmitRagTrace(
                "evidence.exploration.pass_build.skipped",
                ("label", "deterministic"),
                ("origin", "deterministic_seed"),
                ("reason", structuredPlanningTargetCoverageReached ? "structured_target_met" : "exploration_not_needed"),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("remaining_rag_calls", remainingRagCalls));
        }
        else
        {
            var deterministicPassBuildSw = Stopwatch.StartNew();
            EmitRagTrace(
                "evidence.exploration.pass_build.start",
                ("label", "deterministic"),
                ("origin", "deterministic_seed"),
                ("remaining_rag_calls", remainingRagCalls),
                ("force_broadened", forceBroadenedExploration));
            var passes = BuildSourceBackedEvidenceExplorationPasses(
                toolResults,
                explorationQuery,
                plan.Language,
                forceBroadenedExploration);
            deterministicPassBuildSw.Stop();
            EmitRagTrace(
                "evidence.exploration.pass_build.end",
                ("label", "deterministic"),
                ("origin", "deterministic_seed"),
                ("passes", passes.Count),
                ("query_count", passes.Sum(static pass => pass.Queries.Length)),
                ("elapsed_ms", deterministicPassBuildSw.ElapsedMilliseconds));
            foreach (var pass in passes.Take(MaxSourceBackedEvidenceExplorationPasses))
            {
                if (!ShouldContinueExploring())
                    break;

                if (await TryExecuteExplorationPassAsync(pass).ConfigureAwait(false))
                {
                    acceptedAny = true;
                    if (!ShouldDeferAnchorFollowupAfterAcceptedPass(pass)
                        && await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
                        acceptedAny = true;
                }
            }
        }

        if (ShouldContinueExploring()
            && ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPass(currentAnalysis, explorationQuery, acceptedAny)
            && await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
            acceptedAny = true;

        if (!attemptedLlmPlanner
            && ShouldContinueExploring()
            && remainingRagCalls > 0
            && ShouldUseLlmSourceBackedEvidencePlanner(explorationQuery, currentAnalysis)
            && await TryRunLlmPlannerRoundsAsync("post_deterministic").ConfigureAwait(false))
        {
            acceptedAny = true;
        }

        if (ShouldContinueExploring()
            && remainingRagCalls > 0
            && reservedDeterministicPasses.Count > 0
            && (!attemptedLlmPlanner || !llmPlannerProducedPass || ShouldContinueExploring()))
        {
            foreach (var pass in reservedDeterministicPasses.Take(remainingRagCalls).ToArray())
            {
                if (await TryExecuteExplorationPassAsync(pass, allowReservedLlmPlannerCall: true).ConfigureAwait(false))
                {
                    acceptedAny = true;
                    if (!ShouldDeferAnchorFollowupAfterAcceptedPass(pass)
                        && await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
                        acceptedAny = true;
                }
            }
        }

        if (acceptedAny
            && ShouldContinueExploring()
            && remainingRagCalls > 0
            && await TryExecuteAnchorFollowupPassAsync().ConfigureAwait(false))
        {
            acceptedAny = true;
        }

        EmitSourceBackedResearchInventoryTrace(
            toolResults,
            explorationQuery,
            plan.Language,
            currentAnalysis,
            ragCallBudget,
            remainingRagCalls);

        EmitRagTrace(
            "evidence.exploration.end",
            ("accepted_any", acceptedAny),
            ("remaining_rag_calls", remainingRagCalls),
            ("final_kind", currentAnalysis.Kind),
            ("final_reason", currentAnalysis.Reason),
            ("final_score", currentAnalysis.Score),
            ("final_usable_hits", currentAnalysis.UsableHitCount),
            ("final_candidates", currentAnalysis.CandidateCount),
            ("final_distinct_docs", currentAnalysis.DistinctDocumentCount),
            ("final_distinct_pages", currentAnalysis.DistinctSourcePageCount));
        return acceptedAny;
    }

    private static (string? CategoryScope, bool ReusedFromCurrentTurnInference) ResolveSourceBackedExplorationPassCategoryScope(
        string? resolvedPassCategoryScope,
        string? currentCategoryScope,
        string? currentTurnInferredCategoryScope,
        string? passOrigin,
        bool passHasDocumentScope)
    {
        var resolved = NormalizeCategoryPathArg(resolvedPassCategoryScope);
        var current = NormalizeCategoryPathArg(currentCategoryScope);
        var inferred = !passHasDocumentScope
                       && string.Equals(passOrigin, "deterministic_seed", StringComparison.OrdinalIgnoreCase)
            ? NormalizeCategoryPathArg(currentTurnInferredCategoryScope)
            : null;
        var selected = resolved ?? current ?? inferred;
        var reused = string.IsNullOrWhiteSpace(resolved)
                     && string.IsNullOrWhiteSpace(current)
                     && !string.IsNullOrWhiteSpace(inferred)
                     && string.Equals(selected, inferred, StringComparison.OrdinalIgnoreCase);
        return (selected, reused);
    }

    private static bool ShouldTrustSourceBackedExplorationPassCategoryScope(
        string? passOrigin,
        bool passHasDocumentScope,
        string? resolvedPassCategoryScope,
        string? passCategoryScope,
        bool categoryScopeTrustedByCurrentEvidence,
        bool passCategoryScopeReusedFromInference)
    {
        if (passHasDocumentScope || string.IsNullOrWhiteSpace(passCategoryScope))
            return false;

        if (categoryScopeTrustedByCurrentEvidence || passCategoryScopeReusedFromInference)
            return true;

        return string.Equals(passOrigin, "llm_planner", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(resolvedPassCategoryScope)
               && string.Equals(
                   NormalizeCategoryPathArg(passCategoryScope),
                   NormalizeCategoryPathArg(resolvedPassCategoryScope),
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TrySeedSourceBackedDocumentNavigationFromEvidenceAsync(
        ToolResults toolResults,
        string language,
        CancellationToken ct,
        Action<string>? onProgress,
        ISet<string>? seededNavigationScopes,
        int maxDocuments = 3)
    {
        var seeds = SelectSourceBackedDocumentNavigationSeeds(toolResults, maxDocuments);
        if (seeds.Count == 0)
            return false;

        var acceptedAny = false;
        foreach (var seed in seeds)
        {
            var scopeKey = BuildSourceBackedDocumentNavigationSeedScopeKey(seed);
            if (string.IsNullOrWhiteSpace(scopeKey))
                continue;
            if (seededNavigationScopes is not null && !seededNavigationScopes.Add(scopeKey))
                continue;

            var args = string.IsNullOrWhiteSpace(seed.DocId)
                ? CreateJsonArgs(new
                {
                    docPath = seed.DocPath,
                    limit = 180,
                    offset = 0
                })
                : CreateJsonArgs(new
                {
                    docRef = seed.DocId,
                    limit = 180,
                    offset = 0
                });

            var sw = Stopwatch.StartNew();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressInspectDocumentStructure(language));
                var navigation = await ExecDocumentsNavigationAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, true));
                if (!DocumentNavigationHasItems(navigation))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.navigation",
                    Result = navigation,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("documents.navigation", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.navigation");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, false));
            }
        }

        return acceptedAny;
    }

    private static bool ShouldRespectLlmRouterGeneralWithoutTools(RouterPlan? plan)
        => plan is not null
           && plan.Origin == RouterPlanOrigin.Llm
           && string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
           && !plan.NeedClarification
           && (plan.ToolCalls is null || plan.ToolCalls.Count == 0);

    private static bool DocumentNavigationHasItems(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
           && items.GetArrayLength() > 0;

    private static int ResolveSourceBackedEvidenceExplorationRagCallBudget(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration
            || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage)
            || currentAnalysis.Kind is "planning" or "broad")
        {
            var budget = MaxBroadExplorationRagToolCalls;
            if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                || currentAnalysis.Kind == "planning")
            {
                var targetSlots = Math.Max(
                    currentAnalysis.TargetSlotCount,
                    ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
                var minimumCandidates = Math.Max(
                    currentAnalysis.MinimumCandidateCount,
                    ResolveMinimumBroadSourceBackedSynthesisHitCount(effectiveUserMessage));
                if (targetSlots >= 10 || minimumCandidates >= 6)
                    budget = Math.Max(
                        budget,
                        Math.Min(48, Math.Max(targetSlots, minimumCandidates) + 18));
            }

            return forceBroadenedExploration
                ? Math.Min(48, Math.Max(budget, MaxBroadExplorationRagToolCalls + 12))
                : budget;
        }

        return MaxRagToolCalls;
    }

    private static bool ShouldSeedSourceBackedNavigationStructure(
        string effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration)
            return true;

        return ShouldAllowSourceBackedBroadResearchPass(
            effectiveUserMessage,
            currentAnalysis,
            forceBroadenedExploration);
    }

    private static bool LooksLikeSourceBackedBroadResearchRequest(string? effectiveUserMessage)
        => LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
           || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
           || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
           || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
           || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
           || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage);

    private static bool ShouldUseResearchSurfacesForBroadRagRequest(string? effectiveUserMessage)
        => LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage)
           || IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage);

    private static bool ShouldAllowSourceBackedBroadResearchPass(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration)
            return true;
        if (!LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage))
            return false;
        if (currentAnalysis.IsSufficient)
            return false;

        if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            || currentAnalysis.Kind == "planning")
        {
            return currentAnalysis.ShouldExplore
                   || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount;
        }

        return currentAnalysis.ShouldExplore
               || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount
               || currentAnalysis.DistinctSourcePageCount < Math.Min(3, Math.Max(1, currentAnalysis.MinimumCandidateCount));
    }

    private async Task<bool> TrySeedSourceBackedNavigationStructureAsync(
        ToolResults toolResults,
        string? categoryScope,
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onProgress,
        ISet<string>? seededNavigationScopes = null)
    {
        var scopeKey = string.IsNullOrWhiteSpace(categoryScope)
            ? "__global__"
            : NormalizeLooseLookup(categoryScope);
        if (seededNavigationScopes is not null && !seededNavigationScopes.Add(scopeKey))
            return false;

        var hasExistingTree = string.IsNullOrWhiteSpace(categoryScope)
                              && toolResults.Items.Any(static item => item.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(item.Error));

        var acceptedAny = false;
        var args = string.IsNullOrWhiteSpace(categoryScope)
            ? CreateJsonArgs(new { depth = 2, format = "json" })
            : CreateJsonArgs(new { path = categoryScope, depth = 3, format = "json" });

        var sw = new Stopwatch();
        if (!hasExistingTree)
        {
            sw.Start();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressInspectDocumentStructure(language));
                var result = await ExecDocumentsTreeAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.tree",
                    Result = result,
                    DurationMs = sw.ElapsedMilliseconds
                });
                _lastToolDurations.Add(("documents.tree", sw.ElapsedMilliseconds, true));
                if (!_mem.LastToolNames.Contains("documents.tree", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.tree");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.tree", sw.ElapsedMilliseconds, false));
            }
        }

        var orientationQueryLimit = UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                                    || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
                                    || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            ? Math.Max(MaxSourceBackedNavigationOrientationQueries, 8)
            : MaxSourceBackedNavigationOrientationQueries;
        foreach (var navigationQuery in BuildSourceBackedNavigationOrientationQueries(effectiveUserMessage, categoryScope)
                     .Take(orientationQueryLimit))
        {
            var navigationArgs = string.IsNullOrWhiteSpace(categoryScope)
                ? CreateJsonArgs(new
                {
                    q = navigationQuery,
                    limit = 120,
                    offset = 0
                })
                : CreateJsonArgs(new
                {
                    path = categoryScope,
                    q = navigationQuery,
                    limit = 120,
                    offset = 0
                });

            sw.Restart();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));
                var navigation = await ExecDocumentsNavigationAsync(navigationArgs, ct).ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, true));
                if (!DocumentNavigationHasItems(navigation))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.navigation",
                    Result = navigation,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("documents.navigation", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("documents.navigation");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("documents.navigation", sw.ElapsedMilliseconds, false));
            }
        }

        var summaryOrientationQueryLimit = UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                                           || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
                                           || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            ? Math.Max(MaxSourceBackedSummaryOrientationQueries, 8)
            : MaxSourceBackedSummaryOrientationQueries;
        foreach (var summaryQuery in BuildSourceBackedSummaryOrientationQueries(effectiveUserMessage, categoryScope)
                     .Take(summaryOrientationQueryLimit))
        {
            sw.Restart();
            try
            {
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));
                var summary = await ExecSummarySearchAsync(
                        CreateJsonArgs(new
                        {
                            q = summaryQuery,
                            limit = 12,
                            offset = 0
                        }),
                        ct)
                    .ConfigureAwait(false);
                sw.Stop();
                _lastToolDurations.Add(("summary.search", sw.ElapsedMilliseconds, true));
                if (!SummarySearchHasItems(summary))
                    continue;

                toolResults.Items.Add(new ToolResults.Item
                {
                    ToolName = "summary.search",
                    Result = summary,
                    DurationMs = sw.ElapsedMilliseconds
                });
                if (!_mem.LastToolNames.Contains("summary.search", StringComparer.OrdinalIgnoreCase))
                    _mem.LastToolNames.Add("summary.search");
                acceptedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                sw.Stop();
                _lastToolDurations.Add(("summary.search", sw.ElapsedMilliseconds, false));
            }
        }

        return acceptedAny;
    }

    private static string[] BuildSourceBackedNavigationOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }

    private static string[] BuildSourceBackedSummaryOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }

    private static bool SummarySearchHasItems(JsonElement result)
        => result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
           && items.GetArrayLength() > 0;

    private async Task<IReadOnlyList<SourceBackedEvidenceExplorationPass>> TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onProgress = null)
    {
        var alreadyTriedQueries = BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(toolResults, effectiveUserMessage, language);
        var categoryHints = BuildSourceBackedLlmCategoryHintsForPrompt(
            effectiveUserMessage,
            MaxSourceBackedLlmEvidencePlannerCategoryHints);
        var system = BuildSourceBackedLlmEvidenceExplorationSystemPrompt(language);
        var user = BuildSourceBackedLlmEvidenceExplorationUserPrompt(
            toolResults,
            currentAnalysis,
            effectiveUserMessage,
            language,
            alreadyTriedQueries,
            categoryHints);

        var sw = Stopwatch.StartNew();
        try
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressPlanRetrievalStrategy(language));
            EmitRagTrace(
                "evidence.llm_planner.start",
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("score", currentAnalysis.Score),
                ("usable_hits", currentAnalysis.UsableHitCount),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("already_tried_queries", alreadyTriedQueries.Count),
                ("system_chars", system.Length),
                ("user_chars", user.Length),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs));
            using var plannerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plannerTimeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    plannerTimeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, true));
            var categoryDecision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(raw);
            var parsedPasses = ParseSourceBackedLlmEvidenceExplorationPasses(raw, alreadyTriedQueries);
            var passes = FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
                parsedPasses,
                effectiveUserMessage,
                language,
                categoryDecision.CategoryScope,
                out var rejectedPassLabels,
                out var rejectedQueries);
            var filteredPassCount = passes.Count;
            passes = ApplyResolvedSourceBackedLlmCategoryScopeDecision(
                passes,
                categoryDecision,
                "planner_output",
                out var addedScopeOnlyPass);
            if (ShouldRunLlmSourceBackedCategoryScopeAdjudication(
                    passes,
                    currentAnalysis,
                    effectiveUserMessage,
                    language,
                    categoryHints))
            {
                var adjudicatedCategoryDecision = await TryAdjudicateSourceBackedLlmCategoryScopeAsync(
                        toolResults,
                        currentAnalysis,
                        passes,
                        effectiveUserMessage,
                        language,
                        categoryHints,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
                passes = ApplyResolvedSourceBackedLlmCategoryScopeDecision(
                    passes,
                    adjudicatedCategoryDecision,
                    "adjudication",
                    out var addedAdjudicatedScopeOnlyPass);
                addedScopeOnlyPass |= addedAdjudicatedScopeOnlyPass;
            }
            if (rejectedPassLabels.Length > 0)
            {
                EmitRagTrace(
                    "evidence.llm_planner.quality_filter",
                    ("reason", "structured_axis_label_only_queries"),
                    ("parsed_passes", parsedPasses.Count),
                    ("kept_passes", filteredPassCount),
                    ("passes_after_scope", passes.Count),
                    ("scope_only_pass_added", addedScopeOnlyPass),
                    ("rejected_passes", rejectedPassLabels.Length),
                    ("rejected_labels", rejectedPassLabels),
                    ("rejected_query_samples", rejectedQueries.Take(8).ToArray()));
            }
            EmitRagTrace(
                "evidence.llm_planner.end",
                ("accepted", passes.Count > 0),
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("raw_chars", raw?.Length ?? 0),
                ("parsed_passes", parsedPasses.Count),
                ("passes", passes.Count),
                ("labels", passes.Select(static pass => pass.Label).ToArray()),
                ("query_count", passes.Sum(static pass => pass.Queries.Length)),
                ("category_scopes", passes
                    .Select(static pass => pass.CategoryScope)
                    .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
            return passes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_planner.timeout",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_planner.error",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
    }

    private IReadOnlyList<SourceBackedEvidenceExplorationPass> ApplyResolvedSourceBackedLlmCategoryScopeDecision(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> passes,
        SourceBackedLlmCategoryScopeDecision decision,
        string origin,
        out bool addedScopeOnlyPass)
    {
        addedScopeOnlyPass = false;
        if (string.IsNullOrWhiteSpace(decision.CategoryScope)
            && string.IsNullOrWhiteSpace(decision.Decision)
            && string.IsNullOrWhiteSpace(decision.Confidence)
            && string.IsNullOrWhiteSpace(decision.Reason))
        {
            return passes;
        }

        var resolvedCategoryScope = ResolveLlmPlannedRagCategoryScope(decision.CategoryScope);
        EmitRagTrace(
            "evidence.llm_planner.category_decision",
            ("origin", origin),
            ("decision", decision.Decision),
            ("confidence", decision.Confidence),
            ("raw_category", decision.CategoryScope),
            ("resolved_category", resolvedCategoryScope),
            ("accepted", !string.IsNullOrWhiteSpace(resolvedCategoryScope)),
            ("reason", decision.Reason));
        if (string.IsNullOrWhiteSpace(resolvedCategoryScope))
            return passes;

        var updated = ApplySourceBackedLlmCategoryScopeDecisionOrCreateScopeOnlyPass(
            passes,
            resolvedCategoryScope,
            "llm_planner",
            out var updatedPassCount,
            out addedScopeOnlyPass);
        if (updatedPassCount > 0 || addedScopeOnlyPass)
        {
            EmitRagTrace(
                "evidence.llm_planner.category_scope.applied",
                ("origin", origin),
                ("category", resolvedCategoryScope),
                ("updated_passes", updatedPassCount),
                ("scope_only_pass_added", addedScopeOnlyPass),
                ("labels", updated.Select(static pass => pass.Label).ToArray()));
        }

        return updated;
    }

    private async Task<SourceBackedLlmCategoryScopeDecision> TryAdjudicateSourceBackedLlmCategoryScopeAsync(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses,
        string effectiveUserMessage,
        string language,
        string categoryHints,
        CancellationToken ct,
        Action<string>? onProgress = null)
    {
        if (!HasSourceBackedLlmCategoryHints(categoryHints))
            return new SourceBackedLlmCategoryScopeDecision(null, null, null, null);

        var system = BuildSourceBackedLlmCategoryScopeSystemPrompt(language);
        var user = BuildSourceBackedLlmCategoryScopeUserPrompt(
            toolResults,
            currentAnalysis,
            plannedPasses,
            effectiveUserMessage,
            language,
            categoryHints);

        var sw = Stopwatch.StartNew();
        try
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressPlanRetrievalStrategy(language));
            EmitRagTrace(
                "evidence.llm_category_scope.start",
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason),
                ("score", currentAnalysis.Score),
                ("usable_hits", currentAnalysis.UsableHitCount),
                ("candidates", currentAnalysis.CandidateCount),
                ("minimum_candidates", currentAnalysis.MinimumCandidateCount),
                ("target_slots", currentAnalysis.TargetSlotCount),
                ("pass_count", plannedPasses.Count),
                ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length),
                ("system_chars", system.Length),
                ("user_chars", user.Length),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs));
            using var plannerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plannerTimeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    plannerTimeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, true));
            var decision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(raw);
            EmitRagTrace(
                "evidence.llm_category_scope.end",
                ("accepted", !string.IsNullOrWhiteSpace(decision.CategoryScope)),
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("raw_chars", raw?.Length ?? 0),
                ("decision", decision.Decision),
                ("confidence", decision.Confidence),
                ("category", decision.CategoryScope),
                ("reason", decision.Reason));
            return decision;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_category_scope.timeout",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return new SourceBackedLlmCategoryScopeDecision(null, "timeout", null, "category_scope_llm_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.category_scope_plan", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "evidence.llm_category_scope.error",
                ("elapsed_ms", sw.ElapsedMilliseconds),
                ("query", effectiveUserMessage),
                ("kind", currentAnalysis.Kind),
                ("reason", currentAnalysis.Reason));
            return new SourceBackedLlmCategoryScopeDecision(null, "error", null, "category_scope_llm_error");
        }
    }

    private static bool ShouldUseLlmSourceBackedEvidencePlanner(
        string effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis)
    {
        var canPlanWithoutInitialEvidence =
            LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || ShouldOfferBroadenedSourceSearch(effectiveUserMessage);

        if (string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase)
            || canPlanWithoutInitialEvidence)
            return true;

        if (currentAnalysis.UsableHitCount <= 0)
            return false;

        return LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
               || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeBroadSynthesisRequestShape(effectiveUserMessage)
               || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
               || (currentAnalysis.UsableHitCount > 0 && LooksLikeDocumentaryContentRequest(effectiveUserMessage))
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage);
    }

    private static bool ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPass(
        SourceBackedEvidenceSufficiency currentAnalysis,
        string? effectiveUserMessage,
        int remainingPlannerRounds)
    {
        if (remainingPlannerRounds <= 0)
            return false;

        if (!UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            || !string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(currentAnalysis, effectiveUserMessage))
            return false;

        return ShouldUseLlmSourceBackedEvidencePlanner(effectiveUserMessage ?? string.Empty, currentAnalysis);
    }

    private static string BuildSourceBackedLlmEvidenceExplorationSystemPrompt(string language)
        => $@"
You are SAAIA's retrieval strategist, not the final answer writer.
Target user language: {NormalizeLanguageCode(language)}.

Choose the next retrieval path. Do not answer the user.
The tools you can orchestrate are rag.search, rag.multi_search and documents.context.
Use documents.context only when CURRENT_SOURCE_LEADS or STRUCTURE_HINTS already provide a docId, docPath, docRef, chunkId or page range worth reading around. It is for inspecting indexed chunk text like scrolling a document, not for broad discovery without an anchor.
Use REQUEST_SHAPE as the coverage target, especially targetSlots and minimumCandidates.
Use CATEGORY_HINTS as the only allowed scope values: copy an exact category/path/ref from a hint, but judge fit semantically.
A broad category can fit when it naturally contains the user's requested content, even if the label is not a literal query word.
When the user asks to compose a plan, schedule, list or recommendation from source items, choose the category that contains those source items; do not require a category named after the final format.
Treat STRUCTURE_HINTS as maps to concrete pages.
Use WORKING_NOTES as a bounded research notebook: it can guide pivots, repeats to avoid and promising query families, but it is not source evidence.
Prefer short complementary queries from intent, labels, headings, paths and page anchors.
For structured requests with several requested slots/types, cover every requested slot/type at least once before repeating or refining a single slot/type.
For structured plans, infer requested axes from REQUEST_SHAPE and USER_REQUEST; do not rely on domain-specific hardcoded slot routes.
If a clue has docId/docPath/pageStart/pageEnd, scope the pass there to retrieve concrete content.
If evidence is weak, propose the next pass yourself. Do not ask the user to broaden the search.
Avoid duplicates already tried. Keep queries short and domain-neutral.

Return strict JSON only:
{{
  ""categoryDecision"": {{
    ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
    ""decision"": ""use_scope or none"",
    ""confidence"": ""high, medium or low"",
    ""reason"": ""short reason for the scope decision""
  }},
  ""passes"": [
    {{
      ""label"": ""llm_strategy"",
      ""purpose"": ""why this pass may improve coverage"",
      ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
      ""docId"": ""optional document id from CURRENT_SOURCE_LEADS or STRUCTURE_HINTS, otherwise null"",
      ""docPath"": ""optional document path from CURRENT_SOURCE_LEADS or STRUCTURE_HINTS, otherwise null"",
      ""pageStart"": ""optional first page number from a source clue, otherwise null"",
      ""pageEnd"": ""optional last page number from a source clue, otherwise null"",
      ""queries"": [""short query 1"", ""short query 2""]
    }}
  ],
  ""toolCalls"": [
    {{
      ""name"": ""documents.context"",
      ""args"": {{""docId"": null, ""docPath"": null, ""chunkId"": null, ""pageStart"": null, ""pageEnd"": null, ""before"": 2, ""after"": 4, ""limit"": 12}}
    }}
  ]
}}";

    private string BuildSourceBackedLlmEvidenceExplorationUserPrompt(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> alreadyTriedQueries,
        string? categoryHintsOverride = null)
    {
        var deterministicSeeds = BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(effectiveUserMessage, language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidencePlannerDeterministicSeeds)
            .ToArray();
        var sourceLeads = BuildSourceBackedLlmEvidenceSnapshotForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerSourceLeadLines,
            cueMaxLength: 70,
            maxCardHints: 1,
            maxProfileHints: 1,
            retrievalQueryMaxLength: 70);
        var structureHints = BuildSourceBackedStructureHintsForPrompt(
            toolResults,
            _mem.LastSourcesUsed,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerStructureHintLines);
        var categoryHints = categoryHintsOverride
            ?? BuildSourceBackedLlmCategoryHintsForPrompt(
                effectiveUserMessage,
                MaxSourceBackedLlmEvidencePlannerCategoryHints);
        var coverageTrace = BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            MaxSourceBackedLlmEvidencePlannerCoverageTraceLines);
        var weakRetrievalAxes = BuildSourceBackedLlmWeakRetrievalAxesForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            alreadyTriedQueries,
            maxLines: Math.Max(4, MaxSourceBackedLlmEvidencePlannerCoverageTraceLines / 2));
        var workingNotes = BuildSourceBackedLlmWorkingNotesForPrompt(
            effectiveUserMessage,
            language,
            maxLines: MaxSourceBackedLlmEvidencePlannerWorkingNoteLines);

        return $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

SUFFICIENCY:
- kind: {currentAnalysis.Kind}
- reason: {currentAnalysis.Reason}
- score: {currentAnalysis.Score}
- usableHits: {currentAnalysis.UsableHitCount}
- distinctSourcePages: {currentAnalysis.DistinctSourcePageCount}
- navigationAnchors: {currentAnalysis.NavigationAnchorCount}
- candidates: {currentAnalysis.CandidateCount}/{currentAnalysis.MinimumCandidateCount}
- targetSlots: {currentAnalysis.TargetSlotCount}
- hasRequiredAnchor: {currentAnalysis.HasRequiredAnchor}

PLANNING_COVERAGE_TRACE:
{coverageTrace}

WEAK_OR_UNDERCOVERED_AXES:
{weakRetrievalAxes}

WORKING_NOTES:
{workingNotes}

CURRENT_SOURCE_LEADS:
{sourceLeads}

STRUCTURE_HINTS:
{structureHints}

CATEGORY_HINTS:
{categoryHints}

DETERMINISTIC_QUERY_SEEDS:
{FormatPromptList(deterministicSeeds, MaxSourceBackedLlmEvidencePlannerDeterministicSeeds, maxItemLength: 90)}

ALREADY_TRIED_QUERIES:
{FormatPromptList(alreadyTriedQueries, MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries, maxItemLength: 90)}

OUTPUT_RULES:
- Always fill categoryDecision first. If CATEGORY_HINTS contains one category/path/ref that is a clear semantic container for USER_REQUEST, choose it; otherwise set categoryScope null and decision ""none"".
- Do not reject a category only because it is broader than the specific task, or because its label is not repeated verbatim in USER_REQUEST.
- For plans, schedules, lists or recommendations assembled from source items, category fit is about where the source items live, not whether the category label names the final format.
- If exactly one hinted category is semantically plausible and the others are clearly unrelated, prefer using that categoryScope with medium or high confidence.
- When categoryDecision uses a scope, copy that exact same scope into every non-document-scoped pass. Do not invent categories outside CATEGORY_HINTS.
- Return at most {MaxSourceBackedLlmEvidenceExplorationQueries} queries total.
- Prefer 3 to 8 strong complementary queries.
- For broad plans, prioritize distinct concrete candidates until REQUEST_SHAPE minimumCandidates/targetSlots are plausible.
- For structured plans, treat days/rows/columns as placement axes, not standalone retrieval targets. Search option kinds, slot kinds, constraints, candidate inventory and concrete labels first.
- For structured plans, use PLANNING_COVERAGE_TRACE to identify under-covered slots or criteria. `*_title_pool` means the candidate itself names that slot/type; `*_route_fit_pool` means a candidate was found by a retrieval pass that targeted that slot/type and has no visible conflict. If both are weak or assigned slots are missing, pivot the next pass toward that gap instead of repeating already-covered slots.
- If WEAK_OR_UNDERCOVERED_AXES is not none, dedicate the next pass to those axes first. Use its user_terms and suggested_pivots, and avoid repeating failed_or_low_hit_queries with only cosmetic changes.
- Use WORKING_NOTES to avoid repeated dead ends and to continue query families that previously improved concrete candidates. Do not treat WORKING_NOTES as source evidence; retrieve current concrete hits before relying on an item.
- When an explicit axis has already been searched with low/zero hits, diversify the lexical family: use user-provided aliases, source-language variants, broader/narrower option-kind words, or concrete source labels from CURRENT_SOURCE_LEADS. Do not keep repeating the same failed term.
- For structured plans with several requested slots/criteria/phases, cover those requested types broadly before adding variants around one type. Do not over-focus one slot unless CURRENT_SOURCE_LEADS proves it is the only missing part.
- Avoid decorative variants of the same broad noun such as details, ideas, ideal examples, suggestions or menus unless paired with a requested slot/type, a concrete source label, a constraint or a candidate name.
- Do not use examples/ideas/suggestions/menus as filler words. A query like ""generic slot examples"" is weak; prefer the slot/type itself, a source/category label, a document/page clue, or a concrete candidate name.
- Do not create one query per visible day/row/column when those labels are only placement axes. Query each useful option kind, constraint, source label or concrete candidate once; the writer will place supported candidates into the visible structure later.
- Use discovered navigation/profile labels only to reach real content pages.
- If a category is uncertain or only weakly hinted, keep categoryScope null and broaden with semantic queries.
- For pairing/recommendation requests, separate option kinds from target anchors.
- No UI prose, no explanations outside JSON, no source excerpts, no final answer text.";
    }

    private string BuildSourceBackedLlmWorkingNotesForPrompt(
        string effectiveUserMessage,
        string language,
        int maxLines)
    {
        var notes = _mem.ResearchWorkingNotes;
        if (notes.Count == 0)
            return "none";

        var topicKey = BuildSourceBackedResearchTopicKey(effectiveUserMessage, language);
        var shapeKey = BuildSourceBackedResearchShapeKey(effectiveUserMessage);
        var relevant = notes
            .Where(note =>
                string.Equals(note.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(note.RequestShape, shapeKey, StringComparison.OrdinalIgnoreCase)
                    && SharesSourceBackedResearchTopicTerms(note.TopicKey, topicKey)))
            .OrderByDescending(static note => note.CreatedAtUtc)
            .Take(Math.Max(1, maxLines))
            .Select(FormatSourceBackedResearchWorkingNoteForPrompt)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return relevant.Length == 0
            ? "none"
            : FormatPromptList(relevant, maxLines, maxItemLength: 360);
    }

    private static string FormatSourceBackedResearchWorkingNoteForPrompt(ToolMemory.ResearchWorkingNote note)
    {
        var queries = string.Join("; ", note.Queries.Take(4).Select(static query => TruncateForPrompt(query, 90)));
        if (string.IsNullOrWhiteSpace(queries))
            return string.Empty;

        var scopeParts = new[]
        {
            string.IsNullOrWhiteSpace(note.CategoryScope) ? null : $"category={note.CategoryScope}",
            string.IsNullOrWhiteSpace(note.DocPath) ? null : $"doc={note.DocPath}",
            note.PageStart is null ? null : $"pageStart={note.PageStart}",
            note.PageEnd is null ? null : $"pageEnd={note.PageEnd}"
        }.Where(static part => !string.IsNullOrWhiteSpace(part));
        var scope = string.Join(",", scopeParts);
        if (string.IsNullOrWhiteSpace(scope))
            scope = "none";

        var gainParts = new[]
        {
            note.CandidateDelta is null ? null : $"candidates={FormatSignedDelta(note.CandidateDelta.Value)}",
            note.DistinctPageDelta is null ? null : $"pages={FormatSignedDelta(note.DistinctPageDelta.Value)}",
            note.UsableHitDelta is null ? null : $"hits={FormatSignedDelta(note.UsableHitDelta.Value)}"
        }.Where(static part => !string.IsNullOrWhiteSpace(part));
        var gain = string.Join(",", gainParts);
        if (string.IsNullOrWhiteSpace(gain))
            gain = "unknown";

        var reason = note.Accepted
            ? note.ReasonAfter ?? note.ReasonBefore
            : note.RejectReason ?? note.ReasonAfter ?? note.ReasonBefore;
        var guidance = ResolveSourceBackedResearchWorkingNoteGuidance(note);

        return "outcome="
               + FormatPlanningTraceValue(note.Outcome)
               + "|label=" + FormatPlanningTraceValue(note.Label)
               + "|origin=" + FormatPlanningTraceValue(note.Origin)
               + "|queries=" + FormatPlanningTraceValue(queries)
               + "|scope=" + FormatPlanningTraceValue(scope)
               + "|gain=" + FormatPlanningTraceValue(gain)
               + "|reason=" + FormatPlanningTraceValue(reason)
               + "|guidance=" + guidance;
    }

    private static string ResolveSourceBackedResearchWorkingNoteGuidance(ToolMemory.ResearchWorkingNote note)
    {
        if (note.Accepted)
            return "continue_related_pivot_with_new_facets";

        return note.Outcome switch
        {
            "rejected_no_hits" => "avoid_repeat_without_new_terms_or_scope",
            "rejected_no_gain" => "change_axis_scope_or_concrete_label_before_retrying",
            "rejected_regression" => "do_not_reuse_as_primary_route",
            "error" => "retry_only_if_still_necessary_with_narrower_scope",
            _ => "treat_as_low_priority_unless_new_evidence_requires_it"
        };
    }

    private static string FormatSignedDelta(int value)
        => value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static bool SharesSourceBackedResearchTopicTerms(string? left, string? right)
    {
        var leftTerms = ExtractSourceBackedResearchTopicKeyTerms(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTerms.Count == 0)
            return false;

        foreach (var term in ExtractSourceBackedResearchTopicKeyTerms(right))
        {
            if (leftTerms.Contains(term))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractSourceBackedResearchTopicKeyTerms(string? topicKey)
    {
        if (string.IsNullOrWhiteSpace(topicKey))
            yield break;

        var parts = topicKey.Split('|');
        if (parts.Length == 0)
            yield break;

        foreach (var term in parts[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(term))
                yield return term;
        }
    }

    private static string BuildSourceBackedResearchTopicKey(string? effectiveUserMessage, string? language)
    {
        var terms = ExtractSourceBackedResearchTopicTerms(effectiveUserMessage)
            .Take(12)
            .ToArray();
        if (terms.Length == 0)
            return string.Empty;

        return string.Join(
            "|",
            NormalizeLanguageCode(language),
            BuildSourceBackedResearchShapeKey(effectiveUserMessage),
            string.Join(" ", terms));
    }

    private static string BuildSourceBackedResearchShapeKey(string? effectiveUserMessage)
    {
        var message = effectiveUserMessage ?? string.Empty;
        var flags = new List<string>();
        if (LooksLikeAnyDocumentaryPlanningRequest(message))
            flags.Add("planning");
        if (LooksLikeGenericCollectionOrListRequest(message))
            flags.Add("collection");
        if (LooksLikeMultipleCandidateSynthesisRequest(message))
            flags.Add("multi_candidate");
        if (LooksLikeSoftChoiceRecommendationRequest(message))
            flags.Add("recommendation");
        if (LooksLikeSourceBackedPairingRecommendationRequest(message))
            flags.Add("pairing");
        if (LooksLikeBroadSourceBackedCompositionRequest(message))
            flags.Add("composition");
        if (LooksLikeUserNeedsSynthesizedDecisionOrPlan(message))
            flags.Add("synthesis");
        if (DetectRequestedPlanningSlotAxisLabels(message, "fr").Count > 0
            || DetectRequestedPlanningSlotAxisLabels(message, "en").Count > 0)
        {
            flags.Add("slot_axes");
        }
        if (DetectRequestedDayAxisLabels(message, "fr").Count > 0
            || DetectRequestedDayAxisLabels(message, "en").Count > 0)
        {
            flags.Add("day_axes");
        }

        return flags.Count == 0
            ? "targeted"
            : string.Join(",", flags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractSourceBackedResearchTopicTerms(string? effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(normalized, @"[a-z0-9]{3,}", RegexOptions.CultureInvariant))
        {
            var term = match.Value;
            if (IsSourceBackedResearchTopicStopword(term))
                continue;

            if (seen.Add(term))
                yield return term;
        }
    }

    private static bool IsSourceBackedResearchTopicStopword(string term)
        => term is
            "avec" or "sans" or "pour" or "dans" or "que" or "qui" or "quoi" or "dont" or "des" or "les" or "une" or "sur"
            or "this" or "that" or "with" or "from" or "into" or "about" or "please" or "need" or "want"
            or "besoin" or "fasse" or "fasses" or "mettre" or "mets" or "chaque" or "seulement" or "vraiment"
            or "clear" or "clair" or "claire" or "format" or "utile" or "utiles" or "source" or "sources"
            or "lundi" or "mardi" or "mercredi" or "jeudi" or "vendredi" or "samedi" or "dimanche"
            or "monday" or "tuesday" or "wednesday" or "thursday" or "friday" or "saturday" or "sunday";

    private static string BuildSourceBackedLlmWeakRetrievalAxesForPrompt(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> alreadyTriedQueries,
        int maxLines)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            && !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
        {
            return "none";
        }

        language = NormalizeLanguageCode(language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        if (slotAxis.Count == 0)
            return "none";

        var targetSlots = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(
            effectiveUserMessage,
            targetSlots,
            hasStructuredAxes: DetectRequestedDayAxisLabels(effectiveUserMessage, language).Count > 0);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var poolSize = strictStructuredPlanning
            ? Math.Min(
                ResolveSourceBackedPlanningCandidatePoolSize(effectiveUserMessage, Math.Max(targetSlots, minimumCandidates)),
                Math.Max(32, targetSlots))
            : Math.Max(20, targetSlots);
        var candidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                effectiveUserMessage,
                poolSize,
                language)
            .ToArray();
        var hits = EnumerateRagHitSummaries(toolResults).ToArray();
        var queryRuns = EnumerateSourceBackedRetrievalQueryRuns(toolResults).ToArray();
        var triedQueries = alreadyTriedQueries
            .Concat(queryRuns.Select(static run => run.Query))
            .Concat(hits.Select(static hit => hit.RetrievalQuery ?? string.Empty))
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedUser = NormalizeLexicalLookup(effectiveUserMessage);
        var lines = new List<string>();

        for (var axisIndex = 0; axisIndex < slotAxis.Count; axisIndex++)
        {
            var axis = slotAxis[axisIndex];
            var axisTerms = BuildStructuredAxisPromptTerms(axis, effectiveUserMessage)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (axisTerms.Length == 0)
                continue;

            var axisTriedQueries = triedQueries
                .Where(query => QueryMentionsAnyStructuredAxisTerm(query, axisTerms))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            var axisRuns = queryRuns
                .Where(run => QueryMentionsAnyStructuredAxisTerm(run.Query, axisTerms))
                .ToArray();
            var axisHitCount = axisRuns.Length > 0
                ? axisRuns.Sum(static run => Math.Max(0, run.HitCount))
                : hits.Count(hit => QueryMentionsAnyStructuredAxisTerm(hit.RetrievalQuery, axisTerms));
            var requiredSlots = CountRequiredStructuredAxisSlots(slotAxis, axisIndex, targetSlots);
            var routeCandidateCount = candidates.Count(candidate =>
                QueryMentionsAnyStructuredAxisTerm(candidate.Hit.RetrievalQuery, axisTerms));
            var titleCandidateCount = candidates.Count(candidate =>
                QueryMentionsAnyStructuredAxisTerm(candidate.Title, axisTerms));
            var candidatePool = Math.Max(routeCandidateCount, titleCandidateCount);
            var routeFitPool = routeCandidateCount;
            var titlePool = titleCandidateCount;

            var hasLowRuns = axisRuns.Any(static run => run.HitCount <= 1 || run.Busy || !string.IsNullOrWhiteSpace(run.Error));
            var requiredProbeFloor = Math.Min(Math.Max(1, requiredSlots), 3);
            var underCovered = candidatePool < requiredProbeFloor
                               || routeFitPool == 0
                               || (axisTriedQueries.Length > 0 && axisHitCount <= 1)
                               || hasLowRuns;
            if (!underCovered)
                continue;

            var userTerms = axisTerms
                .Where(term => ContainsStructuredAxisPlannerTerm(normalizedUser, term))
                .Take(8)
                .ToArray();
            var unusedPivots = axisTerms
                .Where(term => !axisTriedQueries.Any(query => QueryMentionsAnyStructuredAxisTerm(query, new[] { term })))
                .Take(8)
                .ToArray();
            var suggestedPivots = unusedPivots.Length > 0
                ? unusedPivots
                : axisTerms
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
            var failedOrLow = axisRuns
                .Where(static run => run.HitCount <= 1 || run.Busy || !string.IsNullOrWhiteSpace(run.Error))
                .OrderBy(static run => run.HitCount)
                .ThenBy(static run => run.Query.Length)
                .Select(static run => $"{run.Query} => {run.HitCount} hit(s)")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            lines.Add(
                "axis="
                + FormatPlanningTraceValue(axis)
                + $"|required_slots={requiredSlots}"
                + $"|candidate_pool={candidatePool}"
                + $"|route_fit_pool={routeFitPool}"
                + $"|title_pool={titlePool}"
                + $"|retrieval_hits={axisHitCount}"
                + $"|tried_queries={FormatPlanningTraceValue(string.Join("; ", axisTriedQueries))}"
                + $"|failed_or_low_hit_queries={FormatPlanningTraceValue(string.Join("; ", failedOrLow))}"
                + $"|user_terms={FormatPlanningTraceValue(string.Join(", ", userTerms))}"
                + $"|suggested_pivots={FormatPlanningTraceValue(string.Join(", ", suggestedPivots))}"
                + "|next_action=diversify_this_axis_before_repeating_failed_terms");
        }

        return lines.Count == 0
            ? "none"
            : FormatPromptList(lines, maxLines, maxItemLength: 360);
    }

    private static IEnumerable<string> BuildStructuredAxisPromptTerms(string? axisLabel, string? userMessage)
    {
        foreach (var variant in ExpandPlanningSlotRetrievalTermVariants(axisLabel))
        {
            var normalized = NormalizeLexicalLookup(variant);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }

        var normalizedAxis = NormalizeLexicalLookup(axisLabel);
        if (!string.IsNullOrWhiteSpace(normalizedAxis))
            yield return normalizedAxis;

        var normalizedUser = NormalizeLexicalLookup(userMessage);
        foreach (var term in ExtractPlanningSlotRetrievalTerms(userMessage))
        {
            var normalized = NormalizeLexicalLookup(term);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            var variants = ExpandPlanningSlotRetrievalTermVariants(normalized)
                .Select(NormalizeLexicalLookup)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (variants.Any(variant => ContainsStructuredAxisPlannerTerm(normalizedAxis, variant))
                || variants.Any(variant => PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedUser, variant, normalizedAxis))
                || variants.Any(variant => PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedUser, normalizedAxis, variant))
                || variants.Any(variant => ContainsStructuredAxisPlannerTerm(normalizedUser, variant)
                                           && ContainsStructuredAxisPlannerTerm(normalizedAxis, normalized)))
            {
                foreach (var variant in variants)
                    yield return variant;
            }
        }
    }

    private static bool QueryMentionsAnyStructuredAxisTerm(string? query, IReadOnlyCollection<string> normalizedTerms)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        return !string.IsNullOrWhiteSpace(normalizedQuery)
               && normalizedTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
    }

    private static int CountRequiredStructuredAxisSlots(IReadOnlyList<string> periodAxis, int axisIndex, int targetSlots)
    {
        if (periodAxis.Count == 0 || targetSlots <= 0)
            return 0;

        var normalizedTarget = NormalizeLexicalLookup(periodAxis[axisIndex]);
        var count = 0;
        for (var i = 0; i < targetSlots; i++)
        {
            var normalized = NormalizeLexicalLookup(periodAxis[i % periodAxis.Count]);
            if (string.Equals(normalized, normalizedTarget, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static IEnumerable<(string Query, int HitCount, bool Busy, string? Error)> EnumerateSourceBackedRetrievalQueryRuns(
        ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            var meta = TryGetObject(item.Result, "meta") ?? TryGetObject(item.Result, "Meta");
            if (meta is null)
                continue;

            var runs = TryGetArray(meta.Value, "queryRuns")
                       ?? TryGetArray(meta.Value, "QueryRuns")
                       ?? TryGetArray(meta.Value, "query_runs");
            if (!runs.HasValue || runs.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var run in runs.Value.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object)
                    continue;

                var query = TryGetString(run, "query") ?? TryGetString(run, "Query");
                if (string.IsNullOrWhiteSpace(query))
                    continue;

                yield return (
                    CollapseWhitespace(query),
                    Math.Max(0, TryGetInt(run, "hitCount") ?? TryGetInt(run, "HitCount") ?? TryGetInt(run, "hits") ?? 0),
                    TryGetBool(run, "busy") ?? TryGetBool(run, "Busy") ?? false,
                    TryGetString(run, "error") ?? TryGetString(run, "Error"));
            }
        }
    }

    private static string BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language,
        int maxLines)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            && !LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
        {
            return "none";
        }

        language = NormalizeLanguageCode(language);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        var hasStructuredAxes = dayAxis.Count > 0 && slotAxis.Count > 0;
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(
            effectiveUserMessage,
            targetSlots,
            hasStructuredAxes);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var candidatePoolSize = strictStructuredPlanning
            ? Math.Min(
                ResolveSourceBackedPlanningCandidatePoolSize(effectiveUserMessage, Math.Max(targetSlots, minimumCandidates)),
                Math.Max(32, targetSlots))
            : Math.Max(20, targetSlots);
        var acceptedPool = SelectSourceBackedPlanningCandidates(
                toolResults,
                effectiveUserMessage,
                candidatePoolSize,
                language)
            .ToList();
        var lines = new List<string>
        {
            $"stage=scope|target_slots={targetSlots}|minimum_candidates={minimumCandidates}|structured_axes={FormatPlanningTraceBool(hasStructuredAxes)}|strict={FormatPlanningTraceBool(strictStructuredPlanning)}",
            "stage=candidate_pool"
            + $"|raw_candidates={acceptedPool.Count}"
            + $"|top_titles={FormatPlanningTraceValue(string.Join("; ", acceptedPool.Take(12).Select(static candidate => candidate.Title)))}"
        };

        var accepted = strictStructuredPlanning
            ? SelectPageDiverseSourceBackedPlanningCandidates(
                    acceptedPool,
                    Math.Max(targetSlots, minimumCandidates),
                    effectiveUserMessage)
                .ToList()
            : acceptedPool;

        var reservedTailLines = hasStructuredAxes ? 2 : 1;
        foreach (var candidate in accepted.Take(Math.Max(0, maxLines - lines.Count - reservedTailLines)))
        {
            lines.Add(
                "stage=candidate|decision=accepted"
                + $"|retrieval_query={FormatPlanningTraceValue(candidate.Hit.RetrievalQuery)}"
                + $"|title={FormatPlanningTraceValue(candidate.Title)}"
                + $"|doc={FormatPlanningTraceValue(candidate.Hit.DocPath)}"
                + $"|page={candidate.Hit.PageStart}");
        }

        if (hasStructuredAxes)
        {
            var slotLabels = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
            _ = BuildStructuredSourceBackedSlotAwareGrid(
                accepted,
                slotLabels,
                targetSlots,
                effectiveUserMessage,
                allowSourcedRotation: false,
                requireDistinctItems: true,
                out var routeAwareFit);
            lines.Add(
                "stage=slot_fit"
                + $"|assigned_slots={routeAwareFit.AssignedSlots}"
                + $"|required_slots={targetSlots}"
                + $"|route_evidence={FormatPlanningTraceBool(routeAwareFit.HasRouteEvidence)}"
                + $"|routed_pool={routeAwareFit.RoutedPool}"
                + $"|neutral_pool={routeAwareFit.NeutralPool}"
                + $"|primary_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(slotLabels, routeAwareFit.PrimaryPools))}"
                + $"|alternative_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(slotLabels, routeAwareFit.AlternativePools))}");
        }

        lines.Add(
            "stage=summary"
            + $"|accepted_candidates={accepted.Count}"
            + $"|raw_candidates={acceptedPool.Count}");
        return FormatPromptList(lines, maxLines, maxItemLength: 260);
    }

    private static string BuildSourceBackedLlmCategoryScopeSystemPrompt(string language)
        => $@"
You are SAAIA's retrieval scope adjudicator, not the final answer writer.
Target user language: {NormalizeLanguageCode(language)}.

Choose whether the next retrieval passes should use one exact categoryScope.
Use CATEGORY_HINTS as the only allowed scope values: copy an exact category/path/ref from a hint, but judge fit semantically.
Treat each category label/path/ref as semantic evidence; lexical scores are weak sorting hints only.
A broad category can fit when it naturally contains the user's requested content, even if the label is not a literal query word.
When the user asks to compose a plan, schedule, list or recommendation from source items, choose the category that contains those source items; do not require a category named after the final format.
Do not invent categories.
If no hint clearly matches the user's request, choose null. Do not answer the user.

Return strict JSON only:
{{
  ""categoryDecision"": {{
    ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
    ""decision"": ""use_scope or none"",
    ""confidence"": ""high, medium or low"",
    ""reason"": ""short reason""
  }}
}}";

    private string BuildSourceBackedLlmCategoryScopeUserPrompt(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses,
        string effectiveUserMessage,
        string language,
        string categoryHints)
    {
        var sourceLeads = BuildSourceBackedLlmEvidenceSnapshotForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            maxHits: 2,
            cueMaxLength: 70,
            maxCardHints: 1,
            maxProfileHints: 1,
            retrievalQueryMaxLength: 70);

        return $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

SUFFICIENCY:
- kind: {currentAnalysis.Kind}
- reason: {currentAnalysis.Reason}
- score: {currentAnalysis.Score}
- usableHits: {currentAnalysis.UsableHitCount}
- candidates: {currentAnalysis.CandidateCount}/{currentAnalysis.MinimumCandidateCount}
- targetSlots: {currentAnalysis.TargetSlotCount}

CATEGORY_HINTS:
{categoryHints}

CURRENT_SOURCE_LEADS:
{sourceLeads}

PLANNED_PASSES_WITHOUT_SCOPE:
{FormatSourceBackedLlmEvidenceExplorationPassesForPrompt(plannedPasses)}

DECISION_RULES:
- Pick categoryScope only when one CATEGORY_HINTS line is a clear semantic container for USER_REQUEST.
- Treat the hint label/path/scope itself as semantic evidence; lexicalScore is not a rejection rule.
- Do not reject a category only because it is broader than the specific task, or because its label is not repeated verbatim in USER_REQUEST.
- For plans, schedules, lists or recommendations assembled from source items, category fit is about where the source items live, not whether the category label names the final format.
- If exactly one hinted category is semantically plausible and the other hints are clearly unrelated, prefer using that categoryScope with medium or high confidence.
- Keep null for weak lexical coincidences, ambiguous requests, or when several unrelated categories could fit.
- The decision must be generic and based only on USER_REQUEST, REQUEST_SHAPE, current evidence and CATEGORY_HINTS.
- Return JSON only. Do not write search queries, final answers, citations or UI prose.";
    }

    private static string FormatSourceBackedLlmEvidenceExplorationPassesForPrompt(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses)
    {
        if (plannedPasses.Count == 0)
            return "none";

        return string.Join(
            Environment.NewLine,
            plannedPasses
                .Take(MaxSourceBackedLlmEvidenceExplorationPasses)
                .Select(pass =>
                {
                    var scope = string.IsNullOrWhiteSpace(pass.CategoryScope) ? "null" : pass.CategoryScope;
                    var docScope = string.Join(
                        " ",
                        new[]
                        {
                            string.IsNullOrWhiteSpace(pass.DocId) ? null : $"docId:{pass.DocId}",
                            string.IsNullOrWhiteSpace(pass.DocPath) ? null : $"docPath:{pass.DocPath}",
                            pass.PageStart is null ? null : $"pageStart:{pass.PageStart}",
                            pass.PageEnd is null ? null : $"pageEnd:{pass.PageEnd}"
                        }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                    var queries = string.Join("; ", pass.Queries.Take(4).Select(static query => TruncateForPrompt(query, 80)));
                    return TruncateForPrompt(
                        $"- {pass.Label} | categoryScope: {scope} | docScope: {(string.IsNullOrWhiteSpace(docScope) ? "none" : docScope)} | queries: {queries}",
                        260);
                }));
    }

    private static string BuildSourceBackedAvailableResearchSurfacesForPrompt()
        => """
- CATEGORY_HINTS: exact categoryScope only when semantically fitting; otherwise null.
- STRUCTURE_HINTS: maps from trees, summaries, profiles, headings and cards; use them only to reach concrete pages.
- CURRENT_SOURCE_LEADS: separate concrete page/card evidence from navigation/profile/index leads.
- WORKING_NOTES: bounded notes from earlier research passes; use for strategy only, never as source evidence.
- matchedContentCards/profileSignals/selectionHints: use as pivots and quality hints, not standalone facts.
- retrievalQuery/queryRuns: avoid repeats; search complementary facets, discovered labels and source-language variants.
- rag.multi_search: output JSON passes only; no final answer text.
""";

    private static string BuildSourceBackedRequestShapeForPrompt(string? effectiveUserMessage, string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage ?? string.Empty);
        var normalizedLanguage = NormalizeLanguageCode(language);
        var flags = new List<string>();
        if (LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
            flags.Add("planning");
        if (LooksLikeGenericCollectionOrListRequest(intentQuery))
            flags.Add("collection_or_list");
        if (LooksLikeMultipleCandidateSynthesisRequest(intentQuery))
            flags.Add("multiple_candidates");
        if (LooksLikeSoftChoiceRecommendationRequest(intentQuery))
            flags.Add("recommendation");
        if (LooksLikeSourceBackedPairingRecommendationRequest(intentQuery))
            flags.Add("pairing");
        if (LooksLikeBroadSourceBackedCompositionRequest(intentQuery))
            flags.Add("composition");
        if (LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery))
            flags.Add("synthesis_or_plan");
        if (flags.Count == 0)
            flags.Add("targeted");

        var dayLabels = DetectRequestedDayAxisLabels(intentQuery, normalizedLanguage);
        var explicitSlotLabels = DetectRequestedPlanningSlotAxisLabels(intentQuery, normalizedLanguage);
        var hasStructuredAxes = dayLabels.Count > 0 && explicitSlotLabels.Count > 0;
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(intentQuery);
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(intentQuery, targetSlots, hasStructuredAxes);

        var lines = new List<string>
        {
            $"- flags: {string.Join(", ", flags.Distinct(StringComparer.OrdinalIgnoreCase))}",
            $"- detectedLanguage: {normalizedLanguage}",
            $"- targetSlots: {targetSlots}",
            $"- minimumCandidates: {minimumCandidates}",
            $"- structuredAxes: {(hasStructuredAxes ? "yes" : "no")}"
        };

        if (dayLabels.Count > 0)
            lines.Add($"- requestedDayAxis: {string.Join(", ", dayLabels.Take(10))}");
        if (explicitSlotLabels.Count > 0)
            lines.Add($"- requestedSlotAxis: {string.Join(", ", explicitSlotLabels.Take(10))}");
        if (hasStructuredAxes)
        {
            lines.Add("- structuredSearchGuidance: search complementary slot/type/facet terms, constraints, candidate inventory and concrete candidate labels; day-axis labels are placement targets, not enough as broad retrieval queries unless source leads are explicitly organized by those labels. Do not fan out the same query once per day/row/column.");
            lines.Add("- balancedSlotCoverage: cover multiple requested slot/type/facet labels before repeating one label with near-duplicate wording.");
            lines.Add("- plannerCoverageContract: your query set is invalid if it drops a requested slot/type/facet already covered by the current router queries.");
        }

        lines.Add("- searchObjective: gather enough distinct page-grounded candidates and context to let the writer synthesize a useful answer; if evidence is weak, keep exploring before falling back.");
        lines.Add("- categoryObjective: pick a semantic categoryScope from CATEGORY_HINTS when one hinted category clearly contains the requested content; category labels can be broader than the task, but keep null for weak single-term or ambiguous hints.");
        lines.Add("- evidenceObjective: prefer concrete content pages; use navigation, profiles, summaries and tables of contents as maps for follow-up retrieval.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSourceBackedLlmEvidenceSnapshotForPrompt(
        ToolResults toolResults,
        string query,
        string language,
        int maxHits = 10,
        int cueMaxLength = 120,
        int maxCardHints = 3,
        int maxProfileHints = 4,
        int retrievalQueryMaxLength = 120)
    {
        var lines = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .OrderByDescending(static hit => LooksLikeNavigationOnlyHit(hit) ? 0 : ComputeSourceBackedEvidenceRichnessScore(hit))
            .ThenByDescending(static hit => hit.Score)
            .Take(Math.Max(1, maxHits))
            .Select(hit =>
            {
                var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                var cue = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: cueMaxLength);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = ExtractRouteDiscoveryTitleCue(hit);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = BuildSourceBackedCandidateSupportCue(hit);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = "source-backed hit";
                var role = LooksLikeNavigationOnlyHit(hit) && IsRouteDiscoveryAnchorHit(hit)
                    ? "navigation/title anchor"
                    : "source lead";
                var signalParts = new List<string>();
                var signalRole = CollapseWhitespace(hit.SelectionHintRole ?? hit.ContentRole ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(signalRole))
                    signalParts.Add($"signalRole: {signalRole}");
                var richness = ComputeSourceBackedEvidenceRichnessScore(hit);
                if (richness > 0)
                    signalParts.Add($"richness: {richness}");
                if (hit.MatchedContentCards is { Count: > 0 })
                {
                    var cardHints = hit.MatchedContentCards
                        .Select(static card => CollapseWhitespace(card.Title))
                        .Where(static title => !string.IsNullOrWhiteSpace(title))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, maxCardHints))
                        .ToArray();
                    if (cardHints.Length > 0)
                        signalParts.Add($"contentCards: {string.Join("; ", cardHints)}");
                }

                var profileHints = hit.ProfileSignals is null
                    ? Array.Empty<string>()
                    : hit.ProfileSignals.Topics
                        .Concat(hit.ProfileSignals.Keywords)
                        .Concat(hit.ProfileSignals.Entities)
                        .Concat(hit.ProfileSignals.MatchedTerms)
                        .Select(CollapseWhitespace)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, maxProfileHints))
                        .ToArray();
                if (profileHints.Length > 0)
                    signalParts.Add($"profileHints: {string.Join("; ", profileHints)}");

                var retrievalQuery = string.IsNullOrWhiteSpace(hit.RetrievalQuery)
                    ? string.Empty
                    : $" | retrievalQuery: {TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery), retrievalQueryMaxLength)}";
                var signalSuffix = signalParts.Count == 0 ? string.Empty : $" | {string.Join(" | ", signalParts)}";
                return $"- {role}: {source} {SourceBackedPagePrefix(language)}{Math.Max(1, hit.PageStart)} | {CollapseWhitespace(cue)}{signalSuffix}{retrievalQuery}";
            })
            .ToArray();

        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static string BuildSourceBackedStructureHintsForPrompt(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language,
        int maxLines = 48)
    {
        _ = query;

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(2))
        {
            AppendTreeStructureHints(item.Result, lines, seen);
        }

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "summary.search" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(2))
        {
            AppendSummarySearchStructureHints(item.Result, lines, seen);
        }

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "documents.navigation" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(3))
        {
            AppendDocumentNavigationStructureHints(item.Result, lines, seen);
        }

        AppendPreviousSourceStructureHints(lastSourcesUsed, language, lines, seen);

        return lines.Count == 0
            ? "none"
            : LimitPromptBlockLines(lines, maxLines, maxLineLength: 220);
    }

    private static void AppendSummarySearchStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 48)
            return;

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (lines.Count >= 48)
                return;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = CollapseWhitespace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty);
            var docName = CollapseWhitespace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName") ?? Path.GetFileName(docPath));
            var label = CollapseWhitespace(
                TryGetString(entry, "label")
                ?? TryGetString(entry, "Label")
                ?? docName
                ?? docPath);
            var summaryText = CollapseWhitespace(TryGetString(entry, "summaryText") ?? TryGetString(entry, "SummaryText") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(summaryText))
                continue;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(label))
                parts.Add($"source: {label}");
            if (!string.IsNullOrWhiteSpace(docPath) && !string.Equals(docPath, docName, StringComparison.OrdinalIgnoreCase))
                parts.Add($"path: {docPath}");

            var category = CollapseWhitespace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "category") ?? TryGetString(entry, "Category") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var pageStart = TryGetInt(entry, "pageStart") ?? TryGetInt(entry, "PageStart");
            var pageEnd = TryGetInt(entry, "pageEnd") ?? TryGetInt(entry, "PageEnd");
            if (pageStart is not null)
            {
                parts.Add(pageEnd is not null && pageEnd.Value > pageStart.Value
                    ? $"pages: {pageStart}-{pageEnd}"
                    : $"page: {pageStart}");
            }

            var level = CollapseWhitespace(TryGetString(entry, "level") ?? TryGetString(entry, "Level") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(level))
                parts.Add($"summaryLevel: {level}");

            if (!string.IsNullOrWhiteSpace(summaryText))
                parts.Add($"summary: {TruncateForPrompt(summaryText, 220)}");

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly summary: {TruncateForPrompt(string.Join(" | ", parts), 320)}");
        }
    }

    private static void AppendDocumentNavigationStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 48)
            return;

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (lines.Count >= 48)
                return;

            var label = CleanNavigationRouteAnchorTitle(
                TryGetString(entry, "label")
                ?? TryGetString(entry, "Label")
                ?? TryGetString(entry, "title")
                ?? TryGetString(entry, "Title"));
            if (!IsUsableSourceBackedOptionTitle(label) || LooksLikeNavigationIndexHeadingTitle(label))
                continue;

            var docPath = CollapseWhitespace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty);
            var docName = CollapseWhitespace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName") ?? Path.GetFileName(docPath));
            var source = string.IsNullOrWhiteSpace(docPath)
                ? docName
                : string.IsNullOrWhiteSpace(docName) || string.Equals(docName, docPath, StringComparison.OrdinalIgnoreCase)
                    ? docPath
                    : $"{docName} | {docPath}";

            var pageStart = TryGetInt(entry, "targetPageStart") ?? TryGetInt(entry, "TargetPageStart") ?? TryGetInt(entry, "sourcePage") ?? TryGetInt(entry, "SourcePage");
            var pageEnd = TryGetInt(entry, "targetPageEnd") ?? TryGetInt(entry, "TargetPageEnd");
            var pagePart = pageStart is null
                ? string.Empty
                : pageEnd is not null && pageEnd.Value > pageStart.Value
                    ? $" | pages: {pageStart}-{pageEnd}"
                    : $" | page: {pageStart}";

            var parts = new List<string> { label };
            if (!string.IsNullOrWhiteSpace(source))
                parts.Add($"source: {source}");
            if (!string.IsNullOrWhiteSpace(pagePart))
                parts.Add(pagePart.Trim(' ', '|'));

            var category = CollapseWhitespace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "category") ?? TryGetString(entry, "Category") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var kind = CollapseWhitespace(TryGetString(entry, "kind") ?? TryGetString(entry, "Kind") ?? string.Empty);
            var method = CollapseWhitespace(TryGetString(entry, "resolutionMethod") ?? TryGetString(entry, "ResolutionMethod") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(method))
                parts.Add($"navigation: {string.Join("/", new[] { kind, method }.Where(static value => !string.IsNullOrWhiteSpace(value)))}");

            var confidence = TryGetDouble(entry, "confidence") ?? TryGetDouble(entry, "Confidence");
            if (confidence is not null)
                parts.Add(FormattableString.Invariant($"confidence: {confidence.Value:0.##}"));

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly documentNavigation: {TruncateForPrompt(string.Join(" | ", parts), 280)}");
        }
    }

    private static void AppendTreeStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 36)
            return;

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("markdown", out var markdown)
            && markdown.ValueKind == JsonValueKind.String)
        {
            AppendMarkdownTreeStructureHints(markdown.GetString(), lines, seen);
        }

        AppendTreeJsonStructureHints(result, lines, seen, depth: 0);
    }

    private static void AppendMarkdownTreeStructureHints(string? markdown, List<string> lines, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        foreach (var raw in markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (lines.Count >= 36)
                return;

            var text = CollapseWhitespace(raw);
            text = Regex.Replace(text, @"^[\s\-\*\+\u2022\u00b7|`>\\/.]+", string.Empty, RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"^(?:[????]+\s*)+", string.Empty, RegexOptions.CultureInvariant);
            text = CollapseWhitespace(text.Trim());
            if (string.IsNullOrWhiteSpace(text))
                continue;

            AddStructureHintLine(lines, seen, $"navigationOnly tree: {TruncateForPrompt(text, 160)}");
        }
    }

    private static void AppendTreeJsonStructureHints(JsonElement element, List<string> lines, ISet<string> seen, int depth)
    {
        if (lines.Count >= 36 || depth > 5)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var label = TryBuildTreeJsonStructureLabel(element);
                if (!string.IsNullOrWhiteSpace(label))
                    AddStructureHintLine(lines, seen, $"navigationOnly tree: {TruncateForPrompt(label, 160)}");

                foreach (var property in element.EnumerateObject())
                {
                    if (lines.Count >= 36)
                        return;

                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && IsTreeStructureProperty(property.Name))
                    {
                        AppendTreeJsonStructureHints(property.Value, lines, seen, depth + 1);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (lines.Count >= 36)
                        return;

                    AppendTreeJsonStructureHints(item, lines, seen, depth + 1);
                }

                break;
        }
    }

    private static bool IsTreeStructureProperty(string name)
        => name is "nodes" or "items" or "entries" or "children" or "folders" or "documents" or "categories"
           || name.Equals("Nodes", StringComparison.Ordinal)
           || name.Equals("Items", StringComparison.Ordinal)
           || name.Equals("Entries", StringComparison.Ordinal)
           || name.Equals("Children", StringComparison.Ordinal)
           || name.Equals("Folders", StringComparison.Ordinal)
           || name.Equals("Documents", StringComparison.Ordinal)
           || name.Equals("Categories", StringComparison.Ordinal);

    private static string TryBuildTreeJsonStructureLabel(JsonElement value)
    {
        var label =
            TryGetString(value, "name")
            ?? TryGetString(value, "displayName")
            ?? TryGetString(value, "title")
            ?? TryGetString(value, "canonicalName")
            ?? TryGetString(value, "docName")
            ?? TryGetString(value, "label")
            ?? TryGetString(value, "path")
            ?? TryGetString(value, "categoryPath")
            ?? TryGetString(value, "docPath")
            ?? TryGetString(value, "categoryRef");

        label = CollapseWhitespace(label ?? string.Empty);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var path =
            TryGetString(value, "categoryPath")
            ?? TryGetString(value, "path")
            ?? TryGetString(value, "docPath");
        path = CollapseWhitespace(path ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(path)
            && !string.Equals(path, label, StringComparison.OrdinalIgnoreCase))
        {
            label = $"{path} > {label}";
        }

        var totalDocuments = TryGetInt(value, "totalDocuments")
                             ?? TryGetInt(value, "directDocuments")
                             ?? TryGetInt(value, "documentCount");
        if (totalDocuments is > 0)
            label = $"{label} ({totalDocuments} docs)";

        return label;
    }

    private static void AppendPreviousSourceStructureHints(
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string language,
        List<string> lines,
        ISet<string> seen)
    {
        if (lastSourcesUsed is null || lastSourcesUsed.Count == 0)
            return;

        foreach (var source in lastSourcesUsed.Take(12))
        {
            if (lines.Count >= 48)
                return;

            var docLabel = CollapseWhitespace(source.DocName ?? Path.GetFileName(source.DocPath) ?? source.Label);
            if (string.IsNullOrWhiteSpace(docLabel))
                continue;

            var parts = new List<string>
            {
                $"{docLabel} {SourceBackedPagePrefix(language)}{Math.Max(1, source.PageStart)}"
            };

            var category = CollapseWhitespace(source.CategoryPath ?? source.Category ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var heading = CollapseWhitespace(source.HeadingPath ?? source.SectionTitle ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(heading))
                parts.Add($"heading: {TruncateForPrompt(heading, 120)}");

            var cardHints = source.MatchedContentCards
                .Select(static card => CollapseWhitespace(card.Title))
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (cardHints.Length > 0)
                parts.Add($"contentCards: {string.Join("; ", cardHints)}");

            var profileHints = EnumerateSourceProfileHints(source.ProfileSignals)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
            if (profileHints.Length > 0)
                parts.Add($"profileHints: {string.Join("; ", profileHints)}");

            var role = CollapseWhitespace(source.SelectionHintEvidenceRole ?? source.ContentRole ?? source.NavigationReason ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(role))
                parts.Add($"signalRole: {role}");

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly previousSource: {TruncateForPrompt(string.Join(" | ", parts), 260)}");
        }
    }

    private static IEnumerable<string> EnumerateSourceProfileHints(ToolMemory.SourceProfileSignalsRef? profile)
    {
        if (profile is null)
            yield break;

        foreach (var value in profile.Topics
                     .Concat(profile.Keywords)
                     .Concat(profile.Entities)
                     .Concat(profile.MatchedTerms)
                     .Concat(profile.HypotheticalQuestions))
        {
            var cleaned = CollapseWhitespace(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
                yield return cleaned;
        }
    }

    private static void AddStructureHintLine(List<string> lines, ISet<string> seen, string line)
    {
        line = CollapseWhitespace(line);
        if (string.IsNullOrWhiteSpace(line) || !seen.Add(line))
            return;

        lines.Add(line);
    }

    private string BuildSourceBackedLlmCategoryHintsForPrompt(string effectiveUserMessage, int maxCategories = 80)
    {
        var query = NormalizeLexicalLookup(effectiveUserMessage);
        var categories = (_mem.LastPresentedCategories ?? new List<ToolMemory.CategorySnapshot>())
            .Concat(_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .GroupBy(static category => CollapseWhitespace(
                string.IsNullOrWhiteSpace(category.CategoryRef)
                    ? string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath
                    : category.CategoryRef), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(category => new
            {
                Category = category,
                Score = ComputeCategoryHintScore(category, query)
            })
            .OrderByDescending(static x => x.Score)
            .ThenBy(static x => x.Category.Ordinal)
            .Take(Math.Max(1, maxCategories))
            .Select(static x =>
            {
                var category = x.Category;
                var name = CollapseWhitespace(category.DisplayName);
                var path = CollapseWhitespace(category.CategoryPath);
                var categoryRef = CollapseWhitespace(category.CategoryRef);
                var aliases = category.Aliases is { Count: > 0 }
                    ? $" | aliases: {string.Join(", ", category.Aliases.Select(CollapseWhitespace).Where(static a => !string.IsNullOrWhiteSpace(a)).Take(2))}"
                    : string.Empty;
                var docs = category.TotalDocuments > 0 ? $" | docs: {category.TotalDocuments}" : string.Empty;
                var score = $" | lexicalScore: {x.Score}";
                var reference = string.IsNullOrWhiteSpace(categoryRef) ? string.Empty : $" | ref: {categoryRef}";
                var scopeValue = string.IsNullOrWhiteSpace(categoryRef)
                    ? string.IsNullOrWhiteSpace(path) ? name : path
                    : categoryRef;
                var scope = string.IsNullOrWhiteSpace(scopeValue) ? string.Empty : $" | scopeValue: {scopeValue}";
                var semanticLabel = string.IsNullOrWhiteSpace(path) || string.Equals(path, name, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"{name} / {path}";
                return TruncateForPrompt(
                    $"- {name}{scope}{(string.IsNullOrWhiteSpace(path) || string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" | path: {path}")}{reference}{docs}{aliases}{score} | semanticLabel: {semanticLabel}",
                    260);
            })
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return categories.Length == 0 ? "none" : string.Join(Environment.NewLine, categories);
    }

    private static int ComputeCategoryHintScore(ToolMemory.CategorySnapshot category, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return 0;

        var haystack = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            category.DisplayName,
            category.CategoryPath,
            string.Join(' ', category.Aliases ?? new List<string>())
        }));
        if (string.IsNullOrWhiteSpace(haystack))
            return 0;

        return ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Count(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, NormalizeRagQueryForRetrieval(effectiveUserMessage));

        foreach (var query in BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(effectiveUserMessage, language))
            AddDistinctQuery(queries, query);

        foreach (var retrievalQuery in EnumerateRagHitSummaries(toolResults)
                     .Select(static hit => hit.RetrievalQuery)
                     .Where(static query => !string.IsNullOrWhiteSpace(query))
                     .Select(static query => query!))
        {
            AddDistinctQuery(queries, retrievalQuery);
        }

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(query => ScoreSourceBackedLlmPlannerPromptQuery(query, effectiveUserMessage))
            .ThenBy(static query => query.Length)
            .Take(MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(
        string effectiveUserMessage,
        string language)
    {
        var queries = new List<string>();
        var intentProbe = BuildInitialSourceBackedPlanningIntentProbeQuery(effectiveUserMessage);
        var intentProbeKey = NormalizeLexicalLookup(intentProbe);
        void Add(string? query)
        {
            if (!string.IsNullOrWhiteSpace(query))
                AddDistinctQuery(queries, CollapseWhitespace(query));
        }

        if (LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
        {
            Add(intentProbe);
            foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildPlanningRetrievalQueries(effectiveUserMessage))
                Add(query);
        }
        else
        {
            foreach (var query in BuildSourceBackedActionRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildSourceBackedEvidenceExpansionRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage))
                Add(query);
        }

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(query => string.Equals(NormalizeLexicalLookup(query), intentProbeKey, StringComparison.Ordinal) ? -200 : ScoreSourceBackedLlmPlannerPromptQuery(query, effectiveUserMessage))
            .ThenBy(static query => query.Length)
            .Take(MaxSourceBackedLlmEvidencePlannerDeterministicSeeds)
            .ToArray();
    }

    private static int ScoreSourceBackedLlmPlannerPromptQuery(string query, string effectiveUserMessage)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return int.MaxValue;

        var score = query.Length > 120 ? 120 : query.Length > 90 ? 60 : query.Length > 70 ? 25 : 0;
        var normalizedUser = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (!string.IsNullOrWhiteSpace(normalizedUser)
            && string.Equals(normalizedQuery, normalizedUser, StringComparison.Ordinal)
            && query.Length > 70)
        {
            score += 140;
        }

        var tokenCount = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (tokenCount > 10)
            score += 60;
        else if (tokenCount <= 1)
            score += 15;

        return score;
    }

    private static string FormatPromptList(IEnumerable<string> values, int maxItems = 40, int maxItemLength = 160)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"- {TruncateForPrompt(CollapseWhitespace(value), maxItemLength)}")
            .Take(Math.Max(1, maxItems))
            .ToArray();
        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static string LimitPromptBlockLines(IEnumerable<string> values, int maxLines, int maxLineLength)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TruncateForPrompt(CollapseWhitespace(value), maxLineLength))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(Math.Max(1, maxLines))
            .ToArray();
        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static int CountRagRetrievalToolCalls(ToolResults toolResults)
        => toolResults.Items.Count(static item => item.ToolName is "rag.search" or "rag.multi_search");

    private static bool HasAttemptedBroadenedSourceBackedRetrieval(ToolResults toolResults)
        => CountRagRetrievalToolCalls(toolResults) > 1
           || toolResults.Items.Any(static item =>
               (item.ToolName is "documents.tree" or "documents.navigation" or "summary.search")
               && string.IsNullOrWhiteSpace(item.Error));

    private async Task<bool> TryExpandBackendGuidanceClarificationRetrievalAsync(
        ToolResults toolResults,
        RouterPlan plan,
        string effectiveUserMessage,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onProgress)
    {
        if (!HasBackendGuidanceClarification(toolResults)
            || !ShouldExpandDocumentaryProbeRetrieval(toolResults, effectiveUserMessage, plan.Language))
        {
            EmitRagTrace(
                "backend_guidance.expansion.skipped",
                ("has_guidance", HasBackendGuidanceClarification(toolResults)),
                ("query", effectiveUserMessage));
            return false;
        }

        var queries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage);
        if (queries.Length == 0)
        {
            EmitRagTrace(
                "backend_guidance.expansion.skipped",
                ("reason", "no_queries"),
                ("query", effectiveUserMessage));
            return false;
        }

        var args = CreateJsonArgs(new
        {
            queries,
            topK = ResolveDocumentaryProbeTopK(effectiveUserMessage),
            category = ResolveRagCategoryScope(effectiveUserMessage),
            mode = "broad",
            researchMode = "source_exploration",
            includeResearchSurfaces = true
        });

        var sw = Stopwatch.StartNew();
        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressSearchSourceBackedCandidates(plan.Language));
            EmitRagTrace(
                "backend_guidance.expansion.start",
                ("queries", queries),
                ("query", effectiveUserMessage),
                ("top_k", ResolveDocumentaryProbeTopK(effectiveUserMessage)),
                ("category", ResolveRagCategoryScope(effectiveUserMessage)));
            var expandedResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
            sw.Stop();
            if (!HasRagHits(expandedResult))
            {
                EmitRagTrace(
                    "backend_guidance.expansion.end",
                    ("accepted", false),
                    ("reason", "no_hits"),
                    ("hits", 0),
                    ("ms", sw.ElapsedMilliseconds));
                return false;
            }

            var candidate = new ToolResults();
            candidate.Items.AddRange(toolResults.Items);
            candidate.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });

            if (!IsBetterDocumentaryProbeCoverage(toolResults, candidate, effectiveUserMessage, plan.Language))
            {
                EmitRagTrace(
                    "backend_guidance.expansion.end",
                    ("accepted", false),
                    ("reason", "no_coverage_gain"),
                    ("hits", CountRagHits(expandedResult)),
                    ("ms", sw.ElapsedMilliseconds));
                return false;
            }

            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });
            _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
            if (!_mem.LastToolNames.Contains("rag.multi_search", StringComparer.OrdinalIgnoreCase))
                _mem.LastToolNames.Add("rag.multi_search");
            EmitRagTrace(
                "backend_guidance.expansion.end",
                ("accepted", true),
                ("hits", CountRagHits(expandedResult)),
                ("ms", sw.ElapsedMilliseconds));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "backend_guidance.expansion.end",
                ("accepted", false),
                ("reason", "error"),
                ("ms", sw.ElapsedMilliseconds));
            return false;
        }
    }

    private static bool HasBackendGuidanceClarification(ToolResults toolResults)
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
            if (string.Equals(behavior, "ask_clarification", StringComparison.OrdinalIgnoreCase)
                || string.Equals(responseShape, "clarify", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateLastListedDocumentsFromExtractionQualityResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var docs = new List<ToolMemory.DocumentItem>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = (TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/');
            if (string.IsNullOrWhiteSpace(docPath))
                continue;

            var docName =
                TryGetString(entry, "docName")
                ?? TryGetString(entry, "DocName")
                ?? TryGetString(entry, "canonicalName")
                ?? TryGetString(entry, "CanonicalName")
                ?? Path.GetFileName(docPath);
            var categoryPath = (TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? GuessCategoryPath(docPath))
                .Replace('\\', '/')
                .Trim('/');
            var category = TryGetString(entry, "category")
                ?? TryGetString(entry, "Category")
                ?? ExtractTopLevelCategoryFromPath(categoryPath);
            var pdfRef = NormalizePdfRef(TryGetString(entry, "pdfRef") ?? TryGetString(entry, "PdfRef"))
                ?? $"PDF{docs.Count + 1:00}";

            var doc = new ToolMemory.DocumentItem
            {
                PdfRef = pdfRef,
                DocId = TryGetString(entry, "docId") ?? TryGetString(entry, "DocId") ?? string.Empty,
                DocPath = docPath,
                DocName = string.IsNullOrWhiteSpace(docName) ? Path.GetFileName(docPath) : docName,
                Category = category ?? string.Empty,
                CategoryRef = NullIfWhiteSpace(TryGetString(entry, "categoryRef") ?? TryGetString(entry, "CategoryRef")),
                CategoryPath = categoryPath,
                SourceHash = NullIfWhiteSpace(TryGetString(entry, "sourceHash") ?? TryGetString(entry, "SourceHash")),
                DocLanguage = NullIfWhiteSpace(TryGetDocumentLanguage(entry)),
                ProfileLanguage = NullIfWhiteSpace(TryGetString(entry, "profileLanguage") ?? TryGetString(entry, "ProfileLanguage")),
                Pages = TryGetInt(entry, "pageCount") ?? TryGetInt(entry, "PageCount") ?? TryGetInt(entry, "pages") ?? TryGetInt(entry, "Pages")
            };

            docs.Add(doc);
            RegisterDocumentReference(doc.PdfRef, doc);
            RegisterDocumentReference(doc.DocId, doc);
            RegisterDocumentReference(doc.DocPath, doc);
            RegisterDocumentReference(doc.DocName, doc);
        }

        if (docs.Count == 0)
            return;

        _mem.LastListedDocuments = docs;
        _mem.LastListOffset = 0;
        _mem.LastListLimit = TryGetInt(result, "limit") ?? docs.Count;
        _mem.LastListTotal = TryGetInt(result, "total") ?? TryGetInt(TryGetObject(result, "summary") ?? default, "totalDocuments") ?? docs.Count;
        _mem.LastListEndOfList = true;
        _mem.LastListCategoryPath = TryGetString(result, "scopePath");
        _mem.LastListQuery = null;
        _mem.PromoteDocumentsToWorkspace(docs);
    }

    private void RegisterDocumentReference(string? key, ToolMemory.DocumentItem doc)
    {
        key = NullIfWhiteSpace(key);
        if (key is null)
            return;

        _mem.PdfMap[key] = doc;
    }

    private static string? NormalizePdfRef(string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is null)
            return null;

        var match = Regex.Match(value, @"(?i)^PDF\s*0*(?<n>\d{1,4})$");
        return match.Success && int.TryParse(match.Groups["n"].Value, out var n) && n > 0
            ? $"PDF{n:00}"
            : value;
    }

    private static string ExtractTopLevelCategoryFromPath(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        var index = normalized.IndexOf('/');
        return index > 0 ? normalized[..index] : normalized;
    }

    private object? BuildListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null)
            return null;

        try
        {
            var (docs, limit, offset, total, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            return new
            {
                scopePath = TryInferDocumentsScopePath(item.Result),
                limit,
                offset,
                total,
                endOfList,
                dropped,
                items = docs.Select(d => new
                {
                    pdfRef = d.PdfRef,
                    docId = d.DocId,
                    docPath = d.DocPath,
                    docName = d.DocName,
                    category = d.Category,
                    categoryPath = d.CategoryPath,
                    pages = d.Pages
                }).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildTreeInventoryData(ToolResults toolResults)
    {
        var treeItem = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error));
        if (treeItem is null || treeItem.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var markdown = treeItem.Result.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String
                ? (md.GetString() ?? string.Empty).Trim()
                : string.Empty;

            return new
            {
                path = TryGetString(treeItem.Result, "path") ?? string.Empty,
                depth = TryGetInt(treeItem.Result, "depth"),
                limit = TryGetInt(treeItem.Result, "limit"),
                offset = TryGetInt(treeItem.Result, "offset"),
                totalNodes = TryGetInt(treeItem.Result, "totalNodes"),
                totalDocuments = TryGetInt(treeItem.Result, "totalDocuments"),
                markdown
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildStatsInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rootFolders = new List<object>();
            if (item.Result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in rf.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    rootFolders.Add(new
                    {
                        path = TryGetString(x, "path") ?? string.Empty,
                        name = TryGetString(x, "name") ?? string.Empty,
                        totalDocuments = TryGetInt(x, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(x, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(x, "subfolderCount") ?? 0
                    });
                }
            }

            var foldersByDepth = new List<object>();
            if (item.Result.TryGetProperty("foldersByDepth", out var fd) && fd.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in fd.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    foldersByDepth.Add(new
                    {
                        depth = TryGetInt(x, "depth") ?? 0,
                        folderCount = TryGetInt(x, "folderCount") ?? 0
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                totalDocuments = TryGetInt(item.Result, "totalDocuments") ?? 0,
                maxDepth = TryGetInt(item.Result, "maxDepth") ?? 0,
                totalNonEmptyFolders = TryGetInt(item.Result, "totalNonEmptyFolders") ?? 0,
                topLevelFolderCount = TryGetInt(item.Result, "topLevelFolderCount") ?? 0,
                leafFolderCount = TryGetInt(item.Result, "leafFolderCount") ?? 0,
                includesEmptyFolders = TryGetBoolProp(item.Result, "includesEmptyFolders") ?? false,
                emptyFoldersKnown = TryGetBoolProp(item.Result, "emptyFoldersKnown") ?? false,
                foldersByDepth,
                rootFolders
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildCategoriesInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rows = new List<object>();
            if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var aliases = new List<string>();
                    if (entry.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alias in aliasArray.EnumerateArray())
                        {
                            if (alias.ValueKind != JsonValueKind.String)
                                continue;

                            var value = alias.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(value))
                                aliases.Add(value.Trim());
                        }
                    }

                    rows.Add(new
                    {
                        ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0,
                        displayOrder = TryGetInt(entry, "displayOrder") ?? TryGetInt(entry, "ordinal") ?? 0,
                        path = TryGetString(entry, "path") ?? string.Empty,
                        name = TryGetString(entry, "name") ?? string.Empty,
                        depth = TryGetInt(entry, "depth") ?? 0,
                        totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(entry, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(entry, "subfolderCount") ?? 0,
                        aliases = aliases
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                total = TryGetInt(item.Result, "total") ?? rows.Count,
                limit = TryGetInt(item.Result, "limit") ?? rows.Count,
                offset = TryGetInt(item.Result, "offset") ?? 0,
                items = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildSummaryStatusCountInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0,
            missingStored = TryGetInt(item.Result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? 0,
            profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing")
        };
    }

    private static object? BuildSummaryStatusListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var rows = new List<object>();
        var hasItems = item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = item.Result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;

        if (hasItems)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                rows.Add(new
                {
                    docId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
                    docPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
                    docName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? TryGetString(entry, "CanonicalName") ?? TryGetString(entry, "canonicalName") ?? string.Empty,
                    category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? TryGetString(entry, "CategoryCanonicalName") ?? TryGetString(entry, "categoryCanonicalName") ?? string.Empty,
                    categoryRef = TryGetString(entry, "CategoryRef") ?? TryGetString(entry, "categoryRef"),
                    categoryPath = TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "categoryPath"),
                    summaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing",
                    capabilityBProfileState = TryGetString(entry, "CapabilityBProfileState") ?? TryGetString(entry, "capabilityBProfileState"),
                    capabilityBHasBackofficeProfile = TryGetBool(entry, "CapabilityBHasBackofficeProfile") ?? TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? false,
                    capabilityBReasons = ExtractCompactSignals(entry, "CapabilityBReasons").Concat(ExtractCompactSignals(entry, "capabilityBReasons")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    hasActiveSummaryJob = TryGetBool(entry, "HasActiveSummaryJob") ?? TryGetBool(entry, "hasActiveSummaryJob") ?? false,
                    activeSummaryJobId = TryGetString(entry, "ActiveSummaryJobId") ?? TryGetString(entry, "activeSummaryJobId"),
                    activeSummaryJobType = TryGetString(entry, "ActiveSummaryJobType") ?? TryGetString(entry, "activeSummaryJobType"),
                    activeSummaryJobStatus = TryGetString(entry, "ActiveSummaryJobStatus") ?? TryGetString(entry, "activeSummaryJobStatus"),
                    activeSummaryJobExecutionMode = TryGetString(entry, "ActiveSummaryJobExecutionMode") ?? TryGetString(entry, "activeSummaryJobExecutionMode"),
                    activeSummaryJobRuntimeCapabilityKey = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityKey") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityKey"),
                    activeSummaryJobRuntimeCapabilityStatus = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityStatus") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityStatus"),
                    activeSummaryJobEnqueueSource = TryGetString(entry, "ActiveSummaryJobEnqueueSource") ?? TryGetString(entry, "activeSummaryJobEnqueueSource"),
                    activeSummaryJobCampaignId = TryGetString(entry, "ActiveSummaryJobCampaignId") ?? TryGetString(entry, "activeSummaryJobCampaignId"),
                    capabilityBReadyToEnqueue = TryGetBool(entry, "CapabilityBReadyToEnqueue") ?? TryGetBool(entry, "capabilityBReadyToEnqueue") ?? false,
                    capabilityBRecommendedAction = TryGetString(entry, "CapabilityBRecommendedAction") ?? TryGetString(entry, "capabilityBRecommendedAction"),
                    capabilityBPolicyBlocked = TryGetBool(entry, "CapabilityBPolicyBlocked") ?? TryGetBool(entry, "capabilityBPolicyBlocked") ?? false,
                    capabilityBPolicyBlockReason = TryGetString(entry, "CapabilityBPolicyBlockReason") ?? TryGetString(entry, "capabilityBPolicyBlockReason"),
                    capabilityBPriorityScore = TryGetDouble(entry, "CapabilityBPriorityScore") ?? TryGetDouble(entry, "capabilityBPriorityScore"),
                    capabilityBLastJobStatus = TryGetString(entry, "CapabilityBLastJobStatus") ?? TryGetString(entry, "capabilityBLastJobStatus"),
                    capabilityBLastJobFinishedAt = TryGetString(entry, "CapabilityBLastJobFinishedAt") ?? TryGetString(entry, "capabilityBLastJobFinishedAt"),
                    capabilityBLastJobError = TryGetString(entry, "CapabilityBLastJobError") ?? TryGetString(entry, "capabilityBLastJobError")
                });
            }
        }

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.list", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");

        return new
        {
            total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? rows.Count,
            missingStored = TryGetInt(item.Result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? 0,
            profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0,
            limit = TryGetInt(item.Result, "limit") ?? rows.Count,
            offset = TryGetInt(item.Result, "offset") ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode,
            endOfList = TryGetBool(item.Result, "endOfList"),
            nextLink = TryGetString(item.Result, "nextLink"),
            items = rows
        };
    }

    private static object? BuildExtractionQualityInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_quality" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
                ? summaryElement
                : default;
            var rows = new List<object>();
            if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var signals = ExtractCompactSignals(entry, "signals")
                        .Concat(ExtractCompactSignals(entry, "Signals"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();
                    var retrievalQuality = TryGetObject(entry, "retrievalChunkQuality")
                                           ?? TryGetObject(entry, "RetrievalChunkQuality")
                                           ?? TryGetObject(entry, "retrieval_chunk_quality");
                    object? retrievalChunkQuality = null;
                    if (retrievalQuality.HasValue)
                    {
                        var rejectionReasons = ExtractCompactIntMap(retrievalQuality.Value, "rejectionReasons")
                            .Concat(ExtractCompactIntMap(retrievalQuality.Value, "RejectionReasons"))
                            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                static group => group.Key,
                                static group => group.Sum(static pair => pair.Value),
                                StringComparer.OrdinalIgnoreCase);
                        retrievalChunkQuality = new
                        {
                            totalChunkCount = TryGetInt(retrievalQuality.Value, "totalChunkCount") ?? TryGetInt(retrievalQuality.Value, "TotalChunkCount"),
                            searchableChunkCount = TryGetInt(retrievalQuality.Value, "searchableChunkCount") ?? TryGetInt(retrievalQuality.Value, "SearchableChunkCount"),
                            rejectedChunkCount = TryGetInt(retrievalQuality.Value, "rejectedChunkCount") ?? TryGetInt(retrievalQuality.Value, "RejectedChunkCount"),
                            manualReviewRecommended = TryGetBool(retrievalQuality.Value, "manualReviewRecommended") ?? TryGetBool(retrievalQuality.Value, "ManualReviewRecommended"),
                            rejectionReasons
                        };
                    }

                    rows.Add(new
                    {
                        docId = TryGetString(entry, "docId") ?? TryGetString(entry, "DocId") ?? string.Empty,
                        docPath = (TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty).Replace('\\', '/').TrimStart('/'),
                        documentStatus = TryGetString(entry, "documentStatus") ?? TryGetString(entry, "DocumentStatus") ?? string.Empty,
                        processingRunStatus = TryGetString(entry, "processingRunStatus") ?? TryGetString(entry, "ProcessingRunStatus") ?? string.Empty,
                        documentIndexable = TryGetBool(entry, "documentIndexable") ?? TryGetBool(entry, "DocumentIndexable") ?? true,
                        failureReason = TryGetString(entry, "failureReason") ?? TryGetString(entry, "FailureReason") ?? string.Empty,
                        ocrFailureReason = TryGetString(entry, "ocrFailureReason") ?? TryGetString(entry, "OcrFailureReason") ?? TryGetString(entry, "OCRFailureReason") ?? string.Empty,
                        qualityStatus = TryGetString(entry, "qualityStatus") ?? TryGetString(entry, "QualityStatus") ?? string.Empty,
                        extractionConfidence = TryGetDouble(entry, "extractionConfidence") ?? TryGetDouble(entry, "ExtractionConfidence"),
                        manualReviewRecommended = TryGetBool(entry, "manualReviewRecommended") ?? TryGetBool(entry, "ManualReviewRecommended") ?? false,
                        extractionSource = TryGetString(entry, "extractionSource") ?? TryGetString(entry, "ExtractionSource") ?? string.Empty,
                        ocrAttempted = TryGetBool(entry, "ocrAttempted") ?? TryGetBool(entry, "OcrAttempted") ?? false,
                        ocrApplied = TryGetBool(entry, "ocrApplied") ?? TryGetBool(entry, "OcrApplied") ?? false,
                        ocrRecommended = TryGetBool(entry, "ocrRecommended") ?? TryGetBool(entry, "OcrRecommended") ?? false,
                        ocrLanguages = TryGetString(entry, "ocrLanguages") ?? TryGetString(entry, "OcrLanguages") ?? string.Empty,
                        ocrDurationMs = TryGetLong(entry, "ocrDurationMs") ?? TryGetLong(entry, "OcrDurationMs"),
                        nativeTextStatus = TryGetString(entry, "nativeTextStatus") ?? TryGetString(entry, "NativeTextStatus") ?? string.Empty,
                        nativeOcrRecommended = TryGetBool(entry, "nativeOcrRecommended") ?? TryGetBool(entry, "NativeOcrRecommended"),
                        textStatus = TryGetString(entry, "textStatus") ?? TryGetString(entry, "TextStatus") ?? string.Empty,
                        pageCount = TryGetInt(entry, "pageCount") ?? TryGetInt(entry, "PageCount") ?? 0,
                        textPageCount = TryGetInt(entry, "textPageCount") ?? TryGetInt(entry, "TextPageCount") ?? 0,
                        emptyPageCount = TryGetInt(entry, "emptyPageCount") ?? TryGetInt(entry, "EmptyPageCount") ?? 0,
                        sparsePageCount = TryGetInt(entry, "sparsePageCount") ?? TryGetInt(entry, "SparsePageCount") ?? 0,
                        imagePageCount = TryGetInt(entry, "imagePageCount") ?? TryGetInt(entry, "ImagePageCount") ?? 0,
                        pageWarningCount = TryGetInt(entry, "pageWarningCount") ?? TryGetInt(entry, "PageWarningCount") ?? 0,
                        pageReviewRecommendedCount = TryGetInt(entry, "pageReviewRecommendedCount") ?? TryGetInt(entry, "PageReviewRecommendedCount") ?? 0,
                        retrievalChunkQuality,
                        retrievalTotalChunkCount = TryGetInt(entry, "retrievalTotalChunkCount") ?? TryGetInt(entry, "RetrievalTotalChunkCount"),
                        retrievalSearchableChunkCount = TryGetInt(entry, "retrievalSearchableChunkCount") ?? TryGetInt(entry, "RetrievalSearchableChunkCount"),
                        retrievalRejectedChunkCount = TryGetInt(entry, "retrievalRejectedChunkCount") ?? TryGetInt(entry, "RetrievalRejectedChunkCount"),
                        retrievalManualReviewRecommended = TryGetBool(entry, "retrievalManualReviewRecommended") ?? TryGetBool(entry, "RetrievalManualReviewRecommended"),
                        totalWordCount = TryGetInt(entry, "totalWordCount") ?? TryGetInt(entry, "TotalWordCount"),
                        totalCharCount = TryGetInt(entry, "totalCharCount") ?? TryGetInt(entry, "TotalCharCount"),
                        averageWordsPerPage = TryGetDouble(entry, "averageWordsPerPage") ?? TryGetDouble(entry, "AverageWordsPerPage"),
                        textPageRatio = TryGetDouble(entry, "textPageRatio") ?? TryGetDouble(entry, "TextPageRatio"),
                        signals
                    });
                }
            }

            var categories = new List<object>();
            if (item.Result.TryGetProperty("categories", out var categoryItems) && categoryItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in categoryItems.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var categoryPath = (TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? string.Empty)
                        .Replace('\\', '/')
                        .Trim('/');
                    if (string.IsNullOrWhiteSpace(categoryPath))
                        continue;

                    categories.Add(new
                    {
                        categoryPath,
                        totalDocuments = TryGetInt(entry, "totalDocuments") ?? TryGetInt(entry, "TotalDocuments") ?? 0,
                        okDocuments = TryGetInt(entry, "okDocuments") ?? TryGetInt(entry, "OkDocuments") ?? 0,
                        lowTextDocuments = TryGetInt(entry, "lowTextDocuments") ?? TryGetInt(entry, "LowTextDocuments") ?? 0,
                        emptyTextDocuments = TryGetInt(entry, "emptyTextDocuments") ?? TryGetInt(entry, "EmptyTextDocuments") ?? 0,
                        unknownDocuments = TryGetInt(entry, "unknownDocuments") ?? TryGetInt(entry, "UnknownDocuments") ?? 0,
                        ocrRecommendedDocuments = TryGetInt(entry, "ocrRecommendedDocuments") ?? TryGetInt(entry, "OcrRecommendedDocuments") ?? 0,
                        ocrAppliedDocuments = TryGetInt(entry, "ocrAppliedDocuments") ?? TryGetInt(entry, "OcrAppliedDocuments") ?? 0,
                        manualReviewRecommendedDocuments = TryGetInt(entry, "manualReviewRecommendedDocuments") ?? TryGetInt(entry, "ManualReviewRecommendedDocuments") ?? 0,
                        pageReviewRecommendedPages = TryGetInt(entry, "pageReviewRecommendedPages") ?? TryGetInt(entry, "PageReviewRecommendedPages") ?? 0,
                        pageWarningPages = TryGetInt(entry, "pageWarningPages") ?? TryGetInt(entry, "PageWarningPages") ?? 0,
                        documentsWithRejectedChunks = TryGetInt(entry, "documentsWithRejectedChunks") ?? TryGetInt(entry, "DocumentsWithRejectedChunks") ?? 0,
                        documentsWithNoSearchableChunks = TryGetInt(entry, "documentsWithNoSearchableChunks") ?? TryGetInt(entry, "DocumentsWithNoSearchableChunks") ?? 0,
                        documentsWithRetrievalReviewRecommended = TryGetInt(entry, "documentsWithRetrievalReviewRecommended") ?? TryGetInt(entry, "DocumentsWithRetrievalReviewRecommended") ?? 0
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                limit = TryGetInt(item.Result, "limit") ?? rows.Count,
                summary = new
                {
                    totalDocuments = TryGetInt(summary, "totalDocuments") ?? 0,
                    okDocuments = TryGetInt(summary, "okDocuments") ?? 0,
                    lowTextDocuments = TryGetInt(summary, "lowTextDocuments") ?? 0,
                    emptyTextDocuments = TryGetInt(summary, "emptyTextDocuments") ?? 0,
                    unknownDocuments = TryGetInt(summary, "unknownDocuments") ?? 0,
                    ocrRecommendedDocuments = TryGetInt(summary, "ocrRecommendedDocuments") ?? 0,
                    ocrAttemptedDocuments = TryGetInt(summary, "ocrAttemptedDocuments") ?? 0,
                    ocrAppliedDocuments = TryGetInt(summary, "ocrAppliedDocuments") ?? 0,
                    manualReviewRecommendedDocuments = TryGetInt(summary, "manualReviewRecommendedDocuments") ?? 0,
                    pageReviewRecommendedDocuments = TryGetInt(summary, "pageReviewRecommendedDocuments") ?? 0,
                    pageReviewRecommendedPages = TryGetInt(summary, "pageReviewRecommendedPages") ?? 0,
                    pageWarningDocuments = TryGetInt(summary, "pageWarningDocuments") ?? 0,
                    pageWarningPages = TryGetInt(summary, "pageWarningPages") ?? 0,
                    documentsWithRejectedChunks = TryGetInt(summary, "documentsWithRejectedChunks") ?? 0,
                    documentsWithNoSearchableChunks = TryGetInt(summary, "documentsWithNoSearchableChunks") ?? 0,
                    documentsWithRetrievalReviewRecommended = TryGetInt(summary, "documentsWithRetrievalReviewRecommended") ?? 0,
                    llmEnrichmentPendingDocuments = TryGetInt(summary, "llmEnrichmentPendingDocuments") ?? 0,
                    summaryEnrichmentPendingDocuments = TryGetInt(summary, "summaryEnrichmentPendingDocuments") ?? 0,
                    profileEnrichmentPendingDocuments = TryGetInt(summary, "profileEnrichmentPendingDocuments") ?? 0,
                    contentCardEvidencePendingDocuments = TryGetInt(summary, "contentCardEvidencePendingDocuments") ?? 0
                },
                categories,
                items = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildExtractionPagesInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_pages" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
                ? summaryElement
                : default;
            var rows = new List<object>();
            if (item.Result.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pages.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var signals = ExtractCompactSignals(entry, "signals")
                        .Concat(ExtractCompactSignals(entry, "Signals"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();
                    var unitPreviews = ExtractCompactSignals(entry, "unitPreviews")
                        .Concat(ExtractCompactSignals(entry, "UnitPreviews"))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3)
                        .ToArray();
                    var chunkPreviews = ExtractCompactSignals(entry, "chunkPreviews")
                        .Concat(ExtractCompactSignals(entry, "ChunkPreviews"))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3)
                        .ToArray();
                    rows.Add(new
                    {
                        pageNumber = TryGetInt(entry, "pageNumber") ?? TryGetInt(entry, "PageNumber") ?? 0,
                        qualityStatus = TryGetString(entry, "qualityStatus") ?? TryGetString(entry, "QualityStatus") ?? string.Empty,
                        extractionConfidence = TryGetDouble(entry, "extractionConfidence") ?? TryGetDouble(entry, "ExtractionConfidence"),
                        manualReviewRecommended = TryGetBool(entry, "manualReviewRecommended") ?? TryGetBool(entry, "ManualReviewRecommended") ?? false,
                        charCount = TryGetInt(entry, "charCount") ?? TryGetInt(entry, "CharCount") ?? 0,
                        wordCount = TryGetInt(entry, "wordCount") ?? TryGetInt(entry, "WordCount") ?? 0,
                        imageCount = TryGetInt(entry, "imageCount") ?? TryGetInt(entry, "ImageCount") ?? 0,
                        unitCount = TryGetInt(entry, "unitCount") ?? TryGetInt(entry, "UnitCount") ?? 0,
                        suspiciousUnitCount = TryGetInt(entry, "suspiciousUnitCount") ?? TryGetInt(entry, "SuspiciousUnitCount") ?? 0,
                        chunkCount = TryGetInt(entry, "chunkCount") ?? TryGetInt(entry, "ChunkCount") ?? 0,
                        textStatus = TryGetString(entry, "textStatus") ?? TryGetString(entry, "TextStatus") ?? string.Empty,
                        textEmpty = TryGetBool(entry, "textEmpty") ?? TryGetBool(entry, "TextEmpty") ?? false,
                        textSparse = TryGetBool(entry, "textSparse") ?? TryGetBool(entry, "TextSparse") ?? false,
                        ocrCandidate = TryGetBool(entry, "ocrCandidate") ?? TryGetBool(entry, "OcrCandidate") ?? false,
                        imageOcrStatus = TryGetString(entry, "imageOcrStatus") ?? TryGetString(entry, "ImageOcrStatus") ?? string.Empty,
                        imageOcrReason = TryGetString(entry, "imageOcrReason") ?? TryGetString(entry, "ImageOcrReason") ?? string.Empty,
                        imageOcrWordCount = TryGetInt(entry, "imageOcrWordCount") ?? TryGetInt(entry, "ImageOcrWordCount"),
                        imageOcrCharCount = TryGetInt(entry, "imageOcrCharCount") ?? TryGetInt(entry, "ImageOcrCharCount"),
                        imageOcrExitCode = TryGetInt(entry, "imageOcrExitCode") ?? TryGetInt(entry, "ImageOcrExitCode"),
                        imageOcrTimedOut = TryGetBool(entry, "imageOcrTimedOut") ?? TryGetBool(entry, "ImageOcrTimedOut"),
                        averageCharsPerWord = TryGetDouble(entry, "averageCharsPerWord") ?? TryGetDouble(entry, "AverageCharsPerWord"),
                        signals,
                        unitPreviews,
                        chunkPreviews
                    });
                }
            }

            return new
            {
                docId = TryGetString(item.Result, "docId") ?? TryGetString(item.Result, "DocId") ?? string.Empty,
                docPath = (TryGetString(item.Result, "docPath") ?? TryGetString(item.Result, "DocPath") ?? string.Empty).Replace('\\', '/').TrimStart('/'),
                documentStatus = TryGetString(item.Result, "documentStatus") ?? TryGetString(item.Result, "DocumentStatus") ?? string.Empty,
                processingRunStatus = TryGetString(item.Result, "processingRunStatus") ?? TryGetString(item.Result, "ProcessingRunStatus") ?? string.Empty,
                documentIndexable = TryGetBool(item.Result, "documentIndexable") ?? TryGetBool(item.Result, "DocumentIndexable") ?? true,
                failureReason = TryGetString(item.Result, "failureReason") ?? TryGetString(item.Result, "FailureReason") ?? string.Empty,
                ocrFailureReason =
                    TryGetString(item.Result, "ocrFailureReason")
                    ?? TryGetString(item.Result, "OcrFailureReason")
                    ?? TryGetString(item.Result, "OCRFailureReason")
                    ?? TryGetOcrDiagnosticsString(item.Result, "failureReason"),
                ocrAppliedReason = TryGetOcrDiagnosticsString(item.Result, "appliedReason"),
                ocrMode = TryGetOcrDiagnosticsString(item.Result, "mode"),
                indexedVersion = TryGetInt(item.Result, "indexedVersion") ?? TryGetInt(item.Result, "IndexedVersion"),
                extractionSource = TryGetString(item.Result, "extractionSource") ?? TryGetString(item.Result, "ExtractionSource") ?? string.Empty,
                ocrAttempted = TryGetBool(item.Result, "ocrAttempted") ?? TryGetBool(item.Result, "OcrAttempted") ?? false,
                ocrApplied = TryGetBool(item.Result, "ocrApplied") ?? TryGetBool(item.Result, "OcrApplied") ?? false,
                ocrLanguages = TryGetString(item.Result, "ocrLanguages") ?? TryGetString(item.Result, "OcrLanguages") ?? string.Empty,
                ocrDurationMs = TryGetLong(item.Result, "ocrDurationMs") ?? TryGetLong(item.Result, "OcrDurationMs"),
                summary = new
                {
                    pageCount = TryGetInt(summary, "pageCount") ?? 0,
                    manualReviewRecommendedPages = TryGetInt(summary, "manualReviewRecommendedPages") ?? 0,
                    probableOcrNoisePages = TryGetInt(summary, "probableOcrNoisePages") ?? 0,
                    emptyTextPages = TryGetInt(summary, "emptyTextPages") ?? 0,
                    lowTextPages = TryGetInt(summary, "lowTextPages") ?? 0,
                    indexedByContextPages = TryGetInt(summary, "indexedByContextPages") ?? 0,
                    imagePages = TryGetInt(summary, "imagePages") ?? 0
                },
                pages = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetOcrDiagnosticsString(JsonElement root, string propertyName)
    {
        var diagnostics = TryGetObject(root, "ocrDiagnostics") ?? TryGetObject(root, "OcrDiagnostics");
        return diagnostics is null
            ? null
            : TryGetString(diagnostics.Value, propertyName);
    }

    private static object? BuildCountInventoryData(ToolResults toolResults, string toolName)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == toolName && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? 0
        };
    }

    private static object? BuildEmptyFoldersInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var paths = new List<object>();
            if (item.Result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var path = TryGetString(entry, "path") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(new { path });
                }
            }

            return new
            {
                total = paths.Count,
                items = paths
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildDiagnosticPerformanceInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("memorySummary", out var memorySummary) && memorySummary.ValueKind == JsonValueKind.Object
                ? memorySummary
                : item.Result;

            var workspace = summary.TryGetProperty("workspace", out var workspaceEl) && workspaceEl.ValueKind == JsonValueKind.Object
                ? workspaceEl
                : default;
            var session = summary.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object
                ? sessionEl
                : default;
            var execution = summary.TryGetProperty("execution", out var executionEl) && executionEl.ValueKind == JsonValueKind.Object
                ? executionEl
                : default;
            var persistence = summary.TryGetProperty("persistence", out var persistenceEl) && persistenceEl.ValueKind == JsonValueKind.Object
                ? persistenceEl
                : default;
            var resetPolicy = summary.TryGetProperty("resetPolicy", out var resetEl) && resetEl.ValueKind == JsonValueKind.Object
                ? resetEl
                : default;

            return new
            {
                profile = TryGetString(summary, "profile") ?? "unknown",
                schemaVersion = TryGetInt(summary, "schemaVersion") ?? 0,
                cdcAlignment = TryGetString(summary, "cdcAlignment") ?? "v3.1",
                routerMs = TryGetInt(item.Result, "routerMs") ?? 0,
                toolsMs = TryGetInt(item.Result, "toolsMs") ?? 0,
                writerMs = TryGetInt(item.Result, "writerMs") ?? 0,
                totalMs = TryGetInt(item.Result, "totalMs") ?? 0,
                workspace = new
                {
                    catalogCategoriesCount = TryGetInt(workspace, "catalogCategoriesCount") ?? 0,
                    knownDocumentsCount = TryGetInt(workspace, "knownDocumentsCount") ?? 0,
                    hasCapabilitiesSnapshot = TryGetBoolProp(workspace, "hasCapabilitiesSnapshot") ?? false
                },
                session = new
                {
                    hasFocusedDocument = TryGetBoolProp(session, "hasFocusedDocument") ?? false,
                    lastListedDocumentsCount = TryGetInt(session, "lastListedDocumentsCount") ?? 0,
                    hasResolvedCategory = TryGetBoolProp(session, "hasResolvedCategory") ?? false,
                    hasPendingClarification = TryGetBoolProp(session, "hasPendingClarification") ?? false
                },
                execution = new
                {
                    mode = TryGetString(execution, "mode") ?? "auto",
                    hasRouterIntent = TryGetBoolProp(execution, "hasRouterIntent") ?? false,
                    toolNamesCount = TryGetInt(execution, "toolNamesCount") ?? 0,
                    hasAdminOperation = TryGetBoolProp(execution, "hasAdminOperation") ?? false
                },
                persistence = new
                {
                    language = TryGetBoolProp(persistence, "language") ?? false,
                    style = TryGetBoolProp(persistence, "style") ?? false,
                    mode = TryGetBoolProp(persistence, "mode") ?? false,
                    focusedDocument = TryGetBoolProp(persistence, "focusedDocument") ?? false,
                    resolvedCategory = TryGetBoolProp(persistence, "resolvedCategory") ?? false
                },
                resetPolicy = new
                {
                    preservesM1Lite = TryGetBoolProp(resetPolicy, "preservesM1Lite") ?? false,
                    preservesPreferences = TryGetBoolProp(resetPolicy, "preservesPreferences") ?? false,
                    clearsM3 = TryGetBoolProp(resetPolicy, "clearsM3") ?? false,
                    clearsM6 = TryGetBoolProp(resetPolicy, "clearsM6") ?? false,
                    resetsModeToAuto = TryGetBoolProp(resetPolicy, "resetsModeToAuto") ?? false
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private string BuildStatsFallbackAnswerFromResults(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return BuildStatsFallbackAnswer(item.Result, language);
    }

    private static async Task EmitDeterministicTextAsync(string text, Action<string>? onDelta, CancellationToken ct)
    {
        if (onDelta is null)
            return;

        foreach (var chunk in SplitDeterministicTextForDelivery(text))
        {
            ct.ThrowIfCancellationRequested();
            onDelta(chunk);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> SplitDeterministicTextForDelivery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return new[] { text };
    }

    private static string BuildSourceResolveAnswer(ToolMemory.SourceRef src, string language, out object payload)
    {
        var dp = (src.DocPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var page = src.PageStart > 0 ? src.PageStart : 0;
        var label = AppendPageToOpenTokenLabel((src.Label ?? string.Empty).Trim(), page, language);
        var payloadSource = new ToolMemory.SourceRef
        {
            DocId = src.DocId,
            DocPath = dp,
            PageStart = src.PageStart,
            PageEnd = src.PageEnd,
            Label = label,
            SourceHash = src.SourceHash,
            DocLanguage = src.DocLanguage,
            ProfileLanguage = src.ProfileLanguage,
            CategoryRef = src.CategoryRef,
            CategoryPath = src.CategoryPath,
            ChunkId = src.ChunkId,
            ExtractionSource = src.ExtractionSource,
            DocumentQualityStatus = src.DocumentQualityStatus,
            PageQualityStatus = src.PageQualityStatus,
            TextStatus = src.TextStatus,
            ChunkTextStatus = src.ChunkTextStatus,
            ChunkTextSparse = src.ChunkTextSparse,
            ChunkOcrCandidate = src.ChunkOcrCandidate,
            QualityStatus = src.QualityStatus,
            ExtractionConfidence = src.ExtractionConfidence,
            DocumentExtractionConfidence = src.DocumentExtractionConfidence,
            PageExtractionConfidence = src.PageExtractionConfidence,
            ManualReviewRecommended = src.ManualReviewRecommended,
            DocumentManualReviewRecommended = src.DocumentManualReviewRecommended,
            PageManualReviewRecommended = src.PageManualReviewRecommended,
            OcrAttempted = src.OcrAttempted,
            OcrApplied = src.OcrApplied,
            OcrRecommended = src.OcrRecommended,
            QualitySignals = src.QualitySignals.ToList(),
            ChunkQualitySignals = src.ChunkQualitySignals.ToList(),
            MatchedContentCards = src.MatchedContentCards.ToList(),
            SelectionHintEvidenceRole = src.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = src.SelectionHintActionabilityScore,
            SelectionHintSupportScore = src.SelectionHintSupportScore,
            SelectionHintFragmentScore = src.SelectionHintFragmentScore,
            SelectionHintNavigationScore = src.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = src.SelectionHintQualityPenalty,
            ContentRole = src.ContentRole,
            NavigationReason = src.NavigationReason,
            RetrievalNavigationScore = src.RetrievalNavigationScore,
            ContentDensityScore = src.ContentDensityScore
        };
        payload = BuildSourcesPayload([payloadSource]);

        var heading = DeterministicAgentText.SourceHeading(language);
        return $"{heading}:\n1. [[open|{dp}|{page}|{label}]]";
    }

    private static string SanitizeOpenTokenLabel(string? label)
    {
        var safe = (label ?? string.Empty).Trim();
        if (safe.Length == 0)
            return string.Empty;

        return safe
            .Replace("|", " ")
            .Replace("[", "(")
            .Replace("]", ")");
    }

    private static string AppendPageToOpenTokenLabel(string? label, int? page, string language)
    {
        var safe = SanitizeOpenTokenLabel(label);
        if (string.IsNullOrWhiteSpace(safe))
            return safe;

        if (Regex.IsMatch(safe, @"(?i)\b(?:p\.?|page|s\.)\s*\d+\b", RegexOptions.CultureInvariant))
            return safe;

        if (page is not { } rawPage || rawPage <= 0)
            return safe;

        var safePage = Math.Max(1, rawPage);
        return $"{safe} ({SourceBackedPagePrefix(language)}{safePage})";
    }


    private string TryBuildDocumentsCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.count" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var total = TryGetInt(item.Result, "total") ?? 0;
        return DeterministicAgentText.DocumentsCount(total, language);
    }

    private string TryBuildEmptyFoldersCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_count" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var total = TryGetInt(item.Result, "total") ?? 0;
        return DeterministicAgentText.EmptyFoldersCount(total, language);
    }

    private string TryBuildEmptyFoldersListAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!item.Result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var paths = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "path") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.EmptyFoldersHeader(language));

        for (var i = 0; i < paths.Count; i++)
            sb.AppendLine($"{i + 1}. {paths[i]}");

        return sb.ToString().TrimEnd();
    }

    private string TryBuildSummaryStatusCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0;
        var profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0;
        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        if (profileMissing > 0 && total > 0)
            return DeterministicAgentText.MissingSummariesCount(total, language)
                + Environment.NewLine
                + DeterministicAgentText.BackofficeProfilesMissingCount(profileMissing, language);

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private string TryBuildSummaryStatusListAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var hasItems = item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = item.Result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            return string.Empty;

        var rows = new List<(string path, string state, bool profileMissing)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty;
            var state = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? string.Empty;
            var profileState = TryGetString(entry, "CapabilityBProfileState") ?? TryGetString(entry, "capabilityBProfileState") ?? string.Empty;
            var hasBackofficeProfile = TryGetBool(entry, "CapabilityBHasBackofficeProfile") ?? TryGetBool(entry, "capabilityBHasBackofficeProfile");
            var profileMissing = string.Equals(profileState, "missing", StringComparison.OrdinalIgnoreCase)
                || (hasBackofficeProfile.HasValue && !hasBackofficeProfile.Value && HasReason(entry, "profile_missing"));
            if (!string.IsNullOrWhiteSpace(path))
                rows.Add((path, state, profileMissing));
        }

        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.list", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");
        if (rows.Count == 0)
            return string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var sb = new StringBuilder();
        sb.AppendLine(string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
            ? DeterministicAgentText.StoredSummariesHeader(language)
            : DeterministicAgentText.MissingSummariesHeader(language));
        for (var i = 0; i < rows.Count; i++)
        {
            var suffix = rows[i].state.Equals("stale", StringComparison.OrdinalIgnoreCase)
                ? $" {DeterministicAgentText.SummaryStatusStaleSuffix(language)}"
                : string.Empty;
            if (rows[i].profileMissing)
                suffix += $" [{DeterministicAgentText.BackofficeProfileMissingSuffix(language)}]";
            sb.AppendLine($"{i + 1}. {rows[i].path}{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderDiagnosticPerformanceFromReplayData(JsonElement data, string language)
    {
        language = NormalizeLanguageCode(language);
        var profile = TryGetString(data, "profile") ?? "unknown";
        var schemaVersion = TryGetInt(data, "schemaVersion") ?? 0;
        var cdcAlignment = TryGetString(data, "cdcAlignment") ?? "v3.1";
        var routerMs = TryGetInt(data, "routerMs") ?? 0;
        var toolsMs = TryGetInt(data, "toolsMs") ?? 0;
        var writerMs = TryGetInt(data, "writerMs") ?? 0;
        var totalMs = TryGetInt(data, "totalMs") ?? 0;

        var workspace = data.TryGetProperty("workspace", out var workspaceEl) && workspaceEl.ValueKind == JsonValueKind.Object
            ? workspaceEl
            : default;
        var session = data.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object
            ? sessionEl
            : default;
        var execution = data.TryGetProperty("execution", out var executionEl) && executionEl.ValueKind == JsonValueKind.Object
            ? executionEl
            : default;
        var persistence = data.TryGetProperty("persistence", out var persistenceEl) && persistenceEl.ValueKind == JsonValueKind.Object
            ? persistenceEl
            : default;
        var resetPolicy = data.TryGetProperty("resetPolicy", out var resetEl) && resetEl.ValueKind == JsonValueKind.Object
            ? resetEl
            : default;

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceHeader(language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceProfile(profile, schemaVersion, cdcAlignment, language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceTimings(routerMs, toolsMs, writerMs, totalMs, language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceWorkspace(
            TryGetInt(workspace, "catalogCategoriesCount") ?? 0,
            TryGetInt(workspace, "knownDocumentsCount") ?? 0,
            TryGetBoolProp(workspace, "hasCapabilitiesSnapshot") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceSession(
            TryGetBoolProp(session, "hasFocusedDocument") ?? false,
            TryGetInt(session, "lastListedDocumentsCount") ?? 0,
            TryGetBoolProp(session, "hasResolvedCategory") ?? false,
            TryGetBoolProp(session, "hasPendingClarification") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceExecution(
            TryGetString(execution, "mode") ?? "auto",
            TryGetBoolProp(execution, "hasRouterIntent") ?? false,
            TryGetInt(execution, "toolNamesCount") ?? 0,
            TryGetBoolProp(execution, "hasAdminOperation") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformancePersistence(
            TryGetBoolProp(persistence, "language") ?? false,
            TryGetBoolProp(persistence, "style") ?? false,
            TryGetBoolProp(persistence, "mode") ?? false,
            TryGetBoolProp(persistence, "focusedDocument") ?? false,
            TryGetBoolProp(persistence, "resolvedCategory") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceReset(
            TryGetBoolProp(resetPolicy, "preservesM1Lite") ?? false,
            TryGetBoolProp(resetPolicy, "preservesPreferences") ?? false,
            TryGetBoolProp(resetPolicy, "clearsM3") ?? false,
            TryGetBoolProp(resetPolicy, "clearsM6") ?? false,
            TryGetBoolProp(resetPolicy, "resetsModeToAuto") ?? false,
            language));
        return sb.ToString().TrimEnd();
    }

    private static string InjectInlineSources(string answer, List<ToolMemory.SourceRef> sources, string language)
    {
        if (sources is null || sources.Count == 0) return answer;
        var visibleSources = MergeSourceRefsByPagePreservingOrder(sources);
        if (visibleSources.Count == 0) return answer;

        answer = RemoveTrailingModelEmittedSourceList(answer);

        // If the LLM already emitted clickable tokens, do not add more.
        if (answer.Contains("[[open|", StringComparison.OrdinalIgnoreCase))
            return answer;

        var heading = DeterministicAgentText.SourceHeading(language);

        var sb = new StringBuilder();
        sb.AppendLine(answer.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"{heading}:");

        for (var i = 0; i < visibleSources.Count; i++)
        {
            var s = visibleSources[i];
            var dp = (s.DocPath ?? "").Replace('\\', '/').TrimStart('/');
            var mainCat = "";
            var slash = dp.IndexOf('/');
            if (slash > 0) mainCat = dp.Substring(0, slash);

            var label = (s.Label ?? "").Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                var fn = Path.GetFileName(dp);
                label = string.IsNullOrWhiteSpace(mainCat) ? fn : $"{fn} ({mainCat})";
            }

            var page = s.PageStart > 0 ? s.PageStart : 0;
            label = AppendPageToOpenTokenLabel(label, page, language);

            sb.AppendLine($"{i + 1}. [[open|{dp}|{page}|{label}]]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RemoveTrailingModelEmittedSourceList(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return answer;

        var trimmed = answer.TrimEnd();
        var naturalSourceHeadingPattern = @"(?im)^\s*(?:sources?\s+(?:used|cited|consulted)|sources?\s+(?:utilisees?|citees?|consultees?)|fuentes\s+(?:usadas?|utilizadas?|consultadas?)|fontes\s+(?:usadas?|utilizadas?|consultadas?)|verwendete\s+quellen|genutzte\s+quellen|zitierte\s+quellen|fonti\s+(?:consultate|citate|usate))\s*:\s*.*$";
        var naturalMatches = Regex.Matches(trimmed, naturalSourceHeadingPattern, RegexOptions.CultureInvariant);
        if (naturalMatches.Count > 0)
        {
            var naturalMatch = naturalMatches[^1];
            var naturalWithoutInlineSourceSection = RemoveModelEmittedSourceLinesFromFinalBlock(trimmed, naturalMatch);
            if (!string.Equals(naturalWithoutInlineSourceSection, trimmed, StringComparison.Ordinal))
                return naturalWithoutInlineSourceSection.TrimEnd();

            var naturalTrailing = trimmed[naturalMatch.Index..];
            var naturalSourceLineCount = Regex.Matches(
                naturalTrailing,
                $@"(?im)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|[^\r\n]+|[^\r\n]*(?:{SourceReferenceExtensionRegex}|p\.?\s*\d+|page\s+\d+)[^\r\n]*)\s*$",
                RegexOptions.CultureInvariant).Count;
            var naturalNonEmptyLineCount = Regex.Matches(naturalTrailing, @"(?m)^\s*\S.*$", RegexOptions.CultureInvariant).Count;
            if (naturalSourceLineCount >= 1 && naturalNonEmptyLineCount <= naturalSourceLineCount + 1)
                return trimmed[..naturalMatch.Index].TrimEnd();
        }
        var sourceHeadingPattern = @"(?im)^\s*(?:source|sources|references?|r[eé]f[eé]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)(?:\s+(?:used|cited|consulted|utilis[eé]es?|cit[eé]es?|consult[eé]es?|usadas?|utilizadas?|consultadas?|verwendete|consultate|citate))?\s*:\s*.*$";
        var matches = Regex.Matches(trimmed, sourceHeadingPattern, RegexOptions.CultureInvariant);
        if (matches.Count == 0)
            return trimmed;

        var match = matches[^1];
        var trailing = trimmed[match.Index..];
        var sourceLineCount = Regex.Matches(
            trailing,
            $@"(?im)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|[^\r\n]+|[^\r\n]*(?:{SourceReferenceExtensionRegex}|p\.?\s*\d+|page\s+\d+)[^\r\n]*)\s*$",
            RegexOptions.CultureInvariant).Count;
        var nonEmptyLineCount = Regex.Matches(trailing, @"(?m)^\s*\S.*$", RegexOptions.CultureInvariant).Count;

        if (sourceLineCount >= 1 && nonEmptyLineCount <= sourceLineCount + 1)
            return trimmed[..match.Index].TrimEnd();

        var withoutInlineSourceSection = RemoveModelEmittedSourceLinesFromFinalBlock(trimmed, match);
        if (!string.Equals(withoutInlineSourceSection, trimmed, StringComparison.Ordinal))
            return withoutInlineSourceSection.TrimEnd();

        return trimmed;
    }

    private static string RemoveModelEmittedSourceLinesFromFinalBlock(string text, Match sourceHeadingMatch)
    {
        var trailingLength = text.Length - sourceHeadingMatch.Index;
        if (trailingLength > 1600)
            return text;

        var before = text[..sourceHeadingMatch.Index].TrimEnd();
        var trailing = text[sourceHeadingMatch.Index..];
        var lines = trailing.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length <= 1)
            return text;

        var sourceLinePattern =
            $@"(?i)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|.+|.*(?:{SourceReferenceExtensionRegex}|\(?\s*p\.?\s*\d+\s*\)?|page\s+\d+).*)\s*$";
        var sourceHeadingLinePattern =
            @"(?i)^\s*(?:source|sources|references?|r[eé]f[eé]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)(?:\s+(?:used|cited|consulted|utilis[eé]es?|cit[eé]es?|consult[eé]es?|usadas?|utilizadas?|consultadas?|verwendete|consultate|citate))?\s*:\s*$";
        var naturalSourceHeadingLinePattern =
            @"(?i)^\s*(?:sources?\s+(?:used|cited|consulted)|sources?\s+(?:utilisees?|citees?|consultees?)|fuentes\s+(?:usadas?|utilizadas?|consultadas?)|fontes\s+(?:usadas?|utilizadas?|consultadas?)|verwendete\s+quellen|genutzte\s+quellen|zitierte\s+quellen|fonti\s+(?:consultate|citate|usate))\s*:\s*$";
        var removedSourceLines = 0;
        var firstKeptLine = -1;
        var passedHeading = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!passedHeading)
            {
                if (Regex.IsMatch(line, sourceHeadingLinePattern, RegexOptions.CultureInvariant)
                    || Regex.IsMatch(line, naturalSourceHeadingLinePattern, RegexOptions.CultureInvariant))
                {
                    passedHeading = true;
                    continue;
                }

                continue;
            }

            if (Regex.IsMatch(line, sourceLinePattern, RegexOptions.CultureInvariant))
            {
                removedSourceLines++;
                continue;
            }

            firstKeptLine = i;
            break;
        }

        if (removedSourceLines == 0)
            return text;

        var keptAfter = firstKeptLine >= 0
            ? string.Join(Environment.NewLine, lines.Skip(firstKeptLine)).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(keptAfter))
            return before;

        return $"{before}{Environment.NewLine}{Environment.NewLine}{keptAfter}";
    }

    private bool ShouldRunDocumentaryProbe(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;

        var s = (effectiveUserMessage ?? string.Empty).Trim();
        if (s.Length < 12)
            return false;

        if (Regex.IsMatch(s, @"^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        if (LooksLikeSourceBackedActionRequest(s))
            return false;
        if (LooksLikeComparativeDocumentaryRequest(s))
            return false;
        if (LooksLikeSourceBackedAdaptationRequest(s))
            return false;

        return Regex.IsMatch(s, @"\b(?:qu['’]est\s*ce\s+que\s+tu\s+peux\s+me\s+dire|que\s+peux\s*tu\s+me\s+dire|parle\s*[- ]?moi|au\s+sujet\s+de|a\s+propos\s+de|à\s+propos\s+de|what\s+can\s+you\s+tell\s+me|tell\s+me\s+about|about\s+the|regarding|concerning)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || s.Contains("?", StringComparison.Ordinal)
            || LooksLikeBroadDocumentaryInformationRequest(s);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, string? routerIntent, IReadOnlyList<string> toolNames, bool clearPendingClarification)> TryHandleDocumentaryProbeAsync(
        string effectiveUserMessage,
        RouterPlan plan,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldRunDocumentaryProbe(effectiveUserMessage, plan))
            return (false, string.Empty, null, null, Array.Empty<string>(), true);

        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));

            var probeCategory = string.IsNullOrWhiteSpace(_mem.LastResolvedCategory?.CategoryPath)
                ? null
                : _mem.LastResolvedCategory!.CategoryPath;
            var retrieval = await RunDocumentaryProbeRetrievalAsync(
                effectiveUserMessage,
                plan.Language,
                probeCategory,
                ct).ConfigureAwait(false);
            var probeToolResults = retrieval.ToolResults;
            var hits = SelectDocumentaryProbeClarificationHits(probeToolResults);

            if (hits.Count == 0)
                return (false, string.Empty, null, null, Array.Empty<string>(), true);

            var probeExpansionPlan = new RouterPlan
            {
                Intent = "rag.answer",
                Language = plan.Language,
                Mode = plan.Mode
            };
            var probeShouldTryBroaderExploration = ShouldDeferDocumentaryProbeWriterForBroaderExploration(
                probeToolResults,
                effectiveUserMessage,
                plan.Language);

            var sourcePolicyGuard = TryBuildSourcePolicyGuardAnswer(probeToolResults, effectiveUserMessage, plan.Language);
            if (!string.IsNullOrWhiteSpace(sourcePolicyGuard))
            {
                var guardSources = LooksLikeDocumentInstructionPolicyRequest(effectiveUserMessage)
                    ? new List<ToolMemory.SourceRef>()
                    : DeriveSourcesFromRagHits(probeToolResults).Take(5).ToList();
                if (guardSources.Count > 0)
                {
                    _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(guardSources);
                    sourcePolicyGuard = InjectInlineSources(sourcePolicyGuard, guardSources, plan.Language);
                }

                var guardPayload = guardSources.Count > 0
                    ? BuildSourcesPayload("rag_probe", guardSources)
                    : null;
                await EmitDeterministicTextAsync(sourcePolicyGuard, onDelta, ct).ConfigureAwait(false);
                onProgress?.Invoke(string.Empty);
                return (true, sourcePolicyGuard, guardPayload, "rag.answer", retrieval.ToolNames, true);
            }

            if (!probeShouldTryBroaderExploration
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage)
                && ShouldUseWriterForDocumentaryProbeAnswer(probeToolResults, effectiveUserMessage))
            {
                try
                {
                    var writerPlan = new RouterPlan
                    {
                        Intent = "rag.answer",
                        Language = plan.Language,
                        Mode = plan.Mode
                    };
                    var writerAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                        Array.Empty<(string role, string content)>(),
                        effectiveUserMessage,
                        writerPlan,
                        probeToolResults,
                        ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(writerAnswer))
                    {
                        var writerSources = DeriveSourcesFromRagHits(probeToolResults).Take(8).ToList();
                        if (writerSources.Count == 0)
                            writerSources = DeriveSourcesFromExtractiveHits(probeToolResults, effectiveUserMessage);
                        if (TryFinalizeSourceBackedPlanningResponse(
                                writerAnswer,
                                probeToolResults,
                                effectiveUserMessage,
                                plan.Language,
                                out var finalizedProbeWriterAnswer,
                                out var finalizedProbeWriterSources,
                                out var finalizedProbeWriterAnalysis,
                                out var finalizedProbeWriterResolution))
                        {
                            ClientLog.Info(
                                "ToolAgent planning documentary probe writer finalizer: " +
                                $"resolution={finalizedProbeWriterResolution} " +
                                $"items={finalizedProbeWriterAnalysis.ItemCount} " +
                                $"supported={finalizedProbeWriterAnalysis.SupportedItemCount} " +
                                $"unsupported={finalizedProbeWriterAnalysis.UnsupportedItemCount} " +
                                $"candidates={finalizedProbeWriterAnalysis.CandidateCount} " +
                                $"sources={finalizedProbeWriterSources.Count}");
                            LogSourceBackedPlanningTrace(
                                "documentary-probe-writer-finalizer",
                                probeToolResults,
                                effectiveUserMessage,
                                plan.Language);
                            writerAnswer = finalizedProbeWriterAnswer;
                            writerSources = finalizedProbeWriterSources;
                        }
                        if (writerSources.Count > 0)
                        {
                            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(writerSources);
                            writerAnswer = InjectInlineSources(writerAnswer, writerSources, plan.Language);
                        }

                        var writerPayload = writerSources.Count > 0
                            ? BuildSourcesPayload("rag_probe", writerSources)
                            : null;
                        await EmitDeterministicTextAsync(writerAnswer, onDelta, ct).ConfigureAwait(false);
                        onProgress?.Invoke(string.Empty);
                        return (true, writerAnswer, writerPayload, "rag.answer", retrieval.ToolNames, true);
                    }
                }
                catch
                {
                    // Fall through to deterministic probe handling; probe answers should not fail just because the writer could not be used.
                }
            }

            if (probeShouldTryBroaderExploration)
            {
                await TryExpandSourceBackedEvidenceRetrievalAsync(
                        probeToolResults,
                        probeExpansionPlan,
                        effectiveUserMessage,
                        ct,
                        onPhase,
                        onProgress,
                        probeCategory)
                    .ConfigureAwait(false);

                if (ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage)
                    && ShouldUseWriterForDocumentaryProbeAnswer(probeToolResults, effectiveUserMessage))
                {
                    try
                    {
                        var writerAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                            Array.Empty<(string role, string content)>(),
                            effectiveUserMessage,
                            probeExpansionPlan,
                            probeToolResults,
                            ct).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(writerAnswer))
                        {
                            var writerSources = DeriveSourcesFromRagHits(probeToolResults).Take(8).ToList();
                            if (writerSources.Count == 0)
                                writerSources = DeriveSourcesFromExtractiveHits(probeToolResults, effectiveUserMessage);
                            if (TryFinalizeSourceBackedPlanningResponse(
                                    writerAnswer,
                                    probeToolResults,
                                    effectiveUserMessage,
                                    plan.Language,
                                    out var finalizedExpandedWriterAnswer,
                                    out var finalizedExpandedWriterSources,
                                    out var finalizedExpandedWriterAnalysis,
                                    out var finalizedExpandedWriterResolution))
                            {
                                ClientLog.Info(
                                    "ToolAgent planning documentary probe expanded writer finalizer: " +
                                    $"resolution={finalizedExpandedWriterResolution} " +
                                    $"items={finalizedExpandedWriterAnalysis.ItemCount} " +
                                    $"supported={finalizedExpandedWriterAnalysis.SupportedItemCount} " +
                                    $"unsupported={finalizedExpandedWriterAnalysis.UnsupportedItemCount} " +
                                    $"candidates={finalizedExpandedWriterAnalysis.CandidateCount} " +
                                    $"sources={finalizedExpandedWriterSources.Count}");
                                LogSourceBackedPlanningTrace(
                                    "documentary-probe-expanded-writer-finalizer",
                                    probeToolResults,
                                    effectiveUserMessage,
                                    plan.Language);
                                writerAnswer = finalizedExpandedWriterAnswer;
                                writerSources = finalizedExpandedWriterSources;
                            }
                            if (writerSources.Count > 0)
                            {
                                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(writerSources);
                                writerAnswer = InjectInlineSources(writerAnswer, writerSources, plan.Language);
                            }

                            var writerPayload = writerSources.Count > 0
                                ? BuildSourcesPayload("rag_probe", writerSources)
                                : null;
                            var expandedToolNames = probeToolResults.Items
                                .Select(static item => item.ToolName)
                                .Where(static name => !string.IsNullOrWhiteSpace(name))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            await EmitDeterministicTextAsync(writerAnswer, onDelta, ct).ConfigureAwait(false);
                            onProgress?.Invoke(string.Empty);
                            return (true, writerAnswer, writerPayload, "rag.answer", expandedToolNames, true);
                        }
                    }
                    catch
                    {
                        // Fall through to deterministic probe handling; broad exploration should not make the probe path fragile.
                    }
                }
            }

            var probeRequiresWriterForBroadFinal =
                ShouldRequireWriterForBroadDocumentaryFinal(probeToolResults, effectiveUserMessage, plan.Language);
            var sourceBackedAnswer = probeRequiresWriterForBroadFinal
                ? BuildSourceBackedSafeFallbackAnswer(
                    probeToolResults,
                    effectiveUserMessage,
                    plan.Language,
                    shouldAvoidRaw: true)
                : ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, probeToolResults)
                    ? BuildSourceBackedExtractiveAnswer(probeToolResults, effectiveUserMessage, plan.Language)
                    : string.Empty;
            if (probeRequiresWriterForBroadFinal
                && !string.IsNullOrWhiteSpace(sourceBackedAnswer)
                && LooksLikePoorPlanningFallbackAnswer(
                    sourceBackedAnswer,
                    ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage)))
            {
                sourceBackedAnswer = string.Empty;
            }
            var sourceBackedSources = probeRequiresWriterForBroadFinal
                ? DeriveSourcesForSourceBackedFallback(probeToolResults, effectiveUserMessage)
                : DeriveSourcesFromExtractiveHits(probeToolResults, effectiveUserMessage);
            if (!string.IsNullOrWhiteSpace(sourceBackedAnswer) && sourceBackedSources.Count > 0)
            {
                if (TryFinalizeSourceBackedPlanningResponse(
                        sourceBackedAnswer,
                        probeToolResults,
                        effectiveUserMessage,
                        plan.Language,
                        out var finalizedProbeFallbackAnswer,
                        out var finalizedProbeFallbackSources,
                        out var finalizedProbeFallbackAnalysis,
                        out var finalizedProbeFallbackResolution))
                {
                    ClientLog.Info(
                        "ToolAgent planning documentary probe fallback finalizer: " +
                        $"resolution={finalizedProbeFallbackResolution} " +
                        $"items={finalizedProbeFallbackAnalysis.ItemCount} " +
                        $"supported={finalizedProbeFallbackAnalysis.SupportedItemCount} " +
                        $"unsupported={finalizedProbeFallbackAnalysis.UnsupportedItemCount} " +
                        $"candidates={finalizedProbeFallbackAnalysis.CandidateCount} " +
                        $"sources={finalizedProbeFallbackSources.Count}");
                    LogSourceBackedPlanningTrace(
                        "documentary-probe-fallback-finalizer",
                        probeToolResults,
                        effectiveUserMessage,
                        plan.Language);
                    sourceBackedAnswer = finalizedProbeFallbackAnswer;
                    sourceBackedSources = finalizedProbeFallbackSources;
                }
                object? sourceBackedPayload = null;
                if (sourceBackedSources.Count > 0)
                {
                    _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sourceBackedSources);
                    sourceBackedAnswer = InjectInlineSources(sourceBackedAnswer, sourceBackedSources, plan.Language);
                    sourceBackedPayload = BuildSourcesPayload(sourceBackedSources);
                }
                else
                {
                    _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
                }
                await EmitDeterministicTextAsync(sourceBackedAnswer, onDelta, ct).ConfigureAwait(false);
                onProgress?.Invoke(string.Empty);
                return (true, sourceBackedAnswer, sourceBackedPayload, "rag.answer", retrieval.ToolNames, true);
            }

            var answer = BuildDocumentaryProbeClarification(hits, plan.Language);
            var sourcesPayload = BuildSourcesPayload(
                "rag_probe",
                DeriveSourcesFromRagHits(probeToolResults));
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification("rag_probe", effectiveUserMessage, "documentary_probe", plan.Language);
            onProgress?.Invoke(string.Empty);
            return (true, answer, sourcesPayload, "rag.followup", retrieval.ToolNames, false);
        }
        catch
        {
            return (false, string.Empty, null, null, Array.Empty<string>(), true);
        }
    }

    private static bool ShouldDeferDocumentaryProbeWriterForBroaderExploration(
        ToolResults probeToolResults,
        string effectiveUserMessage,
        string language)
    {
        var probeExpansionQuery = ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage);
        var probeExpansionAnalysis = AnalyzeSourceBackedEvidenceSufficiency(
            probeToolResults,
            probeExpansionQuery,
            language);
        return ShouldAllowSourceBackedBroadResearchPass(
                   probeExpansionQuery,
                   probeExpansionAnalysis,
                   IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage))
               || IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage)
               || ShouldRequireWriterForBroadDocumentaryFinal(probeToolResults, effectiveUserMessage, language);
    }

    private async Task<(ToolResults ToolResults, IReadOnlyList<string> ToolNames)> RunDocumentaryProbeRetrievalAsync(
        string effectiveUserMessage,
        string language,
        string? categoryScope,
        CancellationToken ct)
    {
        static ToolResults MergeProbeToolResults(ToolResults current, ToolResults next)
        {
            var merged = new ToolResults();
            merged.Items.AddRange(current.Items);
            merged.Items.AddRange(next.Items);
            return merged;
        }

        var topK = ResolveDocumentaryProbeTopK(effectiveUserMessage);
        var useResearchSurfaces = ShouldUseResearchSurfacesForBroadRagRequest(effectiveUserMessage);
        var skipDirectProbe = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var bestToolResults = new ToolResults();
        if (!skipDirectProbe)
        {
            var initialArgs = CreateJsonArgs(new
            {
                query = effectiveUserMessage,
                topK,
                categoryPath = categoryScope,
                mode = useResearchSurfaces ? "broad" : "balanced",
                researchMode = useResearchSurfaces ? "source_exploration" : null,
                includeResearchSurfaces = useResearchSurfaces ? true : (bool?)null
            });
            var initialResult = await TryExecRagSearchOrEmptyAsync(initialArgs, ct).ConfigureAwait(false);
            bestToolResults = BuildProbeRagToolResults(initialResult, "rag.search");
        }

        if (ShouldExpandDocumentaryProbeRetrieval(bestToolResults, effectiveUserMessage, language))
        {
            var expandedQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage);
            if (expandedQueries.Length > 0)
            {
                var expandedArgs = CreateJsonArgs(new
                {
                    queries = expandedQueries,
                    topK,
                    categoryPath = categoryScope,
                    mode = "broad",
                    researchMode = "source_exploration",
                    includeResearchSurfaces = true
                });
                var expandedResult = await TryExecRagMultiSearchOrEmptyAsync(expandedArgs, ct).ConfigureAwait(false);
                var expandedToolResults = BuildProbeRagToolResults(expandedResult, "rag.multi_search");
                var mergedToolResults = MergeProbeToolResults(bestToolResults, expandedToolResults);
                if (IsBetterDocumentaryProbeCoverage(bestToolResults, mergedToolResults, effectiveUserMessage, language))
                {
                    bestToolResults = mergedToolResults;
                }
                else if (IsBetterDocumentaryProbeCoverage(bestToolResults, expandedToolResults, effectiveUserMessage, language))
                {
                    bestToolResults = expandedToolResults;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(categoryScope)
            && ShouldExpandDocumentaryProbeRetrieval(bestToolResults, effectiveUserMessage, language))
        {
            var unscopedQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage);
            if (unscopedQueries.Length > 0)
            {
                var unscopedArgs = CreateJsonArgs(new
                {
                    queries = unscopedQueries,
                    topK,
                    categoryPath = (string?)null,
                    mode = "broad",
                    researchMode = "source_exploration",
                    includeResearchSurfaces = true
                });
                var unscopedResult = await TryExecRagMultiSearchOrEmptyAsync(unscopedArgs, ct).ConfigureAwait(false);
                var unscopedToolResults = BuildProbeRagToolResults(unscopedResult, "rag.multi_search");
                var mergedToolResults = MergeProbeToolResults(bestToolResults, unscopedToolResults);
                if (IsBetterDocumentaryProbeCoverage(bestToolResults, mergedToolResults, effectiveUserMessage, language))
                {
                    bestToolResults = mergedToolResults;
                }
                else if (IsBetterDocumentaryProbeCoverage(bestToolResults, unscopedToolResults, effectiveUserMessage, language))
                {
                    bestToolResults = unscopedToolResults;
                }
            }
        }

        var toolNames = bestToolResults.Items
            .Select(static item => item.ToolName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (bestToolResults, toolNames.Length == 0 ? new[] { "rag.search" } : toolNames);
    }

    private static int ResolveDocumentaryProbeTopK(string query)
        => LooksLikeAnyDocumentaryPlanningRequest(query)
            ? NormalizeSourceBackedPlanningTopK(null, query)
            : LooksLikeBroadSynthesisRequestShape(query)
              || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
              || LooksLikeMultipleCandidateSynthesisRequest(query)
              || LooksLikeSoftChoiceRecommendationRequest(query)
              || LooksLikeSourceBackedPairingRecommendationRequest(query)
                ? 12
                : 8;

    private static bool ShouldExpandDocumentaryProbeRetrieval(ToolResults toolResults, string? query, string language)
    {
        if (!toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return ShouldExpandSourceBackedPlanningRetrieval(toolResults, query, language);

        var coverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (coverage.UsableHitCount == 0)
            return true;

        if (LooksLikeDocumentContentSelectionExplanationRequest(query))
            return coverage.DistinctDocumentCount < 3;

        if (LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query))
        {
            return !coverage.IsAdequate;
        }

        return coverage.UsableHitCount < 2
               && coverage.RichEvidenceCount == 0
               && coverage.DistinctDocumentCount < 2;
    }

    private static bool ShouldUseWriterForDocumentaryProbeAnswer(ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return false;
        }

        var coverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (coverage.UsableHitCount == 0)
            return false;

        if (LooksLikeDocumentContentSelectionExplanationRequest(query))
            return coverage.DistinctDocumentCount >= 3
                && coverage.UsableHitCount >= 3;

        if (ShouldAvoidRawSourceBackedFallback(query)
            || ShouldRequireWriterForBroadDocumentaryFinal(toolResults, query)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query))
        {
            return coverage.RichEvidenceCount >= 1
                   || coverage.DistinctSourcePageCount >= 2
                   || coverage.UsableHitCount >= 2;
        }

        return ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query);
    }

    private static bool IsBetterDocumentaryProbeCoverage(
        ToolResults current,
        ToolResults candidate,
        string? query,
        string language)
    {
        if (!candidate.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return false;
        if (!current.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

        return IsBetterSourceBackedEvidenceCoverage(current, candidate, query, language);
    }

    private static string[] BuildDocumentaryProbeRetrievalQueries(string query)
    {
        var queries = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                AddDistinctQuery(queries, CollapseWhitespace(value));
        }

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            foreach (var retrievalQuery in BuildPlanningExplorationRetrievalQueries(query))
                Add(retrievalQuery);
            if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            {
                foreach (var retrievalQuery in BuildPlanningRetrievalQueries(query))
                    Add(retrievalQuery);
            }
        }
        else
        {
            Add(query);
            Add(NormalizeRagQueryForRetrieval(query));
            Add(BuildRagEvidenceSelectionQuery(query));
            foreach (var retrievalQuery in BuildSourceBackedEvidenceExpansionRetrievalQueries(query))
                Add(retrievalQuery);
        }

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(query))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsGenericDocumentaryProbeTerm(term))
            .Take(6)
            .ToArray();
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query) && signalTerms.Length > 0)
        {
            Add(string.Join(' ', signalTerms));
            foreach (var term in signalTerms.Take(4))
                Add(term);
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static bool IsGenericDocumentaryProbeTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is
            "document" or "documents" or "source" or "sources" or "fichier" or "fichiers" or
            "dossier" or "dossiers" or "corpus" or "page" or "pages" or
            "sujet" or "theme" or "topic" or "about" or "regarding" or "concerning" or
            "explique" or "expliquer" or "expliquez" or "parle" or "parler" or
            "tell" or "explain" or "describe" or "descripcion" or "descricao" or
            "explica" or "explicar" or "erklare" or "erklaren" or "spiega" or "spiegare";
    }

    private static ToolResults BuildProbeRagToolResults(JsonElement result, string toolName)
    {
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "rag.search" : toolName,
            Result = result.Clone()
        });
        return toolResults;
    }

    private static IReadOnlyList<RagHitSummary> SelectDocumentaryProbeClarificationHits(ToolResults toolResults)
        => EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath) || !string.IsNullOrWhiteSpace(hit.DocName))
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .GroupBy(static hit => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
                .ThenByDescending(static hit => hit.Score)
                .First())
            .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
            .ThenByDescending(static hit => hit.Score)
            .Take(3)
            .ToArray();

    private static ToolResults BuildProbeRagToolResults(IReadOnlyList<RagItem> hits)
    {
        var payload = new
        {
            hits = hits.Select(x => new
            {
                score = x.Score,
                docId = x.DocId,
                docName = x.DocName,
                docPath = x.DocPath,
                category = x.Category,
                categoryPath = x.CategoryPath,
                categoryRef = x.CategoryRef,
                docLanguage = x.DocLanguage,
                profileLanguage = x.ProfileLanguage,
                pageStart = x.PageStart ?? 1,
                pageEnd = x.PageEnd ?? x.PageStart ?? 1,
                chunkId = x.ChunkId,
                chunkIndex = x.ChunkIndex,
                excerpt = string.IsNullOrWhiteSpace(x.Snippet) ? x.Text : x.Snippet,
                fullText = x.Text,
                sectionTitle = x.SectionTitle ?? x.Context?.SectionTitle,
                headingPath = x.HeadingPath ?? x.Context?.HeadingPath,
                retriever = x.Retriever,
                provenanceInfo = x.ProvenanceInfo,
                context = x.Context,
                rerankScore = x.RerankScore,
                exactMatchHit = x.ExactMatchHit ?? false,
                sourceHash = x.SourceHash,
                embeddingBasis = x.EmbeddingBasis,
                chunkType = x.ChunkType ?? x.Context?.ChunkType,
                prevChunkId = x.PrevChunkId ?? x.Context?.PrevChunkId,
                nextChunkId = x.NextChunkId ?? x.Context?.NextChunkId,
                sameSectionChunkId = x.SameSectionChunkId ?? x.Context?.SameSectionChunkId,
                hasTable = x.HasTable,
                hasWarning = x.HasWarning,
                hypQuestionsMatched = x.HypQuestionsMatched,
                extractionQuality = x.ExtractionQuality,
                contextualSnippet = x.ContextualSnippet,
                matchedContentCards = x.MatchedContentCards,
                profileSignals = x.ProfileSignals,
                selectionHints = x.SelectionHints
            }).ToList()
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = doc.RootElement.Clone()
        });
        return toolResults;
    }

    private static string BuildDocumentaryProbeClarification(IReadOnlyList<RagHitSummary> hits, string language)
    {
        language = NormalizeLanguageCode(language);
        var first = hits[0];
        var firstLabel = string.IsNullOrWhiteSpace(first.DocName) ? (first.DocPath ?? "document") : first.DocName!;
        if (hits.Count == 1)
        {
            return language switch
            {
                "en" => $"I found a likely matching document: {firstLabel}. Do you want me to search in this one?",
                "es" => $"He encontrado un documento que parece coincidir: {firstLabel}. ¿Quieres que busque en ese documento?",
                "pt" => $"Encontrei um documento que parece corresponder: {firstLabel}. Queres que eu pesquise nesse documento?",
                "de" => $"Ich habe ein wahrscheinlich passendes Dokument gefunden: {firstLabel}. Soll ich in diesem Dokument suchen?",
                "it" => $"Ho trovato un documento che sembra corrispondere: {firstLabel}. Vuoi che cerchi in questo documento?",
                _ => $"J'ai trouvé un document qui semble correspondre : {firstLabel}. Veux-tu que je cherche dans celui-ci ?"
            };
        }

        var labels = hits.Select(x => string.IsNullOrWhiteSpace(x.DocName) ? (x.DocPath ?? "document") : x.DocName!).Take(3).ToList();
        var joined = string.Join(language == "fr" ? " ; " : "; ", labels);
        return language switch
        {
            "en" => $"I found several possible matches: {joined}. Which one should I use?",
            "es" => $"He encontrado varias coincidencias posibles: {joined}. ¿Cuál debo usar?",
            "pt" => $"Encontrei várias correspondências possíveis: {joined}. Qual devo usar?",
            "de" => $"Ich habe mehrere mögliche Treffer gefunden: {joined}. Welchen soll ich verwenden?",
            "it" => $"Ho trovato diverse corrispondenze possibili: {joined}. Quale devo usare?",
            _ => $"J'ai trouvé plusieurs correspondances possibles : {joined}. Laquelle veux-tu que j'utilise ?"
        };
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, IReadOnlyList<string> toolNames)> TryHandleSourcePolicyShortcutAsync(
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!LooksLikeSourceBypassOrUnsupportedInventionRequest(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (ShouldSkipExactItemPreRouterShortcut(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
            && !LooksLikeDocumentInstructionPolicyRequest(effectiveUserMessage)
            && !LooksLikeHardSourceBypassOrUnsupportedInventionRequest(effectiveUserMessage))
        {
            return (false, string.Empty, null, Array.Empty<string>());
        }

        language = NormalizeLanguageCode(language);
        onPhase?.Invoke(DeterministicAgentText.PhaseRag(language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));

        if (LooksLikeDocumentInstructionPolicyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildDocumentInstructionPolicyAnswer(language);
            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        if (LooksLikeSourceAbsentAssertionPolicyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildSourceAbsentAssertionPolicyAnswer(language);
            if (ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequest(effectiveUserMessage))
            {
                try
                {
                    var anchorArgs = CreateJsonArgs(new
                    {
                        query = NormalizeRagQueryForRetrieval(effectiveUserMessage),
                        topK = 2,
                        category = ResolveRagCategoryScope(effectiveUserMessage),
                        mode = "focused"
                    });
                    var anchorResult = await ExecRagSearchAsync(anchorArgs, ct).ConfigureAwait(false);
                    if (HasRagHits(anchorResult))
                    {
                        var anchorToolResults = new ToolResults();
                        anchorToolResults.Items.Add(new ToolResults.Item
                        {
                            ToolName = "rag.search",
                            Result = anchorResult
                        });

                        var anchorSources = DeriveSourcesFromRagHits(anchorToolResults).Take(3).ToList();
                        if (anchorSources.Count > 0)
                        {
                            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(anchorSources);
                            _mem.LastToolNames = new List<string> { "rag.search" };
                            deterministicAnswer = InjectInlineSources(deterministicAnswer, anchorSources, language);
                            var anchorSourcesPayload = BuildSourcesPayload(anchorSources);
                            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
                            onProgress?.Invoke(string.Empty);
                            return (true, deterministicAnswer, anchorSourcesPayload, new[] { "rag.search" });
                        }
                    }
                }
                catch
                {
                    // The policy answer is still valid without a source anchor.
                }
            }

            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        if (LooksLikeBinaryAnswerWithSourceUncertaintyRequest(effectiveUserMessage))
        {
            var deterministicAnswer = BuildBinaryAnswerWithSourceUncertaintyPolicyAnswer(language);
            try
            {
                var anchorArgs = CreateJsonArgs(new
                {
                    query = NormalizeRagQueryForRetrieval(effectiveUserMessage),
                    topK = 3,
                    category = ResolveRagCategoryScope(effectiveUserMessage),
                    mode = "focused"
                });
                var anchorResult = await ExecRagSearchAsync(anchorArgs, ct).ConfigureAwait(false);
                if (HasRagHits(anchorResult))
                {
                    var anchorToolResults = new ToolResults();
                    anchorToolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "rag.search",
                        Result = anchorResult
                    });

                    var anchorSources = DeriveSourcesFromRagHits(anchorToolResults).Take(4).ToList();
                    if (anchorSources.Count > 0)
                    {
                        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(anchorSources);
                        _mem.LastToolNames = new List<string> { "rag.search" };
                        deterministicAnswer = InjectInlineSources(deterministicAnswer, anchorSources, language);
                        var anchorSourcesPayload = BuildSourcesPayload(anchorSources);
                        await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
                        onProgress?.Invoke(string.Empty);
                        return (true, deterministicAnswer, anchorSourcesPayload, new[] { "rag.search" });
                    }
                }
            }
            catch
            {
                // The policy answer is still valid without a source anchor.
            }

            await EmitDeterministicTextAsync(deterministicAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, deterministicAnswer, null, Array.Empty<string>());
        }

        var retrievalQuery = BuildSourcePolicyRetrievalQuery(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(retrievalQuery))
            retrievalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);

        ToolResults? toolResults = null;
        List<ToolMemory.SourceRef> sources = new();
        try
        {
            if (!string.IsNullOrWhiteSpace(retrievalQuery))
            {
                var args = CreateJsonArgs(new
                {
                    query = retrievalQuery,
                    topK = 5,
                    category = ResolveRagCategoryScope(effectiveUserMessage),
                    mode = "balanced"
                });
                var ragResult = await ExecRagSearchAsync(args, ct).ConfigureAwait(false);
                if (HasRagHits(ragResult))
                {
                    toolResults = new ToolResults();
                    toolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "rag.search",
                        Result = ragResult
                    });
                    sources = DeriveSourcesFromRagHits(toolResults).Take(5).ToList();
                }
            }
        }
        catch
        {
            toolResults = null;
            sources.Clear();
        }

        var answer = toolResults is null
            ? ApplySourcePolicyGuardPrefix(DeterministicAgentText.AnswerNotEnoughUsableInfo(language), language)
            : ApplySourcePolicyGuardPrefix(
                BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, retrievalQuery, language, minPlanningItems: 1),
                language);
        if (string.IsNullOrWhiteSpace(answer))
            answer = ApplySourcePolicyGuardPrefix(DeterministicAgentText.AnswerNotEnoughUsableInfo(language), language);

        object? sourcesPayload = null;
        if (sources.Count > 0)
        {
            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
            answer = InjectInlineSources(answer, sources, language);
            sourcesPayload = BuildSourcesPayload(sources);
        }

        await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        return (true, answer, sourcesPayload, sources.Count > 0 ? new[] { "rag.search" } : Array.Empty<string>());
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, IReadOnlyList<string> toolNames)> TryHandleDocumentVersionTraceabilityShortcutAsync(
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        var retrievalQuery = BuildDocumentVersionTraceabilitySearchQuery(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(retrievalQuery))
            return (false, string.Empty, null, Array.Empty<string>());

        language = NormalizeLanguageCode(language);
        onPhase?.Invoke(DeterministicAgentText.PhaseRag(language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));

        try
        {
            var queries = BuildDocumentVersionTraceabilitySearchQueries(effectiveUserMessage);
            if (queries.Length == 0)
                queries = new[] { retrievalQuery };

            var args = CreateJsonArgs(new
            {
                queries,
                topK = 12,
                category = ResolveRagCategoryScope(effectiveUserMessage),
                mode = "balanced"
            });
            var ragResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
            if (!HasRagHits(ragResult))
                return (false, string.Empty, null, Array.Empty<string>());

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = ragResult
            });

            var answer = TryBuildDocumentVersionTraceabilityAnswer(toolResults, effectiveUserMessage, language);
            var sources = DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, effectiveUserMessage);
            if (string.IsNullOrWhiteSpace(answer) || sources.Count == 0)
                return (false, string.Empty, null, Array.Empty<string>());

            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
            _mem.LastToolNames = new List<string> { "rag.multi_search" };
            answer = InjectInlineSources(answer, sources, language);
            var sourcesPayload = BuildSourcesPayload(sources);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, answer, sourcesPayload, new[] { "rag.multi_search" });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, string.Empty, null, Array.Empty<string>());
        }
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, IReadOnlyList<string> toolNames)> TryHandleExactItemPreRouterShortcutAsync(
        string effectiveUserMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var shortcutEvidenceQuery = BuildRagEvidenceSelectionQuery(effectiveUserMessage);
        var exactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var requestedExplicitDocument = ExtractExplicitDocumentFileReferenceQueries(effectiveUserMessage).FirstOrDefault();
        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            return (false, string.Empty, null, Array.Empty<string>());

        if (ShouldSkipExactItemPreRouterShortcut(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (LooksLikeGenericCollectionOrListRequest(effectiveUserMessage))
            return (false, string.Empty, null, Array.Empty<string>());

        if (!string.IsNullOrWhiteSpace(requestedExplicitDocument)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage))
        {
            exactItemTitle = requestedExplicitDocument;
        }

        var shouldHandle =
            LooksLikeStructuredItemCardRequest(effectiveUserMessage)
            || LooksLikeItemLocationLookupRequest(effectiveUserMessage)
            || LooksLikeSourceBackedActionRequest(effectiveUserMessage);
        if (!shouldHandle)
            return (false, string.Empty, null, Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(exactItemTitle)
            && LooksLikeCompactTechnicalIdentifier(shortcutEvidenceQuery)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage))
        {
            exactItemTitle = shortcutEvidenceQuery;
        }

        if (string.IsNullOrWhiteSpace(exactItemTitle) && LooksLikeShortTechnicalEvidenceTopic(shortcutEvidenceQuery))
            return (false, string.Empty, null, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(exactItemTitle))
            return (false, string.Empty, null, Array.Empty<string>());

        language = NormalizeLanguageCode(language);
        onPhase?.Invoke(DeterministicAgentText.PhaseRag(language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(language));

        try
        {
            if (!string.IsNullOrWhiteSpace(requestedExplicitDocument))
            {
                var resolution = await ResolveExplicitDocumentReferenceStrictAsync(requestedExplicitDocument!, ct).ConfigureAwait(false);
                if (!resolution.IsResolved && !resolution.IsAmbiguous && !resolution.IsCategoryReference)
                {
                    var missingAnswer = BuildMissingExplicitDocumentAnswer(language, requestedExplicitDocument!, Array.Empty<RagHitSummary>());
                    _mem.LastSourcesUsed = [];
                    _mem.LastToolNames = new List<string> { "documents.search" };
                    await EmitDeterministicTextAsync(missingAnswer, onDelta, ct).ConfigureAwait(false);
                    onProgress?.Invoke(string.Empty);
                    return (true, missingAnswer, null, new[] { "documents.search" });
                }
            }

            var retrievalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);
            var categoryScope = ResolveRagCategoryScope(effectiveUserMessage);
            var isStructuredExactActionRequest = LooksLikeStructuredItemCardRequest(effectiveUserMessage)
                && LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                && !string.IsNullOrWhiteSpace(retrievalQuery);
            var exactSearchQuery = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
                ? BuildDocumentVersionTraceabilityExactSearchQuery(exactItemTitle!, effectiveUserMessage)
                : isStructuredExactActionRequest
                    ? retrievalQuery
                    : exactItemTitle;
            var singleArgs = CreateJsonArgs(new
            {
                query = exactSearchQuery,
                topK = isStructuredExactActionRequest ? 6 : LooksLikeStructuredItemCardRequest(effectiveUserMessage) ? 12 : 8,
                category = categoryScope,
                mode = isStructuredExactActionRequest ? "focused" : "balanced"
            });
            var ragResult = await ExecRagSearchAsync(singleArgs, ct).ConfigureAwait(false);
            var toolName = "rag.search";
            var isCompactTechnicalExactItem = LooksLikeCompactTechnicalIdentifier(exactItemTitle);
            var isDocumentVersionTraceabilityRequest = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage);
            var shouldTryPreciseMultiSearch = ShouldTryPreciseMultiSearchForExactItem(
                ragResult,
                effectiveUserMessage,
                exactItemTitle!,
                requestedExplicitDocument,
                isCompactTechnicalExactItem,
                isDocumentVersionTraceabilityRequest);
            if (shouldTryPreciseMultiSearch)
            {
                var queries = BuildPreciseRetrievalQueries(exactItemTitle!, retrievalQuery, effectiveUserMessage);
                if (queries.Length == 0)
                {
                    if (!HasRagHits(ragResult))
                        return (false, string.Empty, null, Array.Empty<string>());
                }
                else
                {
                    var args = CreateJsonArgs(new
                    {
                        queries,
                        topK = LooksLikeStructuredItemCardRequest(effectiveUserMessage) ? 20 : 12,
                        category = categoryScope,
                        mode = "balanced"
                    });
                    var multiResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
                    if (HasRagHits(multiResult))
                    {
                        ragResult = multiResult;
                        toolName = "rag.multi_search";
                    }
                }
            }

            if (!HasRagHits(ragResult))
            {
                if (!string.IsNullOrWhiteSpace(requestedExplicitDocument))
                {
                    var missingAnswer = BuildMissingExplicitDocumentAnswer(language, requestedExplicitDocument!, Array.Empty<RagHitSummary>());
                    _mem.LastSourcesUsed = [];
                    _mem.LastToolNames = new List<string> { toolName };
                    await EmitDeterministicTextAsync(missingAnswer, onDelta, ct).ConfigureAwait(false);
                    onProgress?.Invoke(string.Empty);
                    return (true, missingAnswer, null, new[] { toolName });
                }

                return (false, string.Empty, null, Array.Empty<string>());
            }

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = toolName,
                Result = ragResult
            });

            string answer;
            List<ToolMemory.SourceRef> sources;
            if (!isDocumentVersionTraceabilityRequest
                && !string.IsNullOrWhiteSpace(requestedExplicitDocument)
                && LooksLikeBriefSourcedCitationRequest(effectiveUserMessage))
            {
                answer = TryBuildBriefDocumentScopedCitationAnswer(toolResults, effectiveUserMessage, requestedExplicitDocument!, language);
                sources = string.IsNullOrWhiteSpace(answer)
                    ? new List<ToolMemory.SourceRef>()
                    : DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage, maxSources: 3);
            }
            else
            {
                answer = isDocumentVersionTraceabilityRequest
                    ? TryBuildDocumentVersionTraceabilityAnswer(toolResults, effectiveUserMessage, language) ?? string.Empty
                    : BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, language);
                sources = isDocumentVersionTraceabilityRequest
                    ? DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, effectiveUserMessage)
                    : DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }
            var isShortTechnicalExactItem = LooksLikeShortTechnicalEvidenceTopic(exactItemTitle);
            if ((string.IsNullOrWhiteSpace(answer)
                    || sources.Count == 0
                    || LooksLikeMissingExactItemWithoutSourceLeads(answer)
                    || (isShortTechnicalExactItem && LooksLikeMissingExactItemAnswer(answer)))
                && !isDocumentVersionTraceabilityRequest
                && LooksLikeSourceBackedActionRequest(effectiveUserMessage))
            {
                var fallbackQuery = isShortTechnicalExactItem
                    ? exactItemTitle!
                    : effectiveUserMessage;
                answer = BuildRagEvidenceFallbackAnswer(toolResults, fallbackQuery, language);
                sources = DeriveSourcesFromExtractiveHits(toolResults, fallbackQuery);
                if (sources.Count == 0)
                    sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
            }

            if (string.IsNullOrWhiteSpace(answer) || sources.Count == 0)
                return (false, string.Empty, null, Array.Empty<string>());

            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
            _mem.LastToolNames = new List<string> { toolName };
            answer = InjectInlineSources(answer, sources, language);
            var sourcesPayload = BuildSourcesPayload(sources);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return (true, answer, sourcesPayload, new[] { toolName });
        }
        catch
        {
            return (false, string.Empty, null, Array.Empty<string>());
        }
    }

    private static bool ShouldSkipExactItemPreRouterShortcut(string effectiveUserMessage)
        => LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           || ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);

    private static bool ShouldTryPreciseMultiSearchForExactItem(
        JsonElement ragResult,
        string effectiveUserMessage,
        string exactItemTitle,
        string? requestedExplicitDocument,
        bool isCompactTechnicalExactItem,
        bool isDocumentVersionTraceabilityRequest)
    {
        var hasInitialHits = HasRagHits(ragResult);
        if (!hasInitialHits)
            return true;

        if (isDocumentVersionTraceabilityRequest)
            return false;

        var explicitDocumentHasInitialHits = !string.IsNullOrWhiteSpace(requestedExplicitDocument)
            && RagResultContainsExplicitDocumentHit(ragResult, requestedExplicitDocument!);
        if (!string.IsNullOrWhiteSpace(requestedExplicitDocument)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            && !explicitDocumentHasInitialHits)
        {
            return true;
        }

        var initialHasUsableRequestedTitle = !string.IsNullOrWhiteSpace(exactItemTitle)
            && RagResultContainsUsableRequestedTitle(ragResult, exactItemTitle);
        var isStructuredItemCardRequest = LooksLikeStructuredItemCardRequest(effectiveUserMessage);
        if (isStructuredItemCardRequest)
            return !initialHasUsableRequestedTitle;

        return !isCompactTechnicalExactItem && !initialHasUsableRequestedTitle;
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleStandaloneTopicRagAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan plan,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        if (!ShouldForceRagForStandaloneTopic(effectiveUserMessage, plan))
            return (false, string.Empty, null);

        EmitRagTrace(
            "standalone_topic_rag.start",
            ("intent", plan.Intent),
            ("language", plan.Language),
            ("query", effectiveUserMessage));
        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        var isDocumentVersionTraceabilityRequest = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage);
        var normalizedOriginalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);
        var isShortTechnicalEvidenceTopic = LooksLikeShortTechnicalEvidenceTopic(normalizedOriginalQuery);
        var exactItemTitle = isShortTechnicalEvidenceTopic
            ? null
            : TryExtractRequestedItemTitle(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            exactItemTitle = null;
        var retrievalQuery = !string.IsNullOrWhiteSpace(exactItemTitle)
            ? CollapseWhitespace($"{exactItemTitle} {normalizedOriginalQuery}")
            : normalizedOriginalQuery;
        if (string.IsNullOrWhiteSpace(retrievalQuery))
        {
            EmitRagTrace(
                "standalone_topic_rag.end",
                ("handled", false),
                ("reason", "empty_retrieval_query"));
            return (false, string.Empty, null);
        }

        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));
            try
            {
                await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Initial retrieval can still proceed without category hints.
            }

            var categoryScope = ResolveRagCategoryScope(effectiveUserMessage);
            var isDocumentaryPlanningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage);
            var isStructuredSourceBackedPlanningRequest = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
            EmitRagTrace(
                "standalone_topic_rag.retrieval",
                ("query", retrievalQuery),
                ("category", categoryScope),
                ("comparative", isComparativeDocumentaryRequest),
                ("traceability", isDocumentVersionTraceabilityRequest),
                ("planning", isDocumentaryPlanningRequest),
                ("structured_planning", isStructuredSourceBackedPlanningRequest),
                ("exact_item_title", exactItemTitle));

            if (isDocumentaryPlanningRequest || isStructuredSourceBackedPlanningRequest)
            {
                var planningQueries = BuildInitialSourceBackedPlanningProbeQueries(effectiveUserMessage);
                onProgress?.Invoke(DeterministicAgentText.ProgressSearchSourceBackedCandidates(plan.Language));
                var multiArgs = CreateJsonArgs(new
                {
                    queries = planningQueries,
                    topK = InitialSourceBackedPlanningProbeTopK,
                    maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                    maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                    category = categoryScope,
                    mode = "broad",
                    researchMode = "source_exploration",
                    includeResearchSurfaces = true
                });
                var multiResult = await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
                if (HasRagHits(multiResult) || isStructuredSourceBackedPlanningRequest)
                {
                    var planningToolResults = new ToolResults();
                    planningToolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "rag.multi_search",
                        Result = multiResult
                    });
                    var effectivePlanningCategoryScope = categoryScope;

                    if (string.IsNullOrWhiteSpace(categoryScope))
                    {
                        ClientLog.Info(
                            "ToolAgent standalone planning category inference skipped: reason=source_exploration");
                    }

                    await TryExpandSourceBackedEvidenceRetrievalAsync(
                        planningToolResults,
                        plan,
                        effectiveUserMessage,
                        ct,
                        onPhase,
                        onProgress,
                        effectivePlanningCategoryScope).ConfigureAwait(false);

                    onProgress?.Invoke(DeterministicAgentText.ProgressVerifyCandidateCoverage(plan.Language));
                    var planningCoverageSw = Stopwatch.StartNew();
                    EmitRagTrace(
                        "evidence.planning.coverage_analysis.start",
                        ("structured_gate", isStructuredSourceBackedPlanningRequest),
                        ("tool_items", planningToolResults.Items.Count),
                        ("query", effectiveUserMessage));
                    var planningCoverage = EvaluateSourceBackedPlanningCoverage(planningToolResults, effectiveUserMessage, plan.Language);
                    EmitRagTrace(
                        "evidence.planning.coverage_analysis.end",
                        ("adequate", planningCoverage.IsAdequate),
                        ("candidate_count", planningCoverage.CandidateCount),
                        ("minimum_candidates", planningCoverage.MinimumCandidates),
                        ("target_slots", planningCoverage.TargetSlots),
                        ("distinct_pages", planningCoverage.DistinctSourcePages),
                        ("score", planningCoverage.Score),
                        ("ms", planningCoverageSw.ElapsedMilliseconds));
                    var requiresStructuredPlanningGate = isStructuredSourceBackedPlanningRequest;
                    var shouldUsePlanningWriter =
                        planningCoverage.IsAdequate
                        && (requiresStructuredPlanningGate
                            || (!requiresStructuredPlanningGate
                                && (ShouldRequireWriterForBroadDocumentaryFinal(planningToolResults, effectiveUserMessage, plan.Language)
                                    || ShouldRouteSourceBackedAnswerThroughWriter(planningToolResults, effectiveUserMessage, plan.Language)
                                    || ShouldAllowWriterForPartialSourceBackedPlanning(planningToolResults, effectiveUserMessage, plan.Language)
                                    || ShouldPreferWriterForPolishedSourceBackedAnswer(planningToolResults, effectiveUserMessage))));
                    EmitRagTrace(
                        "evidence.planning.coverage_gate",
                        ("structured_gate", requiresStructuredPlanningGate),
                        ("adequate", planningCoverage.IsAdequate),
                        ("candidate_count", planningCoverage.CandidateCount),
                        ("minimum_candidates", planningCoverage.MinimumCandidates),
                        ("target_slots", planningCoverage.TargetSlots),
                        ("distinct_pages", planningCoverage.DistinctSourcePages),
                        ("score", planningCoverage.Score),
                        ("use_writer", shouldUsePlanningWriter),
                        ("coverage_ms", planningCoverageSw.ElapsedMilliseconds));
                    var deterministicPlanningDraft = SourceBackedPlanningDraft.Empty;
                    var planningSearchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(planningToolResults);
                    var allowPartialStructuredPlanningDraft = requiresStructuredPlanningGate
                        && !planningCoverage.IsAdequate
                        && HasUsefulPartialSourceBackedPlanningCoverage(
                            planningCoverage,
                            searchWasBroadened: false,
                            searchWasExpanded: planningSearchAlreadyExpanded);
                    EmitRagTrace(
                        "evidence.planning.partial_policy",
                        ("structured_gate", requiresStructuredPlanningGate),
                        ("allow_partial", allowPartialStructuredPlanningDraft),
                        ("candidate_count", planningCoverage.CandidateCount),
                        ("minimum_candidates", planningCoverage.MinimumCandidates),
                        ("target_slots", planningCoverage.TargetSlots),
                        ("distinct_pages", planningCoverage.DistinctSourcePages),
                        ("search_expanded", planningSearchAlreadyExpanded));
                    if (allowPartialStructuredPlanningDraft)
                    {
                        shouldUsePlanningWriter = true;
                        EmitRagTrace(
                            "evidence.planning.coverage_gate.updated",
                            ("reason", "useful_partial_coverage"),
                            ("use_writer", shouldUsePlanningWriter),
                            ("candidate_count", planningCoverage.CandidateCount),
                            ("target_slots", planningCoverage.TargetSlots));
                    }
                    if (requiresStructuredPlanningGate)
                    {
                        var deterministicDraftSw = Stopwatch.StartNew();
                        EmitRagTrace(
                            "evidence.planning.deterministic_draft.start",
                            ("target_items", ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage)),
                            ("tool_items", planningToolResults.Items.Count),
                            ("allow_partial", allowPartialStructuredPlanningDraft),
                            ("search_expanded", planningSearchAlreadyExpanded));
                        deterministicPlanningDraft = BuildSourceBackedPlanningDraft(
                            planningToolResults,
                            plan.Language,
                            minItems: ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage),
                            query: effectiveUserMessage,
                            allowPartialStructuredPlanningDraft: allowPartialStructuredPlanningDraft);
                        EmitRagTrace(
                            "evidence.planning.deterministic_draft.end",
                            ("answer_chars", deterministicPlanningDraft.Answer?.Length ?? 0),
                            ("items", deterministicPlanningDraft.Items.Count),
                            ("sources", deterministicPlanningDraft.Sources.Count),
                            ("partial", allowPartialStructuredPlanningDraft && deterministicPlanningDraft.Items.Count < planningCoverage.TargetSlots),
                            ("ms", deterministicDraftSw.ElapsedMilliseconds));
                    }

                    var deterministicPlanningAnswer = requiresStructuredPlanningGate
                        ? deterministicPlanningDraft.Answer
                        : shouldUsePlanningWriter
                            ? string.Empty
                            : BuildSourceBackedPlanningOrExtractiveAnswer(planningToolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                    var deterministicPlanningSources = requiresStructuredPlanningGate
                        ? deterministicPlanningDraft.Sources.ToList()
                        : DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                    if (deterministicPlanningSources.Count == 0 && !requiresStructuredPlanningGate)
                        deterministicPlanningSources = DeriveSourcesFromExtractiveHits(planningToolResults, effectiveUserMessage);
                    var hasTrustedPartialPlanningDraft = false;
                    if (requiresStructuredPlanningGate)
                    {
                        var deterministicSupportSw = Stopwatch.StartNew();
                        EmitRagTrace(
                            "evidence.planning.deterministic_support.start",
                            ("answer_chars", deterministicPlanningAnswer?.Length ?? 0),
                            ("tool_items", planningToolResults.Items.Count));
                        hasTrustedPartialPlanningDraft = HasTrustedPartialSourceBackedPlanningDraftCoverage(
                            deterministicPlanningDraft,
                            planningCoverage,
                            effectiveUserMessage,
                            searchWasBroadened: false,
                            searchWasExpanded: planningSearchAlreadyExpanded);
                        PlanningAnswerSupportAnalysis deterministicPlanningSupport;
                        if (HasTrustedSourceBackedPlanningDraftCoverage(deterministicPlanningDraft, effectiveUserMessage))
                        {
                            deterministicPlanningSupport = BuildTrustedSourceBackedPlanningDraftAnalysis(
                                deterministicPlanningDraft,
                                effectiveUserMessage);
                        }
                        else if (hasTrustedPartialPlanningDraft)
                        {
                            deterministicPlanningSupport = BuildTrustedPartialSourceBackedPlanningDraftAnalysis(deterministicPlanningDraft);
                        }
                        else
                        {
                            deterministicPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                deterministicPlanningAnswer,
                                planningToolResults,
                                effectiveUserMessage,
                                plan.Language);
                        }
                        var rejectDeterministicPlanningAnswer = !hasTrustedPartialPlanningDraft
                            && ShouldRejectUnsupportedPlanningAnswerForFinal(deterministicPlanningSupport, effectiveUserMessage);
                        EmitRagTrace(
                            "evidence.planning.deterministic_support.end",
                            ("items", deterministicPlanningSupport.ItemCount),
                            ("supported", deterministicPlanningSupport.SupportedItemCount),
                            ("unsupported", deterministicPlanningSupport.UnsupportedItemCount),
                            ("candidates", deterministicPlanningSupport.CandidateCount),
                            ("sources", deterministicPlanningSupport.Sources.Count),
                            ("trusted_partial", hasTrustedPartialPlanningDraft),
                            ("reject", rejectDeterministicPlanningAnswer),
                            ("ms", deterministicSupportSw.ElapsedMilliseconds));
                        deterministicPlanningSources = !string.IsNullOrWhiteSpace(deterministicPlanningAnswer)
                            && !rejectDeterministicPlanningAnswer
                            && deterministicPlanningSupport.Sources.Count > 0
                                ? deterministicPlanningSupport.Sources.ToList()
                                : new List<ToolMemory.SourceRef>();
                    }
                    if (!shouldUsePlanningWriter
                        && !string.IsNullOrWhiteSpace(deterministicPlanningAnswer)
                        && deterministicPlanningSources.Count > 0)
                    {
                        EmitRagTrace(
                            "evidence.planning.deterministic_return",
                            ("answer_chars", deterministicPlanningAnswer.Length),
                            ("sources", deterministicPlanningSources.Count),
                            ("partial", hasTrustedPartialPlanningDraft));
                        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(deterministicPlanningSources);
                        deterministicPlanningAnswer = InjectInlineSources(deterministicPlanningAnswer, deterministicPlanningSources, plan.Language);
                        var deterministicPlanningSourcesPayload = BuildSourcesPayload(deterministicPlanningSources);
                        _lastAnswerSource = "standalone_topic_rag:source_backed_planning_deterministic";
                        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.multi_search", 0, true) };
                        _lastToolsMs = 0;
                        _lastWriterMs = 0;
                        _mem.LastToolNames = new List<string> { "rag.multi_search" };
                        onProgress?.Invoke(string.Empty);

                        var finalizedDeterministicPlan = FinalizeAndReturn(swTotalPipeline, displayUserMessage, deterministicPlanningAnswer, deterministicPlanningSourcesPayload, "rag.answer", _mem.LastToolNames, _mem.LastReasoningTracePublic);
                        return (true, finalizedDeterministicPlan.finalAnswer, finalizedDeterministicPlan.sourcesPayload);
                    }

                    if (requiresStructuredPlanningGate && !planningCoverage.IsAdequate && !shouldUsePlanningWriter)
                    {
                        EmitPlanningInsufficientFallbackTrace(
                            "standalone-coverage-gate",
                            "rag.answer",
                            "coverage_not_adequate",
                            planningCoverage.CandidateCount,
                            planningSearchAlreadyExpanded);
                        var sourceBackedAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            effectiveUserMessage,
                            effectiveUserMessage,
                            planningCoverage.CandidateCount,
                            searchAlreadyExpanded: planningSearchAlreadyExpanded);
                        _lastAnswerSource = "standalone_topic_rag:structured_planning_insufficient_supported_candidates";
                        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.multi_search", 0, true) };
                        _lastToolsMs = 0;
                        _lastWriterMs = 0;
                        _mem.LastToolNames = new List<string> { "rag.multi_search" };
                        onProgress?.Invoke(string.Empty);

                        var finalizedStructuredGate = FinalizeAndReturn(swTotalPipeline, displayUserMessage, sourceBackedAnswer, null, "rag.answer", _mem.LastToolNames, _mem.LastReasoningTracePublic);
                        return (true, finalizedStructuredGate.finalAnswer, null);
                    }

                    var planningWriterPlan = new RouterPlan
                    {
                        Intent = "rag.answer",
                        Language = plan.Language,
                        Mode = plan.Mode,
                        ResponseFormat = "auto",
                        ToolCalls = new List<RouterPlan.ToolCall>
                        {
                            new()
                            {
                                Name = "rag.multi_search",
                                Args = multiArgs
                            }
                        },
                        ReasoningTracePublic = plan.ReasoningTracePublic ?? new List<string>(),
                        RiskFlags = plan.RiskFlags ?? new List<string>(),
                        RouterConfidence = plan.RouterConfidence
                    };

                    onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
                    onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));
                    var writerDelta = requiresStructuredPlanningGate ? null : onDelta;
                    var planningWriterSw = Stopwatch.StartNew();
                    EmitRagTrace(
                        "evidence.planning.writer.start",
                        ("structured_gate", requiresStructuredPlanningGate),
                        ("tool_items", planningToolResults.Items.Count),
                        ("use_writer", shouldUsePlanningWriter),
                        ("timeout_ms", SourceBackedPlanningWriterTimeoutMs));
                    string writerAnswer;
                    List<ToolMemory.SourceRef>? writerSources;
                    try
                    {
                        using var writerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        writerTimeoutCts.CancelAfter(SourceBackedPlanningWriterTimeoutMs);
                        (writerAnswer, writerSources) = await AnswerAsync(
                                chatHistory,
                                effectiveUserMessage,
                                planningWriterPlan,
                                planningToolResults,
                                writerTimeoutCts.Token,
                                writerDelta,
                                onProgress)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        EmitRagTrace(
                            "evidence.planning.writer.timeout",
                            ("structured_gate", requiresStructuredPlanningGate),
                            ("timeout_ms", SourceBackedPlanningWriterTimeoutMs),
                            ("tool_items", planningToolResults.Items.Count));
                        writerAnswer = deterministicPlanningDraft.Answer ?? string.Empty;
                        writerSources = deterministicPlanningDraft.Sources.ToList();
                        if (string.IsNullOrWhiteSpace(writerAnswer))
                        {
                            writerAnswer = BuildSourceBackedSafeFallbackAnswer(
                                planningToolResults,
                                effectiveUserMessage,
                                plan.Language,
                                shouldAvoidRaw: shouldUsePlanningWriter);
                            writerSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                        }
                        _lastAnswerSource = "standalone_topic_rag:source_backed_planning_writer_timeout_fallback";
                    }
                    EmitRagTrace(
                        "evidence.planning.writer.end",
                        ("answer_chars", writerAnswer?.Length ?? 0),
                        ("sources", writerSources?.Count ?? 0),
                        ("ms", planningWriterSw.ElapsedMilliseconds));
                    writerAnswer = (writerAnswer ?? string.Empty).Trim();
                    if (ShouldFallbackFromNoRagDataAnswer(writerAnswer))
                    {
                        writerAnswer = BuildSourceBackedSafeFallbackAnswer(
                            planningToolResults,
                            effectiveUserMessage,
                            plan.Language,
                            shouldAvoidRaw: shouldUsePlanningWriter);
                        writerSources = DeriveSourcesFromExtractiveHits(planningToolResults, effectiveUserMessage);
                    }

                    if (writerSources is null || writerSources.Count == 0)
                        writerSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                    if (requiresStructuredPlanningGate)
                        writerSources = new List<ToolMemory.SourceRef>();

                    if (LooksLikePoorPlanningFallbackAnswer(writerAnswer, effectiveUserMessage)
                        && ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage))
                    {
                        var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                            chatHistory,
                            effectiveUserMessage,
                            planningWriterPlan,
                            planningToolResults,
                            ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(repairAnswer)
                            && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage))
                        {
                            writerAnswer = repairAnswer;
                        }
                        else
                        {
                            var fallbackAnswer = shouldUsePlanningWriter
                                ? BuildSourceBackedSafeFallbackAnswer(
                                    planningToolResults,
                                    effectiveUserMessage,
                                    plan.Language,
                                    shouldAvoidRaw: true)
                                : BuildNonPoorSourceBackedFallbackAnswer(
                                    planningToolResults,
                                    effectiveUserMessage,
                                    plan.Language);
                            if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                            {
                                writerAnswer = fallbackAnswer;
                            }
                            else if (!ShouldAvoidRawSourceBackedFallback(effectiveUserMessage)
                                && !ShouldAllowWriterForPartialSourceBackedPlanning(planningToolResults, effectiveUserMessage, plan.Language)
                                && !ShouldPreferWriterForPolishedSourceBackedAnswer(planningToolResults, effectiveUserMessage))
                            {
                                var planningDraft = BuildSourceBackedPlanningDraft(
                                    planningToolResults,
                                    plan.Language,
                                    minItems: requiresStructuredPlanningGate
                                        ? ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage)
                                        : 1,
                                    query: effectiveUserMessage);
                                var planningAnswer = planningDraft.Answer;
                                if (!string.IsNullOrWhiteSpace(planningAnswer))
                                {
                                    writerAnswer = planningAnswer;
                                    writerSources = planningDraft.Sources.ToList();
                                }
                            }
                        }

                        if (writerSources.Count == 0)
                            writerSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                    }

                    if (requiresStructuredPlanningGate)
                    {
                        var planningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                            writerAnswer,
                            planningToolResults,
                            effectiveUserMessage,
                            plan.Language);
                        if (ShouldRejectUnsupportedPlanningAnswerForFinal(planningSupport, effectiveUserMessage))
                        {
                            var sourceBackedDraft = BuildSourceBackedPlanningDraft(
                                planningToolResults,
                                plan.Language,
                                minItems: ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage),
                                query: effectiveUserMessage);
                            var sourceBackedAnswer = sourceBackedDraft.Answer;
                            var sourceBackedSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                sourceBackedAnswer,
                                planningToolResults,
                                effectiveUserMessage,
                                plan.Language);
                            if (!string.IsNullOrWhiteSpace(sourceBackedAnswer)
                                && !ShouldRejectUnsupportedPlanningAnswerForFinal(sourceBackedSupport, effectiveUserMessage)
                                && sourceBackedSupport.Sources.Count > 0)
                            {
                                writerAnswer = sourceBackedAnswer;
                                writerSources = sourceBackedSupport.Sources.ToList();
                                _lastAnswerSource = "standalone_topic_rag:structured_planning_rebuilt_from_supported_candidates";
                            }
                            else
                            {
                                var searchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(planningToolResults);
                                EmitPlanningInsufficientFallbackTrace(
                                    "standalone-writer-rebuild",
                                    "rag.answer",
                                    "supported_rebuild_rejected",
                                    sourceBackedSupport.CandidateCount > 0 ? sourceBackedSupport.CandidateCount : planningSupport.CandidateCount,
                                    searchAlreadyExpanded);
                                writerAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                                    plan.Language,
                                    effectiveUserMessage,
                                    effectiveUserMessage,
                                    sourceBackedSupport.CandidateCount > 0 ? sourceBackedSupport.CandidateCount : planningSupport.CandidateCount,
                                    searchAlreadyExpanded: searchAlreadyExpanded);
                                writerSources = new List<ToolMemory.SourceRef>();
                                _lastAnswerSource = "standalone_topic_rag:structured_planning_insufficient_supported_candidates";
                            }
                        }
                        else if (planningSupport.Sources.Count > 0)
                        {
                            writerSources = planningSupport.Sources.ToList();
                        }
                        else
                        {
                            var searchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(planningToolResults);
                            EmitPlanningInsufficientFallbackTrace(
                                "standalone-writer-no-sources",
                                "rag.answer",
                                "no_supported_visible_sources",
                                planningSupport.CandidateCount,
                                searchAlreadyExpanded);
                            writerAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                                plan.Language,
                                effectiveUserMessage,
                                effectiveUserMessage,
                                planningSupport.CandidateCount,
                                searchAlreadyExpanded: searchAlreadyExpanded);
                            writerSources = new List<ToolMemory.SourceRef>();
                            _lastAnswerSource = "standalone_topic_rag:structured_planning_no_supported_visible_sources";
                        }
                    }

                    object? writerSourcesPayload = null;
                    if (ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(writerAnswer, planningToolResults, effectiveUserMessage, plan.Language))
                    {
                        writerSources?.Clear();
                        _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
                    }

                    if (writerSources is { Count: > 0 })
                    {
                        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(writerSources);
                        writerAnswer = InjectInlineSources(writerAnswer, writerSources, plan.Language);
                        writerSourcesPayload = BuildSourcesPayload(writerSources);
                    }

                    if (!string.Equals(_lastAnswerSource, "standalone_topic_rag:structured_planning_rebuilt_from_supported_candidates", StringComparison.Ordinal)
                        && !string.Equals(_lastAnswerSource, "standalone_topic_rag:structured_planning_insufficient_supported_candidates", StringComparison.Ordinal))
                    {
                        _lastAnswerSource = "standalone_topic_rag:source_backed_planning_writer";
                    }
                    _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.multi_search", 0, true) };
                    _lastToolsMs = 0;
                    _mem.LastToolNames = new List<string> { "rag.multi_search" };
                    onProgress?.Invoke(string.Empty);

                    var finalizedWriterPlan = FinalizeAndReturn(swTotalPipeline, displayUserMessage, writerAnswer, writerSourcesPayload, "rag.answer", _mem.LastToolNames, _mem.LastReasoningTracePublic);
                    return (true, finalizedWriterPlan.finalAnswer, finalizedWriterPlan.sourcesPayload);
                }
            }

            var isSourceBackedActionRequest = LooksLikeSourceBackedActionRequest(effectiveUserMessage);
            var isDocumentaryContentRequest = LooksLikeDocumentaryContentRequest(effectiveUserMessage);
            var preferSingleRagSearch = ShouldPreferSingleRagSearchForDocumentaryRequest(effectiveUserMessage);
            var useMultiSearch = !string.IsNullOrWhiteSpace(exactItemTitle)
                || (!preferSingleRagSearch && isComparativeDocumentaryRequest)
                || isDocumentVersionTraceabilityRequest
                || (!preferSingleRagSearch && isSourceBackedActionRequest)
                || isDocumentaryContentRequest
                || isShortTechnicalEvidenceTopic;
            var useStandaloneResearchSurfaces = string.IsNullOrWhiteSpace(exactItemTitle)
                && ShouldUseResearchSurfacesForBroadRagRequest(effectiveUserMessage);
            var args = useMultiSearch
                ? CreateJsonArgs(new
                {
                    queries = !string.IsNullOrWhiteSpace(exactItemTitle)
                            ? BuildPreciseRetrievalQueries(exactItemTitle!, retrievalQuery, effectiveUserMessage)
                            : isComparativeDocumentaryRequest
                                ? BuildComparativeRetrievalQueries(effectiveUserMessage)
                            : isShortTechnicalEvidenceTopic
                                ? BuildShortTechnicalEvidenceRetrievalQueries(effectiveUserMessage)
                                : isDocumentVersionTraceabilityRequest
                                    ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                    : BuildSourceBackedActionRetrievalQueries(effectiveUserMessage),
                    topK = !string.IsNullOrWhiteSpace(exactItemTitle)
                        ? 20
                        : isComparativeDocumentaryRequest
                            ? NormalizeComparativeTopK(null, effectiveUserMessage)
                        : isSourceBackedActionRequest || isShortTechnicalEvidenceTopic
                            ? NormalizeSourceBackedActionTopK(null, effectiveUserMessage)
                            : 12,
                    category = categoryScope,
                    mode = useStandaloneResearchSurfaces ? "broad" : "balanced",
                    researchMode = useStandaloneResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = useStandaloneResearchSurfaces ? true : (bool?)null
                })
                : CreateJsonArgs(new
                {
                    query = retrievalQuery,
                    topK = 8,
                    category = categoryScope,
                    mode = useStandaloneResearchSurfaces ? "broad" : "balanced",
                    researchMode = useStandaloneResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = useStandaloneResearchSurfaces ? true : (bool?)null
                });
            var ragResult = useMultiSearch
                ? await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false)
                : await ExecRagSearchAsync(args, ct).ConfigureAwait(false);

            var ragPlan = new RouterPlan
            {
                Intent = "rag.answer",
                Language = plan.Language,
                Mode = plan.Mode,
                ResponseFormat = "auto",
                ToolCalls = new List<RouterPlan.ToolCall>
                {
                    new()
                    {
                        Name = useMultiSearch ? "rag.multi_search" : "rag.search",
                        Args = args
                    }
                },
                ReasoningTracePublic = plan.ReasoningTracePublic ?? new List<string>(),
                RiskFlags = plan.RiskFlags ?? new List<string>(),
                RouterConfidence = plan.RouterConfidence
            };

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = useMultiSearch ? "rag.multi_search" : "rag.search",
                Result = ragResult
            });

            await TryExpandSourceBackedEvidenceRetrievalAsync(
                toolResults,
                ragPlan,
                effectiveUserMessage,
                ct,
                onPhase,
                onProgress).ConfigureAwait(false);
            if (!toolResults.Items.Any(static item => HasRagHits(item.Result)))
                return (false, string.Empty, null);

            var preWriterAnswer = string.Empty;
            List<ToolMemory.SourceRef>? preWriterSources = null;
            var standaloneShouldRouteThroughWriter =
                ShouldRouteSourceBackedAnswerThroughWriter(toolResults, effectiveUserMessage, plan.Language);
            var standaloneShouldRequireWriterForBroadFinal =
                ShouldRequireWriterForBroadDocumentaryFinal(toolResults, effectiveUserMessage, plan.Language);
            var standaloneShouldUseBroadSynthesis =
                ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, effectiveUserMessage)
                || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage)
                || standaloneShouldRouteThroughWriter
                || standaloneShouldRequireWriterForBroadFinal;
            var standaloneShouldAvoidRawSourceBackedFallback =
                ShouldAvoidRawSourceBackedFallback(effectiveUserMessage)
                || standaloneShouldRouteThroughWriter
                || standaloneShouldRequireWriterForBroadFinal;
            if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage)
                && !standaloneShouldUseBroadSynthesis
                && !standaloneShouldAvoidRawSourceBackedFallback)
            {
                preWriterAnswer = BuildSourceBackedCountdownPlanningAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSources = DeriveSourcesFromCountdownPlanningHits(toolResults, effectiveUserMessage);
                if (string.IsNullOrWhiteSpace(preWriterAnswer))
                {
                    preWriterAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                    preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }
            }
            else if (!standaloneShouldUseBroadSynthesis
                && ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage)
                && !ShouldAvoidDeterministicSourceBackedOptionFallback(effectiveUserMessage))
            {
                preWriterAnswer = BuildSourceBackedOptionAnswer(toolResults, plan.Language, minItems: 1, query: effectiveUserMessage);
                preWriterSources = DeriveSourcesFromOptionHits(toolResults, effectiveUserMessage);
                if (string.IsNullOrWhiteSpace(preWriterAnswer))
                {
                    preWriterAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                    preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }
            }
            else if (!standaloneShouldUseBroadSynthesis
                && !standaloneShouldAvoidRawSourceBackedFallback
                && LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
            {
                if (!ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language))
                {
                    if (ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
                    {
                        if (TryBuildSupportedStructuredPlanningAnswer(
                                toolResults,
                                plan.Language,
                                effectiveUserMessage,
                                out var supportedPlanningAnswer,
                                out var supportedPlanningSources,
                                out _))
                        {
                            preWriterAnswer = supportedPlanningAnswer;
                            preWriterSources = supportedPlanningSources;
                        }
                    }
                    else
                    {
                        preWriterAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                        preWriterSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
                        if (preWriterSources.Count == 0)
                            preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                    }
                }
            }
            else if (!standaloneShouldUseBroadSynthesis
                && !standaloneShouldAvoidRawSourceBackedFallback
                && (isSourceBackedActionRequest || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults)))
            {
                preWriterAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }

            if (string.IsNullOrWhiteSpace(preWriterAnswer)
                && !standaloneShouldUseBroadSynthesis
                && !standaloneShouldAvoidRawSourceBackedFallback
                && !ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language)
                && isSourceBackedActionRequest)
            {
                preWriterAnswer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                if (preWriterSources.Count == 0)
                    preWriterSources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
            }

            if (!string.IsNullOrWhiteSpace(preWriterAnswer))
            {
                object? deterministicSourcesPayload = null;
                if (LooksLikeMissingExactItemWithoutSourceLeads(preWriterAnswer))
                {
                    preWriterSources?.Clear();
                    _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
                }

                if (TryFinalizeSourceBackedPlanningResponse(
                        preWriterAnswer,
                        toolResults,
                        effectiveUserMessage,
                        plan.Language,
                        out var finalizedStandalonePreWriterAnswer,
                        out var finalizedStandalonePreWriterSources,
                        out var finalizedStandalonePreWriterAnalysis,
                        out var finalizedStandalonePreWriterResolution))
                {
                    ClientLog.Info(
                        "ToolAgent planning standalone pre-writer finalizer: " +
                        $"resolution={finalizedStandalonePreWriterResolution} " +
                        $"items={finalizedStandalonePreWriterAnalysis.ItemCount} " +
                        $"supported={finalizedStandalonePreWriterAnalysis.SupportedItemCount} " +
                        $"unsupported={finalizedStandalonePreWriterAnalysis.UnsupportedItemCount} " +
                        $"candidates={finalizedStandalonePreWriterAnalysis.CandidateCount} " +
                        $"sources={finalizedStandalonePreWriterSources.Count}");
                    LogSourceBackedPlanningTrace(
                        "standalone-pre-writer-finalizer",
                        toolResults,
                        effectiveUserMessage,
                        plan.Language);
                    EmitPlanningFinalizerDecisionTrace(
                        "standalone-pre-writer-finalizer",
                        ragPlan.Intent,
                        finalizedStandalonePreWriterResolution,
                        finalizedStandalonePreWriterAnalysis,
                        finalizedStandalonePreWriterSources.Count,
                        $"standalone_topic_rag:{finalizedStandalonePreWriterResolution}:{ragPlan.Intent}");
                    preWriterAnswer = finalizedStandalonePreWriterAnswer;
                    preWriterSources = finalizedStandalonePreWriterSources;
                }

                if (ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(preWriterAnswer, toolResults, effectiveUserMessage, plan.Language))
                {
                    preWriterSources?.Clear();
                    _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
                }

                if (preWriterSources is { Count: > 0 })
                {
                    _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(preWriterSources);
                    preWriterAnswer = InjectInlineSources(preWriterAnswer, preWriterSources, plan.Language);
                    deterministicSourcesPayload = BuildSourcesPayload(preWriterSources);
                }

                _lastAnswerSource = $"standalone_topic_rag_pre_writer_source_backed:{ragPlan.Intent}";
                _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { (useMultiSearch ? "rag.multi_search" : "rag.search", 0, true) };
                _lastToolsMs = 0;
                _lastWriterMs = 0;
                _mem.LastToolNames = new List<string> { useMultiSearch ? "rag.multi_search" : "rag.search" };
                onProgress?.Invoke(string.Empty);

                var finalizedDeterministic = FinalizeAndReturn(swTotalPipeline, displayUserMessage, preWriterAnswer, deterministicSourcesPayload, ragPlan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
                return (true, finalizedDeterministic.finalAnswer, finalizedDeterministic.sourcesPayload);
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));

            var standaloneShouldBufferWriterOutputForSourceBackedGuard =
                standaloneShouldUseBroadSynthesis
                || standaloneShouldAvoidRawSourceBackedFallback
                || standaloneShouldRequireWriterForBroadFinal
                || ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
                || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage);
            var (answer, sources) = await AnswerAsync(
                chatHistory,
                effectiveUserMessage,
                ragPlan,
                toolResults,
                ct,
                standaloneShouldBufferWriterOutputForSourceBackedGuard ? null : onDelta,
                onProgress).ConfigureAwait(false);
            answer = (answer ?? string.Empty).Trim();
            if (ShouldFallbackFromNoRagDataAnswer(answer))
            {
                var replacedNoDataAnswer = false;
                if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
                {
                    var countdownAnswer = BuildSourceBackedCountdownPlanningAnswer(toolResults, effectiveUserMessage, plan.Language);
                    if (!string.IsNullOrWhiteSpace(countdownAnswer) && !ShouldFallbackFromNoRagDataAnswer(countdownAnswer))
                    {
                        answer = countdownAnswer;
                        sources = DeriveSourcesFromCountdownPlanningHits(toolResults, effectiveUserMessage);
                        replacedNoDataAnswer = true;
                    }
                }
                else if (!standaloneShouldUseBroadSynthesis
                    && !standaloneShouldAvoidRawSourceBackedFallback
                    && ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage)
                    && !ShouldAvoidDeterministicSourceBackedOptionFallback(effectiveUserMessage))
                {
                    var optionAnswer = BuildSourceBackedOptionAnswer(toolResults, plan.Language, minItems: 1, query: effectiveUserMessage);
                    if (!string.IsNullOrWhiteSpace(optionAnswer) && !ShouldFallbackFromNoRagDataAnswer(optionAnswer))
                    {
                        answer = optionAnswer;
                        sources = DeriveSourcesFromOptionHits(toolResults, effectiveUserMessage);
                        replacedNoDataAnswer = true;
                    }
                }

                if (!replacedNoDataAnswer)
                {
                    var repairAnswer = standaloneShouldBufferWriterOutputForSourceBackedGuard
                            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage)
                        ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                            chatHistory,
                            effectiveUserMessage,
                            plan,
                            toolResults,
                            ct).ConfigureAwait(false)
                        : string.Empty;

                    if (!string.IsNullOrWhiteSpace(repairAnswer)
                        && !ShouldFallbackFromNoRagDataAnswer(repairAnswer)
                        && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage)
                        && !LooksLikeWriterControlLeak(repairAnswer))
                    {
                        answer = repairAnswer.Trim();
                    }
                    else
                    {
                        answer = BuildSourceBackedSafeFallbackAnswer(
                            toolResults,
                            effectiveUserMessage,
                            plan.Language,
                            standaloneShouldAvoidRawSourceBackedFallback || standaloneShouldUseBroadSynthesis || standaloneShouldRequireWriterForBroadFinal);
                    }

                    if (string.IsNullOrWhiteSpace(answer))
                    {
                        answer = BuildSourceBackedSafeFallbackAnswer(
                            toolResults,
                            effectiveUserMessage,
                            plan.Language,
                            shouldAvoidRaw: true);
                    }

                    sources = DeriveSourcesForSourceBackedFallback(toolResults, effectiveUserMessage);
                }
            }
            else if ((!standaloneShouldUseBroadSynthesis
                    && !standaloneShouldAvoidRawSourceBackedFallback
                    && ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults))
                || LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
            {
                string deterministicAnswer;
                List<ToolMemory.SourceRef> deterministicSources;
                if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
                {
                    deterministicAnswer = BuildSourceBackedCountdownPlanningAnswer(toolResults, effectiveUserMessage, plan.Language);
                    deterministicSources = DeriveSourcesFromCountdownPlanningHits(toolResults, effectiveUserMessage);
                    if (string.IsNullOrWhiteSpace(deterministicAnswer))
                    {
                        deterministicAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                        deterministicSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                    }
                }
                else if (ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage)
                    && !ShouldAvoidDeterministicSourceBackedOptionFallback(effectiveUserMessage))
                {
                    deterministicAnswer = BuildSourceBackedOptionAnswer(toolResults, plan.Language, minItems: 1, query: effectiveUserMessage);
                    deterministicSources = DeriveSourcesFromOptionHits(toolResults, effectiveUserMessage);
                    if (string.IsNullOrWhiteSpace(deterministicAnswer))
                    {
                        deterministicAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                        deterministicSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                    }
                }
                else
                {
                    deterministicAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                    deterministicSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }

                if (!string.IsNullOrWhiteSpace(deterministicAnswer))
                {
                    answer = deterministicAnswer;
                    sources = deterministicSources;
                    if (LooksLikeMissingExactItemWithoutSourceLeads(answer))
                        sources.Clear();
                }
            }

            object? sourcesPayload = null;
            if (LooksLikeMissingExactItemWithoutSourceLeads(answer))
            {
                sources?.Clear();
                _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            }
            if (standaloneShouldBufferWriterOutputForSourceBackedGuard
                && ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
                && toolResults.Items.Any(static x => x.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(x.Result)))
            {
                var structuredSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    answer,
                    toolResults,
                    effectiveUserMessage,
                    plan.Language);
                if (ShouldRejectUnsupportedPlanningAnswerForFinal(structuredSupport, effectiveUserMessage)
                    || structuredSupport.Sources.Count == 0)
                {
                    if (TryBuildSupportedStructuredPlanningAnswer(
                            toolResults,
                            plan.Language,
                            effectiveUserMessage,
                            out var supportedStructuredAnswer,
                            out var supportedStructuredSources,
                            out var supportedStructuredSupport))
                    {
                        answer = supportedStructuredAnswer;
                        sources = supportedStructuredSources;
                        _lastAnswerSource = $"standalone_topic_rag_structured_planning_supported_rebuild:{ragPlan.Intent}";
                    }
                    else
                    {
                        var searchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(toolResults);
                        EmitPlanningInsufficientFallbackTrace(
                            "standalone-structured-guard",
                            ragPlan.Intent,
                            "supported_rebuild_unavailable",
                            Math.Max(structuredSupport.CandidateCount, supportedStructuredSupport.CandidateCount),
                            searchAlreadyExpanded);
                        answer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            effectiveUserMessage,
                            effectiveUserMessage,
                            Math.Max(structuredSupport.CandidateCount, supportedStructuredSupport.CandidateCount),
                            searchAlreadyExpanded: searchAlreadyExpanded);
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"standalone_topic_rag_structured_planning_rejected_unsupported:{ragPlan.Intent}";
                        LogSourceBackedPlanningTrace(
                            "standalone-structured-planning-rejected-unsupported",
                            toolResults,
                            effectiveUserMessage,
                            plan.Language);
                    }
                }
                else
                {
                    sources = structuredSupport.Sources.ToList();
                }
            }
            if (TryFinalizeSourceBackedPlanningResponse(
                    answer,
                    toolResults,
                    effectiveUserMessage,
                    plan.Language,
                    out var finalizedStandaloneAnswer,
                    out var finalizedStandaloneSources,
                    out var finalizedStandaloneAnalysis,
                    out var finalizedStandaloneResolution))
            {
                ClientLog.Info(
                    "ToolAgent planning standalone writer finalizer: " +
                    $"resolution={finalizedStandaloneResolution} " +
                    $"items={finalizedStandaloneAnalysis.ItemCount} " +
                    $"supported={finalizedStandaloneAnalysis.SupportedItemCount} " +
                    $"unsupported={finalizedStandaloneAnalysis.UnsupportedItemCount} " +
                    $"candidates={finalizedStandaloneAnalysis.CandidateCount} " +
                    $"sources={finalizedStandaloneSources.Count}");
                LogSourceBackedPlanningTrace(
                    "standalone-writer-finalizer",
                    toolResults,
                    effectiveUserMessage,
                    plan.Language);
                EmitPlanningFinalizerDecisionTrace(
                    "standalone-writer-finalizer",
                    ragPlan.Intent,
                    finalizedStandaloneResolution,
                    finalizedStandaloneAnalysis,
                    finalizedStandaloneSources.Count,
                    $"standalone_topic_rag:{finalizedStandaloneResolution}:{ragPlan.Intent}");
                answer = finalizedStandaloneAnswer;
                sources = finalizedStandaloneSources;
                _lastAnswerSource = $"standalone_topic_rag:{finalizedStandaloneResolution}:{ragPlan.Intent}";
            }
            if (ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(answer, toolResults, effectiveUserMessage, plan.Language))
            {
                sources?.Clear();
                _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            }

            if (sources is { Count: > 0 })
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
                answer = InjectInlineSources(answer, sources, plan.Language);
                sourcesPayload = BuildSourcesPayload(sources);
            }

            if (standaloneShouldBufferWriterOutputForSourceBackedGuard
                && onDelta is not null
                && !string.IsNullOrWhiteSpace(answer))
            {
                onDelta(answer);
            }

            if (string.IsNullOrWhiteSpace(_lastAnswerSource)
                || !_lastAnswerSource.Contains("structured_planning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_lastAnswerSource, $"standalone_topic_rag:{ragPlan.Intent}", StringComparison.Ordinal))
            {
                _lastAnswerSource = $"standalone_topic_rag:{ragPlan.Intent}";
            }
            _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { (useMultiSearch ? "rag.multi_search" : "rag.search", 0, true) };
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            _mem.LastToolNames = new List<string> { useMultiSearch ? "rag.multi_search" : "rag.search" };
            onProgress?.Invoke(string.Empty);

            var finalized = FinalizeAndReturn(swTotalPipeline, displayUserMessage, answer, sourcesPayload, ragPlan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
            return (true, finalized.finalAnswer, finalized.sourcesPayload);
        }
        catch
        {
            EmitRagTrace(
                "standalone_topic_rag.end",
                ("handled", false),
                ("reason", "error"));
            return (false, string.Empty, null);
        }
    }

    private static string BuildDocumentaryProbeClarification(IReadOnlyList<RagItem> hits, string language)
    {
        language = NormalizeLanguageCode(language);
        var first = hits[0];
        var firstLabel = string.IsNullOrWhiteSpace(first.DocName) ? (first.DocPath ?? "document") : first.DocName!;
        if (hits.Count == 1)
        {
            return language switch
            {
                "en" => $"I found a likely matching document: {firstLabel}. Do you want me to search in this one?",
                "es" => $"He encontrado un documento que parece coincidir: {firstLabel}. ¿Quieres que busque en ese documento?",
                "pt" => $"Encontrei um documento que parece corresponder: {firstLabel}. Queres que eu pesquise nesse documento?",
                "de" => $"Ich habe ein wahrscheinlich passendes Dokument gefunden: {firstLabel}. Soll ich in diesem Dokument suchen?",
                "it" => $"Ho trovato un documento che sembra corrispondere: {firstLabel}. Vuoi che cerchi in questo documento?",
                _ => $"J'ai trouvé un document qui semble correspondre : {firstLabel}. Veux-tu que je cherche dans celui-ci ?"
            };
        }

        var labels = hits.Select(x => string.IsNullOrWhiteSpace(x.DocName) ? (x.DocPath ?? "document") : x.DocName!).Take(3).ToList();
        var joined = string.Join(language == "fr" ? " ; " : "; ", labels);
        return language switch
        {
            "en" => $"I found several possible matches: {joined}. Which one should I use?",
            "es" => $"He encontrado varias coincidencias posibles: {joined}. ¿Cuál debo usar?",
            "pt" => $"Encontrei várias correspondências possíveis: {joined}. Qual devo usar?",
            "de" => $"Ich habe mehrere mögliche Treffer gefunden: {joined}. Welchen soll ich verwenden?",
            "it" => $"Ho trovato diverse corrispondenze possibili: {joined}. Quale devo usare?",
            _ => $"J'ai trouvé plusieurs correspondances possibles : {joined}. Laquelle veux-tu que j'utilise ?"
        };
    }

private string GuessLanguage(string userMessage)
{
    return ResolveInteractionLanguage(userMessage);
}
    private async Task<RouterPlan> RouterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        bool disallowMetaSetLanguage)
    {
        var sw = Stopwatch.StartNew();
        var manifestJson = ToolManifest.BuildRouterConversationManifestJson();
        var toolbook = ToolManifest.ConversationToolbookText;
        var repairHint = DocumentRefResolver.IsRepairMessage(userMessage);
        var resolverHint = DocumentRefResolver.Analyze(userMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);

        var memoryCtx = BuildCompactRouterMemoryContext(resolverHint);

        var detectedMessageLanguage = ResolveInteractionLanguage(userMessage);
        var useCompactSourceBackedRouter = ShouldUseCompactSourceBackedRouterPrompt(userMessage);
        var system = useCompactSourceBackedRouter
            ? BuildCompactSourceBackedRouterSystemPrompt(detectedMessageLanguage, disallowMetaSetLanguage)
            : PromptCatalog.BuildCompactRouterSystemPrompt(manifestJson, toolbook) + $@"

Additional runtime rules:
- Last answer language (informational only): {_mem.LastLanguage}
- Current message language hint: {detectedMessageLanguage}
- Disallow meta.set_language for this turn: {(disallowMetaSetLanguage ? "true" : "false")}
- The current message looks like a repair/correction turn: {(repairHint ? "true" : "false")}
- If document resolution hint says clarification is needed, prefer a short clarification over a blind tool call.
";

        var user = useCompactSourceBackedRouter
            ? BuildCompactSourceBackedRouterUserPrompt(chatHistory, userMessage)
            : $@"
MEMORY (json):
{JsonSerializer.Serialize(memoryCtx)}

CHAT_TAIL (for context):
{SerializeTail(chatHistory, maxTurns: 4)}

USER_MESSAGE:
{userMessage}
";

        string raw;
        try
        {
            EmitRagTrace(
                "router.llm.start",
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("history", chatHistory.Count),
                ("user_chars", userMessage.Length),
                ("prompt_chars", system.Length + user.Length));
            ClientLog.Info(
                "ToolAgent router llm request: " +
                $"disallowMetaSetLanguage={disallowMetaSetLanguage}|compactSourceRouter={useCompactSourceBackedRouter}|history={chatHistory.Count}|userChars={userMessage.Length}|promptChars={(system.Length + user.Length)}");
            var routerTimeoutMs = useCompactSourceBackedRouter
                ? SourceBackedRouterLlmTimeoutMs
                : RouterLlmTimeoutMs;
            using var routerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            routerTimeoutCts.CancelAfter(routerTimeoutMs);
            raw = await CompleteWithRetryAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, routerTimeoutCts.Token).ConfigureAwait(false);
            EmitRagTrace(
                "router.llm.end",
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("raw_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds));
            ClientLog.Info(
                "ToolAgent router llm response: " +
                $"rawChars={raw?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "router.llm.fallback",
                ("reason", "llm_timeout"),
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("timeout_ms", useCompactSourceBackedRouter ? SourceBackedRouterLlmTimeoutMs : RouterLlmTimeoutMs),
                ("ms", sw.ElapsedMilliseconds));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=llm_timeout|timeoutMs={(useCompactSourceBackedRouter ? SourceBackedRouterLlmTimeoutMs : RouterLlmTimeoutMs)}|ms={sw.ElapsedMilliseconds}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "router.llm.fallback",
                ("reason", "llm_error"),
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("ms", sw.ElapsedMilliseconds),
                ("error", TruncateForPrompt(ex.Message, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=llm_error|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }

        if (!TryExtractJsonObject(raw ?? string.Empty, out var planJson))
        {
            // Fallback safe: conversationnel sans tools
            EmitRagTrace(
                "router.llm.no_json",
                ("raw_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds),
                ("preview", TruncateForPrompt(raw, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=no_json|rawChars={raw?.Length ?? 0}|ms={sw.ElapsedMilliseconds}|preview={TruncateForPrompt(raw, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }

        try
        {
            var sanitized = ParseAndSanitizeLlmRouterPlanJson(planJson, detectedMessageLanguage, disallowMetaSetLanguage);
            ClientLog.Info(
                "ToolAgent router parsed: " +
                $"intent={sanitized.Intent}|mode={sanitized.Mode}|lang={sanitized.Language}|tools={sanitized.ToolCalls.Count}|" +
                $"toolNames={string.Join(",", sanitized.ToolCalls.Select(x => x.Name))}|clarification={sanitized.NeedClarification}|ms={sw.ElapsedMilliseconds}");
            return sanitized;
        }
        catch (Exception ex)
        {
            if (TryRepairJsonObjectForParsing(planJson, out var repairedPlanJson)
                && !string.Equals(planJson, repairedPlanJson, StringComparison.Ordinal))
            {
                try
                {
                    var sanitized = ParseAndSanitizeLlmRouterPlanJson(repairedPlanJson, detectedMessageLanguage, disallowMetaSetLanguage);
                    EmitRagTrace(
                        "router.llm.parse_repaired",
                        ("ms", sw.ElapsedMilliseconds),
                        ("error", TruncateForPrompt(ex.Message, 180)),
                        ("original_json", TruncateForPrompt(planJson, 220)),
                        ("repaired_json", TruncateForPrompt(repairedPlanJson, 220)));
                    ClientLog.Info(
                        "ToolAgent router parsed after json repair: " +
                        $"intent={sanitized.Intent}|mode={sanitized.Mode}|lang={sanitized.Language}|tools={sanitized.ToolCalls.Count}|" +
                        $"toolNames={string.Join(",", sanitized.ToolCalls.Select(x => x.Name))}|clarification={sanitized.NeedClarification}|ms={sw.ElapsedMilliseconds}");
                    return sanitized;
                }
                catch (Exception repairEx)
                {
                    EmitRagTrace(
                        "router.llm.parse_repair_failed",
                        ("ms", sw.ElapsedMilliseconds),
                        ("error", TruncateForPrompt(ex.Message, 180)),
                        ("repair_error", TruncateForPrompt(repairEx.Message, 180)),
                        ("json", TruncateForPrompt(planJson, 220)));
                    ClientLog.Info(
                        "ToolAgent router json repair failed: " +
                        $"ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 180)}|repairError={TruncateForPrompt(repairEx.Message, 180)}");
                }
            }

            EmitRagTrace(
                "router.llm.parse_error",
                ("ms", sw.ElapsedMilliseconds),
                ("error", TruncateForPrompt(ex.Message, 260)),
                ("json", TruncateForPrompt(planJson, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=parse_error|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 260)}|json={TruncateForPrompt(planJson, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }
    }

    private RouterPlan ParseAndSanitizeLlmRouterPlanJson(
        string planJson,
        string detectedMessageLanguage,
        bool disallowMetaSetLanguage)
    {
        var plan = JsonSerializer.Deserialize<RouterPlan>(planJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new RouterPlan();

        using var legacyDoc = JsonDocument.Parse(planJson);
        if (!plan.RouterConfidence.HasValue
            && legacyDoc.RootElement.TryGetProperty("confidence", out var confidenceEl)
            && confidenceEl.ValueKind == JsonValueKind.Number
            && confidenceEl.TryGetDouble(out var legacyConfidence))
        {
            plan.RouterConfidence = legacyConfidence;
        }

        var sanitized = SanitizeRouterPlan(plan, detectedMessageLanguage, disallowMetaSetLanguage);
        sanitized.Origin = RouterPlanOrigin.Llm;
        return sanitized;
    }

    private async Task<RouterPlan> TryRepairStructuredRouterSearchPlanAsync(
        RouterPlan plan,
        string effectiveUserMessage,
        CancellationToken ct)
    {
        if (plan.Origin != RouterPlanOrigin.Llm
            || plan.NeedClarification
            || !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            || !plan.ToolCalls.Any(call => IsRagToolName(NormalizeToolName(call.Name))))
        {
            return plan;
        }

        var language = NormalizeLanguageCode(plan.Language);
        var missingAxes = DetectMissingStructuredRouterSearchAxes(plan, effectiveUserMessage, language);
        if (missingAxes.Length == 0)
            return plan;

        var existingQueries = ExtractRouterPlanRagQueries(plan).Take(12).ToArray();
        EmitRagTrace(
            "router.repair.start",
            ("reason", "missing_structured_search_axes"),
            ("missing_axes", missingAxes),
            ("existing_queries", existingQueries),
            ("tools", plan.ToolCalls.Count));

        var system = BuildStructuredRouterRepairSystemPrompt(language);
        var user = BuildStructuredRouterRepairUserPrompt(plan, effectiveUserMessage, language, missingAxes, existingQueries);
        var sw = Stopwatch.StartNew();
        try
        {
            using var repairTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            repairTimeoutCts.CancelAfter(SourceBackedRouterLlmTimeoutMs);
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    repairTimeoutCts.Token)
                .ConfigureAwait(false);

            if (!TryExtractJsonObject(raw ?? string.Empty, out var json))
            {
                EmitRagTrace(
                    "router.repair.end",
                    ("accepted", false),
                    ("reason", "no_json"),
                    ("missing_axes", missingAxes),
                    ("raw_chars", raw?.Length ?? 0),
                    ("elapsed_ms", sw.ElapsedMilliseconds));
                return TryApplyStructuredRouterSearchAxisFallbackPlan(
                    plan,
                    effectiveUserMessage,
                    language,
                    missingAxes,
                    existingQueries,
                    reason: "no_json_preserve_missing_axes",
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            var repairedPlan = JsonSerializer.Deserialize<RouterPlan>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RouterPlan();
            repairedPlan = SanitizeRouterPlan(repairedPlan, language, disallowMetaSetLanguage: false);
            repairedPlan.Origin = RouterPlanOrigin.Llm;
            repairedPlan.Language = ResolveTurnLanguage(effectiveUserMessage, repairedPlan.Language, language);
            if (string.IsNullOrWhiteSpace(repairedPlan.Intent) || string.Equals(repairedPlan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
                repairedPlan.Intent = InferIntentFromToolCalls(repairedPlan.ToolCalls) ?? plan.Intent;

            var repairedMissingAxes = DetectMissingStructuredRouterSearchAxes(repairedPlan, effectiveUserMessage, repairedPlan.Language);
            var repairedQueries = ExtractRouterPlanRagQueries(repairedPlan).Take(12).ToArray();
            var regressedAxes = FindStructuredRouterSearchAxisRegressions(missingAxes, repairedMissingAxes);
            var accepted = repairedPlan.ToolCalls.Any(call => IsRagToolName(NormalizeToolName(call.Name)))
                           && repairedMissingAxes.Length < missingAxes.Length
                           && regressedAxes.Length == 0;
            EmitRagTrace(
                "router.repair.end",
                ("accepted", accepted),
                ("reason", accepted ? "coverage_improved" : regressedAxes.Length > 0 ? "coverage_regressed" : "coverage_not_improved"),
                ("missing_before", missingAxes),
                ("missing_after", repairedMissingAxes),
                ("regressed_axes", regressedAxes),
                ("queries_before", existingQueries),
                ("queries_after", repairedQueries),
                ("elapsed_ms", sw.ElapsedMilliseconds));

            if (!accepted)
            {
                return TryApplyStructuredRouterSearchAxisFallbackPlan(
                    plan,
                    effectiveUserMessage,
                    language,
                    missingAxes,
                    existingQueries,
                    reason: "coverage_not_improved_preserve_missing_axes",
                    elapsedMs: sw.ElapsedMilliseconds);
            }

            ClientLog.Info(
                "ToolAgent router repair accepted: " +
                $"missingBefore={string.Join(",", missingAxes)}|missingAfter={string.Join(",", repairedMissingAxes)}|" +
                $"queries={string.Join(" || ", repairedQueries)}|ms={sw.ElapsedMilliseconds}");
            return repairedPlan;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "router.repair.end",
                ("accepted", false),
                ("reason", "timeout"),
                ("missing_axes", missingAxes),
                ("timeout_ms", SourceBackedRouterLlmTimeoutMs),
                ("elapsed_ms", sw.ElapsedMilliseconds));
            return TryApplyStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                existingQueries,
                reason: "timeout_preserve_missing_axes",
                elapsedMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "router.repair.end",
                ("accepted", false),
                ("reason", "error"),
                ("missing_axes", missingAxes),
                ("error", TruncateForPrompt(ex.Message, 220)),
                ("elapsed_ms", sw.ElapsedMilliseconds));
            return TryApplyStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                existingQueries,
                reason: "error_preserve_missing_axes",
                elapsedMs: sw.ElapsedMilliseconds);
        }
    }

    private RouterPlan TryApplyStructuredRouterSearchAxisFallbackPlan(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        IReadOnlyList<string> existingQueries,
        string reason,
        long elapsedMs)
    {
        if (!TryBuildStructuredRouterSearchAxisFallbackPlan(
                plan,
                effectiveUserMessage,
                language,
                missingAxes,
                out var fallbackPlan,
                out var fallbackQueries,
                out var missingAfter))
        {
            return plan;
        }

        EmitRagTrace(
            "router.repair.fallback",
            ("accepted", true),
            ("reason", reason),
            ("missing_before", missingAxes),
            ("missing_after", missingAfter),
            ("queries_before", existingQueries),
            ("queries_after", fallbackQueries.Take(12).ToArray()),
            ("elapsed_ms", elapsedMs));

        ClientLog.Info(
            "ToolAgent router repair fallback accepted: " +
            $"reason={reason}|missingBefore={string.Join(",", missingAxes)}|missingAfter={string.Join(",", missingAfter)}|" +
            $"queries={string.Join(" || ", fallbackQueries.Take(12))}|ms={elapsedMs}");

        return fallbackPlan;
    }

    private static string[] DetectMissingStructuredRouterSearchAxes(
        RouterPlan plan,
        string effectiveUserMessage,
        string language)
        => DetectMissingStructuredRouterSearchAxesForQueries(
            ExtractRouterPlanRagQueries(plan),
            effectiveUserMessage,
            language);

    private static string[] DetectMissingStructuredRouterSearchAxesForQueries(
        IReadOnlyList<string> queries,
        string effectiveUserMessage,
        string language)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
            return Array.Empty<string>();

        language = NormalizeLanguageCode(language);
        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var explicitRequestedAxes = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        if (dayAxis.Count < 2 || explicitRequestedAxes.Count == 0)
            return Array.Empty<string>();

        var normalizedQueries = queries
            .Select(NormalizeLexicalLookup)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedQueries.Length == 0)
            return explicitRequestedAxes.ToArray();

        var requestedAxisTerms = BuildStructuredPlanningSlotTermGroups(explicitRequestedAxes, effectiveUserMessage)
            .Select(group => new
            {
                Label = group.Label,
                Terms = group.PrimaryTerms
                    .Concat(group.AlternativeTerms)
                    .Where(static term => !string.IsNullOrWhiteSpace(term))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            })
            .Where(static axis => axis.Terms.Length > 0)
            .ToArray();

        var allRequestedAxisTerms = requestedAxisTerms
            .Select(static axis => axis.Terms)
            .ToArray();

        var missing = new List<string>();
        foreach (var axis in requestedAxisTerms)
        {
            if (!normalizedQueries.Any(query => QueryCoversFocusedStructuredRouterSearchAxis(query, axis.Terms, allRequestedAxisTerms)))
                missing.Add(axis.Label);
        }

        return missing
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] FindStructuredRouterSearchAxisRegressions(
        IReadOnlyList<string> missingBefore,
        IReadOnlyList<string> missingAfter)
    {
        if (missingAfter.Count == 0)
            return Array.Empty<string>();

        var missingBeforeSet = new HashSet<string>(missingBefore, StringComparer.OrdinalIgnoreCase);
        return missingAfter
            .Where(axis => !missingBeforeSet.Contains(axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool QueryCoversFocusedStructuredRouterSearchAxis(
        string normalizedQuery,
        IReadOnlyList<string> axisTerms,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)
            || axisTerms.Count == 0
            || !axisTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
        {
            return false;
        }

        var mentionedAxes = CountStructuredRouterSearchAxesMentioned(normalizedQuery, requestedAxisTerms);
        if (requestedAxisTerms.Count >= 3 && mentionedAxes >= Math.Min(requestedAxisTerms.Count, 3))
            return false;

        return true;
    }

    private static int CountStructuredRouterSearchAxesMentioned(
        string normalizedQuery,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        var count = 0;
        foreach (var terms in requestedAxisTerms)
        {
            if (terms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term)))
                count++;
        }

        return count;
    }

    private static string[] ExtractRouterPlanRagQueries(RouterPlan plan)
    {
        var queries = new List<string>();
        foreach (var call in plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
        {
            var normalizedName = NormalizeToolName(call.Name);
            if (string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctRagQuery(queries, TryGetStringArg(call.Args, "query"));
            }
            else if (string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var query in NormalizeRagMultiSearchQueries(call.Args))
                    AddDistinctRagQuery(queries, query);
            }
        }

        return queries.ToArray();
    }

    private static bool TryBuildStructuredRouterSearchAxisFallbackPlan(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        out RouterPlan fallbackPlan,
        out string[] fallbackQueries,
        out string[] missingAfter)
    {
        fallbackPlan = plan;
        fallbackQueries = ExtractRouterPlanRagQueries(plan);
        missingAfter = missingAxes.ToArray();

        if (missingAxes.Count == 0)
            return false;

        var toolCalls = new List<RouterPlan.ToolCall>();
        var updated = false;
        foreach (var call in plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
        {
            var normalizedName = NormalizeToolName(call.Name);
            if (!updated && string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                var originalQueries = NormalizeRagMultiSearchQueries(call.Args);
                var enriched = BuildStructuredRouterAxisFallbackQueries(
                    originalQueries,
                    missingAxes,
                    effectiveUserMessage,
                    language);
                if (enriched.Length > originalQueries.Length)
                {
                    toolCalls.Add(new RouterPlan.ToolCall
                    {
                        Name = "rag.multi_search",
                        Args = BuildStructuredRouterAxisFallbackArgs(call.Args, enriched)
                    });
                    updated = true;
                    continue;
                }
            }

            toolCalls.Add(new RouterPlan.ToolCall
            {
                Name = call.Name,
                Args = call.Args.ValueKind == JsonValueKind.Undefined ? default : call.Args.Clone()
            });
        }

        if (!updated)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var normalizedName = NormalizeToolName(toolCalls[i].Name);
                if (!string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase))
                    continue;

                var enriched = BuildStructuredRouterAxisFallbackQueries(
                    new[] { TryGetStringArg(toolCalls[i].Args, "query") ?? string.Empty },
                    missingAxes,
                    effectiveUserMessage,
                    language);
                if (enriched.Length == 0)
                    continue;

                toolCalls[i] = new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = BuildStructuredRouterAxisFallbackArgs(toolCalls[i].Args, enriched, convertSearchToMultiSearch: true)
                };
                updated = true;
                break;
            }
        }

        if (!updated)
            return false;

        fallbackPlan = new RouterPlan
        {
            Mode = plan.Mode,
            Language = NormalizeLanguageCode(string.IsNullOrWhiteSpace(plan.Language) ? language : plan.Language),
            Intent = string.IsNullOrWhiteSpace(plan.Intent) || string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
                ? "rag.answer"
                : plan.Intent,
            ResponseFormat = plan.ResponseFormat,
            Origin = plan.Origin,
            NeedClarification = false,
            ClarificationQuestions = new List<string>(),
            ReasoningTracePublic = plan.ReasoningTracePublic?.ToList() ?? new List<string>(),
            RiskFlags = plan.RiskFlags?.ToList() ?? new List<string>(),
            MemoryUpdate = plan.MemoryUpdate,
            RouterConfidence = plan.RouterConfidence,
            ToolCalls = toolCalls
        };

        fallbackQueries = ExtractRouterPlanRagQueries(fallbackPlan);
        missingAfter = DetectMissingStructuredRouterSearchAxes(fallbackPlan, effectiveUserMessage, fallbackPlan.Language);
        return missingAfter.Length < missingAxes.Count;
    }

    private static string[] BuildStructuredRouterAxisFallbackQueries(
        IEnumerable<string> existingQueries,
        IReadOnlyList<string> missingAxes,
        string? effectiveUserMessage,
        string language)
    {
        var originalQueries = new List<string>();
        foreach (var query in existingQueries)
            AddDistinctRagQuery(originalQueries, query);

        var genericInventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, effectiveUserMessage)
            .Take(1)
            .ToArray();

        var axisVariants = missingAxes
            .Select(axis => BuildStructuredRouterAxisFallbackVariants(axis, effectiveUserMessage))
            .Where(static variants => variants.Length > 0)
            .ToArray();

        var queries = new List<string>();
        var prioritizedOriginalQueries = originalQueries.ToArray();

        var firstExistingCount = 0;
        foreach (var query in prioritizedOriginalQueries.Take(firstExistingCount))
            AddDistinctRagQuery(queries, query);

        foreach (var variants in axisVariants)
        {
            foreach (var inventory in genericInventoryTerms.Take(1))
                AddRouterAxisFallbackQueryIfMissing(queries, $"{variants[0]} {inventory}", axisVariants);
            AddRouterAxisFallbackQueryIfMissing(queries, variants[0], axisVariants);
        }

        foreach (var query in prioritizedOriginalQueries.Skip(firstExistingCount))
        {
            if (queries.Count >= 8)
                break;
            AddDistinctRagQuery(queries, query);
        }

        for (var variantIndex = 1; queries.Count < 8 && variantIndex < 4; variantIndex++)
        {
            foreach (var variants in axisVariants)
            {
                if (queries.Count >= 8)
                    break;
                if (variantIndex < variants.Length)
                    AddRouterAxisFallbackQueryIfMissing(queries, variants[variantIndex], axisVariants);
            }
        }

        return queries.ToArray();
    }

    private static string[] BuildStructuredRouterAxisFallbackVariants(
        string axis,
        string? effectiveUserMessage)
    {
        var group = BuildStructuredPlanningSlotTermGroups(new[] { axis }, effectiveUserMessage)
            .FirstOrDefault();
        var terms = group is null
            ? ExpandPlanningSlotRetrievalTermVariants(axis).Select(NormalizeLexicalLookup)
            : group.PrimaryTerms.Concat(group.AlternativeTerms);

        return terms
            .Select(CollapseWhitespace)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRouterAxisFallbackQueryIfMissing(
        List<string> queries,
        string query,
        IReadOnlyList<string[]> requestedAxisTerms)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return;

        var normalizedQueries = queries
            .Select(NormalizeLexicalLookup)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (normalizedQueries.Any(existing => QueryCoversFocusedStructuredRouterSearchAxis(
                existing,
                new[] { normalizedQuery },
                requestedAxisTerms)))
        {
            return;
        }

        AddDistinctRagQuery(queries, query);
    }

    private static JsonElement BuildStructuredRouterAxisFallbackArgs(
        JsonElement originalArgs,
        IReadOnlyList<string> queries,
        bool convertSearchToMultiSearch = false)
    {
        var args = ParseRouterArgsObject(originalArgs);
        if (convertSearchToMultiSearch)
            args.Remove("query");

        var queryArray = new JsonArray();
        foreach (var query in queries)
            queryArray.Add(query);
        args["queries"] = queryArray;

        if (!args.ContainsKey("topK"))
            args["topK"] = 8;
        if (!args.ContainsKey("mode"))
            args["mode"] = "broad";
        if (!args.ContainsKey("researchMode"))
            args["researchMode"] = "source_exploration";
        if (!args.ContainsKey("includeResearchSurfaces"))
            args["includeResearchSurfaces"] = true;

        return JsonSerializer.SerializeToElement(args).Clone();
    }

    private static JsonObject ParseRouterArgsObject(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        try
        {
            return JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string BuildStructuredRouterRepairSystemPrompt(string language)
        => $@"
You are SAAIA Router Repair. Output ONLY valid JSON.
Target language: {NormalizeLanguageCode(language)}.

The previous router already chose a source-backed route, but its tool queries omitted explicit requested slots/types.
Correct only the route/toolCalls. Do not answer the user.

Rules:
- Keep the LLM as orchestrator: decide the corrected retrieval strategy yourself from the request, request shape, category hints and existing tool calls.
- Use rag.multi_search for broad source-backed structured plans.
- Preserve all explicit requested slots/types/criteria/phases in the query strategy before execution.
- If a category hint clearly fits semantically, use its exact category path; otherwise keep category/categoryPath null.
- Prefer 4 to 8 short complementary queries. Avoid one query per day/row/column.
- Avoid decorative variants around one broad noun. Queries need a requested slot/type, a concrete source label, a constraint or a candidate name.

Return the normal RouterPlan JSON:
{{""mode"":""auto|standard|strict"",""language"":""fr|en|es|pt|de|it"",""intent"":""rag.answer|rag.compare|rag.followup"",""responseFormat"":""auto"",""needClarification"":false,""clarificationQuestions"":[],""reasoningTracePublic"":[],""riskFlags"":[],""memoryUpdate"":null,""routerConfidence"":0.0,""toolCalls"":[{{""name"":""rag.multi_search"",""args"":{{""queries"":[""short query""],""topK"":8,""category"":null,""mode"":""broad"",""researchMode"":""source_exploration"",""includeResearchSurfaces"":true}}}}]}}";

    private string BuildStructuredRouterRepairUserPrompt(
        RouterPlan plan,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> missingAxes,
        IReadOnlyList<string> existingQueries)
        => $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

CATEGORY_HINTS:
{BuildSourceBackedLlmCategoryHintsForPrompt(effectiveUserMessage, maxCategories: 16)}

MISSING_REQUESTED_SLOTS_OR_TYPES:
{FormatPromptList(missingAxes, 12, maxItemLength: 80)}

EXISTING_ROUTER_QUERIES:
{FormatPromptList(existingQueries, 12, maxItemLength: 80)}

EXISTING_TOOL_CALLS:
{FormatRouterToolCallsForPrompt(plan)}

TASK:
Return a corrected RouterPlan JSON whose rag.multi_search queries cover the missing requested slots/types and remain complementary to the existing plan.
Do not add final-answer text.";

    private static string FormatRouterToolCallsForPrompt(RouterPlan plan)
    {
        var lines = (plan.ToolCalls ?? new List<RouterPlan.ToolCall>())
            .Select(call => $"- {NormalizeToolName(call.Name)}: {TruncateForPrompt(call.Args.GetRawText(), 500)}")
            .ToArray();
        return lines.Length == 0 ? "- none" : string.Join(Environment.NewLine, lines);
    }

    private static bool ShouldUseCompactSourceBackedRouterPrompt(string? userMessage)
        => LooksLikeSourceBackedBroadResearchRequest(userMessage)
           || LooksLikeAnyDocumentaryPlanningRequest(userMessage)
           || ShouldUseResearchSurfacesForBroadRagRequest(userMessage);

    private static string BuildCompactSourceBackedRouterSystemPrompt(string detectedLanguage, bool disallowMetaSetLanguage)
        => $@"
You are SAAIA Router. Output ONLY valid JSON.
Decide the path for the current message:
- chat.general with no tools for normal chat, greetings, or questions that do not need documents.
- rag.search for a narrow document/source question.
- rag.multi_search for broad source-backed plans, recommendations, comparisons, selections, or multi-candidate requests.
- clarification only if a safe tool choice is impossible.

Available tools:
- documents.categories args: {{""path"":null,""categoryRef"":null,""limit"":40,""offset"":0}}
- documents.navigation args: {{""categoryPath"":null,""docPath"":null,""q"":null,""limit"":80,""offset"":0}}
- documents.context args: {{""docId"":null,""docPath"":null,""chunkId"":null,""pageStart"":null,""pageEnd"":null,""before"":2,""after"":4,""limit"":12}}
- rag.search args: {{""query"":""short query"",""topK"":8,""mode"":""balanced""}}
- rag.multi_search args: {{""queries"":[""short query""],""topK"":8,""category"":null,""mode"":""broad"",""researchMode"":""source_exploration"",""includeResearchSurfaces"":true}}

Rules:
- Language hint: {NormalizeLanguageCode(detectedLanguage)}.
- Disallow meta.set_language: {(disallowMetaSetLanguage ? "true" : "false")}.
- If the user says documents, sources, available documents, PDFs, corpus, or asks for sourced facts, use RAG.
- For weekly plans or broad candidate requests, choose rag.multi_search with 2-4 compact candidate queries.
- If the user names distinct slots, criteria, phases, roles or option kinds, preserve those explicit types in the search strategy. Do not omit a requested type just because a broader noun is present.
- CATEGORY_HINTS are exact known categories. If one clearly fits the user need, set args.category to the exact categoryPath; otherwise null.
- Use documents.categories/documents.navigation as maps when you need to inspect corpus structure; do not use them as final factual evidence.
- Use documents.context only when the user or previous context gives a concrete doc/page/chunk anchor to read around.
- Never invent a category and never copy explanatory text into category.
- Do not use sommaire, index, table of contents, catalogue, list/liste as initial queries.
- Keep queries short. Remove filler such as je ne sais pas, peux-tu, disponible, source, utile.

Schema:
{{""mode"":""auto|standard|strict"",""language"":""fr|en|es|pt|de|it"",""intent"":""chat.general|rag.answer|rag.compare|rag.followup"",""responseFormat"":""auto"",""needClarification"":false,""clarificationQuestions"":[],""reasoningTracePublic"":[],""riskFlags"":[],""memoryUpdate"":null,""routerConfidence"":0.0,""toolCalls"":[{{""name"":""documents.categories|documents.navigation|documents.context|rag.search|rag.multi_search"",""args"":{{}}}}]}}
";

    private string BuildCompactSourceBackedRouterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage)
        => $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 2)}

CATEGORY_HINTS:
{BuildSourceBackedLlmCategoryHintsForPrompt(userMessage, maxCategories: 16)}

USER_MESSAGE:
{userMessage}
";

    private Dictionary<string, object?> BuildRouterMemoryContext(DocumentRefResolver.AnalysisResult resolverHint)
    {
        return new Dictionary<string, object?>
        {
            ["profile"] = _mem.MemoryProfile,
            ["lastLanguage"] = _mem.LastLanguage,
            ["lastUserDetectedLanguage"] = _mem.LastUserDetectedLanguage,
            ["lastAnswerLanguage"] = _mem.LastAnswerLanguage,
            ["lastList"] = new Dictionary<string, object?>
            {
                ["offset"] = _mem.LastListOffset,
                ["limit"] = _mem.LastListLimit,
                ["categoryPath"] = _mem.LastListCategoryPath,
                ["q"] = _mem.LastListQuery,
                ["total"] = _mem.LastListTotal
            },
            ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
            {
                ["docId"] = _mem.LastFocusedDocument.DocId,
                ["docPath"] = _mem.LastFocusedDocument.DocPath,
                ["docName"] = _mem.LastFocusedDocument.DocName,
                ["category"] = _mem.LastFocusedDocument.Category,
                ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
            },
            ["lastTurn"] = new Dictionary<string, object?>
            {
                ["user"] = _mem.LastUserMessage,
                ["assistant"] = _mem.LastAssistantAnswer,
                ["routerIntent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["reasoningTracePublic"] = _mem.LastReasoningTracePublic,
                ["routerConfidence"] = _mem.LastRouterConfidence,
                ["riskFlags"] = _mem.LastRiskFlags,
                ["mode"] = _mem.LastMode,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate
            },
            ["pendingClarification"] = _mem.PendingClarification is null ? null : new Dictionary<string, object?>
            {
                ["kind"] = _mem.PendingClarification.Kind,
                ["originalUserMessage"] = _mem.PendingClarification.OriginalUserMessage,
                ["hint"] = _mem.PendingClarification.Hint,
                ["language"] = _mem.PendingClarification.Language,
                ["createdAtUtc"] = _mem.PendingClarification.CreatedAtUtc
            },
            ["adminSession"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["lastResolvedCategory"] = _mem.LastResolvedCategory is null ? null : new Dictionary<string, object?>
            {
                ["categoryRef"] = _mem.LastResolvedCategory.CategoryRef,
                ["categoryPath"] = _mem.LastResolvedCategory.CategoryPath,
                ["displayName"] = _mem.LastResolvedCategory.DisplayName,
                ["ordinal"] = _mem.LastResolvedCategory.Ordinal,
                ["totalDocuments"] = _mem.LastResolvedCategory.TotalDocuments,
                ["aliases"] = _mem.LastResolvedCategory.Aliases
            },
            ["lastPresentedCategories"] = _mem.LastPresentedCategories?.Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["totalDocuments"] = x.TotalDocuments,
                ["aliases"] = x.Aliases
            }).ToList(),
            ["m1Lite"] = new Dictionary<string, object?>
            {
                ["canonicalCategories"] = BuildRouterCanonicalCategoryHints(),
                ["canonicalDocuments"] = BuildRouterCanonicalDocumentHints()
            },
            ["lastSummaryStatus"] = _mem.LastSummaryStatusSnapshot is null ? null : new Dictionary<string, object?>
            {
                ["categoryPath"] = _mem.LastSummaryStatusSnapshot.CategoryPath,
                ["categoryRef"] = _mem.LastSummaryStatusSnapshot.CategoryRef,
                ["mode"] = _mem.LastSummaryStatusSnapshot.Mode,
                ["total"] = _mem.LastSummaryStatusSnapshot.Total,
                ["missingStored"] = _mem.LastSummaryStatusSnapshot.MissingStored,
                ["staleStored"] = _mem.LastSummaryStatusSnapshot.StaleStored,
                ["profileMissing"] = _mem.LastSummaryStatusSnapshot.ProfileMissing,
                ["itemsCount"] = _mem.LastSummaryStatusSnapshot.Items?.Count ?? 0
            },
            ["resolverHint"] = new Dictionary<string, object?>
            {
                ["isContentRequest"] = resolverHint.IsContentRequest,
                ["wantsAbout"] = resolverHint.WantsAbout,
                ["wantsSummary"] = resolverHint.WantsSummary,
                ["wantsStoredSummaryCheck"] = resolverHint.WantsStoredSummaryCheck,
                ["wantsStoredSummaryStore"] = resolverHint.WantsStoredSummaryStore,
                ["resolvedDocRef"] = resolverHint.ResolvedDocRef,
                ["needsClarification"] = resolverHint.NeedsClarification,
                ["clarificationKind"] = resolverHint.ClarificationKind
            }
        };
    }

    private Dictionary<string, object?> BuildCompactRouterMemoryContext(DocumentRefResolver.AnalysisResult resolverHint)
    {
        return new Dictionary<string, object?>
        {
            ["lastLanguage"] = _mem.LastLanguage,
            ["lastIntent"] = _mem.LastRouterIntent,
            ["lastToolNames"] = _mem.LastToolNames?.Take(5).ToArray() ?? Array.Empty<string>(),
            ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
            {
                ["docId"] = _mem.LastFocusedDocument.DocId,
                ["docPath"] = _mem.LastFocusedDocument.DocPath,
                ["docName"] = _mem.LastFocusedDocument.DocName,
                ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath
            },
            ["pendingClarification"] = _mem.PendingClarification is null ? null : new Dictionary<string, object?>
            {
                ["kind"] = _mem.PendingClarification.Kind,
                ["hint"] = _mem.PendingClarification.Hint,
                ["language"] = _mem.PendingClarification.Language
            },
            ["lastResolvedCategory"] = _mem.LastResolvedCategory is null ? null : new Dictionary<string, object?>
            {
                ["categoryRef"] = _mem.LastResolvedCategory.CategoryRef,
                ["categoryPath"] = _mem.LastResolvedCategory.CategoryPath,
                ["displayName"] = _mem.LastResolvedCategory.DisplayName
            },
            ["resolverHint"] = new Dictionary<string, object?>
            {
                ["isContentRequest"] = resolverHint.IsContentRequest,
                ["wantsAbout"] = resolverHint.WantsAbout,
                ["wantsSummary"] = resolverHint.WantsSummary,
                ["wantsStoredSummaryCheck"] = resolverHint.WantsStoredSummaryCheck,
                ["resolvedDocRef"] = resolverHint.ResolvedDocRef,
                ["needsClarification"] = resolverHint.NeedsClarification,
                ["clarificationKind"] = resolverHint.ClarificationKind
            }
        };
    }

    private List<Dictionary<string, object?>> BuildRouterCanonicalCategoryHints()
        => (_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DisplayName) || !string.IsNullOrWhiteSpace(x.CategoryPath))
            .OrderBy(x => x.Ordinal == 0 ? int.MaxValue : x.Ordinal)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["aliases"] = x.Aliases?.Take(4).ToList()
            })
            .ToList();

    private List<Dictionary<string, object?>> BuildRouterCanonicalDocumentHints()
        => (_mem.WorkspaceKnownDocuments ?? new List<ToolMemory.DocumentItem>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DocName) || !string.IsNullOrWhiteSpace(x.DocPath))
            .OrderBy(x => x.DocName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["docId"] = x.DocId,
                ["docPath"] = x.DocPath,
                ["docName"] = x.DocName,
                ["category"] = x.Category,
                ["categoryPath"] = x.CategoryPath
            })
            .ToList();

    private static string DescribeToolAction(string toolName, string userMessage, string language, JsonElement args)
    {
        _ = userMessage;
        var docRef = GetStringArg(args, "docRef") ?? GetStringArg(args, "pdfRef") ?? string.Empty;

        return toolName switch
        {
            "summary.exists" => DeterministicAgentText.ProgressCheckStoredSummaryForDocument(docRef, language),
            "summary.get" => DeterministicAgentText.ProgressLoadStoredSummaryForDocument(docRef, language),
            "rag.summarize_live" => DeterministicAgentText.ProgressBuildLiveSummaryForDocument(docRef, language),
            _ => DeterministicAgentText.ToolAction(toolName, language)
        };
    }

    private async Task<ToolResults> ExecuteToolsAsync(RouterPlan plan, string userMessage, CancellationToken ct, Action<string>? onPhase, Action<string>? onProgress)
    {
        var results = new ToolResults();

        foreach (var call in plan.ToolCalls)
        {
            if (plan.Origin == RouterPlanOrigin.Llm
                && string.Equals(NormalizeToolName(call.Name), "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                call.Args = TrustResolvedLlmRagCategoryScopeArg(call.Args);
                call.Args = await TryApplyInitialLlmSourceBackedCategoryScopeArgAsync(
                        plan,
                        call.Args,
                        userMessage,
                        ct,
                        onProgress)
                    .ConfigureAwait(false);
            }

            onPhase?.Invoke(PhaseLabelForTool(call.Name, plan.Language));
            onProgress?.Invoke(DescribeToolAction(call.Name, userMessage, plan.Language, call.Args));

            var sw = Stopwatch.StartNew();
            EmitRagTrace(
                "tool.start",
                ("name", call.Name),
                ("args", call.Args.GetRawText()));
            ClientLog.Info(
                "ToolAgent tool start: " +
                $"name={call.Name}|args={TruncateForPrompt(call.Args.GetRawText(), 420)}");
            try
            {
                if (!ToolManifest.IsKnownTool(call.Name))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=unknown_tool|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "unknown_tool"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }

                if (ToolManifest.IsAdminTool(call.Name) && !_api.HasAdminKey)
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "admin_required",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"admin_required\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=admin_required|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "admin_required"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }
                var handlers = GetOrCreateToolHandlers();
                if (!handlers.TryGetValue(call.Name, out var handler))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    ClientLog.Info(
                        "ToolAgent tool end: " +
                        $"name={call.Name}|ok=false|error=unknown_tool_handler|ms={sw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "tool.end",
                        ("name", call.Name),
                        ("ok", false),
                        ("error", "unknown_tool_handler"),
                        ("ms", sw.ElapsedMilliseconds));
                    continue;
                }

                JsonElement res = await handler(call.Args, ct).ConfigureAwait(false);

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Result = res,
                    DurationMs = sw.ElapsedMilliseconds
                });
                ClientLog.Info(
                    "ToolAgent tool end: " +
                    $"name={call.Name}|ok=true|ms={sw.ElapsedMilliseconds}|resultKind={res.ValueKind}|preview={TruncateForPrompt(res.GetRawText(), 520)}");
                EmitRagTrace(
                    "tool.end",
                    ("name", call.Name),
                    ("ok", true),
                    ("result_kind", res.ValueKind.ToString()),
                    ("result_chars", res.GetRawText().Length),
                    ("ms", sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                var structuredError = ClassifyToolExecutionError(ex, ToolManifest.IsAdminTool(call.Name));
                var effectiveError = string.IsNullOrWhiteSpace(structuredError) ? ex.Message : structuredError;
                var resultError = string.IsNullOrWhiteSpace(structuredError) ? "tool_failed" : structuredError;
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Error = effectiveError,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = JsonDocument.Parse($"{{\"error\":\"{resultError}\"}}").RootElement
                });
                ClientLog.Info(
                    "ToolAgent tool end: " +
                    $"name={call.Name}|ok=false|error={TruncateForPrompt(effectiveError, 260)}|ms={sw.ElapsedMilliseconds}");
                EmitRagTrace(
                    "tool.end",
                    ("name", call.Name),
                    ("ok", false),
                    ("error", effectiveError),
                    ("ms", sw.ElapsedMilliseconds));
            }
        }

        return results;
    }

    private async Task<(string answer, List<ToolMemory.SourceRef>? sources)> AnswerAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        ClientLog.Info(
            "ToolAgent answer stage start: " +
            $"intent={plan.Intent}|mode={plan.Mode}|lang={plan.Language}|toolItems={toolResults.Items.Count}|" +
            $"inputTools={string.Join(",", toolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase))}|chars={userMessage.Length}");
        EmitRagTrace(
            "answer.stage.start",
            ("intent", plan.Intent),
            ("mode", plan.Mode),
            ("language", plan.Language),
            ("tool_items", toolResults.Items.Count),
            ("input_tools", toolResults.Items.Select(static x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            ("chars", userMessage.Length));
        var writerTrace = plan.ReasoningTracePublic?.Skip(1).FirstOrDefault();
        onProgress?.Invoke(!string.IsNullOrWhiteSpace(writerTrace)
            ? writerTrace
            : DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));

        var useGeneralChatPrompt = plan.ToolCalls.Count == 0 && (string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Intent, "meta.help", StringComparison.OrdinalIgnoreCase));
        _lastUsedGeneralChatPrompt = useGeneralChatPrompt;
        var system = useGeneralChatPrompt
            ? PromptCatalog.BuildGeneralChatSystemPrompt(plan.Language)
            : PromptCatalog.BuildWriterSystemPrompt(
                plan.Language,
                plan.Mode,
                LocalizedStrings.NormalizeStyle(_mem.LastStyle),
                allowGeneralChat: plan.ToolCalls.Count == 0 || string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase));

        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        var writerPromptBudget = ResolveWriterPromptBudget();
        var writerToolResults = BuildWriterToolResultsForRuntime(plan, toolResults, writerUserMessage, writerPromptBudget);
        _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastUsedInventoryRendered = _lastUsedInventoryRendered || _lastWriterToolNames.Any(x => string.Equals(x, "inventory.rendered", StringComparison.OrdinalIgnoreCase));
        ClientLog.Info(
            "ToolAgent answer writer context: " +
            $"general={useGeneralChatPrompt}|writerTools={writerToolResults.Items.Count}|writerToolNames={string.Join(",", _lastWriterToolNames)}|" +
            $"budgetToolsChars={writerPromptBudget.ToolResultsChars}|budgetBriefChars={writerPromptBudget.WritingBriefChars}|budgetInventoryChars={writerPromptBudget.EvidenceInventoryChars}");
        EmitRagTrace(
            "writer.context",
            ("general", useGeneralChatPrompt),
            ("writer_tools", writerToolResults.Items.Count),
            ("writer_tool_names", _lastWriterToolNames.ToArray()),
            ("budget_tool_chars", writerPromptBudget.ToolResultsChars),
            ("budget_brief_chars", writerPromptBudget.WritingBriefChars),
            ("budget_inventory_chars", writerPromptBudget.EvidenceInventoryChars));
        var inventoryRenderedText = TryRenderInventoryFallbackText(writerToolResults, plan.Language);
        var inventoryRenderedDataJson = TryExtractInventoryRenderedDataJson(writerToolResults);
        var backendClarification = TryBuildBackendGuidanceClarificationAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(backendClarification))
        {
            RememberPendingClarification("rag_guidance", userMessage, "backend_ask_clarification", plan.Language);
            _lastAnswerSource = $"backend_guidance_ask_clarification:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "backend_guidance_ask_clarification"),
                ("answer_source", _lastAnswerSource));
            return (backendClarification, null);
        }

        var versionTraceabilityAnswer = TryBuildDocumentVersionTraceabilityAnswer(toolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(versionTraceabilityAnswer))
        {
            var traceabilitySources = DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, userMessage);
            _lastAnswerSource = $"writer_bypass_document_version_traceability:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "document_version_traceability"),
                ("answer_source", _lastAnswerSource),
                ("sources", traceabilitySources.Count));
            return (versionTraceabilityAnswer, traceabilitySources.Count > 0 ? traceabilitySources : null);
        }

        var sourcePolicyGuard = TryBuildSourcePolicyGuardAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(sourcePolicyGuard))
        {
            var guardSources = LooksLikeDocumentInstructionPolicyRequest(userMessage)
                ? new List<ToolMemory.SourceRef>()
                : DeriveSourcesFromRagHits(writerToolResults).Take(5).ToList();
            _lastAnswerSource = $"writer_bypass_source_policy:{plan.Intent}";
            EmitRagTrace(
                "writer.bypass",
                ("reason", "source_policy_guard"),
                ("answer_source", _lastAnswerSource),
                ("sources", guardSources.Count));
            return (sourcePolicyGuard, guardSources.Count > 0 ? guardSources : null);
        }

        if (ShouldBypassWriterForDeterministicInventory(plan, writerToolResults, inventoryRenderedText))
        {
            var deterministicAnswer = (inventoryRenderedText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                _lastAnswerSource = $"writer_bypass_deterministic_inventory:{plan.Intent}";
                EmitRagTrace(
                    "writer.bypass",
                    ("reason", "deterministic_inventory"),
                    ("answer_source", _lastAnswerSource));
                return (deterministicAnswer, null);
            }
        }

        var writerEvidenceQuery = BuildRagEvidenceSelectionQuery(writerUserMessage);
        var requestedItemTitle = LooksLikeShortTechnicalEvidenceTopic(writerEvidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(writerUserMessage);
        if (!string.IsNullOrWhiteSpace(requestedItemTitle))
        {
            var ragHits = EnumerateRagHitSummaries(writerToolResults).ToList();
            if (ragHits.Count > 0 && !RagHitsContainRequestedTitle(ragHits, requestedItemTitle!))
            {
                _lastAnswerSource = $"writer_bypass_missing_exact_item:{plan.Intent}";
                var missingExactAnswer = BuildMissingExactItemAnswer(plan.Language, requestedItemTitle!, ragHits);
                if (LooksLikeSourceBypassOrUnsupportedInventionRequest(userMessage))
                    missingExactAnswer = ApplySourcePolicyGuardPrefix(missingExactAnswer, plan.Language);
                var missingExactSources = DeriveSourcesFromMissingExactItemCloseLeads(requestedItemTitle!, ragHits);
                if (LooksLikeMissingExactItemWithoutSourceLeads(missingExactAnswer))
                    missingExactSources.Clear();
                EmitRagTrace(
                    "writer.bypass",
                    ("reason", "missing_exact_item"),
                    ("requested_title", requestedItemTitle),
                    ("answer_source", _lastAnswerSource),
                    ("sources", missingExactSources.Count));
                return (missingExactAnswer, missingExactSources);
            }
        }

        var shouldUseSourceBackedOptionAnswer = ShouldUseSourceBackedOptionAnswer(requestedItemTitle, writerUserMessage)
            && !ShouldAvoidDeterministicSourceBackedOptionFallback(writerUserMessage);
        var shouldUseSourceBackedCountdownAnswer = LooksLikeSourceBackedCountdownPlanningRequest(writerUserMessage);
        var shouldUseSourceBackedExtractiveAnswer = ShouldUseSourceBackedExtractiveAnswer(writerUserMessage, writerToolResults);
        var shouldUseSourceBackedActionAnswer = LooksLikeSourceBackedActionRequest(writerUserMessage)
            && ShouldPreferSourceBackedAnswerOverBackendClarification(writerToolResults, writerUserMessage);
        var shouldUseSourceBackedPairingAnswer = LooksLikeSourceBackedPairingRecommendationRequest(writerUserMessage);
        var shouldRouteSourceBackedAnswerThroughWriter =
            ShouldRouteSourceBackedAnswerThroughWriter(writerToolResults, writerUserMessage, plan.Language);
        var shouldRequireWriterForBroadDocumentaryFinal =
            ShouldRequireWriterForBroadDocumentaryFinal(writerToolResults, writerUserMessage, plan.Language);
        var shouldAvoidRawSourceBackedFallback =
            ShouldAvoidRawSourceBackedFallback(writerUserMessage)
            || shouldRouteSourceBackedAnswerThroughWriter
            || shouldRequireWriterForBroadDocumentaryFinal;
        var shouldUseBroadSourceBackedSynthesis =
            ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, writerUserMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, writerUserMessage)
            || shouldRouteSourceBackedAnswerThroughWriter
            || shouldRequireWriterForBroadDocumentaryFinal;
        var planningGuardSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.guard.coverage.start",
            ("raw_tool_items", toolResults.Items.Count),
            ("writer_tool_items", writerToolResults.Items.Count),
            ("query", writerUserMessage));
        var planningGuardSelection = ResolveStructuredPlanningWriterGuardToolResults(
            toolResults,
            writerToolResults,
            writerUserMessage,
            plan.Language);
        EmitRagTrace(
            "writer.guard.coverage.selection",
            ("basis", planningGuardSelection.Basis),
            ("has_raw_coverage", planningGuardSelection.RawCoverage is not null),
            ("has_writer_coverage", planningGuardSelection.WriterCoverage is not null),
            ("ms", planningGuardSw.ElapsedMilliseconds));
        var structuredPlanningGuardToolResults = planningGuardSelection.ToolResults;
        if (planningGuardSelection.RawCoverage is { } rawPlanningCoverage
            && planningGuardSelection.WriterCoverage is { } writerPlanningCoverage)
        {
            EmitRagTrace(
                "writer.guard.coverage",
                ("raw_adequate", rawPlanningCoverage.IsAdequate),
                ("raw_candidates", rawPlanningCoverage.CandidateCount),
                ("raw_distinct_pages", rawPlanningCoverage.DistinctSourcePages),
                ("writer_adequate", writerPlanningCoverage.IsAdequate),
                ("writer_candidates", writerPlanningCoverage.CandidateCount),
                ("writer_distinct_pages", writerPlanningCoverage.DistinctSourcePages),
                ("guard_basis", planningGuardSelection.Basis));
        }

        var insufficientGuardSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.guard.insufficient_check.start",
            ("basis", planningGuardSelection.Basis),
            ("tool_items", structuredPlanningGuardToolResults.Items.Count));
        var insufficientStructuredPlanningAnswer = TryBuildInsufficientStructuredPlanningBeforeWriterAnswer(
            structuredPlanningGuardToolResults,
            writerUserMessage,
            plan.Language);
        EmitRagTrace(
            "writer.guard.insufficient_check.end",
            ("blocked", !string.IsNullOrWhiteSpace(insufficientStructuredPlanningAnswer)),
            ("answer_chars", insufficientStructuredPlanningAnswer?.Length ?? 0),
            ("ms", insufficientGuardSw.ElapsedMilliseconds));
        if (!string.IsNullOrWhiteSpace(insufficientStructuredPlanningAnswer))
        {
            ClientLog.Info(
                "ToolAgent structured planning writer guard blocked unsupported synthesis before writer: " +
                $"intent={plan.Intent} " +
                $"broad={shouldUseBroadSourceBackedSynthesis} " +
                $"route={shouldRouteSourceBackedAnswerThroughWriter} " +
                $"requireFinal={shouldRequireWriterForBroadDocumentaryFinal}");
            LogSourceBackedPlanningTrace(
                "writer-guard-blocked-before-writer",
                structuredPlanningGuardToolResults,
                writerUserMessage,
                plan.Language);
            _lastAnswerSource = $"writer_bypass_insufficient_structured_planning:{plan.Intent}";
            EmitRagTrace(
                "writer.guard",
                ("decision", "blocked_before_writer"),
                ("reason", "insufficient_structured_planning"),
                ("answer_source", _lastAnswerSource));
            return (insufficientStructuredPlanningAnswer, null);
        }

        if (!shouldUseBroadSourceBackedSynthesis
            && !shouldAvoidRawSourceBackedFallback
            && (shouldUseSourceBackedExtractiveAnswer || shouldUseSourceBackedActionAnswer || shouldUseSourceBackedCountdownAnswer || shouldUseSourceBackedPairingAnswer || shouldUseSourceBackedOptionAnswer))
        {
            string deterministicAnswer;
            List<ToolMemory.SourceRef> deterministicSources;
            if (shouldUseSourceBackedCountdownAnswer)
            {
                deterministicAnswer = BuildSourceBackedCountdownPlanningAnswer(writerToolResults, userMessage, plan.Language);
                if (!string.IsNullOrWhiteSpace(deterministicAnswer))
                {
                    deterministicSources = DeriveSourcesFromCountdownPlanningHits(writerToolResults, userMessage);
                }
                else
                {
                    deterministicAnswer = BuildSourceBackedExtractiveAnswer(writerToolResults, userMessage, plan.Language);
                    deterministicSources = DeriveSourcesFromExtractiveHits(writerToolResults, userMessage);
                }
            }
            else if (shouldUseSourceBackedOptionAnswer && !shouldUseSourceBackedPairingAnswer)
            {
                deterministicAnswer = TryBuildMissingBroadCompositionAnchorAnswer(writerToolResults, userMessage, plan.Language);
                if (string.IsNullOrWhiteSpace(deterministicAnswer) && ShouldUseFallbackForBroadMethodOptionRequest(userMessage))
                    deterministicAnswer = BuildRagEvidenceFallbackAnswer(writerToolResults, userMessage, plan.Language);
                if (string.IsNullOrWhiteSpace(deterministicAnswer))
                    deterministicAnswer = BuildSourceBackedOptionAnswer(writerToolResults, plan.Language, minItems: 1, query: userMessage);

                if (!string.IsNullOrWhiteSpace(deterministicAnswer))
                {
                    deterministicSources = LooksLikeOverPromotedSourceBackedOptionAnswer(deterministicAnswer)
                        ? DeriveSourcesFromOptionHits(writerToolResults, userMessage)
                        : DeriveSourcesFromExtractiveHits(writerToolResults, userMessage);
                }
                else
                {
                    deterministicAnswer = BuildSourceBackedExtractiveAnswer(writerToolResults, userMessage, plan.Language);
                    deterministicSources = DeriveSourcesFromExtractiveHits(writerToolResults, userMessage);
                }
            }
            else if (shouldUseSourceBackedPairingAnswer)
            {
                deterministicAnswer = BuildSourceBackedOptionAnswer(writerToolResults, plan.Language, minItems: 1, query: userMessage);
                if (!string.IsNullOrWhiteSpace(deterministicAnswer))
                {
                    deterministicSources = LooksLikeMissingPairingAnchorAnswer(deterministicAnswer)
                        ? DeriveSourcesFromRagHits(writerToolResults).Take(5).ToList()
                        : DeriveSourcesFromOptionHits(writerToolResults, userMessage);
                }
                else
                {
                    deterministicAnswer = BuildSourceBackedExtractiveAnswer(writerToolResults, userMessage, plan.Language);
                    deterministicSources = DeriveSourcesFromExtractiveHits(writerToolResults, userMessage);
                }
            }
            else
            {
                deterministicAnswer = BuildSourceBackedExtractiveAnswer(writerToolResults, userMessage, plan.Language);
                deterministicSources = DeriveSourcesFromExtractiveHits(writerToolResults, userMessage);
            }
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                if (LooksLikeMissingExactItemWithoutSourceLeads(deterministicAnswer))
                    deterministicSources.Clear();
                _lastAnswerSource = $"writer_bypass_source_backed_extractive:{plan.Intent}";
                EmitRagTrace(
                    "writer.bypass",
                    ("reason", "source_backed_deterministic"),
                    ("answer_source", _lastAnswerSource),
                    ("sources", deterministicSources.Count));
                return (deterministicAnswer, deterministicSources);
            }
        }

        var useCleanSourceBrief = ShouldUseCleanSourceBackedWriterPrompt(plan, writerToolResults, writerUserMessage);
        ClientLog.Info(
            "ToolAgent answer writer selected: " +
            $"intent={plan.Intent}|broad={shouldUseBroadSourceBackedSynthesis}|avoidRaw={shouldAvoidRawSourceBackedFallback}|" +
            $"route={shouldRouteSourceBackedAnswerThroughWriter}|requireBroadFinal={shouldRequireWriterForBroadDocumentaryFinal}|cleanBrief={useCleanSourceBrief}");
        EmitRagTrace(
            "writer.selected",
            ("intent", plan.Intent),
            ("broad", shouldUseBroadSourceBackedSynthesis),
            ("avoid_raw", shouldAvoidRawSourceBackedFallback),
            ("route", shouldRouteSourceBackedAnswerThroughWriter),
            ("require_broad_final", shouldRequireWriterForBroadDocumentaryFinal),
            ("clean_brief", useCleanSourceBrief));
        var candidateAdjudicationJson = await TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
            writerToolResults,
            writerUserMessage,
            plan.Language,
            ct).ConfigureAwait(false);
        var toolResultsPromptBlock = BuildWriterToolResultsPromptBlock(
            writerToolResults,
            writerUserMessage,
            plan.Language,
            useCleanSourceBrief);

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 10)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language)}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{TruncateForPrompt(BuildSourceBackedCoverageHintsForWriter(writerToolResults, writerUserMessage, plan.Language), writerPromptBudget.CoverageNoteChars)}

PRIVATE_SOURCE_WRITING_BRIEF:
{TruncateForPrompt(BuildSourceBackedWritingBriefForWriter(writerToolResults, writerUserMessage, plan.Language), writerPromptBudget.WritingBriefChars)}

PRIVATE_SOURCE_RESEARCH_MAP:
{TruncateForPrompt(BuildSourceBackedResearchMapForWriter(toolResults, _mem.LastSourcesUsed, writerUserMessage, plan.Language), writerPromptBudget.ResearchMapChars)}

PRIVATE_SOURCE_CANDIDATE_ADJUDICATION (json):
{candidateAdjudicationJson ?? "null"}

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{TruncateForPrompt(BuildSourceBackedCandidateLeadsForWriter(writerToolResults, writerUserMessage, plan.Language), writerPromptBudget.EvidenceInventoryChars)}

{toolResultsPromptBlock}

AUTHORITATIVE_INVENTORY_DATA (json):
{inventoryRenderedDataJson ?? "null"}
";

        var writerMessages = new[]
        {
            ("system", system),
            ("user", user)
        };

        var usedWriterContextOverflowFallback = false;
        string finalAnswer;
        try
        {
            ClientLog.Info(
                "ToolAgent answer writer llm call start: " +
                $"intent={plan.Intent}|promptChars={(system.Length + user.Length)}|cleanBrief={useCleanSourceBrief}");
            EmitRagTrace(
                "writer.llm.start",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("clean_brief", useCleanSourceBrief));
            finalAnswer = await StreamOrCompleteWithRetryAsync(writerMessages, onDelta, ct).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent answer writer llm call end: " +
                $"intent={plan.Intent}|answerChars={finalAnswer?.Length ?? 0}");
            EmitRagTrace(
                "writer.llm.end",
                ("intent", plan.Intent),
                ("answer_chars", finalAnswer?.Length ?? 0));
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex)
                                   && writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            EmitRagTrace(
                "writer.llm.overflow",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("error", ex.Message));
            finalAnswer = string.Empty;
            var overflowRetryToolResults = ApplyWriterToolResultsBudget(
                BuildOverflowRetryWriterToolResults(writerToolResults, writerUserMessage),
                writerUserMessage,
                Math.Max(WriterPromptMinimumToolResultsChars, writerPromptBudget.ToolResultsChars / 2));
            if (overflowRetryToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            {
                var retryUser = BuildOverflowRetryWriterUserPrompt(
                    chatHistory,
                    writerUserMessage,
                    plan,
                    overflowRetryToolResults,
                    BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language));
                var retryMessages = new[]
                {
                    ("system", system),
                    ("user", retryUser)
                };

                try
                {
                    finalAnswer = await StreamOrCompleteWithRetryAsync(retryMessages, onDelta, ct).ConfigureAwait(false);
                    _lastAnswerSource = $"writer_context_overflow_compact_retry:{plan.Intent}";
                }
                catch (Exception retryEx) when (IsLlmContextOverflowException(retryEx))
                {
                    finalAnswer = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(finalAnswer))
            {
                finalAnswer = shouldAvoidRawSourceBackedFallback
                ? BuildSourceBackedSafeFallbackAnswer(
                    writerToolResults,
                    userMessage,
                    plan.Language,
                    shouldAvoidRaw: true)
                : BuildSourceBackedPlanningOrExtractiveAnswer(writerToolResults, userMessage, plan.Language, minPlanningItems: 1);
                if (string.IsNullOrWhiteSpace(finalAnswer))
                    finalAnswer = BuildSourceBackedSafeFallbackAnswer(
                        writerToolResults,
                        userMessage,
                        plan.Language,
                        shouldAvoidRawSourceBackedFallback);
                if (string.IsNullOrWhiteSpace(finalAnswer))
                    throw;

                _lastAnswerSource = $"writer_context_overflow_deterministic_fallback:{plan.Intent}";
                usedWriterContextOverflowFallback = true;
                if (onDelta is not null)
                    onDelta(finalAnswer);
            }
        }

        var swWriterPost = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.post.start",
            ("intent", plan.Intent),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("overflow_fallback", usedWriterContextOverflowFallback));

        finalAnswer = (finalAnswer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer) && !string.IsNullOrWhiteSpace(inventoryRenderedText))
            finalAnswer = inventoryRenderedText.Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = DeterministicAgentText.AnswerNotEnoughUsableInfo(plan.Language);

        var writerPostLanguageSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.post.language_check.start",
            ("language", plan.Language),
            ("answer_chars", finalAnswer?.Length ?? 0));
        finalAnswer = await EnsureAnswerMatchesRequestedLanguageAsync(finalAnswer ?? string.Empty, plan.Language, ct).ConfigureAwait(false);
        writerPostLanguageSw.Stop();
        EmitRagTrace(
            "writer.post.language_check.end",
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("elapsed_ms", writerPostLanguageSw.ElapsedMilliseconds));

        List<ToolMemory.SourceRef>? sources = null;
        var usedRagSearch = toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search");
        var usedSourcesResolve = toolResults.Items.Any(x => x.ToolName == "sources.resolve");
        var usedSummarySearch = toolResults.Items.Any(x => x.ToolName == "summary.search");
        var sourceToolResults = usedWriterContextOverflowFallback ? writerToolResults : toolResults;
        var structuredSourceBackedPlanningFinalResolved = false;
        var finalPlanningCoverageQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
        if (string.IsNullOrWhiteSpace(finalPlanningCoverageQuery))
            finalPlanningCoverageQuery = userMessage;

        if (usedRagSearch && LooksLikeDegenerateLlmOutput(finalAnswer) && !LooksLikeWeeklyPlanningRequest(userMessage))
        {
            var guardedAnswer = shouldAvoidRawSourceBackedFallback
                ? string.Empty
                : BuildSourceBackedPlanningOrExtractiveAnswer(sourceToolResults, userMessage, plan.Language, minPlanningItems: 1);
            finalAnswer = string.IsNullOrWhiteSpace(guardedAnswer)
                ? BuildSourceBackedSafeFallbackAnswer(
                    sourceToolResults,
                    userMessage,
                    plan.Language,
                    shouldAvoidRawSourceBackedFallback)
                : guardedAnswer;
            _lastAnswerSource = $"writer_guard_degenerate_output:{plan.Intent}";
        }

        if (usedRagSearch)
        {
            var writerPostSourcesSw = Stopwatch.StartNew();
            EmitRagTrace(
                "writer.post.sources.start",
                ("intent", plan.Intent),
                ("tool_items", sourceToolResults.Items.Count));
            if (LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
                sources = DeriveSourcesFromCountdownPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
                sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeSourceBackedPairingRecommendationRequest(userMessage))
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (shouldUseSourceBackedOptionAnswer)
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (ShouldUseSourceBackedExtractiveAnswer(userMessage, toolResults)
                || LooksLikeSourceBackedActionRequest(userMessage)
                || LooksLikeComparativeDocumentaryRequest(userMessage))
                sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
            else
                sources = DeriveSourcesFromRagHits(sourceToolResults);

            if (sources.Count == 0)
                sources = DeriveSourcesFromRagHits(sourceToolResults);
            writerPostSourcesSw.Stop();
            EmitRagTrace(
                "writer.post.sources.end",
                ("sources", sources.Count),
                ("elapsed_ms", writerPostSourcesSw.ElapsedMilliseconds));

            if (ShouldGateStructuredSourceBackedPlanningCoverage(finalPlanningCoverageQuery))
            {
                var finalStructuredGateSw = Stopwatch.StartNew();
                var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(finalPlanningCoverageQuery);
                EmitRagTrace(
                    "writer.final_structured_gate.start",
                    ("target_items", targetItemCount),
                    ("answer_chars", finalAnswer?.Length ?? 0),
                    ("tool_items", sourceToolResults.Items.Count),
                    ("position", "pre_general_post_guards"));
                var writerFinalSupportSw = Stopwatch.StartNew();
                EmitRagTrace(
                    "writer.final_structured_gate.writer_support.start",
                    ("answer_chars", finalAnswer?.Length ?? 0),
                    ("position", "pre_general_post_guards"));
                var writerPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    finalAnswer,
                    sourceToolResults,
                    finalPlanningCoverageQuery,
                    plan.Language);
                writerFinalSupportSw.Stop();
                EmitRagTrace(
                    "writer.final_structured_gate.writer_support.end",
                    ("items", writerPlanningSupport.ItemCount),
                    ("supported", writerPlanningSupport.SupportedItemCount),
                    ("unsupported", writerPlanningSupport.UnsupportedItemCount),
                    ("candidates", writerPlanningSupport.CandidateCount),
                    ("sources", writerPlanningSupport.Sources.Count),
                    ("accepted", writerPlanningSupport.Sources.Count > 0 && !ShouldRejectUnsupportedPlanningAnswerForFinal(writerPlanningSupport, finalPlanningCoverageQuery)),
                    ("ms", writerFinalSupportSw.ElapsedMilliseconds));
                if (writerPlanningSupport.Sources.Count > 0
                    && !ShouldRejectUnsupportedPlanningAnswerForFinal(writerPlanningSupport, finalPlanningCoverageQuery))
                {
                    sources = writerPlanningSupport.Sources.ToList();
                    _lastAnswerSource = $"structured_planning_supported_writer:{plan.Intent}";
                }
                else
                {
                    var deterministicFinalGateSw = Stopwatch.StartNew();
                    EmitRagTrace(
                        "writer.final_structured_gate.deterministic.start",
                        ("target_items", targetItemCount),
                        ("position", "pre_general_post_guards"));
                    var deterministicFinalGateSupported = TryBuildSupportedStructuredPlanningAnswer(
                            sourceToolResults,
                            plan.Language,
                            finalPlanningCoverageQuery,
                            out var deterministicPlanningAnswer,
                            out var deterministicPlanningSources,
                            out var deterministicPlanningSupport);
                    deterministicFinalGateSw.Stop();
                    EmitRagTrace(
                        "writer.final_structured_gate.deterministic.end",
                        ("supported", deterministicFinalGateSupported),
                        ("answer_chars", deterministicPlanningAnswer?.Length ?? 0),
                        ("sources", deterministicPlanningSources?.Count ?? 0),
                        ("items", deterministicPlanningSupport.ItemCount),
                        ("supported_items", deterministicPlanningSupport.SupportedItemCount),
                        ("unsupported_items", deterministicPlanningSupport.UnsupportedItemCount),
                        ("candidates", deterministicPlanningSupport.CandidateCount),
                        ("ms", deterministicFinalGateSw.ElapsedMilliseconds));
                    if (deterministicFinalGateSupported)
                    {
                        finalAnswer = deterministicPlanningAnswer ?? string.Empty;
                        sources = deterministicPlanningSources;
                        _lastAnswerSource = $"structured_planning_deterministic_supported_candidates:{plan.Intent}";
                    }
                    else
                    {
                        ClientLog.Info(
                            "ToolAgent structured planning final gate rejected unsupported answer: " +
                            $"target={targetItemCount} writerItems={writerPlanningSupport.ItemCount} " +
                            $"writerSupported={writerPlanningSupport.SupportedItemCount} " +
                            $"unsupported={writerPlanningSupport.UnsupportedItemCount} " +
                            $"deterministicItems={deterministicPlanningSupport.ItemCount} " +
                            $"deterministicSupported={deterministicPlanningSupport.SupportedItemCount} " +
                            $"candidates={Math.Max(writerPlanningSupport.CandidateCount, deterministicPlanningSupport.CandidateCount)} " +
                            $"source={_lastAnswerSource}");
                        LogSourceBackedPlanningTrace(
                            "final-gate-rejected-unsupported",
                            sourceToolResults,
                            finalPlanningCoverageQuery,
                            plan.Language);
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningCoverageQuery,
                            finalPlanningCoverageQuery,
                            Math.Max(writerPlanningSupport.CandidateCount, deterministicPlanningSupport.CandidateCount),
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"structured_planning_insufficient_supported_candidates:{plan.Intent}";
                    }
                }

                finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer ?? string.Empty);
                structuredSourceBackedPlanningFinalResolved = true;
                EmitRagTrace(
                    "writer.final_structured_gate.end",
                    ("answer_source", _lastAnswerSource),
                    ("answer_chars", finalAnswer?.Length ?? 0),
                    ("sources", sources?.Count ?? 0),
                    ("position", "pre_general_post_guards"),
                    ("ms", finalStructuredGateSw.ElapsedMilliseconds));
            }

            if (!structuredSourceBackedPlanningFinalResolved && LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
            {
                var countdownAnswer = BuildSourceBackedCountdownPlanningAnswer(sourceToolResults, userMessage, plan.Language);
                if (!string.IsNullOrWhiteSpace(countdownAnswer))
                {
                    finalAnswer = countdownAnswer;
                    sources = DeriveSourcesFromCountdownPlanningHits(sourceToolResults, userMessage);
                    if (sources.Count == 0)
                        sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved && ShouldFallbackFromNoRagDataAnswer(finalAnswer))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    && (ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
                        || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage))
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false)
                    : BuildSourceBackedPlanningOrExtractiveAnswer(sourceToolResults, userMessage, plan.Language, minPlanningItems: 1);
                if (string.IsNullOrWhiteSpace(repairAnswer))
                {
                    repairAnswer = BuildSourceBackedSafeFallbackAnswer(
                        sourceToolResults,
                        userMessage,
                        plan.Language,
                        shouldAvoidRawSourceBackedFallback);
                }

                if (!string.IsNullOrWhiteSpace(repairAnswer))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                }
            }

            var missingRequiredAnswer = TryBuildMissingRequiredEvidenceAnswer(sourceToolResults, userMessage, plan.Language);
            if (!structuredSourceBackedPlanningFinalResolved
                && !ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(sourceToolResults, userMessage)
                && !string.IsNullOrWhiteSpace(missingRequiredAnswer))
            {
                finalAnswer = missingRequiredAnswer;
                sources = DeriveSourcesFromRagHits(sourceToolResults).Take(5).ToList();
                _lastAnswerSource = $"writer_guard_missing_required_evidence:{plan.Intent}";
            }

            var missingPairingAnchorAnswer = TryBuildMissingPairingAnchorAnswer(sourceToolResults, userMessage, plan.Language);
            if (!structuredSourceBackedPlanningFinalResolved
                && !ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(sourceToolResults, userMessage)
                && !string.IsNullOrWhiteSpace(missingPairingAnchorAnswer))
            {
                finalAnswer = missingPairingAnchorAnswer;
                sources = DeriveSourcesFromRagHits(sourceToolResults).Take(5).ToList();
                _lastAnswerSource = $"writer_guard_missing_pairing_anchor:{plan.Intent}";
            }

            var missingBroadAnchorAnswer = (ShouldUseWriterForBroadSourceBackedSynthesis(sourceToolResults, userMessage)
                    || ShouldPreferWriterForPolishedSourceBackedAnswer(sourceToolResults, userMessage))
                ? string.Empty
                : TryBuildMissingBroadCompositionAnchorAnswer(sourceToolResults, userMessage, plan.Language);
            if (!structuredSourceBackedPlanningFinalResolved && !string.IsNullOrWhiteSpace(missingBroadAnchorAnswer))
            {
                finalAnswer = missingBroadAnchorAnswer;
                sources = DeriveSourcesFromRagHits(sourceToolResults).Take(5).ToList();
                _lastAnswerSource = $"writer_guard_missing_broad_anchor:{plan.Intent}";
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && (ShouldPreferPartialEvidenceFallbackOverOptions(sourceToolResults, userMessage)
                    || LooksLikeUnsupportedBroadOptionComposition(userMessage, finalAnswer))
                && ShouldReplaceOverPromotedSourceBackedOptionAnswer(finalAnswer, sourceToolResults, userMessage))
            {
                var fallbackAnswer = BuildSourceBackedSafeFallbackAnswer(
                    sourceToolResults,
                    userMessage,
                    plan.Language,
                    shouldAvoidRawSourceBackedFallback);
                if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                {
                    finalAnswer = fallbackAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_overpromoted_options:{plan.Intent}";
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && LooksLikeUnderusedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage)
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    userMessage,
                    plan,
                    writerToolResults,
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage)
                    && !LooksLikeUnderusedSourceBackedPlanningAnswer(repairAnswer, sourceToolResults, userMessage))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_underused_planning_sources_repaired:{plan.Intent}";
                }
                else if (!shouldAvoidRawSourceBackedFallback
                    && !ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
                {
                    var planningDraft = BuildSourceBackedPlanningDraft(
                        sourceToolResults,
                        plan.Language,
                        minItems: ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
                            ? ResolveSourceBackedPlanningTargetItemCount(userMessage)
                            : 2,
                        query: userMessage);
                    var planningAnswer = planningDraft.Answer;
                    if (!string.IsNullOrWhiteSpace(planningAnswer))
                    {
                        finalAnswer = planningAnswer;
                        sources = planningDraft.Sources.ToList();
                        _lastAnswerSource = $"writer_guard_underused_planning_sources:{plan.Intent}";
                    }
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved && LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_poor_planning_repaired:{plan.Intent}";
                }
                else
                {
                    var fallbackAnswer = shouldAvoidRawSourceBackedFallback
                        ? BuildSourceBackedSafeFallbackAnswer(
                            sourceToolResults,
                            userMessage,
                            plan.Language,
                            shouldAvoidRaw: true)
                        : BuildNonPoorSourceBackedFallbackAnswer(sourceToolResults, userMessage, plan.Language);
                    if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                    {
                        finalAnswer = fallbackAnswer;
                        sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                        _lastAnswerSource = $"writer_guard_poor_planning_clean_fallback:{plan.Intent}";
                    }
                    else if (!shouldAvoidRawSourceBackedFallback
                        && !ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
                    {
                        var planningDraft = BuildSourceBackedPlanningDraft(
                            sourceToolResults,
                            plan.Language,
                            minItems: ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
                                ? ResolveSourceBackedPlanningTargetItemCount(userMessage)
                                : 2,
                            query: userMessage);
                        var planningAnswer = planningDraft.Answer;
                        var planningSources = planningDraft.Sources.ToList();
                        if (string.IsNullOrWhiteSpace(planningAnswer))
                        {
                            planningAnswer = BuildReadablePartialPlanningEvidenceAnswer(
                                SelectSourceBackedExtractiveHits(sourceToolResults, userMessage, maxHits: 8).ToList(),
                                userMessage,
                                plan.Language);
                        }

                        if (!string.IsNullOrWhiteSpace(planningAnswer))
                        {
                            finalAnswer = planningAnswer;
                            sources = planningSources.Count > 0
                                ? planningSources
                                : DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                            _lastAnswerSource = $"writer_guard_poor_planning_deterministic:{plan.Intent}";
                        }
                    }
                }
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && sources is { Count: > 0 }
                && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
            {
                sources.Clear();
            }

            if (!structuredSourceBackedPlanningFinalResolved
                && usedRagSearch
                && LooksLikeUnsupportedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage, plan.Language))
            {
                var repairAnswer = ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage)
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                        chatHistory,
                        userMessage,
                        plan,
                        writerToolResults,
                        ct).ConfigureAwait(false)
                    : string.Empty;
                repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage)
                    && !LooksLikeUnsupportedSourceBackedPlanningAnswer(repairAnswer, sourceToolResults, userMessage, plan.Language))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_unsupported_planning_items_repaired:{plan.Intent}";
                }
                else
                {
                    var planningDraft = BuildSourceBackedPlanningDraft(
                        sourceToolResults,
                        plan.Language,
                        minItems: ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
                            ? ResolveSourceBackedPlanningTargetItemCount(userMessage)
                            : 2,
                        query: userMessage);
                    var planningAnswer = planningDraft.Answer;
                    if (!string.IsNullOrWhiteSpace(planningAnswer))
                    {
                        finalAnswer = planningAnswer;
                        sources = planningDraft.Sources.ToList();
                        _lastAnswerSource = $"writer_guard_unsupported_planning_items_source_backed:{plan.Intent}";
                    }
                    else
                    {
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            userMessage,
                            userMessage,
                            sources?.Count ?? 0,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"writer_guard_unsupported_planning_items_insufficient:{plan.Intent}";
                    }
                }
            }
        }
        else if (usedSourcesResolve)
        {
            var resolved = TryBuildSourceFromResolveResult(toolResults);
            if (resolved is not null)
                sources = new List<ToolMemory.SourceRef> { resolved };
        }
        else if (usedSummarySearch)
        {
            sources = DeriveSourcesFromSummarySearch(toolResults);
        }

        if (!structuredSourceBackedPlanningFinalResolved
            && ShouldRunCriticPass(plan, toolResults, useGeneralChatPrompt, userMessage))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressCheckAlignmentWithSources(plan.Language));

            finalAnswer = await RunCriticPassAsync(chatHistory, userMessage, plan, writerToolResults, finalAnswer ?? string.Empty, ct).ConfigureAwait(false);
            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage)
            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
        {
            var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                chatHistory,
                userMessage,
                plan,
                writerToolResults,
                ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(repairAnswer)
                && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
            {
                finalAnswer = repairAnswer;
                sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                _lastAnswerSource = $"post_critic_guard_poor_planning_repaired:{plan.Intent}";
            }
            else
            {
                var fallbackAnswer = shouldAvoidRawSourceBackedFallback
                    ? BuildSourceBackedSafeFallbackAnswer(
                        sourceToolResults,
                        userMessage,
                        plan.Language,
                        shouldAvoidRaw: true)
                    : BuildNonPoorSourceBackedFallbackAnswer(sourceToolResults, userMessage, plan.Language);
                if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                {
                    finalAnswer = fallbackAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                    _lastAnswerSource = $"post_critic_guard_poor_planning_clean_fallback:{plan.Intent}";
                }
                else if (!shouldAvoidRawSourceBackedFallback
                    && !ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
                {
                    var planningDraft = BuildSourceBackedPlanningDraft(
                        sourceToolResults,
                        plan.Language,
                        minItems: ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
                            ? ResolveSourceBackedPlanningTargetItemCount(userMessage)
                            : 2,
                        query: userMessage);
                    var planningAnswer = planningDraft.Answer;
                    var planningSources = planningDraft.Sources.ToList();
                    if (string.IsNullOrWhiteSpace(planningAnswer))
                    {
                        planningAnswer = BuildReadablePartialPlanningEvidenceAnswer(
                            SelectSourceBackedExtractiveHits(sourceToolResults, userMessage, maxHits: 8).ToList(),
                            userMessage,
                            plan.Language);
                    }

                    if (!string.IsNullOrWhiteSpace(planningAnswer))
                    {
                        finalAnswer = planningAnswer;
                        sources = planningSources.Count > 0
                            ? planningSources
                            : DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                        _lastAnswerSource = $"post_critic_guard_poor_planning_deterministic:{plan.Intent}";
                    }
                }
            }
        }

        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
        {
            sources.Clear();
        }
        else if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && LooksLikeUnsupportedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage, plan.Language))
        {
            var fallbackDraft = BuildSourceBackedPlanningDraft(
                sourceToolResults,
                plan.Language,
                minItems: ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
                    ? ResolveSourceBackedPlanningTargetItemCount(userMessage)
                    : 2,
                query: userMessage);
            var fallbackAnswer = fallbackDraft.Answer;
            if (!string.IsNullOrWhiteSpace(fallbackAnswer))
            {
                finalAnswer = fallbackAnswer;
                sources = fallbackDraft.Sources.ToList();
                _lastAnswerSource = $"post_critic_guard_unsupported_planning_items_source_backed:{plan.Intent}";
            }
            else
            {
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    userMessage,
                    sources?.Count ?? 0,
                    searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                sources = new List<ToolMemory.SourceRef>();
                _lastAnswerSource = $"post_critic_guard_unsupported_planning_items_insufficient:{plan.Intent}";
            }
        }
        else if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && sources is { Count: > 0 }
            && ShouldFallbackFromNoRagDataAnswer(finalAnswer))
        {
            finalAnswer = BuildSourceBackedSafeFallbackAnswer(
                sourceToolResults,
                userMessage,
                plan.Language,
                shouldAvoidRawSourceBackedFallback);
            sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
        }

        finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer ?? string.Empty);
        if (!structuredSourceBackedPlanningFinalResolved
            && usedRagSearch
            && LooksLikeWriterControlLeak(finalAnswer)
            && ShouldAllowSourceBackedWriterRepairForCurrentTurn(userMessage))
        {
            var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                chatHistory,
                userMessage,
                plan,
                writerToolResults,
                ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(repairAnswer)
                && !LooksLikeWriterControlLeak(repairAnswer)
                && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
            {
                finalAnswer = repairAnswer;
                sources = LooksLikeAnyDocumentaryPlanningRequest(userMessage)
                    ? DeriveSourcesFromPlanningHits(sourceToolResults, userMessage)
                    : DeriveSourcesFromRagHits(sourceToolResults);
                _lastAnswerSource = $"post_writer_guard_control_leak_repaired:{plan.Intent}";
            }
            else
            {
                var fallbackAnswer = BuildSourceBackedSafeFallbackAnswer(
                    sourceToolResults,
                    userMessage,
                    plan.Language,
                    shouldAvoidRawSourceBackedFallback);
                if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                {
                    finalAnswer = fallbackAnswer;
                    sources = DeriveSourcesForSourceBackedFallback(sourceToolResults, userMessage);
                    _lastAnswerSource = $"post_writer_guard_control_leak_deterministic:{plan.Intent}";
                }
            }

            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        if (!structuredSourceBackedPlanningFinalResolved && usedRagSearch && sources is { Count: > 0 })
        {
            var finalPlanningSupportQuery = !string.IsNullOrWhiteSpace(finalPlanningCoverageQuery)
                ? finalPlanningCoverageQuery
                : userMessage;
            if (LooksLikeAnyDocumentaryPlanningRequest(finalPlanningSupportQuery))
            {
                var planningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    finalAnswer,
                    sourceToolResults,
                    finalPlanningSupportQuery,
                    plan.Language);
                if (ShouldRejectUnsupportedPlanningAnswerForFinal(planningSupport, finalPlanningSupportQuery))
                {
                    ClientLog.Info(
                        "ToolAgent planning answer rejected after final support check: " +
                        $"items={planningSupport.ItemCount} supported={planningSupport.SupportedItemCount} " +
                        $"unsupported={planningSupport.UnsupportedItemCount} candidates={planningSupport.CandidateCount} " +
                        $"source={_lastAnswerSource}");
                    LogSourceBackedPlanningTrace(
                        "final-support-check-rejected",
                        sourceToolResults,
                        finalPlanningSupportQuery,
                        plan.Language);
                    var deterministicPlanningDraft = BuildSourceBackedPlanningDraft(
                        sourceToolResults,
                        plan.Language,
                        minItems: ResolveSourceBackedPlanningTargetItemCount(finalPlanningSupportQuery),
                        query: finalPlanningSupportQuery);
                    var deterministicPlanningAnswer = deterministicPlanningDraft.Answer;
                    var deterministicPlanningSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                        deterministicPlanningAnswer,
                        sourceToolResults,
                        finalPlanningSupportQuery,
                        plan.Language);
                    if (!string.IsNullOrWhiteSpace(deterministicPlanningAnswer)
                        && !ShouldRejectUnsupportedPlanningAnswerForFinal(deterministicPlanningSupport, finalPlanningSupportQuery)
                        && deterministicPlanningSupport.Sources.Count > 0)
                    {
                        finalAnswer = deterministicPlanningAnswer;
                        sources = deterministicPlanningSupport.Sources.ToList();
                        _lastAnswerSource = $"post_writer_guard_planning_rebuilt_from_supported_candidates:{plan.Intent}";
                    }
                    else
                    {
                        finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            finalPlanningSupportQuery,
                            finalPlanningSupportQuery,
                            planningSupport.CandidateCount,
                            searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                        sources.Clear();
                        _lastAnswerSource = $"post_writer_guard_planning_item_source_mismatch:{plan.Intent}";
                    }
                }
                else if (planningSupport.Sources.Count > 0)
                {
                    sources = planningSupport.Sources.ToList();
                }
            }

            var finalSourceAlignmentQuery = !string.IsNullOrWhiteSpace(finalPlanningCoverageQuery)
                ? finalPlanningCoverageQuery
                : userMessage;
            var reconciledSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(finalAnswer, sources, finalSourceAlignmentQuery);
            if (reconciledSources.Count > 0)
            {
                sources = reconciledSources;
            }
            else if (ShouldRequireVisibleSourcesToBeCited(finalAnswer, finalSourceAlignmentQuery)
                && ShouldAllowSourceBackedWriterRepairForCurrentTurn(finalSourceAlignmentQuery))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                    chatHistory,
                    finalSourceAlignmentQuery,
                    plan,
                    writerToolResults,
                    ct).ConfigureAwait(false);
                repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);

                var repairSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(
                    repairAnswer,
                    DeriveSourcesForSourceBackedFallback(sourceToolResults, finalSourceAlignmentQuery),
                    finalSourceAlignmentQuery);

                if (!string.IsNullOrWhiteSpace(repairAnswer) && repairSources.Count > 0)
                {
                    finalAnswer = repairAnswer;
                    sources = repairSources;
                    _lastAnswerSource = $"post_writer_guard_source_alignment_repaired:{plan.Intent}";
                }
                else
                {
                    finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                        plan.Language,
                        finalSourceAlignmentQuery,
                        finalSourceAlignmentQuery,
                        sources?.Count ?? 0,
                        searchAlreadyExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
                    sources = new List<ToolMemory.SourceRef>();
                    _lastAnswerSource = $"post_writer_guard_source_alignment_insufficient:{plan.Intent}";
                }
            }
        }

        var absoluteFinalizerSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.absolute_finalizer.start",
            ("used_rag", usedRagSearch),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("query", finalPlanningCoverageQuery));
        var absoluteFinalPlanningAnswer = string.Empty;
        var absoluteFinalPlanningSources = new List<ToolMemory.SourceRef>();
        var absoluteFinalPlanningAnalysis = PlanningAnswerSupportAnalysis.Empty;
        var absoluteFinalPlanningResolution = "not_run";
        var absoluteFinalizerHandled = usedRagSearch
            && TryFinalizeSourceBackedPlanningResponse(
                finalAnswer,
                sourceToolResults,
                finalPlanningCoverageQuery,
                plan.Language,
                out absoluteFinalPlanningAnswer,
                out absoluteFinalPlanningSources,
                out absoluteFinalPlanningAnalysis,
                out absoluteFinalPlanningResolution);
        EmitRagTrace(
            "writer.absolute_finalizer.end",
            ("handled", absoluteFinalizerHandled),
            ("resolution", absoluteFinalizerHandled ? absoluteFinalPlanningResolution : null),
            ("items", absoluteFinalPlanningAnalysis?.ItemCount ?? 0),
            ("supported", absoluteFinalPlanningAnalysis?.SupportedItemCount ?? 0),
            ("unsupported", absoluteFinalPlanningAnalysis?.UnsupportedItemCount ?? 0),
            ("candidates", absoluteFinalPlanningAnalysis?.CandidateCount ?? 0),
            ("sources", absoluteFinalizerHandled ? absoluteFinalPlanningSources.Count : sources?.Count ?? 0),
            ("ms", absoluteFinalizerSw.ElapsedMilliseconds));
        if (absoluteFinalizerHandled)
        {
            ClientLog.Info(
                "ToolAgent planning AnswerAsync finalizer: " +
                $"resolution={absoluteFinalPlanningResolution} " +
                $"items={absoluteFinalPlanningAnalysis?.ItemCount ?? 0} " +
                $"supported={absoluteFinalPlanningAnalysis?.SupportedItemCount ?? 0} " +
                $"unsupported={absoluteFinalPlanningAnalysis?.UnsupportedItemCount ?? 0} " +
                $"candidates={absoluteFinalPlanningAnalysis?.CandidateCount ?? 0} " +
                $"sources={absoluteFinalPlanningSources.Count}");
            LogSourceBackedPlanningTrace(
                "answer-async-finalizer",
                sourceToolResults,
                finalPlanningCoverageQuery,
                plan.Language);
            finalAnswer = absoluteFinalPlanningAnswer;
            sources = absoluteFinalPlanningSources;
            _lastAnswerSource = $"{absoluteFinalPlanningResolution}:{plan.Intent}";
        }

        EmitRagTrace(
            "writer.post.end",
            ("intent", plan.Intent),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("answer_source", _lastAnswerSource),
            ("ms", swWriterPost.ElapsedMilliseconds));
        return (finalAnswer ?? string.Empty, sources);
    }

    private static string BuildNonPoorSourceBackedFallbackAnswer(
        ToolResults sourceToolResults,
        string userMessage,
        string language)
    {
        var fallbackAnswer = BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, language);
        return string.IsNullOrWhiteSpace(fallbackAnswer)
            || LooksLikePoorPlanningFallbackAnswer(fallbackAnswer, userMessage)
                ? string.Empty
                : fallbackAnswer;
    }

    private static string BuildSourceBackedSafeFallbackAnswer(
        ToolResults sourceToolResults,
        string userMessage,
        string language,
        bool shouldAvoidRaw)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
        var mustAvoidRaw =
            shouldAvoidRaw
            || ShouldAvoidRawSourceBackedFallback(userMessage)
            || ShouldAvoidRawSourceBackedFallback(intentQuery)
            || LooksLikeSourceBackedBroadResearchRequest(intentQuery);
        if (!mustAvoidRaw)
            return BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, language);

        if (ShouldReturnInsufficientAfterConfirmedBroadPlanningFallback(
                sourceToolResults,
                userMessage,
                intentQuery,
                language,
                out var confirmedBroadPlanningHitCount))
        {
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                userMessage,
                intentQuery,
                confirmedBroadPlanningHitCount,
                searchAlreadyExpanded: true);
        }

        if (RequiresStructuredSourceBackedPlanningCoverage(intentQuery)
            && EvaluateSourceBackedPlanningCoverage(sourceToolResults, intentQuery, language).IsAdequate is false)
        {
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                userMessage,
                intentQuery,
                EnumerateRagHitSummaries(sourceToolResults).Count(),
                HasMergedOrMultipleRagEvidence(sourceToolResults));
        }

        var canUseReadableFallback =
            ShouldAllowReadableSourceBackedPartialFallback(userMessage)
            || ShouldAllowReadableSourceBackedPartialFallback(intentQuery)
            || LooksLikeGenericCollectionOrListRequest(intentQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(intentQuery)
            || LooksLikeSourceBackedOptionRequest(intentQuery)
            || LooksLikeSoftChoiceRecommendationRequest(intentQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(intentQuery)
            || IsBroadenedSourceSearchConfirmationEnvelope(userMessage);
        if (canUseReadableFallback)
        {
            var readableFallback = BuildReadableSourceBackedFallbackIfUseful(sourceToolResults, userMessage, language);
            if (!string.IsNullOrWhiteSpace(readableFallback))
                return SuppressBroadenedSearchOfferIfAlreadyConfirmed(readableFallback, userMessage, language);
        }

        return BuildBroadEvidenceStillInsufficientAnswer(
            language,
            userMessage,
            intentQuery,
            EnumerateRagHitSummaries(sourceToolResults).Count(),
            HasMergedOrMultipleRagEvidence(sourceToolResults));
    }

    private static bool ShouldReturnInsufficientAfterConfirmedBroadPlanningFallback(
        ToolResults sourceToolResults,
        string userMessage,
        string intentQuery,
        string language,
        out int nearbyHitCount)
    {
        nearbyHitCount = 0;
        if (!IsBroadenedSourceSearchConfirmationEnvelope(userMessage)
            || !RequiresStructuredSourceBackedPlanningCoverage(intentQuery))
        {
            return false;
        }

        var coverage = EvaluateSourceBackedPlanningCoverage(sourceToolResults, intentQuery, language);
        nearbyHitCount = Math.Max(
            coverage.CandidateCount,
            EnumerateRagHitSummaries(sourceToolResults).Count());
        return !coverage.IsAdequate
            && !HasUsefulPartialSourceBackedPlanningCoverage(
                coverage,
                searchWasBroadened: true,
                searchWasExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
    }

    private static string BuildSourceBackedSafeFallbackAfterRejectedWriter(
        ToolResults sourceToolResults,
        string userMessage,
        string writerUserMessage,
        string language)
    {
        var fallback = BuildSourceBackedSafeFallbackAnswer(
            sourceToolResults,
            userMessage,
            language,
            shouldAvoidRaw: true);
        fallback = RemoveTrailingModelEmittedSourceList(fallback ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(fallback)
               || ShouldFallbackFromNoRagDataAnswer(fallback)
               || LooksLikeWriterControlLeak(fallback)
               || LooksLikePoorPlanningFallbackAnswer(fallback, writerUserMessage)
            ? string.Empty
            : fallback;
    }

    private async Task<string?> TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
        ToolResults writerToolResults,
        string writerUserMessage,
        string language,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!ShouldRunSourceBackedCandidateAdjudicationForWriter(writerToolResults, writerUserMessage, language))
        {
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "not_applicable"),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }

        var user = BuildSourceBackedCandidateAdjudicationUserPrompt(
            writerToolResults,
            writerUserMessage,
            language);
        if (!user.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase))
        {
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "no_candidate_inventory"),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);
        var system = BuildSourceBackedCandidateAdjudicationSystemPrompt(language);
        try
        {
            EmitRagTrace(
                "writer.candidate_adjudication.start",
                ("prompt_chars", system.Length + user.Length),
                ("tool_items", writerToolResults.Items.Count),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs));
            var raw = await CompleteWithRetryAsync(
                new[] { ("system", system), ("user", user) },
                forceJson: true,
                timeoutCts.Token).ConfigureAwait(false);
            var normalized = NormalizeSourceBackedCandidateAdjudicationJsonForWriter(raw);
            var ok = !string.IsNullOrWhiteSpace(normalized);
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, ok));
            EmitRagTrace(
                "writer.candidate_adjudication.end",
                ("ok", ok),
                ("decision", ok ? ExtractSourceBackedCandidateAdjudicationDecision(normalized) : "invalid_json"),
                ("answer_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds));
            return normalized;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "writer.candidate_adjudication.timeout",
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }
        catch (Exception ex)
        {
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "writer.candidate_adjudication.error",
                ("error", TruncateForPrompt(ex.Message, 240)),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }
    }

    private static bool ShouldRunSourceBackedCandidateAdjudicationForWriter(
        ToolResults writerToolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return false;
        }

        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, query)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, query)
            || ShouldRequireWriterForBroadDocumentaryFinal(writerToolResults, query, language);
    }

    private static string BuildSourceBackedCandidateAdjudicationSystemPrompt(string language)
    {
        language = NormalizeLanguageCode(language);
        return $$"""
You are SAAIA's private source-candidate adjudicator.
Target language for short reasons: {{language}}.

Return one strict JSON object only. Do not answer the user.
Your job is to judge whether retrieved candidates are legitimate building blocks for the user's requested answer shape.

Rules:
- Work for any source category or domain. Do not assume a particular file type, folder, source category or fixed taxonomy.
- Treat route labels, slot labels, retrieval queries, headings and indexes as discovery hints, not proof.
- A candidate is valid only when the title/local evidence/tool result supports using that candidate for at least one requested slot, criterion, phase, role or answer part.
- Mark sourceUseful=false when a source is only navigation, summary-only, duplicated, too vague, too noisy or not needed for the final answer.
- Mark duplicateOf with another candidateKey when two candidates cite the same useful item or the same source/page for the same purpose.
- If evidence is partial, keep useful valid candidates and list the missing slots or criteria instead of rejecting everything.
- Keep reasons short and practical; they are private writer guidance.
""";
    }

    private static string BuildSourceBackedCandidateAdjudicationUserPrompt(
        ToolResults writerToolResults,
        string query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var evidenceInventory = BuildSourceBackedCandidateLeadsForWriter(writerToolResults, query, language);
        var coverageTrace = BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
            writerToolResults,
            query,
            language,
            MaxSourceBackedLlmEvidencePlannerCoverageTraceLines);
        var compactToolResults = TruncateForPrompt(
            SerializeToolResults(writerToolResults),
            SourceBackedCandidateAdjudicationToolResultsChars);

        return $$"""
Do not answer the user. Privately adjudicate the source candidates before the final writer drafts.

USER_REQUEST:
{{query}}

REQUEST_SHAPE:
{{BuildSourceBackedRequestShapeForPrompt(query, language)}}

PLANNING_COVERAGE_TRACE:
{{coverageTrace}}

EVIDENCE_INVENTORY:
{{evidenceInventory}}

COMPACT_TOOL_RESULTS_EXCERPT:
{{compactToolResults}}

Return JSON matching this schema:
{
  "decision": "use_candidates|partial|insufficient",
  "reason": "short private reason",
  "items": [
    {
      "candidateKey": "exact candidateKey from EVIDENCE_ITEM when present",
      "pageKey": "exact pageKey from EVIDENCE_ITEM when present",
      "title": "candidate title",
      "valid": true,
      "sourceUseful": true,
      "duplicateOf": null,
      "requestedSlots": ["requested slot/criterion/phase/role this candidate can support"],
      "confidence": "high|medium|low",
      "reason": "short private reason"
    }
  ],
  "missing": [
    {
      "slotOrCriterion": "requested slot, phase, role, criterion or answer part",
      "reason": "why the available sources do not support it yet"
    }
  ],
  "notes": ["short private writer guidance"]
}

Decision guidance:
- use_candidates: enough valid, useful, non-duplicated candidates exist for a solid answer.
- partial: some valid candidates exist, but the answer must expose missing or uncertain parts.
- insufficient: no candidate is legitimate enough to support the requested concrete answer.
""";
    }

    private static string? NormalizeSourceBackedCandidateAdjudicationJsonForWriter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !TryExtractJsonObject(raw, out var json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("decision", out _)
                && !doc.RootElement.TryGetProperty("items", out _))
            {
                return null;
            }

            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractSourceBackedCandidateAdjudicationDecision(string? normalizedJson)
    {
        if (string.IsNullOrWhiteSpace(normalizedJson))
            return "none";

        try
        {
            using var doc = JsonDocument.Parse(normalizedJson);
            return TryGetString(doc.RootElement, "decision") ?? "unknown";
        }
        catch (JsonException)
        {
            return "invalid_json";
        }
    }

    private async Task<string> TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults writerToolResults,
        CancellationToken ct)
    {
        var swRepair = Stopwatch.StartNew();
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        if (!ShouldAllowSourceBackedWriterRepairForCurrentTurn(writerUserMessage))
        {
            EmitRagTrace(
                "writer.repair.skipped",
                ("reason", "not_allowed_for_turn"),
                ("intent", plan.Intent),
                ("user_chars", writerUserMessage.Length),
                ("ms", swRepair.ElapsedMilliseconds));
            return string.Empty;
        }

        var rawRepairToolResults = writerToolResults;
        var writerPromptBudget = ResolveWriterPromptBudget();
        writerToolResults = BuildWriterToolResultsForRuntime(plan, writerToolResults, writerUserMessage, writerPromptBudget);
        var language = NormalizeLanguageCode(plan.Language);
        var hasRagEvidence = writerToolResults.Items.Any(item => item.ToolName is "rag.search" or "rag.multi_search");
        var repairIntentQuery = ResolveSourceBackedFallbackIntentQuery(writerUserMessage);
        var requiresStructuredPlanningCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage)
            || ShouldGateStructuredSourceBackedPlanningCoverage(repairIntentQuery);
        var broadSourceBackedRequest =
            ShouldAvoidRawSourceBackedFallback(writerUserMessage)
            || LooksLikeBroadSynthesisRequestShape(writerUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(writerUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(writerUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(writerUserMessage)
            || LooksLikeGenericCollectionOrListRequest(writerUserMessage);
        var writerAllowed = requiresStructuredPlanningCoverage
            ? ShouldAllowWriterForPartialSourceBackedPlanning(writerToolResults, writerUserMessage, language)
            : ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, writerUserMessage)
              || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, writerUserMessage)
              || ShouldUseWriterForDocumentaryProbeAnswer(writerToolResults, writerUserMessage)
              || (broadSourceBackedRequest && hasRagEvidence);
        if (!writerAllowed || !hasRagEvidence)
        {
            EmitRagTrace(
                "writer.repair.skipped",
                ("reason", !hasRagEvidence ? "no_rag_evidence" : requiresStructuredPlanningCoverage ? "insufficient_structured_planning_coverage" : "writer_not_allowed"),
                ("intent", plan.Intent),
                ("has_rag_evidence", hasRagEvidence),
                ("writer_allowed", writerAllowed),
                ("structured_planning_coverage_required", requiresStructuredPlanningCoverage),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", swRepair.ElapsedMilliseconds));
            return string.Empty;
        }

        var candidateAdjudicationJson = await TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
            writerToolResults,
            writerUserMessage,
            language,
            ct).ConfigureAwait(false);
        var system = $@"
You are SAAIA assistant.
Target language: {language}.

The tool results contain documented evidence and private drafting aids for a documentary request.

Rules:
- Answer only from the tool results for concrete facts, items, steps, values, dates and citations.
- The final answer must be written in the target language. If a source is in another language, translate/paraphrase the useful meaning into the target language and keep only source names, page numbers, units, values and short quoted terms unchanged.
- Do not say there is no data when hits are present.
- Do not dump raw excerpts.
- Treat PRIVATE_SOURCE_WRITING_BRIEF, PRIVATE_SOURCE_COVERAGE_NOTE, PRIVATE_SOURCE_CANDIDATE_ADJUDICATION and PRIVATE_SOURCE_EVIDENCE_INVENTORY as private drafting aids, not final wording. Do not expose control words such as coverage, candidate(s), slot(s), evidenceRole, writerEvidence or tool result.
- Use PRIVATE_SOURCE_CANDIDATE_ADJUDICATION as a private veto/priority signal: valid=false, sourceUseful=false or duplicateOf entries should not be promoted as final sourced items unless TOOL_RESULTS clearly contradicts the private verdict.
- Your job is to rewrite and synthesize: extract useful facts from the hits, make editorial choices, then present them as polished user-facing prose instead of pasting retrieved text.
- Do not repeat or paraphrase the user's whole question in the first sentence.
- Do not write bullets whose main content is ""document p.N: copied passage"". Keep source names/pages as short references after a concise item, decision, step, comparison point or planning point.
- Do not write a final Source/Sources bibliography section. The application appends clickable source cards.
- Do not invent concrete items/actions that are absent from the hits.
- Separate sourced facts from your organization layer: you may arrange sourced candidates into a plan, comparison, recommendation, procedure outline or document list when useful, but state the limits when the sources are partial.
- Build a short, useful, user-friendly answer from the available evidence: a natural opening, the requested structure, concise items/actions, a short final limitation note only when necessary, and source names/pages.
- For planning requests, start with the actual draft structure. Do not open with ""I can build..."" or with a limitation note; put any source-limit note after the draft.
- If the sources are partial, make the answer useful first and place the limitation at the end. Avoid mechanical phrases such as ""X candidate(s) for Y slot(s)"" unless the user asked for diagnostics.
- Never answer with private-search wording such as ""source-backed leads"", ""usable starting options"", ""candidate bank"", ""documented elements available"", ""without adding facts"", ""I limit the answer to excerpts"", or translated equivalents. Use those signals only to draft cleaner prose.
- Prefer clear user-facing labels instead of technical wording.
- Remove noisy OCR artifacts and avoid copying long passage fragments.
- Correct obvious OCR/text-extraction damage, missing accents, broken spacing and malformed words when doing so does not change the source facts.
- Keep the answer compact by default. For explicit grids, plans, comparisons or step lists, use the requested structure instead of forcing everything into 8 bullets.
- For every concrete item, action, quantity, timing or citation, preserve only what appears in the hits; for wording, structure, grouping, titles and explanations, write naturally.
- You may use lightweight Markdown when it improves readability: short section labels, bullet or numbered lists, and **bold** for important labels. Do not use code fences or Markdown pipe tables.
- For plans or grids, do not fill requested places with generic background, constraints, document summaries, navigation labels, table-of-contents entries, or repeated source fragments. A filled place needs a concrete sourced item/action/value that fits that place.
- If the available hits contain only general context, write a short useful context section and explain that the requested structure still needs more concrete sourced items. Do not turn general context into fake plan entries.
- If the same item/source/page would be repeated across many requested places, stop and give a partial proposal plus the missing/uncertain parts instead of pretending the plan is complete.
";

        var user = BuildSourceBackedRepairWriterUserPrompt(
            chatHistory,
            userMessage,
            plan,
            rawRepairToolResults,
            writerToolResults,
            _mem.LastSourcesUsed,
            writerPromptBudget,
            candidateAdjudicationJson);

        var messages = new[]
        {
            ("system", system),
            ("user", user)
        };

        string repair;
        try
        {
            EmitRagTrace(
                "writer.repair.start",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("tool_items", writerToolResults.Items.Count));
            repair = await StreamOrCompleteWithRetryAsync(messages, onDelta: null, ct).ConfigureAwait(false);
            EmitRagTrace(
                "writer.repair.end",
                ("intent", plan.Intent),
                ("answer_chars", repair?.Length ?? 0),
                ("ms", swRepair.ElapsedMilliseconds));
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex))
        {
            EmitRagTrace(
                "writer.repair.overflow",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("error", ex.Message),
                ("ms", swRepair.ElapsedMilliseconds));
            repair = string.Empty;
            var overflowRetryToolResults = ApplyWriterToolResultsBudget(
                BuildOverflowRetryWriterToolResults(writerToolResults, writerUserMessage),
                writerUserMessage,
                Math.Max(WriterPromptMinimumToolResultsChars, writerPromptBudget.ToolResultsChars / 2));

            if (overflowRetryToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            {
                var retryUser = BuildOverflowRetryWriterUserPrompt(
                    chatHistory,
                    writerUserMessage,
                    plan,
                    overflowRetryToolResults,
                    BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language));
                var retryMessages = new[]
                {
                    ("system", system),
                    ("user", retryUser)
                };

                try
                {
                    EmitRagTrace(
                        "writer.repair.retry.start",
                        ("intent", plan.Intent),
                        ("prompt_chars", system.Length + retryUser.Length),
                        ("tool_items", overflowRetryToolResults.Items.Count));
                    repair = await StreamOrCompleteWithRetryAsync(retryMessages, onDelta: null, ct).ConfigureAwait(false);
                    _lastAnswerSource = $"repair_writer_context_overflow_compact_retry:{plan.Intent}";
                    EmitRagTrace(
                        "writer.repair.retry.end",
                        ("intent", plan.Intent),
                        ("answer_chars", repair?.Length ?? 0),
                        ("ms", swRepair.ElapsedMilliseconds));
                }
                catch (Exception retryEx) when (IsLlmContextOverflowException(retryEx))
                {
                    EmitRagTrace(
                        "writer.repair.retry.overflow",
                        ("intent", plan.Intent),
                        ("error", retryEx.Message),
                        ("ms", swRepair.ElapsedMilliseconds));
                    return string.Empty;
                }
            }
        }

        repair = (repair ?? string.Empty).Trim();
        repair = RemoveTrailingModelEmittedSourceList(repair);

        if (ShouldFallbackFromNoRagDataAnswer(repair)
            || LooksLikeWriterControlLeak(repair)
            || LooksLikePoorPlanningFallbackAnswer(repair, writerUserMessage))
        {
            EmitRagTrace(
                "writer.repair.rejected",
                ("intent", plan.Intent),
                ("answer_chars", repair.Length),
                ("control_leak", LooksLikeWriterControlLeak(repair)),
                ("poor_planning", LooksLikePoorPlanningFallbackAnswer(repair, writerUserMessage)),
                ("no_rag_data", ShouldFallbackFromNoRagDataAnswer(repair)),
                ("ms", swRepair.ElapsedMilliseconds));
            return BuildSourceBackedSafeFallbackAfterRejectedWriter(
                rawRepairToolResults,
                userMessage,
                writerUserMessage,
                language);
        }

        EmitRagTrace(
            "writer.repair.accepted",
            ("intent", plan.Intent),
            ("answer_chars", repair.Length),
            ("ms", swRepair.ElapsedMilliseconds));
        return repair;
    }

    private static string BuildSourceBackedRepairWriterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults rawToolResults,
        ToolResults writerToolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        WriterPromptBudget? promptBudget = null,
        string? candidateAdjudicationJson = null)
    {
        var budget = promptBudget ?? CreateWriterPromptBudget(AppSettings.DefaultCtxSize, 900);
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        var toolResultsPromptBlock = BuildWriterToolResultsPromptBlock(
            writerToolResults,
            writerUserMessage,
            plan.Language,
            useCleanSourceBrief: true);
        return $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 6)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language)}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{TruncateForPrompt(BuildSourceBackedCoverageHintsForWriter(writerToolResults, writerUserMessage, plan.Language), budget.CoverageNoteChars)}

PRIVATE_SOURCE_WRITING_BRIEF:
{TruncateForPrompt(BuildSourceBackedWritingBriefForWriter(writerToolResults, writerUserMessage, plan.Language), budget.WritingBriefChars)}

PRIVATE_SOURCE_RESEARCH_MAP:
{TruncateForPrompt(BuildSourceBackedResearchMapForWriter(rawToolResults, lastSourcesUsed, writerUserMessage, plan.Language), budget.ResearchMapChars)}

PRIVATE_SOURCE_CANDIDATE_ADJUDICATION (json):
{candidateAdjudicationJson ?? "null"}

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{TruncateForPrompt(BuildSourceBackedCandidateLeadsForWriter(writerToolResults, writerUserMessage, plan.Language), budget.EvidenceInventoryChars)}

{toolResultsPromptBlock}
";
    }

    private static string ResolveSourceBackedWriterUserMessage(string? userMessage)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage ?? string.Empty);
        return string.IsNullOrWhiteSpace(intentQuery) ? userMessage ?? string.Empty : intentQuery;
    }

    private static string BuildSourceBackedWriterFollowupContextNote(string? userMessage, string language)
    {
        if (!IsBroadenedSourceSearchConfirmationEnvelope(userMessage))
            return SourceBackedLabel(
                language,
                "Aucun suivi utilisateur particulier.",
                "No special user follow-up context.",
                "Sin contexto especial de seguimiento del usuario.",
                "Sem contexto especial de seguimento do utilizador.",
                "Kein besonderer Folgekontext des Nutzers.",
                "Nessun contesto di follow-up particolare.");

        return SourceBackedLabel(
            language,
            "L'utilisateur a confirme qu'il faut elargir la recherche autour de la demande precedente. Redige la reponse finale a partir de USER_MESSAGE, pas a partir du bloc technique de confirmation. Ne repropose pas la meme recherche elargie; si les sources restent insuffisantes, explique simplement ce qui manque.",
            "The user confirmed that the search should be broadened around the previous request. Write the final answer from USER_MESSAGE, not from the technical confirmation block. Do not offer the same broadened search again; if sources are still insufficient, explain plainly what is missing.",
            "El usuario confirmo que hay que ampliar la busqueda alrededor de la solicitud anterior. Redacta la respuesta final a partir de USER_MESSAGE, no del bloque tecnico de confirmacion. No vuelvas a ofrecer la misma busqueda ampliada; si las fuentes siguen siendo insuficientes, explica claramente que falta.",
            "O utilizador confirmou que a pesquisa deve ser alargada em torno do pedido anterior. Redige a resposta final a partir de USER_MESSAGE, nao do bloco tecnico de confirmacao. Nao voltes a propor a mesma pesquisa alargada; se as fontes continuarem insuficientes, explica claramente o que falta.",
            "Der Nutzer hat bestaetigt, dass die Suche rund um die vorherige Anfrage erweitert werden soll. Schreibe die finale Antwort aus USER_MESSAGE, nicht aus dem technischen Bestaetigungsblock. Biete dieselbe erweiterte Suche nicht erneut an; wenn die Quellen weiterhin nicht ausreichen, erkläre klar, was fehlt.",
            "L'utente ha confermato che la ricerca va ampliata attorno alla richiesta precedente. Scrivi la risposta finale da USER_MESSAGE, non dal blocco tecnico di conferma. Non proporre di nuovo la stessa ricerca ampliata; se le fonti restano insufficienti, spiega chiaramente cosa manca.");
    }

    internal static string? ClassifyToolExecutionError(Exception ex, bool isAdminTool)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return isAdminTool ? "admin_invalid_or_forbidden" : "tool_failed";
        }

        var message = ex.Message ?? string.Empty;
        if (isAdminTool)
        {
            if (Regex.IsMatch(message, @"(^|\D)401(\D|$)", RegexOptions.CultureInvariant)
                || Regex.IsMatch(message, @"(^|\D)403(\D|$)", RegexOptions.CultureInvariant)
                || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return "admin_invalid_or_forbidden";
            }
        }

        return null;
    }

    internal static bool ShouldBypassWriterForDeterministicInventory(string? intent, IEnumerable<string> toolNames, string? inventoryRenderedText)
    {
        if (string.IsNullOrWhiteSpace(inventoryRenderedText))
            return false;

        if (IsInventoryIntent(intent))
            return true;

        var names = toolNames?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        return names.Count > 0 && names.All(IsInventoryLikeToolName);
    }

    private static bool ShouldBypassWriterForDeterministicInventory(RouterPlan plan, ToolResults writerToolResults, string? inventoryRenderedText)
        => ShouldBypassWriterForDeterministicInventory(
            plan.Intent,
            writerToolResults.Items.Where(x => string.IsNullOrWhiteSpace(x.Error)).Select(x => x.ToolName),
            inventoryRenderedText);

    private bool ShouldRunCriticPass(RouterPlan plan, ToolResults toolResults, bool useGeneralChatPrompt, string userMessage)
    {
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;

        if (useGeneralChatPrompt)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "general_chat";
            return false;
        }

        var strict = string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(AppSettings.NormalizeActiveMode(_settings?.ActiveMode), "strict", StringComparison.OrdinalIgnoreCase);
        var broadSourceBackedSynthesis = ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, userMessage)
            || ShouldRouteSourceBackedAnswerThroughWriter(toolResults, userMessage, plan.Language)
            || ShouldRequireWriterForBroadDocumentaryFinal(toolResults, userMessage, plan.Language);
        if (!strict && !broadSourceBackedSynthesis)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "mode_not_strict_or_broad";
            return false;
        }

        var eligible = toolResults.Items.Any(x => string.IsNullOrWhiteSpace(x.Error) && IsGroundedToolForCritic(x.ToolName));
        _lastCriticEligible = eligible;
        if (!eligible)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "no_grounded_tools";
            return false;
        }

        return true;
    }

    private async Task<string> RunCriticPassAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        string draftAnswer,
        CancellationToken ct)
    {
        var system = PromptCatalog.BuildCriticSystemPrompt(plan.Language);
        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}

DRAFT_ANSWER:
{draftAnswer}

TOOL_RESULTS (json):
{SerializeToolResults(toolResults)}
";

        _lastCriticMs = 0;
        _lastCriticStatus = "skipped";
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;

        var swCritic = Stopwatch.StartNew();
        try
        {
            EmitRagTrace(
                "writer.critic.start",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("draft_chars", draftAnswer?.Length ?? 0),
                ("tool_items", toolResults.Items.Count));
            var raw = await _llm.CompleteAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, ct).ConfigureAwait(false);
            swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;

            if (TryParseCriticEnvelope(raw, out var status, out var revised, out var warning))
            {
                _lastCriticStatus = string.IsNullOrWhiteSpace(status) ? "ok" : status!.Trim().ToLowerInvariant();
                _lastCriticWarning = string.IsNullOrWhiteSpace(warning) ? null : warning!.Trim();

                if (string.Equals(status, "revise", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(revised))
                {
                    _lastCriticRevisedAnswer = true;
                    EmitRagTrace(
                        "writer.critic.end",
                        ("intent", plan.Intent),
                        ("status", _lastCriticStatus),
                        ("revised", true),
                        ("raw_chars", raw?.Length ?? 0),
                        ("ms", _lastCriticMs));
                    return revised.Trim();
                }

                EmitRagTrace(
                    "writer.critic.end",
                    ("intent", plan.Intent),
                    ("status", _lastCriticStatus),
                    ("revised", false),
                    ("raw_chars", raw?.Length ?? 0),
                    ("ms", _lastCriticMs));
                return draftAnswer ?? string.Empty;
            }

            _lastCriticStatus = "invalid";
            EmitRagTrace(
                "writer.critic.end",
                ("intent", plan.Intent),
                ("status", _lastCriticStatus),
                ("revised", false),
                ("raw_chars", raw?.Length ?? 0),
                ("ms", _lastCriticMs));
        }
        catch (Exception ex)
        {
            if (swCritic.IsRunning)
                swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;
            _lastCriticStatus = "error";
            EmitRagTrace(
                "writer.critic.failed",
                ("intent", plan.Intent),
                ("error", TruncateForPrompt(ex.Message, 220)),
                ("ms", _lastCriticMs));
        }

        return draftAnswer ?? string.Empty;
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleRouterDrivenDocumentSummaryFlowAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string semanticUserMessage,
        RouterPlan plan,
        DocumentRefResolver.AnalysisResult docResolution,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        if (!TryDetermineSummaryRequestKind(semanticUserMessage, plan, docResolution, out var requestKind))
            return (false, string.Empty, null);

        var docRef = ResolveDocumentReferenceForSummaryPlan(plan, docResolution);
        if (string.IsNullOrWhiteSpace(docRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(plan.Language));
            var clarification = await GenerateClarificationResponseAsync(
                chatHistory,
                displayUserMessage,
                plan.Language,
                "doc_reference",
                docResolution.ClarificationHint ?? "document_reference",
                ct,
                onDelta).ConfigureAwait(false);

            RememberPendingClarification("doc_reference", displayUserMessage, docResolution.ClarificationHint ?? "document_reference", plan.Language);
            onProgress?.Invoke(string.Empty);
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            _lastAnswerSource = "summary.flow:clarification";
            EmitRagTrace(
                "summary.flow.end",
                ("handled", true),
                ("result", "clarification"),
                ("reason", "missing_doc_ref"),
                ("kind", requestKind.ToString()));
            var finalizedClarification = FinalizeAndReturn(
                swTotalPipeline,
                displayUserMessage,
                clarification,
                null,
                "clarification",
                Array.Empty<string>(),
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
            return (true, finalizedClarification.finalAnswer, finalizedClarification.sourcesPayload);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseSummary(plan.Language));
        EmitRagTrace(
            "summary.flow.start",
            ("doc_ref", docRef),
            ("kind", requestKind.ToString()),
            ("intent", plan.Intent));
        var swSummary = Stopwatch.StartNew();
        var summaryAnswer = await RunKnownDocumentSummaryFlowAsync(semanticUserMessage, docRef, requestKind, ct, onDelta, onProgress).ConfigureAwait(false);
        swSummary.Stop();

        var rememberedIntent = requestKind switch
        {
            DocumentSummaryRequestKind.About => "rag.summarize_doc",
            DocumentSummaryRequestKind.SummaryReadStoredExact => "summary.get",
            DocumentSummaryRequestKind.SummaryCheckOnly => "summary.exists",
            DocumentSummaryRequestKind.SummaryStore => "admin.summary.generate",
            _ => "rag.summarize_doc"
        };

        ClearPendingClarification();
        _lastUsedSummaryFlow = true;
        _lastWriterToolNames = new List<string> { "summary.flow" };
        _lastToolsMs = swSummary.ElapsedMilliseconds;
        _lastWriterMs = 0;
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("summary.flow", swSummary.ElapsedMilliseconds, true) };
        _lastAnswerSource = $"summary.flow:{rememberedIntent}";
        EmitRagTrace(
            "summary.flow.end",
            ("handled", true),
            ("result", "answer"),
            ("intent", rememberedIntent),
            ("answer_source", _lastAnswerSource),
            ("answer_chars", summaryAnswer.finalAnswer?.Length ?? 0),
            ("sources_payload", summaryAnswer.sourcesPayload is not null),
            ("ms", swSummary.ElapsedMilliseconds));
        var finalizedSummary = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            summaryAnswer.finalAnswer ?? string.Empty,
            summaryAnswer.sourcesPayload,
            rememberedIntent,
            new[] { "summary.flow" },
            _mem.LastReasoningTracePublic);
        return (true, finalizedSummary.finalAnswer, finalizedSummary.sourcesPayload);
    }

    private bool TryDetermineSummaryRequestKind(string userMessage, RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution, out DocumentSummaryRequestKind requestKind)
    {
        requestKind = default;

        var intent = NormalizeRouterIntent(plan.Intent);
        if (intent == "summary.exists")
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        if (intent is "admin.summary.generate" or "admin.summary.submit")
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (intent is "document.about" or "document_about" or "rag.about_doc")
        {
            requestKind = DocumentSummaryRequestKind.About;
            return true;
        }

        if (intent == "rag.summarize_doc")
        {
            requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
                ? DocumentSummaryRequestKind.About
                : DocumentSummaryRequestKind.SummaryReadOrLive;
            return true;
        }

        var hasSummaryTool = plan.ToolCalls.Any(call => call.Name is "summary.exists" or "summary.get" or "rag.summarize_live" or "admin.summary.generate" or "admin.summary.request");
        if (!hasSummaryTool)
            return false;

        if (docResolution.WantsStoredSummaryStore)
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (IsExplicitStoredSummaryReadRequest(userMessage, docResolution))
        {
            requestKind = DocumentSummaryRequestKind.SummaryReadStoredExact;
            return true;
        }

        if (docResolution.WantsStoredSummaryCheck)
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
            ? DocumentSummaryRequestKind.About
            : DocumentSummaryRequestKind.SummaryReadOrLive;
        return true;
    }

    private string? ResolveDocumentReferenceForSummaryPlan(RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution)
    {
        foreach (var call in plan.ToolCalls)
        {
            var docRef = GetStringArg(call.Args, "docRef")
                         ?? GetStringArg(call.Args, "pdfRef")
                         ?? GetStringArg(call.Args, "docId")
                         ?? GetStringArg(call.Args, "docPath");
            if (!string.IsNullOrWhiteSpace(docRef))
                return docRef;
        }

        if (!string.IsNullOrWhiteSpace(docResolution.ResolvedDocRef))
            return docResolution.ResolvedDocRef;

        if (_mem.LastFocusedDocument is not null)
        {
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocId))
                return _mem.LastFocusedDocument.DocId;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocPath))
                return _mem.LastFocusedDocument.DocPath;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocName))
                return _mem.LastFocusedDocument.DocName;
        }

        return null;
    }

    private static IEnumerable<RouterPlan.ToolCall> SanitizeToolCalls(IReadOnlyList<RouterPlan.ToolCall>? toolCalls)
    {
        if (toolCalls is null)
            yield break;

        var ragCalls = 0;
        foreach (var call in toolCalls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Name))
                continue;

            var normalizedName = NormalizeToolName(call.Name);
            if (!ToolManifest.IsKnownTool(normalizedName))
                continue;

            if (IsRagToolName(normalizedName))
            {
                ragCalls++;
                if (ragCalls > MaxRagToolCalls)
                    continue;
            }

            yield return new RouterPlan.ToolCall
            {
                Name = normalizedName,
                Args = NormalizeToolArgs(normalizedName, call.Args)
            };
        }
    }

    private static string NormalizeToolName(string? toolName)
    {
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "documents.catalog" => "documents.list",
            "documents.find" => "documents.search",
            "diagnostic.latency" => "diagnostic.performance",
            "support.zip" or "support.export" => "support.bundle",
            "summary.read" => "summary.get",
            "summary.missing" or "summaries.missing" => "summary.status.list",
            "summary.missing_count" or "summaries.missing_count" => "summary.status.count",
            _ => normalized
        };
    }

    private static JsonElement NormalizeToolArgs(string toolName, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return JsonDocument.Parse("{}").RootElement.Clone();

        object payload = toolName switch
        {
            "documents.list" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                changedSince = NormalizeChangedSinceArg(GetStringArg(args, "changedSince")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.search" => new
            {
                q = (GetStringArg(args, "q") ?? GetStringArg(args, "query") ?? string.Empty).Trim(),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query")
            },
            "documents.categories" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.tree" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                depth = NormalizeIntArg(GetIntArg(args, "depth"), 8, 1, 20),
                format = NormalizeTreeFormat(GetStringArg(args, "format")),
                limit = NormalizeNullableIntArg(GetIntArg(args, "limit"), 1, 5000),
                offset = NormalizeNullableIntArg(GetIntArg(args, "offset"), 0, 100000)
            },
            "documents.navigation" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                docRef = GetDocRefFromArgs(args),
                docPath = NormalizeCategoryPathArg(GetStringArg(args, "docPath")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 120, 1, 200),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.context" => new
            {
                docRef = GetDocRefFromArgs(args),
                docId = GetStringArg(args, "docId"),
                docPath = NormalizeCategoryPathArg(GetStringArg(args, "docPath")),
                chunkId = GetStringArg(args, "chunkId") ?? GetStringArg(args, "chunk_id"),
                pageStart = NormalizeNullableIntArg(GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start"), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end"), 1, 100000),
                before = NormalizeIntArg(GetIntArg(args, "before"), 2, 0, 20),
                after = NormalizeIntArg(GetIntArg(args, "after"), 4, 0, 30),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 12, 1, 50),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.stats" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "documents.empty_count" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath"))
            },
            "documents.empty_list" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 200, 1, 2000),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.extraction_quality" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 200, 1, 2000)
            },
            "documents.extraction_pages" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty
            },
            "sources.resolve" => new
            {
                @ref = (GetStringArg(args, "ref") ?? GetStringArg(args, "pdfRef") ?? string.Empty).Trim()
            },
            "rag.search" => new
            {
                query = (GetStringArg(args, "query") ?? string.Empty).Trim(),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                categoryRef = GetStringArg(args, "categoryRef") ?? GetNestedStringArg(args, "filters", "categoryRef"),
                docId = GetRagDocIdArg(args),
                docPath = GetRagDocPathArg(args),
                maxPerDoc = NormalizeNullableIntArg(GetRagMaxPerDocArg(args), 1, 20),
                maxPerPage = NormalizeNullableIntArg(GetRagMaxPerPageArg(args), 1, 20),
                pageStart = NormalizeNullableIntArg(GetRagPageStartArg(args), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetRagPageEndArg(args), 1, 100000),
                mode = NormalizeRagMode(GetStringArg(args, "mode")),
                researchMode = GetRagResearchModeArg(args),
                includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args)
            },
            "rag.multi_search" => new
            {
                queries = NormalizeRagMultiSearchQueries(args),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                categoryRef = GetStringArg(args, "categoryRef") ?? GetNestedStringArg(args, "filters", "categoryRef"),
                docId = GetRagDocIdArg(args),
                docPath = GetRagDocPathArg(args),
                maxPerDoc = NormalizeNullableIntArg(GetRagMaxPerDocArg(args), 1, 20),
                maxPerPage = NormalizeNullableIntArg(GetRagMaxPerPageArg(args), 1, 20),
                pageStart = NormalizeNullableIntArg(GetRagPageStartArg(args), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetRagPageEndArg(args), 1, 100000),
                mode = NormalizeRagMode(GetStringArg(args, "mode")),
                researchMode = GetRagResearchModeArg(args),
                includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args)
            },
            "rag.summarize_live" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = NormalizeLiveSummaryLevel(GetStringArg(args, "level")),
                strategy = NormalizeSummaryStrategy(GetStringArg(args, "strategy")),
                language = NormalizeSummaryLanguage(GetStringArg(args, "language")),
                responseLanguage = NormalizeSummaryLanguage(GetStringArg(args, "responseLanguage") ?? GetStringArg(args, "language")),
                docLanguage = NormalizeDocumentLanguageTag(GetStringArg(args, "docLanguage")),
                maxWords = NormalizeNullableIntArg(GetIntArg(args, "maxWords"), 20, 1200),
                maxChunks = NormalizeNullableIntArg(GetIntArg(args, "maxChunks"), 1, 40),
                maxBatches = NormalizeNullableIntArg(GetIntArg(args, "maxBatches"), 1, 8),
                maxCharsPerBatch = NormalizeNullableIntArg(GetIntArg(args, "maxCharsPerBatch"), 1000, 12000)
            },
            "summary.get" or "summary.exists" or "admin.summary.request" or "admin.summary.generate" or "admin.summary.delete" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                force = GetBoolArg(args, "force") ?? false
            },
            "summary.search" => new
            {
                q = (GetStringArg(args, "q") ?? string.Empty).Trim(),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 20, 1, 200),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "summary.status.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "summary.status.list" or "admin.summary.missing" => new
            {
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "admin.summary.submit" => new
            {
                jobId = GetStringArg(args, "jobId"),
                executionLeaseToken = GetStringArg(args, "executionLeaseToken") ?? GetStringArg(args, "leaseToken"),
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                docLanguage = NormalizeAdminSummarySubmitDocLanguage(GetStringArg(args, "docLanguage")),
                sourceHash = GetStringArg(args, "sourceHash") ?? string.Empty,
                summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty,
                meta = TryGetObjectArg(args, "meta")
            },
            "admin.summary.status" or "admin.jobs.cancel" => new
            {
                jobId = GetStringArg(args, "jobId") ?? string.Empty
            },
            "admin.ingestion.reindex" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty
            },
            "admin.jobs.list" => new
            {
                type = GetStringArg(args, "type"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "admin.audit" => new
            {
                action = GetStringArg(args, "action"),
                target = GetStringArg(args, "target"),
                since = NormalizeChangedSinceArg(GetStringArg(args, "since")),
                until = NormalizeChangedSinceArg(GetStringArg(args, "until")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 1000),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "diagnostic.performance" => new
            {
                lastN = NormalizeNullableIntArg(GetIntArg(args, "lastN"), 1, 50)
            },
            "export.create" => new
            {
                format = NormalizeExportFormat(GetStringArg(args, "format")),
                fileName = (GetStringArg(args, "fileName") ?? GetStringArg(args, "title") ?? "export").Trim(),
                content = GetStringArg(args, "content") ?? string.Empty
            },
            "support.bundle" => new
            {
                include = GetStringArrayArg(args, "include")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
            },
            "rag.debug.scroll" => new
            {
                docRef = GetDocRefFromArgs(args),
                docId = GetStringArg(args, "docId"),
                docPath = GetStringArg(args, "docPath"),
                category = NormalizeCategoryPathArg(GetStringArg(args, "category") ?? GetStringArg(args, "categoryPath")),
                cursor = GetStringArg(args, "cursor"),
                pageStart = NormalizeNullableIntArg(GetIntArg(args, "pageStart") ?? GetIntArg(args, "page_start"), 1, 100000),
                pageEnd = NormalizeNullableIntArg(GetIntArg(args, "pageEnd") ?? GetIntArg(args, "page_end"), 1, 100000),
                chunkType = GetStringArg(args, "chunkType") ?? GetStringArg(args, "chunk_type"),
                contentRole = GetStringArg(args, "contentRole") ?? GetStringArg(args, "content_role"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500)
            },
            _ => JsonSerializer.Deserialize<object>(args.GetRawText()) ?? new { }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    private static int NormalizeIntArg(int? value, int fallback, int min, int max)
        => Math.Clamp(value ?? fallback, min, max);

    private static int? NormalizeNullableIntArg(int? value, int min, int max)
        => value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    private static string? NormalizeCategoryPathArg(string? categoryPath)
    {
        var normalized = (categoryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : null;
    }

    private static string NormalizeTreeFormat(string? format)
        => string.Equals((format ?? string.Empty).Trim(), "json", StringComparison.OrdinalIgnoreCase) ? "json" : "markdown";

    private static string NormalizeRagMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "focused" or "balanced" or "broad" or "standard" or "strict" ? normalized : "auto";
    }

    private static string NormalizeLiveSummaryLevel(string? level)
    {
        var normalized = (level ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "short" or "medium" or "long" ? normalized : "medium";
    }

    private static string NormalizeSummaryStrategy(string? strategy)
    {
        var normalized = (strategy ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "about" or "summary" or "store" ? normalized : "summary";
    }

    private static string NormalizeSummaryLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "fr" or "en" or "es" or "pt" or "de" or "it" ? normalized : "auto";
    }

    private static string NormalizeAdminSummarySubmitDocLanguage(string? language)
        => NormalizeDocumentLanguageTag(language);

    private static string NormalizeDocumentLanguageTag(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        return IsPlausibleDocumentLanguageTag(normalized) ? normalized : "und";
    }

    private static bool IsPlausibleDocumentLanguageTag(string language)
    {
        if (string.Equals(language, "und", StringComparison.Ordinal))
            return true;
        if (string.IsNullOrWhiteSpace(language) || language.Length is < 2 or > 35)
            return false;

        var parts = language.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return false;
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return false;

        return parts.Skip(1).All(static part =>
            part.Length is >= 2 and <= 8
            && part.All(static ch => char.IsLetterOrDigit(ch)));
    }

    private static string NormalizeExportFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "txt" or "md" or "csv" or "docx" ? normalized : "txt";
    }

    private static string? GetDocRefFromArgs(JsonElement args)
        => GetStringArg(args, "docRef")
           ?? GetStringArg(args, "docId")
           ?? GetStringArg(args, "docPath")
           ?? GetStringArg(args, "ref")
           ?? GetStringArg(args, "pdfRef");

    private static string? GetNestedStringArg(JsonElement args, string parent, string child)
    {
        if (!args.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return GetStringArg(p, child);
    }

    private static object? TryGetObjectArg(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return JsonSerializer.Deserialize<object>(value.GetRawText());
    }

    private RouterPlan SanitizeRouterPlan(RouterPlan plan, string detectedMessageLanguage, bool disallowMetaSetLanguage)
    {
        plan ??= new RouterPlan();

        plan.Mode = NormalizePlanMode(plan.Mode);
        plan.Language = NormalizeLanguageCode(string.IsNullOrWhiteSpace(plan.Language) ? detectedMessageLanguage : plan.Language);
        plan.Intent = NormalizeRouterIntent(plan.Intent);
        plan.ResponseFormat = NormalizeResponseFormat(plan.ResponseFormat);
        plan.MemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        plan.RouterConfidence = ClampConfidence(plan.RouterConfidence);
        plan.ClarificationQuestions = plan.ClarificationQuestions
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList() ?? new List<string>();
        plan.ReasoningTracePublic = plan.ReasoningTracePublic
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Take(3)
            .ToList() ?? new List<string>();
        plan.RiskFlags = plan.RiskFlags
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList() ?? new List<string>();
        plan.ToolCalls = SanitizeToolCalls(plan.ToolCalls).ToList();

        plan.ToolCalls = plan.ToolCalls
            .Where(call => !ToolManifest.IsAdminTool(call.Name))
            .Take(MaxToolCalls)
            .ToList();

        if (string.IsNullOrWhiteSpace(plan.Intent))
            plan.Intent = InferIntentFromToolCalls(plan.ToolCalls) ?? "chat.general";

        if (disallowMetaSetLanguage && string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase))
            plan.Intent = "chat.general";

        if (IsAdminOnlyIntent(plan.Intent))
        {
            plan.Intent = "meta.help";
            plan.NeedClarification = false;
            plan.ClarificationQuestions.Clear();
            plan.ToolCalls.Clear();
            plan.MemoryUpdate = null;
        }

        if (plan.NeedClarification && plan.ClarificationQuestions.Count == 0)
            plan.ClarificationQuestions = new List<string>();

        return plan;
    }

    private static string NormalizeRouterIntent(string? intent)
    {
        var normalized = (intent ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized switch
        {
            "general" or "chat" or "conversation" => "chat.general",
            "set_language" or "change_language" or "meta.change_language" => "meta.set_language",
            "set_style" or "change_style" or "meta.change_style" => "meta.set_style",
            "set_mode" or "change_mode" or "meta.change_mode" => "meta.set_mode",
            "translate_last" or "translate.last" or "meta.translate_last" => "meta.translate_last_answer",
            "repair" or "rewrite_last" or "meta.rewrite_last" => "meta.rewrite_last",
            "count_documents" or "documents.count" => "inventory.count",
            "list_documents" or "documents.list" => "inventory.list",
            "documents.search" or "inventory.find" or "find_documents" => "inventory.find",
            "inventory.changed_since" or "changed_since" or "documents.changed_since" => "inventory.changed_since",
            "categories" or "documents.categories" => "inventory.categories",
            "tree" or "documents.tree" or "inventory.tree_sub" => "inventory.tree",
            "stats" or "documents.stats" => "inventory.stats",
            "inventory.health" or "catalog.health" => "inventory.health",
            "summary.status" or "summary_status" or "inventory.summary_status" or "admin.summary.missing" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" => "inventory.summary_status",
            "document.about" or "document_about" or "rag.about_doc" => "rag.summarize_doc",
            "summary.exists" or "check_summary" => "summary.exists",
            "summary.store" or "refresh_summary" => "admin.summary.generate",
            _ => normalized
        };
    }

    private static bool IsRagToolName(string toolName)
        => toolName.StartsWith("rag.", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldForceRagForStandaloneTopic(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (ShouldRespectLlmRouterGeneralWithoutTools(plan))
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;
        if (plan.Origin == RouterPlanOrigin.LocalFallback
            && LooksLikeLocalFallbackGeneralChatMessage(effectiveUserMessage))
            return false;

        return LooksLikeStandaloneDocumentaryTopic(effectiveUserMessage)
            || !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(effectiveUserMessage))
            || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
            || LooksLikeSourceBackedActionRequest(effectiveUserMessage)
            || LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage)
            || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
            || LooksLikeDocumentaryContentRequest(effectiveUserMessage)
            || LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);
    }

    private static bool LooksLikeLocalFallbackGeneralChatMessage(string? message)
    {
        var normalized = NormalizeLexicalLookup(CollapseWhitespace(message ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => token.Length > 0)
            .ToArray();
        if (tokens.Length > 8)
            return false;

        return Regex.IsMatch(
                normalized,
                @"^(?:bonjour|salut|coucou|hello|hi|hey|merci|thanks?|ok|okay|d\s*accord|oui|non)(?:\s|$)",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:dis|dit|reponds|repond|ecris|ecrit|say|answer|write)\b.{0,40}\b(?:bonjour|salut|hello|hi)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"^(?:qui\s+es\s+tu|who\s+are\s+you|comment\s+ca\s+va|ca\s+va|what\s+can\s+you\s+do)\b",
                RegexOptions.CultureInvariant);
    }

    private static void ApplySourceBackedClarificationOverride(RouterPlan plan, string effectiveUserMessage)
    {
        if (!plan.NeedClarification
            || (!LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                && string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(effectiveUserMessage))
                && !LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
                && !LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage)
                && !LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
                && !LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
                && !LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
                && !LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
                && !LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage)
                && !LooksLikeDocumentaryContentRequest(effectiveUserMessage)
                && !LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage)))
        {
            return;
        }

        plan.NeedClarification = false;
        plan.ClarificationQuestions.Clear();
        plan.Intent = "rag.answer";
    }

    private static bool IsLowValueRouterRagQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var meaningfulTokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return meaningfulTokens.Length == 0;
    }

    private static bool IsWeakRouterRagQueryToken(string token)
        => token is
            "avec" or "sans" or "pour" or "dans" or "les" or "des" or "une" or "the" or "and" or "with"
            or "documents" or "document" or "sources" or "source" or "fichiers" or "fichier"
            or "cherche" or "recherche" or "trouve" or "trouver" or "repond" or "reponds" or "answer"
            or "respond" or "mentionne" or "mentionnes" or "mentionnent"
            or "peux" or "peut" or "faire" or "proposer" or "propose" or "partir"
            or "disponible" or "disponibles" or "utile" or "utiles" or "sais" or "quoi"
            or "pas" or "cette" or "cela" or "repa";

    private static string[] BuildInitialSourceBackedPlanningProbeQueries(
        string effectiveUserMessage,
        IEnumerable<string>? routerQueries = null)
    {
        var candidates = new List<string>();
        var routerCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var pinnedIntentQueries = new List<string>();
        var intentProbe = BuildInitialSourceBackedPlanningIntentProbeQuery(effectiveUserMessage);
        AddInitialSourceBackedPlanningProbeQuery(pinnedIntentQueries, intentProbe);
        foreach (var query in pinnedIntentQueries)
            AddDistinctQuery(candidates, query);
        var pinnedIntentKeys = pinnedIntentQueries
            .Select(NormalizeLexicalLookup)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var preferredCandidateKeys = new HashSet<string>(pinnedIntentKeys, StringComparer.Ordinal);

        if (routerQueries is not null)
        {
            foreach (var query in routerQueries)
            {
                var beforeCount = candidates.Count;
                AddInitialSourceBackedPlanningProbeQuery(candidates, query);
                if (candidates.Count > beforeCount)
                {
                    var key = NormalizeLexicalLookup(candidates[^1]);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        routerCandidateKeys.Add(key);
                        preferredCandidateKeys.Add(key);
                    }
                }
            }
        }

        foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage))
            AddInitialSourceBackedPlanningProbeQuery(candidates, query);

        foreach (var query in BuildPlanningRetrievalQueries(effectiveUserMessage))
            AddInitialSourceBackedPlanningProbeQuery(candidates, query);

        if (candidates.Count == 0)
            AddDistinctQuery(candidates, NormalizeRagQueryForRetrieval(effectiveUserMessage));

        var selectedQueries = candidates
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .GroupBy(static query => NormalizeInitialSourceBackedPlanningProbeFamilyKey(query), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(query => ScoreInitialSourceBackedPlanningProbeQuery(query, effectiveUserMessage, preferredCandidateKeys))
                .ThenBy(static query => query.Length)
                .First())
            .OrderBy(query => ScoreInitialSourceBackedPlanningProbeQuery(query, effectiveUserMessage, preferredCandidateKeys))
            .ThenBy(static query => query.Length)
            .ToArray();

        return pinnedIntentQueries
            .Concat(selectedQueries.Where(query => !pinnedIntentKeys.Contains(NormalizeLexicalLookup(query))))
            .Take(MaxInitialSourceBackedPlanningProbeQueries)
            .ToArray();
    }

    private static string BuildInitialSourceBackedPlanningIntentProbeQuery(string? effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var language = DetectRetrievalExpansionLanguage(effectiveUserMessage);
        var terms = new List<string>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string? value)
        {
            var normalizedTerm = NormalizeLexicalLookup(value);
            if (string.IsNullOrWhiteSpace(normalizedTerm))
                return;

            var tokens = Regex.Matches(normalizedTerm, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(static match => match.Value)
                .Where(static token => !IsSourceBackedPlanningIntentProbeNoiseToken(token))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (tokens.Length == 0)
                return;

            var cleaned = CollapseWhitespace(string.Join(' ', tokens));
            if (cleaned.Length is < 3 or > 54
                || LooksLikeNavigationDiscoveryProbeQuery(cleaned)
                || IsInitialSourceBackedPlanningProbeModifierToken(cleaned))
            {
                return;
            }

            if (emitted.Add(cleaned))
                terms.Add(cleaned);
        }

        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language)
            .Concat(ExtractPlanningSlotRetrievalTerms(effectiveUserMessage))
            .Select(SelectPreferredPlanningSlotRetrievalTerm)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var axisTokens = dayAxis
            .Concat(slotAxis)
            .SelectMany(static term => Regex.Matches(NormalizeLexicalLookup(term), @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(static match => match.Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     normalized,
                     @"\b(?:plan|planning|programme|schedule|calendar|calendrier|semaine|hebdomadaire|week|weekly|semana|semanal|woche|wochenplan|settimana|settimanale)\b",
                     RegexOptions.CultureInvariant))
        {
            AddTerm(match.Value);
        }

        foreach (var term in Regex.Matches(normalized, @"[\p{L}\p{Nd}]{4,}", RegexOptions.CultureInvariant)
                     .Cast<Match>()
                     .Select(static match => match.Value)
                     .Where(static term => term.Length >= 4)
                     .Where(term => !axisTokens.Contains(term))
                     .Where(static term => !IsGenericPlanningCoverageTerm(term))
                     .Where(static term => !IsSourceBackedPlanningIntentProbeNoiseToken(term))
                     .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
                     .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
                     .Distinct(StringComparer.Ordinal)
                     .Take(6))
        {
            AddTerm(term);
        }

        var compactDayAxis = dayAxis.Count > 2
            ? new[] { dayAxis[0], dayAxis[^1] }
            : dayAxis.ToArray();
        foreach (var day in compactDayAxis)
            AddTerm(day);

        foreach (var slot in slotAxis.Take(8))
        {
            AddTerm(slot);
        }

        if (terms.Count < 2)
            return string.Empty;

        var selected = new List<string>();
        foreach (var term in terms)
        {
            var candidate = selected.Count == 0
                ? term
                : string.Join(' ', selected.Concat(new[] { term }));
            if (candidate.Length > 90)
                continue;

            selected.Add(term);
        }

        return selected.Count >= 2
            ? CollapseWhitespace(string.Join(' ', selected))
            : string.Empty;
    }

    private static bool IsSourceBackedPlanningIntentProbeNoiseToken(string token)
    {
        var normalized = NormalizeLexicalLookup(token);
        return string.IsNullOrWhiteSpace(normalized)
            || IsWeakRouterRagQueryToken(normalized)
            || normalized is
                "uniquement" or "seulement" or "only" or "strictement" or "juste" or
                "utilise" or "utiliser" or "utilises" or "using" or
                "evite" or "eviter" or "evites" or "avoid" or "avoids" or
                "doublon" or "doublons" or "duplicate" or "duplicates" or "duplique" or "dupplique" or
                "inutile" or "inutiles" or "unneeded" or "unnecessary" or
                "donne" or "donner" or "donnes" or "provide" or
                "format" or "clair" or "claire" or "clear" or "friendly" or "lisible" or "readable" or
                "user" or "markdown" or "tableau" or "table" or "final" or "finale" or "reponse" or "answer";
    }

    private static void AddInitialSourceBackedPlanningProbeQuery(List<string> queries, string? query)
    {
        var normalizedQuery = CleanInitialSourceBackedPlanningProbeQuery(CollapseWhitespace(query ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalizedQuery)
                || IsLowValueRouterRagQuery(normalizedQuery)
                || LooksLikeNavigationDiscoveryProbeQuery(normalizedQuery))
        {
            return;
        }

        AddDistinctQuery(queries, normalizedQuery);
    }

    private static string CleanInitialSourceBackedPlanningProbeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var normalized = NormalizeLexicalLookup(query);
        var rawTokenCount = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant).Count;
        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Where(static token => !IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length >= 2)
            return string.Join(' ', tokens);
        if (tokens.Length == 1 && rawTokenCount > 1)
            return tokens[0];
        if (tokens.Length == 0)
            return string.Empty;

        return CollapseWhitespace(query);
    }

    private static int ScoreInitialSourceBackedPlanningProbeQuery(
        string query,
        string effectiveUserMessage,
        ISet<string>? preferredNormalizedQueries = null)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return int.MaxValue;

        var score = 0;
        if (preferredNormalizedQueries?.Contains(normalizedQuery) == true)
            score -= query.Length <= 90 ? 80 : 20;

        if (query.Length > 140)
            score += 220;
        else if (query.Length > 100)
            score += 120;
        else if (query.Length > 72)
            score += 45;

        var tokens = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var modifierTokenCount = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (tokens.Length <= 1)
            score += 30;
        else if (tokens.Length > 10)
            score += 90 + ((tokens.Length - 10) * 4);
        else if (tokens.Length > 6)
            score += 25;
        if (modifierTokenCount > 0)
            score += Math.Min(40, modifierTokenCount * 12);

        var normalizedUserMessage = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (!string.IsNullOrWhiteSpace(normalizedUserMessage)
            && string.Equals(normalizedQuery, normalizedUserMessage, StringComparison.Ordinal)
            && query.Length > 72)
        {
            score += 180;
        }

        var userSignalTerms = ExtractQuerySignalTerms(normalizedUserMessage)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        var overlap = userSignalTerms.Count(term => normalizedQuery.Contains(term, StringComparison.Ordinal));
        score += overlap == 0 ? 20 : -Math.Min(18, overlap * 3);

        return score;
    }

    private static string NormalizeInitialSourceBackedPlanningProbeFamilyKey(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Where(static token => !IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return tokens.Length == 0
            ? normalized
            : string.Join(' ', tokens);
    }

    private static bool IsInitialSourceBackedPlanningProbeModifierToken(string token)
        => token is
            "option" or "options" or "idee" or "idees" or "exemple" or "exemples"
            or "suggestion" or "suggestions" or "candidat" or "candidats" or "candidate" or "candidates"
            or "proposition" or "propositions" or "preparation" or "preparations"
            or "element" or "elements" or "item" or "items" or "contenu" or "contenus"
            or "detail" or "details" or "etape" or "etapes" or "source" or "sources";

    private static bool LooksLikeNavigationDiscoveryProbeQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var navigationTerms = new[]
        {
            "sommaire",
            "table des matieres",
            "contents",
            "table of contents",
            "table matieres",
            "index",
            "catalogue",
            "catalog",
            "liste",
            "list",
            "sections",
            "sections principales",
            "indice",
            "contenido",
            "tabla de contenido",
            "sumario",
            "visao geral",
            "inhaltsverzeichnis",
            "inhalt",
            "uebersicht",
            "sommario",
            "panoramica",
            "overview"
        };

        if (Regex.IsMatch(normalized, @"\btable\b.*\bmatieres?\b|\bmatieres?\b.*\btable\b", RegexOptions.CultureInvariant))
            return true;

        return navigationTerms.Any(term =>
            string.Equals(normalized, term, StringComparison.Ordinal)
            || normalized.Contains(term, StringComparison.Ordinal));
    }

    private static string? ResolveTrustedRouterRagCategoryScope(
        string? routerCategoryScope,
        string? fallbackCategoryScope,
        string effectiveUserMessage)
    {
        var candidate = NormalizeCategoryPathArg(routerCategoryScope);
        var fallback = NormalizeCategoryPathArg(fallbackCategoryScope);
        if (LooksLikeLeakedRouterRagCategoryScope(fallback, effectiveUserMessage))
            fallback = null;
        if (LooksLikeLeakedRouterRagCategoryScope(candidate, effectiveUserMessage))
            return fallback;

        return candidate ?? fallback;
    }

    private static bool LooksLikeLeakedRouterRagCategoryScope(string? categoryScope, string effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(categoryScope ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if ((categoryScope ?? string.Empty).Contains('/', StringComparison.Ordinal)
            || Regex.IsMatch(categoryScope ?? string.Empty, @"\bcat[_-]?\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{2,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .ToArray();
        if (tokens.Length < 5)
            return false;

        var containsPromptLeak = Regex.IsMatch(
            normalized,
            @"\b(?:documents?|sources?|fichiers?|cherche|recherche|trouve|mentionne|mentionnent|reponds?|answer|respond|avec|dans|pour|query|question)\b",
            RegexOptions.CultureInvariant);
        if (!containsPromptLeak)
            return false;

        return true;
    }

    private void ApplyDocumentaryRagDefaults(RouterPlan plan, string effectiveUserMessage)
    {
        if (ShouldRespectLlmRouterGeneralWithoutTools(plan))
        {
            EmitRagTrace(
                "documentary_defaults.skipped",
                ("reason", "router_general_no_tools"),
                ("intent", plan.Intent));
            return;
        }

        var exactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var isSourceBackedActionRequest = LooksLikeSourceBackedActionRequest(effectiveUserMessage);
        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        var isSourceBackedAdaptationRequest = LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage);
        var isDocumentaryContentRequest = LooksLikeDocumentaryContentRequest(effectiveUserMessage);
        var isBroadDocumentaryInformationRequest = LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);
        var isDocumentVersionTraceabilityRequest = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage);
        var isDocumentaryPlanning = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage);
        if (isDocumentaryPlanning && ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
            exactItemTitle = null;
        var isSourceBackedRecommendationRequest =
            LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage);
        var preferSingleRagSearch = ShouldPreferSingleRagSearchForDocumentaryRequest(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            exactItemTitle = null;
        var useBroadResearchSurfaces = string.IsNullOrWhiteSpace(exactItemTitle)
            && ShouldUseResearchSurfacesForBroadRagRequest(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(exactItemTitle)
            && !isSourceBackedActionRequest
            && !isComparativeDocumentaryRequest
            && !isSourceBackedAdaptationRequest
            && !isDocumentaryContentRequest
            && !isBroadDocumentaryInformationRequest
            && !isDocumentVersionTraceabilityRequest
            && !isDocumentaryPlanning
            && !isSourceBackedRecommendationRequest)
            return;

        plan.NeedClarification = false;
        plan.ClarificationQuestions.Clear();
        plan.Intent = "rag.answer";

        var normalizedOriginalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);
        var retrievalQuery = !string.IsNullOrWhiteSpace(exactItemTitle)
            ? CollapseWhitespace($"{exactItemTitle} {normalizedOriginalQuery}")
            : normalizedOriginalQuery;
        if (string.IsNullOrWhiteSpace(retrievalQuery))
            retrievalQuery = effectiveUserMessage;
        var topK = string.IsNullOrWhiteSpace(exactItemTitle) ? 8 : 20;
        var categoryScope = ResolveRagCategoryScope(effectiveUserMessage);

        var hasRagCall = plan.ToolCalls.Any(call =>
        {
            var normalizedName = NormalizeToolName(call.Name);
            return string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase);
        });
        if (!hasRagCall)
        {
            plan.Intent = "rag.answer";
            plan.ToolCalls.Clear();
            if (isDocumentaryPlanning && string.IsNullOrWhiteSpace(exactItemTitle))
            {
                var planningQueries = BuildInitialSourceBackedPlanningProbeQueries(effectiveUserMessage);
                plan.ToolCalls.Add(new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = planningQueries,
                        topK = InitialSourceBackedPlanningProbeTopK,
                        maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                        maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                        category = categoryScope,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    })
                });
            }
            else if (isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
            {
                plan.ToolCalls.Add(new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage),
                        topK = NormalizeSourceBackedActionTopK(null, effectiveUserMessage),
                        category = categoryScope,
                        mode = useBroadResearchSurfaces ? "broad" : "balanced",
                        researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                        includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                    })
                });
            }
            else
            {
                plan.ToolCalls.Add(new RouterPlan.ToolCall
                {
                    Name = preferSingleRagSearch || (string.IsNullOrWhiteSpace(exactItemTitle) && !isComparativeDocumentaryRequest && !isSourceBackedAdaptationRequest && !isDocumentaryContentRequest && !isBroadDocumentaryInformationRequest && !isSourceBackedActionRequest && !isDocumentVersionTraceabilityRequest && !isSourceBackedRecommendationRequest) ? "rag.search" : "rag.multi_search",
                    Args = preferSingleRagSearch || (string.IsNullOrWhiteSpace(exactItemTitle) && !isComparativeDocumentaryRequest && !isSourceBackedAdaptationRequest && !isDocumentaryContentRequest && !isBroadDocumentaryInformationRequest && !isSourceBackedActionRequest && !isDocumentVersionTraceabilityRequest && !isSourceBackedRecommendationRequest)
                        ? CreateJsonArgs(new
                        {
                            query = retrievalQuery,
                            topK,
                            category = categoryScope,
                            mode = useBroadResearchSurfaces ? "broad" : "balanced",
                            researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                            includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                        })
                        : CreateJsonArgs(new
                        {
                            queries = !string.IsNullOrWhiteSpace(exactItemTitle)
                                ? BuildPreciseRetrievalQueries(exactItemTitle!, retrievalQuery, effectiveUserMessage)
                                : isSourceBackedAdaptationRequest
                                    ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                    : isBroadDocumentaryInformationRequest
                                    ? BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage)
                                    : isSourceBackedRecommendationRequest
                                    ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                    : isDocumentaryContentRequest
                                    ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                    : isDocumentVersionTraceabilityRequest
                                        ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                        : isSourceBackedActionRequest
                                            ? BuildSourceBackedActionRetrievalQueries(effectiveUserMessage)
                                            : BuildComparativeRetrievalQueries(effectiveUserMessage),
                            topK = !string.IsNullOrWhiteSpace(exactItemTitle)
                                ? 20
                                : isComparativeDocumentaryRequest
                                    ? NormalizeComparativeTopK(null, effectiveUserMessage)
                                : isSourceBackedActionRequest || isSourceBackedAdaptationRequest
                                    ? NormalizeSourceBackedActionTopK(null, effectiveUserMessage)
                                    : isSourceBackedRecommendationRequest
                                        ? NormalizeSourceBackedActionTopK(null, effectiveUserMessage)
                                    : isBroadDocumentaryInformationRequest
                                        ? 12
                                    : 12,
                            category = categoryScope,
                            mode = useBroadResearchSurfaces ? "broad" : "balanced",
                            researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                            includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                        })
                });
            }
        }

        foreach (var call in plan.ToolCalls)
        {
            call.Name = NormalizeToolName(call.Name);
            var trustedCategoryScope = ResolveTrustedRouterRagCategoryScope(
                TryGetStringArg(call.Args, "category"),
                categoryScope,
                effectiveUserMessage);
            if (string.Equals(call.Name, "rag.search", StringComparison.OrdinalIgnoreCase))
            {
                if (preferSingleRagSearch && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    call.Args = CreateJsonArgs(new
                    {
                        query = retrievalQuery,
                        topK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 12),
                        category = trustedCategoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isDocumentaryPlanning && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var planningQueries = BuildInitialSourceBackedPlanningProbeQueries(
                        effectiveUserMessage,
                        new[] { TryGetStringArg(call.Args, "query") ?? string.Empty });
                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = planningQueries,
                        topK = InitialSourceBackedPlanningProbeTopK,
                        maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                        maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                        category = trustedCategoryScope,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = BuildPreciseRetrievalQueries(exactItemTitle!, retrievalQuery, effectiveUserMessage),
                        topK = 20,
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isComparativeDocumentaryRequest)
                {
                    var comparativeQueries = BuildComparativeRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    existingQuery = NormalizeComparativeSupplementalRetrievalQuery(existingQuery, effectiveUserMessage);
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !comparativeQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        comparativeQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentVersionTraceabilityRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Insert(0, existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isBroadDocumentaryInformationRequest)
                {
                    var broadQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !broadQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        broadQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = broadQueries.Take(16).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    });
                    continue;
                }

                if (isSourceBackedRecommendationRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Insert(0, existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = useBroadResearchSurfaces ? "broad" : "balanced",
                        researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                        includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                    });
                    continue;
                }

                if (isSourceBackedAdaptationRequest || isSourceBackedActionRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Insert(0, existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentaryContentRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!IsLowValueRouterRagQuery(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Insert(0, existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                call.Args = CreateJsonArgs(new
                {
                    query = retrievalQuery,
                    topK = string.IsNullOrWhiteSpace(exactItemTitle)
                        ? NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 20)
                        : 20,
                    category = trustedCategoryScope,
                    mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                });
            }
            else if (string.Equals(call.Name, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                var queries = TryGetStringArrayArg(call.Args, "queries");
                if (preferSingleRagSearch && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    call.Name = "rag.search";
                    call.Args = CreateJsonArgs(new
                    {
                        query = retrievalQuery,
                        topK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 12),
                        category = trustedCategoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isDocumentaryPlanning && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var planningQueries = BuildInitialSourceBackedPlanningProbeQueries(effectiveUserMessage, queries);

                    call.Args = CreateJsonArgs(new
                    {
                        queries = planningQueries,
                        topK = InitialSourceBackedPlanningProbeTopK,
                        maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                        maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                        category = trustedCategoryScope,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(exactItemTitle)
                    && !queries.Any(q => string.Equals(q, exactItemTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    queries.Insert(0, exactItemTitle!);
                }

                if (isComparativeDocumentaryRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var comparativeQueries = BuildComparativeRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        var supplementalQuery = NormalizeComparativeSupplementalRetrievalQuery(query, effectiveUserMessage);
                        if (!comparativeQueries.Any(q => string.Equals(q, supplementalQuery, StringComparison.OrdinalIgnoreCase)))
                            comparativeQueries.Insert(0, supplementalQuery);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentVersionTraceabilityRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        if (!IsLowValueRouterRagQuery(query)
                            && !actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Insert(0, query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isBroadDocumentaryInformationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var broadQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        if (!IsLowValueRouterRagQuery(query)
                            && !broadQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            broadQueries.Insert(0, query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = broadQueries.Take(16).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    });
                    continue;
                }

                if (isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        if (!IsLowValueRouterRagQuery(query)
                            && !actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Insert(0, query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = useBroadResearchSurfaces ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
                        researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                        includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                    });
                    continue;
                }

                if ((isSourceBackedAdaptationRequest || isSourceBackedActionRequest) && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        if (!IsLowValueRouterRagQuery(query)
                            && !actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Insert(0, query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = trustedCategoryScope,
                        mode = useBroadResearchSurfaces ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
                        researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                        includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                    });
                    continue;
                }

                if (isDocumentaryContentRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries.AsEnumerable().Reverse())
                    {
                        if (!IsLowValueRouterRagQuery(query)
                            && !actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Insert(0, query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = trustedCategoryScope,
                        mode = useBroadResearchSurfaces ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
                        researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                        includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                    });
                    continue;
                }

                if (queries.Count == 0)
                    queries.Add(retrievalQuery);
                else if (!isDocumentaryPlanning
                    && string.IsNullOrWhiteSpace(exactItemTitle)
                    && !queries.Any(q => string.Equals(q, retrievalQuery, StringComparison.OrdinalIgnoreCase)))
                {
                    queries.Insert(0, retrievalQuery);
                }

                var normalizedTopK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 20);
                if (!string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    normalizedTopK = LooksLikeStructuredItemCardRequest(effectiveUserMessage)
                        ? 20
                        : Math.Max(12, normalizedTopK);
                }

                call.Args = CreateJsonArgs(new
                {
                    queries = queries.Take(string.IsNullOrWhiteSpace(exactItemTitle) ? 5 : 8).ToArray(),
                    topK = normalizedTopK,
                    category = trustedCategoryScope,
                    mode = useBroadResearchSurfaces ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
                    researchMode = useBroadResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = useBroadResearchSurfaces ? true : (bool?)null
                });
            }
        }
    }

    private static int NormalizeSourceBackedActionTopK(int? requestedTopK, string effectiveUserMessage)
    {
        var topK = NormalizeIntArg(requestedTopK, 8, 1, 20);
        if (LooksLikeSourceBackedVerificationChecklistRequest(effectiveUserMessage)
            || LooksLikeCorpusClaimVerificationRequest(effectiveUserMessage))
        {
            return Math.Min(topK, 6);
        }

        if (LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage))
            return Math.Max(12, topK);

        if (LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage))
        {
            return Math.Max(10, topK);
        }

        return LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage) ? 8 : topK;
    }

    private static int NormalizeSourceBackedPlanningTopK(int? requestedTopK, string effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var needsStructuredCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var maxTopK = needsStructuredCoverage ? 48 : 24;
        var defaultTopK = targetSlots >= 10
            ? Math.Min(maxTopK, Math.Max(24, targetSlots + 9))
            : 10;
        var topK = NormalizeIntArg(requestedTopK, defaultTopK, 4, maxTopK);
        return targetSlots >= 10 ? Math.Min(maxTopK, Math.Max(defaultTopK, topK)) : Math.Max(8, topK);
    }

    private static (int TopK, int MaxPerDoc, int MaxPerPage) ResolveSourceBackedPlanningInventoryCaps(int? requestedTopK, string effectiveUserMessage)
    {
        var topK = NormalizeSourceBackedPlanningTopK(requestedTopK, effectiveUserMessage);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var maxPerPage = Math.Min(topK, Math.Clamp(targetSlots, 8, 24));
        return (topK, topK, maxPerPage);
    }

    private static int NormalizeComparativeTopK(int? requestedTopK, string effectiveUserMessage)
    {
        if (CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            return NormalizeIntArg(requestedTopK, 6, 1, 12);

        return Math.Max(12, NormalizeIntArg(requestedTopK, 12, 1, 20));
    }

    private static bool ShouldPreferSingleRagSearchForDocumentaryRequest(string effectiveUserMessage)
        => LooksLikeSourceBackedVerificationChecklistRequest(effectiveUserMessage)
            || LooksLikeCorpusClaimVerificationRequest(effectiveUserMessage);

    private string? ResolveRagCategoryScope(string effectiveUserMessage)
    {
        var message = NormalizeLooseLookup(effectiveUserMessage);
        var candidates = _mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>();
        foreach (var category in candidates)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(NormalizeLooseLookup)
                .Where(name => name.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Any(name => message.Contains(name, StringComparison.Ordinal)))
                return string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath;
        }

        var lexicalCategoryScope = TryResolveLexicalRagCategoryScope(effectiveUserMessage, candidates);
        if (!string.IsNullOrWhiteSpace(lexicalCategoryScope))
            return lexicalCategoryScope;

        if (!string.IsNullOrWhiteSpace(_mem.LastResolvedCategory?.CategoryPath))
            return _mem.LastResolvedCategory!.CategoryPath;

        var explicitScope = TryExtractExplicitRagCategoryScope(effectiveUserMessage);
        if (!string.IsNullOrWhiteSpace(explicitScope))
            return explicitScope;

        var lastSourceCategories = (_mem.LastSourcesUsed ?? new List<ToolMemory.SourceRef>())
            .Select(static source => TryExtractTopLevelCategoryFromDocPath(source.DocPath))
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return lastSourceCategories.Length == 1 ? lastSourceCategories[0] : null;
    }

    private JsonElement TrustResolvedLlmRagCategoryScopeArg(JsonElement args)
    {
        if (GetRagTrustCategoryScopeArg(args))
            return args;

        var resolvedCategoryScope = ResolveLlmPlannedRagCategoryScope(GetRagCategoryScopeArg(args));
        if (string.IsNullOrWhiteSpace(resolvedCategoryScope))
            return args;

        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["category"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["categoryPath"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["trustCategoryScope"] = JsonSerializer.SerializeToElement(true);

        EmitRagTrace(
            "router.llm.category_scope.trusted",
            ("category", resolvedCategoryScope));

        return JsonSerializer.SerializeToElement(map);
    }

    private async Task<JsonElement> TryApplyInitialLlmSourceBackedCategoryScopeArgAsync(
        RouterPlan plan,
        JsonElement args,
        string userMessage,
        CancellationToken ct,
        Action<string>? onProgress)
    {
        if (!ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(plan, args, userMessage))
            return args;

        try
        {
            await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "catalog_unavailable"));
            return args;
        }

        var categoryHints = BuildSourceBackedLlmCategoryHintsForPrompt(
            userMessage,
            MaxSourceBackedLlmEvidencePlannerCategoryHints);
        if (!HasSourceBackedLlmCategoryHints(categoryHints))
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "no_category_hints"));
            return args;
        }

        var queries = NormalizeRagMultiSearchQueries(args);
        if (queries.Length == 0)
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "no_queries"));
            return args;
        }

        var currentAnalysis = AnalyzeSourceBackedEvidenceSufficiency(new ToolResults(), userMessage, plan.Language);
        var plannedPasses = new[]
        {
            new SourceBackedEvidenceExplorationPass(
                "router_initial",
                "LLM adjudicates whether the first source-backed retrieval should use a catalog scope.",
                queries,
                Origin: "router_llm_initial")
        };

        if (!ShouldRunLlmSourceBackedCategoryScopeAdjudication(
                plannedPasses,
                currentAnalysis,
                userMessage,
                plan.Language,
                categoryHints))
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "adjudication_not_needed"),
                ("queries", queries),
                ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length));
            return args;
        }

        EmitRagTrace(
            "router.llm_category_scope.start",
            ("queries", queries),
            ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length),
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(userMessage)),
            ("structured_planning", ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)));

        var plannerPasses = await TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
                new ToolResults(),
                currentAnalysis,
                userMessage,
                plan.Language,
                ct,
                onProgress)
            .ConfigureAwait(false);
        string? rawPlannerCategoryScope = null;
        string? resolvedCategoryScope = null;
        var plannerQueryCount = 0;
        var plannerQueries = plannerPasses
            .SelectMany(static pass => pass.Queries)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
        foreach (var plannerPass in plannerPasses)
        {
            plannerQueryCount += plannerPass.Queries.Length;
            if (!string.IsNullOrWhiteSpace(rawPlannerCategoryScope))
                continue;

            var resolvedPlannerCategoryScope = ResolveLlmPlannedRagCategoryScope(plannerPass.CategoryScope);
            if (string.IsNullOrWhiteSpace(resolvedPlannerCategoryScope))
                continue;

            rawPlannerCategoryScope = plannerPass.CategoryScope;
            resolvedCategoryScope = resolvedPlannerCategoryScope;
        }

        EmitRagTrace(
            "router.llm_category_scope.decision",
            ("decision", string.IsNullOrWhiteSpace(resolvedCategoryScope) ? "none" : "use_scope"),
            ("confidence", (object?)(string.IsNullOrWhiteSpace(resolvedCategoryScope) ? null : "planner")),
            ("raw_category", rawPlannerCategoryScope),
            ("resolved_category", resolvedCategoryScope),
                ("accepted", !string.IsNullOrWhiteSpace(resolvedCategoryScope)),
                ("planner_passes", plannerPasses.Count),
                ("planner_queries", plannerQueryCount));
        var applyPlannerQueries = ShouldApplyInitialLlmPlannerQueries(
            queries,
            plannerQueries,
            userMessage,
            plan.Language,
            out var plannerQueryReason,
            out var missingBeforePlannerQueries,
            out var missingAfterPlannerQueries,
            out var regressedPlannerQueryAxes);
        var plannerQueriesToApply = applyPlannerQueries
            ? BuildInitialLlmPlannerQueriesToApply(queries, plannerQueries, userMessage, plan.Language)
            : plannerQueries;
        if (plannerQueries.Length > 0)
        {
            EmitRagTrace(
                applyPlannerQueries
                    ? "router.llm_category_scope.queries_applied"
                    : "router.llm_category_scope.queries_skipped",
                ("reason", plannerQueryReason),
                ("queries_before", queries),
                ("queries_after", plannerQueriesToApply),
                ("planner_queries", plannerQueries),
                ("missing_before", missingBeforePlannerQueries),
                ("missing_after", missingAfterPlannerQueries),
                ("regressed_axes", regressedPlannerQueryAxes));
        }

        if (string.IsNullOrWhiteSpace(resolvedCategoryScope) && !applyPlannerQueries)
            return args;

        var updated = string.IsNullOrWhiteSpace(resolvedCategoryScope)
            ? args
            : ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(args, resolvedCategoryScope);
        if (applyPlannerQueries)
            updated = ApplyInitialSourceBackedLlmPlannerQueriesArg(updated, plannerQueriesToApply);

        EmitRagTrace(
            "router.llm_category_scope.applied",
            ("category", resolvedCategoryScope),
            ("queries", applyPlannerQueries ? plannerQueriesToApply : queries));
        return updated;
    }

    private static string[] BuildInitialLlmPlannerQueriesToApply(
        IReadOnlyList<string> currentQueries,
        IReadOnlyList<string> plannerQueries,
        string userMessage,
        string language)
    {
        return plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
    }

    private static bool ShouldApplyInitialLlmPlannerQueries(
        IReadOnlyList<string> currentQueries,
        IReadOnlyList<string> plannerQueries,
        string userMessage,
        string language,
        out string reason,
        out string[] missingBefore,
        out string[] missingAfter,
        out string[] regressedAxes)
    {
        missingBefore = Array.Empty<string>();
        missingAfter = Array.Empty<string>();
        regressedAxes = Array.Empty<string>();

        var normalizedPlannerQueries = plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPlannerQueries.Length == 0)
        {
            reason = "no_planner_queries";
            return false;
        }

        if (!ShouldGateStructuredSourceBackedPlanningCoverage(userMessage))
        {
            reason = "planner_queries_available";
            return true;
        }

        missingBefore = DetectMissingStructuredRouterSearchAxesForQueries(currentQueries, userMessage, language);
        missingAfter = DetectMissingStructuredRouterSearchAxesForQueries(normalizedPlannerQueries, userMessage, language);
        regressedAxes = FindStructuredRouterSearchAxisRegressions(missingBefore, missingAfter);
        if (regressedAxes.Length > 0)
        {
            reason = "coverage_regressed";
            return false;
        }

        if (missingAfter.Length > missingBefore.Length)
        {
            reason = "coverage_worse";
            return false;
        }

        reason = missingAfter.Length < missingBefore.Length
            ? "coverage_improved"
            : "coverage_preserved";
        return true;
    }

    private static bool ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(
        RouterPlan plan,
        JsonElement args,
        string? userMessage)
    {
        if (plan.Origin != RouterPlanOrigin.Llm)
            return false;

        if (!string.IsNullOrWhiteSpace(GetRagCategoryScopeArg(args))
            || GetRagTrustCategoryScopeArg(args)
            || !string.IsNullOrWhiteSpace(GetRagDocIdArg(args))
            || !string.IsNullOrWhiteSpace(GetRagDocPathArg(args))
            || GetRagPageStartArg(args).HasValue
            || GetRagPageEndArg(args).HasValue)
        {
            return false;
        }

        var sourceExploration =
            string.Equals(GetRagResearchModeArg(args), "source_exploration", StringComparison.OrdinalIgnoreCase)
            || GetRagIncludeResearchSurfacesArg(args) == true;
        if (!sourceExploration)
            return false;

        return LooksLikeAnyDocumentaryPlanningRequest(userMessage)
               || LooksLikeGenericCollectionOrListRequest(userMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
               || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
               || LooksLikeBroadSynthesisRequestShape(userMessage)
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage)
               || ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
               || ShouldOfferBroadenedSourceSearch(userMessage);
    }

    private static JsonElement ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(JsonElement args, string resolvedCategoryScope)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
                  ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["category"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["categoryPath"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["trustCategoryScope"] = JsonSerializer.SerializeToElement(true);
        return JsonSerializer.SerializeToElement(map);
    }

    private static JsonElement ApplyInitialSourceBackedLlmPlannerQueriesArg(JsonElement args, IReadOnlyList<string> plannerQueries)
    {
        var queries = plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
        if (queries.Length == 0)
            return args;

        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
                  ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["queries"] = JsonSerializer.SerializeToElement(queries);
        return JsonSerializer.SerializeToElement(map);
    }

    private static string? TryResolveLexicalRagCategoryScope(
        string effectiveUserMessage,
        IReadOnlyList<ToolMemory.CategorySnapshot> candidates)
    {
        var normalizedQuery = NormalizeLexicalLookup(effectiveUserMessage);
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(normalizedQuery))
            return null;

        var scored = candidates
            .Select(category => new
            {
                Category = category,
                Score = ComputeCategoryHintScore(category, normalizedQuery)
            })
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Category.TotalDocuments)
            .ThenBy(static item => item.Category.Ordinal)
            .Take(2)
            .ToArray();

        if (scored.Length == 0)
            return null;
        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
            return null;

        var winner = scored[0].Category;
        return string.IsNullOrWhiteSpace(winner.CategoryPath) ? winner.DisplayName : winner.CategoryPath;
    }

    private string? ResolveLlmPlannedRagCategoryScope(string? plannedCategoryScope)
    {
        var normalizedScope = NormalizeLooseLookup(plannedCategoryScope);
        if (normalizedScope.Length < 3)
            return null;

        var candidates = (_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .Concat(_mem.LastPresentedCategories ?? new List<ToolMemory.CategorySnapshot>())
            .GroupBy(static category => string.Join(
                    "|",
                    category.CategoryPath,
                    category.CategoryRef,
                    category.DisplayName),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First());

        foreach (var category in candidates)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(NormalizeLooseLookup)
                .Where(static name => name.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Any(name =>
                    string.Equals(name, normalizedScope, StringComparison.Ordinal)
                    || normalizedScope.Contains(name, StringComparison.Ordinal)
                    || name.Contains(normalizedScope, StringComparison.Ordinal)))
            {
                return string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath;
            }
        }

        return null;
    }

    private static string? TryExtractExplicitRagCategoryScope(string? effectiveUserMessage)
    {
        var message = CollapseWhitespace(effectiveUserMessage ?? string.Empty);
        if (message.Length < 8)
            return null;

        foreach (Match match in Regex.Matches(
            message,
            @"(?i)\b(?:dans|in|within|categoria|catégorie|categorie|category|dossier|folder)\s+(?:la|le|les|l['\u2019]|the|un|une|des)?\s*(?<scope>[\p{L}\p{N}][\p{L}\p{N}'\u2019 \-_/&]{2,80})",
            RegexOptions.CultureInvariant))
        {
            var scope = CleanupExplicitRagCategoryScope(match.Groups["scope"].Value);
            if (!string.IsNullOrWhiteSpace(scope))
                return scope;
        }

        return null;
    }

    private static string? CleanupExplicitRagCategoryScope(string? value)
    {
        var scope = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (string.IsNullOrWhiteSpace(scope))
            return null;

        scope = Regex.Replace(
            scope,
            @"(?i)\s+\b(?:pour|afin|avec|qui|que|dont|when|with|for|about|sobre)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        scope = scope.Trim(' ', '.', ',', ';', ':', '!', '?', ')', ']', '}');

        var normalized = NormalizeLooseLookup(scope);
        if (normalized.Length < 3)
            return null;
        if (Regex.IsMatch(
            normalized,
            @"^(?:documents?|docs?|pdf|sources?|corpus|base\s+de\s+connaissance|knowledge\s+base|documentation)$",
            RegexOptions.CultureInvariant))
        {
            return null;
        }

        return scope;
    }

    private static string? TryExtractTopLevelCategoryFromDocPath(string? docPath)
    {
        var normalized = (docPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return null;

        var category = normalized[..slash].Trim();
        return string.IsNullOrWhiteSpace(category) ? null : category;
    }

    private static bool RagResultContainsUsableRequestedTitle(JsonElement ragResult, string requestedTitle)
    {
        if (!HasRagHits(ragResult) || string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        var requestedPdfFile = requestedTitle.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = ragResult
        });

        return EnumerateRagHitSummaries(toolResults)
            .Any(hit => (RagHitContainsRequestedTitle(hit, requestedTitle)
                    || (requestedPdfFile && CandidateMatchesDocumentIdentity(requestedTitle, hit.DocName, hit.DocPath))
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit))
                && !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit));
    }

    private static bool RagResultContainsExplicitDocumentHit(JsonElement ragResult, string requestedDocument)
    {
        if (!HasRagHits(ragResult) || string.IsNullOrWhiteSpace(requestedDocument))
            return false;

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = ragResult
        });

        return EnumerateRagHitSummaries(toolResults)
            .Any(hit => CandidateMatchesDocumentIdentity(requestedDocument, hit.DocName, hit.DocPath));
    }

    private static string[] BuildPreciseRetrievalQueries(string exactTitle, string retrievalQuery, string? originalQuery = null)
    {
        var title = CollapseWhitespace(exactTitle);
        var combined = CollapseWhitespace(retrievalQuery);
        var original = CollapseWhitespace(originalQuery ?? string.Empty);

        if (string.IsNullOrWhiteSpace(title))
        {
            var fallbackQueries = new List<string>();
            AddDistinctQuery(fallbackQueries, combined);
            if (!string.IsNullOrWhiteSpace(original)
                && !string.Equals(original, combined, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctQuery(fallbackQueries, original);
            }

            return fallbackQueries.ToArray();
        }

        var quotedTitle = QuoteLookupTitle(title);
        var queries = new List<string>();
        AddDistinctQuery(queries, title);
        AddDistinctQuery(queries, quotedTitle);
        foreach (var variant in BuildTypoTolerantQueryVariants(title))
        {
            AddDistinctQuery(queries, variant);
            AddDistinctQuery(queries, QuoteLookupTitle(variant));
        }

        foreach (var topic in ExtractDelimitedNonFileTopics(original).Take(3))
        {
            AddDistinctQuery(queries, $"{title} {topic}");
            AddDistinctQuery(queries, topic);
        }

        if (LooksLikeItemLocationLookupRequest(combined))
        {
            AddDistinctQuery(queries, $"{title} source");
            AddDistinctQuery(queries, $"{title} document");
            AddDistinctQuery(queries, $"{title} livre");
            AddDistinctQuery(queries, $"{title} reference");
            return queries.Take(8).ToArray();
        }

        if (LooksLikeStructuredItemCardRequest(combined))
        {
            AddDistinctQuery(queries, $"{title} details");
            AddDistinctQuery(queries, $"{title} procedure");
            AddDistinctQuery(queries, $"{title} quantities");
            AddDistinctQuery(queries, $"{title} timing");
            AddDistinctQuery(queries, $"{title} source");
        }

        if (!string.IsNullOrWhiteSpace(combined) && !string.Equals(title, combined, StringComparison.OrdinalIgnoreCase))
            AddDistinctQuery(queries, combined);

        if (!string.IsNullOrWhiteSpace(original)
            && !string.Equals(original, title, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(original, combined, StringComparison.OrdinalIgnoreCase))
        {
            AddDistinctQuery(queries, original);
        }

        return queries.Take(8).ToArray();
    }

    private static IEnumerable<string> ExtractDelimitedNonFileTopics(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(s))
            yield break;

        foreach (Match match in Regex.Matches(
                     s,
                     """(?:\u00ab|\u201c|"|`)(?<topic>.{3,180}?)(?:\u00bb|\u201d|"|`)""",
                     RegexOptions.CultureInvariant))
        {
            var topic = CollapseWhitespace(match.Groups["topic"].Value);
            if (string.IsNullOrWhiteSpace(topic)
                || Regex.IsMatch(topic, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            yield return topic;
        }
    }

    private static string QuoteLookupTitle(string title)
        => "\"" + title.Replace("\"", string.Empty, StringComparison.Ordinal).Trim() + "\"";

    private static bool LooksLikeItemLocationLookupRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:dans\s+quel(?:le)?\s+(?:livre|document|pdf|fichier|source)|quel(?:le)?\s+(?:livre|document|pdf|fichier|source)|where\s+(?:is|can\s+i\s+find)|which\s+(?:book|document|pdf|file|source)|en\s+que\s+(?:libro|documento|pdf|archivo|fuente)|em\s+que\s+(?:livro|documento|pdf|ficheiro|fonte)|in\s+welchem\s+(?:buch|dokument|pdf|datei)|in\s+quale\s+(?:libro|documento|pdf|file|fonte))\b",
            RegexOptions.CultureInvariant);
    }

    private static string? TryGetStringArg(JsonElement args, string name)
    {
        try
        {
            return args.ValueKind == JsonValueKind.Object ? GetStringArg(args, name) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetIntArg(JsonElement args, string name)
    {
        try
        {
            return args.ValueKind == JsonValueKind.Object ? GetIntArg(args, name) : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> TryGetStringArrayArg(JsonElement args, string name)
    {
        var values = new List<string>();
        try
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return values;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var rawValue = item.GetString();
                var value = LooksLikeQuotedLookupQuery(rawValue)
                    ? CollapseWhitespace(rawValue ?? string.Empty)
                    : NormalizeRagQueryForRetrieval(rawValue);
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }
        catch
        {
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksLikeQuotedLookupQuery(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        return Regex.IsMatch(
            s,
            "^[\\u00ab\\u201c\"]([^\\u00bb\\u201d\"]{3,90})[\\u00bb\\u201d\"]$",
            RegexOptions.CultureInvariant);
    }

    private static string[] NormalizeRagMultiSearchQueries(JsonElement args)
    {
        var queries = new List<string>();
        foreach (var query in GetStringArrayArg(args, "queries") ?? new List<string>())
        {
            AddDistinctRagQuery(queries, query);
            if (LooksLikeComparativeDocumentaryRequest(query))
            {
                foreach (var expanded in BuildComparativeRetrievalQueries(query))
                    AddDistinctRagQuery(queries, expanded);
            }
        }

        var singleQuery = GetStringArg(args, "query");
        if (!string.IsNullOrWhiteSpace(singleQuery))
        {
            AddDistinctRagQuery(queries, singleQuery);
            if (LooksLikeComparativeDocumentaryRequest(singleQuery))
            {
                foreach (var expanded in BuildComparativeRetrievalQueries(singleQuery))
                    AddDistinctRagQuery(queries, expanded);
            }
        }

        return queries.Take(8).ToArray();
    }

    private static void AddDistinctRagQuery(List<string> queries, string? query)
    {
        var value = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!queries.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            queries.Add(value);
    }

    private static bool LooksLikeSourceBackedActionRequest(string? userMessage)
    {
        var s = CollapseWhitespace(userMessage ?? string.Empty);
        if (s.Length < 6)
            return false;

        var normalized = NormalizeLooseLookup(s);
        var hasActionVerb = Regex.IsMatch(
            normalized,
            @"\b(?:aide|aider|analyse|analyser|dis|donne|donner|explique|expliquer|propose|proposes|proposer|trouve|trouver|retrouve|retrouver|retrouves|cherche|chercher|faire|fais|vais|veux|voudrais|souhaite|aimerais|peux|peux-tu|pourrais|as|aurais|idee|faut|besoin|conseille|conseiller|choisir|planifie|planifier|organise|organiser|prepare|preparer|pr.?pare|pr.?parer|verifie|verifier|v.?rifie|v.?rifier|check|verify|help|explain|analyze|analyse|tell|suggest|recommend|can|could|make|plan|prepare|find|give|need|ayuda|ayudar|ayudame|explica|analiza|propone|recomienda|recomendar|puedes|puede|podrias|busca|encuentra|preparar|planificar|necesito|ajuda|ajudar|explica|analisa|recomenda|recomendar|pode|podes|procura|encontra|preparar|planejar|planeia|preciso|vorschlag|erklaere|erklaren|analysiere|empfiehl|empfehlen|kannst|konntest|suche|finde|planen|vorbereiten|helfen|brauche|aiutami|aiuta|spiega|analizza|consiglia|consigliare|puoi|cerca|trova|prepara|pianifica|bisogno)\b",
            RegexOptions.CultureInvariant);

        var asksHow = Regex.IsMatch(
            normalized,
            @"\b(?:comment|how|como|como|wie|come)\b",
            RegexOptions.CultureInvariant);
        var mentionsDocumentarySource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|sourc[\p{L}?]*|extrait|extraits|pages?|documentaire|corpus|knowledge|base|connaissance|adaptation|adapte|adapter|adapt|adaptation)\b",
            RegexOptions.CultureInvariant);
        var asksToBypassSources = Regex.IsMatch(
            normalized,
            @"\b(?:ignore|ignorer|ignorez|oublie|oublier|sans\s+source|sans\s+sources|invente|inventer|inventez|hallucine|halluciner|make\s+up|invent|ignore\s+sources?)\b",
            RegexOptions.CultureInvariant);
        var hasSignalTerms = ExtractQuerySignalTerms(normalized).Any();

        if (LooksLikeDocumentaryContentRequest(userMessage))
            return true;

        if (!hasActionVerb
            && !(asksHow && mentionsDocumentarySource && hasSignalTerms)
            && !(mentionsDocumentarySource && asksToBypassSources && hasSignalTerms))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
            || hasSignalTerms;
    }

    private static string NormalizePlanMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "standard" or "strict" ? normalized : "auto";
    }

    private static string NormalizeResponseFormat(string? responseFormat)
    {
        var normalized = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "about" or "summary" ? normalized : "auto";
    }

    private static double? ClampConfidence(double? confidence)
    {
        if (confidence is null)
            return null;
        if (double.IsNaN(confidence.Value) || double.IsInfinity(confidence.Value))
            return null;
        return Math.Max(0d, Math.Min(1d, confidence.Value));
    }

    private static string? InferIntentFromToolCalls(IReadOnlyList<RouterPlan.ToolCall>? toolCalls)
    {
        var first = toolCalls?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
        if (first is null)
            return null;

        return first.Name switch
        {
            "documents.count" => "inventory.count",
            "documents.categories" => "inventory.categories",
            "documents.tree" => "inventory.tree",
            "documents.navigation" => "rag.answer",
            "documents.stats" => "inventory.stats",
            "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" or "admin.summary.missing" => "inventory.summary_status",
            "documents.list" => HasChangedSinceArg(first.Args) ? "inventory.changed_since" : "inventory.list",
            "documents.search" => "inventory.find",
            "summary.exists" or "summary.get" => "summary.exists",
            "rag.search" or "rag.multi_search" => "rag.answer",
            "rag.summarize_live" => "rag.summarize_doc",
            "admin.summary.request" or "admin.summary.generate" => "admin.summary.generate",
            "diagnostic.performance" => "diagnostic.performance",
            "export.create" => "export.create",
            _ => null
        };
    }

    private static bool IsAdminOnlyIntent(string? intent)
    {
        var normalized = NormalizeRouterIntent(intent);
        return normalized.StartsWith("admin.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "inventory.summary_status", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "inventory.health", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasChangedSinceArg(JsonElement args)
        => !string.IsNullOrWhiteSpace(GetStringArg(args, "changedSince"));

    private static bool IsGroundedToolForCritic(string? toolName)
    {
        return toolName switch
        {
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.categories" or "documents.tree" or "documents.navigation" or "documents.stats" or "documents.empty_count" or "documents.empty_list" or "documents.extraction_quality" or "documents.extraction_pages" or
            "inventory.rendered" or
            "rag.search" or "rag.multi_search" or "rag.summarize_live" or
            "summary.get" or "summary.exists" or "summary.search" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" or
            "sources.resolve" => true,
            _ => false
        };
    }

    private static bool IsAboutResponseFormat(string? responseFormat)
    {
        var normalized = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "about" or "brief" or "short" or "overview";
    }

    private static string? TryGetStructuredToolError(ToolResults.Item item)
    {
        if (!string.IsNullOrWhiteSpace(item.Error))
            return item.Error!.Trim();

        if (item.Result.ValueKind == JsonValueKind.Object
            && item.Result.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.String)
        {
            return (errorEl.GetString() ?? string.Empty).Trim();
        }

        return null;
    }

    private static bool IsRagBusyError(string? error)
    {
        var normalized = (error ?? string.Empty).Trim();
        return string.Equals(normalized, "rag_search_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "transient_rate_limit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "backend_busy", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryBuildToolFailureAnswer(RouterPlan plan, ToolResults toolResults, string language)
    {
        var successful = toolResults.Items.Where(x => string.IsNullOrWhiteSpace(TryGetStructuredToolError(x))).ToList();
        if (successful.Count > 0)
            return null;

        var errors = toolResults.Items
            .Select(TryGetStructuredToolError)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (errors.Count == 0)
            return null;

        if (errors.Any(IsRagBusyError))
        {
            return DeterministicAgentText.RagSearchBusy(language);
        }

        if (errors.Any(x => string.Equals(x, "admin_required", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureAdminRequired(language);
        }

        if (errors.Any(x => string.Equals(x, "admin_invalid_or_forbidden", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureAdminInvalidOrForbidden(language);
        }

        if (errors.Any(x => string.Equals(x, "unknown_tool", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureUnknownPlan(language);
        }

        if (errors.Any(x => string.Equals(x, "tool_failed", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureToolFailed(language);
        }

        return null;
    }

    private (string finalAnswer, object? sourcesPayload) FinalizeAndReturn(
        Stopwatch swTotalPipeline,
        string rememberedUserMessage,
        string finalAnswer,
        object? sourcesPayload,
        string? routerIntent,
        IEnumerable<string>? toolNames,
        IEnumerable<string>? reasoningTracePublic,
        bool clearPendingClarification = true)
    {
        rememberedUserMessage ??= string.Empty;
        finalAnswer ??= string.Empty;
        var toolNameList = toolNames?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray()
            ?? Array.Empty<string>();

        if (clearPendingClarification)
            ClearPendingClarification();

        if (ContainsBroadenedSourceSearchOffer(finalAnswer)
            && ShouldOfferBroadenedSourceSearch(rememberedUserMessage))
        {
            RememberPendingClarification(
                "source_backed_broaden_search",
                rememberedUserMessage,
                "broader_source_search_offer",
                DetectMessageLanguage(finalAnswer));
        }

        if (IsInventoryIntent(routerIntent)
            && _mem.LastDeterministicRender is not null
            && string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.SourceUserMessage))
        {
            _mem.LastDeterministicRender.SourceUserMessage = rememberedUserMessage;
        }

        if (sourcesPayload is not null
            && ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(finalAnswer, new ToolResults(), rememberedUserMessage, DetectMessageLanguage(finalAnswer)))
        {
            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer).Trim();
            sourcesPayload = null;
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        }

        RememberTurnState(rememberedUserMessage, finalAnswer, routerIntent, toolNameList, reasoningTracePublic);
        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        EmitRagTrace(
            "turn.final",
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(rememberedUserMessage)),
            ("intent", routerIntent),
            ("answer_source", _lastAnswerSource),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources_payload", sourcesPayload is null ? "none" : sourcesPayload.GetType().Name),
            ("tool_count", toolNameList.Length),
            ("tools", toolNameList.Take(12).ToArray()),
            ("total_ms", _lastTotalMs));
        ClientLog.Info(
            "ToolAgent turn final: " +
            $"planning={FormatPlanningTraceBool(LooksLikeAnyDocumentaryPlanningRequest(rememberedUserMessage))}|" +
            $"intent={TruncateForPrompt(routerIntent, 80)}|" +
            $"answerSource={TruncateForPrompt(_lastAnswerSource, 120)}|" +
            $"answerChars={finalAnswer?.Length ?? 0}|" +
            $"sourcesPayload={(sourcesPayload is null ? "none" : sourcesPayload.GetType().Name)}|" +
            $"toolCount={toolNameList.Length}|" +
            $"tools={TruncateForPrompt(string.Join(',', toolNameList.Take(12)), 180)}|" +
            $"totalMs={_lastTotalMs}|" +
            $"answerPreview={TruncateForPrompt(finalAnswer, 220)}");
        return (finalAnswer ?? string.Empty, sourcesPayload);
    }

    // ---------------- Utility ----------------

    private static string BuildOverflowRetryWriterUserPrompt(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults overflowRetryToolResults,
        string followupContextNote)
    {
        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        return $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 4)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{followupContextNote}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

COMPACT_RETRY_INSTRUCTIONS:
- The first writer prompt was too large. Use this smaller evidence packet only.
- Do not paste raw passages. Rewrite naturally in the target language with correct spelling, accents and punctuation.
- If the request asks for a plan, recommendation, list, procedure or synthesis, organize the sourced items into the requested shape when possible.
- If evidence is partial, still give a useful partial answer first, then state the limit briefly. Do not ask to broaden the search again unless no usable item exists.
- Do not add a final Source/Sources section; the application appends clickable source cards.

TOOL_RESULTS (json):
{SerializeToolResults(overflowRetryToolResults)}
";
    }

    private static ToolResults BuildOverflowRetryWriterToolResults(ToolResults writerToolResults, string userMessage)
    {
        var retry = new ToolResults();
        var ragItems = writerToolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        if (ragItems.Count == 0)
            return retry;

        retry.Items.Add(new ToolResults.Item
        {
            ToolName = ragItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
            Error = null,
            DurationMs = ragItems.Sum(static item => Math.Max(0, item.DurationMs)),
            Result = CompactMergedRagResultsForWriter(
                ragItems.Select(static item => item.Result).ToList(),
                userMessage,
                precise: false,
                maxHitsOverride: LooksLikeAnyDocumentaryPlanningRequest(userMessage) ? 6 : 5)
        });

        return retry;
    }

    private static bool HasMergedOrMultipleRagEvidence(ToolResults toolResults)
    {
        var ragItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        if (ragItems.Count > 1)
            return true;

        foreach (var item in ragItems)
        {
            var meta = TryGetObject(item.Result, "meta") ?? TryGetObject(item.Result, "Meta");
            if (meta.HasValue && TryGetBool(meta.Value, "merged") is true)
                return true;
        }

        return false;
    }

    private readonly record struct WriterPromptBudget(
        int ContextTokens,
        int ToolResultsChars,
        int CoverageNoteChars,
        int WritingBriefChars,
        int ResearchMapChars,
        int EvidenceInventoryChars);

    private ToolResults BuildWriterToolResultsForRuntime(
        RouterPlan plan,
        ToolResults toolResults,
        string userMessage,
        WriterPromptBudget promptBudget)
    {
        var compacted = BuildWriterToolResults(plan, toolResults, userMessage);
        return ApplyWriterToolResultsBudget(compacted, userMessage, promptBudget.ToolResultsChars);
    }

    private WriterPromptBudget ResolveWriterPromptBudget()
    {
        var contextTokens = ResolveActiveLlmContextTokens(_settings);
        var configuredMaxOutputTokens = _settings?.LlmMaxOutputTokens ?? 900;
        var contextAwareMaxOutputTokens = Math.Clamp(contextTokens / 2, 900, 4096);
        var maxOutputTokens = Math.Clamp(configuredMaxOutputTokens, 384, contextAwareMaxOutputTokens);
        return CreateWriterPromptBudget(contextTokens, maxOutputTokens);
    }

    private static bool ShouldUseCleanSourceBackedWriterPrompt(RouterPlan plan, ToolResults writerToolResults, string userMessage)
    {
        if (!writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return false;

        return ShouldAvoidRawSourceBackedFallback(userMessage)
            || ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage)
            || LooksLikeBroadSynthesisRequestShape(userMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            || LooksLikeGenericCollectionOrListRequest(userMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(userMessage)
            || string.Equals(plan.Intent, "rag.multi_search", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWriterToolResultsPromptBlock(
        ToolResults writerToolResults,
        string userMessage,
        string language,
        bool useCleanSourceBrief)
    {
        if (!useCleanSourceBrief)
            return $"TOOL_RESULTS (json):{Environment.NewLine}{SerializeToolResults(writerToolResults)}";

        var sourceReferenceIndex = BuildSourceBackedCandidateLeadsForWriter(writerToolResults, userMessage, language);
        if (string.IsNullOrWhiteSpace(sourceReferenceIndex))
            sourceReferenceIndex = "none";

        var omitted = JsonSerializer.Serialize(new
        {
            omittedFromWriterPrompt = true,
            reason = "broad_source_backed_synthesis_uses_private_brief",
            availablePrivateSections = new[]
            {
                "PRIVATE_SOURCE_COVERAGE_NOTE",
                "PRIVATE_SOURCE_WRITING_BRIEF",
                "PRIVATE_SOURCE_RESEARCH_MAP",
                "PRIVATE_SOURCE_CANDIDATE_ADJUDICATION",
                "PRIVATE_SOURCE_EVIDENCE_INVENTORY",
                "PRIVATE_SOURCE_REFERENCE_INDEX"
            }
        });

        return $"""
TOOL_RESULTS (omitted):
{omitted}

PRIVATE_SOURCE_REFERENCE_INDEX:
{sourceReferenceIndex}
""";
    }

    private static WriterPromptBudget CreateWriterPromptBudget(int contextTokens, int maxOutputTokens)
    {
        var safeContextTokens = Math.Max(WriterPromptMinimumContextTokens, contextTokens);
        var reservedSystemAndTailTokens = safeContextTokens <= 4096
            ? 1200
            : safeContextTokens <= 8192
                ? 1900
                : 2600;
        var availableToolTokens = Math.Max(
            WriterPromptMinimumToolResultsChars / WriterPromptCharsPerTokenEstimate,
            safeContextTokens - maxOutputTokens - reservedSystemAndTailTokens);
        var toolResultsChars = Math.Clamp(
            availableToolTokens * WriterPromptCharsPerTokenEstimate,
            WriterPromptMinimumToolResultsChars,
            WriterPromptMaximumToolResultsChars);
        var sectionScale = safeContextTokens <= 4096
            ? 0.62
            : safeContextTokens <= 8192
                ? 1.0
                : 1.35;

        return new WriterPromptBudget(
            safeContextTokens,
            toolResultsChars,
            CoverageNoteChars: (int)Math.Round(1100 * sectionScale),
            WritingBriefChars: (int)Math.Round(1500 * sectionScale),
            ResearchMapChars: (int)Math.Round(1800 * sectionScale),
            EvidenceInventoryChars: (int)Math.Round(2200 * sectionScale));
    }

    private static int ResolveActiveLlmContextTokens(AppSettings? settings)
    {
        if (settings?.QualifiedProfile?.CtxSize is int profileCtx && profileCtx >= WriterPromptMinimumContextTokens)
            return profileCtx;

        if (TryExtractCtxSizeFromArgs(settings?.ExtraArgs) is int argsCtx && argsCtx >= WriterPromptMinimumContextTokens)
            return argsCtx;

        return AppSettings.DefaultCtxSize;
    }

    private static int? TryExtractCtxSizeFromArgs(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
            return null;

        var match = Regex.Match(
            extraArgs,
            @"(?:^|\s)(?:--ctx-size|-c)(?:=|\s+)(?<value>\d{3,6})(?=\s|$)",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value)
            ? value
            : null;
    }

    private static ToolResults ApplyWriterToolResultsBudget(ToolResults toolResults, string userMessage, int maxSerializedChars)
    {
        if (maxSerializedChars <= 0 || SerializeToolResults(toolResults).Length <= maxSerializedChars)
            return toolResults;

        var precise = !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
                      || LooksLikeShortTechnicalEvidenceTopic(userMessage);
        var ragItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var summaryItems = toolResults.Items
            .Where(static item => item.ToolName == "summary.search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var otherItems = toolResults.Items
            .Where(static item => item.ToolName is not ("rag.search" or "rag.multi_search" or "summary.search"))
            .ToList();

        var hitLimits = ResolveBudgetedWriterHitLimits(userMessage, precise);
        var summaryLimits = new[] { 8, 4, 2, 0 };
        ToolResults? best = null;

        foreach (var hitLimit in hitLimits)
        {
            foreach (var summaryLimit in summaryLimits)
            {
                var candidate = new ToolResults();
                if (ragItems.Count > 0 && hitLimit > 0)
                {
                    candidate.Items.Add(new ToolResults.Item
                    {
                        ToolName = ragItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
                        Error = null,
                        DurationMs = ragItems.Sum(static item => Math.Max(0, item.DurationMs)),
                        Result = CompactMergedRagResultsForWriter(
                            ragItems.Select(static item => item.Result).ToList(),
                            userMessage,
                            precise,
                            maxHitsOverride: hitLimit)
                    });
                }

                foreach (var summary in summaryItems.Take(summaryLimit))
                {
                    candidate.Items.Add(new ToolResults.Item
                    {
                        ToolName = summary.ToolName,
                        Error = summary.Error,
                        DurationMs = summary.DurationMs,
                        Result = CompactSummarySearchResultForWriter(summary.Result, maxItems: Math.Max(1, summaryLimit), summaryTextChars: 420)
                    });
                }

                foreach (var item in otherItems)
                {
                    TryAddWriterBudgetedItem(candidate, item, Math.Max(1200, maxSerializedChars / 4), maxSerializedChars);
                }

                var serializedLength = SerializeToolResults(candidate).Length;
                if (best is null || serializedLength < SerializeToolResults(best).Length)
                    best = candidate;
                if (serializedLength <= maxSerializedChars)
                    return candidate;
            }
        }

        return best ?? new ToolResults();
    }

    private static IEnumerable<int> ResolveBudgetedWriterHitLimits(string userMessage, bool precise)
    {
        if (precise)
            return new[] { 4, 3, 2, 1 };

        if (LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            || LooksLikeBroadSynthesisRequestShape(userMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage))
        {
            if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
            {
                var targetSlots = ResolveSourceBackedPlanningTargetItemCount(userMessage);
                if (targetSlots >= 10)
                    return new[] { 16, 14, 12, 10, 8, 6, 4, 3, 2 };
            }

            return new[] { 8, 6, 4, 3, 2 };
        }

        return new[] { 6, 4, 3, 2 };
    }

    private static void TryAddWriterBudgetedItem(
        ToolResults candidate,
        ToolResults.Item item,
        int maxItemChars,
        int maxSerializedChars)
    {
        if (!string.IsNullOrWhiteSpace(item.Error))
        {
            candidate.Items.Add(new ToolResults.Item
            {
                ToolName = item.ToolName,
                Error = item.Error,
                DurationMs = item.DurationMs,
                Result = JsonDocument.Parse("""{"error":"tool_failed"}""").RootElement.Clone()
            });
            if (SerializeToolResults(candidate).Length > maxSerializedChars)
                candidate.Items.RemoveAt(candidate.Items.Count - 1);
            return;
        }

        var raw = item.Result.ValueKind == JsonValueKind.Undefined ? string.Empty : item.Result.GetRawText();
        if (raw.Length > maxItemChars)
        {
            var omitted = new ToolResults.Item
            {
                ToolName = item.ToolName,
                Error = item.Error,
                DurationMs = item.DurationMs,
                Result = BuildWriterOmittedToolPayload(item.ToolName, raw.Length)
            };
            candidate.Items.Add(omitted);
            if (SerializeToolResults(candidate).Length > maxSerializedChars)
                candidate.Items.RemoveAt(candidate.Items.Count - 1);
            return;
        }

        candidate.Items.Add(item);
        if (SerializeToolResults(candidate).Length > maxSerializedChars)
            candidate.Items.RemoveAt(candidate.Items.Count - 1);
    }

    private static JsonElement BuildWriterOmittedToolPayload(string toolName, int originalChars)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            omittedFromWriterPrompt = true,
            reason = "writer_context_budget",
            originalTool = toolName,
            originalChars,
            guidance = "This payload was available to the research/navigation phase but was not included in the final writer prompt. Do not treat it as factual evidence; rely on concrete RAG/source hits for final claims."
        })).RootElement.Clone();

    private static ToolResults BuildWriterToolResults(RouterPlan plan, ToolResults toolResults, string userMessage)
    {
        var inventoryRendered = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (inventoryRendered is null)
            return CompactRagToolResultsForWriter(toolResults, userMessage);

        var inventoryIntent = IsInventoryIntent(plan.Intent);
        var inventoryOnly = HasOnlyInventoryTools(toolResults);

        if (!inventoryIntent && !inventoryOnly)
            return CompactRagToolResultsForWriter(toolResults, userMessage);

        var filtered = new ToolResults();
        filtered.Items.Add(new ToolResults.Item
        {
            ToolName = inventoryRendered.ToolName,
            Result = inventoryRendered.Result,
            Error = inventoryRendered.Error,
            DurationMs = inventoryRendered.DurationMs
        });

        var diagnostic = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (diagnostic is not null)
        {
            filtered.Items.Add(new ToolResults.Item
            {
                ToolName = diagnostic.ToolName,
                Result = diagnostic.Result,
                Error = diagnostic.Error,
                DurationMs = diagnostic.DurationMs
            });
        }

        return CompactRagToolResultsForWriter(filtered, userMessage);
    }

    private static ToolResults CompactRagToolResultsForWriter(ToolResults toolResults, string userMessage)
    {
        var precise = !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
                      || LooksLikeShortTechnicalEvidenceTopic(userMessage);
        var compacted = new ToolResults();
        var seenRagHitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasRagEvidence = toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search");
        var ragEvidenceItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var mergeRagEvidence = ShouldMergeRagEvidenceForWriter(userMessage, precise, ragEvidenceItems.Count);
        if (mergeRagEvidence)
        {
            compacted.Items.Add(new ToolResults.Item
            {
                ToolName = ragEvidenceItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
                Error = null,
                DurationMs = ragEvidenceItems.Sum(static item => Math.Max(0, item.DurationMs)),
                Result = CompactMergedRagResultsForWriter(ragEvidenceItems.Select(static item => item.Result).ToList(), userMessage, precise)
            });
        }

        foreach (var item in toolResults.Items)
        {
            if (item.ToolName is "rag.search" or "rag.multi_search"
                && string.IsNullOrWhiteSpace(item.Error)
                && item.Result.ValueKind == JsonValueKind.Object)
            {
                if (mergeRagEvidence)
                    continue;

                compacted.Items.Add(new ToolResults.Item
                {
                    ToolName = item.ToolName,
                    Error = item.Error,
                    DurationMs = item.DurationMs,
                    Result = CompactRagResultForWriter(item.Result, userMessage, precise, seenRagHitKeys)
                });
                continue;
            }

            if (item.ToolName == "summary.search"
                && string.IsNullOrWhiteSpace(item.Error)
                && item.Result.ValueKind == JsonValueKind.Object)
            {
                compacted.Items.Add(new ToolResults.Item
                {
                    ToolName = item.ToolName,
                    Error = item.Error,
                    DurationMs = item.DurationMs,
                    Result = CompactSummarySearchResultForWriter(item.Result)
                });
                continue;
            }

            if (item.ToolName is "documents.tree" or "documents.navigation" && string.IsNullOrWhiteSpace(item.Error))
                continue;

            if (hasRagEvidence && item.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(item.Error))
                continue;

            compacted.Items.Add(item);
        }

        return compacted;
    }

    private static bool ShouldMergeRagEvidenceForWriter(string userMessage, bool precise, int ragEvidenceItemCount)
    {
        if (precise || ragEvidenceItemCount <= 1)
            return false;

        return LooksLikeAnyDocumentaryPlanningRequest(userMessage)
               || LooksLikeBroadSynthesisRequestShape(userMessage)
               || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
               || LooksLikeGenericCollectionOrListRequest(userMessage)
               || LooksLikeSoftChoiceRecommendationRequest(userMessage)
               || LooksLikeSourceBackedPairingRecommendationRequest(userMessage)
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage);
    }

    private static JsonElement CompactSummarySearchResultForWriter(JsonElement result, int maxItems = 20, int summaryTextChars = 1600)
    {
        try
        {
            if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;

            var list = new List<object?>();
            foreach (var it in items.EnumerateArray().Where(static entry => entry.ValueKind == JsonValueKind.Object).Take(Math.Max(0, maxItems)))
            {
                var source = TryBuildSourceRefFromSummarySearchItem(it);
                var sourcePayload = source is null
                    ? null
                    : BuildSourcePayloadItems(new List<ToolMemory.SourceRef> { source }).FirstOrDefault();
                var docPath = source?.DocPath ?? TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);

                list.Add(new
                {
                    docId = source?.DocId ?? TryGetString(it, "docId") ?? TryGetString(it, "DocId"),
                    docPath,
                    docName,
                    level = TryGetString(it, "level") ?? TryGetString(it, "Level"),
                    docLanguage = source?.DocLanguage ?? TryGetDocumentLanguage(it),
                    profileLanguage = source?.ProfileLanguage ?? TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage"),
                    category = source?.Category ?? TryGetString(it, "category") ?? TryGetString(it, "Category"),
                    categoryRef = source?.CategoryRef ?? TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef"),
                    categoryPath = source?.CategoryPath ?? TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath"),
                    sourceHash = source?.SourceHash ?? TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash"),
                    pageStart = source?.PageStart ?? TryGetInt(it, "pageStart") ?? TryGetInt(it, "PageStart"),
                    pageEnd = source?.PageEnd ?? TryGetInt(it, "pageEnd") ?? TryGetInt(it, "PageEnd"),
                    label = source?.Label ?? TryGetString(it, "label") ?? TryGetString(it, "Label"),
                    chunkId = source?.ChunkId ?? TryGetString(it, "chunkId") ?? TryGetString(it, "ChunkId"),
                    summaryText = TruncateForPrompt(TryGetString(it, "summaryText") ?? TryGetString(it, "SummaryText"), summaryTextChars),
                    evidenceSurface = "stored_summary",
                    sourceScope = "document_profile",
                    isFinalEvidence = false,
                    requiresConcreteRetrieval = true,
                    writerUse = "Use only to understand document scope or plan follow-up retrieval; do not cite as factual evidence unless supported by page-grounded RAG hits.",
                    meta = CompactSummaryMetaForPrompt(it),
                    extractionQuality = source is null ? CompactExtractionQualityForPrompt(it) : BuildSourceExtractionQualityPayload(source),
                    matchedContentCards = source is null ? CompactMatchedContentCardsForPrompt(it) : BuildSourceContentCardsPayload(source),
                    profileSignals = source is null ? CompactProfileSignalsForPrompt(it) : BuildSourceProfileSignalsPayload(source),
                    selectionHints = source is null ? CompactSelectionHintsForPrompt(it) : BuildSourceSelectionHintsPayload(source),
                    source = sourcePayload
                });
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                items = list,
                limit = TryGetInt(result, "limit"),
                offset = TryGetInt(result, "offset")
            })).RootElement.Clone();
        }
        catch
        {
            return result;
        }
    }

    private static object? CompactSummaryMetaForPrompt(JsonElement item)
    {
        var meta = TryGetObject(item, "meta")
                   ?? TryGetObject(item, "Meta")
                   ?? TryGetObject(item, "summaryMeta")
                   ?? TryGetObject(item, "SummaryMeta");
        if (meta is null)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent("generator", TryGetString(meta.Value, "generator") ?? TryGetString(meta.Value, "Generator"));
        AddIfPresent("strategy", TryGetString(meta.Value, "strategy") ?? TryGetString(meta.Value, "Strategy"));
        AddIfPresent("outputLanguage", TryGetString(meta.Value, "outputLanguage") ?? TryGetString(meta.Value, "OutputLanguage"));
        AddIfPresent("fallbackUsed", TryGetBool(meta.Value, "fallbackUsed") ?? TryGetBool(meta.Value, "FallbackUsed"));
        AddIfPresent("fallbackReason", TryGetString(meta.Value, "fallbackReason") ?? TryGetString(meta.Value, "FallbackReason"));
        AddIfPresent("qualityScore", TryGetDouble(meta.Value, "qualityScore") ?? TryGetDouble(meta.Value, "QualityScore"));
        AddIfPresent("extractionQuality",
            DeserializePromptObject(meta.Value, "extractionQuality")
            ?? DeserializePromptObject(meta.Value, "extraction_quality")
            ?? DeserializePromptObject(meta.Value, "ExtractionQuality"));
        AddIfPresent("qualitySignals",
            DeserializePromptObject(meta.Value, "qualitySignals")
            ?? DeserializePromptObject(meta.Value, "quality_signals")
            ?? DeserializePromptObject(meta.Value, "QualitySignals"));

        return payload.Count == 0 ? null : payload;

        void AddIfPresent(string key, object? value)
        {
            if (value is not null)
                payload[key] = value;
        }
    }

    private static JsonElement CompactRagResultForWriter(JsonElement result, string userMessage, bool precise, ISet<string>? seenRagHitKeys = null)
    {
        try
        {
            if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                return result;

            var rawHits = hits.EnumerateArray()
                .Where(static it => it.ValueKind == JsonValueKind.Object)
                .Select(static it => it.Clone())
                .ToList();

            return CompactRagHitsForWriter(result, rawHits, userMessage, precise, seenRagHitKeys);
        }
        catch
        {
            return result;
        }
    }

    private static JsonElement CompactMergedRagResultsForWriter(
        IReadOnlyList<JsonElement> results,
        string userMessage,
        bool precise,
        int? maxHitsOverride = null)
    {
        try
        {
            var rawHits = new List<JsonElement>();
            var queries = new List<string>();
            foreach (var result in results)
            {
                if (result.ValueKind != JsonValueKind.Object)
                    continue;

                var meta = TryGetObject(result, "meta") ?? TryGetObject(result, "Meta");
                if (meta.HasValue)
                {
                    queries.AddRange(CompactStringArray(meta.Value, "queries", "Queries", maxItems: 8, maxChars: 120));
                }

                if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var hit in hits.EnumerateArray())
                {
                    if (hit.ValueKind != JsonValueKind.Object)
                        continue;

                    var retrievalQuery = TryGetString(hit, "retrievalQuery")
                                         ?? TryGetString(hit, "retrieval_query")
                                         ?? TryGetString(hit, "RetrievalQuery");
                    if (!string.IsNullOrWhiteSpace(retrievalQuery))
                        queries.Add(retrievalQuery);

                    rawHits.Add(hit.Clone());
                }
            }

            var metaPayload = new
            {
                merged = true,
                sourceResultCount = results.Count,
                rawHitCount = rawHits.Count,
                queries = queries
                    .Where(static query => !string.IsNullOrWhiteSpace(query))
                    .Select(static query => CollapseWhitespace(query))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray()
            };

            var maxHits = maxHitsOverride ?? (LooksLikeAnyDocumentaryPlanningRequest(userMessage)
                ? RagWriterMergedPlanningMaxHits
                : RagWriterMergedBroadMaxHits);

            return CompactRagHitsForWriter(
                originalResult: null,
                rawHits,
                userMessage,
                precise,
                seenRagHitKeys: null,
                maxHitsOverride: maxHits,
                metaOverride: metaPayload,
                guidanceOverride: null);
        }
        catch
        {
            var fallback = results.FirstOrDefault(static result => result.ValueKind == JsonValueKind.Object);
            return fallback.ValueKind == JsonValueKind.Object ? fallback : JsonDocument.Parse("""{"hits":[]}""").RootElement.Clone();
        }
    }

    private static JsonElement CompactRagHitsForWriter(
        JsonElement? originalResult,
        IReadOnlyList<JsonElement> rawHits,
        string userMessage,
        bool precise,
        ISet<string>? seenRagHitKeys = null,
        int? maxHitsOverride = null,
        object? metaOverride = null,
        object? guidanceOverride = null)
    {
        try
        {
            var prioritizeComparison = LooksLikeComparativeDocumentaryRequest(userMessage);
            var prioritizeEvidence = precise || prioritizeComparison;
            var prioritizePlanning = !prioritizeEvidence && LooksLikeAnyDocumentaryPlanningRequest(userMessage);
            var maxHits = maxHitsOverride
                          ?? (prioritizeComparison
                              ? RagWriterComparativeMaxHits
                              : prioritizeEvidence
                                  ? RagWriterMaxHits
                                  : prioritizePlanning
                                      ? RagWriterPlanningMaxHits
                                      : RagWriterBroadMaxHits);
            var excerptChars = prioritizeEvidence ? RagWriterMaxExcerptChars : RagWriterBroadExcerptChars;
            var fullTextChars = prioritizeEvidence ? RagWriterMaxFullTextChars : RagWriterBroadFullTextChars;
            var contextualChars = prioritizeEvidence ? RagWriterContextualTotalChars : RagWriterBroadContextualChars;
            var list = new List<object?>();
            var sourceHits = rawHits
                .Where(static it => !LooksLikeNavigationOnlyHit(BuildRagHitSummary(it)))
                .Where(static it => !LooksLikeLowSignalContentCandidateHit(BuildRagHitSummary(it)))
                .ToList();
            var rankedHits = RankRagHitsForWriter(sourceHits, userMessage);
            rankedHits = PreservePrimaryQueryTopHitsForWriter(
                rankedHits,
                rawHits,
                userMessage,
                maxHits);
            var selectedHits = PreserveComparativeEntityCoverageForWriter(
                rankedHits,
                sourceHits,
                userMessage,
                maxHits);
            var preserveCardLevel = LooksLikeBroadSynthesisRequestShape(userMessage)
                || selectedHits.Any(static hit => !string.IsNullOrWhiteSpace(TryBuildRagWriterContentCardKey(hit)));
            selectedHits = DeduplicateRagHitsForWriter(
                    selectedHits,
                    preserveCardLevel: preserveCardLevel)
                .ToList();
            if (seenRagHitKeys is not null)
            {
                selectedHits = selectedHits
                    .Where(hit => seenRagHitKeys.Add(BuildRagWriterVisiblePageKey(hit, preserveCardLevel)))
                    .ToList();
            }

            foreach (var it in selectedHits.Take(maxHits))
            {
                var hitSummary = BuildRagHitSummary(it);
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? Path.GetFileName(docPath);
                var pageStart = ReadRagHitPageStart(it);
                var pageEnd = ReadRagHitPageEnd(it, pageStart);
                var rawExcerpt = TryGetString(it, "excerpt") ?? TryGetString(it, "text") ?? TryGetString(it, "snippet");
                var rawFullText = TryGetString(it, "fullText") ?? TryGetString(it, "text") ?? TryGetString(it, "snippet");
                var excerpt = TruncateForPrompt(rawExcerpt, excerptChars);
                var fullText = TruncateForPrompt(rawFullText, fullTextChars);
                var contextualSnippet = TruncateForPrompt(TryGetString(it, "contextualSnippet"), contextualChars);
                var extractionQuality = CompactExtractionQualityForPrompt(it);
                var contentSignals = CompactRetrievalContentSignalsForPrompt(it);
                var profileSignals = CompactProfileSignalsForPrompt(it);
                var writerEvidence = BuildWriterEvidenceCueForPrompt(hitSummary, userMessage, maxLength: prioritizeEvidence ? 180 : 260);
                var writerUse = BuildWriterUseCueForPrompt(hitSummary, userMessage);
                var keepBroadCardEvidence = ShouldKeepBroadWriterCardEvidence(hitSummary, list.Count, userMessage);
                var includeCardEvidence = prioritizeEvidence || keepBroadCardEvidence;
                var matchedContentCards = CompactMatchedContentCardsForPrompt(
                    it,
                    maxCards: prioritizeEvidence ? RagWriterMaxContentCards : ResolveBroadWriterContentCardLimit(userMessage, keepBroadCardEvidence),
                    includeEvidence: includeCardEvidence,
                    maxQuantityFacts: RagWriterMaxCardQuantityFacts,
                    maxFacts: RagWriterMaxCardFacts,
                    evidenceTextChars: RagWriterMaxCardEvidenceTextChars);
                var provenanceInfo = prioritizeEvidence
                    ? CompactRagProvenanceForPrompt(it)
                    : null;
                var context = prioritizeEvidence
                    ? CompactRagContextForPrompt(it)
                    : null;

                if (!prioritizeEvidence)
                {
                    var readableEvidence = string.IsNullOrWhiteSpace(writerEvidence) ? null : writerEvidence;
                    list.Add(new
                    {
                        docPath,
                        docName,
                        pageStart,
                        pageEnd,
                        excerpt = readableEvidence ?? (string.IsNullOrWhiteSpace(excerpt) ? null : excerpt),
                        fullText = (string?)null,
                        score = TryGetDouble(it, "score") ?? 0.0,
                        sectionTitle = TryGetString(it, "sectionTitle"),
                        headingPath = TryGetString(it, "headingPath"),
                        category = TryGetString(it, "category") ?? TryGetString(it, "Category"),
                        categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "category_path") ?? TryGetString(it, "CategoryPath"),
                        categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "category_ref") ?? TryGetString(it, "CategoryRef"),
                        docLanguage = TryGetDocumentLanguage(it),
                        profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "profile_language") ?? TryGetString(it, "ProfileLanguage"),
                        sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "source_hash") ?? TryGetString(it, "SourceHash"),
                        retrievalQuery = TryGetString(it, "retrievalQuery") ?? TryGetString(it, "retrieval_query") ?? TryGetString(it, "RetrievalQuery"),
                        retrievalQueryIndex = TryGetInt(it, "retrievalQueryIndex") ?? TryGetInt(it, "retrieval_query_index") ?? TryGetInt(it, "RetrievalQueryIndex"),
                        retrievalHitRank = TryGetInt(it, "retrievalHitRank") ?? TryGetInt(it, "retrieval_hit_rank") ?? TryGetInt(it, "RetrievalHitRank"),
                        retrievalQuerySpecificity = TryGetInt(it, "retrievalQuerySpecificity") ?? TryGetInt(it, "retrieval_query_specificity") ?? TryGetInt(it, "RetrievalQuerySpecificity"),
                        extractionQuality,
                        contentSignals,
                        matchedContentCards,
                        profileSignals,
                        selectionHints = BuildRagSelectionHintsPayload(hitSummary, userMessage),
                        writerEvidence = readableEvidence,
                        writerUse = string.IsNullOrWhiteSpace(writerUse) ? null : writerUse,
                        contextualSnippet = (string?)null
                    });
                    continue;
                }

                list.Add(new
                {
                    docId = TryGetString(it, "docId") ?? TryGetString(it, "doc_id") ?? TryGetString(it, "DocId"),
                    docPath,
                    docName,
                    pageStart,
                    pageEnd,
                    excerpt,
                    fullText,
                    score = TryGetDouble(it, "score") ?? 0.0,
                    sectionTitle = TryGetString(it, "sectionTitle"),
                    headingPath = TryGetString(it, "headingPath"),
                    retriever = TryGetString(it, "retriever"),
                    category = TryGetString(it, "category") ?? TryGetString(it, "Category"),
                    categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "category_path") ?? TryGetString(it, "CategoryPath"),
                    categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "category_ref") ?? TryGetString(it, "CategoryRef"),
                    docLanguage = TryGetDocumentLanguage(it),
                    profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "profile_language") ?? TryGetString(it, "ProfileLanguage"),
                    provenanceInfo,
                    context,
                    rerankScore = TryGetDouble(it, "rerankScore"),
                    exactMatchHit = TryGetBool(it, "exactMatchHit") ?? false,
                    sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "source_hash") ?? TryGetString(it, "SourceHash"),
                    retrievalQuery = TryGetString(it, "retrievalQuery") ?? TryGetString(it, "retrieval_query") ?? TryGetString(it, "RetrievalQuery"),
                    retrievalQueryIndex = TryGetInt(it, "retrievalQueryIndex") ?? TryGetInt(it, "retrieval_query_index") ?? TryGetInt(it, "RetrievalQueryIndex"),
                    retrievalHitRank = TryGetInt(it, "retrievalHitRank") ?? TryGetInt(it, "retrieval_hit_rank") ?? TryGetInt(it, "RetrievalHitRank"),
                    retrievalQuerySpecificity = TryGetInt(it, "retrievalQuerySpecificity") ?? TryGetInt(it, "retrieval_query_specificity") ?? TryGetInt(it, "RetrievalQuerySpecificity"),
                    embeddingBasis = TryGetString(it, "embeddingBasis") ?? TryGetString(it, "embedding_basis") ?? TryGetString(it, "EmbeddingBasis"),
                    chunkId = TryGetString(it, "chunkId") ?? TryGetString(it, "chunk_id") ?? TryGetString(it, "ChunkId"),
                    prevChunkId = TryGetString(it, "prevChunkId") ?? TryGetNestedString(it, "context", "prevChunkId"),
                    nextChunkId = TryGetString(it, "nextChunkId") ?? TryGetNestedString(it, "context", "nextChunkId"),
                    sameSectionChunkId = TryGetString(it, "sameSectionChunkId") ?? TryGetNestedString(it, "context", "sameSectionChunkId"),
                    chunkType = TryGetString(it, "chunkType"),
                    hasTable = TryGetBool(it, "hasTable"),
                    hasWarning = TryGetBool(it, "hasWarning"),
                    hypQuestionsMatched = TryGetBool(it, "hypQuestionsMatched"),
                    extractionQuality,
                    contentSignals,
                    matchedContentCards,
                    profileSignals,
                    selectionHints = BuildRagSelectionHintsPayload(hitSummary, userMessage),
                    writerEvidence = string.IsNullOrWhiteSpace(writerEvidence) ? null : writerEvidence,
                    writerUse = string.IsNullOrWhiteSpace(writerUse) ? null : writerUse,
                    contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
                });
            }

            var meta = metaOverride
                       ?? (originalResult.HasValue ? CompactRagMetaForWriter(originalResult.Value) : null);

            object? guidance = guidanceOverride;
            if (guidance is null
                && originalResult.HasValue
                && originalResult.Value.TryGetProperty("guidance", out var guidanceEl)
                && guidanceEl.ValueKind == JsonValueKind.Object)
            {
                guidance = JsonSerializer.Deserialize<object>(guidanceEl.GetRawText());
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new { hits = list, guidance, meta })).RootElement.Clone();
        }
        catch
        {
            return originalResult ?? JsonDocument.Parse("""{"hits":[]}""").RootElement.Clone();
        }
    }

    private static IEnumerable<JsonElement> DeduplicateRagHitsForWriter(IEnumerable<JsonElement> hits, bool preserveCardLevel)
        => hits
            .GroupBy(hit => BuildRagWriterVisiblePageKey(hit, preserveCardLevel), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(BuildRagHitSummary(hit)))
                .ThenByDescending(static hit => TryGetDouble(hit, "score") ?? 0.0)
                .First());

    private static string BuildRagWriterVisiblePageKey(JsonElement hit, bool preserveCardLevel)
    {
        var pageStart = Math.Max(1, ReadRagHitPageStart(hit));
        var contentCardKey = preserveCardLevel ? TryBuildRagWriterContentCardKey(hit) : string.Empty;
        var cardSuffix = string.IsNullOrWhiteSpace(contentCardKey) ? string.Empty : $"|card:{contentCardKey}";
        var categoryPath = NormalizeLexicalLookup(
            TryGetString(hit, "categoryPath")
            ?? TryGetString(hit, "category_path")
            ?? TryGetString(hit, "CategoryPath")
            ?? TryGetString(hit, "category")
            ?? TryGetString(hit, "Category")
            ?? string.Empty);
        var docPathRaw = TryGetString(hit, "docPath")
                         ?? TryGetString(hit, "doc_path")
                         ?? TryGetString(hit, "DocPath")
                         ?? string.Empty;
        var docPath = NormalizeLexicalLookup(docPathRaw);
        var docName = NormalizeLexicalLookup(
            TryGetString(hit, "docName")
            ?? TryGetString(hit, "doc_name")
            ?? TryGetString(hit, "DocName")
            ?? Path.GetFileName(docPathRaw));
        var fileName = NormalizeLexicalLookup(Path.GetFileName(docPathRaw));
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = docName;

        if (!string.IsNullOrWhiteSpace(categoryPath) && !string.IsNullOrWhiteSpace(fileName))
            return $"category:{categoryPath}|file:{fileName}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(docPath) && LooksLikeQualifiedWriterDocPath(docPathRaw))
            return $"path:{docPath}|p:{pageStart}{cardSuffix}";

        var sourceHash = NormalizeLexicalLookup(
            TryGetString(hit, "sourceHash")
            ?? TryGetString(hit, "source_hash")
            ?? TryGetString(hit, "SourceHash")
            ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceHash))
            return $"hash:{sourceHash}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(fileName))
            return $"file:{fileName}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(docName))
            return $"name:{docName}|p:{pageStart}{cardSuffix}";

        return $"unknown:{pageStart}{cardSuffix}";
    }

    private static string TryBuildRagWriterContentCardKey(JsonElement hit)
    {
        var cards = TryGetArray(hit, "matchedContentCards")
                    ?? TryGetArray(hit, "matched_content_cards")
                    ?? TryGetArray(hit, "MatchedContentCards")
                    ?? TryGetArray(hit, "contentCards")
                    ?? TryGetArray(hit, "content_cards")
                    ?? TryGetArray(hit, "ContentCards");
        if (!cards.HasValue || cards.Value.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var card in cards.Value.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var id = NormalizeLexicalLookup(
                TryGetString(card, "contentCardId")
                ?? TryGetString(card, "content_card_id")
                ?? TryGetString(card, "ContentCardId")
                ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            var title = NormalizeLexicalLookup(TryGetString(card, "title") ?? TryGetString(card, "Title") ?? string.Empty);
            var kind = NormalizeLexicalLookup(TryGetString(card, "kind") ?? TryGetString(card, "Kind") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(title))
                return string.IsNullOrWhiteSpace(kind) ? title : $"{kind}:{title}";
        }

        return string.Empty;
    }

    private static bool LooksLikeQualifiedWriterDocPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.Contains('/', StringComparison.Ordinal)
               || path.Contains('\\', StringComparison.Ordinal)
               || path.Contains(':', StringComparison.Ordinal));

    private static object? CompactRagMetaForWriter(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;

        var metaEl = TryGetObject(result, "meta")
                     ?? TryGetObject(result, "Meta");
        var metricsEl = TryGetObject(result, "metrics")
                        ?? TryGetObject(result, "Metrics")
                        ?? (metaEl.HasValue
                            ? TryGetObject(metaEl.Value, "metrics") ?? TryGetObject(metaEl.Value, "Metrics")
                            : null);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["queries"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "queries", "Queries", maxItems: 8, maxChars: 160) : null,
            ["mode"] = metaEl.HasValue ? TryGetString(metaEl.Value, "mode") ?? TryGetString(metaEl.Value, "Mode") : null,
            ["category"] = metaEl.HasValue ? TryGetString(metaEl.Value, "category") ?? TryGetString(metaEl.Value, "Category") : null,
            ["categoryPath"] = metaEl.HasValue ? TryGetString(metaEl.Value, "categoryPath") ?? TryGetString(metaEl.Value, "CategoryPath") : null,
            ["categoryInferred"] = metaEl.HasValue ? TryGetBool(metaEl.Value, "categoryInferred") ?? TryGetBool(metaEl.Value, "CategoryInferred") : null,
            ["fanoutParallelism"] = metaEl.HasValue ? TryGetInt(metaEl.Value, "fanoutParallelism") ?? TryGetInt(metaEl.Value, "FanoutParallelism") : null,
            ["busyQueries"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "busyQueries", "BusyQueries", maxItems: 8, maxChars: 160) : null,
            ["degradedRetrievers"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "degradedRetrievers", "DegradedRetrievers", maxItems: 8, maxChars: 80) : null,
            ["metrics"] = metricsEl.HasValue ? CompactRagMetricsForWriter(metricsEl.Value) : null,
            ["queryRuns"] = metaEl.HasValue ? CompactRagQueryRunsForWriter(metaEl.Value) : null
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                IReadOnlyCollection<string> list => list.Count > 0,
                IReadOnlyCollection<object> list => list.Count > 0,
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagMetricsForWriter(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tookMs"] = TryGetLong(metrics, "tookMs") ?? TryGetLong(metrics, "TookMs"),
            ["returned"] = TryGetInt(metrics, "returned") ?? TryGetInt(metrics, "Returned"),
            ["candidatesEvaluated"] = TryGetInt(metrics, "candidatesEvaluated") ?? TryGetInt(metrics, "CandidatesEvaluated"),
            ["retrieversUsed"] = CompactStringArray(metrics, "retrieversUsed", "RetrieversUsed", maxItems: 8, maxChars: 80),
            ["degradedRetrievers"] = CompactStringArray(metrics, "degradedRetrievers", "DegradedRetrievers", maxItems: 8, maxChars: 80)
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                IReadOnlyCollection<string> list => list.Count > 0,
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagQueryRunsForWriter(JsonElement meta)
    {
        var runs = TryGetArray(meta, "queryRuns")
                   ?? TryGetArray(meta, "QueryRuns")
                   ?? TryGetArray(meta, "query_runs");
        if (!runs.HasValue || runs.Value.ValueKind != JsonValueKind.Array)
            return null;

        var compact = new List<object>();
        foreach (var run in runs.Value.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object)
                continue;

            var runMeta = TryGetObject(run, "meta") ?? TryGetObject(run, "Meta");
            var hits = TryGetArray(run, "hits") ?? TryGetArray(run, "Hits");
            var hitCount = TryGetInt(run, "hitCount") ?? TryGetInt(run, "HitCount");
            var metrics = runMeta.HasValue
                ? TryGetObject(runMeta.Value, "metrics") ?? TryGetObject(runMeta.Value, "Metrics")
                : null;
            compact.Add(new
            {
                query = TruncateForPrompt(TryGetString(run, "query") ?? TryGetString(run, "Query"), 160),
                hitCount = hitCount ?? (hits.HasValue && hits.Value.ValueKind == JsonValueKind.Array ? hits.Value.GetArrayLength() : (int?)null),
                error = TryGetString(run, "error") ?? TryGetString(run, "Error"),
                busy = TryGetBool(run, "busy") ?? TryGetBool(run, "Busy"),
                metrics = metrics.HasValue ? CompactRagMetricsForWriter(metrics.Value) : null
            });

            if (compact.Count >= 8)
                break;
        }

        return compact.Count == 0 ? null : compact;
    }

    private static string[] CompactStringArray(JsonElement item, string primaryName, string secondaryName, int maxItems, int maxChars)
    {
        var values = TryGetArray(item, primaryName)
                     ?? TryGetArray(item, secondaryName);
        if (!values.HasValue || values.Value.ValueKind != JsonValueKind.Array)
            return [];

        return values.Value.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TruncateForPrompt(value, maxChars))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 32))
            .ToArray();
    }

    private static object? CompactRagProvenanceForPrompt(JsonElement item)
    {
        var provenance = TryGetObject(item, "provenanceInfo")
                         ?? TryGetObject(item, "ProvenanceInfo")
                         ?? TryGetObject(item, "provenance_info");
        if (!provenance.HasValue || provenance.Value.ValueKind != JsonValueKind.Object)
            return null;

        var value = provenance.Value;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channel"] = TryGetString(value, "channel") ?? TryGetString(value, "Channel"),
            ["label"] = TruncateForPrompt(TryGetString(value, "label") ?? TryGetString(value, "Label"), 160),
            ["chunkId"] = TryGetString(value, "chunkId") ?? TryGetString(value, "ChunkId"),
            ["pageStart"] = TryGetInt(value, "pageStart") ?? TryGetInt(value, "PageStart"),
            ["pageEnd"] = TryGetInt(value, "pageEnd") ?? TryGetInt(value, "PageEnd"),
            ["offsetStart"] = TryGetInt(value, "offsetStart") ?? TryGetInt(value, "OffsetStart"),
            ["offsetEnd"] = TryGetInt(value, "offsetEnd") ?? TryGetInt(value, "OffsetEnd")
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagContextForPrompt(JsonElement item)
    {
        var context = TryGetObject(item, "context")
                      ?? TryGetObject(item, "Context");
        if (!context.HasValue || context.Value.ValueKind != JsonValueKind.Object)
            return null;

        var value = context.Value;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sectionTitle"] = TruncateForPrompt(TryGetString(value, "sectionTitle") ?? TryGetString(value, "SectionTitle"), 160),
            ["headingPath"] = TruncateForPrompt(TryGetString(value, "headingPath") ?? TryGetString(value, "HeadingPath"), 220),
            ["contentRole"] = TryGetString(value, "contentRole") ?? TryGetString(value, "ContentRole"),
            ["navigationReason"] = TruncateForPrompt(TryGetString(value, "navigationReason") ?? TryGetString(value, "NavigationReason"), 120),
            ["navigationScore"] = TryGetDouble(value, "navigationScore") ?? TryGetDouble(value, "NavigationScore"),
            ["contentDensityScore"] = TryGetDouble(value, "contentDensityScore") ?? TryGetDouble(value, "ContentDensityScore"),
            ["prevChunkId"] = TryGetString(value, "prevChunkId") ?? TryGetString(value, "PrevChunkId"),
            ["nextChunkId"] = TryGetString(value, "nextChunkId") ?? TryGetString(value, "NextChunkId"),
            ["sameSectionChunkId"] = TryGetString(value, "sameSectionChunkId") ?? TryGetString(value, "SameSectionChunkId")
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static bool IsLlmContextOverflowException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            var message = current.Message ?? string.Empty;
            if (message.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("context size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("n_ctx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ResolveBroadWriterContentCardLimit(string? userMessage, bool keepBroadCardEvidence)
    {
        if (!keepBroadCardEvidence)
            return 2;

        if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
            return 4;

        if (LooksLikeBroadSourceBackedCompositionRequest(userMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
            || LooksLikeSoftChoiceRecommendationRequest(userMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage))
        {
            return 3;
        }

        return 2;
    }

    private static bool ShouldKeepBroadWriterCardEvidence(RagHitSummary hit, int selectedIndex, string? userMessage)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        var indexLimit = LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            ? 8
            : LooksLikeBroadSourceBackedCompositionRequest(userMessage)
              || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
              || LooksLikeSoftChoiceRecommendationRequest(userMessage)
              || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage)
                ? 5
                : 2;
        if (selectedIndex >= indexLimit)
            return false;

        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role is not ("actionable_item" or "supporting_context" or "advisory")
            && !HasRichSourceBackedEvidence(hit))
        {
            return false;
        }

        return hit.MatchedContentCards.Any(static card =>
            card.RawEvidence.HasValue
            || card.Evidence is { QuantityFacts.Count: > 0 }
            || card.Evidence?.Facts is { Count: > 0 });
    }

    private static IReadOnlyList<JsonElement> PreserveComparativeEntityCoverageForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> sourceHits,
        string userMessage,
        int maxHits)
    {
        var entityAnchors = ExtractComparativeEntityAnchorTerms(userMessage);
        if (maxHits <= 0 || entityAnchors.Length < 2 || sourceHits.Count == 0)
            return rankedHits;

        var requiredCoverage = Math.Min(entityAnchors.Length, maxHits);
        var currentCoverage = CountComparativeEntityCoverage(
            rankedHits.Take(maxHits).Select(BuildRagHitSummary),
            entityAnchors);
        if (currentCoverage >= requiredCoverage)
            return rankedHits;

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        var candidates = sourceHits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit)
            })
            .Select(item => new
            {
                item.Hit,
                item.Index,
                item.Summary,
                EntityMatches = GetMatchedComparativeEntityAnchorIndexes(item.Summary, entityAnchors),
                Score = ComputeComparativeDocumentaryEvidenceScore(item.Summary, evidenceQuery, focusTerms)
            })
            .Where(item => item.EntityMatches.Length > 0)
            .Select(item => new
            {
                item.Hit,
                Scored = new ComparativeScoredHit(
                    item.Summary,
                    item.Index,
                    item.EntityMatches,
                    item.Score + (item.EntityMatches.Length * 10.0))
            })
            .ToList();
        if (candidates.Count == 0)
            return rankedHits;

        var coverageHits = SelectComparativeEntityCoverageHits(
            candidates.Select(static item => item.Scored).ToList(),
            entityAnchors.Length,
            requiredCoverage);
        if (CountComparativeEntityCoverage(coverageHits, entityAnchors) <= currentCoverage)
            return rankedHits;

        var candidateByKey = candidates
            .GroupBy(static item => BuildRagHitIdentityKey(item.Scored.Hit), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Hit, StringComparer.OrdinalIgnoreCase);
        var coverageKeys = coverageHits
            .Select(BuildRagHitIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return coverageHits
            .Select(hit => candidateByKey.TryGetValue(BuildRagHitIdentityKey(hit), out var sourceHit) ? sourceHit : default)
            .Where(static hit => hit.ValueKind != JsonValueKind.Undefined)
            .Concat(rankedHits.Where(hit => !coverageKeys.Contains(BuildRagHitIdentityKey(BuildRagHitSummary(hit)))))
            .ToList();
    }

    private static IReadOnlyList<JsonElement> PreservePrimaryQueryTopHitsForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> sourceHits,
        string userMessage,
        int maxHits)
    {
        if (maxHits <= 1 || sourceHits.Count == 0)
            return rankedHits;

        if (LooksLikeShortTechnicalEvidenceTopic(userMessage)
            && rankedHits.Any(hit => HasShortTechnicalPhraseEvidence(userMessage, BuildRagHitSummary(hit))))
        {
            return rankedHits;
        }

        var primaryTopHits = sourceHits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit),
                QueryIndex = TryGetInt(hit, "retrievalQueryIndex") ?? TryGetInt(hit, "retrieval_query_index") ?? TryGetInt(hit, "RetrievalQueryIndex"),
                HitRank = TryGetInt(hit, "retrievalHitRank") ?? TryGetInt(hit, "retrieval_hit_rank") ?? TryGetInt(hit, "RetrievalHitRank")
            })
            .Where(static item => (item.Summary.RetrievalQueryIndex ?? item.QueryIndex) == 0)
            .Where(static item => (item.Summary.RetrievalHitRank ?? item.HitRank) is >= 0 and <= 1)
            .Where(static item => IsUsablePrimaryQueryTopWriterHit(item.Summary))
            .OrderBy(static item => item.Summary.RetrievalHitRank ?? item.HitRank ?? int.MaxValue)
            .ThenByDescending(static item => ComputeBackendSelectionPriority(item.Summary))
            .ThenByDescending(static item => item.Summary.Score)
            .ThenBy(static item => item.Index)
            .Take(Math.Min(2, maxHits))
            .ToList();
        if (primaryTopHits.Count == 0)
            return rankedHits;

        var primaryKeys = primaryTopHits
            .Select(static item => GetWriterRagHitIdentityKey(item.Summary))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return primaryTopHits
            .Select(static item => item.Hit)
            .Concat(rankedHits.Where(hit => !primaryKeys.Contains(GetWriterRagHitIdentityKey(BuildRagHitSummary(hit)))))
            .ToList();
    }

    private sealed record WriterRagHitCandidate(JsonElement Hit, int Index, RagHitSummary Summary);

    private static string GetWriterRagHitDocumentKey(RagHitSummary hit)
        => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath;

    private static string GetWriterRagHitIdentityKey(RagHitSummary hit)
    {
        var docKey = GetWriterRagHitDocumentKey(hit);
        if (!string.IsNullOrWhiteSpace(hit.ChunkId))
            return $"chunk|{docKey}|{hit.ChunkId}";

        var contentCardKey = hit.MatchedContentCards?
            .Select(static card => NormalizeLexicalLookup(
                card.ContentCardId
                ?? (string.IsNullOrWhiteSpace(card.Kind) ? card.Title : $"{card.Kind}:{card.Title}")
                ?? string.Empty))
            .FirstOrDefault(static key => !string.IsNullOrWhiteSpace(key));
        if (!string.IsNullOrWhiteSpace(contentCardKey))
            return $"card|{docKey}|{hit.PageStart}|{hit.PageEnd}|{contentCardKey}";

        return $"range|{docKey}|{hit.PageStart}|{hit.PageEnd}|{hit.OffsetStart}|{hit.OffsetEnd}";
    }

    private static bool IsUsableBackendTopDocumentWriterHit(RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) || LooksLikeLowSignalContentCandidateHit(hit))
            return false;

        var hasEnoughText = CollapseWhitespace($"{GetRagHitPrimaryEvidenceText(hit)} {GetRagHitLookupText(hit)}").Length >= 80;
        var hasConcreteContentCard = hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true;
        return hasEnoughText
               || hasConcreteContentCard
               || (hit.ProfileSignals is not null && HasConcretePageGroundedEvidence(hit));
    }

    private static bool IsUsablePrimaryQueryTopWriterHit(RagHitSummary hit)
    {
        var hasConcreteContentCard = hit.MatchedContentCards?.Any(IsConcretePrimaryQueryTopWriterCard) == true;
        var hasEnoughText = CollapseWhitespace(GetRagHitPrimaryEvidenceText(hit)).Length >= 80;

        if (LooksLikeNavigationOnlyHit(hit)
            && !hasConcreteContentCard)
        {
            return false;
        }

        if (LooksLikeLowSignalContentCandidateHit(hit)
            && !hasConcreteContentCard)
        {
            return false;
        }

        return hasEnoughText
               || hasConcreteContentCard
               || (hit.ProfileSignals is not null && HasConcretePageGroundedEvidence(hit));
    }

    private static bool IsConcretePrimaryQueryTopWriterCard(RagHitContentCardSummary card)
    {
        var title = CleanSourceBackedOptionTitle(card.Title);
        if (string.IsNullOrWhiteSpace(title)
            || !IsUsefulSourceBackedDisplayTitle(title)
            || LooksLikePlanItemNoise(title)
            || LooksLikeProcedureSentenceTitle(NormalizeLexicalLookup(title)))
        {
            return false;
        }

        var normalizedKind = NormalizeLexicalLookup(card.Kind);
        if (Regex.IsMatch(
                normalizedKind,
                @"\b(?:navigation|toc|sommaire|contents|index|catalog|catalogue|section|category|categorie|chapter|heading)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var normalizedTitle = NormalizeLexicalLookup(title);
        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:index|sommaire|contents|table\s+of\s+contents|table\s+des\s+matieres|catalog|catalogue|category|categories|section|sections|chapter|chapters|liste|list)$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (card.RawEvidence.HasValue
            || card.Evidence?.QuantityFacts is { Count: > 0 }
            || card.Evidence?.Facts is { Count: > 0 })
        {
            return true;
        }

        if (normalizedKind.Contains("unit", StringComparison.Ordinal)
            || normalizedKind.Contains("exact", StringComparison.Ordinal)
            || normalizedKind.Contains("lead", StringComparison.Ordinal))
        {
            return card.PageStart.HasValue
                   || !string.IsNullOrWhiteSpace(card.ContentCardId)
                   || ExtractQuerySignalTerms(normalizedTitle).Any();
        }

        var titleTermCount = ExtractQuerySignalTerms(normalizedTitle).Count();
        return titleTermCount >= 2
               && (card.PageStart.HasValue
                   || !string.IsNullOrWhiteSpace(card.ContentCardId)
                   || card.Signals is { Count: >= 2 });
    }

    private static IReadOnlyList<JsonElement> PinBackendTopDocumentFirstForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> originalHits,
        string userMessage)
    {
        if (rankedHits.Count <= 1 || originalHits.Count == 0 || !LooksLikeComparativeDocumentaryRequest(userMessage))
            return rankedHits;

        var originalCandidates = originalHits
            .Select((hit, index) => new WriterRagHitCandidate(hit, index, BuildRagHitSummary(hit)))
            .ToList();
        if (originalCandidates.Count == 0)
            return rankedHits;

        var backendTopDocumentKey = GetWriterRagHitDocumentKey(originalCandidates[0].Summary);
        if (string.IsNullOrWhiteSpace(backendTopDocumentKey))
            return rankedHits;

        var pinnedFromRanked = rankedHits
            .Select((hit, index) => new WriterRagHitCandidate(hit, index, BuildRagHitSummary(hit)))
            .FirstOrDefault(item =>
                string.Equals(GetWriterRagHitDocumentKey(item.Summary), backendTopDocumentKey, StringComparison.OrdinalIgnoreCase)
                && IsUsableBackendTopDocumentWriterHit(item.Summary));

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        var pinned = pinnedFromRanked
                     ?? originalCandidates
                         .Where(item =>
                             string.Equals(GetWriterRagHitDocumentKey(item.Summary), backendTopDocumentKey, StringComparison.OrdinalIgnoreCase)
                             && IsUsableBackendTopDocumentWriterHit(item.Summary))
                         .OrderByDescending(item => ComputeBackendSelectionPriority(item.Summary))
                         .ThenByDescending(item => ComputeComparativeDocumentaryEvidenceScore(item.Summary, evidenceQuery, focusTerms))
                         .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(item.Summary)))
                         .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(item.Summary)))
                         .ThenByDescending(item => item.Summary.Score)
                         .ThenBy(item => item.Index)
                         .FirstOrDefault();
        if (pinned is null)
            return rankedHits;

        var pinnedKey = GetWriterRagHitIdentityKey(pinned.Summary);
        return new[] { pinned.Hit }
            .Concat(rankedHits.Where(hit => !string.Equals(GetWriterRagHitIdentityKey(BuildRagHitSummary(hit)), pinnedKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IReadOnlyList<JsonElement> RankRagHitsForWriter(IReadOnlyList<JsonElement> hits, string userMessage)
    {
        if (hits.Count <= 1)
            return hits;

        hits = hits
            .Where(static hit => !LooksLikeNavigationOnlyHit(BuildRagHitSummary(hit)))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(BuildRagHitSummary(hit)))
            .ToList();
        if (hits.Count <= 1)
            return hits;

        if (LooksLikeComparativeDocumentaryRequest(userMessage))
        {
            var summaries = hits
                .Select((hit, index) => new
                {
                    Hit = hit,
                    Index = index,
                    Summary = BuildRagHitSummary(hit)
                })
                .ToList();
            var selected = SelectComparativeDocumentaryHits(
                    summaries.Select(static item => item.Summary),
                    userMessage,
                    RagWriterMaxHits)
                .ToList();
            if (selected.Count > 0)
            {
                var selectedRanks = selected
                    .Select((hit, rank) => new { Key = GetWriterRagHitIdentityKey(hit), Rank = rank })
                    .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static group => group.Key, static group => group.First().Rank, StringComparer.OrdinalIgnoreCase);
                var ranked = summaries
                    .Where(item => selectedRanks.ContainsKey(GetWriterRagHitIdentityKey(item.Summary)))
                    .OrderBy(item => selectedRanks[GetWriterRagHitIdentityKey(item.Summary)])
                    .Concat(summaries.Where(item => !selectedRanks.ContainsKey(GetWriterRagHitIdentityKey(item.Summary))))
                    .Take(RagWriterMaxHits)
                    .Select(static item => item.Hit)
                    .ToList();
                return PinBackendTopDocumentFirstForWriter(ranked, hits, userMessage)
                    .Take(RagWriterMaxHits)
                    .ToList();
            }
        }

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var isShortTechnicalEvidenceTopic = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery);
        var requestedTitle = isShortTechnicalEvidenceTopic
            ? null
            : TryExtractRequestedItemTitle(userMessage);
        evidenceQuery = !string.IsNullOrWhiteSpace(requestedTitle)
            ? requestedTitle!
            : evidenceQuery;

        if (string.IsNullOrWhiteSpace(evidenceQuery))
            return hits;

        var broadAnchorTerms = ExtractBroadCompositionAnchorTerms(userMessage);
        var evidenceCandidates = hits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit)
            })
            .Select(item => new
            {
                item.Hit,
                item.Index,
                item.Summary,
                DocumentTypeScore = ComputeRequestedDocumentTypeAnchorScore(userMessage, item.Summary),
                AnchorMatchCount = broadAnchorTerms.Length == 0
                    ? 0
                    : broadAnchorTerms.Count(term => NormalizeLexicalLookup(GetRagHitLookupText(item.Summary)).Contains(term, StringComparison.Ordinal))
            })
            .ToList();
        var documentTypeCandidates = evidenceCandidates
            .Where(static item => item.DocumentTypeScore > 0)
            .ToList();
        if (documentTypeCandidates.Count > 0)
            evidenceCandidates = documentTypeCandidates;

        var rankedByEvidence = evidenceCandidates
            .OrderBy(item => LooksLikeNavigationOnlyHit(item.Summary) ? 1 : 0)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(requestedTitle) ? ComputeExactItemAnchorStrengthScore(requestedTitle!, item.Summary) : 0)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(requestedTitle) && RagHitContainsRequestedTitle(item.Summary, requestedTitle!) ? 1 : 0)
            .ThenByDescending(item => item.DocumentTypeScore)
            .ThenByDescending(item => isShortTechnicalEvidenceTopic ? ComputeDirectTechnicalEvidenceScore(evidenceQuery, item.Summary) : 0)
            .ThenByDescending(item => ComputeBackendSelectionPriority(item.Summary))
            .ThenByDescending(item => item.AnchorMatchCount)
            .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(item.Summary)))
            .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(item.Summary)))
            .ThenByDescending(item => item.Summary.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Hit)
            .ToList();
        return PinBackendTopDocumentFirstForWriter(rankedByEvidence, hits, userMessage);
    }

    private static bool IsInventoryIntent(string? intent)
    {
        var normalized = NormalizeRouterIntent(intent);
        return normalized.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInventoryLikeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName is
            "inventory.rendered"
            or "documents.list"
            or "documents.search"
            or "documents.tree"
            or "documents.categories"
            or "documents.stats"
            or "documents.count"
            or "documents.empty_count"
            or "documents.empty_list"
            or "documents.extraction_quality"
            or "documents.extraction_pages"
            or "summary.status.count"
            or "summary.status.list"
            or "admin.summary.missing"
            or "summary.present.count"
            or "summary.present.list"
            or "diagnostic.performance";
    }

    private static bool HasOnlyInventoryTools(ToolResults toolResults)
    {
        var successful = toolResults.Items.Where(x => string.IsNullOrWhiteSpace(x.Error)).ToList();
        if (successful.Count == 0)
            return false;

        return successful.All(x => IsInventoryLikeToolName(x.ToolName));
    }

    private static string? TryRenderInventoryFallbackText(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var kind = item.Result.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
            ? (kindEl.GetString() ?? string.Empty)
            : string.Empty;

        if (!item.Result.TryGetProperty("data", out var data))
            return null;

        var fallback = RenderDeterministicInventoryFromData(kind, data, language);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private static string? TryExtractInventoryRenderedDataJson(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.Result.TryGetProperty("data", out var data))
            return null;

        return data.GetRawText();
    }

    internal static JsonElement ApplySpecificDocumentQueryGuard(JsonElement rawData, string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (!LooksLikeSpecificDocumentReferenceQuery(query))
            return rawData;

        if (!rawData.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return rawData;

        var filtered = new List<JsonElement>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docName = TryGetString(entry, "docName");
            var docPath = TryGetString(entry, "docPath");
            if (MatchesSpecificDocumentReferenceQuery(query, docName, docPath))
                filtered.Add(entry.Clone());
        }

        var normalized = new
        {
            scopePath = TryGetString(rawData, "scopePath"),
            searchQuery = query,
            limit = TryGetInt(rawData, "limit") ?? filtered.Count,
            offset = TryGetInt(rawData, "offset") ?? 0,
            total = filtered.Count,
            endOfList = true,
            dropped = TryGetInt(rawData, "dropped") ?? 0,
            items = filtered
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
        return doc.RootElement.Clone();
    }

    internal static bool LooksLikeSpecificDocumentReferenceQuery(string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return Regex.IsMatch(query, @"(?i)\.pdf\b")
            || query.Contains('/')
            || query.Contains('\\')
            || Regex.IsMatch(query, @"(?i)\bPDF\s*0*\d{1,4}\b");
    }

    internal static bool MatchesSpecificDocumentReferenceQuery(string searchQuery, string? docName, string? docPath)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalizedQueryPath = query.Replace('\\', '/').Trim().Trim('/');
        var normalizedQueryText = NormalizeDocumentLookupText(query);
        var normalizedQueryCompact = NormalizeDocumentLookupCompact(query);
        var queryFileName = Path.GetFileName(normalizedQueryPath);
        var queryFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedQueryPath);
        var normalizedQueryFileName = NormalizeDocumentLookupText(queryFileName);
        var normalizedQueryFileNameWithoutExtension = NormalizeDocumentLookupText(queryFileNameWithoutExtension);

        IEnumerable<string> EnumerateCandidates()
        {
            if (!string.IsNullOrWhiteSpace(docName))
                yield return docName!;
            if (!string.IsNullOrWhiteSpace(docPath))
            {
                yield return docPath!;
                var fileName = Path.GetFileName(docPath!);
                if (!string.IsNullOrWhiteSpace(fileName))
                    yield return fileName;
            }
        }

        foreach (var raw in EnumerateCandidates().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = raw.Trim();
            var normalizedCandidatePath = candidate.Replace('\\', '/').Trim().Trim('/');
            var normalizedCandidateText = NormalizeDocumentLookupText(candidate);
            var normalizedCandidateCompact = NormalizeDocumentLookupCompact(candidate);
            var candidateFileName = Path.GetFileName(normalizedCandidatePath);
            var candidateFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedCandidatePath);
            var normalizedCandidateFileName = NormalizeDocumentLookupText(candidateFileName);
            var normalizedCandidateFileNameWithoutExtension = NormalizeDocumentLookupText(candidateFileNameWithoutExtension);

            if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedCandidatePath, normalizedQueryPath, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(normalizedQueryCompact) && string.Equals(normalizedCandidateCompact, normalizedQueryCompact, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryText) && string.Equals(normalizedCandidateText, normalizedQueryText, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileName) && string.Equals(normalizedCandidateFileName, normalizedQueryFileName, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileNameWithoutExtension) && string.Equals(normalizedCandidateFileNameWithoutExtension, normalizedQueryFileNameWithoutExtension, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string SerializeToolResults(ToolResults tr)
    {
        var o = tr.Items.Select(x => new
        {
            tool = x.ToolName,
            error = x.Error,
            durationMs = x.DurationMs,
            result = x.Result
        }).ToList();

        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string SerializeTail(IReadOnlyList<(string role, string content)> hist, int maxTurns)
    {
        var tail = hist
            .TakeLast(maxTurns)
            .Select(m => new
            {
                role = m.role,
                content = TruncateForPrompt(m.content, SerializedTailContentMaxChars)
            })
            .ToList();
        return JsonSerializer.Serialize(tail);
    }

    private static string TruncateForPrompt(string? value, int maxChars)
    {
        var s = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (maxChars <= 0 || s.Length <= maxChars)
            return s;

        return s[..maxChars].TrimEnd() + "...";
    }

    private static bool TryExtractJsonObject(string raw, out string json)
    {
        json = (raw ?? "").Trim();

        // enlever ```json ... ``` si présent
        if (json.StartsWith("```"))
        {
            var i = json.IndexOf('\n');
            if (i >= 0) json = json.Substring(i + 1);
            json = json.Replace("```", "").Trim();
        }

        // trouver premier '{' et dernier '}'
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return false;

        json = json.Substring(start, end - start + 1);
        return true;
    }

    private static bool TryRepairJsonObjectForParsing(string json, out string repaired)
    {
        repaired = (json ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repaired))
            return false;

        if (CanParseJsonObject(repaired))
            return true;

        var candidates = new[]
            {
                RepairJsonDelimiters(repaired, closeBeforeMismatchedCloser: false),
                RepairJsonDelimiters(repaired, closeBeforeMismatchedCloser: true)
            }
            .Select(RemoveTrailingCommasBeforeJsonClosers)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, repaired, StringComparison.Ordinal))
                continue;

            if (CanParseJsonObject(candidate))
            {
                repaired = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool CanParseJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static string RepairJsonDelimiters(string json, bool closeBeforeMismatchedCloser)
    {
        var sb = new StringBuilder(json.Length + 8);
        var expectedClosers = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var ch in json)
        {
            if (inString)
            {
                sb.Append(ch);
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
                continue;
            }

            if (ch == '{')
            {
                expectedClosers.Push('}');
                sb.Append(ch);
                continue;
            }

            if (ch == '[')
            {
                expectedClosers.Push(']');
                sb.Append(ch);
                continue;
            }

            if (ch is '}' or ']')
            {
                if (expectedClosers.Count == 0)
                    continue;

                if (expectedClosers.Peek() == ch)
                {
                    expectedClosers.Pop();
                    sb.Append(ch);
                    continue;
                }

                if (closeBeforeMismatchedCloser)
                {
                    while (expectedClosers.Count > 0 && expectedClosers.Peek() != ch)
                        sb.Append(expectedClosers.Pop());

                    if (expectedClosers.Count > 0 && expectedClosers.Peek() == ch)
                    {
                        expectedClosers.Pop();
                        sb.Append(ch);
                    }
                }

                continue;
            }

            sb.Append(ch);
        }

        while (expectedClosers.Count > 0)
            sb.Append(expectedClosers.Pop());

        return sb.ToString();
    }

    private static string RemoveTrailingCommasBeforeJsonClosers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        foreach (var ch in json)
        {
            if (inString)
            {
                sb.Append(ch);
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
                continue;
            }

            if (ch is '}' or ']')
            {
                var i = sb.Length - 1;
                while (i >= 0 && char.IsWhiteSpace(sb[i]))
                    i--;
                if (i >= 0 && sb[i] == ',')
                    sb.Remove(i, 1);
            }

            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private static bool TryExtractFinalAnswerFromRaw(string raw, out string finalAnswer)
    {
        finalAnswer = "";
        raw ??= "";

        // 1) Try to parse a JSON substring first
        if (TryExtractJsonObject(raw, out var json) && TryParseAnswerEnvelope(json, out var fa, out _))
        {
            finalAnswer = (fa ?? "").Trim();
            return !string.IsNullOrWhiteSpace(finalAnswer);
        }

        // 2) Regex fallback: "finalAnswer":"..."
        var m = Regex.Match(raw, "\\\"finalAnswer\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Singleline);
        if (m.Success)
        {
            var v = m.Groups["v"].Value;
            try
            {
                // Use JSON parser to unescape
                finalAnswer = JsonSerializer.Deserialize<string>("\"" + v + "\"") ?? "";
                finalAnswer = finalAnswer.Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
            catch
            {
                // best-effort
                finalAnswer = v.Replace("\\n", "\n").Replace("\\t", "\t").Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
        }

        return false;
    }

    private static string BuildJsonEnvelopeError(string? lang)
    {
        return DeterministicAgentText.JsonEnvelopeError(lang);
    }

    private static string PhaseLabelForTool(string toolName, string language)
        => DeterministicAgentText.ToolPhase(toolName, language);
}
