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
    public async Task ExactRuntimeTokenCount_RecompressesBeforeTheGenerationRequestOverflows()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 5000, 2000, 2000 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: rag_search {\"query\":\"couple serrage HX-42\"}"),
            Completion("Le couple de serrage est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "E1 contient directement la valeur demandee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumOutputTokens = 900
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { 5000, 2000, 2000 }, llm.InputTokenCountsReturned);
        var recovery = Assert.Single(
            result.TraceEvents,
            static trace => string.Equals(
                trace.EventName,
                "source_backed_agent_v2.context.recovery_requested",
                StringComparison.Ordinal));
        Assert.Equal(
            "preflight_exact_token_budget",
            recovery.Fields["reason"]);
        var completedTurn = Assert.Single(
            result.TraceEvents,
            static trace => string.Equals(
                                trace.EventName,
                                "source_backed_agent_v2.turn.completed",
                                StringComparison.Ordinal)
                            && trace.Fields.GetValueOrDefault("input_tokens_exact") == "2000");
        Assert.NotNull(completedTurn);
    }

    [Fact]
    public async Task ExactRuntimeTokenCount_CompactsSemanticReviewBeforeSendingIt()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 2000, 8000, 3000 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: rag_search {\"query\":\"couple serrage HX-42\"}"),
            Completion("Le couple de serrage est de 85 NÃ‚Â·m [E1]."),
            SemanticReview("accept", "E1 contient directement la valeur demandee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumContextTokens = 8192,
                MaximumSemanticReviewTokens = 640
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { 2000, 8000, 3000 },
            llm.InputTokenCountsReturned);
        Assert.Equal(3, llm.CompleteCallCount);
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
                && trace.Fields["context_recovery_used"] == "true");
    }

    [Fact]
    public async Task ExactRuntimeTokenCount_NeverSendsARequestKnownOutsideContextAfterRecovery()
    {
        var llm = new ExactTokenCountLlm(
            new[] { 5000, 4500 },
            Completion(
                "LIVRABLE: reponse factuelle sourcee\n"
                + "PREUVES_ATOMIQUES: 1 passage\n"
                + "PREMIERE_ACTION: aucune"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options() with
            {
                MaximumContextTokens = 4096,
                MaximumOutputTokens = 900
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                Intake("Quel est le couple de serrage de la pompe HX-42 ?"),
                CancellationToken.None));

        Assert.Contains(
            "outside the measured context budget",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { 5000, 4500 }, llm.InputTokenCountsReturned);
        Assert.Equal(1, llm.CompleteCallCount);
    }
}
