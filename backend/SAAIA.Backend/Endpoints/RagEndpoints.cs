using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class RagEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);

        // Nouveau endpoint "client"
        app.MapPost("/rag/search", SearchAsync);

        // Compat (dev / anciens scripts)
        app.MapPost("/rag/query", QueryAsync);

        app.MapGet("/rag/debug/scroll", ScrollAsync).RequireAdminKey();
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

    // =========================
    // /rag/query (compat legacy)
    // =========================
    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ragOpt.Value, httpFactory, req);

        // Format "historique" (backward compat)
        return Results.Ok(new
        {
            query = req.Query,
            category = resp.Category,
            topK = resp.TopK,
            matches = resp.Matches
        });
    }

    // =========================
    // /rag/search (client) — CDC v2.7
    // =========================
    private static async Task<IResult> SearchAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ragOpt.Value, httpFactory, req);

        // Convertir au format CDC v2.7 (items[] au lieu de matches[])
        var responseDto = new RagSearchResponseDto(
            RequestId: resp.RequestId,
            Query: resp.Query,
            QueryNormalized: resp.QueryNormalized,
            Category: resp.Category,
            TopK: resp.TopK,
            MinScore: resp.MinScore,
            Candidates: resp.Candidates,
            MaxPerDoc: resp.MaxPerDoc,
            MaxPerPage: resp.MaxPerPage,
            Metrics: new RagMetricsDto(
                TookMs: resp.Timings.TotalMs,
                Returned: resp.Matches.Count
            ),
            Items: resp.Matches
                .Select(m => new RagItemDto(
                    Score: m.Score,
                    DocId: m.DocId,
                    DocName: m.DocName ?? "Unknown",
                    DocPath: m.DocPath,
                    Category: resp.Category,
                    PageStart: m.PageStart,
                    PageEnd: m.PageEnd,
                    ChunkId: m.ChunkId,
                    ChunkIndex: m.ChunkIndex,
                    Text: m.Text ?? ""
                ))
                .ToList()
        );

        return Results.Ok(responseDto);
    }

    private static async Task<RagSearchResponse> SearchCoreAsync(
        HttpContext ctx,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = ctx.GetTenantId();

        if (string.IsNullOrWhiteSpace(req.Query))
            throw new BadHttpRequestException("query is required");

        var queryNorm = NormalizeQuery(req.Query);

        var topK = req.TopK ?? rag.DefaultTopK;
        topK = Math.Clamp(topK, 1, rag.MaxTopK);

        var category = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();

        // defaults selon mode
        var mode = (req.Mode ?? "balanced").Trim().ToLowerInvariant();
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * 12,
            _ => topK * 6
        };
        int defMaxPerDoc = mode switch
        {
            "focused" => topK, // pas de diversité imposée
            "broad" => 2,
            _ => Math.Max(2, topK / 2)
        };

        var minScore = req.MinScore ?? defMinScore;
        minScore = Math.Clamp(minScore, 0.0, 1.0);

        var candidates = req.Candidates ?? defCandidates;
        candidates = Math.Clamp(candidates, topK, Math.Max(topK, rag.MaxTopK * 20)); // max 400 si MaxTopK=20

        var maxPerDoc = req.MaxPerDoc ?? defMaxPerDoc;
        maxPerDoc = Math.Clamp(maxPerDoc, 1, topK);

        var maxPerPage = req.MaxPerPage ?? 1;
        maxPerPage = Math.Clamp(maxPerPage, 1, topK);

        // Diversity override (CDC v2.7 compat) — si diversity != null, override maxPerDoc et maxPerPage
        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
            if (req.Diversity.PreferDistinctPages == true)
                maxPerPage = 1;
        }

        var ct = ctx.RequestAborted;
        var swTotal = Stopwatch.StartNew();

        // Embed query (TEI)
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var swTei = Stopwatch.StartNew();
        var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { queryNorm }, ct);
        swTei.Stop();

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
            limit = candidates,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";

        HttpResponseMessage? resp = null;
        int qdrantStatus = 0;
        List<RagMatch> rawMatches;

        var swQ = Stopwatch.StartNew();
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))
                resp = await qdrant.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = null;

                await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

                using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                resp = await qdrant.PostAsync(url, content2, ct);
            }

            qdrantStatus = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            rawMatches = QdrantClient.ParseSearchResults(doc);
        }
        finally
        {
            swQ.Stop();
            resp?.Dispose();
        }

        // Post-filter: minScore + diversité (per doc / per page)
        var selected = new List<RagMatch>(capacity: topK);
        var perDoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in rawMatches)
        {
            if (m.Score < minScore) continue;
            if (string.IsNullOrWhiteSpace(m.DocPath)) continue;

            var docKey = m.DocPath!;
            perDoc.TryGetValue(docKey, out var docCount);
            if (docCount >= maxPerDoc) continue;

            var pageKey = $"{docKey}:{m.PageStart ?? -1}:{m.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCount);
            if (pageCount >= maxPerPage) continue;

            selected.Add(m);
            perDoc[docKey] = docCount + 1;
            perPage[pageKey] = pageCount + 1;

            if (selected.Count >= topK) break;
        }

        swTotal.Stop();

        return new RagSearchResponse(
            RequestId: ctx.GetRequestId(),
            Query: req.Query,
            QueryNormalized: queryNorm,
            Category: category,
            TopK: topK,
            MinScore: minScore,
            Candidates: candidates,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            QdrantStatus: qdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: swTotal.ElapsedMilliseconds,
                TeiMs: swTei.ElapsedMilliseconds,
                QdrantMs: swQ.ElapsedMilliseconds
            ),
            Matches: selected
        );
    }

    private static string NormalizeQuery(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;

        // collapse whitespace
        var sb = new StringBuilder(s.Length);
        bool inWs = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWs)
                {
                    sb.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWs = false;
            }
        }
        return sb.ToString();
    }

    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit,
        string? docPath)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        docPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(docPath))
            must.Add(new { key = "doc_path", match = new { value = docPath } });

        var body = new
        {
            limit = Math.Clamp(limit ?? 20, 1, 200),
            with_payload = true,
            filter = new
            {
                must = must.ToArray()
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            content,
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }
}

public sealed record RagQueryRequest(string Query, string? Category, int? TopK);

public sealed record RagSearchRequest(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null
);

public sealed record RagSearchTimings(long TotalMs, long TeiMs, long QdrantMs);

public sealed record RagSearchResponse(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    int QdrantStatus,
    RagSearchTimings Timings,
    IReadOnlyList<RagMatch> Matches
);
