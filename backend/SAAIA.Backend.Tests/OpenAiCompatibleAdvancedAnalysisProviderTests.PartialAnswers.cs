using System.Text.Json;
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
    public async Task Partial_findings_survive_writer_critic_and_result_validation(string style, string protocol)
    {
        const string partial = """
            {"outcome":"insufficient_documentation",
             "answerText":"Réponse partielle. OptionA est documentée [C1]. OptionB est documentée [C2]. Les autres choix restent à établir.",
             "claims":[
               {"claimId":"C1","selectedItem":"OptionA","text":"OptionA est documentée.","evidenceIds":["E1"]},
               {"claimId":"C2","selectedItem":"OptionB","text":"OptionB est documentée.","evidenceIds":["E2"]}]}
            """;
        var bodies = EarlyBodies();
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"documented options","topK":12}]}"""),
            EarlyReply(protocol, partial), EarlyReply(protocol, partial));
        var options = EarlyOptions(protocol, 3);
        options.SemanticCriticEnabled = true;
        options.SynthesisPromptStyle = style;
        var request = BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option");

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(bodies), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(3, result.ProviderCallCount);
        foreach (var call in factory.Requests.Skip(1))
        {
            var user = WorkspaceUser(call.Body);
            Assert.True(user.GetProperty("allowsPartialAnswer").GetBoolean());
            Assert.Equal(20, user.GetProperty("selectionContract").GetProperty("requiredClaimCount").GetInt32());
            var system = SelectionPromptWords(PayloadAuditSystem(call.Body));
            Assert.Contains("clearly labeled partial answer", system);
            Assert.DoesNotContain("partial answers are not allowed", system);
        }
        var candidate = WorkspaceUser(factory.Requests[2].Body).GetProperty("candidate");
        Assert.Equal(2, candidate.GetProperty("claims").GetArrayLength());
        var validated = AdvancedAnalysisResultValidator.ValidateAndBuild(
            "fake-internal", result, bodies, 10, DateTimeOffset.UtcNow);
        Assert.True(validated.IsValid, validated.ErrorCode);
        Assert.Equal(result.AnswerText, validated.Result!.AnswerText);
        Assert.Equal("insufficient_documentation", validated.Result.Outcome);
        Assert.Equal(new[] { "E1", "E2" }, validated.Result.Evidence.Select(item => item.EvidenceId));
    }
}
