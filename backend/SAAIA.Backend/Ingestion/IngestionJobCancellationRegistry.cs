using System.Collections.Concurrent;

sealed class IngestionJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, ActiveJob> _jobs = new();

    public IDisposable Register(Guid jobId, Guid tenantId, string docPath, CancellationTokenSource cts)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        var entry = new ActiveJob(jobId, tenantId, docPath, cts);
        _jobs[jobId] = entry;
        return new Registration(this, entry);
    }

    public bool TryCancel(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var entry))
        {
            try { entry.Cts.Cancel(); } catch { }
            return true;
        }

        return false;
    }

    public int CancelByDocPath(Guid tenantId, string docPath)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        var count = 0;
        foreach (var entry in _jobs.Values)
        {
            if (entry.TenantId == tenantId && string.Equals(entry.DocPath, docPath, StringComparison.OrdinalIgnoreCase))
            {
                try { entry.Cts.Cancel(); } catch { }
                count++;
            }
        }

        return count;
    }

    private void Unregister(ActiveJob entry)
    {
        if (_jobs.TryGetValue(entry.JobId, out var current) && ReferenceEquals(current.Cts, entry.Cts))
            _jobs.TryRemove(entry.JobId, out _);
    }

    private sealed record ActiveJob(Guid JobId, Guid TenantId, string DocPath, CancellationTokenSource Cts);

    private sealed class Registration : IDisposable
    {
        private readonly IngestionJobCancellationRegistry _owner;
        private readonly ActiveJob _entry;
        private int _disposed;

        public Registration(IngestionJobCancellationRegistry owner, ActiveJob entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _owner.Unregister(_entry);
            _entry.Cts.Dispose();
        }
    }
}
