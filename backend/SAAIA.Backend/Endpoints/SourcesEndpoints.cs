using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static class SourcesEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/sources/resolve", ResolveSourceAsync);
    }

    private static async Task<IResult> ResolveSourceAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        SourceResolveRequest request)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        var raw = (request.Ref ?? request.PdfRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Results.Ok(new SourceResolveResponse
            {
                Error = "missing_source_ref",
                RequestedRef = raw
            });
        }

        var normalizedPath = raw.Replace('\\', '/').Trim().Trim('/');
        var normalizedName = Path.GetFileName(normalizedPath);
        var likePath = $"%/{normalizedName}";

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
SELECT
  doc_id      AS ""DocId"",
  doc_path    AS ""DocPath"",
  doc_name    AS ""DocName"",
  page_count  AS ""PageCount""
FROM documents
WHERE tenant_id=@tenant
  AND status='indexed'
  AND (
        CAST(doc_id AS text)=@raw
     OR LOWER(doc_path)=LOWER(@normalizedPath)
     OR LOWER(doc_name)=LOWER(@normalizedName)
     OR LOWER(doc_path) LIKE LOWER(@likePath)
  )
ORDER BY
  CASE
    WHEN CAST(doc_id AS text)=@raw THEN 0
    WHEN LOWER(doc_path)=LOWER(@normalizedPath) THEN 1
    WHEN LOWER(doc_name)=LOWER(@normalizedName) THEN 2
    ELSE 3
  END,
  updated_at DESC
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync<ResolvedSourceRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, raw, normalizedPath, normalizedName, likePath },
            cancellationToken: ct));

        if (row is null)
        {
            return Results.Ok(new SourceResolveResponse
            {
                Error = "source_not_found",
                RequestedRef = raw
            });
        }

        var pageEnd = row.PageCount > 0 ? row.PageCount : 1;
        return Results.Ok(new SourceResolveResponse
        {
            RequestedRef = raw,
            Source = new ResolvedSourceDto
            {
                DocId = row.DocId,
                DocPath = row.DocPath ?? string.Empty,
                DocName = row.DocName ?? string.Empty,
                PageStart = 1,
                PageEnd = pageEnd,
                Label = string.IsNullOrWhiteSpace(row.DocName) ? row.DocPath ?? string.Empty : row.DocName!
            }
        });
    }

    private sealed class ResolvedSourceRow
    {
        public Guid? DocId { get; set; }
        public string? DocPath { get; set; }
        public string? DocName { get; set; }
        public int PageCount { get; set; }
    }
}
