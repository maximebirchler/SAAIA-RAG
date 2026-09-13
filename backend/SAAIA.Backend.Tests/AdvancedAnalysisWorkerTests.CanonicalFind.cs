using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Fact]
    public async Task Canonical_find_locates_literal_text_paginates_and_excludes_other_documents_and_tenants()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("literal-source", "ISO %_\n17\nHold below 17 bar.");
        await database.SeedAdditionalEvidenceAsync(seed, "other-document", "ISO %_ 17\nUnrelated content.");
        await database.SeedEvidenceAsync("other-tenant", "ISO %_ 17\nOther tenant content.");
        var secondId = Guid.NewGuid();
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,
                  page_start,page_end,text_content,token_count)
                VALUES(@chunk,@tenant,@revision,1,3,3,'ISO %_ 17: inspect the seal.',8);
                INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,
                  source_size,ingestion_version,indexed_version)
                SELECT @other_revision,tenant_id,doc_id,doc_path,source_hash,source_size,2,2
                FROM document_revisions WHERE revision_id=@revision;
                INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,
                  page_start,page_end,text_content,token_count)
                VALUES(@other_chunk,@tenant,@other_revision,0,2,2,'ISO %_ 17: another revision.',8);
                """, new { chunk = secondId, tenant = seed.TenantId, revision = seed.RevisionId,
                    other_revision = Guid.NewGuid(), other_chunk = Guid.NewGuid() });
        }
        var settings = BuildAdvancedOptions();
        var options = Options.Create(settings);
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, options);
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { DocId = seed.DocId.ToString(), ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);
        var request = new AdvancedAnalysisSearchRequest("iso %_ 17", TopK: 1,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(), Operation: "find_source_text");
        var first = await gateway.SearchAsync(request, CancellationToken.None);
        Assert.Equal(seed.ChunkId.ToString(), Assert.Single(first.Evidence).Reference.ChunkId);
        Assert.Equal("canonical_text_matches_more_available", first.FindDiagnostic!.Status);
        Assert.Equal(1, first.FindDiagnostic.NextOffset);
        var second = await gateway.SearchAsync(request with { Offset = 1 }, CancellationToken.None);
        Assert.Equal(secondId.ToString(), Assert.Single(second.Evidence).Reference.ChunkId);
        Assert.Null(second.FindDiagnostic!.NextOffset);
        var again = await gateway.SearchAsync(request, CancellationToken.None);
        Assert.Equal(first.Evidence[0].Reference.EvidenceId, again.Evidence[0].Reference.EvidenceId);
        var absent = await gateway.SearchAsync(request with { Query = "ISO XX 17" }, CancellationToken.None);
        Assert.Empty(absent.Evidence); // Percent and underscore never act as wildcards.
        Assert.Equal("no_literal_match_at_offset_in_observed_revision", absent.FindDiagnostic!.Status);
        Assert.Equal(2, gateway.Evidence.Count);
        var unseen = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
            request with { RevisionId = Guid.NewGuid().ToString() }, CancellationToken.None));
        Assert.Equal("canonical_read_source_not_observed", unseen.ErrorCode);
    }

    [Fact]
    public async Task Canonical_find_rejects_changed_identity_even_for_an_absent_literal()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("changed-literal-source", "A documented source body.");
        var settings = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(settings));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE document_revisions SET source_hash=@hash WHERE revision_id=@revision;",
                new { hash = new byte[32], revision = seed.RevisionId });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(
            new AdvancedAnalysisSearchRequest("absent text", DocId: seed.DocId.ToString(), DocPath: seed.DocPath,
                RevisionId: seed.RevisionId.ToString(), Operation: "find_source_text"), CancellationToken.None));
        Assert.Equal("canonical_find_source_identity_changed", error.ErrorCode);
    }
}
