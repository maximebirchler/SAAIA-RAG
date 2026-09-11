using System.Diagnostics;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> CompleteVerifiedSourceBackedPipelineAsync(
        string displayUserMessage,
        string pipelineIntent,
        string entryPoint,
        Stopwatch swTotalPipeline,
        SourceBackedPipelineResult result,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(language));
        var payload = SourceBackedUiPayloadMapper.FromVerifiedResult(result);
        var sources = payload.Sources.ToList();
        _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
        RememberUniqueSourceBackedFocusedDocument(_mem.LastSourcesUsed);
        _mem.LastToolNames = result.RetrievalPlan?.Requests
            .Select(static request => request.ToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        _mem.LastRagQueries = result.RetrievalPlan?.Requests
            .Select(static request => request.Query)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .ToList() ?? new List<string>();
        _mem.LastRagTraceEvents = result.TraceEvents
            .OrderBy(static trace => trace.Sequence)
            .Select(FormatSourceBackedMemoryTraceEvent)
            .ToList();
        _mem.RememberSourceBackedConversationTurn(result.ConversationMemory);
        _lastAnswerSource = $"source_backed_pipeline:{entryPoint}:{pipelineIntent}";

        var sourcesPayload = _mem.LastSourcesUsed.Count > 0
                             || result.ConversationMemory is not null
            ? BuildSourcesPayload(
                pipelineIntent,
                _mem.LastSourcesUsed,
                result.ConversationMemory)
            : null;
        await EmitDeterministicTextAsync(payload.Answer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        EmitRagTrace(
            "source_backed_pipeline.active.accepted",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("answer_chars", payload.Answer.Length),
            ("evidence_items", result.EvidenceBundle.Items.Count),
            ("sources", _mem.LastSourcesUsed.Count),
            ("was_repaired", result.WasRepaired));

        var finalized = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            payload.Answer,
            sourcesPayload,
            pipelineIntent,
            _mem.LastToolNames,
            _mem.LastReasoningTracePublic);
        return (true, finalized.finalAnswer, finalized.sourcesPayload);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> CompleteTerminalSourceBackedPipelineAsync(
        string displayUserMessage,
        string pipelineIntent,
        string entryPoint,
        Stopwatch swTotalPipeline,
        SourceBackedPipelineResult result,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var terminalAnswer = SourceBackedTerminalAnswer.Build(result);
        var isClarification = string.Equals(
            result.JudgeDecision.Decision,
            "clarify",
            StringComparison.OrdinalIgnoreCase)
            && result.Clarification is not null;
        var terminalIntent = ResolveTerminalSourceBackedIntent(
            pipelineIntent,
            result.JudgeDecision.Decision,
            isClarification);
        _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        _mem.LastToolNames = result.RetrievalPlan?.Requests
            .Select(static request => request.ToolName)
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        _mem.LastRagQueries = result.RetrievalPlan?.Requests
            .Select(static request => request.Query)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .ToList() ?? new List<string>();
        _mem.LastRagTraceEvents = result.TraceEvents
            .OrderBy(static trace => trace.Sequence)
            .Select(FormatSourceBackedMemoryTraceEvent)
            .ToList();
        _mem.RememberSourceBackedConversationTurn(result.ConversationMemory);
        _lastAnswerSource = $"source_backed_pipeline_terminal:{entryPoint}:{terminalIntent}";
        if (isClarification)
        {
            RememberPendingClarification(
                "rag_probe",
                displayUserMessage,
                result.Clarification!.AmbiguityKind,
                result.Intake.Language,
                new RouterPlan.ClarificationDecisionPlan
                {
                    Message = result.Clarification.Message,
                    Options = result.Clarification.Options.ToList(),
                    ExecutionImpact =
                        result.Clarification.ExecutionImpact,
                    ResumeRoute = "source_backed",
                    AmbiguityKind =
                        result.Clarification.AmbiguityKind
                });
        }

        var sourcesPayload = result.ConversationMemory is null
            ? null
            : BuildSourcesPayload(
                terminalIntent,
                _mem.LastSourcesUsed,
                result.ConversationMemory);

        await EmitDeterministicTextAsync(terminalAnswer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        EmitRagTrace(
            "source_backed_pipeline.active.terminal",
            ("intent", pipelineIntent),
            ("final_intent", terminalIntent),
            ("entry", entryPoint),
            ("decision", result.JudgeDecision.Decision),
            ("clarification", isClarification),
            ("clarification_kind",
                result.Clarification?.AmbiguityKind),
            ("clarification_options",
                result.Clarification?.Options.Count ?? 0),
            ("evidence_items", result.EvidenceBundle.Items.Count),
            ("verification_errors", result.Verification?.Errors.Count ?? 0));

        var finalized = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            terminalAnswer,
            sourcesPayload,
            terminalIntent,
            _mem.LastToolNames,
            _mem.LastReasoningTracePublic,
            clearPendingClarification: !isClarification);
        return (true, finalized.finalAnswer, finalized.sourcesPayload);
    }

    private static string ResolveTerminalSourceBackedIntent(
        string pipelineIntent,
        string? judgeDecision,
        bool isClarification)
    {
        if (isClarification)
            return "clarification";

        return string.Equals(
            judgeDecision,
            "insufficient_evidence",
            StringComparison.OrdinalIgnoreCase)
                ? "insufficient_evidence"
                : pipelineIntent;
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> CompleteFailedSourceBackedPipelineAsync(
        string displayUserMessage,
        string pipelineIntent,
        string entryPoint,
        string language,
        Stopwatch swTotalPipeline,
        Exception exception,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var terminalAnswer = SourceBackedTerminalAnswer.BuildFailure(language);
        _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        _mem.LastToolNames = new List<string>();
        _mem.LastRagQueries = new List<string>();
        var errorMessage = FormatPipelineErrorForTrace(exception);
        EmitRagTrace(
            "source_backed_pipeline.error",
            ("error_type", exception.GetType().Name),
            ("error_message", errorMessage));
        _lastAnswerSource = $"source_backed_pipeline_failed:{entryPoint}:{pipelineIntent}";

        await EmitDeterministicTextAsync(terminalAnswer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        EmitRagTrace(
            "source_backed_pipeline.active.terminal",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("decision", "pipeline_failure"),
            ("error_type", exception.GetType().Name),
            ("error_message", errorMessage));

        var finalized = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            terminalAnswer,
            null,
            pipelineIntent,
            _mem.LastToolNames,
            _mem.LastReasoningTracePublic);
        return (true, finalized.finalAnswer, finalized.sourcesPayload);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> CompleteAdvancedSourceBackedBudgetHandoffAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        string pipelineIntent,
        string entryPoint,
        Stopwatch swTotalPipeline,
        SourceBackedLlmBudgetExceededException exception,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var handoff = SourceBackedBudgetHandoffContract.Create(routerPlan.Language);
        var boundary = EvaluateLocalCapabilityBoundary(
            routerPlan,
            effectiveUserMessage,
            _mem.LastSourcesUsed is { Count: > 0 });
        BuildAndRememberAdvancedAnalysisHandoff(
            routerPlan,
            effectiveUserMessage,
            handoff.ReasonCode,
            "after_local_budget",
            boundary.AnswerUnitCount,
            MapAdvancedAnalysisBudgetSnapshot(exception));
        _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        _mem.LastToolNames = new List<string>();
        _mem.LastRagQueries = new List<string>();
        _lastAnswerSource =
            $"capability_boundary:advanced_analysis_required_after_budget:{entryPoint}:{pipelineIntent}";

        EmitRagTrace(
            "source_backed_pipeline.budget_exhausted",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("decision", handoff.Intent),
            ("reason", handoff.ReasonCode),
            ("budget_reason", exception.Reason),
            ("maximum_tokens", exception.Snapshot.MaximumTokens),
            ("charged_tokens", exception.Snapshot.ChargedTokens),
            ("reserved_tokens", exception.Snapshot.ReservedTokens),
            ("remaining_tokens", exception.Snapshot.RemainingTokens),
            ("remaining_ms", exception.Snapshot.RemainingMilliseconds),
            ("admitted_calls", exception.Snapshot.AdmittedCalls),
            ("completed_calls", exception.Snapshot.CompletedCalls),
            ("failed_calls", exception.Snapshot.FailedCalls),
            ("visible_sources", handoff.HasVisibleSources));

        await EmitDeterministicTextAsync(handoff.Answer, onDelta, ct)
            .ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        EmitRagTrace(
            "source_backed_pipeline.active.terminal",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("decision", handoff.Intent),
            ("reason", handoff.ReasonCode),
            ("visible_sources", handoff.HasVisibleSources));

        var finalized = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            handoff.Answer,
            null,
            handoff.Intent,
            _mem.LastToolNames,
            _mem.LastReasoningTracePublic);
        return (true, finalized.finalAnswer, finalized.sourcesPayload);
    }

    internal Task<(bool handled, string finalAnswer, object? sourcesPayload)>
        CompleteAdvancedSourceBackedBudgetHandoffForTestsAsync(
            string requestText,
            RouterPlan routerPlan,
            SourceBackedLlmBudgetExceededException exception)
        => CompleteAdvancedSourceBackedBudgetHandoffAsync(
            requestText,
            requestText,
            routerPlan,
            routerPlan.Intent,
            "test",
            Stopwatch.StartNew(),
            exception,
            CancellationToken.None,
            onDelta: null,
            onProgress: null);

    private static string FormatPipelineErrorForTrace(Exception exception)
    {
        var baseException = exception.GetBaseException();
        var message = string.IsNullOrWhiteSpace(baseException.Message)
            ? exception.Message
            : baseException.Message;
        return FormatRagTraceValue(message, 220);
    }
}
