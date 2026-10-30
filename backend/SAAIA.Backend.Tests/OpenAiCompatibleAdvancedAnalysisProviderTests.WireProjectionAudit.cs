using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static void AssertWireProjection(JsonElement prompt, int inputCount, int? projectedCount = null,
        int? selectedCount = null)
    {
        var collection = prompt.TryGetProperty("evidence", out var evidence) ? evidence : prompt.GetProperty("observations");
        var projection = prompt.GetProperty("evidenceProjection");
        Assert.Equal("observed", projection.GetProperty("status").GetString());
        var canonical = projection.GetProperty("canonicalProjection");
        var projected = canonical.GetProperty("projectedCount").GetInt32();
        Assert.Equal(inputCount, canonical.GetProperty("inputCount").GetInt32());
        Assert.Equal(inputCount - projected, canonical.GetProperty("omittedByPromptBudgetCount").GetInt32());
        if (projectedCount is { } expected) Assert.Equal(expected, projected);
        var current = projection.GetProperty("currentCall");
        var count = collection.GetArrayLength();
        Assert.Equal(count, current.GetProperty("visibleCount").GetInt32());
        Assert.Equal(count, current.GetProperty("fromCanonicalProjectionCount").GetInt32()
            + current.GetProperty("outsideCanonicalProjectionCount").GetInt32());
        Assert.Equal(projected - current.GetProperty("fromCanonicalProjectionCount").GetInt32(),
            current.GetProperty("canonicalProjectedNotSelectedCount").GetInt32());
        Assert.Equal(collection.EnumerateArray().Count(item => item.GetProperty("contentTruncated").GetBoolean()),
            current.GetProperty("truncatedContentCount").GetInt32());
        if (selectedCount is { } selected) Assert.Equal(selected, count);
        Assert.Contains("not corpus absence", projection.GetProperty("instruction").GetString());
    }

    private static AdvancedAnalysisResolvedEvidence[] WireProjectionSources()
    {
        // Exercise excerpt projection across thirty chunks, with a small bounded
        // source catalogue. Catalogue-only overflow has its own causal tests.
        var documents = Enumerable.Range(1, 3).Select(_ => (Doc: Guid.NewGuid().ToString("D"),
            Revision: Guid.NewGuid().ToString("D"))).ToArray();
        return Enumerable.Range(1, 30).Select(index => BuildEvidence($"E{index}",
            WireTextFact + new string('x', 3_000), fileName: $"projection-{(index - 1) % documents.Length}.pdf",
            docId: documents[(index - 1) % documents.Length].Doc,
            revisionId: documents[(index - 1) % documents.Length].Revision,
            pageStart: index)).ToArray();
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireProjectionAudit_budget_omissions_are_counted_separately_from_truncated_visible_content(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(WireProjectionSources()), CancellationToken.None);
        var prompt = WorkspaceUser(factory.Requests[1].Body);
        AssertWireProjection(prompt, 30, prompt.GetProperty("evidence").GetArrayLength());
        Assert.InRange(prompt.GetProperty("evidence").GetArrayLength(), 1, 29);
        Assert.True(prompt.GetProperty("evidenceProjection").GetProperty("currentCall").GetProperty("truncatedContentCount").GetInt32() > 0);
        Assert.Equal("not_applicable", WorkspaceUser(factory.Requests[0].Body).GetProperty("evidenceProjection").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireProjectionAudit_review_writer_and_critic_use_their_real_current_projection(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            Completion(CandidateTerminal), Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.NativeResearchTopology = "reviewed";
        options.SemanticCriticEnabled = true; options.MaximumEvidencePromptCharacters = 8_000;
        options.ExternalMaximumCallsPerJob = 6;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(WireProjectionSources()), CancellationToken.None);
        Assert.Equal(4, factory.Requests.Count);
        foreach (var request in factory.Requests.Skip(1)) AssertWireProjection(WorkspaceUser(request.Body), 30);
        Assert.True(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("observations", out _));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireProjectionAudit_writer_and_critic_repairs_keep_the_original_projection_context(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("invalid json"), Completion(NativeAnswered), Completion("invalid json"), Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = true;
        options.MaximumEvidencePromptCharacters = 8_000; options.ExternalMaximumCallsPerJob = 5;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(WireProjectionSources()), CancellationToken.None);
        Assert.Equal(5, factory.Requests.Count);
        foreach (var request in factory.Requests.Skip(1)) AssertWireProjection(WorkspaceUser(request.Body), 30);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireProjectionAudit_staged_roles_distinguish_their_selection_from_prompt_budget_omissions(string protocol)
    {
        var sources = Enumerable.Range(1, 3).Select(index => BuildEvidence($"E{index}",
            $"R{index}\nUne préparation documentée complète.", docId: "same-doc", revisionId: "same-revision", exactTitle: $"R{index}")).ToArray();
        var judge = JsonSerializer.Serialize(new { items = Enumerable.Range(1, 3).Select(index => new
        {
            key = LifecycleCandidateKey($"R{index}"), exactTitle = $"R{index}", sourceKey = "internal-source-1",
            targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(), status = index == 1 ? "body_verified" : "rejected",
            note = "Suitability reviewed.", locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
        }).ToArray() });
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner), Completion(judge),
            Completion("""{"outcome":"navigation_bounded","reason":"No useful untried source locator remains."}"""), Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true; options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 5;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(sources), CancellationToken.None);
        Assert.Equal(4, factory.Requests.Count);
        AssertWireProjection(WorkspaceUser(factory.Requests[1].Body), 3, 3, 3);
        AssertWireProjection(WorkspaceUser(factory.Requests[2].Body), 3, 3, 1);
        AssertWireProjection(WorkspaceUser(factory.Requests[3].Body), 3, 3, 3);
    }
}
