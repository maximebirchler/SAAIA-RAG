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
    public async Task EmergencyCompaction_PreservesUnconsumedPaginationAfterItsOriginalActionAgesOut()
    {
        var llm = new ContextOverflowOnceLlm(
            Completion("Livrable: une option documentee.\nPREUVES_ATOMIQUES: 1 option."),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 5,
                offset = 0
            })),
            Completion(Call("search-1", "rag_search", new { query = "option une" })),
            Completion(Call("search-2", "rag_search", new { query = "option deux" })),
            Completion(Call("search-3", "rag_search", new { query = "option trois" })),
            Completion(Call("search-4", "rag_search", new { query = "option quatre" })),
            new HttpRequestException(
                "request exceeds the available context size",
                null,
                System.Net.HttpStatusCode.BadRequest),
            Completion("L'option Sticks de feta est documentee [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var executor = new ScriptedToolExecutor(
            PagedContentCardInventoryResult(),
            SearchResult(),
            SearchResult(),
            SearchResult(),
            SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumTurns = 9
            });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var recoveredRequest = RequestText(llm, 7);
        Assert.Contains("PAGINATIONS ENCORE DISPONIBLES", recoveredRequest);
        Assert.Contains("documents.content_cards", recoveredRequest);
        Assert.Contains("prochain_offset_exact=5", recoveredRequest);
    }
}
