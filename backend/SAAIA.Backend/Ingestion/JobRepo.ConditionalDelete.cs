using Dapper;
using Npgsql;

internal enum ReturnedScannerSourceOutcome { FreshGenerationQueued, Superseded }

static partial class JobRepo
{
    internal const string ReturnedScannerSourceReason = "scanner_source_returned_before_delete";

    /// <summary>Cancel only our conditional delete and atomically schedule a fresh source generation.</summary>
    internal static async Task<ReturnedScannerSourceOutcome> PreserveReturnedScannerSourceAsync(NpgsqlDataSource ds,
        IngestionJob job, string leaseOwner, CancellationToken ct)
    {
        if (!job.RequiresSourceAbsence || string.IsNullOrWhiteSpace(leaseOwner))
            throw new IngestionLeaseLostException();
        await using var connection = await ds.OpenConnectionAsync(ct);
        // Reservation is a short autocommit statement before job/document locks. A lost
        // lease can leave a harmless gap, never a mutation of the successor publication.
        var ownsConditionalDelete = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_jobs WHERE job_id=@job AND tenant_id=@tenant AND doc_path=@path
 AND action='delete' AND status='running' AND locked_by=@owner
 AND payload->'requiresSourceAbsence'='true'::jsonb);
""", new { job = job.JobId, tenant = job.TenantId, path = job.DocPath, owner = leaseOwner }, cancellationToken: ct));
        if (!ownsConditionalDelete) throw new IngestionLeaseLostException();
        var generation = await IngestionGenerationRegistry.ReserveAsync(connection, job.TenantId, job.DocId, ct);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await IngestionLeaseGuard.LockOwnedAsync(connection, transaction, job.JobId, leaseOwner, ct, job.TenantId, job.DocPath))
            throw new IngestionLeaseLostException();
        var cancelRequested = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT COALESCE(payload #>> '{control,cancelRequested}','false')='true'
FROM ingestion_jobs WHERE job_id=@job;
""", new { job = job.JobId }, transaction: transaction, cancellationToken: ct));
        if (cancelRequested) throw new JobCanceledException("canceled_by_admin_request");
        var document = await LoadCurrentDocumentVersionStateAsync(connection, transaction, job.TenantId, job.DocPath, ct);
        var sameDocument = document?.DocId == job.DocId;
        var current = sameDocument && document?.IngestionVersion == job.Version;
        if (current)
        {
            var currentDocument = document!;
            if (generation <= job.Version) throw new InvalidOperationException("ingestion_generation_not_advanced");
            var updated = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE documents SET status=CASE WHEN indexed_version>0 THEN 'indexed' ELSE 'pending' END,
 ingestion_version=@generation, missing_since=NULL, last_seen_at=now(), updated_at=now()
WHERE tenant_id=@tenant AND doc_id=@doc AND doc_path=@path AND ingestion_version=@version;
""", new { generation, tenant = job.TenantId, doc = job.DocId, path = job.DocPath, version = job.Version },
                transaction: transaction, cancellationToken: ct));
            if (updated != 1) throw new InvalidOperationException("conditional_delete_document_changed");
            var payload = IngestionJobPayloadJson.Serialize(job.DocId, generation,
                source: "scanner_source_returned", indexedVersionBefore: currentDocument.IndexedVersion);
            // An active upsert here would represent a conflicting state not admitted by
            // this transition. Roll back rather than overwrite another running attempt.
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ingestion_jobs(job_id,tenant_id,action,doc_path,category,status,payload,available_at)
VALUES(@id,@tenant,'upsert',@path,@category,'queued',@payload::jsonb,now());
""", new { id = Guid.NewGuid(), tenant = job.TenantId, path = job.DocPath,
                category = IngestionCategoryResolver.Normalize(currentDocument.Category), payload },
                transaction: transaction, cancellationToken: ct));
        }
        var canceled = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ingestion_jobs SET status='canceled', finished_at=COALESCE(finished_at,now()),
 last_error=@reason, locked_by=NULL, locked_at=NULL,
 payload=((COALESCE(payload,'{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE job_id=@job AND status='running' AND locked_by=@owner;
""", new { job = job.JobId, owner = leaseOwner,
            reason = current ? ReturnedScannerSourceReason : "superseded_at_commit" },
            transaction: transaction, cancellationToken: ct));
        if (canceled != 1) throw new IngestionLeaseLostException();
        if (!current && sameDocument)
            await QueueCurrentVersionAfterSupersededAsync(connection, transaction, job.TenantId, job.DocPath,
                document, job.Version, 100, ct);
        await FreezeTerminalSnapshotAsync(connection, job.JobId, transaction, ct);
        await transaction.CommitAsync(ct);
        return current ? ReturnedScannerSourceOutcome.FreshGenerationQueued : ReturnedScannerSourceOutcome.Superseded;
    }

    private static async Task<bool> CurrentDeleteRequiresSourceAbsenceAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenant, string path, int version, CancellationToken ct)
    {
        // Coalescing a newer request into a running delete must not lose its provenance.
        // Legacy requests without an annotation remain explicit/false. Do not invent
        // scanner provenance from status or from a historical filesystem observation.
        return await connection.ExecuteScalarAsync<bool?>(new CommandDefinition("""
SELECT CASE WHEN payload #> '{pendingDeleteIntent,version}'=to_jsonb(@version::int)
 THEN COALESCE(payload #> '{pendingDeleteIntent,requiresSourceAbsence}'='true'::jsonb,false)
 ELSE COALESCE(payload->'requiresSourceAbsence'='true'::jsonb,false) END
FROM ingestion_jobs WHERE tenant_id=@tenant AND doc_path=@path AND action='delete'
 AND (payload #> '{pendingDeleteIntent,version}'=to_jsonb(@version::int) OR payload->'version'=to_jsonb(@version::int))
ORDER BY created_at DESC,job_id LIMIT 1;
""", new { tenant, path, version }, transaction: transaction, cancellationToken: ct)) ?? false;
    }
}

internal sealed class IngestionSourcePresenceUnknownException : IOException
{
    internal IngestionSourcePresenceUnknownException() : base("scanner_delete_source_presence_unknown") { }
}

internal sealed class IngestionSourceReturnedBeforeCommitException : IOException
{
    internal IngestionSourceReturnedBeforeCommitException() : base("scanner_source_returned_before_delete_commit") { }
}
