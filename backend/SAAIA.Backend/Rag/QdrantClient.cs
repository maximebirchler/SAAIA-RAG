using System.Net;
using System.Text;
using System.Text.Json;

static class QdrantClient
{
    public static async Task EnsureCollectionAsync(HttpClient qdrant, string collection, int vectorSize, CancellationToken ct)
    {
        var get = await qdrant.GetAsync($"/collections/{collection}", ct);
        if (get.IsSuccessStatusCode) return;

        if (get.StatusCode != HttpStatusCode.NotFound)
            return; // avoid failing startup hard

        var body = new { vectors = new { size = vectorSize, distance = "Cosine" } };

        var put = await qdrant.PutAsync($"/collections/{collection}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!put.IsSuccessStatusCode)
            throw new Exception($"Create collection failed: {(int)put.StatusCode} {put.ReasonPhrase}");
    }

    public static async Task DeleteByDocAsync(HttpClient qdrant, string collection, Guid tenantId, Guid docId, CancellationToken ct)
    {
        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } }
            }
        };
        var body = new { filter };

        var resp = await qdrant.PostAsync($"/collections/{collection}/points/delete?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant delete failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    public static async Task UpsertPointsAsync(HttpClient qdrant, string collection, List<object> points, CancellationToken ct)
    {
        var body = new { points };
        var resp = await qdrant.PutAsync($"/collections/{collection}/points?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant upsert failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    public static object[] ParseSearchResults(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        var list = new List<object>();
        foreach (var item in result.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            var payload = item.TryGetProperty("payload", out var p) ? p : default;

            string? docPath = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("doc_path", out var dp) ? dp.GetString() : null;
            int? pageStart = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page_start", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : null;
            int? pageEnd = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page_end", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : null;
            int? chunkIndex = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("chunk_index", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
            string? text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var t) ? t.GetString() : null;

            list.Add(new { score, docPath, pageStart, pageEnd, chunkIndex, text });
        }
        return list.ToArray();
    }
}
