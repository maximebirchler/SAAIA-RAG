using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Options;

namespace SAAIA.Backend.Chat;

public sealed record RagHit(
    double Score,
    string? DocPath,
    string? DocName,
    int? PageStart,
    int? PageEnd,
    int? ChunkIndex,
    string? Text
);

internal sealed class RagRetriever
{
    private readonly IHttpClientFactory _http;
    private readonly IOptions<global::RagOptions> _ragOpt;

    public RagRetriever(IHttpClientFactory http, IOptions<global::RagOptions> ragOpt)
    {
        _http = http;
        _ragOpt = ragOpt;
    }

    public async Task<List<RagHit>> RetrieveAsync(Guid tenantId, string query, string? category, int topK, CancellationToken ct)
    {
        var rag = _ragOpt.Value;

        // Embed query (TEI)
        var tei = _http.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var emb = await global::TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { query }, ct);
        var qvec = emb[0];

        // Search Qdrant
        var qdrant = _http.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };

        if (!string.IsNullOrWhiteSpace(category))
            filterMust.Add(new { key = "category", match = new { value = category.Trim() } });

        var body = new
        {
            vector = qvec,
            limit = topK,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage resp = await qdrant.PostAsync(url, content, ct);

        // ✅ auto-create si la collection n'existe pas
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();

            // collec + dim = qvec.Length
            await global::QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

            content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            resp = await qdrant.PostAsync(url, content, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}");
        }


        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return new List<RagHit>();

        var hits = new List<RagHit>();
        foreach (var item in result.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;

            if (!item.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                hits.Add(new RagHit(score, null, null, null, null, null, null));
                continue;
            }

            string? docPath = payload.TryGetProperty("doc_path", out var dp) ? dp.GetString() : null;
            string? docName = payload.TryGetProperty("doc_name", out var dn) ? dn.GetString() : null;

            int? pageStart = payload.TryGetProperty("page_start", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : null;
            int? pageEnd = payload.TryGetProperty("page_end", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : null;
            int? chunkIndex = payload.TryGetProperty("chunk_index", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;

            string? text = payload.TryGetProperty("text", out var t) ? t.GetString() : null;

            hits.Add(new RagHit(score, docPath, docName, pageStart, pageEnd, chunkIndex, text));
        }

        return hits;
    }
}
