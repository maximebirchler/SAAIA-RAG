using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task SelectionPromptConsistency_repeatable_twenty_coordinates_keep_four_supported_choices_through_critic(
        string style, string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, RepeatableTwentyAnswer()));
        var options = EarlyOptions(protocol, 5);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"),
                new RecordingToolGateway(EarlyBodies()), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(3, factory.Requests.Count);
        foreach (var call in factory.Requests.Skip(1))
        {
            var contract = WorkspaceUser(call.Body).GetProperty("selectionContract");
            Assert.Equal(20, contract.GetProperty("requiredClaimCount").GetInt32());
            Assert.False(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
            Assert.True(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
            var system = SelectionPromptWords(PayloadAuditSystem(call.Body));
            Assert.DoesNotContain("If enough distinct documented candidates exist", system);
            Assert.DoesNotContain("reassigning supported distinct candidates", system);
            Assert.DoesNotContain("the already supported distinct candidates", system);
        }
        Assert.Contains("selectionContract.distinctNamedItemsRequired is true",
            SelectionPromptWords(PayloadAuditSystem(factory.Requests[2].Body)));
        // Scripted replies verify the transported contract and binding. They do
        // not prove that a real model will choose or justify these placements.
    }

    [Theory]
    [InlineData("BuildResearchReviewSystemPrompt")]
    [InlineData("BuildCriticRepairSystemPrompt")]
    public void SelectionPromptConsistency_review_and_repair_do_not_impose_a_unique_choice_per_coordinate(string method)
    {
        var prompt = SelectionPromptWords((string)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!);

        Assert.Contains("selectionContract.distinctNamedItemsRequired is true", prompt);
        Assert.DoesNotContain("seek enough distinct concrete items to fill it", prompt);
        Assert.DoesNotContain("fewer distinct usable candidates than load.rowCount", prompt);
        Assert.DoesNotContain("If that count is below load.answerUnitCount", prompt);
        Assert.DoesNotContain("counts and duplicate selected items. For a complete", prompt);
        Assert.Contains("reuse", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectionPromptConsistency_an_explicit_distinct_load_still_requires_distinct_choices()
    {
        var load = BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option").Handoff.Load;
        var resolved = typeof(OpenAiCompatibleAdvancedAnalysisProvider)
            .GetMethod("BuildResolvedSelectionContract", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [load]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(resolved));
        Assert.Equal("distinct_named_items", json.RootElement.GetProperty("selectionMode").GetString());
        Assert.Equal(20, json.RootElement.GetProperty("requiredClaimCount").GetInt32());
        Assert.True(json.RootElement.GetProperty("distinctNamedItemsRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("namedItemReuseAllowed").GetBoolean());
    }

    private static string SelectionPromptWords(string value) => Regex.Replace(value, @"\s+", " ");
}
