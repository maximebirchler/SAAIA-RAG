using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string RepeatableInventoryPlanner =
        """{"selectionMode":"repeatable_named_items","queries":[{"query":"recettes petit-déjeuner","topK":12}]}""";

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Repeatable_inventory_retains_one_verified_candidate_without_a_twenty_distinct_gap(
        string protocol)
    {
        var update = CandidateUpdate("omelette", "Omelette", "internal-source-1",
            "petit-déjeuner", "body_verified", [], ["E1"]);
        using var factory = new QueuedHttpClientFactory(
            Completion(RepeatableInventoryPlanner),
            protocol == "responses"
                ? NativeResponseFunction("save_candidate_inventory", update)
                : NativeCompletion(("save_candidate_inventory", update)),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateTerminal)
                : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = 3;
        var evidence = BuildEvidence("E1",
            "OMELETTE\nINGRÉDIENTS\n4 œufs. PRÉPARATION\nBattre les œufs puis cuire l'omelette.",
            exactTitle: "Omelette");

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout"),
                new RecordingToolGateway(evidence), CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var inventory = prompt.GetProperty("candidateInventory");
        AssertNoDistinctInventoryGap(inventory);
        Assert.Equal(20, inventory.GetProperty("requiredClaimCount").GetInt32());
        Assert.Equal(20, prompt.GetProperty("load").GetProperty("answerUnitCount").GetInt32());
        Assert.Equal(20, prompt.GetProperty("claimCoordinates").GetArrayLength());
        Assert.True(prompt.GetProperty("selectionContract").GetProperty("namedItemReuseAllowed").GetBoolean());
        Assert.False(inventory.GetProperty("coordinateCoverageEvaluated").GetBoolean());
        Assert.Equal(1, inventory.GetProperty("rememberedBodyVerifiedCandidateCount").GetInt32());
        var breakfast = inventory.GetProperty("observedRoles").EnumerateArray()
            .Single(role => role.GetProperty("targetRole").GetString() == "petit-déjeuner");
        Assert.Equal(5, breakfast.GetProperty("requestedCoordinateCount").GetInt32());
        Assert.Equal(1, breakfast.GetProperty("rememberedBodyVerifiedCandidateCount").GetInt32());
        Assert.Equal("E1", Assert.Single(inventory.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("status").GetString() == "body_verified")
            .GetProperty("bodyEvidenceIds")[0].GetString());
        Assert.Contains("not distinct candidates", inventory.GetProperty("instruction").GetString());
        Assert.Contains("No placement, coverage, readiness", inventory.GetProperty("instruction").GetString());
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Repeatable_inventory_without_verified_bodies_does_not_assert_a_gap_or_complete_coverage(
        string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion(RepeatableInventoryPlanner),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateTerminal)
                : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = 2;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout"),
                new RecordingToolGateway(BuildEvidence("E1", "An observed body awaiting semantic review.")),
                CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[1].Body);
        var inventory = prompt.GetProperty("candidateInventory");
        AssertNoDistinctInventoryGap(inventory);
        Assert.Equal(0, inventory.GetProperty("rememberedBodyVerifiedCandidateCount").GetInt32());
        Assert.False(inventory.GetProperty("coordinateCoverageEvaluated").GetBoolean());
        Assert.Equal(20, prompt.GetProperty("claimCoordinates").GetArrayLength());
        Assert.DoesNotContain("\"ready\":true", inventory.GetRawText(), StringComparison.Ordinal);
        Assert.All(inventory.GetProperty("observedRoles").EnumerateArray(), role =>
        {
            Assert.False(role.TryGetProperty("missingCount", out _));
            Assert.False(role.TryGetProperty("assignedCount", out _));
            Assert.False(role.TryGetProperty("requiredCount", out _));
        });
    }

    [Fact]
    public async Task Four_remembered_candidates_for_twenty_repeatable_cells_do_not_become_a_distinct_gap_or_coverage_approval()
    {
        string[] roles = ["petit-déjeuner", "déjeuner", "collation", "souper"];
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 4).Select(index => new
            {
                key = $"choice-{index}", exactTitle = $"Choix {index}",
                sourceKey = "internal-source-1", targetRoles = new[] { roles[index - 1] },
                selectedRoles = Array.Empty<string>(), status = "body_verified",
                note = "A retained candidate, not a completed placement.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"repeatable_named_items","queries":[{"query":"recettes documentées","topK":12}]}"""),
            NativeCompletion(("save_candidate_inventory", update)), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.ExternalMaximumCallsPerJob = 3;
        var document = Guid.NewGuid().ToString("D");
        var revision = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 4).Select(index => BuildEvidence($"E{index}",
            $"Choix {index}\nINGRÉDIENTS\nDes ingrédients documentés. PRÉPARATION\nPréparer le choix documenté.",
            docId: document, revisionId: revision, pageStart: index, exactTitle: $"Choix {index}")).ToArray();
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout"),
                new RecordingToolGateway(evidence), CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var inventory = prompt.GetProperty("candidateInventory");
        AssertNoDistinctInventoryGap(inventory);
        Assert.Equal(4, inventory.GetProperty("rememberedBodyVerifiedCandidateCount").GetInt32());
        Assert.Equal(20, prompt.GetProperty("claimCoordinates").GetArrayLength());
        Assert.Equal(20, inventory.GetProperty("requiredClaimCount").GetInt32());
        Assert.False(inventory.GetProperty("coordinateCoverageEvaluated").GetBoolean());
        Assert.All(inventory.GetProperty("observedRoles").EnumerateArray(), role =>
        {
            Assert.Equal(5, role.GetProperty("requestedCoordinateCount").GetInt32());
            Assert.Equal(1, role.GetProperty("rememberedBodyVerifiedCandidateCount").GetInt32());
            Assert.False(role.TryGetProperty("assignedCount", out _));
            Assert.False(role.TryGetProperty("missingCount", out _));
        });
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Distinct_inventory_keeps_its_original_twenty_candidate_assignment_contract(
        string protocol)
    {
        var update = CandidateUpdate("omelette", "Omelette", "internal-source-1",
            "petit-déjeuner", "body_verified", [], ["E1"]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            protocol == "responses"
                ? NativeResponseFunction("save_candidate_inventory", update)
                : NativeCompletion(("save_candidate_inventory", update)),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateTerminal)
                : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = 3;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout"),
                new RecordingToolGateway(BuildEvidence("E1", "Omelette\nA concrete substantive body.", exactTitle: "Omelette")),
                CancellationToken.None);

        var inventory = WorkspaceUser(factory.Requests[2].Body).GetProperty("candidateInventory");
        Assert.Equal(20, inventory.GetProperty("requiredDistinctCount").GetInt32());
        Assert.Equal(1, inventory.GetProperty("maximumAssignableCount").GetInt32());
        Assert.Equal(19, inventory.GetProperty("missingByRole").EnumerateObject().Sum(role => role.Value.GetInt32()));
        Assert.False(inventory.TryGetProperty("coordinateCoverageEvaluated", out _));
    }

    [Fact]
    public async Task Planner_bounded_name_quota_is_conditional_on_distinct_selection()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion(RepeatableInventoryPlanner), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.ExternalMaximumCallsPerJob = 2;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout"),
                new RecordingToolGateway(BuildEvidence("E1", "A substantive documented choice.")),
                CancellationToken.None);

        using var envelope = JsonDocument.Parse(factory.Requests[0].Body);
        var system = envelope.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString()!;
        var normalized = System.Text.RegularExpressions.Regex.Replace(system, @"\s+", " ");
        Assert.Contains("Require at least the requested number of distinct names only when distinct selected objects are required.", normalized);
        Assert.Contains("the coordinate count is not a distinct-name quota.", normalized);
    }

    private static void AssertNoDistinctInventoryGap(JsonElement inventory)
    {
        foreach (var name in new[] { "requiredDistinctCount", "maximumAssignableCount", "missingByRole", "proposedAssignments", "coverage" })
            Assert.False(inventory.TryGetProperty(name, out _), $"Repeatable inventory must not publish the distinct-only field {name}.");
    }
}
