using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

sealed class IngestionBulkheads
{
    private readonly ILogger<IngestionBulkheads> _log;

    private readonly SemaphoreSlim _tei;
    private readonly SemaphoreSlim _qdrant;
    private readonly SemaphoreSlim _ocr;
    private readonly SemaphoreSlim _heavyCompute;

    private readonly int _teiMax;
    private readonly int _qdrantMax;
    private readonly int _ocrMax;
    private readonly int _heavyComputeMax;
    private readonly TimeSpan _acquireTimeout;
    private readonly TimeSpan _ocrAcquireTimeout;
    private readonly TimeSpan _heavyComputeAcquireTimeout;

    public IngestionBulkheads(IOptions<IngestionOptions> opt, ILogger<IngestionBulkheads> log)
    {
        _log = log;

        var o = opt.Value;

        _teiMax = Math.Clamp(o.TeiMaxConcurrency, 1, 16);
        _qdrantMax = Math.Clamp(o.QdrantMaxConcurrency, 1, 32);
        _ocrMax = Math.Clamp(o.OcrMaxConcurrency, 1, 8);
        _heavyComputeMax = Math.Clamp(o.HeavyComputeMaxConcurrency, 1, 16);

        _acquireTimeout = TimeSpan.FromSeconds(Math.Clamp(o.BulkheadAcquireTimeoutSeconds, 1, 3600));
        _ocrAcquireTimeout = TimeSpan.FromSeconds(IngestionOptions.ResolveOcrBulkheadQueueWaitTimeoutSeconds(
            o.OcrBulkheadAcquireTimeoutSeconds,
            o.OcrBulkheadQueueWaitTimeoutSeconds));
        _heavyComputeAcquireTimeout = TimeSpan.FromSeconds(
            IngestionOptions.ResolveHeavyComputeQueueWaitTimeoutSeconds(
                o.HeavyComputeQueueWaitTimeoutSeconds,
                o.OcrBulkheadAcquireTimeoutSeconds,
                o.OcrBulkheadQueueWaitTimeoutSeconds));

        _tei = new SemaphoreSlim(_teiMax, _teiMax);
        _qdrant = new SemaphoreSlim(_qdrantMax, _qdrantMax);
        _ocr = new SemaphoreSlim(_ocrMax, _ocrMax);
        _heavyCompute = new SemaphoreSlim(_heavyComputeMax, _heavyComputeMax);

        _log.LogInformation(
            "Bulkheads: TEI={Tei} Qdrant={Qdrant} OCR={Ocr} HeavyCompute={HeavyCompute} AcquireTimeout={Timeout}s OcrAcquireTimeout={OcrTimeout}s HeavyComputeAcquireTimeout={HeavyComputeTimeout}s",
            _teiMax,
            _qdrantMax,
            _ocrMax,
            _heavyComputeMax,
            (int)_acquireTimeout.TotalSeconds,
            (int)_ocrAcquireTimeout.TotalSeconds,
            (int)_heavyComputeAcquireTimeout.TotalSeconds);
    }

    public Task<IDisposable> AcquireTeiAsync(CancellationToken ct)
        => AcquireAsync(_tei, "TEI", _teiMax, ct);

    public Task<IDisposable> AcquireQdrantAsync(CancellationToken ct)
        => AcquireAsync(_qdrant, "Qdrant", _qdrantMax, ct);

    public Task<IDisposable> AcquireOcrAsync(CancellationToken ct)
        => AcquireDedicatedWithHeavyComputeAsync(
            _ocr,
            "OCR",
            _ocrMax,
            _ocrAcquireTimeout,
            ct);

    public Task<IDisposable> AcquireHeavyComputeAsync(CancellationToken ct)
        => AcquireAsync(
            _heavyCompute,
            "HeavyCompute",
            _heavyComputeMax,
            _heavyComputeAcquireTimeout,
            ct);

    private async Task<IDisposable> AcquireDedicatedWithHeavyComputeAsync(
        SemaphoreSlim dedicated,
        string dedicatedName,
        int dedicatedMax,
        TimeSpan dedicatedTimeout,
        CancellationToken ct)
    {
        var dedicatedLease = await AcquireAsync(
            dedicated,
            dedicatedName,
            dedicatedMax,
            dedicatedTimeout,
            ct);
        try
        {
            var computeLease = await AcquireHeavyComputeAsync(ct);
            return new CompositeReleaser(computeLease, dedicatedLease);
        }
        catch
        {
            dedicatedLease.Dispose();
            throw;
        }
    }

    private Task<IDisposable> AcquireAsync(SemaphoreSlim sem, string name, int max, CancellationToken ct)
        => AcquireAsync(sem, name, max, _acquireTimeout, ct);

    private async Task<IDisposable> AcquireAsync(SemaphoreSlim sem, string name, int max, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        bool ok;
        try
        {
            ok = await sem.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{name} bulkhead: canceled while waiting (timeout={timeout.TotalSeconds}s).");
        }

        if (!ok)
        {
            throw new IngestionBulkheadTimeoutException(name, max, timeout);
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds > 250)
        {
            _log.LogWarning("{Name} bulkhead waited {Ms}ms (max={Max})", name, sw.ElapsedMilliseconds, max);
        }

        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }

    private sealed class CompositeReleaser(
        IDisposable computeLease,
        IDisposable dedicatedLease) : IDisposable
    {
        private IDisposable? _computeLease = computeLease;
        private IDisposable? _dedicatedLease = dedicatedLease;

        public void Dispose()
        {
            Interlocked.Exchange(ref _computeLease, null)?.Dispose();
            Interlocked.Exchange(ref _dedicatedLease, null)?.Dispose();
        }
    }
}

sealed class IngestionBulkheadTimeoutException : TimeoutException
{
    public string BulkheadName { get; }
    public int MaxConcurrency { get; }
    public TimeSpan WaitTimeout { get; }

    public IngestionBulkheadTimeoutException(string bulkheadName, int maxConcurrency, TimeSpan waitTimeout)
        : base($"{bulkheadName} bulkhead: wait timeout after {waitTimeout.TotalSeconds}s (max={maxConcurrency}).")
    {
        BulkheadName = bulkheadName;
        MaxConcurrency = maxConcurrency;
        WaitTimeout = waitTimeout;
    }
}
