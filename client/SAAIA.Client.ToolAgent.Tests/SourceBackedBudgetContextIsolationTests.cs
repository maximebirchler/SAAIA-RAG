using System.Reflection;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedBudgetContextIsolationTests
{
    [Fact]
    public void Reusable_runner_fields_are_explicit_readonly_ports_and_an_immutable_options_snapshot()
    {
        var allowed = new[]
        {
            typeof(ISourceBackedAgentLlmClient),
            typeof(ISourceBackedAgentToolExecutor),
            typeof(SourceBackedAgentV2Options),
            typeof(Action<SourceBackedTraceEvent>),
            typeof(ISourceBackedNamedDocumentResolver)
        };
        var fields = typeof(SourceBackedAgentV2Runner).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.Equal(allowed.Length, fields.Length);
        Assert.All(fields, field =>
        {
            Assert.True(field.IsInitOnly, $"Mutable runner field: {field.Name}");
            Assert.Contains(field.FieldType, allowed);
        });
    }

    [Fact]
    public async Task Overlapping_request_budgets_isolate_exhaustion_usage_and_restore_the_parent_context()
    {
        var previous = SourceBackedLlmCumulativeBudgetContext.Current;
        var parent = Budget(500);
        var exhausted = Budget(15);
        var successful = Budget(100);
        var bothCounting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counting = 0;
        var simulatedIoCalls = 0;
        using (SourceBackedLlmCumulativeBudgetContext.Push(parent))
        {
            async Task RunRequestAsync(SourceBackedLlmCumulativeBudget budget, bool shouldBeRejected)
            {
                using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
                {
                    Task<SourceBackedAgentCompletion> ExecuteAsync() => SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                        "isolated_contract", terminal: false,
                        Array.Empty<SourceBackedAgentMessage>(), Array.Empty<SourceBackedAgentToolDefinition>(),
                        maximumOutputTokens: 10,
                        async token =>
                        {
                            Assert.Same(budget, SourceBackedLlmCumulativeBudgetContext.Current);
                            if (Interlocked.Increment(ref counting) == 2) bothCounting.TrySetResult();
                            await bothCounting.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                            Assert.Same(budget, SourceBackedLlmCumulativeBudgetContext.Current);
                            return 10;
                        },
                        async _ =>
                        {
                            Interlocked.Increment(ref simulatedIoCalls);
                            await Task.Yield();
                            Assert.Same(budget, SourceBackedLlmCumulativeBudgetContext.Current);
                            return new SourceBackedAgentCompletion("completed", Array.Empty<SourceBackedAgentToolCall>(),
                                "stop", PromptTokens: 10, CompletionTokens: 5);
                        }, CancellationToken.None);

                    if (shouldBeRejected)
                    {
                        var error = await Assert.ThrowsAsync<SourceBackedLlmBudgetExceededException>(ExecuteAsync);
                        Assert.Equal("token_budget_exhausted", error.Reason);
                        Assert.Equal(15, error.Snapshot.MaximumTokens);
                    }
                    else
                    {
                        var completion = await ExecuteAsync();
                        Assert.Equal("completed", completion.Content);
                    }
                    Assert.Same(budget, SourceBackedLlmCumulativeBudgetContext.Current);
                }
                Assert.Same(parent, SourceBackedLlmCumulativeBudgetContext.Current);
            }

            await Task.WhenAll(RunRequestAsync(exhausted, true), RunRequestAsync(successful, false));
            Assert.Same(parent, SourceBackedLlmCumulativeBudgetContext.Current);
            Assert.Equal(2, counting);
            Assert.Equal(1, simulatedIoCalls);
            Assert.Equal(0, exhausted.GetSnapshot().AdmittedCalls);
            Assert.Equal(0, exhausted.GetSnapshot().ChargedTokens);
            Assert.Equal(15, successful.GetSnapshot().ChargedTokens);
            Assert.Equal(1, successful.GetSnapshot().CompletedCalls);
            Assert.Equal(0, successful.GetSnapshot().ReservedTokens);
            Assert.Equal(0, parent.GetSnapshot().AdmittedCalls);
        }
        Assert.Same(previous, SourceBackedLlmCumulativeBudgetContext.Current);
    }

    private static SourceBackedLlmCumulativeBudget Budget(int maximumTokens)
        => new(maximumTokens, maximumElapsedMilliseconds: 30_000, terminalReserveTokens: 0,
            terminalReserveMilliseconds: 0, elapsedMilliseconds: static () => 0);
}
