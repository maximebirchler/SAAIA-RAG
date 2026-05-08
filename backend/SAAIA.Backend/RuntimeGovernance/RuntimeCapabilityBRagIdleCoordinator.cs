namespace SAAIA.Backend;

internal static class RuntimeCapabilityBRagIdleCoordinator
{
    private static readonly object Gate = new();
    private static int _activeRetrievals;
    private static DateTimeOffset? _lastActivityAt;

    internal static TimeSpan ResolveRequiredIdleDelay(RuntimeGovernanceOptions options)
        => TimeSpan.FromSeconds(Math.Max(0, options.CapabilityBRagIdleDelaySeconds));

    internal static IDisposable? BeginInteractiveRetrieval(RuntimeGovernanceOptions? options = null, DateTimeOffset? now = null)
    {
        if (options?.CapabilityBRequireRagIdleForExecution != true)
            return null;

        lock (Gate)
        {
            _activeRetrievals++;
            _lastActivityAt = now ?? DateTimeOffset.UtcNow;
        }

        return new ActivityScope(now);
    }

    internal static CapabilityBRagIdleSnapshot LoadSnapshot(RuntimeGovernanceOptions options, DateTimeOffset now)
    {
        int activeRetrievals;
        DateTimeOffset? lastActivityAt;
        lock (Gate)
        {
            activeRetrievals = _activeRetrievals;
            lastActivityAt = _lastActivityAt;
        }

        return Evaluate(activeRetrievals, lastActivityAt, ResolveRequiredIdleDelay(options), now);
    }

    internal static CapabilityBRagIdleSnapshot Evaluate(
        int activeRetrievals,
        DateTimeOffset? lastActivityAt,
        TimeSpan requiredIdleDelay,
        DateTimeOffset now)
    {
        if (activeRetrievals > 0)
        {
            return new CapabilityBRagIdleSnapshot(
                IsIdle: false,
                ActiveRetrievals: activeRetrievals,
                LastRagActivityAt: lastActivityAt,
                RequiredIdleDelay: requiredIdleDelay,
                IdleFor: null,
                Reason: "rag_active");
        }

        if (!lastActivityAt.HasValue)
        {
            return new CapabilityBRagIdleSnapshot(
                IsIdle: true,
                ActiveRetrievals: 0,
                LastRagActivityAt: null,
                RequiredIdleDelay: requiredIdleDelay,
                IdleFor: null,
                Reason: "no_rag_activity");
        }

        var idleFor = now - lastActivityAt.Value;
        if (idleFor < TimeSpan.Zero)
            idleFor = TimeSpan.Zero;

        var isIdle = idleFor >= requiredIdleDelay;
        return new CapabilityBRagIdleSnapshot(
            IsIdle: isIdle,
            ActiveRetrievals: 0,
            LastRagActivityAt: lastActivityAt,
            RequiredIdleDelay: requiredIdleDelay,
            IdleFor: idleFor,
            Reason: isIdle ? "rag_idle" : "rag_recent");
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _activeRetrievals = 0;
            _lastActivityAt = null;
        }
    }

    private static void EndInteractiveRetrieval(DateTimeOffset? now)
    {
        lock (Gate)
        {
            _activeRetrievals = Math.Max(0, _activeRetrievals - 1);
            _lastActivityAt = now ?? DateTimeOffset.UtcNow;
        }
    }

    private sealed class ActivityScope(DateTimeOffset? fixedNow) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            EndInteractiveRetrieval(fixedNow);
        }
    }
}

internal sealed record CapabilityBRagIdleSnapshot(
    bool IsIdle,
    int ActiveRetrievals,
    DateTimeOffset? LastRagActivityAt,
    TimeSpan RequiredIdleDelay,
    TimeSpan? IdleFor,
    string Reason);
