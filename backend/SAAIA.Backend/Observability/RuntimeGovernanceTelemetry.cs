using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceTelemetry
{
    public const string MeterName = "SAAIA.Backend.RuntimeGovernance";
    public const string ActivitySourceName = "SAAIA.Backend.RuntimeGovernance";

    private static readonly Meter Meter = new(MeterName);
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Counter<long> RequalifyRequests = Meter.CreateCounter<long>(
        "saaia.runtime.requalify.requests",
        unit: "{request}",
        description: "Number of runtime requalification requests.");

    private static readonly Counter<long> StaleQualificationDetections = Meter.CreateCounter<long>(
        "saaia.runtime.qualification.stale_detected",
        unit: "{event}",
        description: "Number of times a stale qualification was detected for a capability.");

    private static readonly Counter<long> StaleQualificationReconciliations = Meter.CreateCounter<long>(
        "saaia.runtime.qualification.stale_reconciled",
        unit: "{event}",
        description: "Number of stale qualifications that were reconciled by admin action.");

    private static readonly Counter<long> SelectionUpdates = Meter.CreateCounter<long>(
        "saaia.runtime.selection.updates",
        unit: "{request}",
        description: "Number of capability selection update requests.");

    private static readonly Counter<long> SelectionFailures = Meter.CreateCounter<long>(
        "saaia.runtime.selection.failures",
        unit: "{request}",
        description: "Number of rejected capability selection updates.");

    private static readonly Counter<long> ArtifactReads = Meter.CreateCounter<long>(
        "saaia.runtime.artifact.reads",
        unit: "{request}",
        description: "Number of runtime governance artifact reads.");

    private static readonly Counter<long> EventWrites = Meter.CreateCounter<long>(
        "saaia.runtime.event.writes",
        unit: "{event}",
        description: "Number of persisted runtime governance events.");

    private static readonly Counter<long> CapabilityACandidateReads = Meter.CreateCounter<long>(
        "saaia.runtime.capability_a.candidate_reads",
        unit: "{request}",
        description: "Number of capability A candidate read requests.");

    private static readonly Counter<long> CapabilityAEnqueueRequests = Meter.CreateCounter<long>(
        "saaia.runtime.capability_a.enqueue.requests",
        unit: "{request}",
        description: "Number of capability A enqueue requests.");

    private static readonly Counter<long> CapabilityAQueuedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_a.queued_docs",
        unit: "{document}",
        description: "Number of documents queued through capability A.");

    private static readonly Counter<long> CapabilityASkippedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_a.skipped_docs",
        unit: "{document}",
        description: "Number of documents skipped during capability A enqueue requests.");

    private static readonly Counter<long> WarmupPasses = Meter.CreateCounter<long>(
        "saaia.runtime.warmup.passes",
        unit: "{pass}",
        description: "Number of warmup passes executed.");

    private static readonly Counter<long> WarmupFailures = Meter.CreateCounter<long>(
        "saaia.runtime.warmup.failures",
        unit: "{pass}",
        description: "Number of failed warmup passes.");

    private static readonly Counter<long> WarmupBudgetFailures = Meter.CreateCounter<long>(
        "saaia.runtime.warmup.budget_failures",
        unit: "{pass}",
        description: "Number of warmup passes that exceeded performance budgets.");

    private static readonly Counter<long> WarmupRuntimeGateFailures = Meter.CreateCounter<long>(
        "saaia.runtime.warmup.runtime_gate_failures",
        unit: "{pass}",
        description: "Number of warmup passes that failed runtime-specific gates.");

    private static readonly Histogram<double> WarmupDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.warmup.duration",
        unit: "ms",
        description: "Warmup pass duration in milliseconds.");

    private static readonly Histogram<double> WarmupQdrantDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.warmup.qdrant.duration",
        unit: "ms",
        description: "Qdrant warmup check duration in milliseconds.");

    private static readonly Histogram<double> WarmupEmbeddingsDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.warmup.embeddings.duration",
        unit: "ms",
        description: "Embeddings warmup check duration in milliseconds.");

    private static readonly Histogram<double> WarmupRerankDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.warmup.rerank.duration",
        unit: "ms",
        description: "Rerank warmup check duration in milliseconds.");

    private static readonly Histogram<double> SelectionDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.selection.duration",
        unit: "ms",
        description: "Capability selection update duration in milliseconds.");

    private static readonly Histogram<double> ArtifactReadDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.artifact.duration",
        unit: "ms",
        description: "Runtime governance artifact read duration in milliseconds.");

    private static readonly Histogram<double> CapabilityAOperationDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.capability_a.duration",
        unit: "ms",
        description: "Capability A operation duration in milliseconds.");

    private static readonly Histogram<double> CapabilityACandidateCount = Meter.CreateHistogram<double>(
        "saaia.runtime.capability_a.candidate_count",
        unit: "{document}",
        description: "Number of candidates returned by capability A plan operations.");

    private static readonly Counter<long> CapabilityBCandidateReads = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.candidate_reads",
        unit: "{request}",
        description: "Number of capability B candidate read requests.");

    private static readonly Counter<long> CapabilityBEnqueueRequests = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.enqueue.requests",
        unit: "{request}",
        description: "Number of capability B enqueue requests.");

    private static readonly Counter<long> CapabilityBClaimRequests = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.claim.requests",
        unit: "{request}",
        description: "Number of capability B execution claim requests.");

    private static readonly Counter<long> CapabilityBQueuedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.queued_docs",
        unit: "{document}",
        description: "Number of documents queued through capability B.");

    private static readonly Counter<long> CapabilityBCompletedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.completed_docs",
        unit: "{document}",
        description: "Number of documents completed through capability B.");

    private static readonly Counter<long> CapabilityBSkippedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.skipped_docs",
        unit: "{document}",
        description: "Number of documents skipped during capability B enqueue requests.");

    private static readonly Counter<long> CapabilityBFailedDocs = Meter.CreateCounter<long>(
        "saaia.runtime.capability_b.failed_docs",
        unit: "{document}",
        description: "Number of documents failed during capability B execution.");

    private static readonly Histogram<double> CapabilityBOperationDurationMs = Meter.CreateHistogram<double>(
        "saaia.runtime.capability_b.duration",
        unit: "ms",
        description: "Capability B operation duration in milliseconds.");

    private static readonly Histogram<double> CapabilityBCandidateCount = Meter.CreateHistogram<double>(
        "saaia.runtime.capability_b.candidate_count",
        unit: "{document}",
        description: "Number of candidates returned by capability B plan operations.");

    internal static Activity? StartWarmupCheckActivity(string capabilityKey, string profileKey)
    {
        var activity = ActivitySource.StartActivity("warmup_check", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.runtime.capability_key", capabilityKey);
        activity.SetTag("saaia.runtime.profile_key", profileKey);
        return activity;
    }

    internal static void CompleteWarmupCheck(
        Activity? activity,
        string capabilityKey,
        string profileKey,
        int passNumber,
        bool passed,
        bool hardGatesPassed,
        bool runtimeGatesPassed,
        bool performanceBudgetPassed,
        long durationMs,
        long qdrantDurationMs,
        long embeddingsDurationMs,
        long rerankDurationMs)
    {
        activity?.SetTag("saaia.runtime.pass_number", passNumber);
        activity?.SetTag("saaia.runtime.passed", passed);
        activity?.SetTag("saaia.runtime.hard_gates_passed", hardGatesPassed);
        activity?.SetTag("saaia.runtime.runtime_gates_passed", runtimeGatesPassed);
        activity?.SetTag("saaia.runtime.performance_budget_passed", performanceBudgetPassed);
        activity?.SetTag("saaia.runtime.duration_ms", durationMs);
        activity?.SetTag("saaia.runtime.qdrant_duration_ms", qdrantDurationMs);
        activity?.SetTag("saaia.runtime.embeddings_duration_ms", embeddingsDurationMs);
        activity?.SetTag("saaia.runtime.rerank_duration_ms", rerankDurationMs);

        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.profile_key", profileKey },
            { "saaia.runtime.hard_gates_passed", hardGatesPassed },
            { "saaia.runtime.runtime_gates_passed", runtimeGatesPassed },
            { "saaia.runtime.performance_budget_passed", performanceBudgetPassed }
        };

        WarmupPasses.Add(1, tags);
        WarmupDurationMs.Record(durationMs, tags);
        WarmupQdrantDurationMs.Record(qdrantDurationMs, tags);
        WarmupEmbeddingsDurationMs.Record(embeddingsDurationMs, tags);
        WarmupRerankDurationMs.Record(rerankDurationMs, tags);
        if (!passed)
            WarmupFailures.Add(1, tags);
        if (!runtimeGatesPassed)
            WarmupRuntimeGateFailures.Add(1, tags);
        if (!performanceBudgetPassed)
            WarmupBudgetFailures.Add(1, tags);
    }

    internal static Activity? StartCapabilitySelectActivity(string capabilityKey)
    {
        var activity = ActivitySource.StartActivity("capability_select", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.runtime.capability_key", capabilityKey);
        return activity;
    }

    internal static void CompleteCapabilitySelect(
        Activity? activity,
        string capabilityKey,
        bool desiredEnabled,
        bool authorized,
        bool selected,
        bool success,
        long durationMs,
        string? errorReason = null)
    {
        activity?.SetTag("saaia.runtime.desired_enabled", desiredEnabled);
        activity?.SetTag("saaia.runtime.authorized", authorized);
        activity?.SetTag("saaia.runtime.selected", selected);
        activity?.SetTag("saaia.runtime.success", success);
        activity?.SetTag("saaia.runtime.duration_ms", durationMs);
        if (!string.IsNullOrWhiteSpace(errorReason))
            activity?.SetTag("saaia.runtime.error_reason", errorReason);

        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.success", success }
        };

        SelectionUpdates.Add(1, tags);
        SelectionDurationMs.Record(durationMs, tags);
        if (!success)
        {
            if (!string.IsNullOrWhiteSpace(errorReason))
                tags.Add("saaia.runtime.error_reason", errorReason);
            SelectionFailures.Add(1, tags);
        }
    }

    internal static Activity? StartArtifactReadActivity(string artifactName)
    {
        var activity = ActivitySource.StartActivity("runtime_artifact_read", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.runtime.artifact_name", artifactName);
        return activity;
    }

    internal static void CompleteArtifactRead(Activity? activity, string artifactName, bool success, long durationMs)
    {
        activity?.SetTag("saaia.runtime.success", success);
        activity?.SetTag("saaia.runtime.duration_ms", durationMs);

        var tags = new TagList
        {
            { "saaia.runtime.artifact_name", artifactName },
            { "saaia.runtime.success", success }
        };

        ArtifactReads.Add(1, tags);
        ArtifactReadDurationMs.Record(durationMs, tags);
    }

    internal static Activity? StartCapabilityAOperationActivity(string operationName)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.runtime.capability_key", "capability_a.corpus_enrichment");
        activity.SetTag("saaia.runtime.operation_name", operationName);
        return activity;
    }

    internal static void CompleteCapabilityAOperation(
        Activity? activity,
        string operationName,
        bool success,
        long durationMs,
        int? candidateCount = null,
        int? plannedCount = null,
        int? queuedCount = null,
        int? skippedCount = null,
        bool? dryRun = null,
        string? errorReason = null)
    {
        activity?.SetTag("saaia.runtime.success", success);
        activity?.SetTag("saaia.runtime.duration_ms", durationMs);
        if (candidateCount.HasValue)
            activity?.SetTag("saaia.runtime.candidate_count", candidateCount.Value);
        if (plannedCount.HasValue)
            activity?.SetTag("saaia.runtime.planned_count", plannedCount.Value);
        if (queuedCount.HasValue)
            activity?.SetTag("saaia.runtime.queued_count", queuedCount.Value);
        if (skippedCount.HasValue)
            activity?.SetTag("saaia.runtime.skipped_count", skippedCount.Value);
        if (dryRun.HasValue)
            activity?.SetTag("saaia.runtime.dry_run", dryRun.Value);
        if (!string.IsNullOrWhiteSpace(errorReason))
            activity?.SetTag("saaia.runtime.error_reason", errorReason);

        var tags = new TagList
        {
            { "saaia.runtime.capability_key", "capability_a.corpus_enrichment" },
            { "saaia.runtime.operation_name", operationName },
            { "saaia.runtime.success", success }
        };
        if (dryRun.HasValue)
            tags.Add("saaia.runtime.dry_run", dryRun.Value);

        CapabilityAOperationDurationMs.Record(durationMs, tags);

        if (string.Equals(operationName, "capability_a_candidates", StringComparison.Ordinal))
        {
            CapabilityACandidateReads.Add(1, tags);
            if (candidateCount.HasValue)
                CapabilityACandidateCount.Record(candidateCount.Value, tags);
        }
        else if (string.Equals(operationName, "capability_a_enqueue", StringComparison.Ordinal))
        {
            CapabilityAEnqueueRequests.Add(1, tags);
            var planned = plannedCount.GetValueOrDefault();
            if (planned > 0)
                CapabilityACandidateCount.Record(planned, tags);
            var queued = queuedCount.GetValueOrDefault();
            var skipped = skippedCount.GetValueOrDefault();
            if (queued > 0)
                CapabilityAQueuedDocs.Add(queued, tags);
            if (skipped > 0)
                CapabilityASkippedDocs.Add(skipped, tags);
        }
    }

    internal static Activity? StartCapabilityBOperationActivity(string operationName)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.runtime.capability_key", "capability_b.backoffice_generation");
        activity.SetTag("saaia.runtime.operation_name", operationName);
        return activity;
    }

    internal static void CompleteCapabilityBOperation(
        Activity? activity,
        string operationName,
        bool success,
        long durationMs,
        int? candidateCount = null,
        int? plannedCount = null,
        int? queuedCount = null,
        int? skippedCount = null,
        bool? dryRun = null,
        string? errorReason = null)
    {
        activity?.SetTag("saaia.runtime.success", success);
        activity?.SetTag("saaia.runtime.duration_ms", durationMs);
        if (candidateCount.HasValue)
            activity?.SetTag("saaia.runtime.candidate_count", candidateCount.Value);
        if (plannedCount.HasValue)
            activity?.SetTag("saaia.runtime.planned_count", plannedCount.Value);
        if (queuedCount.HasValue)
            activity?.SetTag("saaia.runtime.queued_count", queuedCount.Value);
        if (skippedCount.HasValue)
            activity?.SetTag("saaia.runtime.skipped_count", skippedCount.Value);
        if (dryRun.HasValue)
            activity?.SetTag("saaia.runtime.dry_run", dryRun.Value);
        if (!string.IsNullOrWhiteSpace(errorReason))
            activity?.SetTag("saaia.runtime.error_reason", errorReason);

        var tags = new TagList
        {
            { "saaia.runtime.capability_key", "capability_b.backoffice_generation" },
            { "saaia.runtime.operation_name", operationName },
            { "saaia.runtime.success", success }
        };
        if (dryRun.HasValue)
            tags.Add("saaia.runtime.dry_run", dryRun.Value);

        CapabilityBOperationDurationMs.Record(durationMs, tags);

        if (string.Equals(operationName, "capability_b_candidates", StringComparison.Ordinal))
        {
            CapabilityBCandidateReads.Add(1, tags);
            if (candidateCount.HasValue)
                CapabilityBCandidateCount.Record(candidateCount.Value, tags);
        }
        else if (string.Equals(operationName, "capability_b_enqueue", StringComparison.Ordinal))
        {
            CapabilityBEnqueueRequests.Add(1, tags);
            var planned = plannedCount.GetValueOrDefault();
            if (planned > 0)
                CapabilityBCandidateCount.Record(planned, tags);
            var queued = queuedCount.GetValueOrDefault();
            var skipped = skippedCount.GetValueOrDefault();
            if (queued > 0)
                CapabilityBQueuedDocs.Add(queued, tags);
            if (skipped > 0)
                CapabilityBSkippedDocs.Add(skipped, tags);
        }
        else if (string.Equals(operationName, "capability_b_claim", StringComparison.Ordinal))
        {
            CapabilityBClaimRequests.Add(1, tags);
        }
        else if (string.Equals(operationName, "capability_b_fail", StringComparison.Ordinal))
        {
            CapabilityBFailedDocs.Add(1, tags);
        }
        else if (string.Equals(operationName, "capability_b_summary_completed", StringComparison.Ordinal))
        {
            CapabilityBCompletedDocs.Add(1, tags);
        }
    }

    internal static void MarkError(Activity? activity, Exception ex)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("exception.type", ex.GetType().FullName);
    }

    internal static void RecordRequalifyRequest(string capabilityKey, string profileKey, int capabilityCount)
    {
        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.profile_key", profileKey }
        };

        RequalifyRequests.Add(capabilityCount, tags);
    }

    internal static void RecordStaleQualificationDetected(string capabilityKey, string profileKey, string reason)
    {
        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.profile_key", profileKey },
            { "saaia.runtime.stale_reason", reason }
        };

        StaleQualificationDetections.Add(1, tags);
    }

    internal static void RecordStaleQualificationReconciled(string capabilityKey, string profileKey, string reason)
    {
        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.profile_key", profileKey },
            { "saaia.runtime.stale_reason", reason }
        };

        StaleQualificationReconciliations.Add(1, tags);
    }

    internal static void RecordCapabilityEventWritten(string capabilityKey, string eventType)
    {
        var tags = new TagList
        {
            { "saaia.runtime.capability_key", capabilityKey },
            { "saaia.runtime.event_type", eventType }
        };

        EventWrites.Add(1, tags);
    }
}
