using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

sealed class IngestionBulkheads
{
    private readonly ILogger<IngestionBulkheads> _log;

    private readonly SemaphoreSlim _tei;
    private readonly SemaphoreSlim _qdrant;
    private readonly SemaphoreSlim _ocr;

    private readonly int _teiMax;
    private readonly int _qdrantMax;
    private readonly int _ocrMax;
    private readonly TimeSpan _acquireTimeout;

    public IngestionBulkheads(IOptions<IngestionOptions> opt, ILogger<IngestionBulkheads> log)
    {
        _log = log;

        var o = opt.Value;

        _teiMax = Math.Clamp(o.TeiMaxConcurrency, 1, 16);
        _qdrantMax = Math.Clamp(o.QdrantMaxConcurrency, 1, 32);
        _ocrMax = Math.Clamp(o.OcrMaxConcurrency, 1, 8);

        _acquireTimeout = TimeSpan.FromSeconds(Math.Clamp(o.BulkheadAcquireTimeoutSeconds, 1, 3600));

        _tei = new SemaphoreSlim(_teiMax, _teiMax);
        _qdrant = new SemaphoreSlim(_qdrantMax, _qdrantMax);
        _ocr = new SemaphoreSlim(_ocrMax, _ocrMax);

        _log.LogInformation("Bulkheads: TEI={Tei} Qdrant={Qdrant} OCR={Ocr} AcquireTimeout={Timeout}s",
            _teiMax, _qdrantMax, _ocrMax, (int)_acquireTimeout.TotalSeconds);
    }

    public Task<IDisposable> AcquireTeiAsync(CancellationToken ct)
        => AcquireAsync(_tei, "TEI", _teiMax, ct);

    public Task<IDisposable> AcquireQdrantAsync(CancellationToken ct)
        => AcquireAsync(_qdrant, "Qdrant", _qdrantMax, ct);

    public Task<IDisposable> AcquireOcrAsync(CancellationToken ct)
        => AcquireAsync(_ocr, "OCR", _ocrMax, ct);

    private async Task<IDisposable> AcquireAsync(SemaphoreSlim sem, string name, int max, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        bool ok;
        try
        {
            ok = await sem.WaitAsync(_acquireTimeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{name} bulkhead: canceled while waiting (timeout={_acquireTimeout.TotalSeconds}s).");
        }

        if (!ok)
        {
            throw new TimeoutException($"{name} bulkhead: wait timeout after {_acquireTimeout.TotalSeconds}s (max={max}).");
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
}
