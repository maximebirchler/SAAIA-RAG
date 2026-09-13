using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Agent_prompt_style_preserves_canonical_research_and_critic_for_both_protocols(string protocol)
    {
        const string read="""{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""";
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol=="responses"?NativeResponseFunction("read_source",read):NativeCompletion(("read_source",read)),
            protocol=="responses"?NativeResponseAnswer(NativeAnswered):Completion(NativeAnswered),
            protocol=="responses"?NativeResponseAnswer(NativeAnswered):Completion(NativeAnswered));
        var options=WorkspaceOptions(protocol);options.SynthesisPromptStyle="agent";options.SemanticCriticEnabled=true;
        var source=BuildEvidence("E1","Le seuil est 17 bar.");var gateway=new RecordingToolGateway(source);
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None);
        Assert.Equal("answered",result.Outcome);Assert.Equal(4,result.ProviderCallCount);
        Assert.Equal("read_source",gateway.Searches[1].Operation);
        Assert.Equal(source.Reference.RevisionId,Assert.Single(gateway.Evidence).Reference.RevisionId);
        Assert.Equal("E1",Assert.Single(result.Claims).EvidenceIds[0]);
    }

    [Fact]
    public async Task Invalid_synthesis_prompt_style_never_runs_a_model_or_source_operation()
    {
        using var factory=new QueuedHttpClientFactory();var options=WorkspaceOptions();options.SynthesisPromptStyle="unknown";
        var gateway=new RecordingToolGateway();
        var error=await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(()=>new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None));
        Assert.Equal("advanced_synthesis_prompt_style_invalid",error.ErrorCode);Assert.Empty(factory.Requests);Assert.Empty(gateway.Searches);
    }
}
