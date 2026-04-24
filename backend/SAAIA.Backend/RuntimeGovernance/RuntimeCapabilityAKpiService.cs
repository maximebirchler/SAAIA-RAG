using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeCapabilityAKpiService(IHostEnvironment env)
{
    private const string CdcAlignment = "v3.1";
    private const string CapabilityAKpisArtifact = "capability-a-kpis.json";

    internal AdminRuntimeCapabilityAKpisResponseDto GetKpis(RuntimeGovernanceOptions options)
        => new(
            CdcAlignment,
            env.EnvironmentName,
            DateTimeOffset.UtcNow,
            BuildPolicy(options),
            BuildMetrics(),
            BuildAlerts(options),
            BuildDashboardPanels());

    internal AdminRuntimeCapabilityAKpisArtifactDto GetKpisArtifact(RuntimeGovernanceOptions options)
    {
        var payload = GetKpis(options);
        return new AdminRuntimeCapabilityAKpisArtifactDto(
            CapabilityAKpisArtifact,
            payload.CdcAlignment,
            payload.Environment,
            payload.GeneratedAt,
            payload.Policy,
            payload.Metrics,
            payload.Alerts,
            payload.DashboardPanels);
    }

    private static AdminRuntimeCapabilityAKpiPolicyDto BuildPolicy(RuntimeGovernanceOptions options)
        => new(
            ObservationWindowMinutes: Math.Max(5, options.CapabilityAKpiObservationWindowMinutes),
            OperationP95TargetMs: options.CapabilityAOperationP95TargetMs,
            SkipRateTargetPercent: options.CapabilityASkipRateTargetPercent,
            ReadyToEnqueueRateTargetPercent: options.CapabilityAReadyToEnqueueRateTargetPercent,
            OffsetBackfillShareTargetPercent: options.CapabilityAOffsetBackfillShareTargetPercent,
            SkipRateFormula: "100 * sum(saaia.runtime.capability_a.skipped_docs) / (sum(saaia.runtime.capability_a.queued_docs) + sum(saaia.runtime.capability_a.skipped_docs))",
            ReadyToEnqueueRateFormula: "100 * ready_to_enqueue_count / candidate_count from /admin/runtime/operational-summary for capability_a",
            OffsetBackfillShareFormula: "100 * offset_backfill_candidate_count / candidate_count from /admin/runtime/operational-summary for capability_a",
            Notes: "Capability A ops targets: candidate/enqueue operation latency, enqueue skip rate, ready-to-enqueue share, and offset-backfill share. Candidate mix rates are derived from operational-summary because they are snapshot health signals rather than streaming counters.");

    private static AdminRuntimeMetricDefinitionDto[] BuildMetrics()
        =>
        [
            new(
                Key: "capability_a_operation_p95",
                Instrument: "saaia.runtime.capability_a.duration",
                Aggregation: "p95",
                Unit: "ms",
                Description: "Capability A candidates/enqueue operation latency, tagged by operation and success.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success", "saaia.runtime.dry_run"]),
            new(
                Key: "capability_a_candidate_reads",
                Instrument: "saaia.runtime.capability_a.candidate_reads",
                Aggregation: "sum",
                Unit: "{request}",
                Description: "Number of admin reads of capability A enrichment candidates.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success"]),
            new(
                Key: "capability_a_candidate_count",
                Instrument: "saaia.runtime.capability_a.candidate_count",
                Aggregation: "p50",
                Unit: "{document}",
                Description: "Median number of candidate documents seen by capability A plan/enqueue operations.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success", "saaia.runtime.dry_run"]),
            new(
                Key: "capability_a_enqueue_requests",
                Instrument: "saaia.runtime.capability_a.enqueue.requests",
                Aggregation: "sum",
                Unit: "{request}",
                Description: "Number of capability A enqueue attempts issued by admins.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success", "saaia.runtime.dry_run"]),
            new(
                Key: "capability_a_skip_rate",
                Instrument: "saaia.runtime.capability_a.queued_docs + saaia.runtime.capability_a.skipped_docs",
                Aggregation: "ratio",
                Unit: "%",
                Description: "Share of planned capability A documents skipped instead of queued.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success", "saaia.runtime.dry_run"]),
            new(
                Key: "capability_a_ready_to_enqueue_rate",
                Instrument: "/admin/runtime/operational-summary",
                Aggregation: "snapshot-ratio",
                Unit: "%",
                Description: "Share of current capability A candidates that are ready to enqueue.",
                Tags: ["capability_key=capability_a.corpus_enrichment"]),
            new(
                Key: "capability_a_offset_backfill_share",
                Instrument: "/admin/runtime/operational-summary",
                Aggregation: "snapshot-ratio",
                Unit: "%",
                Description: "Share of current capability A candidates driven by missing derived offsets, useful for legacy backfill tracking.",
                Tags: ["capability_key=capability_a.corpus_enrichment"])
        ];

    private static AdminRuntimeAlertDefinitionDto[] BuildAlerts(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: "capability_a_operation_p95_regression",
                Severity: "warning",
                Condition: $"p95(saaia.runtime.capability_a.duration) > {options.CapabilityAOperationP95TargetMs:0} ms over {Math.Max(5, options.CapabilityAKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Inspect candidate generation, LLM question/tag enrichment, and database latency before increasing A batch sizes."),
            new(
                Key: "capability_a_skip_rate_regression",
                Severity: "warning",
                Condition: $"100 * sum(saaia.runtime.capability_a.skipped_docs) / (sum(saaia.runtime.capability_a.queued_docs) + sum(saaia.runtime.capability_a.skipped_docs)) > {options.CapabilityASkipRateTargetPercent:0.##} over {Math.Max(5, options.CapabilityAKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Review cooldowns, active ingestion jobs, and force-targeted enqueue usage; a high skip rate means A is being asked to work but cannot queue documents."),
            new(
                Key: "capability_a_ready_to_enqueue_rate_regression",
                Severity: "info",
                Condition: $"operational-summary capability_a ready_to_enqueue_rate < {options.CapabilityAReadyToEnqueueRateTargetPercent:0.##}%",
                RecommendedAction: "Use the operational summary to decide whether candidates are blocked by active jobs, cooldowns, or missing prerequisites."),
            new(
                Key: "capability_a_offset_backfill_share_watch",
                Severity: "info",
                Condition: $"operational-summary capability_a offset_backfill_share > {options.CapabilityAOffsetBackfillShareTargetPercent:0.##}%",
                RecommendedAction: "Prioritize controlled A reindex/backfill campaigns until legacy documents with missing derived offsets shrink below the target share.")
        ];

    private static string[] BuildDashboardPanels()
        =>
        [
            "Capability A operation P95 split by candidates vs enqueue and success",
            "Capability A candidate reads and candidate-count median",
            "Capability A queued vs skipped documents and skip-rate trend",
            "Capability A ready-to-enqueue ratio from operational summary",
            "Capability A offset-backfill candidate share from operational summary"
        ];
}
