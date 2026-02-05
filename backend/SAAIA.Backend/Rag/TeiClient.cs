using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

/// <summary>
/// Client TEI (Text Embeddings Inference) compatible OpenAI embeddings (/v1/embeddings).
/// IMPORTANT: dispose toujours les HttpResponseMessage pour éviter l'épuisement du pool de connexions.
/// </summary>
static class TeiClient
{
    private static readonly ConcurrentDictionary<string, int> _dimCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<int> GetVectorDimAsync(HttpClient tei, string model, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model) && _dimCache.TryGetValue(model, out var cached) && cached > 0)
            return cached;

        var vecs = await EmbedAsync(tei, model, new[] { "ping" }, ct);
        var dim = (vecs.Length > 0) ? vecs[0].Length : 0;
        if (dim <= 0)
            throw new Exception("TEI returned empty embedding dimension");

        if (!string.IsNullOrWhiteSpace(model))
            _dimCache[model] = dim;

        return dim;
    }

    public static async Task<float[][]> EmbedAsync(HttpClient tei, string model, string[] inputs, CancellationToken ct)
    {
        inputs ??= Array.Empty<string>();
        if (inputs.Length == 0) return Array.Empty<float[]>();

        var body = new
        {
            model,
            input = inputs,
            encoding_format = "float"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var resp = await tei.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await TryReadErrorBodyAsync(resp, ct);
            throw new Exception($"TEI embeddings failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("TEI response missing 'data' array");

        var list = new List<float[]>(capacity: data.GetArrayLength());

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
                throw new Exception("TEI response item missing 'embedding' array");

            var v = new float[emb.GetArrayLength()];
            var i = 0;
            foreach (var n in emb.EnumerateArray())
                v[i++] = n.ValueKind == JsonValueKind.Number ? n.GetSingle() : 0f;

            list.Add(v);
        }

        if (list.Count != inputs.Length)
            throw new Exception($"TEI embeddings count mismatch: got {list.Count}, expected {inputs.Length}");

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
