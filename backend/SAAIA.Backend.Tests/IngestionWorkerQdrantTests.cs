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
}
