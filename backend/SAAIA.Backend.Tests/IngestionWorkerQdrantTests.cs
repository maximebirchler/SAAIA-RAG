using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionWorkerQdrantTests
{
    [Theory]
    [InlineData(0, null, 5, true)]
    [InlineData(0, 4, 5, true)]
    [InlineData(0, 5, 5, false)]
    [InlineData(16, 4, 5, false)]
    [InlineData(16, 5, 5, false)]
    [InlineData(0, 0, 0, false)]
    public void ShouldPurgeQdrantVersionBeforeFullEmbedding_only_for_unpublished_full_restarts(
        int resumeFromChunk,
        int? indexedVersion,
        int jobVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            IngestionWorker.ShouldPurgeQdrantVersionBeforeFullEmbedding(resumeFromChunk, indexedVersion, jobVersion));
    }

    [Fact]
    public void Resume_checkpoint_requires_same_embedding_model_and_format()
    {
        var checkpoint = new JobRepo.ResumeCheckpointState
        {
            ProgressCurrent = 16,
            ProgressTotal = 32,
            SourceHash = "abc123",
            FileSize = 1234,
            ChunkTotal = 32,
            EmbeddingModel = "intfloat/multilingual-e5-base",
            EmbeddingInputFormat = "e5_passage_v1"
        };

        Assert.True(IngestionWorker.IsResumeCheckpointCompatible(
            checkpoint,
            "ABC123",
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1"));

        Assert.False(IngestionWorker.IsResumeCheckpointCompatible(
            checkpoint,
            "abc123",
            32,
            "sentence-transformers/all-MiniLM-L6-v2",
            "raw_passage_v1"));

        checkpoint.EmbeddingInputFormat = null;
        Assert.False(IngestionWorker.IsResumeCheckpointCompatible(
            checkpoint,
            "abc123",
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1"));
    }

    [Theory]
    [InlineData("intfloat/multilingual-e5-base", "e5_passage_v1")]
    [InlineData("sentence-transformers/all-MiniLM-L6-v2", "raw_passage_v1")]
    [InlineData("", "raw_passage_v1")]
    public void ResolveEmbeddingInputFormat_marks_e5_passage_format(string model, string expected)
    {
        Assert.Equal(expected, IngestionWorker.ResolveEmbeddingInputFormat(model));
    }

    [Theory]
    [InlineData("Qdrant upsert failed: 500 Internal Server Error {\"status\":{\"error\":\"Service internal error: RocksDB put_cf error: IO error: Too many open files\"}}", true)]
    [InlineData("Qdrant get collection failed: 503 Service Unavailable maintenance", true)]
    [InlineData("Qdrant upsert failed: 400 Bad Request bad vector dimension", false)]
    [InlineData("PDF extraction failed: unsupported encryption", false)]
    public void ShouldDeferTransientInfrastructureFailure_only_defers_recoverable_dependency_errors(
        string message,
        bool expected)
    {
        Assert.Equal(expected, IngestionWorker.ShouldDeferTransientInfrastructureFailure(new Exception(message)));
    }

    [Fact]
    public void ComputeTransientInfrastructureDeferralDelay_waits_longer_after_qdrant_storage_pressure()
    {
        var storagePressureDelay = IngestionWorker.ComputeTransientInfrastructureDeferralDelay(
            new Exception("Qdrant upsert failed: 500 Internal Server Error RocksDB Too many open files"));
        var genericGatewayDelay = IngestionWorker.ComputeTransientInfrastructureDeferralDelay(
            new Exception("Qdrant upsert failed: 503 Service Unavailable"));

        Assert.True(storagePressureDelay > genericGatewayDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), storagePressureDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), genericGatewayDelay);
    }

    [Fact]
    public void ShouldEmbedRetrievalChunk_skips_navigation_and_review_only_chunks()
    {
        var content = new ProjectedRetrievalChunk(
            0,
            0,
            0,
            1,
            1,
            "Reliable content with enough words to embed as a semantic passage.",
            11,
            [1],
            "unit_exact_v1",
            ExtractionTextStatus: "ok");
        var navigation = content with
        {
            ChunkType = RetrievalContentClassifier.NavigationChunkType,
            ContentRole = RetrievalContentClassifier.NavigationRole
        };
        var sparse = content with
        {
            TokenCount = 5,
            ExtractionTextStatus = "low_text",
            ExtractionTextSparse = true,
            ExtractionOcrCandidate = true
        };
        var replacementChars = content with
        {
            ExtractionQualitySignals = ["replacement_chars_remaining"]
        };
        var ocrNoise = content with
        {
            Text = string.Join(
                ' ',
                Enumerable.Repeat(
                    "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A 0 OQ =| Zz UO | W = cr O = 0",
                    3)),
            TokenCount = 120
        };
        var lowSubstance = content with
        {
            Text = "31",
            TokenCount = 1
        };
        var shortOcrLayoutFragment = content with
        {
            Text = "S 8 N ol 1 A % 1 3 Bild 1.11. Anwendung:",
            TokenCount = 12,
            ContentDensityScore = 0.35
        };
        var shortUsefulHeading = content with
        {
            Text = "Seite 6 zu DVS 2205 Bild 3.3. Anwendung Behälter",
            TokenCount = 9,
            ContentDensityScore = 0.35
        };
        var balancedMixedNavigationContent = content with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.62,
            ContentDensityScore = 0.58
        };
        var navigationDominantMixedContent = content with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.72,
            ContentDensityScore = 0.45
        };

        Assert.True(IngestionWorker.ShouldEmbedRetrievalChunk(content));
        Assert.True(IngestionWorker.ShouldEmbedRetrievalChunk(balancedMixedNavigationContent));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(navigation));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(navigationDominantMixedContent));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(sparse));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(replacementChars));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(ocrNoise));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(lowSubstance));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(shortOcrLayoutFragment));
        Assert.True(IngestionWorker.ShouldEmbedRetrievalChunk(shortUsefulHeading));
        Assert.Equal("low_substance", IngestionWorker.ResolveRetrievalChunkEmbeddingRejectionReason(lowSubstance));
        Assert.Equal("low_substance", IngestionWorker.ResolveRetrievalChunkEmbeddingRejectionReason(shortOcrLayoutFragment));
    }

    [Fact]
    public void BuildRetrievalChunkQualitySummary_counts_searchable_and_rejected_chunks()
    {
        var content = new ProjectedRetrievalChunk(
            0,
            0,
            0,
            1,
            1,
            "Reliable content with enough words to embed as a semantic passage.",
            11,
            [1],
            "unit_exact_v1",
            ExtractionTextStatus: "ok");
        var navigation = content with
        {
            ChunkIndex = 1,
            ChunkType = RetrievalContentClassifier.NavigationChunkType,
            ContentRole = RetrievalContentClassifier.NavigationRole
        };
        var sparse = content with
        {
            ChunkIndex = 2,
            TokenCount = 5,
            ExtractionTextSparse = true
        };
        var replacementChars = content with
        {
            ChunkIndex = 3,
            ExtractionQualitySignals = ["replacement_chars_remaining"]
        };
        var empty = content with
        {
            ChunkIndex = 4,
            ExtractionTextStatus = "empty_text"
        };
        var ocrNoise = content with
        {
            ChunkIndex = 5,
            Text = string.Join(
                ' ',
                Enumerable.Repeat(
                    "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A 0 OQ =| Zz UO | W = cr O = 0",
                    3)),
            TokenCount = 120
        };
        var lowSubstance = content with
        {
            ChunkIndex = 6,
            Text = "31",
            TokenCount = 1
        };

        var summary = IngestionWorker.BuildRetrievalChunkQualitySummary(
            [content, navigation, sparse, replacementChars, empty, ocrNoise, lowSubstance]);

        Assert.Equal(7, summary.TotalChunkCount);
        Assert.Equal(1, summary.SearchableChunkCount);
        Assert.Equal(6, summary.RejectedChunkCount);
        Assert.Equal(1, summary.NavigationChunkCount);
        Assert.Equal(1, summary.SparseRejectedChunkCount);
        Assert.Equal(1, summary.ReplacementCharRejectedChunkCount);
        Assert.Equal(1, summary.EmptyTextRejectedChunkCount);
        Assert.Equal(1, summary.OcrNoiseRejectedChunkCount);
        Assert.Equal(1, summary.OtherRejectedChunkCount);
        Assert.False(summary.ManualReviewRecommended);

        var rejectedOnly = IngestionWorker.BuildRetrievalChunkQualitySummary(
            [navigation, sparse, replacementChars, empty, ocrNoise]);
        Assert.Equal(0, rejectedOnly.SearchableChunkCount);
        Assert.True(rejectedOnly.ManualReviewRecommended);
    }

    [Fact]
    public void BuildChunkLinkMap_links_next_chunk_in_same_section_without_rescanning()
    {
        var docId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        const int ingestionVersion = 7;
        var chunks = new[]
        {
            Chunk(10, sectionOrdinal: 1),
            Chunk(20, sectionOrdinal: 2),
            Chunk(30, sectionOrdinal: 1),
            Chunk(40, sectionOrdinal: 1),
            Chunk(50, sectionOrdinal: null)
        };

        var map = IngestionWorker.BuildChunkLinkMap(docId, ingestionVersion, chunks);

        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, 30),
            map[10].SameSectionChunkId);
        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, 40),
            map[30].SameSectionChunkId);
        Assert.Null(map[40].SameSectionChunkId);
        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, 20),
            map[10].NextChunkId);
        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, 40),
            map[50].PreviousChunkId);
    }

    private static ProjectedRetrievalChunk Chunk(int chunkIndex, int? sectionOrdinal)
        => new(
            chunkIndex,
            sectionOrdinal,
            UnitOrdinal: chunkIndex,
            PageStart: 1,
            PageEnd: 1,
            Text: $"Reliable content chunk {chunkIndex} with enough words to embed as a semantic passage.",
            TokenCount: 11,
            Checksum: [1],
            ChunkType: "unit_exact_v1",
            ExtractionTextStatus: "ok");
}
