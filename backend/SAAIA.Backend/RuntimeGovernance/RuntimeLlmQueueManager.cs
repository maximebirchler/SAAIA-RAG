using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeLlmQueueManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _activeByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _queuedByUser = new(StringComparer.Ordinal);
    private int _active;
    private int _queued;

    internal AdminRuntimeLlmQueueSnapshotDto GetSnapshot(AdminRuntimeLlmCapacityPlanDto? plan = null)
    {
        var policy = RuntimeLlmQueuePolicy.FromPlan(plan);
        lock (_gate)
        {
            return new AdminRuntimeLlmQueueSnapshotDto(
                Active: _active,
                Queued: _queued,
                TotalSlots: policy.TotalSlots,
                QueueLimit: policy.QueueLimit,
                PerUserActiveLimit: policy.PerUserActiveLimit,
                PerUserQueuedLimit: policy.PerUserQueuedLimit,
                AvailableSlots: Math.Max(0, policy.TotalSlots - _active));
        }
    }

    internal RuntimeLlmQueueLease? TryAcquire(string userKey, AdminRuntimeLlmCapacityPlanDto? plan = null)
    {
        userKey = NormalizeUserKey(userKey);
        var policy = RuntimeLlmQueuePolicy.FromPlan(plan);

        lock (_gate)
        {
            if (_active >= policy.TotalSlots)
                return null;

            if (GetCount(_activeByUser, userKey) >= policy.PerUserActiveLimit)
                return null;

            _active++;
            _activeByUser[userKey] = GetCount(_activeByUser, userKey) + 1;
            return new RuntimeLlmQueueLease(this, userKey);
        }
    }

    internal async Task<RuntimeLlmQueueLease?> AcquireOrQueueAsync(
        string userKey,
        AdminRuntimeLlmCapacityPlanDto? plan,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        var lease = TryAcquire(userKey, plan);
        if (lease is not null)
            return lease;

        if (!TryRegisterQueued(userKey, plan))
            return null;

        var stillQueued = true;
        try
        {
            var deadline = DateTimeOffset.UtcNow.Add(maxWait);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);

                lease = TryAcquire(userKey, plan);
                if (lease is null)
                    continue;

                ReleaseQueued(userKey);
                stillQueued = false;
                return lease;
            }

            return null;
        }
        finally
        {
            if (stillQueued)
                ReleaseQueued(userKey);
        }
    }

    internal bool TryRegisterQueued(string userKey, AdminRuntimeLlmCapacityPlanDto? plan = null)
    {
        userKey = NormalizeUserKey(userKey);
        var policy = RuntimeLlmQueuePolicy.FromPlan(plan);

        lock (_gate)
        {
            if (_queued >= policy.QueueLimit)
                return false;

            if (GetCount(_queuedByUser, userKey) >= policy.PerUserQueuedLimit)
                return false;

            _queued++;
            _queuedByUser[userKey] = GetCount(_queuedByUser, userKey) + 1;
            return true;
        }
    }

    internal void ReleaseQueued(string userKey)
    {
        userKey = NormalizeUserKey(userKey);
        lock (_gate)
        {
            Decrement(_queuedByUser, userKey);
            _queued = Math.Max(0, _queued - 1);
        }
    }

    private void ReleaseActive(string userKey)
    {
        lock (_gate)
        {
            Decrement(_activeByUser, userKey);
            _active = Math.Max(0, _active - 1);
        }
    }

    private static string NormalizeUserKey(string userKey)
        => string.IsNullOrWhiteSpace(userKey) ? "anonymous" : userKey.Trim();

    private static int GetCount(Dictionary<string, int> values, string key)
        => values.TryGetValue(key, out var count) ? count : 0;

    private static void Decrement(Dictionary<string, int> values, string key)
    {
        if (!values.TryGetValue(key, out var count))
            return;

        if (count <= 1)
            values.Remove(key);
        else
            values[key] = count - 1;
    }

    internal sealed class RuntimeLlmQueueLease(RuntimeLlmQueueManager owner, string userKey) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseActive(userKey);
        }
    }
}

internal sealed record RuntimeLlmQueuePolicy(
    int TotalSlots,
    int QueueLimit,
    int PerUserActiveLimit,
    int PerUserQueuedLimit)
{
    internal static RuntimeLlmQueuePolicy FromPlan(AdminRuntimeLlmCapacityPlanDto? plan)
        => new(
            TotalSlots: Math.Max(1, plan?.TotalSlots ?? 1),
            QueueLimit: Math.Max(0, plan?.QueueLimit ?? 10),
            PerUserActiveLimit: Math.Max(1, plan?.PerUserActiveLimit ?? 1),
            PerUserQueuedLimit: Math.Max(0, plan?.PerUserQueuedLimit ?? 2));
}
