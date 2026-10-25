using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("physical_page_context_unassigned_prefix")]
    [InlineData("physical_page_context_unassigned_suffix")]
    public async Task Canonical_resolver_preserves_physical_context_metadata_without_fabricating_heading_or_proof_identity(string composition)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");
        const string content = "Cycle : 20 minutes\nInspect the installed module and record the verified isolation state.";
        var seed = await database.SeedEvidenceAsync("physical-context-metadata", content);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("""
                UPDATE retrieval_chunks SET metadata=jsonb_build_object(
                    'chunkType','physical_page_context_v1','chunkComposition',@composition)
                WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunk;
                """, new { composition, tenant = seed.TenantId, chunk = seed.ChunkId });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(BuildAdvancedOptions()));

        var result = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);

        Assert.True(result.IsValid);
        var item = Assert.Single(result.Evidence);
        Assert.NotNull(item.SourceStructure);
        using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(item.SourceStructure,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal("physical_page_context_v1", metadata.RootElement.GetProperty("chunkType").GetString());
        Assert.Equal(composition, metadata.RootElement.GetProperty("chunkComposition").GetString());
        Assert.Null(item.SourceStructure.SectionTitle);
        Assert.Null(item.SourceStructure.HeadingPath);
        Assert.Null(item.ExactTitle);
        Assert.Equal(content, item.Content);
        Assert.Equal(seed.ChunkId.ToString(), item.Reference.ChunkId);
        Assert.False((await resolver.ResolveAsync(Guid.NewGuid(),
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None)).IsValid);
    }
}
