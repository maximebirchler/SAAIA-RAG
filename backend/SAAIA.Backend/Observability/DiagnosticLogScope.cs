namespace SAAIA.Backend;

/// <summary>Log sinks and scopes must not change the outcome of the operation they observe.</summary>
internal static class DiagnosticLogScope
{
    internal static IDisposable Begin<TState>(ILogger logger, TState state) where TState : notnull
    {
        try { return new SafeScope(logger.BeginScope(state)); }
        catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); return new SafeScope(null); }
    }

    internal static void Write(Action write)
    {
        try { write(); }
        catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
    }

    private sealed class SafeScope(IDisposable? inner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { inner?.Dispose(); }
            catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
        }
    }
}
