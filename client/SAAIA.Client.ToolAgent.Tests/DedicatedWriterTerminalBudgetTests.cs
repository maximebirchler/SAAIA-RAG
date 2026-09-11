using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class DedicatedWriterTerminalBudgetTests
{
    [Theory]
    [InlineData(1000, true)]
    [InlineData(5100, false)]
    public async Task Dedicated_writer_uses_its_existing_reserve_without_reopening_research(int inputTokens, bool fitsTotal)
    {
        // Measured charged total from A703 round66. Inputs below are controlled
        // admission boundaries, not claimed native token measurements.
        var budget = new SourceBackedLlmCumulativeBudget(12000, 240000, 4800, 60000, static () => 0);
        var prior = budget.TryReserve(6229, 0, false);
        Assert.True(prior.Admitted);
        budget.Complete(prior.ReservationId, 6229, 0);
        var llm = new RecordingLlm(inputTokens);
        var runner = new SourceBackedAgentV2Runner(llm, new NoTools(), new SourceBackedAgentV2Options(
            MaximumTurns: 4, MaximumToolCalls: 4, MaximumObservationItems: 8,
            MaximumObservationExcerptCharacters: 360, MaximumOutputTokens: 900,
            MaximumActionTokens: 256, MaximumWorkingEvidenceItems: 12, StructuredFlatWriterEnabled: false));
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(new
        {
            hits = new[] { new { docId = "observed-document", docPath = "Lab/Indicator.pdf",
                revisionId = "observed-revision", sourceHash = new string('a', 64),
                chunkId = "observed-chunk", pageStart = 4, pageEnd = 4,
                excerpt = "The status light blinks twice after initialization." } }
        }) });
        var intake = new SourceBackedIntake("What does the status light do?", "rag.answer", [], [], false, "en");
        var bundle = EvidenceBundleBuilder.FromToolResults(results, intake.UserQuestion);
        var id = Assert.Single(bundle.Items).EvidenceId;
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("CompleteDedicatedWriterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            var task = (Task<SourceBackedAgentCompletion>)method.Invoke(runner,
                [Array.Empty<SourceBackedAgentMessage>(), "SELECTION_LLM_AUTORISEE: " + id,
                 intake, bundle, new[] { id }, "content_claim", CancellationToken.None, null, null])!;
            if (fitsTotal)
            {
                var completion = await task;
                Assert.Equal("The status light blinks twice [E1].", completion.Content);
                Assert.Equal(1, llm.Executions);
            }
            else
            {
                await Assert.ThrowsAsync<SourceBackedLlmBudgetExceededException>(() => task);
                Assert.Equal(0, llm.Executions);
            }
            Assert.True(llm.Terminal);
            Assert.Equal("terminal_writer_or_review", llm.CallClass);
            Assert.False(SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall([]));
            Assert.False(SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall("ordinary_evidence_review"));
            Assert.False(budget.TryReserve(1000, 900, false).Admitted);
        }
        Assert.Equal(12000, budget.GetSnapshot().MaximumTokens);
        Assert.Equal(4800, budget.GetSnapshot().TerminalReserveTokens);
        Assert.Equal(0, budget.GetSnapshot().ReservedTokens);
        Assert.True(budget.GetSnapshot().ChargedTokens <= 12000);
    }

    private sealed class NoTools : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName,
            JsonElement arguments, CancellationToken ct) => throw new InvalidOperationException("Writer must not retrieve.");
    }

    private sealed class RecordingLlm(int inputTokens) : ISourceBackedAgentLlmClient
    {
        public bool Terminal { get; private set; }
        public string? CallClass { get; private set; }
        public int Executions { get; private set; }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            Assert.Empty(tools);
            Assert.Equal(900, maxTokens);
            Assert.Contains(messages, message => message.Content?.Contains("blinks twice", StringComparison.Ordinal) == true);
            Terminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools);
            CallClass = SourceBackedLlmCumulativeBudgetContext.ResolveNativeCallClass(tools);
            return SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(CallClass, Terminal, messages, tools, maxTokens,
                _ => Task.FromResult<int?>(inputTokens), _ =>
                {
                    Executions++;
                    return Task.FromResult(new SourceBackedAgentCompletion("The status light blinks twice [E1].", [], "stop", inputTokens, 20));
                }, ct);
        }
    }
}
