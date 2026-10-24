using SAAIA.Backend;

/// <summary>Every ingestion exit retains completed counts and the active failing/cancelled phase.</summary>
internal sealed class IngestionAttemptDiagnostics : IDisposable
{
    private readonly ILogger _logger;
    private readonly BackendDiagnostics.Operation _attempt;
    private BackendDiagnostics.Operation? _phase;
    private string _phaseName = "preparing";
    private int _completedPhases;
    private bool _terminal;

    internal IngestionAttemptDiagnostics(IngestionJob job, string workerId, ILogger logger)
    {
        _logger = logger;
        _attempt = BackendDiagnostics.Start("ingestion.upsert", logger);
        _attempt.Set("job_id", job.JobId);
        _attempt.Set("doc_id", job.DocId);
        _attempt.Set("document_version", job.Version);
        _attempt.Set("worker_id", workerId);
        Begin("preparing");
    }

    internal void Begin(string phase)
    {
        if (_phase is not null)
        {
            _phase.Complete();
            _phase.Dispose();
            _completedPhases++;
        }
        _phaseName = phase;
        _phase = BackendDiagnostics.Start("ingestion." + phase, _logger);
    }

    internal void Set(string name, object? value) => _attempt.Set(name, value);

    internal void Complete(string outcome = "success", string? reason = null)
    {
        _terminal = true;
        _phase?.Complete(outcome, reason);
        _attempt.Complete(outcome, reason);
    }

    internal void Fail(Exception exception)
    {
        _terminal = true;
        if (exception is JobCanceledException)
        {
            _phase?.Complete("cancelled", "job_cancelled");
            _attempt.Complete("cancelled", "job_cancelled");
        }
        else
        {
            _phase?.Fail(exception);
            _attempt.Fail(exception);
        }
    }

    public void Dispose()
    {
        _attempt.Set("terminal_phase", _phaseName);
        _attempt.Set("completed_phases", _completedPhases);
        if (!_terminal) _attempt.Complete("superseded", "not_published");
        _phase?.Dispose();
        _attempt.Dispose();
    }
}
