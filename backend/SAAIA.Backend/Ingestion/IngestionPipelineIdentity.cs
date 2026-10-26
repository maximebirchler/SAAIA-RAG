using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Persisted with the published generation, so a source unchanged on disk can still require reprocessing.</summary>
internal static class IngestionPipelineIdentity
{
    public static string Compute(RagOptions rag, IngestionOptions ingestion, DocumentIntelligenceOptions documentIntelligence)
    {
        var profile = new
        {
            schema = "ingestion-pipeline-identity.v1",
            code = typeof(IngestionPipelineIdentity).Module.ModuleVersionId,
            ingestion.ChunkMaxWords, ingestion.ChunkMinWords, ingestion.ChunkOverlapWords, ingestion.CanonicalArtifactsEnabled,
            ingestion.OcrEnabled, ingestion.OcrCommand, ingestion.OcrArguments, ingestion.OcrForceArguments,
            ingestion.OcrLanguages, ingestion.OcrAutoFallbackLanguages, ingestion.OcrMaxLanguages, ingestion.OcrAutoDetectLanguages,
            ingestion.OcrMinWords, ingestion.OcrImagePageEnabled, ingestion.OcrImageRendererCommand,
            ingestion.OcrImageTextCommand, ingestion.OcrImagePageMaxPages, ingestion.OcrImagePageRenderDpi,
            ingestion.OcrImagePageSegmentationMode, ingestion.OcrImagePageMinWords, ingestion.OcrImagePageMaxTotalSeconds,
            ingestion.OcrTimeoutSeconds, ingestion.OcrImagePageTimeoutSeconds,
            documentIntelligence,
            target = rag.QdrantBaseUrl.TrimEnd('/'), rag.QdrantCollection, rag.QdrantRuntimeRevision,
            embeddingTarget = rag.EmbeddingsBaseUrl.TrimEnd('/'), rag.EmbeddingsModel,
            rag.EmbeddingsModelRevision, rag.EmbeddingsRuntimeRevision
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile))).ToLowerInvariant();
    }
}
