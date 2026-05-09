using System.Diagnostics;
using System.Diagnostics.Metrics;
using SAAIA.Backend.Endpoints;

namespace SAAIA.Backend;

internal static class RetrievalTelemetry
{
    public const string MeterName = "SAAIA.Backend.Retrieval";
    public const string ActivitySourceName = "SAAIA.Backend.Retrieval";

    private static readonly Meter Meter = new(MeterName);
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Counter<long> SearchRequests = Meter.CreateCounter<long>(
        "saaia.retrieval.requests",
        unit: "{request}",
        description: "Number of retrieval searches executed by the backend.");

    private static readonly Counter<long> ZeroResultRequests = Meter.CreateCounter<long>(
        "saaia.retrieval.zero_results",
        unit: "{request}",
        description: "Number of retrieval searches returning zero results.");

    private static readonly Counter<long> RetrieverDegraded = Meter.CreateCounter<long>(
        "saaia.retrieval.retriever_degraded",
        unit: "{event}",
        description: "Number of retriever phases that degraded and returned a fallback result.");

    private static readonly Histogram<double> SearchDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.duration",
        unit: "ms",
        description: "End-to-end retrieval latency in milliseconds.");

    private static readonly Histogram<double> ExactDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.exact_match.duration",
        unit: "ms",
        description: "Exact-match retrieval latency in milliseconds.");

    private static readonly Histogram<double> SparseDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.sparse.duration",
        unit: "ms",
        description: "Sparse/BM25 retrieval latency in milliseconds.");

    private static readonly Histogram<double> DenseDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.dense.duration",
        unit: "ms",
        description: "Dense retrieval latency in milliseconds.");

    private static readonly Histogram<double> LinkedDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.linked.duration",
        unit: "ms",
        description: "Linked-context expansion latency in milliseconds.");

    private static readonly Histogram<double> RerankDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.rerank.duration",
        unit: "ms",
        description: "Rerank latency in milliseconds.");

    private static readonly Histogram<double> TeiDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.tei.duration",
        unit: "ms",
        description: "TEI embedding latency in milliseconds.");

    private static readonly Histogram<double> QdrantDurationMs = Meter.CreateHistogram<double>(
        "saaia.retrieval.qdrant.duration",
        unit: "ms",
        description: "Qdrant latency in milliseconds.");

    private static readonly Histogram<long> ReturnedResults = Meter.CreateHistogram<long>(
        "saaia.retrieval.returned_results",
        unit: "{result}",
        description: "Number of results returned after final retrieval selection.");

    private static readonly Histogram<long> CandidatesEvaluated = Meter.CreateHistogram<long>(
        "saaia.retrieval.candidates",
        unit: "{candidate}",
        description: "Number of retrieval candidates evaluated before final selection.");

    internal static Activity? StartSearchActivity(
        string mode,
        bool hasCategoryFilter,
        bool hasDocScope,
        int topK,
        int candidates,
        string query)
    {
        var activity = ActivitySource.StartActivity("rag.search", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("saaia.retrieval.mode", NormalizeMode(mode));
        activity.SetTag("saaia.retrieval.has_category_filter", hasCategoryFilter);
        activity.SetTag("saaia.retrieval.has_doc_scope", hasDocScope);
        activity.SetTag("saaia.retrieval.top_k", topK);
        activity.SetTag("saaia.retrieval.candidates", candidates);
        activity.SetTag("saaia.retrieval.query_length", query?.Length ?? 0);
        activity.SetTag("saaia.retrieval.has_reference_hint", HasReferenceHint(query));

        return activity;
    }

    internal static Activity? StartPhaseActivity(string phaseName)
        => ActivitySource.StartActivity(phaseName, ActivityKind.Internal);

    internal static void CompletePhase(Activity? activity, int returned, long durationMs, string? retriever = null)
    {
        if (activity is null)
            return;

        activity.SetTag("saaia.retrieval.returned", returned);
        activity.SetTag("saaia.retrieval.duration_ms", durationMs);
        if (!string.IsNullOrWhiteSpace(retriever))
            activity.SetTag("saaia.retrieval.retriever", retriever);
    }

    internal static void MarkPhaseError(Activity? activity, Exception ex)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("exception.type", ex.GetType().FullName);
    }

    internal static void RecordRetrieverDegraded(string retriever, Exception ex)
    {
        var normalizedRetriever = string.IsNullOrWhiteSpace(retriever) ? "unknown" : retriever.Trim().ToLowerInvariant();
        var tags = new TagList
        {
            { "saaia.retrieval.retriever", normalizedRetriever },
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name }
        };
        if (ex is Npgsql.PostgresException pg)
            tags.Add("db.postgresql.sql_state", pg.SqlState);

        RetrieverDegraded.Add(1, tags);
        var activityTags = new ActivityTagsCollection
        {
            { "saaia.retrieval.retriever", normalizedRetriever },
            { "exception.type", ex.GetType().FullName ?? ex.GetType().Name },
            { "exception.message", ex.Message }
        };
        if (ex is Npgsql.PostgresException pgActivity)
        {
            activityTags.Add("db.postgresql.sql_state", pgActivity.SqlState);
            activityTags.Add("db.postgresql.message_text", pgActivity.MessageText);
        }

        Activity.Current?.AddEvent(new ActivityEvent("retriever.degraded", tags: activityTags));
    }

    internal static void CompleteSearch(
        Activity? activity,
        RagSearchResponse response,
        string mode,
        bool hasCategoryFilter,
        bool hasDocScope)
    {
        if (activity is null)
            return;

        activity.SetTag("saaia.retrieval.mode", NormalizeMode(mode));
        activity.SetTag("saaia.retrieval.has_category_filter", hasCategoryFilter);
        activity.SetTag("saaia.retrieval.has_doc_scope", hasDocScope);
        activity.SetTag("saaia.retrieval.returned", response.Matches.Count);
        activity.SetTag("saaia.retrieval.retrievers", BuildRetrieverSetTag(response.Matches));
        activity.SetTag("saaia.retrieval.qdrant_status", BuildQdrantStatusTag(response.QdrantStatus));
        activity.SetTag("saaia.retrieval.zero_result", response.Matches.Count == 0);
    }

    internal static void RecordSearch(
        RagSearchResponse response,
        string mode,
        bool hasCategoryFilter,
        bool hasDocScope,
        long exactMs,
        long sparsePhaseMs,
        long densePhaseMs,
        long linkedPhaseMs)
    {
        var tags = BuildMetricTags(response, mode, hasCategoryFilter, hasDocScope);

        SearchRequests.Add(1, tags);
        ReturnedResults.Record(response.Matches.Count, tags);
        CandidatesEvaluated.Record(response.Candidates, tags);
        SearchDurationMs.Record(response.Timings.TotalMs, tags);

        if (response.Matches.Count == 0)
            ZeroResultRequests.Add(1, tags);

        if (exactMs > 0)
            ExactDurationMs.Record(exactMs, tags);
        if (sparsePhaseMs > 0)
            SparseDurationMs.Record(sparsePhaseMs, tags);
        if (densePhaseMs > 0)
            DenseDurationMs.Record(densePhaseMs, tags);
        if (linkedPhaseMs > 0)
            LinkedDurationMs.Record(linkedPhaseMs, tags);
        if (response.Timings.RerankMs > 0)
            RerankDurationMs.Record(response.Timings.RerankMs, tags);
        if (response.Timings.TeiMs > 0)
            TeiDurationMs.Record(response.Timings.TeiMs, tags);
        if (response.Timings.QdrantMs > 0)
            QdrantDurationMs.Record(response.Timings.QdrantMs, tags);
    }

    internal static TagList BuildMetricTags(
        RagSearchResponse response,
        string mode,
        bool hasCategoryFilter,
        bool hasDocScope)
    {
        return new TagList
        {
            { "saaia.retrieval.mode", NormalizeMode(mode) },
            { "saaia.retrieval.has_category_filter", hasCategoryFilter },
            { "saaia.retrieval.has_doc_scope", hasDocScope },
            { "saaia.retrieval.retrievers", BuildRetrieverSetTag(response.Matches) },
            { "saaia.retrieval.qdrant_status", BuildQdrantStatusTag(response.QdrantStatus) }
        };
    }

    internal static string BuildRetrieverSetTag(IReadOnlyList<RagMatch> matches)
    {
        var retrievers = matches
            .Select(RagEndpoints.ResolveRetriever)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return retrievers.Length == 0 ? "none" : string.Join("+", retrievers);
    }

    internal static string BuildQdrantStatusTag(int qdrantStatus)
    {
        if (qdrantStatus <= 0)
            return "not_used";

        return $"{qdrantStatus / 100}xx";
    }

    private static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "balanced" : mode.Trim().ToLowerInvariant();

    private static bool HasReferenceHint(string? query)
        => ExactMatchEntryExtractor.ExtractReferenceKeys(query ?? string.Empty).Count > 0;
}
