using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void PartialDraftContinuity_bounds_untrusted_text_and_whole_claims_and_marks_visibility(int count)
    {
        var draft = new AdvancedAnalysisProviderResult
        {
            Outcome = "insufficient_documentation",
            AnswerText = new string('x', 1_999) + "\U0001F642" + "TAIL",
            Claims = Enumerable.Range(1, count).Select(i => new AdvancedAnalysisResultClaim
            {
                ClaimId = "C" + i, SelectedItem = "Option" + i,
                Text = new string('é', 700), EvidenceIds = ["E" + i]
            }).ToList()
        };
        var payload = OpenAiCompatibleAdvancedAnalysisProvider.BuildPartialDraftForPrompt(draft,
            new HashSet<string>(["E1"], StringComparer.Ordinal));
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        };
        Assert.InRange(payload.ToJsonString(wireOptions).Length, 1, 12_000);
        Assert.Equal(new string('x', 1_999), payload["answerText"]!.GetValue<string>());
        Assert.True(payload["answerTextTruncated"]!.GetValue<bool>());
        var claims = payload["claims"]!.AsArray();
        Assert.NotEmpty(claims);
        Assert.Equal(count - claims.Count, payload["omittedClaimCount"]!.GetValue<int>());
        Assert.All(claims, claim => Assert.Equal(new string('é', 700), claim!["text"]!.GetValue<string>()));
        Assert.Equal("E1", Assert.Single(payload["currentlyVisibleEvidenceIds"]!.AsArray())!.GetValue<string>());
        Assert.Equal(count, draft.Claims.Count);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task PartialDraftContinuity_keeps_prior_choices_after_a_new_research_turn_without_inventing_positions(string protocol)
    {
        var partial = JsonSerializer.Serialize(new
        {
            outcome = "insufficient_documentation",
            answerText = "OptionA convient à la première étape [C7]. Les autres étapes restent à vérifier.",
            claims = new[] { new { claimId = "C7", selectedItem = "OptionA", text = "OptionA documentée.", evidenceIds = new[] { "E1" } } }
        });
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, partial), PostResearchFunction(protocol, "search_corpus", PostResearchSearch),
            EarlyReply(protocol, partial));
        var options = EarlyOptions(protocol, 8);
        var bodies = EarlyBodies();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(result.Claims);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.False(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("priorPartialDraft", out _));
        foreach (var index in new[] { 2, 3 })
        {
            var prompt = WorkspaceUser(factory.Requests[index].Body);
            var draft = prompt.GetProperty("priorPartialDraft");
            Assert.False(draft.GetProperty("documentaryEvidence").GetBoolean());
            Assert.Contains("première étape", draft.GetProperty("answerText").GetString());
            Assert.False(draft.GetProperty("answerTextTruncated").GetBoolean());
            var claim = Assert.Single(draft.GetProperty("claims").EnumerateArray());
            Assert.Equal("C7", claim.GetProperty("claimId").GetString());
            Assert.Equal("OptionA", claim.GetProperty("selectedItem").GetString());
            Assert.False(claim.TryGetProperty("requestedCoordinate", out _));
            Assert.Equal(0, draft.GetProperty("omittedClaimCount").GetInt32());
            Assert.Contains(draft.GetProperty("currentlyVisibleEvidenceIds").EnumerateArray(), e => e.GetString() == "E1");
            Assert.Contains("not grid positions", draft.GetProperty("instruction").GetString());
        }
        Assert.False(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("insufficiencyReview", out _));
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task PartialDraftContinuity_does_not_leak_to_another_job(string protocol)
    {
        const string partial = """{"outcome":"insufficient_documentation","answerText":"OptionA [C1].","claims":[{"claimId":"C1","selectedItem":"OptionA","text":"OptionA documentée.","evidenceIds":["E1"]}]}""";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), EarlyReply(protocol, partial),
            Completion(RepeatablePlan), EarlyReply(protocol, partial));
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, 2), null);
        for (var i = 0; i < 2; i++)
            await provider.ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway(EarlyBodies()), CancellationToken.None);
        Assert.False(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("priorPartialDraft", out _));
    }
}
