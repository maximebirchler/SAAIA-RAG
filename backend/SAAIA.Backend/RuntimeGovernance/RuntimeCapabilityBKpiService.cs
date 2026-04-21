using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeCapabilityBKpiService(IHostEnvironment env)
{
    private const string CdcAlignment = "v3.0";
    private const string CapabilityBKpisArtifact = "capability-b-kpis.json";

    internal AdminRuntimeCapabilityBKpisResponseDto GetKpis(RuntimeGovernanceOptions options)
        => new(
            CdcAlignment,
            env.EnvironmentName,
            DateTimeOffset.UtcNow,
            BuildPolicy(options),
            BuildMetrics(),
            BuildAlerts(options),
            BuildDashboardPanels());

    internal AdminRuntimeCapabilityBKpisArtifactDto GetKpisArtifact(RuntimeGovernanceOptions options)
    {
        var payload = GetKpis(options);
        return new AdminRuntimeCapabilityBKpisArtifactDto(
            CapabilityBKpisArtifact,
            payload.CdcAlignment,
            payload.Environment,
            payload.GeneratedAt,
            payload.Policy,
            payload.Metrics,
            payload.Alerts,
            payload.DashboardPanels);
    }

    private static AdminRuntimeCapabilityBKpiPolicyDto BuildPolicy(RuntimeGovernanceOptions options)
        => new(
            ObservationWindowMinutes: Math.Max(5, options.CapabilityBKpiObservationWindowMinutes),
            GenerationP95TargetMs: options.CapabilityBGenerationP95TargetMs,
            LiveFallbackRateTargetPercent: options.CapabilityBLiveFallbackRateTargetPercent,
            FailureRateTargetPercent: options.CapabilityBFailureRateTargetPercent,
            QualityScoreTarget: options.CapabilityBQualityScoreTarget,
            LiveFallbackRateFormula: "100 * sum(saaia.runtime.capability_b.execution_decisions{status=runtime_unavailable}) / sum(saaia.runtime.capability_b.execution_decisions)",
            FailureRateFormula: "100 * sum(saaia.runtime.capability_b.failed_docs) / (sum(saaia.runtime.capability_b.completed_docs) + sum(saaia.runtime.capability_b.failed_docs))",
            Notes: "Capability B ops targets: generation P95 on summary generation, live fallback rate for runtime_unavailable decisions, worker failure rate, and a heuristic quality score. The current first-response metric is an approximate proxy measured as first readable response byte because the local LLM path is not streamed token-by-token yet.");

    private static AdminRuntimeMetricDefinitionDto[] BuildMetrics()
        =>
        [
            new(
                Key: "capability_b_generation_p95",
                Instrument: "saaia.runtime.capability_b.summary_generation.duration",
                Aggregation: "p95",
                Unit: "ms",
                Description: "End-to-end summary generation latency for the worker, tagged by resolved strategy and fallback usage.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.strategy", "saaia.runtime.fallback_used", "saaia.runtime.fallback_reason"]),
            new(
                Key: "capability_b_live_fallback_rate",
                Instrument: "saaia.runtime.capability_b.execution_decisions",
                Aggregation: "ratio",
                Unit: "%",
                Description: "Share of execution decisions that fell back to client_admin because the live B runtime was unavailable.",
                Tags: ["saaia.runtime.execution_mode", "saaia.runtime.status", "saaia.runtime.uses_capability_b"]),
            new(
                Key: "capability_b_first_response_p95",
                Instrument: "saaia.runtime.capability_b.summary_generation.first_response",
                Aggregation: "p95",
                Unit: "ms",
                Description: "Approximate time-to-first-response for the local LLM path, measured as the first readable response byte rather than a streamed token.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.strategy", "saaia.runtime.fallback_used", "saaia.runtime.fallback_reason"]),
            new(
                Key: "capability_b_failure_rate",
                Instrument: "saaia.runtime.capability_b.failed_docs + saaia.runtime.capability_b.completed_docs",
                Aggregation: "ratio",
                Unit: "%",
                Description: "Share of capability B jobs ending in failure over terminal outcomes.",
                Tags: ["saaia.runtime.capability_key", "saaia.runtime.operation_name", "saaia.runtime.success"]),
            new(
                Key: "capability_b_output_length_p50",
                Instrument: "saaia.runtime.capability_b.summary_generation.output_length",
                Aggregation: "p50",
                Unit: "{char}",
                Description: "Median generated summary length as a lightweight quality/sanity proxy until richer scoring is available.",
                Tags: ["saaia.runtime.strategy", "saaia.runtime.fallback_used"]),
            new(
                Key: "capability_b_quality_score_p50",
                Instrument: "saaia.runtime.capability_b.summary_generation.quality_score",
                Aggregation: "p50",
                Unit: "{score}",
                Description: "Median heuristic quality score for generated summaries, computed from structure, length, section coverage, and excerpt keyword coverage.",
                Tags: ["saaia.runtime.strategy", "saaia.runtime.fallback_used", "saaia.runtime.fallback_reason"])
        ];

    private static AdminRuntimeAlertDefinitionDto[] BuildAlerts(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: "capability_b_generation_p95_regression",
                Severity: "warning",
                Condition: $"p95(saaia.runtime.capability_b.summary_generation.duration) > {options.CapabilityBGenerationP95TargetMs:0} ms over {Math.Max(5, options.CapabilityBKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Inspect local LLM saturation, prompt size, and fallback reasons before raising worker concurrency."),
            new(
                Key: "capability_b_live_fallback_rate_regression",
                Severity: "critical",
                Condition: $"100 * sum(saaia.runtime.capability_b.execution_decisions{{status=runtime_unavailable}}) / sum(saaia.runtime.capability_b.execution_decisions) > {options.CapabilityBLiveFallbackRateTargetPercent:0.##} over {Math.Max(5, options.CapabilityBKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Investigate the live B runtime health immediately; repeated runtime_unavailable decisions mean server backoffice is no longer available."),
            new(
                Key: "capability_b_failure_rate_regression",
                Severity: "critical",
                Condition: $"100 * sum(saaia.runtime.capability_b.failed_docs) / (sum(saaia.runtime.capability_b.completed_docs) + sum(saaia.runtime.capability_b.failed_docs)) > {options.CapabilityBFailureRateTargetPercent:0.##} over {Math.Max(5, options.CapabilityBKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Review worker failures, document-specific error bursts, and summary completion paths before expanding B traffic."),
            new(
                Key: "capability_b_quality_score_regression",
                Severity: "warning",
                Condition: $"p50(saaia.runtime.capability_b.summary_generation.quality_score) < {options.CapabilityBQualityScoreTarget:0.##} over {Math.Max(5, options.CapabilityBKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Inspect prompt drift, fallback bursts, and low-coverage summaries before trusting server backoffice outputs at scale.")
        ];

    private static string[] BuildDashboardPanels()
        =>
        [
            "Capability B generation P95 split by strategy and fallback reason",
            "Capability B first-response P95 (approximate first readable byte)",
            "Capability B live runtime fallback rate with execution status breakdown",
            "Capability B terminal failure rate and completed-vs-failed trend",
            "Capability B output length median as a lightweight quality proxy",
            "Capability B heuristic quality score median split by strategy"
        ];
}
