using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task UnifiedSearch_RoutesAnLlmQueryArrayToTheMultiSearchBackend()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: deux options sourcees. Rechercher les deux facettes ensemble."),
            Completion(Call("search-many", "rag_search", new
            {
                queries = new[] { "couple serrage HX-42", "boulons couvercle HX-42" },
                topK = 8
            })),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La reponse est complete et soutenue."));
        var executor = new ScriptedToolExecutor(SearchResult("rag.multi_search"));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour les boulons du couvercle HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.multi_search" }, executor.ToolNames);
        var retrievalPlan = Assert.IsType<RetrievalPlan>(result.RetrievalPlan);
        Assert.Equal("rag.multi_search", Assert.Single(retrievalPlan.Requests).ToolName);
    }
}
