namespace SAAIA.Backend.AdvancedAnalysis;

/// <summary>Process-local liveness and diagnostic collector health; no prompts, paths or keys.</summary>
internal sealed class AdvancedAnalysisRuntimeState
{
    private readonly object _gate = new();
    private DateTimeOffset? _workerHeartbeat;
    private string _workerState = "not_started";
    private long _capturesSucceeded;
    private long _capturesFailed;
    private string? _captureFailure;
    private DateTimeOffset? _lastCaptureFailure;

    internal void WorkerHeartbeat(string state)
    {
        lock (_gate) { _workerHeartbeat = DateTimeOffset.UtcNow; _workerState = state; }
    }

    internal void CaptureSucceeded()
    {
        lock (_gate) _capturesSucceeded++;
    }

    internal void CaptureFailed(string reason)
    {
        lock (_gate)
        {
            _capturesFailed++;
            _captureFailure = reason;
            _lastCaptureFailure = DateTimeOffset.UtcNow;
        }
    }

    internal Snapshot GetSnapshot(TimeSpan maximumHeartbeatAge)
    {
        lock (_gate)
        {
            var age = _workerHeartbeat.HasValue ? DateTimeOffset.UtcNow - _workerHeartbeat.Value : (TimeSpan?)null;
            return new(_workerState, _workerHeartbeat, age?.TotalMilliseconds,
                (_workerState is "idle" or "executing") && age <= maximumHeartbeatAge,
                _capturesSucceeded, _capturesFailed, _captureFailure, _lastCaptureFailure);
        }
    }

    internal sealed record Snapshot(string WorkerState, DateTimeOffset? WorkerHeartbeatUtc,
        double? HeartbeatAgeMilliseconds, bool WorkerLive, long CapturesSucceeded, long CapturesFailed,
        string? LastCaptureFailureCode, DateTimeOffset? LastCaptureFailureUtc);
}
