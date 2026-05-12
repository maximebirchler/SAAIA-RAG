using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Net;

/// <summary>
/// Client TEI (Text Embeddings Inference) compatible OpenAI embeddings (/v1/embeddings).
/// IMPORTANT: dispose toujours les HttpResponseMessage pour éviter l'épuisement du pool de connexions.
/// </summary>
static class TeiClient
{
    internal enum EmbeddingInputKind
    {
        Query,
        Passage
    }

    internal sealed record RerankItem(int Index, double Score);
    private const int DimCacheMaxEntries = 32;

    private static readonly ConcurrentDictionary<string, int> _dimCache = new(StringComparer.OrdinalIgnoreCase);

    // TEI can be briefly unavailable during startup (model download / warmup) or under load.
    // We retry a few times on transient failures to avoid failing ingestion for a simple race.
    private static readonly TimeSpan[] _retryDelays = new[]
    {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
    };

    public static async Task<int> GetVectorDimAsync(HttpClient tei, string model, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model) && _dimCache.TryGetValue(model, out var cached) && cached > 0)
            return cached;

        var probeInput = FormatEmbeddingInput(model, "ping", EmbeddingInputKind.Query);
        var vecs = await EmbedAsync(tei, model, new[] { probeInput }, ct);
        var dim = (vecs.Length > 0) ? vecs[0].Length : 0;
        if (dim <= 0)
            throw new Exception("TEI returned empty embedding dimension");

        if (!string.IsNullOrWhiteSpace(model))
        {
            if (_dimCache.Count >= DimCacheMaxEntries && !_dimCache.ContainsKey(model))
                _dimCache.Clear();
            _dimCache[model] = dim;
        }

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

        // Build payload once (so retries are consistent)
        var payload = JsonSerializer.Serialize(body);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage? resp = null;
            try
            {
                resp = await tei.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await DelayWithJitterAsync(_retryDelays[attempt], ct);
                continue;
            }
            catch (TaskCanceledException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await DelayWithJitterAsync(_retryDelays[attempt], ct);
                continue;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    // Retry only on transient HTTP errors.
                    if (IsTransient(resp.StatusCode) && attempt < _retryDelays.Length && !ct.IsCancellationRequested)
                    {
                        await DelayWithJitterAsync(_retryDelays[attempt], ct);
                        continue;
                    }

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
        }
    }

    public static async Task<IReadOnlyList<RerankItem>> RerankAsync(
        HttpClient tei,
        string? model,
        string query,
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || texts.Count == 0)
            return Array.Empty<RerankItem>();

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["texts"] = texts
        };
        if (!string.IsNullOrWhiteSpace(model))
            body["model"] = model;

        var payload = JsonSerializer.Serialize(body);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/rerank");
            req.Content = new StringContent(payload, Encoding.UTF8);
            req.Content.Headers.ContentType = new("application/json");

            HttpResponseMessage? resp = null;
            try
            {
                resp = await tei.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await DelayWithJitterAsync(_retryDelays[attempt], ct);
                continue;
            }
            catch (TaskCanceledException) when (attempt < _retryDelays.Length && !ct.IsCancellationRequested)
            {
                await DelayWithJitterAsync(_retryDelays[attempt], ct);
                continue;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    if (IsTransient(resp.StatusCode) && attempt < _retryDelays.Length && !ct.IsCancellationRequested)
                    {
                        await DelayWithJitterAsync(_retryDelays[attempt], ct);
                        continue;
                    }

                    var err = await TryReadErrorBodyAsync(resp, ct);
                    throw new Exception($"TEI rerank failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return ParseRerankResponse(doc.RootElement);
            }
        }
    }

    internal static IReadOnlyList<RerankItem> ParseRerankResponse(JsonElement root)
    {
        JsonElement items = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("results", out var results))
                items = results;
            else if (root.TryGetProperty("data", out var data))
                items = data;
        }

        if (items.ValueKind != JsonValueKind.Array)
            throw new Exception("TEI rerank response missing array payload");

        var ranked = new List<RerankItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var index = item.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                ? indexEl.GetInt32()
                : -1;
            var score = item.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
                ? scoreEl.GetDouble()
                : 0.0;

            if (index >= 0)
                ranked.Add(new RerankItem(index, score));
        }

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .ToArray();
    }

    internal static string FormatEmbeddingInput(string? model, string? input, EmbeddingInputKind kind)
    {
        var text = input ?? string.Empty;
        if (!RequiresE5InstructionPrefix(model))
            return text;

        var expectedPrefix = kind == EmbeddingInputKind.Query ? "query:" : "passage:";
        var otherPrefix = kind == EmbeddingInputKind.Query ? "passage:" : "query:";
        if (text.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            return text;

        if (text.StartsWith(otherPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[otherPrefix.Length..].TrimStart();

        return $"{expectedPrefix} {text}";
    }

    internal static bool RequiresE5InstructionPrefix(string? model)
        => !string.IsNullOrWhiteSpace(model)
           && model.Contains("e5", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode code)
        => code is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static async Task DelayWithJitterAsync(TimeSpan delay, CancellationToken ct)
    {
        // Small jitter to avoid sync retries
        var jitterMs = Random.Shared.Next(50, 200);
        var total = delay + TimeSpan.FromMilliseconds(jitterMs);
        await Task.Delay(total, ct);
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
