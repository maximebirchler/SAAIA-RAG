using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ScopeReviewActionBudgetTests
{
    [Theory]
    [InlineData(480, 0, true)]
    [InlineData(96, 0, false)]
    [InlineData(480, 6200, false)]
    public async Task Scope_review_can_complete_its_contract_within_the_configured_action_budget(
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
        // Candidate identities are synthetic; the completion/cost comes from a
        // native capture. This tests transport/admission, not recipe semantics.
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(new
        {
            hits = Enumerable.Range(1, 3).Select(i => new { docId = "doc-" + i,
                docPath = "Lab/Procedure-" + i + ".pdf", revisionId = "rev-" + i,
                sourceHash = new string('a', 64), chunkId = "chunk-" + i,
                pageStart = 1, pageEnd = 1, excerpt = "La procédure Atlas explique le mode de démarrage " + i + "." }).ToArray()
        }) });
        var bundle = EvidenceBundleBuilder.FromToolResults(results, "Donne un exemple de procédure.");
        Assert.Equal(3, bundle.Items.Count);
        var candidateType = typeof(SourceBackedAgentV2Runner).GetNestedType("FastEvidenceCandidate", BindingFlags.NonPublic)!;
        var candidates = Array.CreateInstance(candidateType, 3);
        for (var i = 0; i < 3; i++) candidates.SetValue(Activator.CreateInstance(candidateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { bundle.Items[i], new[] { bundle.Items[i] } }, null), i);
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("ReviewSingleSelectionScopeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            var task = (Task)method.Invoke(runner, new object[] {
                new SourceBackedIntake("Donne un exemple de procédure.", "rag.answer", [], [], false, "fr"),
                candidates, candidates.GetValue(0)!, CancellationToken.None })!;
            if (precharged > 0)
            {
                await Assert.ThrowsAsync<SourceBackedLlmBudgetExceededException>(() => task);
                Assert.Equal(0, llm.Executions);
            }
            else
            {
                await task;
                var review = task.GetType().GetProperty("Result")!.GetValue(task)!;
                var valid = (bool)review.GetType().GetProperty("ProtocolValid")!.GetValue(review)!;
                Assert.Equal(expectedValid, valid);
                Assert.Equal(1, llm.Executions);
            }
            Assert.False(llm.Terminal);
            Assert.Equal(actionTokens, llm.MaximumOutput);
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
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages, LlmStructuredOutputContract contract,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            MaximumOutput = maxTokens;
            Terminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(contract.Name);
            return SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(contract.Name, Terminal,
                messages, [], maxTokens, _ => Task.FromResult<int?>(971), _ =>
                {
                    Executions++;
                    var complete = maxTokens >= 147;
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                        "Fixtures", complete ? "CompletedScopeReviewCompletionContract.json" : "TruncatedScopeReviewCompletionContract.json")));
                    var choice = doc.RootElement.GetProperty("choices")[0];
                    return Task.FromResult(new SourceBackedAgentCompletion(
                        choice.GetProperty("message").GetProperty("content").GetString()!, [],
                        choice.GetProperty("finish_reason").GetString()!, 971, complete ? 147 : Math.Min(128, maxTokens)));
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
