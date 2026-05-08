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
}
