using System.Diagnostics;
using System.Net;
using SAAIA.Contracts;

namespace SAAIA.Client.Core.AdvancedAnalysis;

public sealed class AdvancedAnalysisPollingPolicy
{
    public int MaximumPolls { get; init; } = 600;
    public int MaximumConsecutiveTransportFailures { get; init; } = 8;
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromMilliseconds(1_500);
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan MaximumScheduledPollDelay { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record AdvancedAnalysisPollEvent(string Operation, string Outcome, string? Reason,
    int Attempt, int ConsecutiveFailures, int Revision, long DurationMilliseconds);
public sealed record AdvancedAnalysisPollingResult(AdvancedAnalysisJobDto Job, string Outcome, string? Reason);

/// <summary>Portable transport workflow. It never reads model state or UI objects and never invents a terminal answer.</summary>
public sealed class AdvancedAnalysisJobPoller(
    Func<Guid, CancellationToken, Task<AdvancedAnalysisJobDto>> getJob,
    Func<TimeSpan, CancellationToken, Task> delayAsync,
    Action<AdvancedAnalysisPollEvent>? observe = null,
    Func<Exception, TimeSpan?>? retryAfter = null,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<Guid, CancellationToken, Task<AdvancedAnalysisJobDto>> _getJob = getJob ?? throw new ArgumentNullException(nameof(getJob));
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    public int ObserverFailures { get; private set; }
    public async Task<AdvancedAnalysisPollingResult> PollAsync(AdvancedAnalysisJobDto initial,
        AdvancedAnalysisPollingPolicy policy, CancellationToken cancellationToken,
        Func<AdvancedAnalysisJobDto, CancellationToken, Task>? onSnapshot = null,
        Action<AdvancedAnalysisJobDto>? onWaiting = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(policy);
        var job = initial;
        var consecutiveFailures = 0;
        var maximumPolls = Math.Clamp(policy.MaximumPolls, 0, 100_000);
        var attempt = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            for (; !IsTerminal(job.Status); attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= maximumPolls)
                    return Pending(job, "poll_limit_reached", attempt, consecutiveFailures, clock.ElapsedMilliseconds);
                onWaiting?.Invoke(job);
                await _delayAsync(PollDelay(job, policy), cancellationToken).ConfigureAwait(false);
                AdvancedAnalysisJobDto next;
                var requestClock = Stopwatch.StartNew();
                Emit("attempt", "success", null, attempt + 1, consecutiveFailures, job.Revision, 0);
                try
                {
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var requestTimeout = policy.RequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30)
                        : policy.RequestTimeout > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : policy.RequestTimeout;
                    requestCancellation.CancelAfter(requestTimeout);
                    try { next = await _getJob(job.JobId, requestCancellation.Token).WaitAsync(requestCancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && requestCancellation.IsCancellationRequested)
                    {
                        throw new HttpRequestException("advanced_poll_request_timeout", exception, HttpStatusCode.RequestTimeout);
                    }
                }
                catch (Exception exception) when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    Emit("retry", "degraded", exception is HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout, InnerException: OperationCanceledException }
                            ? "request_deadline_exceeded" : exception.GetType().Name, attempt + 1, consecutiveFailures,
                        job.Revision, requestClock.ElapsedMilliseconds);
                    if (consecutiveFailures > Math.Clamp(policy.MaximumConsecutiveTransportFailures, 0, 1_000))
                        return Pending(job, "transport_retry_limit_reached", attempt + 1, consecutiveFailures, clock.ElapsedMilliseconds);
                    await _delayAsync(RetryDelay(exception, consecutiveFailures, policy), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                consecutiveFailures = 0;
                var invalid = ValidateSnapshot(next, job.JobId, job.HandoffId, job.SessionId, job.Revision);
                if (invalid is not null)
                {
                    Emit("validation", "failed", invalid, attempt + 1, 0, job.Revision, requestClock.ElapsedMilliseconds);
                    return new(job, "invalid", invalid);
                }
                job = next;
                Emit("response", "success", job.Status, attempt + 1, 0, job.Revision, requestClock.ElapsedMilliseconds);
                if (onSnapshot is not null) await onSnapshot(job, cancellationToken).ConfigureAwait(false);
            }
            Emit("complete", job.Status == "succeeded" ? "success" : job.Status == "canceled" ? "cancelled" : "failed",
                job.LastErrorCode, attempt, consecutiveFailures, job.Revision, clock.ElapsedMilliseconds);
            return new(job, "terminal", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Emit("cancelled", "cancelled", "cancellation_requested", attempt + 1, consecutiveFailures, job.Revision, clock.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            Emit("failed", "failed", exception.GetType().Name, attempt + 1, consecutiveFailures, job.Revision, clock.ElapsedMilliseconds);
            throw;
        }
    }

    public static string? ValidateSnapshot(AdvancedAnalysisJobDto job, Guid? expectedJobId,
        Guid expectedHandoffId, Guid expectedSessionId, int minimumRevision)
    {
        if (job.JobId == Guid.Empty || (expectedJobId.HasValue && job.JobId != expectedJobId)) return "job_identity_changed";
        if (job.HandoffId != expectedHandoffId) return "handoff_identity_changed";
        if (job.SessionId != expectedSessionId) return "session_identity_changed";
        if (job.Status is not ("queued" or "running" or "succeeded" or "failed" or "canceled")) return "job_status_invalid";
        return job.Revision < minimumRevision ? "job_revision_regressed" : null;
    }

    public static bool IsTransient(Exception exception)
        => exception is not OperationCanceledException && (exception is IOException || exception is HttpRequestException http
            && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout));

    private static bool IsTerminal(string? status) => status is "succeeded" or "failed" or "canceled";
    private AdvancedAnalysisPollingResult Pending(AdvancedAnalysisJobDto job, string reason, int attempt, int failures, long duration)
    {
        Emit("pending", "degraded", reason, attempt, failures, job.Revision, duration);
        return new(job, "pending", reason);
    }
    private void Emit(string operation, string outcome, string? reason, int attempt, int failures, int revision, long duration)
    {
        try { observe?.Invoke(new(operation, outcome, reason, attempt, failures, revision, Math.Max(0, duration))); } catch { ObserverFailures++; }
    }
    private TimeSpan PollDelay(AdvancedAnalysisJobDto job, AdvancedAnalysisPollingPolicy policy)
    {
        var ordinary = ClampDelay(policy.PollDelay, policy.MaximumRetryDelay);
        var now = utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
        if (job.Status != "queued" || job.AvailableAtUtc <= now + ordinary) return ordinary;
        return TimeSpan.FromMilliseconds(Math.Clamp((job.AvailableAtUtc - now).TotalMilliseconds,
            ordinary.TotalMilliseconds, Math.Max(ordinary.TotalMilliseconds,
                ClampDelay(policy.MaximumScheduledPollDelay, TimeSpan.FromMinutes(5)).TotalMilliseconds)));
    }
    private TimeSpan RetryDelay(Exception exception, int attempt, AdvancedAnalysisPollingPolicy policy)
    {
        if (retryAfter?.Invoke(exception) is { } delay) return ClampDelay(delay, policy.MaximumRetryDelay);
        var maximum = policy.MaximumRetryDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : policy.MaximumRetryDelay;
        var milliseconds = Math.Max(0, policy.RetryBaseDelay.TotalMilliseconds) * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, maximum.TotalMilliseconds));
    }
    private static TimeSpan ClampDelay(TimeSpan delay, TimeSpan maximum)
        => delay < TimeSpan.Zero ? TimeSpan.Zero : delay > (maximum <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : maximum)
            ? maximum <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : maximum : delay;
}
