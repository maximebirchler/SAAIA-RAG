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
    public async Task DuplicateToolCalls_DoNotConsumeTheExternalExecutionBudget()
    {
        var firstCall = Call("search-1", "rag_search", new { query = "option initiale" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option complete et sourcee."),
            Completion(firstCall),
            new SourceBackedAgentCompletion(
                string.Empty,
                new[]
                {
                    firstCall with { Id = "search-duplicate" },
                    Call("search-2", "rag_search", new { query = "option complete" })
                },
                "tool_calls",
                220,
                60),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with { MaximumToolCalls = 2 });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.Fields.GetValueOrDefault("error") == "tool_call_budget_exhausted");
    }
}
