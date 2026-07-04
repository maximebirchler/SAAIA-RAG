using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionBulkheadsTests
{
    [Fact]
    public void BuildWorkerId_includes_instance_marker_and_worker_index()
    {
        var workerId = IngestionWorker.BuildWorkerId("backend-1234-abcd", 2);

        Assert.Equal("backend-1234-abcd-w2", workerId);
    }

    [Fact]
    public void CreateWorkerInstanceId_returns_short_unique_marker()
    {
        var marker = IngestionWorker.CreateWorkerInstanceId();

        Assert.False(string.IsNullOrWhiteSpace(marker));
        Assert.True(marker.Length <= 32);
        Assert.Matches(@"^[\p{L}\p{N}_-]+-\d+-[a-f0-9]{8}$", marker);
    }

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
    public void ResolveOcrBulkheadQueueWaitTimeoutSeconds_preserves_legacy_timeout_when_queue_wait_is_not_configured()
    {
        var resolved = IngestionOptions.ResolveOcrBulkheadQueueWaitTimeoutSeconds(
            ocrBulkheadAcquireTimeoutSeconds: 1800,
            ocrBulkheadQueueWaitTimeoutSeconds: 0);

        Assert.Equal(1800, resolved);
    }

    [Fact]
    public void ResolveOcrBulkheadQueueWaitTimeoutSeconds_allows_explicit_queue_wait_above_legacy_timeout()
    {
        var resolved = IngestionOptions.ResolveOcrBulkheadQueueWaitTimeoutSeconds(
            ocrBulkheadAcquireTimeoutSeconds: 30,
            ocrBulkheadQueueWaitTimeoutSeconds: 300);

        Assert.Equal(300, resolved);
    }
}
