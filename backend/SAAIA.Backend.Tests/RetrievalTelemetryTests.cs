using System.Diagnostics;
using System.Diagnostics.Metrics;
using SAAIA.Backend;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalTelemetryTests
{
    [Fact]
    public void RecordSearch_emits_cdc_retrieval_metrics_with_stable_tags()
    {
        var measurements = new List<RecordedMetric>();
        using var listener = CreateMeterListener(measurements);

        var response = new RagSearchResponse(
            RequestId: "req-1",
            Query: "Ou trouve-t-on EN 15281 ?",
            QueryNormalized: "ou trouve t on en 15281",
            Category: "atex",
            TopK: 5,
            MinScore: 0.25,
            Candidates: 30,
            MaxPerDoc: 3,
            MaxPerPage: 1,
            QdrantStatus: 200,
            Timings: new RagSearchTimings(
                TotalMs: 145,
                TeiMs: 21,
                RerankMs: 9,
                SparseMs: 5,
                QdrantMs: 34),
            Matches:
            [
                new RagMatch(0.99, "doc-1", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "chunk-1", 0, "EN 15281", 1, "hash-1", "EN 15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null),
                new RagMatch(0.72, "doc-1", "ATEX/CEN.pdf", "CEN.pdf", 2, 2, "chunk-2", 1, "Guidance", 1, "hash-1", "Document: CEN.pdf\nExcerpt:\nGuidance", "sparse_bm25_v1", 1, 1, "Overview", "Overview", "unit_exact_v1", null, null, null)
            ]);

        RetrievalTelemetry.RecordSearch(
            response,
            mode: "balanced",
            hasCategoryFilter: true,
            hasDocScope: false,
            exactMs: 7,
            sparsePhaseMs: 11,
            densePhaseMs: 48,
            linkedPhaseMs: 0);

        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.requests" && item.AsLong() == 1);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.returned_results" && item.AsLong() == 2);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.candidates" && item.AsLong() == 30);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.duration" && item.AsDouble() == 145);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.exact_match.duration" && item.AsDouble() == 7);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.sparse.duration" && item.AsDouble() == 11);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.dense.duration" && item.AsDouble() == 48);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.rerank.duration" && item.AsDouble() == 9);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.tei.duration" && item.AsDouble() == 21);
        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.qdrant.duration" && item.AsDouble() == 34);

        var requestMetric = Assert.Single(measurements, item => item.Name == "saaia.retrieval.requests");
        Assert.Equal("balanced", requestMetric.Tags["saaia.retrieval.mode"]);
        Assert.Equal(true, requestMetric.Tags["saaia.retrieval.has_category_filter"]);
        Assert.Equal(false, requestMetric.Tags["saaia.retrieval.has_doc_scope"]);
        Assert.Equal("2xx", requestMetric.Tags["saaia.retrieval.qdrant_status"]);
        Assert.Equal("exact_match+sparse_bm25", requestMetric.Tags["saaia.retrieval.retrievers"]);
    }

    [Fact]
    public void RecordSearch_emits_zero_result_counter_when_search_returns_nothing()
    {
        var measurements = new List<RecordedMetric>();
        using var listener = CreateMeterListener(measurements);

        var response = new RagSearchResponse(
            RequestId: "req-2",
            Query: "norme xxx",
            QueryNormalized: "norme xxx",
            Category: null,
            TopK: 5,
            MinScore: 0.25,
            Candidates: 15,
            MaxPerDoc: 3,
            MaxPerPage: 1,
            QdrantStatus: 0,
            Timings: new RagSearchTimings(88, 0, 0, 0, 0),
            Matches: []);

        RetrievalTelemetry.RecordSearch(
            response,
            mode: "focused",
            hasCategoryFilter: false,
            hasDocScope: false,
            exactMs: 4,
            sparsePhaseMs: 0,
            densePhaseMs: 0,
            linkedPhaseMs: 0);

        Assert.Contains(measurements, item => item.Name == "saaia.retrieval.zero_results" && item.AsLong() == 1);
        var zeroMetric = Assert.Single(measurements, item => item.Name == "saaia.retrieval.zero_results");
        Assert.Equal("not_used", zeroMetric.Tags["saaia.retrieval.qdrant_status"]);
    }

    [Fact]
    public void Search_and_phase_activities_expose_stable_cdc_tags()
    {
        var started = new List<Activity>();
        var stopped = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => started.Add(activity),
            ActivityStopped = activity => stopped.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);

        using (var search = RetrievalTelemetry.StartSearchActivity(
            mode: "broad",
            hasCategoryFilter: true,
            hasDocScope: true,
            topK: 8,
            candidates: 48,
            query: "Ou trouve-t-on EN 15281 ?"))
        {
            using (var phase = RetrievalTelemetry.StartPhaseActivity("retrieval_exact_match"))
            {
                RetrievalTelemetry.CompletePhase(phase, returned: 1, durationMs: 12, retriever: "exact_match");
            }

            var response = new RagSearchResponse(
                RequestId: "req-3",
                Query: "Ou trouve-t-on EN 15281 ?",
                QueryNormalized: "ou trouve t on en 15281",
                Category: "atex",
                TopK: 8,
                MinScore: 0.25,
                Candidates: 48,
                MaxPerDoc: 3,
                MaxPerPage: 1,
                QdrantStatus: 0,
                Timings: new RagSearchTimings(120, 0, 0, 0, 0),
                Matches:
                [
                    new RagMatch(1.0, "doc-1", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "chunk-1", 0, "EN 15281", 1, "hash", "EN 15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null)
                ]);

            RetrievalTelemetry.CompleteSearch(search, response, "broad", hasCategoryFilter: true, hasDocScope: true);
        }

        Assert.Contains(started, activity => activity.OperationName == "rag.search");
        Assert.Contains(started, activity => activity.OperationName == "retrieval_exact_match");

        var searchActivity = Assert.Single(stopped, activity => activity.OperationName == "rag.search");
        Assert.Equal("broad", searchActivity.GetTagItem("saaia.retrieval.mode"));
        Assert.Equal(true, searchActivity.GetTagItem("saaia.retrieval.has_category_filter"));
        Assert.Equal(true, searchActivity.GetTagItem("saaia.retrieval.has_doc_scope"));
        Assert.Equal(true, searchActivity.GetTagItem("saaia.retrieval.has_reference_hint"));
        Assert.Equal("exact_match", searchActivity.GetTagItem("saaia.retrieval.retrievers"));
        Assert.Equal(false, searchActivity.GetTagItem("saaia.retrieval.zero_result"));

        var phaseActivity = Assert.Single(stopped, activity => activity.OperationName == "retrieval_exact_match");
        Assert.Equal("exact_match", phaseActivity.GetTagItem("saaia.retrieval.retriever"));
        Assert.Equal(1, phaseActivity.GetTagItem("saaia.retrieval.returned"));
        Assert.Equal(12L, phaseActivity.GetTagItem("saaia.retrieval.duration_ms"));
    }

    [Fact]
    public void RecordRetrieverDegraded_emits_metric_and_search_activity_event()
    {
        var measurements = new List<RecordedMetric>();
        using var meterListener = CreateMeterListener(measurements);
        var stopped = new List<Activity>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity)
        };

        ActivitySource.AddActivityListener(activityListener);

        using (RetrievalTelemetry.StartSearchActivity(
            mode: "balanced",
            hasCategoryFilter: false,
            hasDocScope: false,
            topK: 5,
            candidates: 25,
            query: "profile lookup"))
        {
            RetrievalTelemetry.RecordRetrieverDegraded(
                "document_profile_v1",
                new InvalidOperationException("simulated profile retriever failure"));
        }

        var metric = Assert.Single(measurements, item => item.Name == "saaia.retrieval.retriever_degraded");
        Assert.Equal(1, metric.AsLong());
        Assert.Equal("document_profile_v1", metric.Tags["saaia.retrieval.retriever"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, metric.Tags["exception.type"]);

        var searchActivity = Assert.Single(stopped, activity => activity.OperationName == "rag.search");
        var degradedEvent = Assert.Single(searchActivity.Events, activityEvent => activityEvent.Name == "retriever.degraded");
        var tags = degradedEvent.Tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
        Assert.Equal("document_profile_v1", tags["saaia.retrieval.retriever"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, tags["exception.type"]);
    }

    private static MeterListener CreateMeterListener(List<RecordedMetric> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RetrievalTelemetry.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Add(new RecordedMetric(instrument.Name, measurement, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            measurements.Add(new RecordedMetric(instrument.Name, measurement, CopyTags(tags))));
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value;

        return copy;
    }

    private sealed record RecordedMetric(string Name, object Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public long AsLong() => Convert.ToInt64(Value);
        public double AsDouble() => Convert.ToDouble(Value);
    }
}
