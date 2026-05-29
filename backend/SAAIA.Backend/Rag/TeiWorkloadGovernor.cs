sealed class TeiWorkloadGovernor
{
    private readonly SemaphoreSlim _teiSlot = new(1, 1);
    private readonly object _stateLock = new();
    private int _activeInteractiveRequests;
    private long _lastInteractiveTicks;

    public IDisposable BeginInteractiveRequest()
    {
        lock (_stateLock)
        {
            _activeInteractiveRequests++;
            _lastInteractiveTicks = DateTimeOffset.UtcNow.UtcTicks;
        }

        return new Lease(() =>
        {
            lock (_stateLock)
            {
                if (_activeInteractiveRequests > 0)
                    _activeInteractiveRequests--;
                _lastInteractiveTicks = DateTimeOffset.UtcNow.UtcTicks;
            }
        });
    }

    public async Task<IDisposable> AcquireInteractiveAsync(CancellationToken ct)
    {
        MarkInteractiveActivity();
        await _teiSlot.WaitAsync(ct);
        return new Lease(() => _teiSlot.Release());
    }

    public async Task<IDisposable> AcquireReadyProbeAsync(CancellationToken ct)
    {
        await _teiSlot.WaitAsync(ct);
        return new Lease(() => _teiSlot.Release());
    }

    public async Task<IDisposable> AcquireIngestionAsync(int interactiveQuietPeriodMs, CancellationToken ct)
    {
        var quietPeriod = TimeSpan.FromMilliseconds(Math.Clamp(interactiveQuietPeriodMs, 0, 30000));
        while (true)
        {
            await WaitForInteractiveQuietPeriodAsync(quietPeriod, ct);
            await _teiSlot.WaitAsync(ct);
            if (!HasActiveOrRecentInteractiveRequest(quietPeriod))
                return new Lease(() => _teiSlot.Release());

            _teiSlot.Release();
            await Task.Delay(ComputeQuietRetryDelay(quietPeriod), ct);
        }
    }

    public TeiWorkloadGovernorSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            var last = _lastInteractiveTicks <= 0
                ? (DateTimeOffset?)null
                : new DateTimeOffset(_lastInteractiveTicks, TimeSpan.Zero);
            return new TeiWorkloadGovernorSnapshot(
                ActiveInteractiveRequests: _activeInteractiveRequests,
                LastInteractiveActivityUtc: last,
                AvailableSlots: _teiSlot.CurrentCount);
        }
    }

    private void MarkInteractiveActivity()
    {
        lock (_stateLock)
        {
            _lastInteractiveTicks = DateTimeOffset.UtcNow.UtcTicks;
        }
    }

    private async Task WaitForInteractiveQuietPeriodAsync(TimeSpan quietPeriod, CancellationToken ct)
    {
        while (HasActiveOrRecentInteractiveRequest(quietPeriod))
            await Task.Delay(ComputeQuietRetryDelay(quietPeriod), ct);
    }

    private bool HasActiveOrRecentInteractiveRequest(TimeSpan quietPeriod)
    {
        lock (_stateLock)
        {
            if (_activeInteractiveRequests > 0)
                return true;
            if (quietPeriod <= TimeSpan.Zero || _lastInteractiveTicks <= 0)
                return false;

            var elapsedTicks = DateTimeOffset.UtcNow.UtcTicks - _lastInteractiveTicks;
            return elapsedTicks >= 0 && elapsedTicks < quietPeriod.Ticks;
        }
    }

    private static TimeSpan ComputeQuietRetryDelay(TimeSpan quietPeriod)
    {
        if (quietPeriod <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(50);

        var delayMs = Math.Clamp((int)Math.Ceiling(quietPeriod.TotalMilliseconds / 4), 50, 250);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private sealed class Lease : IDisposable
    {
        private Action? _release;

        public Lease(Action release) => _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

sealed record TeiWorkloadGovernorSnapshot(
    int ActiveInteractiveRequests,
    DateTimeOffset? LastInteractiveActivityUtc,
    int AvailableSlots);
