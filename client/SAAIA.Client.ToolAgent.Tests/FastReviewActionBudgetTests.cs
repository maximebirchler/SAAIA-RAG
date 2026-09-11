using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class FastReviewActionBudgetTests
{
    [Theory]
    [InlineData(480, 0, true)]
    [InlineData(112, 0, false)]
    [InlineData(480, 6200, false)]
    public async Task Complete_fast_review_uses_configured_budget_and_keeps_the_terminal_reserve(
        int actionTokens, int precharged, bool expectedValid)
    {
        var llm = new RecordedReview();
        var runner = new SourceBackedAgentV2Runner(llm, new NoTools(),
            new SourceBackedAgentV2Options(4, 4, 8, 360, 900, MaximumActionTokens: actionTokens));
        var budget = new SourceBackedLlmCumulativeBudget(12000, 240000, 4800, 60000, static () => 0);
        if (precharged > 0)
        {
            var prior = budget.TryReserve(precharged, 0, false);
            Assert.True(prior.Admitted);
            budget.Complete(prior.ReservationId, precharged, 0);
        }
        // Real completion bytes and costs, synthetic source identities. This
        // qualifies transport and admission, not the truth of the captured answer.
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                hits = Enumerable.Range(1, 2).Select(i => new
                {
                    docId = "atlas",
                    docPath = "Lab/Atlas.pdf",
                    revisionId = "rev-atlas",
                    sourceHash = new string('a', 64),
                    chunkId = "chunk-" + i,
                    pageStart = 1,
                    pageEnd = 1,
                    excerpt = "Le module Atlas décrit la procédure " + i + "."
                }).ToArray()
            })
        });
        var intake = new SourceBackedIntake("Donne une procédure documentée.", "rag.answer", [], [], false, "fr");
        var built = EvidenceBundleBuilder.FromToolResults(results, intake.UserQuestion);
        Assert.Equal(2, built.Items.Count);
        var bundle = built with { Items = new[] { built.Items[0], built.Items[1] with { EvidenceId = "E11" } } };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("ReviewInitialEvidenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            var task = (Task)method.Invoke(runner, new object[] { intake,
                "PREUVES_ATOMIQUES: 1 procédure\nMODE_PREUVES_ATOMIQUES: named_item\nPOLITIQUE_SELECTION: single_item",
                bundle, new[] { "E1" }, new HashSet<string>(), 3, CancellationToken.None })!;
            if (precharged > 0)
            {
                await Assert.ThrowsAsync<SourceBackedLlmBudgetExceededException>(() => task);
                Assert.Equal(0, llm.Executions);
            }
            else
            {
                await task;
                var review = task.GetType().GetProperty("Result")!.GetValue(task)!;
                Assert.Equal(expectedValid, review.GetType().GetProperty("ProtocolValid")!.GetValue(review));
                Assert.Equal(1, llm.Executions);
                if (expectedValid)
                {
                    Assert.Equal("write", review.GetType().GetProperty("NextCapability")!.GetValue(review));
                    Assert.Equal(string.Empty, review.GetType().GetProperty("Answer")!.GetValue(review));
                }
            }
            Assert.Equal(actionTokens, llm.MaximumOutput);
            Assert.False(llm.Terminal);
        }
        Assert.Equal(12000, budget.GetSnapshot().MaximumTokens);
        Assert.Equal(4800, budget.GetSnapshot().TerminalReserveTokens);
        Assert.Equal(0, budget.GetSnapshot().ReservedTokens);
    }

    private sealed class RecordedReview : ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public int MaximumOutput { get; private set; }
        public int Executions { get; private set; }
        public bool Terminal { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            MaximumOutput = maxTokens;
            Terminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(contract.Name);
            return SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(contract.Name, Terminal,
                messages, [], maxTokens, _ => Task.FromResult<int?>(1208), _ =>
                {
                    Executions++;
                    var complete = maxTokens >= 126;
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                        "Fixtures", complete ? "CompletedFastReviewCompletionContract.json" : "TruncatedFastReviewCompletionContract.json")));
                    var choice = doc.RootElement.GetProperty("choices")[0];
                    return Task.FromResult(new SourceBackedAgentCompletion(
                        choice.GetProperty("message").GetProperty("content").GetString()!, [],
                        choice.GetProperty("finish_reason").GetString()!, 1208, complete ? 126 : 112));
                }, ct);
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false) => throw new NotSupportedException();
    }
    private sealed class NoTools : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName,
            JsonElement arguments, CancellationToken ct) => throw new NotSupportedException();
    }
}
