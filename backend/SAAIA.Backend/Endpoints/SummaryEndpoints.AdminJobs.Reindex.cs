using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

public static partial class SummaryEndpoints
{
    private static async Task<IResult> ReindexAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);

        DocumentRow? doc = null;
        if (cmd.DocId is not null && cmd.DocId != Guid.Empty)
            doc = await LoadDocumentAsync(conn, tenantId, cmd.DocId.Value, ct);
        else if (!string.IsNullOrWhiteSpace(cmd.DocPath))
            doc = await LoadDocumentByPathAsync(conn, tenantId, cmd.DocPath!, ct);

        if (doc is null)
            return Results.NotFound(new { error = "document_not_found" });

        FileInfo? fi = null;
        try
        {
            var documentsRoot = ingestOpt.Value.DocumentsRoot;
            var absPath = DocPathNormalizer.ToAbsoluteFromRelative(doc.DocPath, documentsRoot);
            if (!File.Exists(absPath))
                return Results.NotFound(new { error = "document_file_not_found", docPath = doc.DocPath });

            fi = new FileInfo(absPath);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "invalid_document_path", detail = ex.Message, docPath = doc.DocPath });
        }

        var activeJob = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string Status, bool CancelRequested, string? RequestedAction)>(new CommandDefinition(
            """
SELECT
  job_id AS "JobId",
  status AS "Status",
  COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
  payload #>> '{control,requestedAction}' AS "RequestedAction"
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running','paused')
ORDER BY created_at DESC
LIMIT 1;
""",
            new { tenant = tenantId, docPath = doc.DocPath },
            cancellationToken: ct));

        if (activeJob.JobId != Guid.Empty)
        {
            var effectiveStatus = string.Equals(activeJob.Status, "running", StringComparison.OrdinalIgnoreCase) && activeJob.CancelRequested
                ? (IngestionAdminStatePolicies.IsPauseRequested(activeJob.RequestedAction) ? "paused" : "cancel_requested")
                : activeJob.Status;
            return Results.Conflict(new
            {
                error = "active_job_exists",
                jobId = activeJob.JobId,
                status = effectiveStatus,
                docPath = doc.DocPath
            });
        }

        var category = IngestionCategoryResolver.Derive(doc.DocPath, ingestOpt.Value);
        var enqueued = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, doc.DocPath, category, fi, ct: ct, enqueueSource: "admin");
        return Results.Ok(new { queued = true, docId = enqueued.DocId, jobId = enqueued.JobId, docPath = doc.DocPath });
    }
}
