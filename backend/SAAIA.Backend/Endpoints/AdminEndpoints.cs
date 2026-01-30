using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/admin/reindex", ReindexAsync);
    }

    private static async Task<IResult> ReindexAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestOpt,
        ReindexRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ingest = ingestOpt.Value;
        var ct = ctx.RequestAborted;

        var max = Math.Clamp(req.Max ?? 5000, 1, 200000);
        var root = ingest.DocumentsRoot;

        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Take(max)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);

        int enqueued = 0;
        foreach (var abs in files)
        {
            var rel = DocPathNormalizer.NormalizeToRelative(abs, root);

            var category = ingest.CategoryFromFirstFolder
                ? (rel.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant()
                : (ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(req.Category) &&
                category != req.Category.Trim().ToLowerInvariant())
                continue;

            var fi = new FileInfo(abs);
            await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct);
            enqueued++;
        }

        return Results.Ok(new { enqueued, scanned = files.Length, max });
    }
}

public sealed record ReindexRequest(int? Max = 5000, string? Category = null);
