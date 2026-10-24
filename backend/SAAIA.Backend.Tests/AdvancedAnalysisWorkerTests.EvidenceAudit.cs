using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Fact]
    public async Task Page_only_reference_cites_actual_chunk_page_union_without_relabeling_outer_page_text()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        const string body = "First-page introduction.\nMiddle-page procedure.\nLast-page conditions.";
        var seed = await database.SeedEvidenceAsync("cross-page-reference", body);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE retrieval_chunks SET page_start=1,page_end=3 WHERE retrieval_chunk_id=@chunk;",
                new { chunk = seed.ChunkId });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(BuildAdvancedOptions()));
        var pageReference = new AdvancedAnalysisEvidenceReference
        {
            DocId = seed.DocId.ToString(), RevisionId = seed.RevisionId.ToString(), PageStart = 2, PageEnd = 2
        };

        var resolved = await resolver.ResolveAsync(seed.TenantId, [pageReference], CancellationToken.None);

        Assert.True(resolved.IsValid);
        var proof = Assert.Single(resolved.Evidence);
        Assert.Equal(body, proof.Content);
        Assert.Equal(1, proof.Reference.PageStart);
        Assert.Equal(3, proof.Reference.PageEnd);
        Assert.Null(proof.Reference.ChunkId);
        var falseChunkPage = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString(), PageStart = 2, PageEnd = 2 }],
            CancellationToken.None);
        Assert.False(falseChunkPage.IsValid);
        var differentDocument = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { DocId = Guid.NewGuid().ToString(), RevisionId = seed.RevisionId.ToString(), PageStart = 2, PageEnd = 2 }],
            CancellationToken.None);
        Assert.False(differentDocument.IsValid);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("status")]
    [InlineData("index")]
    public async Task Empty_canonical_page_window_revalidates_source_before_reporting_no_chunks(string mutation)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("empty-window-identity", "Canonical source body.");
        var settings = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(settings));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, http, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);
        var read = new AdvancedAnalysisSearchRequest("physical page 100", TopK: 20,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(),
            PageStart: 100, PageEnd: 100, Operation: "read_source");
        Assert.Empty((await gateway.SearchAsync(read, CancellationToken.None)).Evidence);
        var sql = mutation switch
        {
            "hash" => "UPDATE document_revisions SET source_hash=@hash WHERE revision_id=@revision;",
            "path" => "UPDATE documents SET doc_path='Documents/relocated.pdf' WHERE doc_id=@doc;",
            "status" => "UPDATE documents SET status='pending' WHERE doc_id=@doc;",
            _ => "UPDATE documents SET indexed_version=0 WHERE doc_id=@doc;"
        };
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync(sql, new { hash = new byte[32], revision = seed.RevisionId, doc = seed.DocId });

        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            gateway.SearchAsync(read, CancellationToken.None));

        Assert.Equal("canonical_read_source_identity_changed", error.ErrorCode);
        Assert.Single(gateway.Evidence);
    }

    [Theory]
    [InlineData("pending", 1, false)]
    [InlineData("indexed", 0, false)]
    [InlineData("indexed", 1, true)]
    public async Task Resolver_never_reintroduces_withdrawn_or_unindexed_document_proof(
        string status, int indexedVersion, bool expectedValid)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("availability", "Documented source body.");
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE documents SET status=@status,indexed_version=@indexedVersion WHERE doc_id=@doc;",
                new { status, indexedVersion, doc = seed.DocId });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(BuildAdvancedOptions()));
        var reference = new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() };
        var chunk = await resolver.ResolveAsync(seed.TenantId, [reference], CancellationToken.None);
        var span = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { RevisionId = seed.RevisionId.ToString(), PageStart = 2, PageEnd = 2 }],
            CancellationToken.None);
        var otherTenant = await resolver.ResolveAsync(Guid.NewGuid(), [reference], CancellationToken.None);

        Assert.Equal(expectedValid, chunk.IsValid);
        Assert.Equal(expectedValid, span.IsValid);
        Assert.False(otherTenant.IsValid);
    }

    [Fact]
    public async Task Scoped_search_rejects_stale_revision_handle_but_explicit_page_read_can_inspect_it()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) return;
        var seed = await database.SeedEvidenceAsync("revision-scope", "Canonical source body.");
        var settings = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(settings));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId = seed.ChunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE documents SET indexed_version=2 WHERE doc_id=@doc;", new { doc = seed.DocId });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, http, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);
        var search = new AdvancedAnalysisSearchRequest("canonical", TopK: 20,
            DocId: seed.DocId.ToString(), DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString());

        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            gateway.SearchAsync(search, CancellationToken.None));
        Assert.Equal("canonical_search_source_identity_changed", error.ErrorCode);
        Assert.Single(gateway.Evidence);
        var historical = await gateway.SearchAsync(search with
        {
            Operation = "read_source", PageStart = 2, PageEnd = 2
        }, CancellationToken.None);
        Assert.Equal(seed.RevisionId.ToString(), Assert.Single(historical.Evidence).Reference.RevisionId);
    }
}
