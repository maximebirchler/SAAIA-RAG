using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LegacyIngestionStageManifestFactoryTests
{
    [Fact]
    public void Stage_manifest_records_immutable_model_and_runtime_revisions()
    {
        const string text = "fixture text";
        var page = new ExtractedPdfPage(1, text, 2, text.Length, [1]);
        var extraction = new PdfExtractionResult(
            [],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));
        var ingestion = new IngestionOptions
        {
            ChunkMaxWords = 220,
            ChunkOverlapWords = 0,
            ChunkMinWords = 25,
            EmbeddingsBatchSize = 16
        };
        var rag = new RagOptions
        {
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EmbeddingsModelRevision = "d128750597153bb5987e10b1c3493a34e5a4502a",
            EmbeddingsRuntimeRevision = "sha256:tei",
            QdrantRuntimeRevision = "sha256:qdrant"
        };

        var stages = LegacyIngestionStageManifestFactory.Create(
            ingestion,
            rag,
            extraction,
            ocrAttempted: false,
            ocrApplied: false,
            ocrLanguages: null,
            ocrDiagnostics: null,
            new(1, 0, 2, 3, 4, 5, 6),
            codeRevision: "head-source-hash");

        var embedding = Assert.Single(stages, stage => stage.StageType == "embedding");
        Assert.Equal(rag.EmbeddingsModel, embedding.ModelId);
        Assert.Equal(rag.EmbeddingsModelRevision, embedding.ModelRevision);
        Assert.Equal(rag.EmbeddingsRuntimeRevision, embedding.EngineVersion);
        Assert.All(stages, stage => Assert.Equal(64, stage.OptionsSha256.Length));
    }
}
