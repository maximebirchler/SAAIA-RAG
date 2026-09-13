using SAAIA.Backend.AdvancedAnalysis;
using System.Text.Json.Nodes;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(256, 0)]
    [InlineData(257, 1)]
    public async Task Active_proposal_retention_is_bounded_and_reports_omitted_references(int count, int omitted)
    {
        var evidence=Enumerable.Range(1,count).Select(i=>BuildEvidence("E"+i,"Le seuil est 17 bar.")).ToArray();
        var proposal=JsonNode.Parse(NativeAnswered)!;
        proposal["claims"]![0]!["evidenceIds"]=new JsonArray(evidence.Select(e=>JsonValue.Create(e.Reference.EvidenceId)).ToArray());
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),Completion(proposal.ToJsonString()),Completion(NativeAnswered));
        var options=WorkspaceOptions();options.NativeResearchActiveProposalEnabled=true;options.SemanticCriticEnabled=true;
        options.MaximumEvidencePromptCharacters=1_000_000;options.ExternalMaximumCallsPerJob=3;
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),new RecordingToolGateway(evidence),CancellationToken.None);
        Assert.Equal("answered",result.Outcome);
        var active=WorkspaceUser(factory.Requests[2].Body).GetProperty("activeProposal");
        Assert.Equal(256,active.GetProperty("retainedEvidenceIds").GetArrayLength());
        Assert.Equal(omitted,active.GetProperty("omittedBeyondRetentionLimit").GetInt32());
    }

    [Fact]
    public async Task Active_proposal_does_not_resurrect_a_reference_already_invisible_when_model_selects_it()
    {
        var original=BuildEvidence("E1","Le seuil est 17 bar.");
        var newcomers=Enumerable.Range(2,5).Select(i=>BuildEvidence("E"+i,"Le seuil est 17 bar. "+new string('x',2300))).ToArray();
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("search_corpus",WorkspaceSearch)),Completion(NativeAnswered),Completion(NativeAnswered.Replace("E1","E2")));
        var options=WorkspaceOptions();options.NativeResearchActiveProposalEnabled=true;options.SemanticCriticEnabled=true;
        options.MaximumEvidencePromptCharacters=8000;options.ExternalMaximumCallsPerJob=4;
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),new SequencedToolGateway([original],newcomers),CancellationToken.None);
        var frame=WorkspaceUser(factory.Requests[3].Body);
        Assert.Empty(frame.GetProperty("activeProposal").GetProperty("retainedEvidenceIds").EnumerateArray());
        Assert.DoesNotContain(frame.GetProperty("evidence").EnumerateArray(),e=>e.GetProperty("evidenceId").GetString()=="E1");
        Assert.Equal("E2",Assert.Single(result.Claims).EvidenceIds[0]);
    }

    [Theory]
    [InlineData("responses", true)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("chat-completions", false)]
    public async Task Active_proposal_keeps_model_chosen_canonical_body_when_critic_research_would_evict_it(
        string protocol, bool enabled)
    {
        var original=BuildEvidence("E1","Le seuil est 17 bar. Canonical original body.");
        var newcomers=Enumerable.Range(2,5).Select(i=>BuildEvidence("E"+i,"Le seuil est 17 bar. "+new string('x',2300))).ToArray();
        var final=enabled?NativeAnswered.Replace("[\"E1\"]","[\"E1\",\"E2\"]"):NativeAnswered.Replace("E1","E2");
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol=="responses"?NativeResponseAnswer(NativeAnswered):Completion(NativeAnswered),
            protocol=="responses"?NativeResponseFunction("search_corpus",WorkspaceSearch):NativeCompletion(("search_corpus",WorkspaceSearch)),
            protocol=="responses"?NativeResponseAnswer(final):Completion(final));
        var options=WorkspaceOptions(protocol);options.NativeResearchActiveProposalEnabled=enabled;
        options.MaximumEvidencePromptCharacters=8000;options.SemanticCriticEnabled=true;options.ExternalMaximumCallsPerJob=4;
        var gateway=new SequencedToolGateway([original],newcomers);
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None);
        Assert.Equal("answered",result.Outcome);Assert.Equal(4,result.ProviderCallCount);Assert.Equal(2,gateway.Searches.Count);
        var frame=WorkspaceUser(factory.Requests[3].Body);var projected=frame.GetProperty("evidence");
        Assert.True(projected.GetRawText().Length<=8000);
        Assert.Equal(enabled?"E1":"E2",projected[0].GetProperty("evidenceId").GetString());
        Assert.Contains(projected.EnumerateArray(),e=>e.GetProperty("evidenceId").GetString()=="E2");
        if(enabled)
        {
            Assert.Equal(original.Content,projected[0].GetProperty("content").GetString());
            Assert.Equal("E1",frame.GetProperty("activeProposal").GetProperty("currentlyVisibleEvidenceIds")[0].GetString());
            Assert.Empty(frame.GetProperty("activeProposal").GetProperty("currentlyAbsentEvidenceIds").EnumerateArray());
        }
        else
        {
            Assert.DoesNotContain(projected.EnumerateArray(),e=>e.GetProperty("evidenceId").GetString()=="E1");
            Assert.False(frame.TryGetProperty("activeProposal",out _));
        }
    }

    [Fact]
    public async Task Active_proposal_references_are_replaced_after_a_model_support_correction()
    {
        const string locator="""{"outcome":"answered","answerText":"Option alpha [C1].","claims":[{"claimId":"C1","selectedItem":"Option alpha","text":"Option alpha est documentée.","evidenceIds":["E1"]}]}""";
        var body=locator.Replace("E1","E2");
        using var factory=new QueuedHttpClientFactory(Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),Completion(locator),Completion(body),Completion(body));
        var options=WorkspaceOptions();options.NativeResearchActiveProposalEnabled=true;options.SemanticCriticEnabled=true;options.ExternalMaximumCallsPerJob=4;
        var gateway=new RecordingToolGateway(BuildEvidence("E1","Options\nOption alpha I page 64",exactTitle:"Options"),
            BuildEvidence("E2","Option alpha\nSet the pressure to 17 bar and hold for three minutes.",exactTitle:"Option alpha"));
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildRequest(atomicEvidenceMode:"named_item"),gateway,CancellationToken.None);
        Assert.Equal("answered",result.Outcome);
        var retained=WorkspaceUser(factory.Requests[3].Body).GetProperty("activeProposal").GetProperty("retainedEvidenceIds");
        Assert.Equal("E2",Assert.Single(retained.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Active_proposal_state_does_not_leak_when_provider_is_reused_for_another_job()
    {
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),Completion(NativeAnswered),Completion(NativeAnswered),
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),Completion(NativeAnswered.Replace("E1","OTHER")),Completion(NativeAnswered.Replace("E1","OTHER")));
        var options=WorkspaceOptions();options.NativeResearchActiveProposalEnabled=true;options.SemanticCriticEnabled=true;options.ExternalMaximumCallsPerJob=3;
        var provider=new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null);
        await provider.ExecuteAsync(BuildDirectRequest(),new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar.")),CancellationToken.None);
        await provider.ExecuteAsync(BuildDirectRequest(),new RecordingToolGateway(BuildEvidence("OTHER","Le seuil est 17 bar.")),CancellationToken.None);
        Assert.Empty(WorkspaceUser(factory.Requests[4].Body).GetProperty("activeProposal").GetProperty("retainedEvidenceIds").EnumerateArray());
        Assert.Equal("OTHER",Assert.Single(WorkspaceUser(factory.Requests[5].Body).GetProperty("activeProposal").GetProperty("retainedEvidenceIds").EnumerateArray()).GetString());
    }
}
