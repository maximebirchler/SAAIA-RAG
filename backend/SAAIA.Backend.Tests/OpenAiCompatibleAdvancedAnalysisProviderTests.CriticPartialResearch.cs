using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string CriticPartialChoice = """{"outcome":"insufficient_documentation","answerText":"OptionB reste documentée [C7]; les autres choix sont à revoir.","claims":[{"claimId":"C7","selectedItem":"OptionB","text":"OptionB documentée.","evidenceIds":["E2"]}]}""";

    [Theory]
    [InlineData("responses", "agent", false)]
    [InlineData("responses", "agent", true)]
    [InlineData("responses", "contract", false)]
    [InlineData("responses", "contract", true)]
    [InlineData("chat-completions", "agent", false)]
    [InlineData("chat-completions", "agent", true)]
    [InlineData("chat-completions", "contract", false)]
    [InlineData("chat-completions", "contract", true)]
    public async Task CriticPartialResearch_reopens_a_new_gap_with_tools_and_keeps_the_latest_critic_candidate(
        string protocol, string style, bool completed)
    {
        var bodies = EarlyBodies();
        var alternative = BuildEvidence("E5", "OptionE\nComposition, conditions et procédure documentées.",
            docId: bodies[0].Reference.DocId, revisionId: bodies[0].Reference.RevisionId,
            pageStart: 8, exactTitle: "OptionE");
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? [.. bodies, EarlyLocator(bodies[0])] : [alternative], [], 1, number));
        var final = JsonNode.Parse(completed ? RepeatableTwentyAnswer() : CriticPartialChoice)!.AsObject();
        final["claims"]![0]!["selectedItem"] = "OptionE";
        final["claims"]![0]!["text"] = "OptionE documentée.";
        final["claims"]![0]!["evidenceIds"] = new JsonArray("E5");
        final["answerText"] = completed
            ? final["answerText"]!.GetValue<string>().Replace("OptionA [C1]", "OptionE [C1]", StringComparison.Ordinal)
            : "OptionE documentée [C7]; les autres choix restent à vérifier.";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, CriticPartialChoice),
            PostResearchFunction(protocol, "find_source_text", """{"sourceKey":"internal-source-1","query":"OptionE","offset":0,"topK":3}"""),
            EarlyReply(protocol, final.ToJsonString()));
        var options = EarlyOptions(protocol, 6);
        options.SemanticCriticEnabled = true; options.SynthesisPromptStyle = style;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(SynthesisPurposeRequest(false), gateway, CancellationToken.None);

        Assert.Equal(completed ? "answered" : "insufficient_documentation", result.Outcome);
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal("find_source_text", gateway.Searches[1].Operation);
        Assert.Equal("E5", Assert.Single(result.Claims[0].EvidenceIds));
        var review = WorkspaceUser(factory.Requests[3].Body);
        Assert.True(review.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.True(review.TryGetProperty("insufficiencyReview", out _));
        foreach (var index in new[] { 3, 4 })
        {
            var prompt = WorkspaceUser(factory.Requests[index].Body);
            var candidate = prompt.GetProperty("candidate");
            Assert.Equal("insufficient_documentation", candidate.GetProperty("outcome").GetString());
            var claim = Assert.Single(candidate.GetProperty("claims").EnumerateArray());
            Assert.Equal("C7", claim.GetProperty("claimId").GetString());
            Assert.Equal("OptionB", claim.GetProperty("selectedItem").GetString());
            Assert.Empty(prompt.GetProperty("citationReview").GetProperty("answeredPlacements").EnumerateArray());
        }
        Assert.False(WorkspaceUser(factory.Requests[4].Body).TryGetProperty("insufficiencyReview", out _));
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("ReasonCode")?.ToString() == "latest_critic_partial_candidate");
        Assert.DoesNotContain(factory.Requests, call => PayloadAuditSystem(call.Body)
            .Contains("final SAAIA synthesis completer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, 32)]
    [InlineData(6, 1)]
    public async Task CriticPartialResearch_respects_remaining_model_and_tool_budgets(int calls, int tools)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            [.. bodies, EarlyLocator(bodies[0])], [], 1, number), maximumCalls: tools);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeResponseAnswer(RepeatableTwentyAnswer()), NativeResponseAnswer(CriticPartialChoice),
            Completion(CriticPartialChoice));
        var options = EarlyOptions("responses", calls); options.SemanticCriticEnabled = true;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(SynthesisPurposeRequest(false), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(gateway.Searches);
        Assert.InRange(result.ProviderCallCount, 3, calls);
        Assert.DoesNotContain(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("insufficiencyReview", out _));
    }
}
