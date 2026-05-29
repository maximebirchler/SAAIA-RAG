using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class TeiWorkloadGovernorTests
{
    [Fact]
    public async Task AcquireIngestionAsync_waits_while_interactive_request_is_active()
    {
        var governor = new TeiWorkloadGovernor();
        using var interactive = governor.BeginInteractiveRequest();

        var pendingIngestion = governor.AcquireIngestionAsync(
            interactiveQuietPeriodMs: 25,
            CancellationToken.None);
        await Task.Delay(50);

        Assert.False(pendingIngestion.IsCompleted);

        interactive.Dispose();
        using var ingestionLease = await pendingIngestion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(ingestionLease);
    }

    [Fact]
    public async Task AcquireIngestionAsync_respects_recent_interactive_quiet_period()
    {
        var governor = new TeiWorkloadGovernor();
        using (governor.BeginInteractiveRequest())
        {
        }

        var pendingIngestion = governor.AcquireIngestionAsync(
            interactiveQuietPeriodMs: 150,
            CancellationToken.None);
        await Task.Delay(50);

        Assert.False(pendingIngestion.IsCompleted);

        using var ingestionLease = await pendingIngestion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(ingestionLease);
    }

    [Fact]
    public async Task AcquireInteractiveAsync_can_take_slot_while_ingestion_is_waiting_for_quiet_period()
    {
        var governor = new TeiWorkloadGovernor();
        using var interactiveMarker = governor.BeginInteractiveRequest();
        var pendingIngestion = governor.AcquireIngestionAsync(
            interactiveQuietPeriodMs: 200,
            CancellationToken.None);
        await Task.Delay(50);

        using var interactiveLease = await governor.AcquireInteractiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pendingIngestion.IsCompleted);
    }

    [Fact]
    public async Task AcquireInteractiveAsync_waits_only_for_current_tei_slot_holder()
    {
        var governor = new TeiWorkloadGovernor();
        using var ingestionLease = await governor.AcquireIngestionAsync(
            interactiveQuietPeriodMs: 0,
            CancellationToken.None);

        var pendingInteractive = governor.AcquireInteractiveAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(pendingInteractive.IsCompleted);

        ingestionLease.Dispose();
        using var interactiveLease = await pendingInteractive.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(interactiveLease);
    }
}
