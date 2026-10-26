using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed record IngestionVectorIdentity(Guid TenantId, Guid DocumentId, string DocumentPath, string Category, int Version = 0);
internal sealed record IngestionVectorPoint(Guid PointId, Guid ChunkId);

internal static class IngestionVectorPlan
{
    public const string Schema = "ingestion-vector-plan.v1";

    public static string Fingerprint(string sourceHash, RagOptions rag, int dimensions,
        string inputFormat, IReadOnlyList<object> inputs, IngestionVectorIdentity? identity = null)
    {
        var manifest = new
        {
            schema = Schema,
            sourceHash,
            identity,
            dimensions,
            // Hash the complete target identity; never log URLs or input text.
            target = rag.QdrantBaseUrl.TrimEnd('/'),
            collection = rag.QdrantCollection,
            qdrantRevision = rag.QdrantRuntimeRevision,
            embeddingTarget = rag.EmbeddingsBaseUrl.TrimEnd('/'),
            model = rag.EmbeddingsModel,
            modelRevision = rag.EmbeddingsModelRevision,
            runtimeRevision = rag.EmbeddingsRuntimeRevision,
            inputFormat,
            code = typeof(IngestionVectorPlan).Module.ModuleVersionId,
            inputs
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))).ToLowerInvariant();
    }

    internal static Guid PointId(Guid canonicalChunkId, string planFingerprint)
        => IdUtil.DeterministicGuid($"qdrant|{canonicalChunkId:N}|{planFingerprint}");

    public static bool CanResume(JobRepo.ResumeCheckpointState? checkpoint, string sourceHash,
        int chunkTotal, string? model, string inputFormat, string planFingerprint)
        => checkpoint is { ProgressCurrent: > 0 }
            && checkpoint.ProgressCurrent <= chunkTotal
            && checkpoint.ProgressTotal == chunkTotal
            && checkpoint.ChunkTotal == chunkTotal
            && string.Equals(checkpoint.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(checkpoint.EmbeddingModel?.Trim(), model?.Trim(), StringComparison.Ordinal)
            && string.Equals(checkpoint.EmbeddingInputFormat, inputFormat, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(planFingerprint)
            && string.Equals(checkpoint.VectorPlanFingerprint, planFingerprint, StringComparison.Ordinal);

    public static async Task<bool> HasCompletePointsAsync(HttpClient qdrant, string collection,
        IReadOnlyList<IngestionVectorPoint> points, string planFingerprint, CancellationToken ct)
    {
        foreach (var batch in points.Chunk(128))
        {
            var expected = batch.ToDictionary(point => point.PointId, point => point.ChunkId);
            using var response = await qdrant.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(collection)}/points",
                new { ids = expected.Keys, with_payload = new[] { "ingestion_plan_fingerprint", "chunk_id" }, with_vector = false }, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("result", out var returned) || returned.ValueKind != JsonValueKind.Array)
                return false;
            var found = new HashSet<Guid>();
            foreach (var point in returned.EnumerateArray())
            {
                if (!point.TryGetProperty("id", out var idElement) || !Guid.TryParse(idElement.GetString(), out var id)
                    || !expected.TryGetValue(id, out var canonicalChunkId) || !found.Add(id)
                    || !point.TryGetProperty("payload", out var payload)
                    || !payload.TryGetProperty("chunk_id", out var chunkId) || chunkId.GetString() != canonicalChunkId.ToString()
                    || !payload.TryGetProperty("ingestion_plan_fingerprint", out var fingerprint)
                    || fingerprint.GetString() != planFingerprint)
                    return false;
            }
            if (found.Count != batch.Length) return false;
        }
        return true;
    }
}
