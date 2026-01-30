using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class IngestionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/ingest/enqueue", EnqueueAsync);
    }

    private static async Task<IResult> EnqueueAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestOpt,
        IngestEnqueueRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ingest = ingestOpt.Value;
        var ct = ctx.RequestAborted;

        if (string.IsNullOrWhiteSpace(req.DocPath))
            return Results.BadRequest(new { error = "docPath is required" });

        string relDocPath;
        try
        {
            // accepte relatif OU absolu (si sous DocumentsRoot), et sort toujours un relatif propre
            relDocPath = DocPathNormalizer.NormalizeToRelative(req.DocPath, ingest.DocumentsRoot);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (IngestionPathFilter.ShouldIgnoreRel(relDocPath))
            return Results.BadRequest(new { error = "docPath is ignored by ingestion filter (must be a non-temp .pdf path)" });

        var action = (req.Action ?? "upsert").Trim().ToLowerInvariant();
        if (action is not ("upsert" or "delete"))
            return Results.BadRequest(new { error = "action must be upsert|delete" });

        // category toujours en lower; défaut = config
        var category = string.IsNullOrWhiteSpace(req.Category)
            ? ingest.DefaultCategory
            : req.Category.Trim().ToLowerInvariant();

        await using var conn = await ds.OpenConnectionAsync(ct);

        if (action == "delete")
        {
            var r = await IngestionEnqueue.EnqueueDeleteAsync(conn, tenantId, relDocPath, ct);
            return Results.Ok(new
            {
                jobId = r.JobId,
                tenantId,
                docId = r.DocId,
                version = r.Version,
                action,
                docPath = relDocPath,
                category = (string?)null
            });
        }
        else
        {
            FileInfo? fi = null;
            try
            {
                var abs = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);
                if (File.Exists(abs)) fi = new FileInfo(abs);
            }
            catch { /* ignore */ }

            var r = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, relDocPath, category, fi, ct);
            return Results.Ok(new
            {
                jobId = r.JobId,
                tenantId,
                docId = r.DocId,
                version = r.Version,
                action = "upsert",
                docPath = relDocPath,
                category
            });
        }
    }
}

public sealed record IngestEnqueueRequest(string DocPath, string? Category, string? Action);
