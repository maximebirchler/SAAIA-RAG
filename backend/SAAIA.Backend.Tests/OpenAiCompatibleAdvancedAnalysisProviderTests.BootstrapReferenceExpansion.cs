using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Bootstrap_reference_expansion_uses_the_real_four_card_plus_chunk_builder()
    {
        var matches = Enumerable.Range(0, 3).Select(_ => BootstrapFanoutMatch()).ToArray();
        var references = AdvancedAnalysisToolGateway.BuildEvidenceReferences(matches);
        Assert.Equal(15, references.Count);
        Assert.Equal(12, references.Count(reference => reference.ContentCardId is not null));
        Assert.Equal(3, references.Count(reference => reference.ChunkId is not null));
        Assert.Equal(15, references.Select(reference => reference.ContentCardId ?? reference.ChunkId).Distinct().Count());
        foreach (var match in matches)
        {
            Assert.Contains(references, reference => reference.ChunkId == match.ChunkId);
            Assert.All(match.MatchedContentCards!, card =>
                Assert.Contains(references, reference => reference.ContentCardId == card.ContentCardId));
        }
    }

    [Theory]
    [InlineData("responses", 0)]
    [InlineData("chat-completions", 0)]
    [InlineData("responses", 119)]
    [InlineData("chat-completions", 119)]
    public async Task Bootstrap_reference_expansion_preserves_complete_results_and_reports_capacity_limit(
        string protocol, int initialCount)
    {
        var gateway = new BootstrapFanoutGateway(initialCount);
        var plan = JsonSerializer.Serialize(new
        {
            selectionMode = "repeatable_named_items",
            queries = Enumerable.Range(1, 8).Select(index => new
            {
                query = "fanout-scope-" + index, category = "", documentHint = "", topK = 20
            })
        });
        using var factory = new QueuedHttpClientFactory(Completion(plan), EarlyReply(protocol, EarlyInsufficient));
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

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(2, result.ProviderCallCount);
        Assert.Equal(2, factory.Requests.Count); // No retry or extra model call.
        Assert.Empty(gateway.Retired);
        Assert.All(gateway.RotationDuringSearch, value => Assert.False(value));
        Assert.InRange(gateway.MaximumObservedActiveCount, initialCount, 128);
        Assert.All(gateway.Initial.Concat(gateway.Admitted), body => Assert.Contains(gateway.Evidence,
            kept => kept.Reference == body.Reference && kept.Content == body.Content));
        Assert.All(gateway.Searches, search =>
        {
            Assert.Equal("search_corpus", search.Operation);
            Assert.True(string.IsNullOrEmpty(search.Category));
            Assert.True(string.IsNullOrEmpty(search.DocumentHint));
            Assert.Null(search.DocId); Assert.Null(search.DocPath);
            Assert.Null(search.PageStart); Assert.Null(search.PageEnd);
            Assert.Equal(8, search.MaxPerDocument); Assert.Equal(2, search.MaxPerPage);
        });
        var writer = WorkspaceUser(factory.Requests[1].Body);
        var history = writer.GetProperty("researchTools").GetProperty("priorSearches").EnumerateArray().ToArray();
        Assert.Equal(gateway.Searches.Count, history.Length);
        for (var i = 0; i < history.Length; i++)
            Assert.Equal(gateway.Searches[i].TopK, history[i].GetProperty("topK").GetInt32());

        if (initialCount == 0)
        {
            Assert.Equal(Enumerable.Range(1, 8).Select(i => "fanout-scope-" + i), gateway.Searches.Select(s => s.Query));
            Assert.Equal(new[] { 3, 3, 3, 3, 3, 3, 3, 4 }, gateway.Searches.Select(s => s.TopK));
            Assert.Equal(new[] { 15, 15, 15, 15, 15, 15, 15, 20 }, gateway.ResultSizes);
            Assert.Equal(125, gateway.Evidence.Count);
            Assert.Equal(125, gateway.Admitted.Count);
            Assert.Equal(0, gateway.CapacityRefusals);
            Assert.False(writer.TryGetProperty("bootstrapResearch", out _));
            Assert.All(history, prior => Assert.Equal("succeeded", prior.GetProperty("executionStatus").GetString()));
        }
        else
        {
            // Nine free reference slots cannot guarantee eight results that can
            // each expand to five. Keep TopK1 and the original atomic refusal.
            Assert.Equal(new[] { "fanout-scope-1", "fanout-scope-2" }, gateway.Searches.Select(s => s.Query));
            Assert.Equal(new[] { 1, 1 }, gateway.Searches.Select(s => s.TopK));
            Assert.Equal(new[] { 5, 5 }, gateway.ResultSizes);
            Assert.Equal(124, gateway.Evidence.Count);
            Assert.Equal(5, gateway.Admitted.Count);
            Assert.Equal(1, gateway.CapacityRefusals);
            Assert.True(gateway.AtomicRefusalPreservedEveryPriorBody);
            var interruption = writer.GetProperty("bootstrapResearch");
            Assert.Equal("accumulated_evidence_limit_exceeded", interruption.GetProperty("reasonCode").GetString());
            Assert.Equal(124, interruption.GetProperty("consumedEvidenceItems").GetInt32());
            Assert.Equal(5, interruption.GetProperty("requestedNewEvidenceItems").GetInt32());
            Assert.Equal(6, interruption.GetProperty("omittedOperationCount").GetInt32());
            Assert.Equal("succeeded", history[0].GetProperty("executionStatus").GetString());
            Assert.Equal("resource_limit_before_evidence_admission", history[1].GetProperty("executionStatus").GetString());
        }

        var probes = logger.Events.Where(entry => entry.Values.GetValueOrDefault("Operation")?.ToString()
            == "advanced.bootstrap_search_bound").ToArray();
        Assert.Equal(gateway.Searches.Count, probes.Length);
        for (var i = 0; i < probes.Length; i++)
        {
            using var parsed = JsonDocument.Parse(probes[i].Values["Measurements"]!.ToString()!);
            var values = parsed.RootElement;
            Assert.Equal(5, values.GetProperty("maximum_evidence_references_per_match").GetInt32());
            Assert.Equal(initialCount == 0 ? new[] { 25, 22, 19, 16, 13, 10, 7, 4 }[i] : new[] { 1, 0 }[i],
                values.GetProperty("remaining_match_capacity").GetInt32());
            Assert.Equal(initialCount == 0, values.GetProperty("conservative_batch_capacity_sufficient").GetBoolean());
            Assert.Equal(gateway.Searches[i].TopK, values.GetProperty("effective_top_k").GetInt32());
            Assert.Equal(8 - i, values.GetProperty("remaining_bootstrap_operations").GetInt32());
            Assert.False(values.GetProperty("working_set_rotation_enabled").GetBoolean());
            Assert.Equal(0, values.GetProperty("diagnostic_dropped_measurements").GetInt32());
            Assert.Equal(0, values.GetProperty("diagnostic_truncated_values").GetInt32());
            Assert.DoesNotContain("fanout-scope", values.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static RagMatch BootstrapFanoutMatch()
        => new(0.9, "11111111-1111-4111-8111-111111111111", "Documents/menus.pdf", "menus.pdf", 1, 1,
            Guid.NewGuid().ToString("D"), 0, "Canonical chunk.", 1, "sha256:test", "Canonical chunk.",
            "source_text_v1", 1, 1, "Documented item", "Documented item", "content", null, null, null,
            MatchedContentCards: Enumerable.Range(0, 4).Select(i => new RagMatchedContentCard(
                "Canonical card " + i, Guid.NewGuid().ToString("D"), 1, 1, "unit_lead")).ToArray());

    private sealed class BootstrapFanoutGateway : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisToolGateway _active;
        public BootstrapFanoutGateway(int initialCount)
        {
            Initial = Enumerable.Range(0, initialCount).Select(i => WorkingSetBody(
                "fanout-old-" + i, "Previously admitted canonical body " + i + ".")).ToArray();
            _active = WorkingSetGateway(128, Initial);
        }
        public AdvancedAnalysisResolvedEvidence[] Initial { get; }
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public List<AdvancedAnalysisResolvedEvidence> Admitted { get; } = [];
        public List<string> Retired { get; } = [];
        public List<int> ResultSizes { get; } = [];
        public List<bool> RotationDuringSearch { get; } = [];
        public int CapacityRefusals { get; private set; }
        public bool AtomicRefusalPreservedEveryPriorBody { get; private set; }
        public int MaximumObservedActiveCount { get; private set; }
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active.Evidence;
        public AdvancedAnalysisToolBudget Budget => _active.Budget with { ConsumedCalls = Searches.Count };
        public void BeginEvidenceBatch(IReadOnlyCollection<string> ids) => _active.BeginEvidenceBatch(ids);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Searches.Add(request);
            RotationDuringSearch.Add(_active.Budget.EvidenceRotationEnabled);
            // Use the actual production builder. The fixture does not assume
            // that TopK is already an evidence-reference count.
            var references = AdvancedAnalysisToolGateway.BuildEvidenceReferences(
                Enumerable.Range(0, request.TopK).Select(_ => BootstrapFanoutMatch()).ToArray());
            var bodies = references.Select(reference => new AdvancedAnalysisResolvedEvidence(
                new AdvancedAnalysisResultEvidence
                {
                    EvidenceId = "fanout-" + (reference.ContentCardId ?? reference.ChunkId),
                    DocId = reference.DocId, FileName = reference.FileName, DocPath = reference.DocPath,
                    RevisionId = "22222222-2222-4222-8222-222222222222", SourceHash = "sha256:test",
                    ContentCardId = reference.ContentCardId, ChunkId = reference.ChunkId,
                    PageStart = reference.PageStart, PageEnd = reference.PageEnd
                }, "Complete canonical body " + (reference.ContentCardId ?? reference.ChunkId) + ".", null)).ToArray();
            ResultSizes.Add(bodies.Length);
            var before = _active.Evidence.ToArray();
            try { Retired.AddRange(_active.AdmitRevalidatedEvidence(bodies)); }
            catch (AdvancedAnalysisToolException error) when (error.ErrorCode == "accumulated_evidence_limit_exceeded")
            {
                CapacityRefusals++;
                AtomicRefusalPreservedEveryPriorBody = before.SequenceEqual(_active.Evidence);
                throw;
            }
            Admitted.AddRange(bodies);
            MaximumObservedActiveCount = Math.Max(MaximumObservedActiveCount, Evidence.Count);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, bodies, [], 1, Searches.Count));
        }
    }
}
