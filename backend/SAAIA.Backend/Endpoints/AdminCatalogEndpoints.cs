using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.CatalogSnapshot;

namespace SAAIA.Backend.Endpoints;

// Marker type for DI logging (static classes cannot be generic type arguments)
internal sealed class AdminCatalogEndpointsMarker { }

public static class AdminCatalogEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/catalog/snapshot/status", StatusAsync);
        app.MapPost("/admin/catalog/snapshot/refresh", RefreshAsync);
        app.MapGet("/admin/qdrant/health", QdrantHealthAsync);

        app.Logger.LogInformation("Mapped admin catalog snapshot endpoints");
    }

    private static async Task<IResult> StatusAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  tenant_id   AS ""TenantId"",
  computed_at AS ""ComputedAt"",
  total_docs  AS ""TotalDocs"",
  max_depth   AS ""MaxDepth""
FROM documents_catalog_summary
ORDER BY computed_at DESC;";

        var rows = await conn.QueryAsync(sql);
        return Results.Ok(new { items = rows });
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<AdminCatalogEndpointsMarker> logger,
        IOptions<CatalogSnapshotOptions> opt)
    {
        AdminAuth.EnsureAdmin(ctx);

        var result = await CatalogSnapshotBuilder.BuildAllActiveTenantsAsync(ds, opt.Value, logger, ctx.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> QdrantHealthAsync(
        HttpContext ctx,
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> ragOpt,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);

        var rag = ragOpt.Value;
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var sw = Stopwatch.StartNew();
        using var collectionResp = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ctx.RequestAborted);
        sw.Stop();

        if (!collectionResp.IsSuccessStatusCode)
        {
            var err = await collectionResp.Content.ReadAsStringAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                ok = false,
                collection = rag.QdrantCollection,
                status = (int)collectionResp.StatusCode,
                latencyMs = sw.ElapsedMilliseconds,
                error = string.IsNullOrWhiteSpace(err) ? collectionResp.ReasonPhrase : err
            });
        }

        using var collectionDoc = JsonDocument.Parse(await collectionResp.Content.ReadAsStringAsync(ctx.RequestAborted));
        var vectorsCount = TryGetLong(collectionDoc.RootElement, "result", "points_count")
                           ?? TryGetLong(collectionDoc.RootElement, "result", "vectors_count");
        var segmentsCount = TryGetLong(collectionDoc.RootElement, "result", "segments_count");
        var collectionStatus = TryGetString(collectionDoc.RootElement, "result", "status");

        using var countReq = new StringContent("{\"exact\":false}", Encoding.UTF8, "application/json");
        using var countResp = await qdrant.PostAsync($"/collections/{rag.QdrantCollection}/points/count", countReq, ctx.RequestAborted);

        long? pointsCount = null;
        if (countResp.IsSuccessStatusCode)
        {
            using var countDoc = JsonDocument.Parse(await countResp.Content.ReadAsStringAsync(ctx.RequestAborted));
            pointsCount = TryGetLong(countDoc.RootElement, "result", "count");
        }

        return Results.Ok(new
        {
            ok = true,
            baseUrl = rag.QdrantBaseUrl,
            collection = rag.QdrantCollection,
            status = collectionStatus,
            httpStatus = (int)collectionResp.StatusCode,
            latencyMs = sw.ElapsedMilliseconds,
            vectorsCount,
            pointsCount,
            segmentsCount,
            environment = env.EnvironmentName
        });
    }

    private static long? TryGetLong(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(child, out var c)) return null;
        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n)) return n;
        return null;
    }

    private static string? TryGetString(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(child, out var c) || c.ValueKind != JsonValueKind.String) return null;
        return c.GetString();
    }

}
