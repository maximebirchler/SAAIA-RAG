using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RuntimeGovernanceTelemetryTests
{
    [Fact]
    public void Warmup_and_selection_emit_runtime_governance_metrics_and_spans()
    {
        var metricNames = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeGovernanceTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.Start();

        var stoppedActivities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeGovernanceTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        using (var warmup = RuntimeGovernanceTelemetry.StartWarmupCheckActivity("core.retrieval", "default-local"))
        {
            RuntimeGovernanceTelemetry.CompleteWarmupCheck(
                warmup,
                "core.retrieval",
                "default-local",
                passNumber: 1,
                passed: true,
                hardGatesPassed: true,
                runtimeGatesPassed: true,
                performanceBudgetPassed: true,
                durationMs: 42,
                qdrantDurationMs: 11,
                embeddingsDurationMs: 21,
                rerankDurationMs: 0);
        }

        using (var budgetFailedWarmup = RuntimeGovernanceTelemetry.StartWarmupCheckActivity("core.retrieval", "strict-local"))
        {
            RuntimeGovernanceTelemetry.CompleteWarmupCheck(
                budgetFailedWarmup,
                "core.retrieval",
                "strict-local",
                passNumber: 2,
                passed: false,
                hardGatesPassed: true,
                runtimeGatesPassed: false,
                performanceBudgetPassed: false,
                durationMs: 84,
                qdrantDurationMs: 25,
                embeddingsDurationMs: 33,
                rerankDurationMs: 0);
        }

        using (var selection = RuntimeGovernanceTelemetry.StartCapabilitySelectActivity("core.retrieval"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilitySelect(
                selection,
                "core.retrieval",
                desiredEnabled: true,
                authorized: true,
                selected: true,
                success: true,
                durationMs: 7);
        }

        using (var failedSelection = RuntimeGovernanceTelemetry.StartCapabilitySelectActivity("core.retrieval"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilitySelect(
                failedSelection,
                "core.retrieval",
                desiredEnabled: false,
                authorized: true,
                selected: false,
                success: false,
                durationMs: 5,
                errorReason: "capability must be desired-enabled before it can be authorized");
        }

        using (var artifact = RuntimeGovernanceTelemetry.StartArtifactReadActivity("runtime_catalog.json"))
        {
            RuntimeGovernanceTelemetry.CompleteArtifactRead(
                artifact,
                "runtime_catalog.json",
                success: true,
                durationMs: 3);
        }

        using (var capabilityACandidates = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_candidates"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                capabilityACandidates,
                "capability_a_candidates",
                success: true,
                durationMs: 9,
                candidateCount: 3);
        }

        using (var capabilityAEnqueue = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_enqueue"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                capabilityAEnqueue,
                "capability_a_enqueue",
                success: true,
                durationMs: 13,
                queuedCount: 2,
                skippedCount: 1);
        }

        using (var capabilityBCandidates = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_candidates"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                capabilityBCandidates,
                "capability_b_candidates",
                success: true,
                durationMs: 8,
                candidateCount: 4);
        }

        using (var capabilityBEnqueue = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_enqueue"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                capabilityBEnqueue,
                "capability_b_enqueue",
                success: true,
                durationMs: 15,
                queuedCount: 1,
                skippedCount: 3);
        }

        using (var capabilityBClaim = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_claim"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                capabilityBClaim,
                "capability_b_claim",
                success: true,
                durationMs: 4);
        }

        using (var capabilityBFail = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_fail"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                capabilityBFail,
                "capability_b_fail",
                success: true,
                durationMs: 5);
        }

        using (var capabilityBCompleted = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_summary_completed"))
        {
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                capabilityBCompleted,
                "capability_b_summary_completed",
                success: true,
                durationMs: 6);
        }

        RuntimeGovernanceTelemetry.RecordRequalifyRequest("core.retrieval", "default-local", 1);
        RuntimeGovernanceTelemetry.RecordStaleQualificationDetected("core.retrieval", "default-local", "qualification_inputs_changed");
        RuntimeGovernanceTelemetry.RecordCapabilityEventWritten("core.retrieval", "requalified");

        Assert.Contains("saaia.runtime.warmup.passes", metricNames);
        Assert.Contains("saaia.runtime.warmup.duration", metricNames);
        Assert.Contains("saaia.runtime.warmup.qdrant.duration", metricNames);
        Assert.Contains("saaia.runtime.warmup.embeddings.duration", metricNames);
        Assert.Contains("saaia.runtime.warmup.rerank.duration", metricNames);
        Assert.Contains("saaia.runtime.warmup.budget_failures", metricNames);
        Assert.Contains("saaia.runtime.warmup.runtime_gate_failures", metricNames);
        Assert.Contains("saaia.runtime.selection.updates", metricNames);
        Assert.Contains("saaia.runtime.selection.failures", metricNames);
        Assert.Contains("saaia.runtime.selection.duration", metricNames);
        Assert.Contains("saaia.runtime.artifact.reads", metricNames);
        Assert.Contains("saaia.runtime.artifact.duration", metricNames);
        Assert.Contains("saaia.runtime.capability_a.candidate_reads", metricNames);
        Assert.Contains("saaia.runtime.capability_a.enqueue.requests", metricNames);
        Assert.Contains("saaia.runtime.capability_a.queued_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_a.skipped_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_a.duration", metricNames);
        Assert.Contains("saaia.runtime.capability_a.candidate_count", metricNames);
        Assert.Contains("saaia.runtime.capability_b.candidate_reads", metricNames);
        Assert.Contains("saaia.runtime.capability_b.enqueue.requests", metricNames);
        Assert.Contains("saaia.runtime.capability_b.claim.requests", metricNames);
        Assert.Contains("saaia.runtime.capability_b.queued_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_b.failed_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_b.completed_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_b.skipped_docs", metricNames);
        Assert.Contains("saaia.runtime.capability_b.duration", metricNames);
        Assert.Contains("saaia.runtime.capability_b.candidate_count", metricNames);
        Assert.Contains("saaia.runtime.requalify.requests", metricNames);
        Assert.Contains("saaia.runtime.qualification.stale_detected", metricNames);
        Assert.Contains("saaia.runtime.event.writes", metricNames);

        RuntimeGovernanceTelemetry.RecordStaleQualificationReconciled("core.retrieval", "default-local", "qualification_inputs_changed");
        Assert.Contains("saaia.runtime.qualification.stale_reconciled", metricNames);

        var activitySnapshot = stoppedActivities.ToArray();

        var warmupActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "warmup_check"
            && Equals(activity.GetTagItem("saaia.runtime.profile_key"), "default-local"));
        Assert.Equal("core.retrieval", warmupActivity.GetTagItem("saaia.runtime.capability_key"));
        Assert.Equal("default-local", warmupActivity.GetTagItem("saaia.runtime.profile_key"));
        Assert.Equal(true, warmupActivity.GetTagItem("saaia.runtime.passed"));
        Assert.Equal(true, warmupActivity.GetTagItem("saaia.runtime.performance_budget_passed"));
        Assert.Equal(true, warmupActivity.GetTagItem("saaia.runtime.runtime_gates_passed"));
        Assert.Equal(11L, warmupActivity.GetTagItem("saaia.runtime.qdrant_duration_ms"));
        Assert.Equal(21L, warmupActivity.GetTagItem("saaia.runtime.embeddings_duration_ms"));

        var selectionActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_select"
            && Equals(activity.GetTagItem("saaia.runtime.success"), true));
        Assert.Equal(true, selectionActivity.GetTagItem("saaia.runtime.success"));
        Assert.Equal(true, selectionActivity.GetTagItem("saaia.runtime.selected"));

        var failedSelectionActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_select"
            && Equals(activity.GetTagItem("saaia.runtime.success"), false));
        Assert.Equal("capability must be desired-enabled before it can be authorized", failedSelectionActivity.GetTagItem("saaia.runtime.error_reason"));

        var artifactActivity = activitySnapshot.First(activity =>
            activity.OperationName == "runtime_artifact_read"
            && Equals(activity.GetTagItem("saaia.runtime.artifact_name"), "runtime_catalog.json"));
        Assert.Equal("runtime_catalog.json", artifactActivity.GetTagItem("saaia.runtime.artifact_name"));
        Assert.Equal(true, artifactActivity.GetTagItem("saaia.runtime.success"));

        var capabilityAActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_a_candidates"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_a.corpus_enrichment"));
        Assert.Equal(3, capabilityAActivity.GetTagItem("saaia.runtime.candidate_count"));

        var capabilityAEnqueueActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_a_enqueue"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_a.corpus_enrichment"));
        Assert.Equal(2, capabilityAEnqueueActivity.GetTagItem("saaia.runtime.queued_count"));
        Assert.Equal(1, capabilityAEnqueueActivity.GetTagItem("saaia.runtime.skipped_count"));

        var capabilityBActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_b_candidates"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_b.backoffice_generation"));
        Assert.Equal(4, capabilityBActivity.GetTagItem("saaia.runtime.candidate_count"));

        var capabilityBEnqueueActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_b_enqueue"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_b.backoffice_generation"));
        Assert.Equal(1, capabilityBEnqueueActivity.GetTagItem("saaia.runtime.queued_count"));
        Assert.Equal(3, capabilityBEnqueueActivity.GetTagItem("saaia.runtime.skipped_count"));

        var capabilityBClaimActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_b_claim"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_b.backoffice_generation"));
        Assert.Equal(true, capabilityBClaimActivity.GetTagItem("saaia.runtime.success"));

        var capabilityBFailActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_b_fail"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_b.backoffice_generation"));
        Assert.Equal(true, capabilityBFailActivity.GetTagItem("saaia.runtime.success"));

        var capabilityBCompletedActivity = Assert.Single(activitySnapshot, activity =>
            activity.OperationName == "capability_b_summary_completed"
            && Equals(activity.GetTagItem("saaia.runtime.capability_key"), "capability_b.backoffice_generation"));
        Assert.Equal(true, capabilityBCompletedActivity.GetTagItem("saaia.runtime.success"));
    }

    [Fact]
    public void EvaluateHardwareGate_returns_failure_when_thresholds_are_unreachable()
    {
        var result = RuntimeGovernanceService.EvaluateHardwareGate(new RuntimeGovernanceOptions
        {
            MinCpuCores = Environment.ProcessorCount + 1000,
            MinAvailableMemoryMb = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)) + 1024,
            Require64BitProcess = true
        });

        Assert.False(result.Passed);
        Assert.NotNull(result.Error);
        Assert.Contains("reasons", result.Details.Keys);
    }
}
