using SAAIA.Client.Core.Diagnostics;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI.Services;

/// <summary>Routes a shared provider's events to exactly the owning turn, independently of callback context.</summary>
internal sealed class LlmCallDiagnosticSubscription : IDisposable
{
    private readonly ILlmProvider _provider;
    private readonly ClientDiagnosticJournal.TurnScope _turn;
    private int _disposed;

    internal LlmCallDiagnosticSubscription(ILlmProvider provider, ClientDiagnosticJournal.TurnScope turn)
    {
        _provider = provider;
        _turn = turn;
        _provider.CallCompleted += Observe;
    }

    private void Observe(LlmCallMetrics metrics)
    {
        if (Volatile.Read(ref _disposed) != 0 || !string.Equals(metrics.TraceId, _turn.TraceId, StringComparison.Ordinal)) return;
        if (!string.Equals(metrics.TurnId ?? ClientDiagnostics.Journal.CurrentTurnId,
                _turn.TurnId, StringComparison.Ordinal)) return;
        _turn.Emit("model.call", metrics.Success ? "success"
            : metrics.ErrorCode is "cancelled" or "canceled" ? "cancelled" : "failed",
            metrics.ErrorCode, new Dictionary<string, long?>
            {
                ["durationMs"] = metrics.ElapsedMilliseconds,
                ["timeToFirstTokenMs"] = metrics.TimeToFirstTokenMilliseconds,
                ["inputTokens"] = metrics.Usage.InputTokens, ["outputTokens"] = metrics.Usage.OutputTokens,
                ["cachedInputTokens"] = metrics.Usage.CachedInputTokens, ["retryCount"] = metrics.RetryCount
            }, new Dictionary<string, string>
            {
                ["requestId"] = metrics.RequestId, ["provider"] = metrics.Provider,
                ["model"] = metrics.ModelId, ["role"] = metrics.Role.ToString()
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _provider.CallCompleted -= Observe;
    }
}
