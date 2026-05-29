using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Linq;
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
    private const int RagWriterBroadExcerptChars = 220;
    private const int RagWriterBroadFullTextChars = 240;
    private const int RagWriterBroadContextualChars = 320;
    private const int RagWriterMaxContentCards = 4;
    private const int RagWriterMaxCardQuantityFacts = 4;
    private const int RagWriterMaxCardFacts = 6;
    private const int RagWriterMaxCardEvidenceTextChars = 120;
    private const int SourceBackedEvidenceMaxChars = 620;
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
        try
        {
            return await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            return await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> StreamOrCompleteWithRetryAsync(
        IReadOnlyList<(string role, string content)> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        if (onDelta is null)
            return await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

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
            throw;
        }
        catch
        {
            if (streamed.Length == 0)
                return await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
        }

        var finalAnswer = streamed.ToString();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

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

        var swRouter = Stopwatch.StartNew();
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
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

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
            plan.ToolCalls = plan.ToolCalls.Where(c => !string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)).ToList();

        ApplyDocumentaryRagDefaults(plan, effectiveUserMessage);

        var swTools = Stopwatch.StartNew();
        var toolResults = await ExecuteToolsAsync(plan, effectiveUserMessage, ct, onPhase, onProgress).ConfigureAwait(false);
        swTools.Stop();
        _lastToolsMs = swTools.ElapsedMilliseconds;
        _lastToolDurations = toolResults.Items.Select(x => (x.ToolName, x.DurationMs, string.IsNullOrWhiteSpace(x.Error))).ToList();
        _mem.LastToolNames = toolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (localItems.Count > 0)
            toolResults.Items.InsertRange(0, localItems);

        CaptureStructuredConversationState(plan, toolResults);

        var inventoryRendered = TryBuildInventoryRenderedItem(toolResults, plan.Language, ct);
        _lastUsedInventoryRendered = inventoryRendered is not null;
        if (inventoryRendered is not null)
        {
            toolResults.Items.Add(inventoryRendered);
        }

        var deterministicToolFailure = TryBuildToolFailureAnswer(plan, toolResults, plan.Language);
        if (!string.IsNullOrWhiteSpace(deterministicToolFailure))
        {
            await EmitDeterministicTextAsync(deterministicToolFailure, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
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
        var preWriterShouldUseBroadSynthesis =
            ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, effectiveUserMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage);
        var preWriterSourceBackedAnswer = string.Empty;
        List<ToolMemory.SourceRef>? preWriterSourceBackedSources = null;
        if (LooksLikeCategoryOverviewOrDocumentOrientationRequest(effectiveUserMessage))
        {
            preWriterSourceBackedAnswer = BuildCategoryOverviewAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromRagHits(toolResults).Take(6).ToList();
        }
        else if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
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
                && !ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language))
            {
                preWriterSourceBackedAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                preWriterSourceBackedSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
                if (preWriterSourceBackedSources.Count == 0)
                    preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }
        }
        else if (ShouldUseSourceBackedOptionAnswer(preWriterExactItemTitle, effectiveUserMessage)
            && !preWriterShouldUseBroadSynthesis)
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
            && (LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults)))
        {
            preWriterSourceBackedAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
        }

        if (string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer)
            && !preWriterShouldUseBroadSynthesis
            && !ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language)
            && LooksLikeSourceBackedActionRequest(effectiveUserMessage))
        {
            preWriterSourceBackedAnswer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
            preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            if (preWriterSourceBackedSources.Count == 0)
                preWriterSourceBackedSources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
        }

        if (!string.IsNullOrWhiteSpace(preWriterSourceBackedAnswer))
        {
            object? preWriterSourcesPayload = null;
            if (LooksLikePoorPlanningFallbackAnswer(preWriterSourceBackedAnswer, effectiveUserMessage))
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
                    preWriterSourceBackedSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
                    if (preWriterSourceBackedSources.Count == 0)
                        preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
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
                        preWriterSourceBackedSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                    }
                }
            }

            if (LooksLikeMissingExactItemWithoutSourceLeads(preWriterSourceBackedAnswer))
            {
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
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                preWriterSourceBackedAnswer,
                preWriterSourcesPayload,
                plan.Intent,
                _mem.LastToolNames,
                _mem.LastReasoningTracePublic);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));
        var swWriter = Stopwatch.StartNew();
        var (answer, sources) = await AnswerAsync(chatHistory, effectiveUserMessage, plan, toolResults, ct, onDelta, onProgress).ConfigureAwait(false);
        swWriter.Stop();
        _lastWriterMs = swWriter.ElapsedMilliseconds;

        answer = (answer ?? string.Empty).Trim();
        if (toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search")
            && !_lastAnswerSource.StartsWith("backend_guidance_ask_clarification:", StringComparison.Ordinal)
            && !LooksLikeMissingExactItemWithoutSourceLeads(answer)
            && (sources is null || sources.Count == 0 || ShouldFallbackFromNoRagDataAnswer(answer)))
        {
            var repairedSources = (LooksLikeSourceBackedActionRequest(effectiveUserMessage)
                    || LooksLikeComparativeDocumentaryRequest(effectiveUserMessage)
                    || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults))
                ? DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage)
                : DeriveSourcesFromRankedRagHits(toolResults, effectiveUserMessage);
            if (repairedSources.Count == 0 && LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
                repairedSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
            if (repairedSources.Count == 0)
                repairedSources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

            if (repairedSources.Count > 0)
            {
                sources = repairedSources;
                if (ShouldFallbackFromNoRagDataAnswer(answer))
                    answer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
            }
        }

        if (LooksLikeMissingExactItemWithoutSourceLeads(answer))
        {
            sources?.Clear();
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        }

        if (sources is { Count: > 0 })
            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);

        if (sources is { Count: > 0 })
            answer = InjectInlineSources(answer, sources, plan.Language);

        object? sourcesPayload = null;
        if (sources is { Count: > 0 })
            sourcesPayload = BuildSourcesPayload(plan.Intent, sources);

        _lastAnswerSource = _lastUsedInventoryRendered
            ? $"router+tools+inventory_bypass:{plan.Intent}"
            : $"router+tools+writer:{plan.Intent}";
        onProgress?.Invoke(string.Empty);
        return FinalizeAndReturn(swTotalPipeline, userMessage, answer, sourcesPayload, plan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
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

        if (ShouldOfferBroadenedSourceSearch(query))
        {
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
        var currentAnalysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, effectiveUserMessage, plan.Language);
        if (!currentAnalysis.ShouldExplore)
            return false;

        var acceptedAny = false;
        var categoryScope = string.IsNullOrWhiteSpace(categoryScopeOverride)
            ? ResolveRagCategoryScope(effectiveUserMessage)
            : categoryScopeOverride;
        var remainingRagCalls = Math.Max(0, MaxRagToolCalls - CountRagRetrievalToolCalls(toolResults));

        void RememberExplorationPass(
            SourceBackedEvidenceExplorationPass pass,
            SourceBackedEvidenceSufficiency before,
            SourceBackedEvidenceSufficiency? after,
            long elapsedMs,
            bool accepted,
            string? rejectReason)
        {
            var traces = _mem.Execution.LastRagEvidenceExploration;
            if (traces.Count >= 16)
                traces.RemoveAt(0);

            traces.Add(new ToolMemory.RagEvidenceExplorationTrace
            {
                Label = TruncateForPrompt(pass.Label, 80),
                Queries = pass.Queries
                    .Where(static query => !string.IsNullOrWhiteSpace(query))
                    .Select(query => TruncateForPrompt(query, 180))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList(),
                KindBefore = before.Kind,
                ReasonBefore = before.Reason,
                ScoreBefore = before.Score,
                UsableHitsBefore = before.UsableHitCount,
                CandidateCountBefore = before.CandidateCount,
                DistinctDocumentsBefore = before.DistinctDocumentCount,
                DistinctPagesBefore = before.DistinctSourcePageCount,
                MinimumCandidates = before.MinimumCandidateCount,
                TargetSlots = before.TargetSlotCount,
                ElapsedMs = Math.Max(0, elapsedMs),
                Accepted = accepted,
                RejectReason = rejectReason,
                ReasonAfter = after?.Reason,
                ScoreAfter = after?.Score,
                UsableHitsAfter = after?.UsableHitCount,
                CandidateCountAfter = after?.CandidateCount,
                DistinctDocumentsAfter = after?.DistinctDocumentCount,
                DistinctPagesAfter = after?.DistinctSourcePageCount
            });
        }

        async Task<bool> TryExecuteExplorationPassAsync(SourceBackedEvidenceExplorationPass pass)
        {
            if (!currentAnalysis.ShouldExplore || remainingRagCalls <= 0 || pass.Queries.Length == 0)
                return false;

            var beforeAnalysis = currentAnalysis;
            remainingRagCalls--;

            var args = CreateJsonArgs(new
            {
                queries = pass.Queries,
                topK = ResolveSourceBackedEvidenceExplorationTopK(effectiveUserMessage, pass.Label),
                category = categoryScope,
                mode = "balanced"
            });

            var sw = Stopwatch.StartNew();
            try
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));
                var expandedResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
                sw.Stop();
                if (!HasRagHits(expandedResult))
                {
                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberExplorationPass(pass, beforeAnalysis, null, sw.ElapsedMilliseconds, accepted: false, rejectReason: "no_hits");
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

                var candidateAnalysis = AnalyzeSourceBackedEvidenceSufficiency(candidate, effectiveUserMessage, plan.Language);
                var improvesCoverage = IsBetterSourceBackedEvidenceCoverage(toolResults, candidate, effectiveUserMessage, plan.Language)
                    || CandidateSourceBackedEvidenceAddsUsefulDiversity(currentAnalysis, candidateAnalysis)
                    || candidateAnalysis.Score > currentAnalysis.Score;
                if (!improvesCoverage)
                {
                    _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
                    RememberExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: false, rejectReason: "no_coverage_gain");
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
                currentAnalysis = candidateAnalysis;
                RememberExplorationPass(pass, beforeAnalysis, candidateAnalysis, sw.ElapsedMilliseconds, accepted: true, rejectReason: null);
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
                RememberExplorationPass(pass, beforeAnalysis, null, sw.ElapsedMilliseconds, accepted: false, rejectReason: "error");
            }

            return false;
        }

        var passes = BuildSourceBackedEvidenceExplorationPasses(toolResults, effectiveUserMessage, plan.Language);
        foreach (var pass in passes.Take(MaxSourceBackedEvidenceExplorationPasses))
        {
            if (await TryExecuteExplorationPassAsync(pass).ConfigureAwait(false))
                acceptedAny = true;
        }

        if (currentAnalysis.ShouldExplore
            && remainingRagCalls > 0
            && ShouldUseLlmSourceBackedEvidencePlanner(effectiveUserMessage, currentAnalysis))
        {
            var plannedPasses = await TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
                    toolResults,
                    currentAnalysis,
                    effectiveUserMessage,
                    plan.Language,
                    ct)
                .ConfigureAwait(false);

            foreach (var pass in plannedPasses.Take(Math.Min(MaxSourceBackedLlmEvidenceExplorationPasses, remainingRagCalls)))
            {
                if (await TryExecuteExplorationPassAsync(pass).ConfigureAwait(false))
                    acceptedAny = true;
            }
        }

        return acceptedAny;
    }

    private async Task<IReadOnlyList<SourceBackedEvidenceExplorationPass>> TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        CancellationToken ct)
    {
        var alreadyTriedQueries = BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(toolResults, effectiveUserMessage, language);
        var system = BuildSourceBackedLlmEvidenceExplorationSystemPrompt(language);
        var user = BuildSourceBackedLlmEvidenceExplorationUserPrompt(
            toolResults,
            currentAnalysis,
            effectiveUserMessage,
            language,
            alreadyTriedQueries);

        var sw = Stopwatch.StartNew();
        try
        {
            var raw = await CompleteWithRetryAsync(
                    new[]
                    {
                        ("system", system),
                        ("user", user)
                    },
                    forceJson: true,
                    ct)
                .ConfigureAwait(false);
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, true));
            return ParseSourceBackedLlmEvidenceExplorationPasses(raw, alreadyTriedQueries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            _lastToolDurations.Add(("rag.exploration_plan", sw.ElapsedMilliseconds, false));
            return Array.Empty<SourceBackedEvidenceExplorationPass>();
        }
    }

    private static bool ShouldUseLlmSourceBackedEvidencePlanner(
        string effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis)
    {
        if (string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
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

    private static string BuildSourceBackedLlmEvidenceExplorationSystemPrompt(string language)
        => $@"
You are SAAIA's retrieval strategist, not the final answer writer.
Target user language: {NormalizeLanguageCode(language)}.

Task:
- Read the user request, the current evidence sufficiency report and the current source leads.
- Propose only additional retrieval queries that may find better source-backed evidence.
- Do not answer the user.
- Do not invent document names, category names, file names, product names, recipes or facts.
- Use generic search reasoning: split broad requests into useful facets, requested constraints, candidate types, synonyms and possible source-language terms.
- You may derive retrieval keywords from the user intent, not only repeat the exact words. Broad requests often need related nouns, constraints, slot names, option types and source-language equivalents.
- For planning, recommendation, comparison or selection requests, search both for concrete items and for framing/context passages.
- If category hints are present, you may use their names as optional retrieval terms, but do not create category-specific hardcoded rules.
- Keep queries short and reusable across domains.
- Avoid duplicates of queries already tried.

Return strict JSON only:
{{
  ""passes"": [
    {{
      ""label"": ""llm_strategy"",
      ""purpose"": ""why this pass may improve coverage"",
      ""queries"": [""short query 1"", ""short query 2""]
    }}
  ]
}}";

    private string BuildSourceBackedLlmEvidenceExplorationUserPrompt(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> alreadyTriedQueries)
    {
        var deterministicSeeds = BuildSourceBackedEvidenceExplorationPasses(toolResults, effectiveUserMessage, language)
            .SelectMany(static pass => pass.Queries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        return $@"
USER_REQUEST:
{effectiveUserMessage}

SUFFICIENCY:
- kind: {currentAnalysis.Kind}
- reason: {currentAnalysis.Reason}
- score: {currentAnalysis.Score}
- usableHits: {currentAnalysis.UsableHitCount}
- distinctSourcePages: {currentAnalysis.DistinctSourcePageCount}
- candidates: {currentAnalysis.CandidateCount}/{currentAnalysis.MinimumCandidateCount}
- targetSlots: {currentAnalysis.TargetSlotCount}
- hasRequiredAnchor: {currentAnalysis.HasRequiredAnchor}

CURRENT_SOURCE_LEADS:
{BuildSourceBackedLlmEvidenceSnapshotForPrompt(toolResults, effectiveUserMessage, language)}

CATEGORY_HINTS:
{BuildSourceBackedLlmCategoryHintsForPrompt(effectiveUserMessage)}

DETERMINISTIC_QUERY_SEEDS:
{FormatPromptList(deterministicSeeds)}

ALREADY_TRIED_QUERIES:
{FormatPromptList(alreadyTriedQueries)}

OUTPUT_RULES:
- Return at most {MaxSourceBackedLlmEvidenceExplorationQueries} queries in one pass.
- Prefer 4 to 10 strong queries over many weak queries.
- Include terms that broaden evidence only when the current leads are too narrow.
- For broad plans, include candidate-discovery queries and constraint/slot queries.
- For broad plans or recommendations, include queries that search for concrete options even when the user did not name those options explicitly.
- For pairing/recommendation requests, include requested option kinds and target anchors separately.
- Do not include UI prose, explanations outside JSON, source excerpts or final answer text.";
    }

    private static string BuildSourceBackedLlmEvidenceSnapshotForPrompt(ToolResults toolResults, string query, string language)
    {
        var lines = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
            .ThenByDescending(static hit => hit.Score)
            .Take(8)
            .Select(hit =>
            {
                var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                var cue = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: 120);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = BuildSourceBackedCandidateSupportCue(hit);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = "source-backed hit";

                var retrievalQuery = string.IsNullOrWhiteSpace(hit.RetrievalQuery)
                    ? string.Empty
                    : $" | retrievalQuery: {CollapseWhitespace(hit.RetrievalQuery)}";
                return $"- {source} {SourceBackedPagePrefix(language)}{Math.Max(1, hit.PageStart)} | {CollapseWhitespace(cue)}{retrievalQuery}";
            })
            .ToArray();

        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private string BuildSourceBackedLlmCategoryHintsForPrompt(string effectiveUserMessage)
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
            .Take(12)
            .Select(static x =>
            {
                var category = x.Category;
                var name = CollapseWhitespace(category.DisplayName);
                var path = CollapseWhitespace(category.CategoryPath);
                var aliases = category.Aliases is { Count: > 0 }
                    ? $" | aliases: {string.Join(", ", category.Aliases.Select(CollapseWhitespace).Where(static a => !string.IsNullOrWhiteSpace(a)).Take(4))}"
                    : string.Empty;
                var docs = category.TotalDocuments > 0 ? $" | docs: {category.TotalDocuments}" : string.Empty;
                return $"- {name}{(string.IsNullOrWhiteSpace(path) || string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" | path: {path}")}{docs}{aliases}";
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

        foreach (var pass in BuildSourceBackedEvidenceExplorationPasses(toolResults, effectiveUserMessage, language))
        {
            foreach (var query in pass.Queries)
                AddDistinctQuery(queries, query);
        }

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
            .Take(40)
            .ToArray();
    }

    private static string FormatPromptList(IEnumerable<string> values)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => $"- {CollapseWhitespace(value)}")
            .Take(40)
            .ToArray();
        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static int CountRagRetrievalToolCalls(ToolResults toolResults)
        => toolResults.Items.Count(static item => item.ToolName is "rag.search" or "rag.multi_search");

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
            return false;
        }

        var queries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage);
        if (queries.Length == 0)
            return false;

        var args = CreateJsonArgs(new
        {
            queries,
            topK = ResolveDocumentaryProbeTopK(effectiveUserMessage),
            category = ResolveRagCategoryScope(effectiveUserMessage),
            mode = "balanced"
        });

        var sw = Stopwatch.StartNew();
        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));
            var expandedResult = await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
            sw.Stop();
            if (!HasRagHits(expandedResult))
                return false;

            var candidate = new ToolResults();
            candidate.Items.AddRange(toolResults.Items);
            candidate.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });

            if (!IsBetterDocumentaryProbeCoverage(toolResults, candidate, effectiveUserMessage, plan.Language))
                return false;

            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = expandedResult,
                DurationMs = sw.ElapsedMilliseconds
            });
            _lastToolDurations.Add(("rag.multi_search", sw.ElapsedMilliseconds, true));
            if (!_mem.LastToolNames.Contains("rag.multi_search", StringComparer.OrdinalIgnoreCase))
                _mem.LastToolNames.Add("rag.multi_search");
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
            @"(?i)^\s*(?:source|sources|references?|r[eÃ©]f[eÃ©]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)(?:\s+(?:used|cited|consulted|utilis[eÃ©]es?|cit[eÃ©]es?|consult[eÃ©]es?|usadas?|utilizadas?|consultadas?|verwendete|consultate|citate))?\s*:\s*$";
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
                if (Regex.IsMatch(line, sourceHeadingLinePattern, RegexOptions.CultureInvariant))
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

            if (ShouldUseWriterForDocumentaryProbeAnswer(probeToolResults, effectiveUserMessage))
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

            var sourceBackedAnswer = BuildSourceBackedExtractiveAnswer(probeToolResults, effectiveUserMessage, plan.Language);
            var sourceBackedSources = DeriveSourcesFromExtractiveHits(probeToolResults, effectiveUserMessage);
            if (!string.IsNullOrWhiteSpace(sourceBackedAnswer) && sourceBackedSources.Count > 0)
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sourceBackedSources);
                sourceBackedAnswer = InjectInlineSources(sourceBackedAnswer, sourceBackedSources, plan.Language);
                var sourceBackedPayload = BuildSourcesPayload(sourceBackedSources);
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

    private async Task<(ToolResults ToolResults, IReadOnlyList<string> ToolNames)> RunDocumentaryProbeRetrievalAsync(
        string effectiveUserMessage,
        string language,
        string? categoryScope,
        CancellationToken ct)
    {
        var topK = ResolveDocumentaryProbeTopK(effectiveUserMessage);
        var initialArgs = CreateJsonArgs(new
        {
            query = effectiveUserMessage,
            topK,
            categoryPath = categoryScope,
            mode = "balanced"
        });
        var bestResult = await TryExecRagSearchOrEmptyAsync(initialArgs, ct).ConfigureAwait(false);
        var bestToolName = "rag.search";
        var bestToolResults = BuildProbeRagToolResults(bestResult, bestToolName);

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
                    mode = "balanced"
                });
                var expandedResult = await TryExecRagMultiSearchOrEmptyAsync(expandedArgs, ct).ConfigureAwait(false);
                var expandedToolResults = BuildProbeRagToolResults(expandedResult, "rag.multi_search");
                if (IsBetterDocumentaryProbeCoverage(bestToolResults, expandedToolResults, effectiveUserMessage, language))
                {
                    bestResult = expandedResult;
                    bestToolName = "rag.multi_search";
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
                    mode = "balanced"
                });
                var unscopedResult = await TryExecRagMultiSearchOrEmptyAsync(unscopedArgs, ct).ConfigureAwait(false);
                var unscopedToolResults = BuildProbeRagToolResults(unscopedResult, "rag.multi_search");
                if (IsBetterDocumentaryProbeCoverage(bestToolResults, unscopedToolResults, effectiveUserMessage, language))
                {
                    bestResult = unscopedResult;
                    bestToolName = "rag.multi_search";
                    bestToolResults = unscopedToolResults;
                }
            }
        }

        return (bestToolResults, new[] { bestToolName });
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

        Add(query);
        Add(NormalizeRagQueryForRetrieval(query));
        Add(BuildRagEvidenceSelectionQuery(query));

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            foreach (var retrievalQuery in BuildPlanningRetrievalQueries(query))
                Add(retrievalQuery);
            foreach (var retrievalQuery in BuildPlanningExplorationRetrievalQueries(query))
                Add(retrievalQuery);
        }
        else
        {
            foreach (var retrievalQuery in BuildSourceBackedEvidenceExpansionRetrievalQueries(query))
                Add(retrievalQuery);
        }

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(query))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsGenericDocumentaryProbeTerm(term))
            .Take(6)
            .ToArray();
        if (signalTerms.Length > 0)
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
            return (false, string.Empty, null);

        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));
            var categoryScope = ResolveRagCategoryScope(effectiveUserMessage);

            if (LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
            {
                var multiArgs = CreateJsonArgs(new
                {
                    queries = BuildPlanningRetrievalQueries(effectiveUserMessage),
                    topK = NormalizeSourceBackedPlanningTopK(null, effectiveUserMessage),
                    category = categoryScope,
                    mode = "balanced"
                });
                var multiResult = await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
                if (HasRagHits(multiResult))
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
                        var inferredCategoryScope = TryInferDominantTopLevelCategoryScope(planningToolResults, effectiveUserMessage);
                        if (!string.IsNullOrWhiteSpace(inferredCategoryScope))
                        {
                            var scopedMultiArgs = CreateJsonArgs(new
                            {
                                queries = BuildPlanningRetrievalQueries(effectiveUserMessage),
                                topK = NormalizeSourceBackedPlanningTopK(null, effectiveUserMessage),
                                category = inferredCategoryScope,
                                mode = "balanced"
                            });
                            var scopedMultiResult = await ExecRagMultiSearchAsync(scopedMultiArgs, ct).ConfigureAwait(false);
                            if (HasRagHits(scopedMultiResult))
                            {
                                multiArgs = scopedMultiArgs;
                                multiResult = scopedMultiResult;
                                effectivePlanningCategoryScope = inferredCategoryScope;
                                planningToolResults = new ToolResults();
                                planningToolResults.Items.Add(new ToolResults.Item
                                {
                                    ToolName = "rag.multi_search",
                                    Result = multiResult
                                });
                            }
                        }
                    }

                    await TryExpandSourceBackedEvidenceRetrievalAsync(
                        planningToolResults,
                        plan,
                        effectiveUserMessage,
                        ct,
                        onPhase,
                        onProgress,
                        effectivePlanningCategoryScope).ConfigureAwait(false);

                    var shouldUsePlanningWriter =
                        ShouldAllowWriterForPartialSourceBackedPlanning(planningToolResults, effectiveUserMessage, plan.Language)
                        || ShouldPreferWriterForPolishedSourceBackedAnswer(planningToolResults, effectiveUserMessage);
                    var deterministicPlanningAnswer = shouldUsePlanningWriter
                        ? string.Empty
                        : BuildSourceBackedPlanningOrExtractiveAnswer(planningToolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                    var deterministicPlanningSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                    if (deterministicPlanningSources.Count == 0)
                        deterministicPlanningSources = DeriveSourcesFromExtractiveHits(planningToolResults, effectiveUserMessage);
                    if (!string.IsNullOrWhiteSpace(deterministicPlanningAnswer) && deterministicPlanningSources.Count > 0)
                    {
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
                        return (true, finalizedDeterministicPlan.finalAnswer, deterministicPlanningSourcesPayload);
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
                    var (writerAnswer, writerSources) = await AnswerAsync(chatHistory, effectiveUserMessage, planningWriterPlan, planningToolResults, ct, onDelta, onProgress).ConfigureAwait(false);
                    writerAnswer = (writerAnswer ?? string.Empty).Trim();
                    if (ShouldFallbackFromNoRagDataAnswer(writerAnswer))
                    {
                        writerAnswer = BuildRagEvidenceFallbackAnswer(planningToolResults, effectiveUserMessage, plan.Language);
                        writerSources = DeriveSourcesFromExtractiveHits(planningToolResults, effectiveUserMessage);
                    }

                    if (writerSources is null || writerSources.Count == 0)
                        writerSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);

                    if (LooksLikePoorPlanningFallbackAnswer(writerAnswer, effectiveUserMessage))
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
                        else if (!ShouldAllowWriterForPartialSourceBackedPlanning(planningToolResults, effectiveUserMessage, plan.Language)
                            && !ShouldPreferWriterForPolishedSourceBackedAnswer(planningToolResults, effectiveUserMessage))
                        {
                            var planningAnswer = BuildSourceBackedPlanningAnswer(
                                planningToolResults,
                                plan.Language,
                                minItems: 1,
                                query: effectiveUserMessage);
                            if (!string.IsNullOrWhiteSpace(planningAnswer))
                                writerAnswer = planningAnswer;
                        }

                        writerSources = DeriveSourcesFromPlanningHits(planningToolResults, effectiveUserMessage);
                    }

                    object? writerSourcesPayload = null;
                    if (writerSources is { Count: > 0 })
                    {
                        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(writerSources);
                        writerAnswer = InjectInlineSources(writerAnswer, writerSources, plan.Language);
                        writerSourcesPayload = BuildSourcesPayload(writerSources);
                    }

                    _lastAnswerSource = "standalone_topic_rag:source_backed_planning_writer";
                    _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.multi_search", 0, true) };
                    _lastToolsMs = 0;
                    _mem.LastToolNames = new List<string> { "rag.multi_search" };
                    onProgress?.Invoke(string.Empty);

                    var finalizedWriterPlan = FinalizeAndReturn(swTotalPipeline, displayUserMessage, writerAnswer, writerSourcesPayload, "rag.answer", _mem.LastToolNames, _mem.LastReasoningTracePublic);
                    return (true, finalizedWriterPlan.finalAnswer, writerSourcesPayload);
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
                    mode = "balanced"
                })
                : CreateJsonArgs(new
                {
                    query = retrievalQuery,
                    topK = 8,
                    category = categoryScope,
                    mode = "balanced"
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
            var standaloneShouldUseBroadSynthesis =
                ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, effectiveUserMessage)
                || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, effectiveUserMessage);
            if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
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
                && ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage))
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
                && LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
            {
                if (!ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, effectiveUserMessage, plan.Language))
                {
                    preWriterAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language, minPlanningItems: 1);
                    preWriterSources = DeriveSourcesFromPlanningHits(toolResults, effectiveUserMessage);
                    if (preWriterSources.Count == 0)
                        preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }
            }
            else if (!standaloneShouldUseBroadSynthesis
                && (isSourceBackedActionRequest || ShouldUseSourceBackedExtractiveAnswer(effectiveUserMessage, toolResults)))
            {
                preWriterAnswer = BuildSourceBackedExtractiveAnswer(toolResults, effectiveUserMessage, plan.Language);
                preWriterSources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
            }

            if (string.IsNullOrWhiteSpace(preWriterAnswer)
                && !standaloneShouldUseBroadSynthesis
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
                return (true, finalizedDeterministic.finalAnswer, deterministicSourcesPayload);
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));

            var (answer, sources) = await AnswerAsync(chatHistory, effectiveUserMessage, ragPlan, toolResults, ct, onDelta, onProgress).ConfigureAwait(false);
            answer = (answer ?? string.Empty).Trim();
            if (ShouldFallbackFromNoRagDataAnswer(answer))
            {
                if (LooksLikeSourceBackedCountdownPlanningRequest(effectiveUserMessage))
                {
                    answer = BuildSourceBackedCountdownPlanningAnswer(toolResults, effectiveUserMessage, plan.Language);
                    sources = DeriveSourcesFromCountdownPlanningHits(toolResults, effectiveUserMessage);
                }
                else if (ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage))
                {
                    answer = BuildSourceBackedOptionAnswer(toolResults, plan.Language, minItems: 1, query: effectiveUserMessage);
                    sources = DeriveSourcesFromOptionHits(toolResults, effectiveUserMessage);
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    answer = BuildRagEvidenceFallbackAnswer(toolResults, effectiveUserMessage, plan.Language);
                    sources = DeriveSourcesFromExtractiveHits(toolResults, effectiveUserMessage);
                }
            }
            else if ((!standaloneShouldUseBroadSynthesis
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
                else if (ShouldUseSourceBackedOptionAnswer(exactItemTitle, effectiveUserMessage))
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
            if (sources is { Count: > 0 })
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
                answer = InjectInlineSources(answer, sources, plan.Language);
                sourcesPayload = BuildSourcesPayload(sources);
            }

            _lastAnswerSource = $"standalone_topic_rag:{ragPlan.Intent}";
            _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { (useMultiSearch ? "rag.multi_search" : "rag.search", 0, true) };
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            _mem.LastToolNames = new List<string> { useMultiSearch ? "rag.multi_search" : "rag.search" };
            onProgress?.Invoke(string.Empty);

            var finalized = FinalizeAndReturn(swTotalPipeline, displayUserMessage, answer, sourcesPayload, ragPlan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
            return (true, finalized.finalAnswer, sourcesPayload);
        }
        catch
        {
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
        var manifestJson = ToolManifest.BuildConversationManifestJson();
        var toolbook = ToolManifest.ConversationToolbookText;
        var repairHint = DocumentRefResolver.IsRepairMessage(userMessage);
        var resolverHint = DocumentRefResolver.Analyze(userMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);

        var memoryCtx = BuildRouterMemoryContext(resolverHint);

        var detectedMessageLanguage = ResolveInteractionLanguage(userMessage);
        var system = PromptCatalog.BuildRouterSystemPrompt(manifestJson, toolbook) + $@"

Additional runtime rules:
- Last answer language (informational only): {_mem.LastLanguage}
- Current message language hint: {detectedMessageLanguage}
- Disallow meta.set_language for this turn: {(disallowMetaSetLanguage ? "true" : "false")}
- The current message looks like a repair/correction turn: {(repairHint ? "true" : "false")}
- If document resolution hint says clarification is needed, prefer a short clarification over a blind tool call.
";

        var user = $@"
MEMORY (json):
{JsonSerializer.Serialize(memoryCtx)}

CHAT_TAIL (for context):
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}
";

        string raw;
        try
        {
            raw = await CompleteWithRetryAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }

        if (!TryExtractJsonObject(raw, out var planJson))
        {
            // Fallback safe: conversationnel sans tools
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }

        try
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

            return SanitizeRouterPlan(plan, detectedMessageLanguage, disallowMetaSetLanguage);
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }
    }

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
            onPhase?.Invoke(PhaseLabelForTool(call.Name, plan.Language));
            onProgress?.Invoke(DescribeToolAction(call.Name, userMessage, plan.Language, call.Args));

            var sw = Stopwatch.StartNew();
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
                    continue;
                }

                JsonElement res = await handler(call.Args, ct).ConfigureAwait(false);

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Result = res,
                    DurationMs = sw.ElapsedMilliseconds
                });
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

        var writerToolResults = BuildWriterToolResults(plan, toolResults, userMessage);
        _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastUsedInventoryRendered = _lastUsedInventoryRendered || _lastWriterToolNames.Any(x => string.Equals(x, "inventory.rendered", StringComparison.OrdinalIgnoreCase));
        var inventoryRenderedText = TryRenderInventoryFallbackText(writerToolResults, plan.Language);
        var inventoryRenderedDataJson = TryExtractInventoryRenderedDataJson(writerToolResults);
        var backendClarification = TryBuildBackendGuidanceClarificationAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(backendClarification))
        {
            RememberPendingClarification("rag_guidance", userMessage, "backend_ask_clarification", plan.Language);
            _lastAnswerSource = $"backend_guidance_ask_clarification:{plan.Intent}";
            return (backendClarification, null);
        }

        var versionTraceabilityAnswer = TryBuildDocumentVersionTraceabilityAnswer(toolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(versionTraceabilityAnswer))
        {
            var traceabilitySources = DeriveSourcesFromDocumentVersionTraceabilityHits(toolResults, userMessage);
            _lastAnswerSource = $"writer_bypass_document_version_traceability:{plan.Intent}";
            return (versionTraceabilityAnswer, traceabilitySources.Count > 0 ? traceabilitySources : null);
        }

        var sourcePolicyGuard = TryBuildSourcePolicyGuardAnswer(writerToolResults, userMessage, plan.Language);
        if (!string.IsNullOrWhiteSpace(sourcePolicyGuard))
        {
            var guardSources = LooksLikeDocumentInstructionPolicyRequest(userMessage)
                ? new List<ToolMemory.SourceRef>()
                : DeriveSourcesFromRagHits(writerToolResults).Take(5).ToList();
            _lastAnswerSource = $"writer_bypass_source_policy:{plan.Intent}";
            return (sourcePolicyGuard, guardSources.Count > 0 ? guardSources : null);
        }

        if (ShouldBypassWriterForDeterministicInventory(plan, writerToolResults, inventoryRenderedText))
        {
            var deterministicAnswer = (inventoryRenderedText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                _lastAnswerSource = $"writer_bypass_deterministic_inventory:{plan.Intent}";
                return (deterministicAnswer, null);
            }
        }

        var writerEvidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var requestedItemTitle = LooksLikeShortTechnicalEvidenceTopic(writerEvidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(userMessage);
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
                return (missingExactAnswer, missingExactSources);
            }
        }

        var shouldUseSourceBackedOptionAnswer = ShouldUseSourceBackedOptionAnswer(requestedItemTitle, userMessage);
        var shouldUseSourceBackedCountdownAnswer = LooksLikeSourceBackedCountdownPlanningRequest(userMessage);
        var shouldUseSourceBackedExtractiveAnswer = ShouldUseSourceBackedExtractiveAnswer(userMessage, writerToolResults);
        var shouldUseSourceBackedActionAnswer = LooksLikeSourceBackedActionRequest(userMessage)
            && ShouldPreferSourceBackedAnswerOverBackendClarification(writerToolResults, userMessage);
        var shouldUseSourceBackedPairingAnswer = LooksLikeSourceBackedPairingRecommendationRequest(userMessage);
        var shouldUseBroadSourceBackedSynthesis =
            ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage);

        if (!shouldUseBroadSourceBackedSynthesis
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
                return (deterministicAnswer, deterministicSources);
            }
        }

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 10)}

USER_MESSAGE:
{userMessage}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(userMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{BuildSourceBackedCoverageHintsForWriter(writerToolResults, userMessage, plan.Language)}

PRIVATE_SOURCE_WRITING_BRIEF:
{BuildSourceBackedWritingBriefForWriter(writerToolResults, userMessage, plan.Language)}

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{BuildSourceBackedCandidateLeadsForWriter(writerToolResults, userMessage, plan.Language)}

TOOL_RESULTS (json):
{SerializeToolResults(writerToolResults)}

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
            finalAnswer = await StreamOrCompleteWithRetryAsync(writerMessages, onDelta, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex)
                                   && writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            finalAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(writerToolResults, userMessage, plan.Language, minPlanningItems: 1);
            if (string.IsNullOrWhiteSpace(finalAnswer))
                finalAnswer = BuildRagEvidenceFallbackAnswer(writerToolResults, userMessage, plan.Language);
            if (string.IsNullOrWhiteSpace(finalAnswer))
                throw;

            _lastAnswerSource = $"writer_context_overflow_deterministic_fallback:{plan.Intent}";
            usedWriterContextOverflowFallback = true;
            if (onDelta is not null)
                onDelta(finalAnswer);
        }

        finalAnswer = (finalAnswer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer) && !string.IsNullOrWhiteSpace(inventoryRenderedText))
            finalAnswer = inventoryRenderedText.Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = DeterministicAgentText.AnswerNotEnoughUsableInfo(plan.Language);

        finalAnswer = await EnsureAnswerMatchesRequestedLanguageAsync(finalAnswer, plan.Language, ct).ConfigureAwait(false);

        List<ToolMemory.SourceRef>? sources = null;
        var usedRagSearch = toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search");
        var usedSourcesResolve = toolResults.Items.Any(x => x.ToolName == "sources.resolve");
        var usedSummarySearch = toolResults.Items.Any(x => x.ToolName == "summary.search");
        var sourceToolResults = usedWriterContextOverflowFallback ? writerToolResults : toolResults;

        if (usedRagSearch && LooksLikeDegenerateLlmOutput(finalAnswer) && !LooksLikeWeeklyPlanningRequest(userMessage))
        {
            var guardedAnswer = BuildSourceBackedPlanningOrExtractiveAnswer(sourceToolResults, userMessage, plan.Language, minPlanningItems: 1);
            finalAnswer = string.IsNullOrWhiteSpace(guardedAnswer)
                ? BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, plan.Language)
                : guardedAnswer;
            _lastAnswerSource = $"writer_guard_degenerate_output:{plan.Intent}";
        }

        if (usedRagSearch)
        {
            if (LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
                sources = DeriveSourcesFromCountdownPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
                sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
            else if (LooksLikeSourceBackedPairingRecommendationRequest(userMessage))
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (ShouldUseSourceBackedOptionAnswer(requestedItemTitle, userMessage))
                sources = DeriveSourcesFromOptionHits(sourceToolResults, userMessage);
            else if (ShouldUseSourceBackedExtractiveAnswer(userMessage, toolResults)
                || LooksLikeSourceBackedActionRequest(userMessage)
                || LooksLikeComparativeDocumentaryRequest(userMessage))
                sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
            else
                sources = DeriveSourcesFromRagHits(sourceToolResults);

            if (sources.Count == 0)
                sources = DeriveSourcesFromRagHits(sourceToolResults);

            if (LooksLikeSourceBackedCountdownPlanningRequest(userMessage))
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

            if (ShouldFallbackFromNoRagDataAnswer(finalAnswer))
            {
                var repairAnswer = (ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
                        || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage))
                    ? await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false)
                    : BuildSourceBackedPlanningOrExtractiveAnswer(sourceToolResults, userMessage, plan.Language, minPlanningItems: 1);
                if (string.IsNullOrWhiteSpace(repairAnswer))
                    repairAnswer = BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, plan.Language);

                if (!string.IsNullOrWhiteSpace(repairAnswer))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                }
            }

            var missingRequiredAnswer = TryBuildMissingRequiredEvidenceAnswer(sourceToolResults, userMessage, plan.Language);
            if (!ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(sourceToolResults, userMessage)
                && !string.IsNullOrWhiteSpace(missingRequiredAnswer))
            {
                finalAnswer = missingRequiredAnswer;
                sources = DeriveSourcesFromRagHits(sourceToolResults).Take(5).ToList();
                _lastAnswerSource = $"writer_guard_missing_required_evidence:{plan.Intent}";
            }

            var missingPairingAnchorAnswer = TryBuildMissingPairingAnchorAnswer(sourceToolResults, userMessage, plan.Language);
            if (!ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(sourceToolResults, userMessage)
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
            if (!string.IsNullOrWhiteSpace(missingBroadAnchorAnswer))
            {
                finalAnswer = missingBroadAnchorAnswer;
                sources = DeriveSourcesFromRagHits(sourceToolResults).Take(5).ToList();
                _lastAnswerSource = $"writer_guard_missing_broad_anchor:{plan.Intent}";
            }

            if ((ShouldPreferPartialEvidenceFallbackOverOptions(sourceToolResults, userMessage)
                    || LooksLikeUnsupportedBroadOptionComposition(userMessage, finalAnswer))
                && ShouldReplaceOverPromotedSourceBackedOptionAnswer(finalAnswer, sourceToolResults, userMessage))
            {
                var fallbackAnswer = BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, plan.Language);
                if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                {
                    finalAnswer = fallbackAnswer;
                    sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_overpromoted_options:{plan.Intent}";
                }
            }

            if (LooksLikeUnderusedSourceBackedPlanningAnswer(finalAnswer, sourceToolResults, userMessage))
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
                else if (!ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
                {
                    var planningAnswer = BuildSourceBackedPlanningAnswer(sourceToolResults, plan.Language, minItems: 2, query: userMessage);
                    if (!string.IsNullOrWhiteSpace(planningAnswer))
                    {
                        finalAnswer = planningAnswer;
                        sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                        _lastAnswerSource = $"writer_guard_underused_planning_sources:{plan.Intent}";
                    }
                }
            }

            if (LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage))
            {
                var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(chatHistory, userMessage, plan, writerToolResults, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(repairAnswer)
                    && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, userMessage))
                {
                    finalAnswer = repairAnswer;
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"writer_guard_poor_planning_repaired:{plan.Intent}";
                }
                else if (!ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
                {
                    var planningAnswer = BuildSourceBackedPlanningAnswer(sourceToolResults, plan.Language, minItems: 2, query: userMessage);
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
                        sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                        if (sources.Count == 0)
                            sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                        _lastAnswerSource = $"writer_guard_poor_planning_deterministic:{plan.Intent}";
                    }
                }
            }

            if (sources.Count > 0 && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
            {
                sources.Clear();
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

        if (ShouldRunCriticPass(plan, toolResults, useGeneralChatPrompt, userMessage))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressCheckAlignmentWithSources(plan.Language));

            finalAnswer = await RunCriticPassAsync(chatHistory, userMessage, plan, writerToolResults, finalAnswer, ct).ConfigureAwait(false);
            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        if (usedRagSearch
            && sources is { Count: > 0 }
            && LooksLikePoorPlanningFallbackAnswer(finalAnswer, userMessage))
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
                sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                if (sources.Count == 0)
                    sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"post_critic_guard_poor_planning_repaired:{plan.Intent}";
                }
            else if (!ShouldAllowWriterForPartialSourceBackedPlanning(sourceToolResults, userMessage, plan.Language))
            {
                var planningAnswer = BuildSourceBackedPlanningAnswer(sourceToolResults, plan.Language, minItems: 2, query: userMessage);
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
                    sources = DeriveSourcesFromPlanningHits(sourceToolResults, userMessage);
                    if (sources.Count == 0)
                        sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
                    _lastAnswerSource = $"post_critic_guard_poor_planning_deterministic:{plan.Intent}";
                }
            }
        }

        if (usedRagSearch && sources is { Count: > 0 } && LooksLikeMissingExactItemWithoutSourceLeads(finalAnswer))
        {
            sources.Clear();
        }
        else if (usedRagSearch && sources is { Count: > 0 } && ShouldFallbackFromNoRagDataAnswer(finalAnswer))
        {
            finalAnswer = BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, plan.Language);
            if (ShouldUseSourceBackedExtractiveAnswer(userMessage, sourceToolResults)
                || LooksLikeSourceBackedActionRequest(userMessage)
                || LooksLikeComparativeDocumentaryRequest(userMessage))
            {
                sources = DeriveSourcesFromExtractiveHits(sourceToolResults, userMessage);
            }
        }

        finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        if (usedRagSearch && LooksLikeWriterControlLeak(finalAnswer))
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
                var fallbackAnswer = BuildRagEvidenceFallbackAnswer(sourceToolResults, userMessage, plan.Language);
                if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                {
                    finalAnswer = fallbackAnswer;
                    sources = DeriveSourcesFromRagHits(sourceToolResults);
                    _lastAnswerSource = $"post_writer_guard_control_leak_deterministic:{plan.Intent}";
                }
            }

            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer);
        }

        return (finalAnswer, sources);
    }

    private async Task<string> TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults writerToolResults,
        CancellationToken ct)
    {
        var writerAllowed = ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, userMessage)
            || ShouldUseWriterForDocumentaryProbeAnswer(writerToolResults, userMessage);
        var hasRagEvidence = writerToolResults.Items.Any(item => item.ToolName is "rag.search" or "rag.multi_search");
        if (!writerAllowed || !hasRagEvidence)
        {
            return string.Empty;
        }

        var language = NormalizeLanguageCode(plan.Language);
        var system = $@"
You are SAAIA assistant.
Target language: {language}.

The tool results contain partial documented evidence for a broad documentary request.

Rules:
- Answer only from the tool results.
- The final answer must be written in the target language. If a source is in another language, translate/paraphrase the useful meaning into the target language and keep only source names, page numbers, units, values and short quoted terms unchanged.
- Do not say there is no data when hits are present.
- Do not dump raw excerpts.
- Treat PRIVATE_SOURCE_WRITING_BRIEF, PRIVATE_SOURCE_COVERAGE_NOTE and PRIVATE_SOURCE_EVIDENCE_INVENTORY as private drafting aids, not final wording. Do not expose control words such as coverage, candidate(s), slot(s), evidenceRole, writerEvidence or tool result.
- Your job is to rewrite and synthesize: extract useful facts from the hits, then present them as polished user-facing prose instead of pasting retrieved text.
- Do not repeat or paraphrase the user's whole question in the first sentence.
- Do not write bullets whose main content is ""document p.N: copied passage"". Keep source names/pages as short references after a concise candidate or planning point.
- Do not write a final Source/Sources bibliography section. The application appends clickable source cards.
- Do not invent concrete items/actions that are absent from the hits.
- Separate sourced facts from your organization layer: you may arrange sourced candidates into a plan, comparison, recommendation, procedure outline or document list when useful, but state the limits when the sources are partial.
- Build a short, useful, user-friendly answer from the sourced leads: a natural opening, the requested structure, concise candidate items/actions, caveat for missing coverage, and source names/pages.
- For planning requests, start with the actual draft structure. Do not open with ""I can build..."" or with the caveat that the sources are partial; put that caveat after the draft.
- If the sources are partial, make the answer useful first and place the limitation at the end. Avoid mechanical phrases such as ""X candidate(s) for Y slot(s)"" unless the user asked for diagnostics.
- Prefer clear user-facing labels instead of technical wording.
- Remove noisy OCR artifacts and avoid copying long passage fragments.
- Correct obvious OCR/text-extraction damage, missing accents, broken spacing and malformed words when doing so does not change the source facts.
- Keep the answer compact by default. For explicit grids, plans, comparisons or step lists, use the requested structure instead of forcing everything into 8 bullets.
- For every concrete item, action, quantity, timing or citation, preserve only what appears in the hits.
- You may use lightweight Markdown when it improves readability: short section labels, bullet or numbered lists, and **bold** for important labels. Do not use code fences.
";

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 6)}

USER_MESSAGE:
{userMessage}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(userMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{BuildSourceBackedCoverageHintsForWriter(writerToolResults, userMessage, plan.Language)}

PRIVATE_SOURCE_WRITING_BRIEF:
{BuildSourceBackedWritingBriefForWriter(writerToolResults, userMessage, plan.Language)}

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{BuildSourceBackedCandidateLeadsForWriter(writerToolResults, userMessage, plan.Language)}

TOOL_RESULTS (json):
{SerializeToolResults(writerToolResults)}
";

        var messages = new[]
        {
            ("system", system),
            ("user", user)
        };

        var repair = await StreamOrCompleteWithRetryAsync(messages, onDelta: null, ct).ConfigureAwait(false);
        repair = (repair ?? string.Empty).Trim();

        return ShouldFallbackFromNoRagDataAnswer(repair)
            ? string.Empty
            : repair;
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
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, userMessage);
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
                    return revised.Trim();
                }

                return draftAnswer;
            }

            _lastCriticStatus = "invalid";
        }
        catch
        {
            if (swCritic.IsRunning)
                swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;
            _lastCriticStatus = "error";
        }

        return draftAnswer;
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
            RememberTurnState(displayUserMessage, clarification, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic);
            onProgress?.Invoke(string.Empty);
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            swTotalPipeline.Stop();
            _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
            return (true, clarification, null);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseSummary(plan.Language));
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
        RememberTurnState(displayUserMessage, summaryAnswer.finalAnswer, rememberedIntent, new[] { "summary.flow" }, _mem.LastReasoningTracePublic);
        _lastUsedSummaryFlow = true;
        _lastWriterToolNames = new List<string> { "summary.flow" };
        _lastToolsMs = swSummary.ElapsedMilliseconds;
        _lastWriterMs = 0;
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("summary.flow", swSummary.ElapsedMilliseconds, true) };

        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        return (true, summaryAnswer.finalAnswer, summaryAnswer.sourcesPayload);
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
                mode = NormalizeRagMode(GetStringArg(args, "mode"))
            },
            "rag.multi_search" => new
            {
                queries = NormalizeRagMultiSearchQueries(args),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                categoryRef = GetStringArg(args, "categoryRef") ?? GetNestedStringArg(args, "filters", "categoryRef"),
                mode = NormalizeRagMode(GetStringArg(args, "mode"))
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
                docPath = GetStringArg(args, "docPath"),
                cursor = GetStringArg(args, "cursor"),
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
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
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

    private void ApplyDocumentaryRagDefaults(RouterPlan plan, string effectiveUserMessage)
    {
        var exactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var isSourceBackedActionRequest = LooksLikeSourceBackedActionRequest(effectiveUserMessage);
        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        var isSourceBackedAdaptationRequest = LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage);
        var isDocumentaryContentRequest = LooksLikeDocumentaryContentRequest(effectiveUserMessage);
        var isBroadDocumentaryInformationRequest = LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);
        var isDocumentVersionTraceabilityRequest = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage);
        var isDocumentaryPlanning = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage);
        var isSourceBackedRecommendationRequest =
            LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage);
        var preferSingleRagSearch = ShouldPreferSingleRagSearchForDocumentaryRequest(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            exactItemTitle = null;
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
            if (isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
            {
                plan.ToolCalls.Add(new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage),
                        topK = NormalizeSourceBackedActionTopK(null, effectiveUserMessage),
                        category = categoryScope,
                        mode = "balanced"
                    })
                });
            }
            else if (isDocumentaryPlanning && string.IsNullOrWhiteSpace(exactItemTitle))
            {
                plan.ToolCalls.Add(new RouterPlan.ToolCall
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = BuildPlanningRetrievalQueries(effectiveUserMessage),
                        topK = NormalizeSourceBackedPlanningTopK(null, effectiveUserMessage),
                        category = categoryScope,
                        mode = "balanced"
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
                            mode = "balanced"
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
                            mode = "balanced"
                        })
                });
            }
        }

        foreach (var call in plan.ToolCalls)
        {
            call.Name = NormalizeToolName(call.Name);
            if (string.Equals(call.Name, "rag.search", StringComparison.OrdinalIgnoreCase))
            {
                if (preferSingleRagSearch && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    call.Args = CreateJsonArgs(new
                    {
                        query = retrievalQuery,
                        topK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 12),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isDocumentaryPlanning && !isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = BuildPlanningRetrievalQueries(effectiveUserMessage),
                        topK = NormalizeSourceBackedPlanningTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
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
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isComparativeDocumentaryRequest)
                {
                    var comparativeQueries = BuildComparativeRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    existingQuery = NormalizeComparativeSupplementalRetrievalQuery(existingQuery, effectiveUserMessage);
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !comparativeQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        comparativeQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentVersionTraceabilityRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isBroadDocumentaryInformationRequest)
                {
                    var broadQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !broadQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        broadQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = broadQueries.Take(16).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isSourceBackedRecommendationRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isSourceBackedAdaptationRequest || isSourceBackedActionRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentaryContentRequest)
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
                    if (!string.IsNullOrWhiteSpace(existingQuery)
                        && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
                    {
                        actionQueries.Add(existingQuery);
                    }

                    call.Name = "rag.multi_search";
                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
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
                    category = TryGetStringArg(call.Args, "category") ?? categoryScope,
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
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isDocumentaryPlanning && !isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var planningQueries = queries
                        .Where(static q => !string.IsNullOrWhiteSpace(q))
                        .Select(CollapseWhitespace)
                        .ToList();
                    foreach (var query in BuildPlanningRetrievalQueries(effectiveUserMessage))
                    {
                        if (!planningQueries.Any(existing => string.Equals(existing, query, StringComparison.OrdinalIgnoreCase)))
                            planningQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = planningQueries.Take(12).ToArray(),
                        topK = NormalizeSourceBackedPlanningTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
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
                    foreach (var query in queries)
                    {
                        var supplementalQuery = NormalizeComparativeSupplementalRetrievalQuery(query, effectiveUserMessage);
                        if (!comparativeQueries.Any(q => string.Equals(q, supplementalQuery, StringComparison.OrdinalIgnoreCase)))
                            comparativeQueries.Add(supplementalQuery);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = "balanced"
                    });
                    continue;
                }

                if (isDocumentVersionTraceabilityRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries)
                    {
                        if (!actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isBroadDocumentaryInformationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var broadQueries = BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries)
                    {
                        if (!broadQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            broadQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = broadQueries.Take(16).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isSourceBackedRecommendationRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries)
                    {
                        if (!actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if ((isSourceBackedAdaptationRequest || isSourceBackedActionRequest) && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries)
                    {
                        if (!actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(effectiveUserMessage)).ToArray(),
                        topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), effectiveUserMessage),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
                    });
                    continue;
                }

                if (isDocumentaryContentRequest && string.IsNullOrWhiteSpace(exactItemTitle))
                {
                    var actionQueries = BuildSourceBackedActionRetrievalQueries(effectiveUserMessage).ToList();
                    foreach (var query in queries)
                    {
                        if (!actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                            actionQueries.Add(query);
                    }

                    call.Args = CreateJsonArgs(new
                    {
                        queries = actionQueries.Take(12).ToArray(),
                        topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                        category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                        mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
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
                    category = TryGetStringArg(call.Args, "category") ?? categoryScope,
                    mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
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
        var defaultTopK = targetSlots >= 10 ? Math.Min(20, targetSlots + 5) : 10;
        var topK = NormalizeIntArg(requestedTopK, defaultTopK, 4, 20);
        return targetSlots >= 10 ? Math.Min(20, Math.Max(defaultTopK, topK)) : Math.Max(8, topK);
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
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.categories" or "documents.tree" or "documents.stats" or "documents.empty_count" or "documents.empty_list" or "documents.extraction_quality" or "documents.extraction_pages" or
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
        if (clearPendingClarification)
            ClearPendingClarification();

        if (IsInventoryIntent(routerIntent)
            && _mem.LastDeterministicRender is not null
            && string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.SourceUserMessage))
        {
            _mem.LastDeterministicRender.SourceUserMessage = rememberedUserMessage;
        }

        RememberTurnState(rememberedUserMessage, finalAnswer, routerIntent, toolNames, reasoningTracePublic);
        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        return (finalAnswer, sourcesPayload);
    }

    // ---------------- Utility ----------------

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

        foreach (var item in toolResults.Items)
        {
            if (item.ToolName is "rag.search" or "rag.multi_search"
                && string.IsNullOrWhiteSpace(item.Error)
                && item.Result.ValueKind == JsonValueKind.Object)
            {
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

            compacted.Items.Add(item);
        }

        return compacted;
    }

    private static JsonElement CompactSummarySearchResultForWriter(JsonElement result)
    {
        try
        {
            if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;

            var list = new List<object?>();
            foreach (var it in items.EnumerateArray().Where(static entry => entry.ValueKind == JsonValueKind.Object).Take(20))
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
                    summaryText = TruncateForPrompt(TryGetString(it, "summaryText") ?? TryGetString(it, "SummaryText"), 1600),
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

            var prioritizeComparison = LooksLikeComparativeDocumentaryRequest(userMessage);
            var prioritizeEvidence = precise || prioritizeComparison;
            var prioritizePlanning = !prioritizeEvidence && LooksLikeAnyDocumentaryPlanningRequest(userMessage);
            var maxHits = prioritizeComparison ? RagWriterComparativeMaxHits : prioritizeEvidence ? RagWriterMaxHits : prioritizePlanning ? RagWriterPlanningMaxHits : RagWriterBroadMaxHits;
            var excerptChars = prioritizeEvidence ? RagWriterMaxExcerptChars : RagWriterBroadExcerptChars;
            var fullTextChars = prioritizeEvidence ? RagWriterMaxFullTextChars : RagWriterBroadFullTextChars;
            var contextualChars = prioritizeEvidence ? RagWriterContextualTotalChars : RagWriterBroadContextualChars;
            var list = new List<object?>();
            var rawHits = hits.EnumerateArray()
                .Where(static it => it.ValueKind == JsonValueKind.Object)
                .Select(static it => it.Clone())
                .ToList();
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
                var writerEvidence = BuildWriterEvidenceCueForPrompt(hitSummary, userMessage, maxLength: prioritizeEvidence ? 180 : 140);
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
                    list.Add(new
                    {
                        docPath,
                        docName,
                        pageStart,
                        pageEnd,
                        excerpt,
                        fullText,
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
                        writerEvidence = string.IsNullOrWhiteSpace(writerEvidence) ? null : writerEvidence,
                        writerUse = string.IsNullOrWhiteSpace(writerUse) ? null : writerUse,
                        contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
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

            var meta = CompactRagMetaForWriter(result);

            object? guidance = null;
            if (result.TryGetProperty("guidance", out var guidanceEl) && guidanceEl.ValueKind == JsonValueKind.Object)
            {
                guidance = JsonSerializer.Deserialize<object>(guidanceEl.GetRawText());
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new { hits = list, guidance, meta })).RootElement.Clone();
        }
        catch
        {
            return result;
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
        return hasEnoughText
               || hit.MatchedContentCards is { Count: > 0 }
               || hit.ProfileSignals is not null;
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
               || hit.ProfileSignals is not null;
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
