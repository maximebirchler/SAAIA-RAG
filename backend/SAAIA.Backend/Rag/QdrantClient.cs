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

    public static async Task DeleteOtherVersionsByDocAsync(HttpClient qdrant, string collection, Guid tenantId, Guid docId, int keepVersion, CancellationToken ct)
    {
        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } }
            },
            must_not = new object[]
            {
                new { key = "ingestion_version", match = new { value = keepVersion } }
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
            throw new Exception($"Qdrant delete stale versions failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
        }
    }

    public static async Task DeleteVersionByDocAsync(HttpClient qdrant, string collection, Guid tenantId, Guid docId, int version, CancellationToken ct)
    {
        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } },
                new { key = "ingestion_version", match = new { value = version } }
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
            throw new Exception($"Qdrant delete version failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {err}".Trim());
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

    public static List<RagMatch> ParseSearchResults(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return new List<RagMatch>();

        var list = new List<RagMatch>(result.GetArrayLength());
        foreach (var item in result.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            var payload = item.TryGetProperty("payload", out var p) ? p : default;

            string? GetStr(string k)
                => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(k, out var v) ? v.GetString() : null;

            int? GetInt(string k)
                => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

            double? GetDouble(string k)
                => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

            var m = new RagMatch(
                Score: score,
                DocId: GetStr("doc_id"),
                DocPath: GetStr("doc_path"),
                DocName: GetStr("doc_name"),
                PageStart: GetInt("page_start"),
                PageEnd: GetInt("page_end"),
                ChunkId: GetStr("chunk_id"),
                ChunkIndex: GetInt("chunk_index"),
                Text: GetStr("text"),
                IngestionVersion: GetInt("ingestion_version"),
                HashDoc: GetStr("hash_doc"),
                EmbedText: GetStr("embed_text"),
                EmbeddingBasis: GetStr("embedding_basis"),
                EmbeddingModel: GetStr("embedding_model"),
                EmbeddingInputFormat: GetStr("embedding_input_format"),
                SectionOrdinal: GetInt("section_ordinal"),
                UnitOrdinal: GetInt("unit_ordinal"),
                SectionTitle: GetStr("section_title"),
                HeadingPath: GetStr("heading_path"),
                ChunkType: GetStr("chunk_type"),
                PrevChunkId: GetStr("prev_chunk_id"),
                NextChunkId: GetStr("next_chunk_id"),
                SameSectionChunkId: GetStr("same_section_chunk_id"),
                OffsetStart: GetInt("offset_start"),
                OffsetEnd: GetInt("offset_end"),
                Category: GetStr("category"),
                ContentRole: GetStr("content_role"),
                NavigationReason: GetStr("navigation_reason"),
                OriginalChunkType: GetStr("original_chunk_type"),
                NavigationScore: GetDouble("navigation_score"),
                ContentDensityScore: GetDouble("content_density_score")
            );

            list.Add(m);
        }

        return list;
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

public sealed record RagMatchedContentCard(
    string Title,
    string? ContentCardId = null,
    int? PageStart = null,
    int? PageEnd = null,
    string? Kind = null,
    IReadOnlyList<string>? Signals = null,
    JsonElement? Evidence = null
);

public sealed record RagMatch(
    double Score,
    string? DocId,
    string? DocPath,
    string? DocName,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    int? ChunkIndex,
    string? Text,
    int? IngestionVersion,
    string? HashDoc,
    string? EmbedText,
    string? EmbeddingBasis,
    int? SectionOrdinal,
    int? UnitOrdinal,
    string? SectionTitle,
    string? HeadingPath,
    string? ChunkType,
    string? PrevChunkId,
    string? NextChunkId,
    string? SameSectionChunkId,
    double? RerankScore = null,
    int? OffsetStart = null,
    int? OffsetEnd = null,
    IReadOnlyList<RagMatchedContentCard>? MatchedContentCards = null,
    string? Category = null,
    string? ContentRole = null,
    string? NavigationReason = null,
    string? OriginalChunkType = null,
    double? NavigationScore = null,
    double? ContentDensityScore = null,
    string? EmbeddingModel = null,
    string? EmbeddingInputFormat = null
);
