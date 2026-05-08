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
                BulkheadAcquireTimeoutSeconds = 1
            }),
            NullLogger<IngestionBulkheads>.Instance);

        using var first = await bulkheads.AcquireOcrAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => bulkheads.AcquireOcrAsync(CancellationToken.None));
    }
}
