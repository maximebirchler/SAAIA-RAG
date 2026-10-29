using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static AdvancedAnalysisResolvedEvidence WorkingSetBody(string id, string content)
        => BuildEvidence(id, content, docId: "11111111-1111-4111-8111-111111111111",
            revisionId: "22222222-2222-4222-8222-222222222222");

    private static AdvancedAnalysisToolGateway WorkingSetGateway(
        int capacity, params AdvancedAnalysisResolvedEvidence[] items)
        => new(Guid.NewGuid(), Guid.NewGuid(), items, null!, new RagOptions(), null!, null!, null!, null!,
            new AdvancedAnalysisOptions { MaximumAccumulatedEvidenceItems = capacity });

    [Fact]
    public void Working_set_full_rotates_only_unprotected_old_proofs_and_retains_source_handle()
    {
        var initial = Enumerable.Range(0, 128).Select(i => WorkingSetBody("E" + i, "Documented body " + i)).ToArray();
        var gateway = WorkingSetGateway(128, initial);
        gateway.BeginEvidenceBatch(["E1", "E2"]);
        Assert.Equal(0, gateway.Budget.RemainingEvidenceItems);
        Assert.True(gateway.Budget.EvidenceOperationsAllowed);
        Assert.Equal(126, gateway.Budget.ReplaceableEvidenceItems);
        var next = WorkingSetBody("new", "Newly revalidated exact body.");
        var retired = gateway.AdmitRevalidatedEvidence([next]);
        Assert.Equal(["E0"], retired);
        Assert.Equal(128, gateway.Evidence.Count);
        Assert.Contains(gateway.Evidence, e => e.Reference.EvidenceId == "E2");
        Assert.Contains(gateway.Evidence, e => e.Reference.EvidenceId == "E1");
        Assert.Contains(gateway.Evidence, e => e.Reference.EvidenceId == "new");
        Assert.DoesNotContain(gateway.Evidence, e => e.Reference.EvidenceId == "E0");
        Assert.Equal("Newly revalidated exact body.", gateway.Evidence.Single(e => e.Reference.EvidenceId == "new").Content);
    }

    [Fact]
    public void Working_set_batch_results_are_pinned_and_rejected_result_is_wholly_absent()
    {
        var a = WorkingSetBody("A", "Initial canonical body.");
        var b = WorkingSetBody("B", "First complete returned body.");
        var c = WorkingSetBody("C", "Second body must not partially replace a result.");
        var gateway = WorkingSetGateway(2, a);
        gateway.BeginEvidenceBatch(["A"]);
        Assert.Empty(gateway.AdmitRevalidatedEvidence([b]));
        var error = Assert.Throws<AdvancedAnalysisToolException>(() => gateway.AdmitRevalidatedEvidence([c]));
        Assert.Equal("accumulated_evidence_limit_exceeded", error.ErrorCode);
        Assert.False(error.ResourceLimit!.StopsResearch);
        Assert.Equal(new[] { "A", "B" }, gateway.Evidence.Select(e => e.Reference.EvidenceId));
        Assert.Empty(gateway.AdmitRevalidatedEvidence([a, b]));
        Assert.True(gateway.Budget.EvidenceOperationsAllowed);
        Assert.Equal(0, gateway.Budget.ReplaceableEvidenceItems);
    }

    [Fact]
    public void Working_set_result_larger_than_capacity_cannot_evict_any_prior_body()
    {
        var gateway = WorkingSetGateway(2, WorkingSetBody("A", "Source carrier."), WorkingSetBody("B", "Old body."));
        gateway.BeginEvidenceBatch([]);
        var before = gateway.Evidence;
        var error = Assert.Throws<AdvancedAnalysisToolException>(() => gateway.AdmitRevalidatedEvidence(
            [WorkingSetBody("C", "whole1"), WorkingSetBody("D", "whole2"), WorkingSetBody("E", "whole3")]));
        Assert.False(error.ResourceLimit!.StopsResearch);
        Assert.Equal(before, gateway.Evidence);
        Assert.Equal(3, error.ResourceLimit.RequestedNewEvidenceItems);
    }

    [Fact]
    public void Working_set_next_prompt_releases_old_unreferenced_batch_results_but_not_new_pins()
    {
        var gateway = WorkingSetGateway(3, WorkingSetBody("A", "Source carrier."), WorkingSetBody("B", "Old body."));
        gateway.BeginEvidenceBatch(["A"]);
        gateway.AdmitRevalidatedEvidence([WorkingSetBody("C", "First result.")]);
        gateway.BeginEvidenceBatch(["A", "C"]);
        Assert.Equal(["B"], gateway.AdmitRevalidatedEvidence([WorkingSetBody("D", "Next result.")]));
        Assert.Equal(new[] { "A", "C", "D" }, gateway.Evidence.Select(e => e.Reference.EvidenceId));
    }

    [Fact]
    public void Working_set_cannot_pin_unknown_ids_or_accept_a_changed_identity_under_an_existing_id()
    {
        var gateway = WorkingSetGateway(2, WorkingSetBody("A", "Original."));
        Assert.Throws<AdvancedAnalysisToolException>(() => gateway.BeginEvidenceBatch(["unknown"]));
        gateway.BeginEvidenceBatch(["A"]);
        // A different canonical locator under the old ID is never an eviction.
        var other = WorkingSetBody("other", "Different body.");
        var changed = other with { Reference = new()
        {
            EvidenceId = "A", DocId = other.Reference.DocId, RevisionId = other.Reference.RevisionId,
            SourceHash = other.Reference.SourceHash, DocPath = other.Reference.DocPath,
            ChunkId = other.Reference.ChunkId, PageStart = other.Reference.PageStart, PageEnd = other.Reference.PageEnd
        }};
        Assert.Equal("evidence_admission_identity_changed", Assert.Throws<AdvancedAnalysisToolException>(
            () => gateway.AdmitRevalidatedEvidence([changed])).ErrorCode);
        Assert.Equal("Original.", Assert.Single(gateway.Evidence).Content);
    }

    [Fact]
    public void Working_set_local_refusal_can_retry_same_result_after_releasing_unneeded_pins()
    {
        var gateway = WorkingSetGateway(2, WorkingSetBody("A", "Kept."), WorkingSetBody("B", "Unneeded."));
        var incoming = WorkingSetBody("C", "Complete new body.");
        gateway.BeginEvidenceBatch(["A", "B"]);
        Assert.False(Assert.Throws<AdvancedAnalysisToolException>(() => gateway.AdmitRevalidatedEvidence([incoming])).ResourceLimit!.StopsResearch);
        gateway.BeginEvidenceBatch(["A"]);
        Assert.Equal(["B"], gateway.AdmitRevalidatedEvidence([incoming]));
        Assert.Equal(new[] { "A", "C" }, gateway.Evidence.Select(item => item.Reference.EvidenceId));
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Working_set_full_keeps_research_available_and_new_body_citable_after_rotation(string protocol)
    {
        var initial = Enumerable.Range(0, 128).Select(i => WorkingSetBody("E" + i, "General documented context " + i)).ToArray();
        var target = WorkingSetBody("target", "Le seuil est 17 bar.");
        const string arguments = """{"query":"exact new fact","sourceKey":"","category":"","documentHint":"","topK":1}""";
        const string answer = """{"outcome":"answered","answerText":"Le seuil est 17 bar [C1].","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["target"]}]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("search_corpus", arguments) : NativeCompletion(("search_corpus", arguments)),
            protocol == "responses" ? NativeResponseAnswer(answer) : Completion(answer));
        var gateway = new WorkingSetProviderGateway(128, initial, [target]);
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = false;
        options.MaximumEvidencePromptCharacters = 8_000; options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal("target", Assert.Single(result.Claims).EvidenceIds[0]);
        Assert.Equal(128, gateway.Evidence.Count);
        Assert.Equal(2, gateway.Searches.Count);
        using var request = JsonDocument.Parse(factory.Requests[1].Body);
        Assert.Contains(request.RootElement.GetProperty("tools").EnumerateArray(), t =>
            (protocol == "responses" ? t.GetProperty("name") : t.GetProperty("function").GetProperty("name")).GetString() == "search_corpus");
        var prompt = request.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Single(m => m.TryGetProperty("role", out var role) && role.GetString() == "user").GetProperty("content").GetString()!;
        using var user = JsonDocument.Parse(prompt);
        Assert.True(user.RootElement.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.Equal(0, user.RootElement.GetProperty("researchTools").GetProperty("documentaryBudget").GetProperty("remainingEvidenceItems").GetInt32());
        Assert.True(user.RootElement.GetProperty("researchTools").GetProperty("documentaryBudget").GetProperty("evidenceRotationEnabled").GetBoolean());
        foreach (var id in user.RootElement.GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("evidenceId").GetString()))
            Assert.Contains(gateway.Evidence, e => e.Reference.EvidenceId == id);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Working_set_fully_pinned_refusal_is_local_and_an_existing_read_in_same_batch_still_runs(string protocol)
    {
        var a = WorkingSetBody("A", "Le seuil est 17 bar.");
        var b = WorkingSetBody("B", "Other grounded retained fact.");
        const string search = """{"query":"new fact","sourceKey":"","category":"","documentHint":"","topK":1}""";
        const string read = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":1}""";
        const string answer = """{"outcome":"answered","answerText":"Le seuil est 17 bar [C1].","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["A"]}]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? BudgetNativeResponseFunctions(("search_corpus", search), ("read_source", read))
                : NativeCompletion(("search_corpus", search), ("read_source", read)),
            protocol == "responses" ? NativeResponseAnswer(answer) : Completion(answer));
        var gateway = new WorkingSetProviderGateway(2, [a, b], [WorkingSetBody("C", "Cannot replace protected proofs.")], true);
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = false; options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(3, gateway.Searches.Count);
        Assert.Equal(new[] { "A", "B" }, gateway.Evidence.Select(item => item.Reference.EvidenceId));
        Assert.Contains("resource_limit_before_evidence_admission", factory.Requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("not_executed_after_resource_limit", factory.Requests[2].Body, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(factory.Requests[2].Body);
        var input = parsed.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray();
        var output = input.First(m => protocol == "responses"
            ? m.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : m.GetProperty("role").GetString() == "tool");
        using var refusal = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.False(refusal.RootElement.GetProperty("resourceLimit").GetProperty("stopsResearch").GetBoolean());
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Working_set_local_refusal_does_not_blacklist_the_query_after_a_valid_workspace_update(string protocol)
    {
        const string query = """{"query":"targeted retry","sourceKey":"","category":"","documentHint":"","topK":1}""";
        const string rejected = """{"items":[{"key":"old","label":"Old option","purpose":"research","status":"rejected","note":"Discard previous choice","evidenceIds":["A"]}]}""";
        const string answer = """{"outcome":"answered","answerText":"Le seuil est 17 bar [C1].","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["A"]}]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("search_corpus", query) : NativeCompletion(("search_corpus", query)),
            protocol == "responses" ? BudgetNativeResponseFunctions(("save_research_state", rejected), ("search_corpus", query))
                : NativeCompletion(("save_research_state", rejected), ("search_corpus", query)),
            protocol == "responses" ? NativeResponseAnswer(answer) : Completion(answer));
        var gateway = new WorkingSetProviderGateway(2, [WorkingSetBody("A", "Le seuil est 17 bar.")], [], transientCapacityRefusal: true);
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = false; options.ExternalMaximumCallsPerJob = 5;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(3, gateway.Searches.Count);
        Assert.Equal("targeted retry", gateway.Searches[1].Query); Assert.Equal(gateway.Searches[1], gateway.Searches[2]);
        Assert.Contains("resource_limit_before_evidence_admission", factory.Requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("already_executed", factory.Requests[3].Body, StringComparison.Ordinal);
    }

    private sealed class WorkingSetProviderGateway(int capacity,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initial,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> next, bool existingReads = false,
        bool transientCapacityRefusal = false) : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisToolGateway _active = WorkingSetGateway(capacity);
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active.Evidence;
        public AdvancedAnalysisToolBudget Budget => _active.Budget with { ConsumedCalls = Searches.Count };
        public void BeginEvidenceBatch(IReadOnlyCollection<string> ids) => _active.BeginEvidenceBatch(ids);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Searches.Add(request);
            if (transientCapacityRefusal && Searches.Count == 2)
                throw new AdvancedAnalysisToolException("accumulated_evidence_limit_exceeded",
                    new("accumulated_evidence_limit_exceeded", capacity, capacity, 1, StopsResearch: false));
            var result = Searches.Count == 1 ? initial : transientCapacityRefusal
                || existingReads && request.Operation == "read_source" ? initial.Take(1).ToArray() : next;
            _active.AdmitRevalidatedEvidence(result);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, result, [], 1, Searches.Count));
        }
    }
}
