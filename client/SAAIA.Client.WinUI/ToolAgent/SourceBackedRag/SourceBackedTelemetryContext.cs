using System.Threading;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedTelemetryContext
{
    private sealed record State(
        string TraceId,
        CollectorHealth Health,
        State? Parent);

    internal sealed class CollectorHealth
    {
        private long _traceSinkFailures;
        internal long TraceSinkFailures => Interlocked.Read(ref _traceSinkFailures);
        internal long RecordTraceSinkFailure() => Interlocked.Increment(ref _traceSinkFailures);
    }

    private static readonly AsyncLocal<State?> CurrentState = new();

    public static string? TraceId => ClientDiagnostics.Journal.CurrentTraceId ?? CurrentState.Value?.TraceId;

    internal static long RecordTraceSinkFailure() => CurrentState.Value?.Health.RecordTraceSinkFailure() ?? 0;

    public static IDisposable Push(string traceId, CollectorHealth? collectorHealth = null)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = new State(traceId, collectorHealth ?? new(), previous);
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
