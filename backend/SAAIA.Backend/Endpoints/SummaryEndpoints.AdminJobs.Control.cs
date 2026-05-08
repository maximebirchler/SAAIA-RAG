using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

public static partial class SummaryEndpoints
{
    private static Task<IResult> PauseAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, IngestionJobCancellationRegistry cancelRegistry, JobPauseCommand cmd)
        => HandleAdminJobControlAsync(ctx, ds, cancelRegistry, cmd.JobId, AdminJobControlAction.Pause);

    private static Task<IResult> CancelAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, IngestionJobCancellationRegistry cancelRegistry, JobCancelCommand cmd)
        => HandleAdminJobControlAsync(ctx, ds, cancelRegistry, cmd.JobId, AdminJobControlAction.Cancel);

    private static async Task<IResult> HandleAdminJobControlAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IngestionJobCancellationRegistry cancelRegistry,
        Guid jobId,
        AdminJobControlAction requestedAction)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (jobId == Guid.Empty)
            return Results.BadRequest(new { error = "job_id_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (requestedAction == AdminJobControlAction.Pause)
        {
            var pausedAdminRows = await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE admin_jobs
SET status='paused',
    started_at=NULL,
    last_error=NULL,
    payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,pauseRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb('pause'::text),
        true)
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='queued';
""",
                new { tenant = tenantId, jobId },
                transaction: tx,
                cancellationToken: ct));

            if (pausedAdminRows > 0)
            {
                await tx.CommitAsync(ct);
                return Results.Ok(new
                {
                    paused = true,
                    jobId,
                    type = "summary",
                    requestedAction = "pause",
                    status = "paused",
                    queuedStateChanged = pausedAdminRows,
                    result = "paused"
                });
            }

            var adminJob = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string JobType, string Status, string? DocPath)>(
                new CommandDefinition(
                    """
SELECT job_id AS "JobId", job_type AS "JobType", status AS "Status", doc_path AS "DocPath"
FROM admin_jobs
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""",
                    new { tenant = tenantId, jobId },
                    transaction: tx,
                    cancellationToken: ct));

            if (adminJob.JobId != Guid.Empty)
            {
                await tx.CommitAsync(ct);
                var status = (adminJob.Status ?? string.Empty).Trim().ToLowerInvariant();
                var pauseResult = status switch
                {
                    "paused" => "already_paused",
                    "running" => "running_item_will_finish",
                    "done" or "failed" or "canceled" or "cancelled" => "already_finished",
                    _ => "nothing_changed"
                };

                return Results.Ok(new
                {
                    paused = status == "paused",
                    jobId,
                    type = "summary",
                    requestedAction = "pause",
                    docPath = adminJob.DocPath,
                    previousStatus = status,
                    status,
                    queuedStateChanged = 0,
                    result = pauseResult
                });
            }
        }

        if (requestedAction == AdminJobControlAction.Cancel)
        {
            var cancelAdminSql = """
UPDATE admin_jobs
SET status='canceled', canceled_at=now(), finished_at=now()
WHERE tenant_id=@tenant AND job_id=@jobId AND status IN ('queued','running','paused');
""";
            var changed = await conn.ExecuteAsync(new CommandDefinition(cancelAdminSql, new { tenant = tenantId, jobId }, transaction: tx, cancellationToken: ct));
            if (changed > 0)
            {
                await tx.CommitAsync(ct);
                return Results.Ok(new { canceled = true, jobId, type = "summary", affected = changed, status = "canceled", result = "canceled", requestedAction = "cancel" });
            }
        }

        var ingestionRef = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string DocPath, string Status, bool CancelRequested, string? RequestedAction, string Action, int DocumentIndexedVersion, string? DocumentStatus, bool DocumentAutoIngestPaused, string? DocumentAutoIngestPauseReason)>(
            new CommandDefinition(
                """
SELECT
  i.job_id AS "JobId",
  i.doc_path AS "DocPath",
  i.status AS "Status",
  COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
  i.payload #>> '{control,requestedAction}' AS "RequestedAction",
  i.action AS "Action",
  COALESCE(d.indexed_version, 0) AS "DocumentIndexedVersion",
  d.status AS "DocumentStatus",
  COALESCE(d.auto_ingest_paused, false) AS "DocumentAutoIngestPaused",
  d.auto_ingest_pause_reason AS "DocumentAutoIngestPauseReason"
FROM ingestion_jobs i
LEFT JOIN documents d
  ON d.tenant_id=i.tenant_id
 AND d.doc_path=i.doc_path
WHERE i.tenant_id=@tenant AND i.job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId },
                transaction: tx,
                cancellationToken: ct));

        if (ingestionRef.JobId == Guid.Empty)
        {
            await tx.CommitAsync(ct);
            return Results.NotFound(new { error = "job_not_found", jobId });
        }

        if (requestedAction == AdminJobControlAction.Pause
            && !IngestionAdminStatePolicies.CanPause(ingestionRef.Action, ingestionRef.DocumentIndexedVersion))
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new
            {
                error = "job_not_pausable",
                jobId,
                type = "ingestion",
                docPath = ingestionRef.DocPath,
                action = ingestionRef.Action,
                status = ingestionRef.Status
            });
        }

        var terminalStatus = (ingestionRef.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (terminalStatus is "done" or "failed" or "canceled" or "cancelled")
        {
            await tx.CommitAsync(ct);
            return Results.Ok(new
            {
                canceled = false,
                jobId,
                type = "ingestion",
                requestedAction = IngestionAdminStatePolicies.ToControlValue(requestedAction),
                docPath = ingestionRef.DocPath,
                previousStatus = terminalStatus,
                status = terminalStatus,
                queuedStateChanged = 0,
                runningCancelRequested = 0,
                canceledInMemory = 0,
                result = "already_finished"
            });
        }

        var requestedActionValue = IngestionAdminStatePolicies.ToControlValue(requestedAction);
        var runningCancelRequested = requestedAction == AdminJobControlAction.Pause
            ? await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE ingestion_jobs
SET status='paused',
    started_at=NULL,
    finished_at=NULL,
    last_error=NULL,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb(@requestedAction::text),
        true)
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND action='upsert'
  AND status='running';
""",
                new { tenant = tenantId, jobId, requestedAction = requestedActionValue },
                transaction: tx,
                cancellationToken: ct))
            : await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE ingestion_jobs
SET payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb(@requestedAction::text),
        true)
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running';
""",
                new { tenant = tenantId, jobId, requestedAction = requestedActionValue },
                transaction: tx,
                cancellationToken: ct));

        var queuedStateChanged = requestedAction == AdminJobControlAction.Pause
            ? await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE ingestion_jobs
SET status='paused',
    attempts=0,
    started_at=NULL,
    finished_at=NULL,
    last_error=NULL,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = jsonb_set(
        (COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}'),
        '{control,requestedAction}',
        to_jsonb(@requestedAction::text),
        true)
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status IN ('queued','paused');
""",
                new { tenant = tenantId, jobId, requestedAction = requestedActionValue },
                transaction: tx,
                cancellationToken: ct))
            : await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error,'canceled_by_admin'),
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status IN ('queued','paused');
""",
                new { tenant = tenantId, jobId },
                transaction: tx,
                cancellationToken: ct));

        if (requestedAction == AdminJobControlAction.Pause && (queuedStateChanged > 0 || runningCancelRequested > 0))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE documents
SET auto_ingest_paused=true,
    auto_ingest_paused_at=COALESCE(auto_ingest_paused_at, now()),
    auto_ingest_pause_reason='admin_cancel',
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        WHEN COALESCE(status, '') IN ('missing','deleted') THEN status
        ELSE 'pending'
    END,
    updated_at=now()
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND COALESCE(indexed_version, 0) <= 0;
""",
                new { tenant = tenantId, docPath = ingestionRef.DocPath },
                transaction: tx,
                cancellationToken: ct));
        }
        else if (requestedAction == AdminJobControlAction.Cancel
            && string.Equals(ingestionRef.Action, "upsert", StringComparison.OrdinalIgnoreCase)
            && ingestionRef.DocumentIndexedVersion > 0
            && (queuedStateChanged > 0 || runningCancelRequested > 0)
            && !string.Equals(ingestionRef.DocumentStatus, "missing", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ingestionRef.DocumentStatus, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE documents
SET status='indexed',
    ingestion_version=COALESCE(indexed_version, 0),
    auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL,
    updated_at=now()
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND COALESCE(indexed_version, 0) > 0
  AND COALESCE(status, '') NOT IN ('missing','deleted');
""",
                new { tenant = tenantId, docPath = ingestionRef.DocPath },
                transaction: tx,
                cancellationToken: ct));
        }

        if (requestedAction == AdminJobControlAction.Cancel && queuedStateChanged > 0)
            await JobRepo.FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

        var effectiveStatusRow = await conn.QueryFirstOrDefaultAsync<(string? Status, bool CancelRequested, string? RequestedAction, string? Action, int DocumentIndexedVersion, bool DocumentAutoIngestPaused, string? DocumentAutoIngestPauseReason)>(new CommandDefinition(
            """
SELECT
    i.status AS "Status",
    COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    i.payload #>> '{control,requestedAction}' AS "RequestedAction",
    i.action AS "Action",
    COALESCE(d.indexed_version, 0) AS "DocumentIndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "DocumentAutoIngestPaused",
    d.auto_ingest_pause_reason AS "DocumentAutoIngestPauseReason"
FROM ingestion_jobs i
LEFT JOIN documents d
  ON d.tenant_id=i.tenant_id
 AND d.doc_path=i.doc_path
WHERE i.tenant_id=@tenant AND i.job_id=@jobId
LIMIT 1;
""",
            new { tenant = tenantId, jobId },
            transaction: tx,
            cancellationToken: ct));

        var effectiveStatus = AdminJobsQuerySupport.GetVisibleIngestionStatus(
            effectiveStatusRow.Status,
            effectiveStatusRow.CancelRequested,
            effectiveStatusRow.RequestedAction,
            effectiveStatusRow.Action,
            effectiveStatusRow.DocumentIndexedVersion,
            effectiveStatusRow.DocumentAutoIngestPaused,
            effectiveStatusRow.DocumentAutoIngestPauseReason);

        var normalizedPreviousStatus = string.Equals(ingestionRef.Status, "running", StringComparison.OrdinalIgnoreCase) && ingestionRef.CancelRequested
            ? "cancel_requested"
            : ingestionRef.Status;

        var result = effectiveStatus switch
        {
            "paused" => "paused",
            "canceled" or "cancelled" => "canceled",
            "cancel_requested" => "cancel_requested",
            "done" or "failed" => "already_finished",
            _ when queuedStateChanged > 0 || runningCancelRequested > 0 => "accepted",
            _ => "nothing_changed"
        };

        await tx.CommitAsync(ct);

        var canceledInMemory = 0;
        if (runningCancelRequested > 0 && cancelRegistry.TryCancel(jobId))
            canceledInMemory++;

        return Results.Ok(new
        {
            canceled = queuedStateChanged > 0 || runningCancelRequested > 0 || string.Equals(effectiveStatus, "canceled", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveStatus, "paused", StringComparison.OrdinalIgnoreCase),
            jobId,
            type = "ingestion",
            requestedAction = requestedActionValue,
            docPath = ingestionRef.DocPath,
            previousStatus = normalizedPreviousStatus,
            status = string.IsNullOrWhiteSpace(effectiveStatus) ? normalizedPreviousStatus : effectiveStatus,
            queuedStateChanged,
            runningCancelRequested,
            canceledInMemory,
            result
        });
    }

    private static async Task<IResult> ResumeAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, JobResumeCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.JobId == Guid.Empty)
            return Results.BadRequest(new { error = "job_id_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var resumedAdminRows = await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE admin_jobs
SET status='queued',
    started_at=NULL,
    finished_at=NULL,
    canceled_at=NULL,
    last_error=NULL,
    payload = ((COALESCE(payload, '{}'::jsonb) #- '{control,pauseRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='paused';
""",
            new { tenant = tenantId, jobId = cmd.JobId },
            transaction: tx,
            cancellationToken: ct));

        if (resumedAdminRows > 0)
        {
            await tx.CommitAsync(ct);
            return Results.Ok(new
            {
                resumed = true,
                jobId = cmd.JobId,
                type = "summary",
                status = "queued",
                queuedStateChanged = resumedAdminRows
            });
        }

        var adminJob = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string JobType, string Status, string? DocPath)>(
            new CommandDefinition(
                """
SELECT job_id AS "JobId", job_type AS "JobType", status AS "Status", doc_path AS "DocPath"
FROM admin_jobs
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId = cmd.JobId },
                transaction: tx,
                cancellationToken: ct));

        if (adminJob.JobId != Guid.Empty)
        {
            await tx.CommitAsync(ct);
            var status = (adminJob.Status ?? string.Empty).Trim().ToLowerInvariant();
            return Results.Ok(new
            {
                resumed = false,
                reason = status == "paused" ? "not_resumed" : "not_paused",
                jobId = cmd.JobId,
                type = "summary",
                docPath = adminJob.DocPath,
                status
            });
        }

        var row = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string DocPath, string Category, bool AutoIngestPaused, string? AutoIngestPauseReason, string Status, string Action, string? DocumentStatus, int DocumentIndexedVersion)>(
            new CommandDefinition(
                """
SELECT
  i.job_id AS "JobId",
  i.doc_path AS "DocPath",
  COALESCE(d.category, i.category, '') AS "Category",
  COALESCE(d.auto_ingest_paused, false) AS "AutoIngestPaused",
  d.auto_ingest_pause_reason AS "AutoIngestPauseReason",
  i.status AS "Status",
  i.action AS "Action",
  d.status AS "DocumentStatus",
  COALESCE(d.indexed_version, 0) AS "DocumentIndexedVersion"
FROM ingestion_jobs i
LEFT JOIN documents d
  ON d.tenant_id=i.tenant_id
 AND d.doc_path=i.doc_path
WHERE i.tenant_id=@tenant AND i.job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId = cmd.JobId },
                transaction: tx,
                cancellationToken: ct));

        if (row.JobId == Guid.Empty)
        {
            await tx.CommitAsync(ct);
            return Results.NotFound(new { error = "job_not_found", jobId = cmd.JobId });
        }

        var resumeEligibility = IngestionAdminStatePolicies.EvaluateResumeEligibility(
            row.Action,
            row.Status,
            row.AutoIngestPaused,
            row.AutoIngestPauseReason,
            row.DocumentStatus);

        if (resumeEligibility == ResumeEligibility.NotPaused)
        {
            await tx.CommitAsync(ct);
            return Results.Ok(new { resumed = false, reason = "not_paused", jobId = cmd.JobId, docPath = row.DocPath });
        }

        if (resumeEligibility == ResumeEligibility.NotUpsert)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "only_upsert_can_resume", jobId = cmd.JobId, docPath = row.DocPath });
        }

        if (resumeEligibility == ResumeEligibility.WrongJobStatus)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "job_not_resumable", jobId = row.JobId, status = row.Status, docPath = row.DocPath });
        }

        if (resumeEligibility == ResumeEligibility.UnsupportedPauseReason)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "unsupported_pause_reason", jobId = row.JobId, reason = row.AutoIngestPauseReason, docPath = row.DocPath });
        }

        if (resumeEligibility == ResumeEligibility.DocumentDeleted)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "document_deleted", jobId = row.JobId, docPath = row.DocPath });
        }

        var active = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
SELECT 1
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running')
  AND job_id<>@jobId
LIMIT 1;
""",
            new { tenant = tenantId, docPath = row.DocPath, jobId = row.JobId },
            transaction: tx,
            cancellationToken: ct));

        if (active.HasValue)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "active_job_exists", docPath = row.DocPath });
        }

        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(row.DocPath, ingestOpt.Value.DocumentsRoot);
        if (!File.Exists(absPath))
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "document_file_not_found", docPath = row.DocPath });
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE documents
SET auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL,
    status=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        WHEN COALESCE(status, '') IN ('missing','deleted') THEN status
        ELSE 'pending'
    END,
    updated_at=now()
WHERE tenant_id=@tenant AND doc_path=@docPath;
""",
            new { tenant = tenantId, docPath = row.DocPath },
            transaction: tx,
            cancellationToken: ct));

        var resumedRows = await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE ingestion_jobs
SET status='queued',
    attempts=0,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    started_at=NULL,
    finished_at=NULL,
    last_error=NULL,
    payload = ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND action='upsert'
  AND status='paused';
""",
            new { tenant = tenantId, jobId = row.JobId },
            transaction: tx,
            cancellationToken: ct));

        if (resumedRows <= 0)
        {
            await tx.CommitAsync(ct);
            return Results.Conflict(new { error = "job_not_resumable", jobId = row.JobId, status = row.Status, docPath = row.DocPath });
        }

        await tx.CommitAsync(ct);

        return Results.Ok(new { resumed = true, docPath = row.DocPath, jobId = row.JobId });
    }
}
