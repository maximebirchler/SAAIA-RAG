using Dapper;
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
        var duplicateFiles = await BuildDuplicateFileMapAsync(files, root, ingest, ct);

        await using var conn = await ds.OpenConnectionAsync(ct);

        int enqueued = 0, duplicateSuppressed = 0, duplicateDeletes = 0;
        foreach (var abs in files)
        {
            var rel = DocPathNormalizer.NormalizeToRelative(abs, root);

            if (IngestionPathFilter.ShouldIgnoreRel(rel))
                continue;

            var category = IngestionCategoryResolver.Derive(rel, ingest);

            if (categoryFilter is not null && category != categoryFilter)
                continue;

            if (duplicateFiles.TryGetValue(rel, out _))
            {
                duplicateSuppressed++;
                if (await ActiveDocumentExistsAsync(conn, tenantId, rel, ct))
                {
                    await IngestionEnqueue.EnqueueDeleteAsync(conn, tenantId, rel, ct);
                    duplicateDeletes++;
                }

                continue;
            }

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
            payload: new { scanned = files.Length, enqueued, duplicateSuppressed, duplicateDeletes, max, category = categoryFilter, forceWholeCatalog = req.ForceWholeCatalog },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { enqueued, scanned = files.Length, duplicateSuppressed, duplicateDeletes, max, category = categoryFilter, forceWholeCatalog = req.ForceWholeCatalog });
    }

    private static async Task<IReadOnlyDictionary<string, IngestionDuplicateFileDecision>> BuildDuplicateFileMapAsync(
        IReadOnlyCollection<string> files,
        string root,
        IngestionOptions opt,
        CancellationToken ct)
    {
        var candidates = new List<IngestionDuplicateFileCandidate>();
        foreach (var file in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(file); }
            catch { continue; }

            var rel = DocPathNormalizer.NormalizeToRelative(file, root);
            if (IngestionPathFilter.ShouldIgnoreRel(rel))
                continue;

            candidates.Add(new IngestionDuplicateFileCandidate(rel, file, fi.Length));
        }

        return await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(candidates, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ActiveDocumentExistsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        CancellationToken ct)
    {
        const string sql = """
SELECT 1
FROM documents
WHERE tenant_id=@tenantId
  AND doc_path=@docPath
  AND COALESCE(status, '') <> 'deleted'
  AND NOT (
    COALESCE(status, '') = 'missing'
    AND COALESCE(indexed_version, 0) <= 0
  )
LIMIT 1;
""";
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql,
            new { tenantId, docPath },
            cancellationToken: ct));
        return exists.HasValue;
    }
}

public sealed record ReindexRequest(int? Max = 5000, string? Category = null, bool? ForceWholeCatalog = null);
