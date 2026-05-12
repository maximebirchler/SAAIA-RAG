using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagSearchBulkheadTests
{
    [Fact]
    public async Task AcquireAsync_rejects_immediately_when_no_slot_and_queue_disabled()
    {
        var bulkhead = CreateBulkhead(maxConcurrency: 1, queueLimit: 0, waitTimeoutSeconds: 1);

        var first = await bulkhead.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        var second = await bulkhead.AcquireAsync(CancellationToken.None);

        Assert.Null(second);
        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(1, snapshot.Active);
        Assert.Equal(0, snapshot.Queued);
        first!.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_queues_within_limit_until_slot_is_released()
    {
        var bulkhead = CreateBulkhead(maxConcurrency: 1, queueLimit: 1, waitTimeoutSeconds: 5);

        var first = await bulkhead.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        var pending = bulkhead.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);

        var waitingSnapshot = bulkhead.GetSnapshot();
        Assert.Equal(1, waitingSnapshot.Active);
        Assert.Equal(1, waitingSnapshot.Queued);

        first!.Dispose();
        var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(second);
        using (second!)
        {
            Assert.True(second.WaitedQueued);
            var admittedSnapshot = bulkhead.GetSnapshot();
            Assert.Equal(1, admittedSnapshot.Active);
            Assert.Equal(0, admittedSnapshot.Queued);
        }
    }

    [Fact]
    public async Task AcquireAsync_rejects_when_queue_limit_is_full()
    {
        var bulkhead = CreateBulkhead(maxConcurrency: 1, queueLimit: 1, waitTimeoutSeconds: 5);

        var first = await bulkhead.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);
        var pending = bulkhead.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);

        var rejected = await bulkhead.AcquireAsync(CancellationToken.None);

        Assert.Null(rejected);
        first!.Dispose();
        var admitted = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(admitted);
        admitted!.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_does_not_let_later_arrivals_bypass_existing_queue()
    {
        var bulkhead = CreateBulkhead(maxConcurrency: 1, queueLimit: 2, waitTimeoutSeconds: 5);

        var first = await bulkhead.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        var queuedFirst = bulkhead.AcquireAsync(CancellationToken.None);
        await WaitForQueuedCountAsync(bulkhead, expectedQueued: 1);

        first!.Dispose();
        var laterArrival = bulkhead.AcquireAsync(CancellationToken.None);

        var admittedFirst = await queuedFirst.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(admittedFirst);
        Assert.True(admittedFirst!.WaitedQueued);

        await Task.Delay(100);
        Assert.False(laterArrival.IsCompleted);

        admittedFirst.Dispose();
        var admittedLater = await laterArrival.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(admittedLater);
        admittedLater!.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_releases_queue_count_when_wait_times_out()
    {
        var bulkhead = CreateBulkhead(maxConcurrency: 1, queueLimit: 1, waitTimeoutSeconds: 1);

        var first = await bulkhead.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        var timedOut = await bulkhead.AcquireAsync(CancellationToken.None);

        Assert.Null(timedOut);
        var snapshot = bulkhead.GetSnapshot();
        Assert.Equal(1, snapshot.Active);
        Assert.Equal(0, snapshot.Queued);
        first!.Dispose();
    }

    [Fact]
    public void ComputeRagSearchRetryAfterSeconds_scales_with_queue_depth_and_timeout()
    {
        var rag = new RagOptions
        {
            SearchRetryAfterSeconds = 3
        };
        var saturated = new RagSearchBulkheadSnapshot(
            Active: 4,
            Queued: 16,
            MaxConcurrency: 4,
            QueueLimit: 16,
            AvailableSlots: 0,
            QueueWaitTimeoutSeconds: 25);
        var shortTimeout = saturated with { QueueWaitTimeoutSeconds = 8 };
        var empty = saturated with { Queued = 0, QueueWaitTimeoutSeconds = 25 };

        Assert.Equal(15, RagEndpoints.ComputeRagSearchRetryAfterSeconds(rag, saturated));
        Assert.Equal(8, RagEndpoints.ComputeRagSearchRetryAfterSeconds(rag, shortTimeout));
        Assert.Equal(3, RagEndpoints.ComputeRagSearchRetryAfterSeconds(rag, empty));
    }

    private static async Task WaitForQueuedCountAsync(RagSearchBulkhead bulkhead, int expectedQueued)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested)
        {
            if (bulkhead.GetSnapshot().Queued == expectedQueued)
                return;

            await Task.Delay(25, cts.Token);
        }

        Assert.Equal(expectedQueued, bulkhead.GetSnapshot().Queued);
    }

    private static RagSearchBulkhead CreateBulkhead(int maxConcurrency, int queueLimit, int waitTimeoutSeconds)
        => new(
            Options.Create(new RagOptions
            {
                SearchMaxConcurrency = maxConcurrency,
                SearchQueueLimit = queueLimit,
                SearchQueueWaitTimeoutSeconds = waitTimeoutSeconds
            }),
            NullLogger<RagSearchBulkhead>.Instance);
}
