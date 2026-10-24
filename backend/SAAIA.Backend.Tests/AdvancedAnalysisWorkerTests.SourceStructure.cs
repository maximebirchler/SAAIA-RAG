using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Fact]
    public async Task Canonical_resolver_preserves_ingested_section_locators_without_changing_proof_identity()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("section-locators", "Stage two\nHold below 17 bar.");
        var previous = Guid.NewGuid().ToString();
        var next = Guid.NewGuid().ToString();
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("""
                UPDATE retrieval_chunks SET metadata=jsonb_build_object(
                    'sectionTitle','Stage two','headingPath','Isolation procedure > Stage two',
                    'prevChunkId',@previous,'nextChunkId',@next)
                WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunk;
                """, new { previous, next, tenant = seed.TenantId, chunk = seed.ChunkId });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(BuildAdvancedOptions()));
        var result = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(result.IsValid);
        var item = Assert.Single(result.Evidence);
        Assert.Equal("Stage two", item.SourceStructure!.SectionTitle);
        Assert.Equal("Isolation procedure > Stage two", item.SourceStructure.HeadingPath);
        Assert.Equal(previous, item.SourceStructure.PreviousChunkId);
        Assert.Equal(next, item.SourceStructure.NextChunkId);
        Assert.Equal(seed.ChunkId.ToString(), item.Reference.ChunkId);
        Assert.Null(item.ExactTitle);
        Assert.DoesNotContain("Isolation procedure", item.Content);
        Assert.Equal("Stage two\nHold below 17 bar.", item.Content);
        var other = await resolver.ResolveAsync(Guid.NewGuid(),
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.False(other.IsValid);
    }
}
