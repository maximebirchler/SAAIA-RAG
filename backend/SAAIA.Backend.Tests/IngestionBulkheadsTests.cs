using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionBulkheadsTests
{
    [Fact]
    public async Task AcquireOcrAsync_respects_configured_concurrency_limit()
    {
        var bulkheads = new IngestionBulkheads(
            Options.Create(new IngestionOptions
            {
                OcrMaxConcurrency = 1,
                BulkheadAcquireTimeoutSeconds = 1,
                OcrBulkheadAcquireTimeoutSeconds = 1
            }),
            NullLogger<IngestionBulkheads>.Instance);

        using var first = await bulkheads.AcquireOcrAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IngestionBulkheadTimeoutException>(() => bulkheads.AcquireOcrAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcquireOcrAsync_timeout_carries_bulkhead_metadata()
    {
        var bulkheads = new IngestionBulkheads(
            Options.Create(new IngestionOptions
            {
                OcrMaxConcurrency = 1,
                BulkheadAcquireTimeoutSeconds = 1,
                OcrBulkheadAcquireTimeoutSeconds = 1
            }),
            NullLogger<IngestionBulkheads>.Instance);

        using var first = await bulkheads.AcquireOcrAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IngestionBulkheadTimeoutException>(
            () => bulkheads.AcquireOcrAsync(CancellationToken.None));

        Assert.Equal("OCR", ex.BulkheadName);
        Assert.Equal(1, ex.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(1), ex.WaitTimeout);
    }

    [Fact]
    public async Task AcquireOcrAsync_uses_ocr_specific_acquire_timeout()
    {
        var bulkheads = new IngestionBulkheads(
            Options.Create(new IngestionOptions
            {
                OcrMaxConcurrency = 1,
                BulkheadAcquireTimeoutSeconds = 1,
                OcrBulkheadAcquireTimeoutSeconds = 3
            }),
            NullLogger<IngestionBulkheads>.Instance);

        using var first = await bulkheads.AcquireOcrAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<OperationCanceledException>(() => bulkheads.AcquireOcrAsync(cts.Token));
    }

    [Fact]
    public async Task AcquireOcrAsync_uses_short_queue_wait_before_legacy_ceiling()
    {
        var bulkheads = new IngestionBulkheads(
            Options.Create(new IngestionOptions
            {
                OcrMaxConcurrency = 1,
                BulkheadAcquireTimeoutSeconds = 1,
                OcrBulkheadAcquireTimeoutSeconds = 1800,
                OcrBulkheadQueueWaitTimeoutSeconds = 1
            }),
            NullLogger<IngestionBulkheads>.Instance);

        using var first = await bulkheads.AcquireOcrAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IngestionBulkheadTimeoutException>(
            () => bulkheads.AcquireOcrAsync(CancellationToken.None));

        Assert.Equal("OCR", ex.BulkheadName);
        Assert.Equal(TimeSpan.FromSeconds(1), ex.WaitTimeout);
    }

    [Fact]
    public void ResolveOcrBulkheadQueueWaitTimeoutSeconds_never_exceeds_legacy_acquire_timeout()
    {
        var resolved = IngestionOptions.ResolveOcrBulkheadQueueWaitTimeoutSeconds(
            ocrBulkheadAcquireTimeoutSeconds: 30,
            ocrBulkheadQueueWaitTimeoutSeconds: 300);

        Assert.Equal(30, resolved);
    }
}
