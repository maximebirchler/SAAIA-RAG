using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

sealed class RagSearchBulkhead
{
    private readonly ILogger<RagSearchBulkhead> _log;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly int _maxConcurrency;
    private readonly int _queueLimit;
    private readonly TimeSpan _queueWaitTimeout;
    private int _active;
    private int _queued;

    public RagSearchBulkhead(IOptions<RagOptions> opt, ILogger<RagSearchBulkhead> log)
    {
        _log = log;

        var rag = opt.Value;
        _maxConcurrency = Math.Clamp(rag.SearchMaxConcurrency, 1, 64);
        _queueLimit = Math.Clamp(rag.SearchQueueLimit, 0, 2048);
        _queueWaitTimeout = TimeSpan.FromSeconds(Math.Clamp(rag.SearchQueueWaitTimeoutSeconds, 1, 600));
        _slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        _log.LogInformation(
            "RAG search bulkhead: MaxConcurrency={MaxConcurrency} QueueLimit={QueueLimit} QueueWaitTimeout={Timeout}s",
            _maxConcurrency,
            _queueLimit,
            (int)_queueWaitTimeout.TotalSeconds);
    }

    public RagSearchBulkheadSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new RagSearchBulkheadSnapshot(
                Active: _active,
                Queued: _queued,
                MaxConcurrency: _maxConcurrency,
                QueueLimit: _queueLimit,
                AvailableSlots: Math.Max(0, _maxConcurrency - _active),
                QueueWaitTimeoutSeconds: (int)_queueWaitTimeout.TotalSeconds);
        }
    }

    public async Task<RagSearchBulkheadLease?> AcquireAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var immediate = TryAcquireImmediate(sw.ElapsedMilliseconds);
        if (immediate is not null)
            return immediate;

        if (!TryRegisterQueued())
            return null;

        var stillQueued = true;
        try
        {
            var admitted = await _slots.WaitAsync(_queueWaitTimeout, ct).ConfigureAwait(false);
            if (!admitted)
                return null;

            var lease = ActivateQueued(sw.ElapsedMilliseconds);
            stillQueued = false;
            return lease;
        }
        finally
        {
            if (stillQueued)
                ReleaseQueued();
        }
    }

    private RagSearchBulkheadLease? TryAcquireImmediate(long waitMs)
    {
        lock (_gate)
        {
            if (_queued > 0 || !_slots.Wait(0))
                return null;

            _active++;
        }

        LogWaitIfNeeded(waitMs);
        return new RagSearchBulkheadLease(this, waitedQueued: false, waitMs);
    }

    private RagSearchBulkheadLease ActivateQueued(long waitMs)
    {
        lock (_gate)
        {
            _queued = Math.Max(0, _queued - 1);
            _active++;
        }

        LogWaitIfNeeded(waitMs);
        return new RagSearchBulkheadLease(this, waitedQueued: true, waitMs);
    }

    private void LogWaitIfNeeded(long waitMs)
    {
        if (waitMs > 250)
        {
            _log.LogWarning(
                "RAG search waited {WaitMs}ms for a slot (max={MaxConcurrency}, queued={Queued})",
                waitMs,
                _maxConcurrency,
                GetSnapshot().Queued);
        }
    }

    private bool TryRegisterQueued()
    {
        lock (_gate)
        {
            if (_queued >= _queueLimit)
                return false;

            _queued++;
            return true;
        }
    }

    private void ReleaseQueued()
    {
        lock (_gate)
        {
            _queued = Math.Max(0, _queued - 1);
        }
    }

    private void ReleaseActive()
    {
        lock (_gate)
        {
            _active = Math.Max(0, _active - 1);
        }

        _slots.Release();
    }

    public sealed class RagSearchBulkheadLease(
        RagSearchBulkhead owner,
        bool waitedQueued,
        long waitMs) : IDisposable
    {
        private int _disposed;

        public bool WaitedQueued { get; } = waitedQueued;
        public long WaitMs { get; } = waitMs;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseActive();
        }
    }
}

sealed record RagSearchBulkheadSnapshot(
    int Active,
    int Queued,
    int MaxConcurrency,
    int QueueLimit,
    int AvailableSlots,
    int QueueWaitTimeoutSeconds);
