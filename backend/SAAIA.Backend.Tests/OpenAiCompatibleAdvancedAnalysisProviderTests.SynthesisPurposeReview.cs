using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task SynthesisPurposeReview_requested_stable_choices_remain_valid_without_an_automatic_research_pass(
        string style, string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, RepeatableTwentyAnswer()));
        var options = EarlyOptions(protocol, 5);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;
        var request = SynthesisPurposeRequest(requestedRepeat: true);
        var gateway = new RecordingToolGateway(EarlyBodies());

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Single(gateway.Searches);
        foreach (var call in factory.Requests.Skip(1))
        {
            Assert.Equal(request.Handoff.RequestText, WorkspaceUser(call.Body).GetProperty("request").GetString());
            AssertSynthesisPurposePolicy(PayloadAuditSystem(call.Body));
            var contract = WorkspaceUser(call.Body).GetProperty("selectionContract");
            Assert.False(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
            Assert.True(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
        }
        AssertSynthesisPurposeCriticPolicy(PayloadAuditSystem(factory.Requests[2].Body));
    }

    [Theory]
    [InlineData("contract", "chat-completions")]
    [InlineData("contract", "responses")]
    [InlineData("agent", "chat-completions")]
    [InlineData("agent", "responses")]
    public async Task SynthesisPurposeReview_critic_can_read_and_substitute_a_supported_alternative_without_a_factual_defect(
        string style, string protocol)
    {
        var bodies = EarlyBodies();
        const string alternativeContent = "OptionE\nComposition et préparation documentées. Utilisation uniquement avec le prérequis P.";
        var alternative = BuildEvidence("E5", alternativeContent, docId: bodies[0].Reference.DocId,
            revisionId: bodies[0].Reference.RevisionId, pageStart: 8, exactTitle: "OptionE");
        var locator = BuildEvidence("LOCATOR", "Table of contents\nOptionE 8", docId: bodies[0].Reference.DocId,
            revisionId: bodies[0].Reference.RevisionId, exactTitle: "Table of contents") with
        { SourceOverview = new(1, 40, 100) };
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? bodies.Concat([locator]).ToArray() : [alternative], [], 1, number));
        const string find = """{"sourceKey":"internal-source-1","query":"OptionE","offset":0,"topK":3}""";
        var revised = JsonNode.Parse(RepeatableTwentyAnswer())!.AsObject();
        revised["answerText"] = revised["answerText"]!.GetValue<string>().Replace("OptionA [C1]", "OptionE avec P [C1]", StringComparison.Ordinal);
        revised["claims"]![0]!["selectedItem"] = "OptionE";
        revised["claims"]![0]!["text"] = "OptionE nécessite le prérequis P.";
        revised["claims"]![0]!["evidenceIds"] = new JsonArray("E5");
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), PostResearchFunction(protocol, "find_source_text", find),
            EarlyReply(protocol, revised.ToJsonString()));
        var options = EarlyOptions(protocol, 5);
        options.SynthesisPromptStyle = style;
        options.SemanticCriticEnabled = true;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(SynthesisPurposeRequest(requestedRepeat: false), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(5, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal("find_source_text", gateway.Searches[1].Operation);
        Assert.Equal(bodies[0].Reference.RevisionId, gateway.Searches[1].RevisionId);
        var critic = WorkspaceUser(factory.Requests[2].Body);
        Assert.True(critic.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.False(critic.GetProperty("selectionContract").GetProperty("distinctNamedItemsRequired").GetBoolean());
        AssertSynthesisPurposeCriticPolicy(PayloadAuditSystem(factory.Requests[2].Body));
        Assert.Equal("OptionA", critic.GetProperty("candidate").GetProperty("claims")[0].GetProperty("selectedItem").GetString());
        var current = WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").EnumerateArray()
            .Single(item => item.GetProperty("evidenceId").GetString() == "E5");
        Assert.Equal(alternativeContent, current.GetProperty("content").GetString());
        var changed = result.Claims.Single(claim => claim.ClaimId == "C1");
        Assert.Equal("OptionE", changed.SelectedItem);
        Assert.Equal("E5", Assert.Single(changed.EvidenceIds));
        Assert.Contains("prérequis P", changed.Text);
        using var original = JsonDocument.Parse(RepeatableTwentyAnswer());
        var unaffected = original.RootElement.GetProperty("claims").EnumerateArray()
            .Where(claim => claim.GetProperty("claimId").GetString() != "C1")
            .ToDictionary(claim => claim.GetProperty("claimId").GetString()!, claim => claim);
        Assert.Equal(19, unaffected.Count);
        foreach (var claim in result.Claims.Where(claim => claim.ClaimId != "C1"))
        {
            var expected = unaffected[claim.ClaimId];
            Assert.Equal(expected.GetProperty("selectedItem").GetString(), claim.SelectedItem);
            Assert.Equal(expected.GetProperty("text").GetString(), claim.Text);
            Assert.Equal(expected.GetProperty("evidenceIds").EnumerateArray().Select(value => value.GetString()!), claim.EvidenceIds);
        }
        // Scripted replies prove policy transport, permitted source-bound research,
        // substitution and unchanged limits. They do not prove autonomous usefulness.
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task SynthesisPurposeReview_a_bounded_supported_answer_is_not_forced_into_a_quota_or_an_extra_loop(string protocol)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query, bodies, [], 1, number), maximumCalls: 1);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, RepeatableTwentyAnswer()));
        var options = EarlyOptions(protocol, 3);
        options.SemanticCriticEnabled = true;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(SynthesisPurposeRequest(requestedRepeat: false), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Single(gateway.Searches);
        Assert.False(WorkspaceUser(factory.Requests[2].Body).GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
    }

    private static void AssertSynthesisPurposePolicy(string prompt)
    {
        var words = SelectionPromptWords(prompt);
        Assert.Contains("Assess the whole deliverable's usefulness for the stated purpose", words);
        Assert.Contains("inspect promising observed alternatives whose substantive content is still missing", words);
        Assert.Contains("using offered research tools within the available budget before finalizing", words);
        Assert.Contains("A supported selection may be replaced for this substantive improvement even when it has no documentary defect", words);
        Assert.Contains("Respect an explicitly requested repetition or stable choice", words);
        Assert.Contains("Useful variety is a selection objective, not an extra mandatory distinct-item quota", words);
        Assert.Contains("Do not turn conditional support into an unconditional assertion", words);
        Assert.Contains("a cosmetic rewrite is not an improvement", words);
    }

    private static void AssertSynthesisPurposeCriticPolicy(string prompt)
    {
        var words = SelectionPromptWords(prompt);
        AssertSynthesisPurposePolicy(prompt);
        Assert.DoesNotContain("unless its identity, suitability, factual content or placement has a specific documentary defect", words);
        Assert.DoesNotContain("If it is fully supported, reproduce it", words);
        Assert.Contains("without a documentary defect", words);
        Assert.Contains("a locator alone cannot justify a replacement", words, StringComparison.OrdinalIgnoreCase);
    }

    private static AdvancedAnalysisProviderRequest SynthesisPurposeRequest(bool requestedRepeat)
    {
        var request = BuildRequest(20, "named_item", "structured_layout", "documented_option");
        var original = request.Handoff;
        return request with
        {
            Handoff = new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = original.HandoffId, CreatedAtUtc = original.CreatedAtUtc, Language = original.Language,
                RequestText = requestedRepeat
                    ? "Construis un planning de repas 5 x 4 sourcé. Garde les mêmes quatre choix chaque jour pour simplifier les achats."
                    : "Construis un planning de repas 5 x 4 sourcé utile pour la semaine.",
                OriginIntent = original.OriginIntent, ReasonCode = original.ReasonCode, TransferStage = original.TransferStage,
                Load = original.Load, ResearchState = original.ResearchState
            }
        };
    }
}
