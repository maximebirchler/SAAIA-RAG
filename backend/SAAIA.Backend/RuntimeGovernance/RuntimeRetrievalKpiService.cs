using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeRetrievalKpiService(IHostEnvironment env)
{
    private const string CdcAlignment = "v3.1";
    private const string RetrievalKpisArtifact = "retrieval-kpis.json";

    internal AdminRuntimeRetrievalKpisResponseDto GetKpis(RuntimeGovernanceOptions options)
        => new(
            CdcAlignment,
            env.EnvironmentName,
            DateTimeOffset.UtcNow,
            BuildPolicy(options),
            BuildMetrics(),
            BuildAlerts(options),
            BuildDashboardPanels());

    internal AdminRuntimeRetrievalKpisArtifactDto GetKpisArtifact(RuntimeGovernanceOptions options)
    {
        var payload = GetKpis(options);
        return new AdminRuntimeRetrievalKpisArtifactDto(
            RetrievalKpisArtifact,
            payload.CdcAlignment,
            payload.Environment,
            payload.GeneratedAt,
            payload.Policy,
            payload.Metrics,
            payload.Alerts,
            payload.DashboardPanels);
    }

    private static AdminRuntimeRetrievalKpiPolicyDto BuildPolicy(RuntimeGovernanceOptions options)
        => new(
            ObservationWindowMinutes: Math.Max(5, options.RetrievalKpiObservationWindowMinutes),
            RetrievalP95TargetMs: options.RetrievalP95TargetMs,
            RerankP95TargetMs: options.RerankP95TargetMs,
            ZeroResultRateTargetPercent: options.ZeroResultRateTargetPercent,
            ZeroResultRateFormula: "sum(saaia.retrieval.zero_results) / sum(saaia.retrieval.requests) * 100",
            Notes: "CDC v3.1 retrieval targets: P95 retrieval < 800 ms, P95 rerank < 300 ms, zero-result rate < 5 %.");

    private static AdminRuntimeMetricDefinitionDto[] BuildMetrics()
        =>
        [
            new(
                Key: "retrieval_p95",
                Instrument: "saaia.retrieval.duration",
                Aggregation: "p95",
                Unit: "ms",
                Description: "End-to-end retrieval latency excluding the writer path.",
                Tags: ["saaia.retrieval.mode", "saaia.retrieval.has_category_filter", "saaia.retrieval.has_doc_scope", "saaia.retrieval.retrievers", "saaia.retrieval.qdrant_status"]),
            new(
                Key: "rerank_p95",
                Instrument: "saaia.retrieval.rerank.duration",
                Aggregation: "p95",
                Unit: "ms",
                Description: "TEI rerank latency when rerank is active.",
                Tags: ["saaia.retrieval.mode", "saaia.retrieval.retrievers", "saaia.retrieval.qdrant_status"]),
            new(
                Key: "zero_result_rate",
                Instrument: "saaia.retrieval.zero_results + saaia.retrieval.requests",
                Aggregation: "ratio",
                Unit: "%",
                Description: "Share of retrieval requests returning no exploitable evidence.",
                Tags: ["saaia.retrieval.mode", "saaia.retrieval.has_category_filter", "saaia.retrieval.has_doc_scope"]),
            new(
                Key: "retrieval_requests",
                Instrument: "saaia.retrieval.requests",
                Aggregation: "sum",
                Unit: "{request}",
                Description: "Request volume baseline for interpreting P95 and zero-result trends.",
                Tags: ["saaia.retrieval.mode", "saaia.retrieval.retrievers"])
        ];

    private static AdminRuntimeAlertDefinitionDto[] BuildAlerts(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: "retrieval_p95_regression",
                Severity: "warning",
                Condition: $"p95(saaia.retrieval.duration) > {options.RetrievalP95TargetMs:0} ms over {Math.Max(5, options.RetrievalKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Inspect retrieval phases, retriever mix, and Qdrant/TEI timings before relaxing thresholds."),
            new(
                Key: "rerank_p95_regression",
                Severity: "warning",
                Condition: $"p95(saaia.retrieval.rerank.duration) > {options.RerankP95TargetMs:0} ms over {Math.Max(5, options.RetrievalKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Check rerank runtime saturation or disable rerank on profiles that cannot sustain the CDC target."),
            new(
                Key: "zero_result_rate_regression",
                Severity: "critical",
                Condition: $"100 * sum(saaia.retrieval.zero_results) / sum(saaia.retrieval.requests) > {options.ZeroResultRateTargetPercent:0.##} over {Math.Max(5, options.RetrievalKpiObservationWindowMinutes)} minutes",
                RecommendedAction: "Review corpus freshness, exact-match coverage, and sparse/dense retrieval health before rolling out further traffic.")
        ];

    private static string[] BuildDashboardPanels()
        =>
        [
            "Retrieval P95 latency split by mode and retriever set",
            "Rerank P95 latency with qdrant status correlation",
            "Zero-result rate with request volume baseline",
            "Requests by retrieval mode and category/doc-scope filters"
        ];
}
