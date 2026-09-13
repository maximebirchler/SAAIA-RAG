using System.Net;
using System.Net.Http.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Explicit_responses_cache_writes_use_the_configured_write_rate_and_actual_counter()
    {
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.OK) { Content=JsonContent.Create(new {
                status="completed",output=new[]{new{type="message",content=new[]{new{type="output_text",text=NativeAnswered}}}},
                usage=new{input_tokens=100,output_tokens=50,input_tokens_details=new{cached_tokens=10,cache_write_tokens=45}}}) });
        var options=WorkspaceOptions("responses");options.Provider="openai-dev";options.LlmLocation="external-service";
        options.LlmBaseUrl="https://api.example/v1";options.ExternalMaximumCostPerJobUsd=2m;
        options.ExternalInputUsdPerMillionTokens=2m;options.ExternalCachedInputUsdPerMillionTokens=.2m;
        options.ExternalCacheWriteInputUsdPerMillionTokens=4m;options.ExternalOutputUsdPerMillionTokens=12m;
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,"synthetic-test-key")
            .ExecuteAsync(BuildDirectRequest(),new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar.")),CancellationToken.None);
        Assert.Equal(45,result.CacheWriteTokens);Assert.Equal(20,result.CachedInputTokens);
        Assert.Equal(.001654m,result.EstimatedCostUsd);
    }
}
