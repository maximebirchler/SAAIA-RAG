using System.Net;
using System.Text;
using System.Text.Json;
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

        // ---- 1) Embed query (TEI) ----
        var tei = _http.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var emb = await global::TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { query }, ct);
        var qvec = emb[0];

        // ---- 2) Qdrant search ----
        var qdrant = _http.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };

        if (!string.IsNullOrWhiteSpace(category))
            must.Add(new { key = "category", match = new { value = category.Trim().ToLowerInvariant() } });

        var payload = new
        {
            vector = qvec,
            limit = topK,
            with_payload = true,
            filter = new { must }
        };

        var col = rag.QdrantCollection;

        async Task<HttpResponseMessage> PostSearchAsync()
        {
            var json = JsonSerializer.Serialize(payload);
            return await qdrant.PostAsync(
                $"/collections/{col}/points/search",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);
        }

        HttpResponseMessage? resp = null;
        try
        {
            resp = await PostSearchAsync();

            // Auto-create si la collection n'existe pas
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = null;

                await global::QdrantClient.EnsureCollectionAsync(qdrant, col, qvec.Length, ct);
                resp = await PostSearchAsync();
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return new List<RagHit>();

            var hits = new List<RagHit>();

            foreach (var item in result.EnumerateArray())
            {
                var score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;

                if (!item.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
                {
                    hits.Add(new RagHit(score, null, null, null, null, null, null));
                    continue;
                }

                string? docPath = p.TryGetProperty("doc_path", out var dp) ? dp.GetString() : null;
                string? docName = p.TryGetProperty("doc_name", out var dn) ? dn.GetString() : null;

                int? pageStart = p.TryGetProperty("page_start", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : null;
                int? pageEnd   = p.TryGetProperty("page_end", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : null;
                int? chunkIndex= p.TryGetProperty("chunk_index", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;

                string? text = p.TryGetProperty("text", out var t) ? t.GetString() : null;

                hits.Add(new RagHit(score, docPath, docName, pageStart, pageEnd, chunkIndex, text));
            }

            return hits;
        }
        finally
        {
            resp?.Dispose();
        }
    }
}
