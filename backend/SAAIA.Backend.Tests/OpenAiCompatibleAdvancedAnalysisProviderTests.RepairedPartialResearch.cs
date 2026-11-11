using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task Repaired_partial_preserves_critic_budget_and_never_opens_a_second_protocol_repair(
        string protocol, bool invalidAfterReview)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, "not json"), EarlyReply(protocol, EarlyInsufficient),
            EarlyReply(protocol, invalidAfterReview ? "not json again" : EarlyInsufficient));
        var options = EarlyOptions(protocol, invalidAfterReview ? 7 : 4);
        options.SemanticCriticEnabled = true;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var bodies = EarlyBodies();
        var task = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]), CancellationToken.None);
        if (invalidAfterReview)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => task);
            Assert.Equal("advanced_writer_protocol_invalid", error.ErrorCode);
            Assert.True(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("insufficiencyReview", out _));
        }
        else
        {
            var result = await task;
            Assert.Equal("insufficient_documentation", result.Outcome);
            Assert.Equal(4, result.ProviderCallCount);
            Assert.True(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("candidate", out _));
        }
        Assert.Equal(4, factory.Requests.Count);
        Assert.Single(logger.Events, e => Equals(e.Values.GetValueOrDefault("Operation"), "llm.writer_repair"));
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Repaired_partial_rejoins_bounded_research_and_keeps_supported_findings(string protocol)
    {
        const string invalid = """{"outcome":"answered","answerText":"OptionA [C1]. À établir [C2].","claims":[{"claimId":"C1","selectedItem":"OptionA","text":"OptionA documentée.","evidenceIds":["E1"]},{"claimId":"C2","selectedItem":"","text":"Choix manquant.","evidenceIds":[]}]}""";
        const string partial = """{"outcome":"insufficient_documentation","answerText":"OptionA est documentée [C7]. Les autres choix restent à vérifier.","claims":[{"claimId":"C7","selectedItem":"OptionA","text":"OptionA documentée.","evidenceIds":["E1"]}]}""";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, invalid), EarlyReply(protocol, partial),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch),
            EarlyReply(protocol, partial), EarlyReply(protocol, partial));
        var options = EarlyOptions(protocol, 6);
        options.SemanticCriticEnabled = true;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var bodies = EarlyBodies();
        var gateway = new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal("C7", Assert.Single(result.Claims).ClaimId);
        Assert.Equal(6, result.ProviderCallCount);
        Assert.Equal(6, factory.Requests.Count);
        Assert.Equal(2, gateway.Searches.Count);
        var repair = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal("advanced_writer_evidence_id_invalid", repair.GetProperty("validationErrorCode").GetString());
        Assert.True(repair.GetProperty("allowsPartialAnswer").GetBoolean());
        var system = SelectionPromptWords(PayloadAuditSystem(factory.Requests[2].Body));
        Assert.DoesNotContain("Restore the required claim count", system);
        Assert.Contains("A protocol error does not establish that missing choices exist", system);
        var review = WorkspaceUser(factory.Requests[3].Body);
        Assert.True(review.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.True(review.TryGetProperty("insufficiencyReview", out _));
        var draft = review.GetProperty("priorPartialDraft");
        Assert.Equal("C7", Assert.Single(draft.GetProperty("claims").EnumerateArray()).GetProperty("claimId").GetString());
        Assert.False(draft.GetProperty("documentaryEvidence").GetBoolean());
        Assert.True(WorkspaceUser(factory.Requests[5].Body).TryGetProperty("candidate", out _));
        var probe = Assert.Single(logger.Events, e => Equals(e.Values.GetValueOrDefault("Operation"), "llm.writer_repair"));
        using var measurements = JsonDocument.Parse(probe.Values["Measurements"]!.ToString()!);
        Assert.True(measurements.RootElement.GetProperty("critic_call_reserved").GetBoolean());
        Assert.True(measurements.RootElement.GetProperty("repaired_partial").GetBoolean());
        // Scripted replies establish routing, budgets and the transmitted policy,
        // not the semantic quality or autonomy of a real model.
    }
}
