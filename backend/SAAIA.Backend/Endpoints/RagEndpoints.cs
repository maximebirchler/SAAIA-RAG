using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Auth;
using Npgsql;
using Dapper;

namespace SAAIA.Backend.Endpoints;

public static class RagEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);
        app.MapPost("/rag/query", QueryAsync);
        app.MapGet("/rag/debug/scroll", ScrollAsync);
    }

    private static async Task<IResult> CategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

        const string sql = @"
SELECT DISTINCT category
FROM documents
WHERE tenant_id=@tenant_id AND status='indexed'
ORDER BY category;";

        var cats = (await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted)
        )).ToArray();

        return Results.Ok(cats);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagQueryRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var topK = req.TopK ?? rag.DefaultTopK;
        topK = Math.Clamp(topK, 1, rag.MaxTopK);

        var category = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(req.Query))
            return Results.BadRequest(new { error = "query is required" });

        var ct = ctx.RequestAborted;

        // Embed query (TEI)
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { req.Query }, ct);
        var qvec = emb[0];

        // Search Qdrant
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(category))
            filterMust.Add(new { key = "category", match = new { value = category } });

        var payload = new
        {
            vector = qvec,
            limit = topK,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await qdrant.PostAsync(url, content, ct);

        // auto-create collection si n'existe pas
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

            content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            resp = await qdrant.PostAsync(url, content, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            return Results.Problem($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var result = QdrantClient.ParseSearchResults(doc);

        return Results.Ok(new { query = req.Query, category, topK, matches = result });
    }

    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit)
    {
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var body = new
        {
            limit = Math.Clamp(limit ?? 20, 1, 200),
            with_payload = true,
            filter = new
            {
                must = new object[]
                {
                    new { key = "tenant_id", match = new { value = tenantId.ToString() } }
                }
            }
        };

        var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }
}

public sealed record RagQueryRequest(string Query, string? Category, int? TopK);
