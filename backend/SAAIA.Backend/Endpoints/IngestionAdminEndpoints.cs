using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class IngestionAdminEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/ingestion/scan", ScanAsync).RequireAdminKey();
        app.MapGet("/ingestion/jobs", ListJobsAsync).RequireAdminKey();
    }

    public sealed record ScanRequest(int? Max = null, string? Category = null);

    private static async Task<IResult> ScanAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestOpt,
        int? max,
        string? category)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ingest = ingestOpt.Value;
        var ct = ctx.RequestAborted;

        var root = ingest.DocumentsRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Results.BadRequest(new { error = "DocumentsRoot not found", root });

        // Allow empty body (curl without JSON) by reading optional JSON payload.
        ScanRequest? body = null;
        if (ctx.Request.ContentLength is > 0)
        {
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ScanRequest>(cancellationToken: ct);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }
        }

        var maxEff = Math.Clamp(max ?? body?.Max ?? ingest.MaxFilesPerScan, 1, 200000);
        var minAge = TimeSpan.FromSeconds(Math.Clamp(ingest.MinFileAgeSeconds, 0, 3600));

        var catRaw = string.IsNullOrWhiteSpace(category) ? body?.Category : category;
        var filterCategory = string.IsNullOrWhiteSpace(catRaw) ? null : catRaw.Trim().ToLowerInvariant();

        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Take(maxEff)
            .ToArray();

        var now = DateTimeOffset.UtcNow;

        await using var conn = await ds.OpenConnectionAsync(ct);

        int enqueued = 0;
        int eligible = 0;

        foreach (var abs in files)
        {
            string rel;
            try { rel = DocPathNormalizer.NormalizeToRelative(abs, root); }
            catch { continue; }

            if (IngestionPathFilter.ShouldIgnoreRel(rel))
                continue;

            FileInfo fi;
            try { fi = new FileInfo(abs); }
            catch { continue; }

            // évite d'ingérer un fichier en cours de copie
            if (minAge > TimeSpan.Zero && (now - fi.LastWriteTimeUtc) < minAge)
                continue;

            var docCategory = ingest.CategoryFromFirstFolder
                ? (rel.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant()
                : (ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant();

            if (filterCategory is not null && docCategory != filterCategory)
                continue;

            eligible++;

            await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, docCategory, fi, ct, enqueueSource: "admin");
            enqueued++;
        }

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "ingestion.scan",
            target: "ingestion.scan",
            payload: new { scanned = files.Length, eligible, enqueued, max = maxEff, category = filterCategory },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { scanned = files.Length, eligible, enqueued, max = maxEff, category = filterCategory });
    }

    private static async Task<IResult> ListJobsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? status,
        string? action,
        string? docPath,
        int? limit,
        int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 200, 1, 2000);
        var off = Math.Max(offset ?? 0, 0);

        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        action = string.IsNullOrWhiteSpace(action) ? null : action.Trim().ToLowerInvariant();
        docPath = string.IsNullOrWhiteSpace(docPath) ? null : docPath.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  job_id       AS ""JobId"",
  action       AS ""Action"",
  doc_path     AS ""DocPath"",
  category     AS ""Category"",
  priority     AS ""Priority"",
  status       AS ""Status"",
  attempts     AS ""Attempts"",
  locked_by    AS ""LockedBy"",
  locked_at    AS ""LockedAt"",
  available_at AS ""AvailableAt"",
  last_error   AS ""LastError"",
  created_at   AS ""CreatedAt"",
  started_at   AS ""StartedAt"",
  finished_at  AS ""FinishedAt"",
  payload      AS ""Payload""
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND (@status IS NULL OR status=@status)
  AND (@action IS NULL OR action=@action)
  AND (@docPath IS NULL OR doc_path ILIKE ('%' || @docPath || '%'))
ORDER BY created_at DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, status, action, docPath, lim, off });
        return Results.Ok(new { items = rows, limit = lim, offset = off });
    }
}
