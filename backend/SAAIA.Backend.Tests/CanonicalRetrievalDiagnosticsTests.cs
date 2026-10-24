using System.Diagnostics.Metrics;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

[Collection("RetrievalDiagnosticIsolation")]
public sealed class CanonicalRetrievalDiagnosticsTests
{
    [Fact]
    public async Task Disabled_channel_is_reported_as_skipped_without_executing_the_action()
    {
        var stopped = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var invoked = false;
        var phases = new RetrievalPhaseDiagnostics(null);
        var result = await phases.MeasureOptionalAsync("retrieval.disabled_channel", "dense", false, "route_disabled",
            () => { invoked = true; return Task.FromResult(new List<RagMatch>()); }, () => new List<RagMatch>(), matches => matches.Count);
        Assert.False(invoked);
        Assert.Empty(result.Result);
        Assert.Equal(0, result.DurationMs);
        var activity = Assert.Single(stopped);
        Assert.Equal("skipped", activity.GetTagItem("saaia.phase.outcome"));
        Assert.Equal("route_disabled", activity.GetTagItem("saaia.phase.reason"));
    }

    [Theory]
    [InlineData(null, 0, false, 0, 0, 0, "document_not_found")]
    [InlineData("pending", 0, false, 0, 0, 0, "index_not_published")]
    [InlineData("indexed", 1, false, 0, 0, 0, "revision_not_found")]
    [InlineData("indexed", 2, true, 1, 1, 1, "revision_not_current")]
    [InlineData("indexed", 2, true, 2, 0, 0, "no_canonical_chunks")]
    [InlineData("indexed", 2, true, 2, 5, 0, "requested_locator_not_present")]
    [InlineData("indexed", 2, true, 2, 5, 2, "canonical_present")]
    public void Presence_distinguishes_missing_document_revision_and_requested_locator(string? status,
        int indexedVersion, bool hasRevision, int revisionVersion, int chunks, int matching, string expected)
        => Assert.Equal(expected, CanonicalIndexProbe.Classify(status, indexedVersion,
            hasRevision ? Guid.NewGuid() : null, revisionVersion, chunks, matching));

    [Fact]
    public void Selection_explains_each_rejection_without_exposing_document_text()
    {
        var first = Match("doc-a", "chunk-a", 1, .9);
        var diagnostics = new RagRetrievalDiagnosticsBuilder("private question", "private retrieval", "private selection",
            "source_backed_canonical", "private-category", null, 10, 20, 1, 1, includeContent: false);
        var matches = new[] { first, first, Match("doc-a", "chunk-b", 2, .8),
            Match("doc-b", "chunk-c", 1, .1), Match("doc-c", "chunk-d", 1, .7) };
        var selected = CanonicalRetrieval.SelectCanonicalMatches(matches, 10, .5, 1, 1, diagnostics);
        var snapshot = diagnostics.Build(selected);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { "duplicate_identity", "document_limit", "score_threshold" }, snapshot.Rejections!.Select(item => item.ReasonCode));
        Assert.Equal("metadata", snapshot.CaptureMode);
        Assert.Equal(string.Empty, snapshot.Query);
        Assert.Null(snapshot.Category);
        Assert.All(snapshot.Selection.Items, item => { Assert.Null(item.Snippet); Assert.Null(item.DocPath); Assert.Null(item.DocName); });
    }

    [Fact]
    public void Bounded_collection_reports_omissions_instead_of_claiming_completeness()
    {
        var diagnostic = new RagRetrievalDiagnosticsBuilder("q", "q", "q", "canonical", null, null, 20, 40, 20, 20, false);
        var matches = Enumerable.Range(0, 20).Select(index => Match("doc-a", $"chunk-{index}", 1, .9)).ToArray();
        for (var index = 0; index < 131; index++) diagnostic.CapturePhase("phase", "exact", 1, matches);
        for (var index = 0; index < 100; index++) diagnostic.Reject("selection", "top_k_limit", matches[0]);
        var result = diagnostic.Build([]);
        Assert.Equal(3, result.DroppedPhases);
        Assert.All(result.Phases.Take(16), phase => Assert.Equal(8, phase.OmittedCandidates));
        Assert.All(result.Phases.Skip(16), phase => Assert.Equal(20, phase.OmittedCandidates));
        Assert.Equal(192, result.CapturedPhaseCandidates);
        Assert.Equal(192, result.CandidateSampleBudget);
        Assert.Equal(4, result.DroppedRejections);
        Assert.Equal(96, result.Rejections!.Count);
        Assert.Equal(100, result.RejectionCounts!["top_k_limit"]);
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "cancelled")]
    public async Task Failed_search_is_counted_once_and_terminated_even_before_network(bool cancelled, string outcome)
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        { if (instrument.Name == "saaia.retrieval.outcomes") meterListener.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "saaia.retrieval.outcome") lock (outcomes) outcomes.Add(tag.Value!.ToString()!);
        });
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var context = new RetrievalExecutionContext(Guid.NewGuid(), "unit-test", cancellation.Token);
        var error = await Record.ExceptionAsync(() => RetrievalEngine.SearchAsync(context, null!, new RagOptions(), null!,
            new RagSearchRequestDto(string.Empty, SourceBackedCanonical: true)));
        Assert.NotNull(error);
        Assert.Equal(outcome, Assert.Single(outcomes));
    }

    private static RagMatch Match(string docId, string chunkId, int page, double score)
        => new(Score: score, DocId: docId, DocName: "private-name", DocPath: $"private/{docId}.pdf", PageStart: page,
            PageEnd: page, Text: "private document body", ChunkId: chunkId, ChunkIndex: 0,
            IngestionVersion: 1, HashDoc: null, EmbedText: null, EmbeddingBasis: "test",
            SectionOrdinal: null, UnitOrdinal: null, SectionTitle: "private-section", HeadingPath: null,
            ChunkType: "text", PrevChunkId: null, NextChunkId: null, SameSectionChunkId: null);
}

[CollectionDefinition("RetrievalDiagnosticIsolation", DisableParallelization = true)]
public sealed class RetrievalDiagnosticIsolationCollection;
