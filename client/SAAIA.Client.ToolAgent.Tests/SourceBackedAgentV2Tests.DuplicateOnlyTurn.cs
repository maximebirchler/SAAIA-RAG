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
    public async Task DuplicateOnlyTurn_CompactsItsRepairGuidanceWithEarlierSemanticFeedback()
    {
        var firstCall = Call("search-1", "rag_search", new { query = "option initiale" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option documentee."),
            Completion(firstCall),
            Completion(firstCall with { Id = "search-duplicate" }),
            Completion("L'option documentee est disponible [E1]."),
            SemanticReview("accept", "E1 soutient directement l'option."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with { MaximumContextTokens = 4096 });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "duplicate_tool_call",
            RequestText(llm, 3));
        Assert.Contains("ACTIONS DEJA EXECUTEES", RequestText(llm, 3));
    }
}
