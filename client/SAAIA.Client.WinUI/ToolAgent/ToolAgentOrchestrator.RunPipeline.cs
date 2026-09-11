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
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    /// <summary>
    /// ExÃƒÂ©cute le pipeline Router Ã¢â€ â€™ Tools Ã¢â€ â€™ Answer.
    /// </summary>
    /// <param name="onPhase">Callback UX (status bar) : "RouteurÃ¢â‚¬Â¦", "Recherche documentsÃ¢â‚¬Â¦", "RÃƒÂ©dactionÃ¢â‚¬Â¦", etc.</param>
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
        using var telemetryScope = SourceBackedTelemetryContext.Push(
            EnsureRagTraceId());
        var cumulativeLlmOptions = SourceBackedAgentV2Options
            .ResolveFromEnvironment();
        var cumulativeLlmStopwatch = Stopwatch.StartNew();
        using var cumulativeLlmBudgetScope =
            _llm is ISourceBackedAgentLlmClient
                ? SourceBackedLlmCumulativeBudgetContext.Push(
                    new SourceBackedLlmCumulativeBudget(
                        cumulativeLlmOptions.MaximumCumulativeLlmTokens,
                        cumulativeLlmOptions
                            .MaximumCumulativeLlmElapsedMilliseconds,
                        cumulativeLlmOptions
                            .CumulativeLlmTerminalReserveTokens,
                        cumulativeLlmOptions
                            .CumulativeLlmTerminalReserveMilliseconds,
                        () => cumulativeLlmStopwatch.ElapsedMilliseconds))
                : null;
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

        onPhase?.Invoke(DeterministicAgentText.PhaseRouter(interactionLanguage));
        onProgress?.Invoke(LocalizedStrings.Get("phase.interpreting", interactionLanguage));

        var semanticNativeRouter =
            _llm is ISourceBackedAgentLlmClient;
        if (semanticNativeRouter
            || ShouldUseCompactSourceBackedRouterPrompt(effectiveUserMessage))
        {
            var catalogContextSw = Stopwatch.StartNew();
            EmitRagTrace(
                "router.catalog_context.start",
                ("reason", semanticNativeRouter
                    ? "semantic_native_router"
                    : "source_backed_router_hint"),
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
        RouterPlan plan;
        try
        {
            plan = await RouterAsync(
                    chatHistory,
                    effectiveUserMessage,
                    ct,
                    disallowMetaSetLanguage: false)
                .ConfigureAwait(false);
            if (string.Equals(
                    plan.Intent,
                    "meta.set_language",
                    StringComparison.OrdinalIgnoreCase)
                && !TryDetectExplicitLanguageSwitch(userMessage, out _))
            {
                plan = await RouterAsync(
                        chatHistory,
                        effectiveUserMessage,
                        ct,
                        disallowMetaSetLanguage: true)
                    .ConfigureAwait(false);
            }
        }
        catch (SourceBackedLlmBudgetExceededException ex)
        {
            swRouter.Stop();
            _lastRouterMs = swRouter.ElapsedMilliseconds;
            var handoff = SourceBackedBudgetHandoffContract.Create(
                interactionLanguage);
            BuildAndRememberAdvancedAnalysisHandoff(
                new RouterPlan
                {
                    Language = interactionLanguage,
                    Intent = "router.unresolved"
                },
                effectiveUserMessage,
                handoff.ReasonCode,
                "router_budget_exhausted",
                answerUnitCount: 0,
                MapAdvancedAnalysisBudgetSnapshot(ex));
            _lastAnswerSource =
                "capability_boundary:advanced_analysis_required_after_router_budget";
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            _mem.LastToolNames = new List<string>();
            _mem.LastRagQueries = new List<string>();
            EmitRagTrace(
                "router.cumulative_budget.terminal",
                ("reason", ex.Reason),
                ("reason_code", handoff.ReasonCode),
                ("charged_tokens", ex.Snapshot.ChargedTokens),
                ("reserved_tokens", ex.Snapshot.ReservedTokens),
                ("remaining_tokens", ex.Snapshot.RemainingTokens),
                ("remaining_ms", ex.Snapshot.RemainingMilliseconds),
                ("decision", handoff.Intent),
                ("visible_sources", handoff.HasVisibleSources));
            await EmitDeterministicTextAsync(
                    handoff.Answer,
                    onDelta,
                    ct)
                .ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                handoff.Answer,
                null,
                handoff.Intent,
                Array.Empty<string>(),
                Array.Empty<string>());
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

        if (ShouldRequestUnresolvedComparativeDocumentReferences(
                effectiveUserMessage,
                _mem.LastSourcesUsed is { Count: > 0 }))
        {
            var clarification =
                BuildUnresolvedComparativeDocumentReferenceClarification(
                    plan.Language);
            _lastAnswerSource =
                "capability_boundary:comparative_document_references_required";
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            await EmitDeterministicTextAsync(
                clarification,
                onDelta,
                ct).ConfigureAwait(false);
            RememberPendingClarification(
                "comparative_document_references",
                userMessage,
                "two_document_references",
                plan.Language);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "clarification.comparative_document_references"),
                ("intent", plan.Intent),
                ("reason", "unresolved_deictic_comparison"));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                clarification,
                null,
                "clarification",
                Array.Empty<string>(),
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
        }

        var capabilityBoundary = EvaluateLocalCapabilityBoundary(
            plan,
            effectiveUserMessage,
            _mem.LastSourcesUsed is { Count: > 0 });
        EmitRagTrace(
            "capability_boundary.decision",
            ("decision", capabilityBoundary.RequiresAdvancedAnalysis
                ? "advanced_analysis_required"
                : "local_eligible"),
            ("reason", capabilityBoundary.ReasonCode),
            ("plan_kind", capabilityBoundary.PlanKind),
            ("answer_units", capabilityBoundary.AnswerUnitCount),
            ("structured_threshold", AdvancedStructuredAnswerUnitThreshold),
            ("multi_item_threshold", AdvancedMultiItemAnswerUnitThreshold));
        if (capabilityBoundary.RequiresAdvancedAnalysis)
        {
            var boundaryAnswer = DeterministicAgentText.AdvancedAnalysisRequired(
                capabilityBoundary.AnswerUnitCount,
                plan.Language);
            BuildAndRememberAdvancedAnalysisHandoff(
                plan,
                effectiveUserMessage,
                capabilityBoundary.ReasonCode,
                "before_retrieval",
                capabilityBoundary.AnswerUnitCount);
            _lastAnswerSource = "capability_boundary:advanced_analysis_required";
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            await EmitDeterministicTextAsync(boundaryAnswer, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "turn.branch",
                ("path", "capability_boundary.advanced_analysis_required"),
                ("reason", capabilityBoundary.ReasonCode),
                ("plan_kind", capabilityBoundary.PlanKind),
                ("answer_units", capabilityBoundary.AnswerUnitCount));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                boundaryAnswer,
                null,
                "advanced_analysis_required",
                Array.Empty<string>(),
                _mem.LastReasoningTracePublic);
        }

        if (plan.NeedClarification
            && plan.ClarificationQuestions.Count > 0)
        {
            _lastAnswerSource = "router.clarification";
            var clarification = RenderRouterClarification(plan);
            await EmitDeterministicTextAsync(
                clarification,
                onDelta,
                ct).ConfigureAwait(false);
            var clarificationKind = plan.Origin == RouterPlanOrigin.Llm
                ? "llm_router"
                : docResolution.ClarificationKind ?? "generic";
            RememberPendingClarification(
                clarificationKind,
                pendingClarification.Consumed
                    ? effectiveUserMessage
                    : userMessage,
                plan.Clarification?.AmbiguityKind
                    ?? docResolution.ClarificationHint,
                plan.Language,
                plan.Clarification);
            onProgress?.Invoke(string.Empty);
            EmitRagTrace(
                "clarification.requested",
                ("path", "router_before_tools"),
                ("origin", plan.Origin.ToString()),
                ("intent", plan.Intent),
                ("kind", clarificationKind),
                ("ambiguity_kind", plan.Clarification?.AmbiguityKind),
                ("resume_route", plan.Clarification?.ResumeRoute),
                ("option_count", plan.Clarification?.Options.Count ?? 0),
                ("execution_impact", plan.Clarification?.ExecutionImpact));
            return FinalizeAndReturn(
                swTotalPipeline,
                userMessage,
                clarification,
                null,
                "clarification",
                Array.Empty<string>(),
                _mem.LastReasoningTracePublic,
                clearPendingClarification: false);
        }

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

        var standaloneFallbackSourceBackedPipeline = await TryHandleStandaloneFallbackSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (standaloneFallbackSourceBackedPipeline.handled)
            return (standaloneFallbackSourceBackedPipeline.finalAnswer, standaloneFallbackSourceBackedPipeline.sourcesPayload);

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

        if (ShouldPreserveCanonicalSourceBackedRouterDecision(
                plan,
                IsSourceBackedAgentV2Available()))
        {
            EmitRagTrace(
                "router.llm_source_backed_decision.preserved",
                ("reason", "canonical_agent_owns_semantic_adaptation"),
                ("tools", plan.ToolCalls
                    .Select(static call => call.Name)
                    .ToArray()));
        }
        else
        {
            ApplyDocumentaryRagDefaults(plan, effectiveUserMessage);
        }

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

        var sourceBackedPipeline = await TryHandleSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (sourceBackedPipeline.handled)
            return (sourceBackedPipeline.finalAnswer, sourceBackedPipeline.sourcesPayload);

        var versionTraceabilitySourceBackedPipeline = await TryHandleDocumentVersionTraceabilityFallbackSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (versionTraceabilitySourceBackedPipeline.handled)
            return (versionTraceabilitySourceBackedPipeline.finalAnswer, versionTraceabilitySourceBackedPipeline.sourcesPayload);

        var exactItemSourceBackedPipeline = await TryHandleExactItemFallbackSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (exactItemSourceBackedPipeline.handled)
            return (exactItemSourceBackedPipeline.finalAnswer, exactItemSourceBackedPipeline.sourcesPayload);

        var documentaryProbeSourceBackedPipeline = await TryHandleDocumentaryProbeFallbackSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (documentaryProbeSourceBackedPipeline.handled)
            return (documentaryProbeSourceBackedPipeline.finalAnswer, documentaryProbeSourceBackedPipeline.sourcesPayload);

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
        if (IsSourceBackedAgentV2Available())
        {
            EmitRagTrace(
                "router.legacy_structured_repair.skipped",
                ("reason", "source_backed_agent_v2_owns_semantic_adaptation"));
        }
        else
        {
            plan = await TryRepairStructuredRouterSearchPlanAsync(
                    plan,
                    effectiveUserMessage,
                    ct)
                .ConfigureAwait(false);
        }

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

        var postToolRagSourceBackedPipeline = await TryHandlePostToolRagResultsSourceBackedRagPipelineAsync(
            userMessage,
            effectiveUserMessage,
            plan,
            toolResults,
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (postToolRagSourceBackedPipeline.handled)
            return (postToolRagSourceBackedPipeline.finalAnswer, postToolRagSourceBackedPipeline.sourcesPayload);

        EmitRagTrace(
            "source_backed_pipeline.canonical_retrieval.owner_confirmed",
            ("reason", "canonical_pipeline_owns_post_tool_rag"),
            ("intent", plan.Intent),
            ("tool_items", toolResults.Items.Count));

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
        var sourceBackedWriterTimeoutMs = ResolveSourceBackedPlanningWriterTimeoutMs(effectiveUserMessage);
        var useStageWideSourceBackedWriterTimeout =
            shouldBufferWriterOutputForSourceBackedGuard
            && ShouldUseStageWideSourceBackedWriterTimeout(effectiveUserMessage);
        try
        {
            using var sourceBackedWriterTimeoutCts = useStageWideSourceBackedWriterTimeout
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (sourceBackedWriterTimeoutCts is not null)
                sourceBackedWriterTimeoutCts.CancelAfter(sourceBackedWriterTimeoutMs);

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
        catch (OperationCanceledException) when (useStageWideSourceBackedWriterTimeout && !ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "writer.stage.timeout",
                ("intent", plan.Intent),
                ("timeout_ms", sourceBackedWriterTimeoutMs),
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
        var shouldFinalizeStructuredPlanningRejection =
            ShouldTreatStructuredPlanningRejectionAsTerminal(_lastAnswerSource, effectiveUserMessage);
        if (shouldFinalizeStructuredPlanningRejection)
        {
            sources = new List<ToolMemory.SourceRef>();
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
            EmitRagTrace(
                "writer.structured_terminal_rejection.accepted",
                ("intent", plan.Intent),
                ("answer_source", _lastAnswerSource),
                ("answer_chars", answer.Length));
        }

        if (!shouldFinalizeStructuredPlanningRejection
            && toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search")
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
                        EmitRagTrace(
                            "writer.no_rag_data.raw_fallback.skipped",
                            ("reason", "canonical_writer_required_no_raw_deterministic_rebuild"),
                            ("intent", plan.Intent),
                            ("sources", repairedSources.Count));
                        answer = BuildSourceBackedSafeFallbackAnswer(
                            toolResults,
                            effectiveUserMessage,
                            plan.Language,
                            shouldAvoidRaw: true);
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
            && !shouldFinalizeStructuredPlanningRejection
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
            && !shouldFinalizeStructuredPlanningRejection
            && shouldBufferWriterOutputForSourceBackedGuard
            && ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            && toolResults.Items.Any(static x => x.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(x.Result)))
        {
            if (TryGetVisibleSourceCitedStructuredPlanningSources(
                    answer,
                    toolResults,
                    effectiveUserMessage,
                    out var visibleCitedStructuredSources,
                    plan.Language))
            {
                sources = visibleCitedStructuredSources;
                _lastAnswerSource = $"router+tools_structured_planning_visible_cited_writer:{plan.Intent}";
                EmitRagTrace(
                    "writer.structured_visible_source_citations.accepted",
                    ("intent", plan.Intent),
                    ("items", 0),
                    ("candidates", 0),
                    ("sources", visibleCitedStructuredSources.Count),
                    ("position", "router_structured_guard"));
            }
            else
            {
                var structuredSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                    answer,
                    toolResults,
                    effectiveUserMessage,
                    plan.Language);
                if (!ShouldRejectUnsupportedPlanningAnswerForFinal(structuredSupport, effectiveUserMessage)
                    && structuredSupport.Sources.Count > 0)
                {
                    sources = structuredSupport.Sources.ToList();
                }
                else
                {
                    var repairSupport = PlanningAnswerSupportAnalysis.Empty;
                    var repairAccepted = false;
                    var repairAcceptedByVisibleCitations = false;
                    var repairAcceptedByCandidateSupport = false;
                    var repairAnswerForTrace = string.Empty;
                    if (ShouldAllowSourceBackedWriterRepairForCurrentTurn(effectiveUserMessage))
                    {
                        var repairSw = Stopwatch.StartNew();
                        EmitRagTrace(
                            "writer.structured_support_repair.start",
                            ("intent", plan.Intent),
                            ("items", structuredSupport.ItemCount),
                            ("supported", structuredSupport.SupportedItemCount),
                            ("unsupported", structuredSupport.UnsupportedItemCount),
                            ("candidates", structuredSupport.CandidateCount),
                            ("sources", structuredSupport.Sources.Count));
                        var repairAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                                chatHistory,
                                effectiveUserMessage,
                                plan,
                                toolResults,
                                ct)
                            .ConfigureAwait(false);
                        repairAnswer = RemoveTrailingModelEmittedSourceList(repairAnswer ?? string.Empty);
                        repairAnswerForTrace = repairAnswer ?? string.Empty;
                        repairAcceptedByVisibleCitations = TryGetVisibleSourceCitedStructuredPlanningSources(
                            repairAnswer,
                            toolResults,
                            effectiveUserMessage,
                            out var repairVisibleCitedSources,
                            plan.Language);
                        if (!repairAcceptedByVisibleCitations)
                        {
                            repairSupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                repairAnswer,
                                toolResults,
                                effectiveUserMessage,
                                plan.Language);
                        }
                        repairAcceptedByCandidateSupport = !string.IsNullOrWhiteSpace(repairAnswer)
                            && !LooksLikeWriterControlLeak(repairAnswer)
                            && !LooksLikePoorPlanningFallbackAnswer(repairAnswer, effectiveUserMessage)
                            && repairSupport.Sources.Count > 0
                            && !ShouldRejectUnsupportedPlanningAnswerForFinal(repairSupport, effectiveUserMessage);
                        repairAccepted = repairAcceptedByVisibleCitations || repairAcceptedByCandidateSupport;
                        EmitRagTrace(
                            "writer.structured_support_repair.end",
                            ("intent", plan.Intent),
                            ("accepted", repairAccepted),
                            ("accepted_by_visible_citations", repairAcceptedByVisibleCitations),
                            ("answer_chars", repairAnswer?.Length ?? 0),
                            ("items", repairSupport.ItemCount),
                            ("supported", repairSupport.SupportedItemCount),
                            ("unsupported", repairSupport.UnsupportedItemCount),
                            ("candidates", repairSupport.CandidateCount),
                            ("sources", repairAcceptedByVisibleCitations ? repairVisibleCitedSources.Count : repairSupport.Sources.Count),
                            ("ms", repairSw.ElapsedMilliseconds));
                        if (!repairAccepted
                            && ShouldRetryStructuredPlanningRepairAfterSourceGuardFailure(repairAnswer, effectiveUserMessage))
                        {
                            var retryFeedback = BuildStructuredPlanningRepairFeedback(
                                repairAnswer,
                                toolResults,
                                effectiveUserMessage,
                                plan.Language);
                            var retrySw = Stopwatch.StartNew();
                            EmitRagTrace(
                                "writer.structured_support_repair.retry.start",
                                ("intent", plan.Intent),
                                ("answer_chars", repairAnswer?.Length ?? 0),
                                ("feedback_chars", retryFeedback.Length));
                            var retryAnswer = await TryRepairSourceBackedSynthesisAnswerWithWriterAsync(
                                    chatHistory,
                                    effectiveUserMessage,
                                    plan,
                                    toolResults,
                                    ct,
                                    retryFeedback)
                                .ConfigureAwait(false);
                            retryAnswer = RemoveTrailingModelEmittedSourceList(retryAnswer ?? string.Empty);
                            var retryAcceptedByVisibleCitations = TryGetVisibleSourceCitedStructuredPlanningSources(
                                retryAnswer,
                                toolResults,
                                effectiveUserMessage,
                                out var retryVisibleCitedSources,
                                plan.Language);
                            var retrySupport = PlanningAnswerSupportAnalysis.Empty;
                            if (!retryAcceptedByVisibleCitations)
                            {
                                retrySupport = AnalyzeSourceBackedPlanningAnswerSupport(
                                    retryAnswer,
                                    toolResults,
                                    effectiveUserMessage,
                                    plan.Language);
                            }
                            var retryAcceptedByCandidateSupport = !string.IsNullOrWhiteSpace(retryAnswer)
                                && !LooksLikeWriterControlLeak(retryAnswer)
                                && !LooksLikePoorPlanningFallbackAnswer(retryAnswer, effectiveUserMessage)
                                && retrySupport.Sources.Count > 0
                                && !ShouldRejectUnsupportedPlanningAnswerForFinal(retrySupport, effectiveUserMessage);
                            var retryAccepted = retryAcceptedByVisibleCitations || retryAcceptedByCandidateSupport;
                            retrySw.Stop();
                            EmitRagTrace(
                                "writer.structured_support_repair.retry.end",
                                ("intent", plan.Intent),
                                ("accepted", retryAccepted),
                                ("accepted_by_visible_citations", retryAcceptedByVisibleCitations),
                                ("answer_chars", retryAnswer?.Length ?? 0),
                                ("items", retrySupport.ItemCount),
                                ("supported", retrySupport.SupportedItemCount),
                                ("unsupported", retrySupport.UnsupportedItemCount),
                                ("candidates", retrySupport.CandidateCount),
                                ("sources", retryAcceptedByVisibleCitations ? retryVisibleCitedSources.Count : retrySupport.Sources.Count),
                                ("ms", retrySw.ElapsedMilliseconds));
                            if (retryAccepted)
                            {
                                repairAnswer = retryAnswer ?? string.Empty;
                                repairAnswerForTrace = repairAnswer;
                                repairSupport = retrySupport;
                                repairAcceptedByVisibleCitations = retryAcceptedByVisibleCitations;
                                repairAcceptedByCandidateSupport = retryAcceptedByCandidateSupport;
                                repairAccepted = true;
                                repairVisibleCitedSources = retryVisibleCitedSources;
                            }
                        }

                        if (repairAccepted)
                        {
                            answer = repairAnswer ?? string.Empty;
                            sources = repairAcceptedByVisibleCitations
                                ? repairVisibleCitedSources
                                : repairSupport.Sources.ToList();
                            _lastAnswerSource = repairAcceptedByVisibleCitations
                                ? $"router+tools_structured_planning_visible_cited_writer_repair:{plan.Intent}"
                                : $"router+tools_structured_planning_supported_writer_repair:{plan.Intent}";
                        }
                    }

                    if (!repairAccepted)
                    {
                        var searchAlreadyExpanded = HasExpandedSourceBackedSearchEvidence(toolResults);
                        EmitRagTrace(
                            "writer.structured_support_repair.post_rebuild.skipped",
                            ("reason", "canonical_writer_repair_no_deterministic_post_rebuild"),
                            ("intent", plan.Intent),
                            ("items", repairSupport.ItemCount),
                            ("supported", repairSupport.SupportedItemCount),
                            ("candidates", Math.Max(structuredSupport.CandidateCount, repairSupport.CandidateCount)));
                        EmitPlanningInsufficientFallbackTrace(
                            "router-structured-guard",
                            plan.Intent,
                            "writer_repair_rejected_without_deterministic_rebuild",
                            Math.Max(structuredSupport.CandidateCount, repairSupport.CandidateCount),
                            searchAlreadyExpanded);
                        answer = BuildBroadEvidenceStillInsufficientAnswer(
                            plan.Language,
                            effectiveUserMessage,
                            effectiveUserMessage,
                            Math.Max(structuredSupport.CandidateCount, repairSupport.CandidateCount),
                            searchAlreadyExpanded: searchAlreadyExpanded);
                        sources = new List<ToolMemory.SourceRef>();
                        _lastAnswerSource = $"router+tools_structured_planning_rejected_unsupported_without_post_rebuild:{plan.Intent}";
                        LogSourceBackedPlanningTrace(
                            "structured-planning-rejected-unsupported-without-post-rebuild",
                            toolResults,
                            effectiveUserMessage,
                            plan.Language);
                    }
                }
            }
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

}
