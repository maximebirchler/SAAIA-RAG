using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Bootstrap_first_capacity_refusal_with_prefilled_handoff_is_still_fatal(string protocol)
    {
        var initial = Enumerable.Range(0, 128)
            .Select(i => WorkingSetBody("retained-" + i, "Previously admitted canonical body " + i))
            .ToArray();
        var incoming = Enumerable.Range(0, 19)
            .Select(i => WorkingSetBody("new-" + i, "New complete canonical body " + i))
            .ToArray();
        var gateway = new PrefilledBootstrapBoundaryGateway(initial, incoming);
        var request = BuildDirectRequest() with { Evidence = initial };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"first new canonical fact","topK":19}]}"""));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = false;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(request, gateway, CancellationToken.None));

        Assert.Equal("accumulated_evidence_limit_exceeded", error.ErrorCode);
        Assert.NotNull(error.ResourceLimit);
        Assert.Equal(128, error.ResourceLimit.MaximumEvidenceItems);
        Assert.Equal(128, error.ResourceLimit.ConsumedEvidenceItems);
        Assert.Equal(19, error.ResourceLimit.RequestedNewEvidenceItems);
        Assert.True(error.ResourceLimit.StopsResearch);
        Assert.Single(factory.Requests); // The Planner ran; no Writer may soften this first refusal.
        Assert.Equal("first new canonical fact", Assert.Single(gateway.Searches).Query);
        Assert.Equal(19, gateway.Searches[0].TopK);
        Assert.Equal(0, gateway.RotationActivations);
        Assert.Equal(128, gateway.Evidence.Count);
        Assert.Equal(initial, gateway.Evidence);
        Assert.DoesNotContain(gateway.Evidence, item => item.Reference.EvidenceId.StartsWith("new-", StringComparison.Ordinal));
    }

    private sealed class PrefilledBootstrapBoundaryGateway(
        AdvancedAnalysisResolvedEvidence[] initial,
        AdvancedAnalysisResolvedEvidence[] incoming) : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisToolGateway _active = WorkingSetGateway(128, initial);
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public int RotationActivations { get; private set; }
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active.Evidence;
        public AdvancedAnalysisToolBudget Budget => _active.Budget with { ConsumedCalls = Searches.Count };

        public void BeginEvidenceBatch(IReadOnlyCollection<string> ids)
        {
            RotationActivations++;
            _active.BeginEvidenceBatch(ids);
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            // Use the real atomic admission: the entire new result must be refused
            // before any body is evicted or added while bootstrap rotation is off.
            _active.AdmitRevalidatedEvidence(incoming);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(
                request.Query, incoming, [], 1, Searches.Count));
        }
    }
}
