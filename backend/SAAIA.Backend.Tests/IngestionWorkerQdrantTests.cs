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

        Assert.True(IngestionWorker.ShouldEmbedRetrievalChunk(content));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(navigation));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(sparse));
        Assert.False(IngestionWorker.ShouldEmbedRetrievalChunk(replacementChars));
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
