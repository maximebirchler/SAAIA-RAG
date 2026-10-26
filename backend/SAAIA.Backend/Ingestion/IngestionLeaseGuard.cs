using Dapper;
using Npgsql;

internal sealed class IngestionLeaseLostException : Exception
{
    internal IngestionLeaseLostException(string reason = "ingestion_lease_lost") : base(reason) { }
}

internal enum IngestionLeaseRenewal { Renewed, OwnerObservedRenewalDeferred, OwnTerminal }

/// <summary>The persisted owner is a fresh opaque token for each dequeue, never a reusable consumer name.</summary>
internal static class IngestionLeaseGuard
{
    internal static string CreateOwner(string consumerId) => $"{consumerId}:{Guid.NewGuid():N}";

    internal static async Task<bool> LockOwnedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid jobId, string? leaseOwner, CancellationToken cancellationToken,
        Guid? tenantId = null, string? documentPath = null)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner)) return false;
        var owned = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
SELECT job_id FROM ingestion_jobs
WHERE job_id=@job AND locked_by=@owner AND status='running'
  AND (@tenant IS NULL OR tenant_id=@tenant)
  AND (@path IS NULL OR doc_path=@path)
FOR UPDATE;
""", new { job = jobId, owner = leaseOwner, tenant = tenantId, path = documentPath },
            transaction: transaction, cancellationToken: cancellationToken));
        return owned.HasValue;
    }

    internal static async Task<IngestionLeaseRenewal> RenewAsync(NpgsqlDataSource dataSource, Guid jobId, string? leaseOwner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner)) throw new IngestionLeaseLostException();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
WITH available AS (
    SELECT job_id FROM ingestion_jobs
    WHERE job_id=@job AND locked_by=@owner AND status='running'
    FOR UPDATE SKIP LOCKED
)
UPDATE ingestion_jobs j SET locked_at=now()
FROM available a WHERE j.job_id=a.job_id AND j.locked_by=@owner AND j.status='running';
""", new { job = jobId, owner = leaseOwner }, cancellationToken: cancellationToken));
        if (changed == 1) return IngestionLeaseRenewal.Renewed;
        // A publication holding this row already excludes stale requeue. Observe its owner without
        // waiting on its own lock; this does not claim that locked_at/TTL was renewed.
        var observedOwner = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_jobs WHERE job_id=@job AND locked_by=@owner AND status='running');
""", new { job = jobId, owner = leaseOwner }, cancellationToken: cancellationToken));
        if (observedOwner) return IngestionLeaseRenewal.OwnerObservedRenewalDeferred;
        // Publication clears locked_by before the wrapping operation unwinds. Recognize only our own
        // completed attempt; a successor always replaces executionLeaseOwner at dequeue.
        var completedByThisLease = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_jobs WHERE job_id=@job AND locked_by IS NULL
  AND status IN ('done','failed') AND payload->>'executionLeaseOwner'=@owner);
""", new { job = jobId, owner = leaseOwner }, cancellationToken: cancellationToken));
        if (!completedByThisLease) throw new IngestionLeaseLostException();
        return IngestionLeaseRenewal.OwnTerminal;
    }
}

/// <summary>A failed renewal cancels and observes the operation before ownership loss leaves this boundary.</summary>
internal static class IngestionLeaseExecution
{
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task> renew,
        Func<CancellationToken, Task<T>> operation, TimeSpan interval, CancellationToken cancellationToken,
        Func<int, TimeSpan, CancellationToken, Task>? onHeartbeat = null)
    {
        await renew(cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Exception? leaseFailure = null;
        var heartbeat = MonitorAsync();
        T result = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? operationFailure = null;
        try
        {
            result = await operation(operationCancellation.Token);
        }
        catch (Exception exception)
        {
            operationFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            await heartbeat;
        }
        // Decide only after the last in-flight renewal has been observed.
        if (leaseFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(leaseFailure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        operationFailure?.Throw();
        return result;

        async Task MonitorAsync()
        {
            var count = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(interval, heartbeatCancellation.Token);
                    await renew(heartbeatCancellation.Token);
                    if (onHeartbeat is not null)
                        await onHeartbeat(++count, elapsed.Elapsed, heartbeatCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                leaseFailure = exception is IngestionLeaseLostException
                    ? exception : new IngestionLeaseLostException("ingestion_heartbeat_failed");
                operationCancellation.Cancel();
            }
        }
    }
}
