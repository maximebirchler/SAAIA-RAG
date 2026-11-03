using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", false)]
    [InlineData("chat-completions", true)]
    public async Task Repeatable_identity_correction_uses_visible_passages_even_with_optional_feedback_disabled(
        string protocol, bool criticDefect)
    {
        var request = ContextualPlacementRequest(structured: true);
        var valid = ContextualPlacementAnswer(request.Handoff.Load);
        var invalid = UnsupportedRepeatableIdentity(valid);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, criticDefect ? valid : invalid),
            EarlyReply(protocol, criticDefect ? invalid : valid), EarlyReply(protocol, valid));
        var options = EarlyOptions(protocol, 4);
        options.SemanticCriticEnabled = true;
        options.CandidateBindingFeedbackEnabled = false;
        var gateway = new RecordingToolGateway(ContextualPlacementBodies());

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal("OptionA", result.Claims.Single(claim => claim.ClaimId == "C9").SelectedItem);
        Assert.Single(gateway.Searches);
        var correction = WorkspaceUser(factory.Requests[criticDefect ? 3 : 2].Body);
        var issue = Assert.Single(correction.GetProperty("candidateSupportCorrections")
            .GetProperty("corrections").EnumerateArray());
        Assert.Equal("C9", issue.GetProperty("claimId").GetString());
        Assert.Equal("candidate_identity_not_supported", issue.GetProperty("reasonCode").GetString());
        var cited = Assert.Single(correction.GetProperty("evidence").EnumerateArray(),
            item => item.GetProperty("evidenceId").GetString() == "E1");
        Assert.StartsWith("OptionA\n", cited.GetProperty("content").GetString());
        Assert.DoesNotContain("Unsupported documentary identity", cited.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeatable_identity_exhaustion_returns_bounded_insufficiency_without_claims(bool criticDefect)
    {
        var request = ContextualPlacementRequest(structured: true);
        var valid = ContextualPlacementAnswer(request.Handoff.Load);
        var invalid = UnsupportedRepeatableIdentity(valid);
        using var factory = new QueuedHttpClientFactory(criticDefect
            ? [Completion(RepeatablePlan), Completion(valid), Completion(invalid)]
            : [Completion(RepeatablePlan), Completion(invalid)]);
        var maximumCalls = criticDefect ? 3 : 2;
        var options = EarlyOptions("chat-completions", maximumCalls);
        options.SemanticCriticEnabled = criticDefect;
        options.CandidateBindingFeedbackEnabled = false;
        var gateway = new RecordingToolGateway(ContextualPlacementBodies());

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Contains("passages examinés", result.AnswerText);
        Assert.DoesNotContain("Unsupported documentary identity", result.AnswerText);
        Assert.Equal(maximumCalls, result.ProviderCallCount);
        Assert.Equal(maximumCalls, factory.Requests.Count);
        Assert.Single(gateway.Searches);
    }

    private static string UnsupportedRepeatableIdentity(string valid)
    {
        var answer = JsonNode.Parse(valid)!.AsObject();
        var claim = answer["claims"]![8]!;
        claim["selectedItem"] = "Unsupported documentary identity";
        claim["text"] = "Unsupported documentary identity est documentée.";
        answer["answerText"] = answer["answerText"]!.GetValue<string>()
            .Replace("OptionA [C9]", "Unsupported documentary identity [C9]", StringComparison.Ordinal);
        return answer.ToJsonString();
    }
}
