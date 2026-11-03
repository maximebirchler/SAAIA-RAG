using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", 0)]
    [InlineData("chat-completions", 0)]
    [InlineData("responses", 119)]
    [InlineData("chat-completions", 119)]
    public async Task BootstrapBoundedSearch_all_eight_scopes_run_under_the_same_cap_without_eviction_or_extra_calls(
        string protocol, int initialCount)
    {
        var gateway = new BootstrapBoundedSearchGateway(initialCount);
        var plan = JsonSerializer.Serialize(new
        {
            selectionMode = "repeatable_named_items",
            queries = Enumerable.Range(1, 8).Select(index => new
            {
                query = "bounded-scope-" + index,
                category = "",
                documentHint = "",
                topK = 20
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(plan), EarlyReply(protocol, EarlyInsufficient));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = false;
        options.MaximumPlanQueries = 8;
        options.ExternalMaximumCallsPerJob = 2;
        options.MaximumEvidencePromptCharacters = 65_536;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var request = BuildRequest(20, "named_item", "structured_layout", "documented_option") with
        {
            Evidence = gateway.Initial
        };

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        // This is an orchestration/capacity test, not a scripted claim that the
        // new observations support a meal plan. No extra model retry is used.
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => "bounded-scope-" + i),
            gateway.Searches.Select(search => search.Query));
        Assert.Equal(0, gateway.CapacityRefusals);
        Assert.Empty(gateway.Retired);
        Assert.Equal(128, gateway.Evidence.Count);
        Assert.True(gateway.MaximumObservedActiveCount <= 128);
        Assert.All(gateway.Initial, old => Assert.Contains(gateway.Evidence,
            item => item.Reference == old.Reference && item.Content == old.Content));
        Assert.All(gateway.Returned, body => Assert.Contains(gateway.Evidence,
            item => item.Reference == body.Reference && item.Content == body.Content));
        Assert.All(gateway.Searches, search =>
        {
            Assert.Equal("search_corpus", search.Operation);
            Assert.True(string.IsNullOrEmpty(search.Category));
            Assert.True(string.IsNullOrEmpty(search.DocumentHint));
            Assert.Null(search.DocId);
            Assert.Null(search.DocPath);
            Assert.Null(search.PageStart);
            Assert.Null(search.PageEnd);
            Assert.InRange(search.TopK, 1, 20);
        });
        var writer = WorkspaceUser(factory.Requests[1].Body);
        Assert.False(writer.TryGetProperty("bootstrapResearch", out _));
        var history = writer.GetProperty("researchTools").GetProperty("priorSearches").EnumerateArray().ToArray();
        Assert.Equal(8, history.Length);
        for (var index = 0; index < history.Length; index++)
        {
            Assert.Equal("succeeded", history[index].GetProperty("executionStatus").GetString());
            Assert.Equal(gateway.Searches[index].TopK, history[index].GetProperty("topK").GetInt32());
        }
        var bounds = logger.Events.Where(entry =>
            entry.Values.GetValueOrDefault("Operation")?.ToString() == "advanced.bootstrap_search_bound").ToArray();
        Assert.Equal(8, bounds.Length);
        foreach (var entry in bounds)
        {
            using var measurement = JsonDocument.Parse(entry.Values["Measurements"]!.ToString()!);
            var value = measurement.RootElement;
            Assert.Equal(20, value.GetProperty("requested_top_k").GetInt32());
            Assert.InRange(value.GetProperty("effective_top_k").GetInt32(), 1, 19);
            Assert.Equal(128, value.GetProperty("maximum_evidence_items").GetInt32());
            Assert.Equal(0, value.GetProperty("diagnostic_dropped_measurements").GetInt32());
            Assert.Equal(0, value.GetProperty("diagnostic_truncated_values").GetInt32());
            Assert.DoesNotContain("bounded-scope", value.GetRawText(), StringComparison.Ordinal);
        }
    }

    private sealed class BootstrapBoundedSearchGateway : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisToolGateway _active;
        public BootstrapBoundedSearchGateway(int initialCount)
        {
            Initial = Enumerable.Range(0, initialCount)
                .Select(i => WorkingSetBody("bounded-old-" + i, "Prior complete canonical body " + i + "."))
                .ToArray();
            _active = WorkingSetGateway(128, Initial);
        }

        public AdvancedAnalysisResolvedEvidence[] Initial { get; }
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public List<AdvancedAnalysisResolvedEvidence> Returned { get; } = [];
        public List<string> Retired { get; } = [];
        public int CapacityRefusals { get; private set; }
        public int MaximumObservedActiveCount { get; private set; }
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active.Evidence;
        public AdvancedAnalysisToolBudget Budget => _active.Budget with { ConsumedCalls = Searches.Count };
        public void BeginEvidenceBatch(IReadOnlyCollection<string> ids) => _active.BeginEvidenceBatch(ids);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            var ordinal = Searches.Count;
            // The old path reaches119 after six successful scopes, rejects the
            // seventh twenty-item admission and never executes the eighth.
            var available = ordinal == 6 ? 19 : 20;
            var bodies = Enumerable.Range(0, available).Take(request.TopK)
                .Select(i => WorkingSetBody("bounded-" + ordinal + "-" + i,
                    "Complete canonical body for scope " + ordinal + ", item " + i + "."))
                .ToArray();
            try { Retired.AddRange(_active.AdmitRevalidatedEvidence(bodies)); }
            catch (AdvancedAnalysisToolException error) when (error.ErrorCode == "accumulated_evidence_limit_exceeded")
            {
                CapacityRefusals++;
                throw;
            }
            Returned.AddRange(bodies);
            MaximumObservedActiveCount = Math.Max(MaximumObservedActiveCount, Evidence.Count);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, bodies, [], 1, Searches.Count));
        }
    }
}
