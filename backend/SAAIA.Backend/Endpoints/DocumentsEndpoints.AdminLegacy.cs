using Dapper;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    private static async Task<IResult> ListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? category,
        string? status,
        string? q,
        int? limit,
        int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 200, 1, 2000);
        var off = Math.Max(offset ?? 0, 0);

        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  doc_id            AS ""DocId"",
  doc_path          AS ""DocPath"",
  doc_name          AS ""DocName"",
  category          AS ""Category"",
  status            AS ""Status"",
  file_size         AS ""FileSize"",
  file_mtime        AS ""FileMtime"",
  page_count        AS ""PageCount"",
  last_ingested_at  AS ""LastIngestedAt"",
  ingestion_version AS ""IngestionVersion"",
  last_seen_at      AS ""LastSeenAt"",
  missing_since     AS ""MissingSince"",
  created_at        AS ""CreatedAt"",
  updated_at        AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant
  AND (@category IS NULL OR category=@category)
  AND (@status IS NULL OR status=@status)
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')))
ORDER BY updated_at DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, category, status, q, lim, off });
        return Results.Ok(new { items = rows, limit = lim, offset = off });
    }

    private static async Task<IResult> GetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  doc_id            AS ""DocId"",
  doc_path          AS ""DocPath"",
  doc_name          AS ""DocName"",
  category          AS ""Category"",
  status            AS ""Status"",
  content_hash      AS ""ContentHash"",
  file_size         AS ""FileSize"",
  file_mtime        AS ""FileMtime"",
  mime_type         AS ""MimeType"",
  page_count        AS ""PageCount"",
  last_ingested_at  AS ""LastIngestedAt"",
  ingestion_version AS ""IngestionVersion"",
  last_seen_at      AS ""LastSeenAt"",
  missing_since     AS ""MissingSince"",
  created_at        AS ""CreatedAt"",
  updated_at        AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, docId });
        if (row is null) return Results.NotFound();

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.get",
            target: docId.ToString(),
            payload: null,
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(row);
    }
}
