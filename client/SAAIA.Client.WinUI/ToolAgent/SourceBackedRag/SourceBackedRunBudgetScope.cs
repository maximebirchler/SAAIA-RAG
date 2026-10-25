using System.Diagnostics;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>A standalone run owns its budget; nested runners reuse the caller's cumulative accounting.</summary>
internal sealed class SourceBackedRunBudgetScope : IDisposable
{
    private readonly IDisposable? _owned;
    private SourceBackedRunBudgetScope(IDisposable? owned) => _owned = owned;
    internal static SourceBackedRunBudgetScope Enter(SourceBackedAgentV2Options options)
    {
        if (SourceBackedLlmCumulativeBudgetContext.Current is not null) return new(null);
        var elapsed = Stopwatch.StartNew();
        return new(SourceBackedLlmCumulativeBudgetContext.Push(new SourceBackedLlmCumulativeBudget(
            options.MaximumCumulativeLlmTokens, options.MaximumCumulativeLlmElapsedMilliseconds,
            options.CumulativeLlmTerminalReserveTokens, options.CumulativeLlmTerminalReserveMilliseconds,
            () => elapsed.ElapsedMilliseconds)));
    }
    public void Dispose() => _owned?.Dispose();
}
