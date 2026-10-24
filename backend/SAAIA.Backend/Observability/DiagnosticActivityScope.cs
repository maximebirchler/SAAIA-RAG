using System.Diagnostics;

namespace SAAIA.Backend;

/// <summary>Collector callbacks cannot replace the operation being observed.</summary>
internal sealed class DiagnosticActivityScope : IDisposable
{
    private readonly Activity? _previous;
    private int _disposed;
    internal Activity? Activity { get; }

    internal DiagnosticActivityScope(ActivitySource source, string name)
    {
        _previous = System.Diagnostics.Activity.Current;
        try { Activity = source.StartActivity(name, ActivityKind.Internal); }
        catch (Exception)
        {
            BackendDiagnostics.RecordCollectionFailure();
            var failed = System.Diagnostics.Activity.Current;
            if (!ReferenceEquals(failed, _previous) && failed?.OperationName == name)
            {
                try { failed.Stop(); }
                catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
            }
            RestorePrevious();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || Activity is null) return;
        try { Activity.Dispose(); }
        catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
        finally
        {
            if (ReferenceEquals(System.Diagnostics.Activity.Current, Activity)
                || System.Diagnostics.Activity.Current?.IsStopped == true) RestorePrevious();
        }
    }

    private void RestorePrevious()
    {
        var previous = _previous;
        while (previous?.IsStopped == true) previous = previous.Parent;
        if (ReferenceEquals(System.Diagnostics.Activity.Current, previous)) return;
        try { System.Diagnostics.Activity.Current = previous; }
        catch (Exception) { BackendDiagnostics.RecordCollectionFailure(); }
    }
}
