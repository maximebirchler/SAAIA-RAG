using System.Diagnostics;
using System.IO;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static RouterPlan BuildSourceBackedRouterPlanForTests(SourceBackedIntake intake, RetrievalPlan plan)
        => SourceBackedRouterPlanAdapter.ToRouterPlan(intake, plan);

    internal Task<ToolResults> ExecuteSourceBackedRetrievalPlanForTests(
        SourceBackedIntake intake,
        RetrievalPlan plan,
        CancellationToken ct)
        => new OrchestratorSourceBackedRagToolExecutor(this, onPhase: null, onProgress: null)
            .ExecuteAsync(intake, plan, ct);

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldUseSourceBackedRagPipeline(routerPlan))
            return (false, string.Empty, null);

        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            routerPlan.Intent,
            "router_plan",
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleStandaloneFallbackSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldRouteStandaloneFallbackThroughSourceBackedRagPipeline(effectiveUserMessage, routerPlan))
            return (false, string.Empty, null);

        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            "rag.answer",
            "standalone_fallback",
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleDocumentaryProbeFallbackSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldRunDocumentaryProbe(effectiveUserMessage, routerPlan))
            return (false, string.Empty, null);

        return await RunSourceBackedRagPipelineAsync(
            displayUserMessage,
            effectiveUserMessage,
            routerPlan,
            "rag.answer",
            "documentary_probe_fallback",
            swTotalPipeline,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> RunSourceBackedRagPipelineAsync(
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan routerPlan,
        string pipelineIntent,
        string entryPoint,
        Stopwatch swTotalPipeline,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var allowsPartialAnswer = ShouldAllowSourceBackedPartialAnswer(routerPlan);
        EmitRagTrace(
            "source_backed_pipeline.active.start",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("allows_partial_answer", allowsPartialAnswer),
            ("router_tools", routerPlan.ToolCalls.Select(static call => call.Name).ToArray()));

        onPhase?.Invoke(DeterministicAgentText.PhaseTools(routerPlan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(routerPlan.Language));

        var catalogContextSw = Stopwatch.StartNew();
        try
        {
            await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
            EmitRagTrace(
                "source_backed_catalog_context.ready",
                ("categories", _mem.CatalogSnapshotCache?.Categories?.Count ?? 0),
                ("ms", catalogContextSw.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "source_backed_catalog_context.failed",
                ("error", TruncateForPrompt(ex.Message, 220)),
                ("ms", catalogContextSw.ElapsedMilliseconds));
        }

        var intake = new SourceBackedIntake(
            effectiveUserMessage,
            pipelineIntent,
            routerPlan.RiskFlags?.ToArray() ?? Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: allowsPartialAnswer,
            routerPlan.Language,
            BuildSourceBackedCatalogHints(),
            BuildSourceBackedMemoryContext(),
            QuestionFocus: NullIfBlankSourceBackedMissionValue(
                routerPlan.SourceBackedMission?.QuestionFocus),
            RequestedDocumentName: ResolveSourceBackedRequestedDocumentName(
                effectiveUserMessage,
                routerPlan),
            InitialToolCalls: BuildSourceBackedRouterInitialActions(routerPlan),
            InitialSemanticMission:
            BuildSourceBackedRouterSemanticMission(routerPlan),
            NamedReferenceKind: ResolveSourceBackedNamedReferenceKind(
                effectiveUserMessage,
                routerPlan));
        SourceBackedPipelineResult result;
        try
        {
            result = await RunSourceBackedAgentV2Async(
                    intake,
                    pipelineIntent,
                    entryPoint,
                    ct,
                    onPhase,
                    onProgress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceBackedLlmBudgetExceededException ex)
        {
            return await CompleteAdvancedSourceBackedBudgetHandoffAsync(
                displayUserMessage,
                effectiveUserMessage,
                routerPlan,
                pipelineIntent,
                entryPoint,
                swTotalPipeline,
                ex,
                ct,
                onDelta,
                onProgress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "source_backed_pipeline.active.failed",
                ("intent", pipelineIntent),
                ("entry", entryPoint),
                ("error", ex.Message));
            return await CompleteFailedSourceBackedPipelineAsync(
                displayUserMessage,
                pipelineIntent,
                entryPoint,
                routerPlan.Language,
                swTotalPipeline,
                ex,
                ct,
                onDelta,
                onProgress).ConfigureAwait(false);
        }

        EmitSourceBackedPipelineTraceEvents(result);

        if (!result.IsSourceVerified || string.IsNullOrWhiteSpace(result.Answer))
        {
            EmitRagTrace(
                "source_backed_pipeline.active.skipped",
                ("intent", pipelineIntent),
                ("entry", entryPoint),
                ("verified", result.IsSourceVerified),
                ("decision", result.JudgeDecision.Decision),
                ("evidence_items", result.EvidenceBundle.Items.Count),
                ("verification_errors", result.Verification?.Errors.Count ?? 0));
            return await CompleteTerminalSourceBackedPipelineAsync(
                displayUserMessage,
                pipelineIntent,
                entryPoint,
                swTotalPipeline,
                result,
                ct,
                onDelta,
                onProgress).ConfigureAwait(false);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseWriting(routerPlan.Language));
        return await CompleteVerifiedSourceBackedPipelineAsync(
            displayUserMessage,
            pipelineIntent,
            entryPoint,
            swTotalPipeline,
            result,
            routerPlan.Language,
            ct,
            onDelta,
            onProgress).ConfigureAwait(false);
    }

}
