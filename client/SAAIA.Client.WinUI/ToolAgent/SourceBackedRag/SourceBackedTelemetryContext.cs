using System.Threading;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedTelemetryContext
{
    private sealed record State(
        string TraceId,
        State? Parent);

    private static readonly AsyncLocal<State?> CurrentState = new();

    public static string? TraceId => CurrentState.Value?.TraceId;

    public static IDisposable Push(string traceId)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = new State(traceId, previous);
        return new Scope(previous);
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        private State? _previous = previous;

        public void Dispose()
        {
            CurrentState.Value = Interlocked.Exchange(
                ref _previous,
                null);
        }
    }
}
