using System.Net;
using SAAIA.Client.Core.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class AdvancedAnalysisJobPollerTests
{
    [Fact]
    public async Task Poller_records_transient_disconnect_and_recovers_without_advancing_revision_until_valid_snapshot()
    {
        var initial = Job("queued", 1);
        var events = new List<AdvancedAnalysisPollEvent>();
        var calls = 0;
        var snapshots = new List<int>();
        var poller = new AdvancedAnalysisJobPoller((_, _) => ++calls == 1
                ? throw new HttpRequestException("lost response") : Task.FromResult(Job("succeeded", 2, initial)),
            static (_, _) => Task.CompletedTask, events.Add);
        var result = await poller.PollAsync(initial, new() { MaximumPolls = 4 }, CancellationToken.None,
            (snapshot, _) => { snapshots.Add(snapshot.Revision); return Task.CompletedTask; });
        Assert.Equal("terminal", result.Outcome);
        Assert.Equal(new[] { 2 }, snapshots);
        Assert.Contains(events, entry => entry.Operation == "retry" && entry.ConsecutiveFailures == 1);
        Assert.Contains(events, entry => entry.Operation == "complete" && entry.Outcome == "success");
    }

    [Theory]
    [InlineData("changed_job", "job_identity_changed")]
    [InlineData("revision_regressed", "job_revision_regressed")]
    [InlineData("invalid_status", "job_status_invalid")]
    public async Task Poller_rejects_regressed_or_wrong_identity_before_notifying_ui(string mutation, string reason)
    {
        var initial = Job("running", 5);
        var next = mutation == "changed_job" ? Job("succeeded", 6) : Job(mutation == "invalid_status" ? "unknown" : "running", 4, initial);
        var snapshots = 0;
        var poller = new AdvancedAnalysisJobPoller((_, _) => Task.FromResult(next), static (_, _) => Task.CompletedTask);
        var result = await poller.PollAsync(initial, new(), CancellationToken.None,
            (_, _) => { snapshots++; return Task.CompletedTask; });
        Assert.Equal("invalid", result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Same(initial, result.Job);
        Assert.Equal(0, snapshots);
    }

    [Fact]
    public async Task Poller_reports_pending_when_no_poll_was_allowed_instead_of_success()
    {
        var calls = 0;
        var poller = new AdvancedAnalysisJobPoller((_, _) => { calls++; return Task.FromResult(Job("succeeded", 2)); }, static (_, _) => Task.CompletedTask);
        var result = await poller.PollAsync(Job("queued", 1), new() { MaximumPolls = 0 }, CancellationToken.None);
        Assert.Equal("pending", result.Outcome);
        Assert.Equal("poll_limit_reached", result.Reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Poller_respects_scheduled_queue_delay_and_observer_failure_is_nonblocking()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = Job("queued", 1, availableAtUtc: now.AddMinutes(4));
        var delays = new List<TimeSpan>();
        var poller = new AdvancedAnalysisJobPoller((_, _) => Task.FromResult(Job("failed", 2, initial)),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            _ => throw new IOException(), utcNow: () => now);
        var result = await poller.PollAsync(initial, new() { MaximumScheduledPollDelay = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
        Assert.Equal("terminal", result.Outcome);
        Assert.Equal("failed", result.Job.Status);
        Assert.True(poller.ObserverFailures > 0);
    }

    [Fact]
    public async Task Poller_cancellation_is_reported_and_never_calls_cancel_server_as_side_effect()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<AdvancedAnalysisPollEvent>();
        var poller = new AdvancedAnalysisJobPoller((_, _) => throw new InvalidOperationException("must not poll"),
            (_, _) => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); }, events.Add);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PollAsync(Job("running", 1), new(), cancellation.Token));
        Assert.Contains(events, entry => entry.Operation == "cancelled" && entry.Outcome == "cancelled");
    }

    [Fact]
    public async Task Hung_poll_has_a_bounded_attempt_timeout_and_keeps_the_durable_job_pending()
    {
        var events = new List<AdvancedAnalysisPollEvent>();
        var poller = new AdvancedAnalysisJobPoller(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }, static (_, _) => Task.CompletedTask, events.Add);
        var result = await poller.PollAsync(Job("running", 1), new()
            { RequestTimeout = TimeSpan.FromMilliseconds(10), MaximumConsecutiveTransportFailures = 0 }, CancellationToken.None);
        Assert.Equal("pending", result.Outcome);
        Assert.Equal("transport_retry_limit_reached", result.Reason);
        Assert.Contains(events, entry => entry.Operation == "retry" && entry.Outcome == "degraded");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public void Retry_policy_distinguishes_permanent_and_transient_http_failures(HttpStatusCode status, bool expected)
        => Assert.Equal(expected, AdvancedAnalysisJobPoller.IsTransient(new HttpRequestException("test", null, status)));

    private static AdvancedAnalysisJobDto Job(string status, int revision, AdvancedAnalysisJobDto? previous = null,
        DateTimeOffset? availableAtUtc = null) => new()
    {
        JobId = previous?.JobId ?? Guid.NewGuid(), HandoffId = previous?.HandoffId ?? Guid.NewGuid(),
        SessionId = previous?.SessionId ?? Guid.NewGuid(), Status = status, Revision = revision,
        AvailableAtUtc = availableAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1)
    };
}
