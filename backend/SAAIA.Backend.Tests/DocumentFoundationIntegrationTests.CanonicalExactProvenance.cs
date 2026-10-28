using Dapper;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_exact_publishes_split_chunk_identity_and_returns_only_the_literal_bearing_fragment()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var corpus = await CanonicalExactMatchEntryProjectionTests.BuildCorpusAsync(filterOneChunk: true);
        var tenant = Guid.NewGuid(); var document = Guid.NewGuid();
        await PublishCanonicalExactAsync(db, tenant, document, corpus);
        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        var matches = await CanonicalRetrieval.SearchSourceBackedCanonicalExactMatchesAsync(dataSource, tenant,
            "IND570", null, document.ToString(), null, 30, CancellationToken.None);
        Assert.NotEmpty(matches);
        Assert.All(matches, match => Assert.Contains("IND570", match.Text, StringComparison.Ordinal));
        var expected = corpus.Chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk)
            .Where(chunk => chunk.Text.Contains("IND570", StringComparison.Ordinal))
            .Select(chunk => DocumentFoundationRepo.BuildStableRetrievalChunkId(document, 1, chunk.ChunkIndex).ToString()).ToArray();
        Assert.Equal(expected.Order(), matches.Select(match => match.ChunkId).Order());
        Assert.Empty(await CanonicalRetrieval.SearchSourceBackedCanonicalExactMatchesAsync(dataSource, Guid.NewGuid(),
            "IND570", null, document.ToString(), null, 30, CancellationToken.None));
        Assert.Empty(await CanonicalRetrieval.SearchSourceBackedCanonicalExactMatchesAsync(dataSource, tenant,
            "IND570", null, Guid.NewGuid().ToString(), null, 30, CancellationToken.None));
        await using var connection = await dataSource.OpenConnectionAsync();
        Assert.Equal(corpus.Index.ExactEntries.Count, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM exact_match_entries WHERE tenant_id=@tenant AND unit_id IS NULL AND metadata->>'sourceKind'='canonical_retrieval_chunk_v1';", new { tenant }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM exact_match_entries e
            LEFT JOIN retrieval_chunks c ON c.tenant_id=e.tenant_id AND c.revision_id=e.revision_id
              AND c.retrieval_chunk_id::text=e.metadata->>'sourceRetrievalChunkId'
            WHERE e.tenant_id=@tenant AND c.retrieval_chunk_id IS NULL;
            """, new { tenant }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_exact_rejects_stale_foreign_nonliteral_and_old_misbound_entries_in_real_sql()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var corpus = await CanonicalExactMatchEntryProjectionTests.BuildCorpusAsync();
        var tenant = Guid.NewGuid(); var document = Guid.NewGuid();
        await PublishCanonicalExactAsync(db, tenant, document, corpus);
        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        async Task<IReadOnlyList<RagMatch>> Search() => await CanonicalRetrieval.SearchSourceBackedCanonicalExactMatchesAsync(
            dataSource, tenant, "IND570", null, document.ToString(), null, 30, CancellationToken.None);
        Assert.NotEmpty(await Search());
        var good = await connection.QuerySingleAsync<string>("SELECT metadata->>'sourceRetrievalChunkId' FROM exact_match_entries WHERE tenant_id=@tenant AND text_content='IND570' LIMIT 1;", new { tenant });
        foreach (var invalid in new[] { DocumentFoundationRepo.BuildStableRetrievalChunkId(document, 2, 0).ToString(),
                                       DocumentFoundationRepo.BuildStableRetrievalChunkId(Guid.NewGuid(), 1, 0).ToString() })
        {
            await connection.ExecuteAsync("UPDATE exact_match_entries SET metadata=jsonb_set(metadata,'{sourceRetrievalChunkId}',to_jsonb(CAST(@invalid AS text))) WHERE tenant_id=@tenant;", new { tenant, invalid });
            Assert.Empty(await Search());
        }
        await connection.ExecuteAsync("UPDATE exact_match_entries SET metadata=jsonb_set(metadata,'{sourceRetrievalChunkId}',to_jsonb(CAST(@good AS text))) WHERE tenant_id=@tenant;", new { tenant, good });
        Assert.NotEmpty(await Search());
        await connection.ExecuteAsync("UPDATE retrieval_chunks SET text_content='A different synthetic source without that identifier.', metadata=metadata-'literalSearchText' WHERE tenant_id=@tenant;", new { tenant });
        Assert.Empty(await Search());
        // An old erroneous ordinal link must not be revived by the new canonical branch.
        var wrongUnit = await connection.QuerySingleAsync<Guid>("SELECT unit_id FROM document_units WHERE tenant_id=@tenant AND ordinal=0;", new { tenant });
        await connection.ExecuteAsync("""
            UPDATE retrieval_chunks SET unit_id=@wrongUnit,text_content='The IND570 source fragment is present here.' WHERE tenant_id=@tenant;
            UPDATE exact_match_entries SET unit_id=@wrongUnit,metadata='{}'::jsonb WHERE tenant_id=@tenant;
            """, new { tenant, wrongUnit });
        Assert.Empty(await Search()); // The linked unit still contains the unrelated heading.
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_exact_publication_refuses_missing_filtered_ambiguous_or_nonliteral_source_and_rolls_back()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var corpus = await CanonicalExactMatchEntryProjectionTests.BuildCorpusAsync(filterOneChunk: true);
        var candidate = corpus.Index.ExactEntries.First();
        var invalid = new[] {
            candidate with { SourceRetrievalChunkIndex = int.MaxValue },
            candidate with { SourceRetrievalChunkIndex = corpus.FilteredIndex },
            candidate with { UnitOrdinal = 0 },
            candidate with { Text = "ABSENT-SYNTHETIC-SOURCE-999", NormalizedText = "absent-synthetic-source-999" }
        };
        foreach (var entry in invalid)
        {
            var tenant = Guid.NewGuid(); var document = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidDataException>(() => PublishCanonicalExactAsync(db, tenant, document, corpus, [entry]));
            await using var connection = new NpgsqlConnection(db.ConnectionString);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM exact_match_entries WHERE tenant_id=@tenant;", new { tenant }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM retrieval_chunks WHERE tenant_id=@tenant;", new { tenant }));
        }
    }

    private static async Task PublishCanonicalExactAsync(PostgresIntegrationDb db, Guid tenant, Guid document,
        CanonicalExactMatchEntryProjectionTests.Corpus corpus, IReadOnlyList<ExtractedExactMatchEntry>? entries = null)
    {
        const string path = "Synthetic/canonical-exact.pdf";
        var job = Guid.NewGuid();
        await db.SeedRunningJobAsync(tenant, document, job, path, ingestionVersion: 1, indexedVersion: 0);
        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(dataSource, tenant, job, path,
            hash: Convert.FromHexString(corpus.Document.Source.Sha256), size: 1234, mtimeUtc: DateTime.UtcNow, version: 1,
            corpus.Pages, corpus.Sections, corpus.Units, corpus.Chunks, entries ?? corpus.Index.ExactEntries,
            corpus.Index.ContextualEntries, CancellationToken.None, leaseOwner: "test-worker", structuredProfileUnits: corpus.ProfileUnits));
    }
}
