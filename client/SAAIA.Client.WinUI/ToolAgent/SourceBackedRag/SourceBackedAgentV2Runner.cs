using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    public async Task<SourceBackedPipelineResult> RunAsync(
        SourceBackedIntake intake, CancellationToken ct)
    {
        using var budgetScope = SourceBackedRunBudgetScope.Enter(_options);
        var lifecycle = new AgentRunLifecycle(transition => ClientDiagnostics.Journal.Emit(
            "agent.lifecycle." + transition.To.ToString().ToLowerInvariant(),
            transition.To == AgentRunPhase.Failed ? "failed" : transition.To == AgentRunPhase.Cancelled ? "cancelled" : "success",
            transition.Reason, new Dictionary<string, long?> { ["iteration"] = transition.Iteration }));
        lifecycle.Initialize();
        try
        {
            var result = await RunCoreAsync(intake, lifecycle, ct).ConfigureAwait(false);
            lifecycle.Complete(result.IsSourceVerified ? "source_verified" : "safe_nonverified_result");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lifecycle.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            lifecycle.Fail(exception.GetType().Name);
            throw;
        }
    }
    private async Task<SourceBackedPipelineResult> RunCoreAsync(
        SourceBackedIntake intake, AgentRunLifecycle lifecycle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var runState = new RunExecutionState(intake, lifecycle)
        {
            traceId = "source-backed-agent-v2-" + Guid.NewGuid().ToString("N")[..10]
        };
        using var telemetryScope = SourceBackedTelemetryContext.Push(runState.traceId, runState.traceObserverHealth);
        try
        {
            await InitializeExecutionAsync(runState, ct).ConfigureAwait(false);
            for (runState.turn = 1; runState.turn <= runState.maximumRunTurns; runState.turn++)
            {
                var iteration = new IterationExecutionState();
                var outcome = await ExecuteIterationAsync(runState, iteration, ct).ConfigureAwait(false);
                switch (outcome.Disposition)
                {
                    case AgentIterationDisposition.Complete:
                        return outcome.Result!;
                    case AgentIterationDisposition.Continue:
                    case AgentIterationDisposition.Advance:
                        continue;
                    case AgentIterationDisposition.Stop:
                        break;
                    default:
                        throw new InvalidOperationException("unknown_agent_iteration_outcome");
                }
                break;
            }

            return BuildResult(
                runState.intake,
                runState.evidenceState.ExecutedRequests,
                runState.bundle,
                runState.firstDraft,
                runState.latestDraft,
                runState.verification,
                runState.repaired,
                runState.semanticAccepted,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.semanticReviewReasons,
                runState.traces);

        }
        finally
        {
            var traceFailures = runState.traceObserverHealth.TraceSinkFailures;
            var phaseFailures = runState.phaseObserverHealth.ObserverFailures;
            ClientDiagnostics.Journal.Emit("agent.collector_health", traceFailures + phaseFailures > 0 ? "degraded" : "success",
                traceFailures + phaseFailures > 0 ? "observer_failed" : "no_observer_failures",
                new Dictionary<string, long?> { ["trace_sink_failures"] = traceFailures, ["phase_observer_failures"] = phaseFailures,
                    ["trace_sink_configured"] = _traceSink is null ? 0 : 1 });
        }
    }

    private async Task<AgentIterationOutcome<SourceBackedPipelineResult>> ExecuteIterationAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        try
        {
            return await AgentIterationPipeline.RunAsync(runState, iteration,
            new AgentIterationStep<RunExecutionState, IterationExecutionState, SourceBackedPipelineResult>[]
            {
                new("prepare", PrepareIterationAsync),
                new("acquire_completion", AcquireIterationCompletionAsync),
                new("submissions_and_tools", ProcessIterationSubmissionsAndToolsAsync),
                new("verify_and_review", VerifyAndReviewIterationDraftAsync)
            }, ct, runState.phaseObserverHealth, observation => ClientDiagnostics.Journal.Emit("agent.iteration." + observation.Phase,
                observation.Outcome, observation.Reason, new Dictionary<string, long?>
                {
                    ["duration_ms"] = observation.ElapsedMilliseconds,
                    ["iteration"] = runState.lifecycle.Iteration
                })).ConfigureAwait(false);
        }
        finally
        {
            if (runState.phaseObserverHealth.ObserverFailures > 0)
                ClientDiagnostics.Journal.Emit("agent.iteration.collector_health", "unavailable", "observer_failed",
                    new Dictionary<string, long?> { ["observer_failures"] = runState.phaseObserverHealth.ObserverFailures });
        }
    }
}
