using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/admin/reindex", ReindexAsync);
    }

    internal static async Task<IResult> ReindexAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestOpt,
        ReindexRequest req)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();

        var ingest = ingestOpt.Value;
        var ct = ctx.RequestAborted;

        var max = Math.Clamp(req.Max ?? 5000, 1, 200000);
        var root = ingest.DocumentsRoot;
        var categoryFilter = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();

        if (categoryFilter is null && req.ForceWholeCatalog != true)
        {
            return Results.BadRequest(new
            {
                error = "whole_catalog_reindex_requires_force",
                hint = "Pass forceWholeCatalog=true for a full catalog scan, or pass category to scope the operation."
            });
        }

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Results.BadRequest(new { error = "DocumentsRoot not found", root });

        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Take(max)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);

        int enqueued = 0;
        foreach (var abs in files)
        {
            var rel = DocPathNormalizer.NormalizeToRelative(abs, root);

            if (IngestionPathFilter.ShouldIgnoreRel(rel))
                continue;

            var category = IngestionCategoryResolver.Derive(rel, ingest);

            if (categoryFilter is not null && category != categoryFilter)
                continue;

            var fi = new FileInfo(abs);
            await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct, enqueueSource: "admin");
            enqueued++;
        }

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "ingestion.scan",
            target: "admin.reindex",
            payload: new { scanned = files.Length, enqueued, max, category = categoryFilter, forceWholeCatalog = req.ForceWholeCatalog },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { enqueued, scanned = files.Length, max, category = categoryFilter, forceWholeCatalog = req.ForceWholeCatalog });
    }
}

public sealed record ReindexRequest(int? Max = 5000, string? Category = null, bool? ForceWholeCatalog = null);
