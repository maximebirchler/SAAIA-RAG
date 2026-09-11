using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedLlmCumulativeBudgetContext
{
    private sealed record BudgetState(
        SourceBackedLlmCumulativeBudget Budget,
        BudgetState? Parent);

    private static readonly AsyncLocal<BudgetState?> CurrentState = new();
    private static readonly AsyncLocal<int> TerminalCallDepth = new();

    internal static SourceBackedLlmCumulativeBudget? Current =>
        CurrentState.Value?.Budget;

    internal static IDisposable Push(SourceBackedLlmCumulativeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var previous = CurrentState.Value;
        CurrentState.Value = new BudgetState(budget, previous);
        return new BudgetScope(previous);
    }

    internal static IDisposable PushTerminalCall()
    {
        var previous = TerminalCallDepth.Value;
        TerminalCallDepth.Value = previous + 1;
        return new TerminalScope(previous);
    }

    internal static bool IsTerminalNativeCall(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        if (TerminalCallDepth.Value > 0)
            return true;
        if (tools.Count != 1)
            return false;

        return tools[0].Name is
            "resolve_source_yield"
            or "submit_evidence_selection"
            or "manage_evidence_workspace";
    }

    internal static bool IsTerminalStructuredCall(string contractName)
        => TerminalCallDepth.Value > 0
           || contractName.Contains(
               "writer",
               StringComparison.OrdinalIgnoreCase);

    internal static string ResolveNativeCallClass(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => tools.Count switch
        {
            0 => TerminalCallDepth.Value > 0
                ? "terminal_writer_or_review"
                : "agent_text_decision",
            1 => tools[0].Name,
            _ => "agent_tool_decision"
        };

    internal static async Task<SourceBackedAgentCompletion> ExecuteAsync(
        string callClass,
        bool terminal,
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        int maximumOutputTokens,
        Func<CancellationToken, Task<int?>> countInputTokensAsync,
        Func<CancellationToken, Task<SourceBackedAgentCompletion>> executeAsync,
        CancellationToken ct)
    {
        var budget = Current;
        if (budget is null)
            return await executeAsync(ct).ConfigureAwait(false);

        budget.SetLastCallClass(callClass);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ApplyRemainingDeadline(
            deadlineCts,
            budget.GetSnapshot(),
            terminal);

        int? exactInputTokens;
        try
        {
            exactInputTokens = await countInputTokensAsync(deadlineCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw CreateExceeded(budget, "time_budget_exhausted");
        }

        var measuredInputTokens = exactInputTokens is >= 0
            ? exactInputTokens.Value
            : EstimateInputTokens(messages, tools);
        var admission = budget.TryReserve(
            measuredInputTokens,
            maximumOutputTokens,
            terminal);
        EmitBudgetTrace(budget.GetSnapshot(), admission, callClass);
        if (!admission.Admitted)
            throw CreateExceeded(budget, admission.Reason);

        ApplyRemainingDeadline(
            deadlineCts,
            budget.GetSnapshot(),
            terminal);
        try
        {
            var completion = await executeAsync(deadlineCts.Token)
                .ConfigureAwait(false);
            budget.Complete(
                admission.ReservationId,
                completion.PromptTokens,
                completion.CompletionTokens);
            EmitBudgetTrace(
                budget.GetSnapshot(),
                admission with { Reason = "completed" },
                callClass);
            return completion;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            budget.Fail(admission.ReservationId);
            throw CreateExceeded(budget, "time_budget_exhausted");
        }
        catch
        {
            budget.Fail(admission.ReservationId);
            throw;
        }
    }

    internal static int EstimateInputTokens(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        long characters = messages.Sum(static message =>
            (long)(message.Content?.Length ?? 0));
        characters += tools.Sum(static tool =>
            (long)tool.Name.Length
            + tool.Description.Length
            + tool.Parameters.GetRawText().Length);
        // Conservative fallback for an unavailable exact tokenizer. The local
        // runtime normally supplies exact counts; two characters per token
        // intentionally over-reserves mixed-language and JSON payloads.
        return (int)Math.Min(
            int.MaxValue,
            Math.Max(1, (characters + 1) / 2));
    }

    private static void ApplyRemainingDeadline(
        CancellationTokenSource cts,
        SourceBackedLlmBudgetSnapshot snapshot,
        bool terminal)
    {
        var usableMilliseconds = terminal
            ? snapshot.RemainingMilliseconds
            : snapshot.RemainingMilliseconds
              - snapshot.TerminalReserveMilliseconds;
        var remaining = Math.Clamp(
            usableMilliseconds,
            1,
            int.MaxValue);
        cts.CancelAfter(TimeSpan.FromMilliseconds(remaining));
    }

    private static SourceBackedLlmBudgetExceededException CreateExceeded(
        SourceBackedLlmCumulativeBudget budget,
        string reason)
        => new(reason, budget.GetSnapshot());

    private static void EmitBudgetTrace(
        SourceBackedLlmBudgetSnapshot snapshot,
        SourceBackedLlmBudgetAdmission admission,
        string callClass)
    {
        ClientLog.Info(
            "[SOURCE_BACKED_LLM_BUDGET"
            + $" maximum_tokens={snapshot.MaximumTokens}"
            + $" charged_tokens={snapshot.ChargedTokens}"
            + $" reserved_tokens={snapshot.ReservedTokens}"
            + $" remaining_tokens={snapshot.RemainingTokens}"
            + $" remaining_ms={snapshot.RemainingMilliseconds}"
            + $" admission_reason={admission.Reason}"
            + $" call_class={callClass}"
            + $" terminal={admission.Terminal}]" );
    }

    private sealed class BudgetScope(BudgetState? previous) : IDisposable
    {
        private BudgetState? _previous = previous;

        public void Dispose()
        {
            CurrentState.Value = Interlocked.Exchange(
                ref _previous,
                null);
        }
    }

    private sealed class TerminalScope(int previous) : IDisposable
    {
        private int _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            TerminalCallDepth.Value = _previous;
        }
    }
}
