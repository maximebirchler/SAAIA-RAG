using System.Text.Json;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class InteractiveRetrievalPipelineTests
{
    [Fact]
    public async Task Planner_preserves_explicit_diversity_caps_and_normalizes_the_scoped_page_window()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=never_opened;Username=fixture");
        var resources = new InteractiveRetrievalResources(new(Guid.NewGuid(), "request-17", CancellationToken.None),
            dataSource, new RagOptions { DefaultTopK = 12, MaxTopK = 60 }, new NeverHttpClientFactory());
        var request = new RagSearchRequestDto("alpha", TopK: 8, DocId: Guid.NewGuid().ToString(), PageStart: 5,
            PageEnd: 4, Mode: "focused", Diversity: new(MaxChunksPerDoc: 2, PreferDistinctPages: true));
        var plan = await InteractiveRetrievalPlanner.CreateAsync(resources, request, new());
        Assert.Equal(8, plan.Selection.RequestedTopK);
        Assert.Equal(2, plan.Selection.MaxPerDoc);
        Assert.Equal(1, plan.Selection.MaxPerPage);
        Assert.Equal(5, plan.Scope.RequestedPageStart);
        Assert.Equal(5, plan.Scope.RequestedPageEnd);
        Assert.True(plan.Scope.HasDocScope);
        Assert.Equal("focused", plan.Routes.Mode);
    }

    [Fact]
    public async Task Stage_diagnostics_detect_order_changes_even_if_the_candidate_key_set_is_unchanged()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        fixture.Flow.Selection.Matches.AddRange([Match("doc-1", "chunk-1"), Match("doc-2", "chunk-2")]);
        fixture.Flow.Selection.Keys.UnionWith(["unchanged-key-1", "unchanged-key-2"]);
        InteractiveStepDiagnostics.Apply(fixture.Flow, "reorder", () => fixture.Flow.Selection.Matches.Reverse());
        var operation = Assert.Single(fixture.Logger.Events);
        Assert.Equal("selection_changed", operation.Values["ReasonCode"]);
        using var values = JsonDocument.Parse((string)operation.Values["Measurements"]!);
        Assert.Equal(2, values.RootElement.GetProperty("candidates_before").GetInt32());
        Assert.Equal(2, values.RootElement.GetProperty("candidates_after").GetInt32());
        Assert.True(values.RootElement.GetProperty("selection_changed").GetBoolean());
    }

    [Fact]
    public async Task A_failed_stage_retains_its_partial_selection_and_one_terminal_diagnostic()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        await Assert.ThrowsAsync<IOException>(() => InteractiveStepDiagnostics.RunAsync(fixture.Flow, "partial_failure", () =>
        {
            fixture.Flow.Selection.Matches.Add(Match("doc-1", "chunk-1"));
            return Task.FromException(new IOException("private source marker"));
        }));
        var operation = Assert.Single(fixture.Logger.Events);
        Assert.Equal("failed", operation.Values["Outcome"]);
        using var values = JsonDocument.Parse((string)operation.Values["Measurements"]!);
        Assert.Equal(0, values.RootElement.GetProperty("candidates_before").GetInt32());
        Assert.Equal(1, values.RootElement.GetProperty("candidates_after").GetInt32());
        var snapshot = fixture.Flow.Session.Diagnostics.Build(fixture.Flow.Selection.Matches);
        Assert.Equal(1, Assert.Single(snapshot.Phases, phase => phase.Name == "partial_failure.after").Returned);
        Assert.DoesNotContain("private source marker", operation.Text);
    }

    private static RagMatch Match(string document, string chunk)
        => JsonSerializer.Deserialize<RagMatch>(JsonSerializer.Serialize(new { Score = 0.9, DocId = document, ChunkId = chunk }))!;

    private sealed class FlowFixture(NpgsqlDataSource dataSource, InteractiveRetrievalFlow flow,
        DiagnosticRecordingLogger<InteractiveRetrievalPipelineTests> logger) : IAsyncDisposable
    {
        internal InteractiveRetrievalFlow Flow => flow;
        internal DiagnosticRecordingLogger<InteractiveRetrievalPipelineTests> Logger => logger;
        internal static async Task<FlowFixture> CreateAsync()
        {
            var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=never_opened;Username=fixture");
            var logger = new DiagnosticRecordingLogger<InteractiveRetrievalPipelineTests>();
            var resources = new InteractiveRetrievalResources(new(Guid.NewGuid(), "request-17", CancellationToken.None, Logger: logger),
                dataSource, new RagOptions(), new NeverHttpClientFactory());
            var timings = new InteractiveRetrievalTimings();
            var plan = await InteractiveRetrievalPlanner.CreateAsync(resources, new("alpha"), timings);
            var diagnostics = new RagRetrievalDiagnosticsBuilder("private question", "private query", "private selection", "balanced",
                null, null, 12, 60, 3, 1, includeContent: false);
            var session = new InteractiveRetrievalSession(resources, plan, timings, new(logger), new(), diagnostics);
            return new(dataSource, new(session, new([], [], [], string.Empty, false, null, 0, InteractiveInitialRoute.Broad), new(12)), logger);
        }
        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    private sealed class NeverHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("This pure stage must not access HTTP.");
    }
}
