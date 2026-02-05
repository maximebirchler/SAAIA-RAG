using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Client HTTP minimal pour Qdrant.
/// IMPORTANT:
/// - Dispose toujours les HttpResponseMessage (sinon épuisement du pool de connexions)
/// - Dispose aussi les HttpContent (sinon pression mémoire inutile)
/// - Gère le race: 2 threads peuvent créer la collection en même temps (409 Conflict).
/// </summary>
static class QdrantClient
{
    public static async Task EnsureCollectionAsync(HttpClient qdrant, string collection, int vectorSize, CancellationToken ct)
    {
        using var get = await qdrant.GetAsync($"/collections/{collection}", ct);
        if (get.IsSuccessStatusCode) return;

        if (get.StatusCode != HttpStatusCode.NotFound)
        {
            var err = await TryReadErrorBodyAsync(get, ct);
            throw new Exception($"Qdrant get collection failed: {(int)get.StatusCode} {get.ReasonPhrase} {err}".Trim());
        }

        var body = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = "Cosine"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var put = await qdrant.PutAsync($"/collections/{collection}", content, ct);

        // Race condition: si une autre requête a créé la collection entre temps,
        // Qdrant peut répondre 409 Conflict -> on considère OK.
        if (put.StatusCode == HttpStatusCode.Conflict) return;

        if (!put.IsSuccessStatusCode)
        {
            var err = await TryReadErrorBodyAsync(put, ct);
            throw new Exception($"Qdrant create collection failed: {(int)put.StatusCode} {put.ReasonPhrase} {err}".Trim());
        }
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

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await qdrant.PostAsync(
            $"/collections/{collection}/points/delete?wait=true",
            content,
            ct);

        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await TryReadErrorBodyAsync(resp, ct);
            throw new Exception($"Qdrant delete failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
        }
    }

    public static async Task UpsertPointsAsync(HttpClient qdrant, string collection, List<object> points, CancellationToken ct)
    {
        var body = new { points };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await qdrant.PutAsync(
            $"/collections/{collection}/points?wait=true",
            content,
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await TryReadErrorBodyAsync(resp, ct);
            throw new Exception($"Qdrant upsert failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
        }
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

    private static async Task<string> TryReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 2000 ? s : s[..2000];
        }
        catch
        {
            return string.Empty;
        }
    }
}
