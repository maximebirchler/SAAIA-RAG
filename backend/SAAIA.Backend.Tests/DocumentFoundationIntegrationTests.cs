using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using Xunit;
namespace SAAIA.Backend.Tests;

public sealed class DocumentFoundationIntegrationTests
{
    [Fact]
    public async Task CompleteUpsertAsync_publishes_revision_foundation_and_artifact_summaries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Intro text", 2, 10, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Intro text", 10, 2, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Intro text", 2, [3], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "Intro text", "intro text", 10, 2, [4], "verbatim_excerpt")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\n\nExcerpt:\nIntro text", 37, 5, [5])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 9],
            size: 123,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var pageCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_page_index;");
        var sectionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_sections;");
        var unitCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_units;");
        var chunkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunks;");
        var exactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM exact_match_entries;");
        var contextualCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contextual_text_entries;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(1, revisionCount);
        Assert.Equal(1, pageCount);
        Assert.Equal(1, sectionCount);
        Assert.Equal(1, unitCount);
        Assert.Equal(1, chunkCount);
        Assert.Equal(1, exactCount);
        Assert.Equal(1, contextualCount);
        Assert.Equal(6, artifactCount);

        var revision = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM document_revisions LIMIT 1;");
        Assert.Equal(1, revision.ingestion_version);
        Assert.Equal(1, revision.indexed_version);

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='exact_match_entries';");
        Assert.Contains("\"count\":1", artifactPayload, StringComparison.Ordinal);
        Assert.Contains("\"hashBasis\":\"canonical_text_v1\"", artifactPayload, StringComparison.Ordinal);

        var retrievalChunkMetadata = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM retrieval_chunks LIMIT 1;");
        Assert.Contains("\"chunkType\":\"unit_exact_v1\"", retrievalChunkMetadata, StringComparison.Ordinal);
        Assert.Contains("\"sectionTitle\":\"Introduction\"", retrievalChunkMetadata, StringComparison.Ordinal);
        Assert.Contains("\"headingPath\":\"Introduction\"", retrievalChunkMetadata, StringComparison.Ordinal);

        var retrievalChunkId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT retrieval_chunk_id FROM retrieval_chunks LIMIT 1;");
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 0), retrievalChunkId);
    }

    [Fact]
    public async Task CompleteUpsertAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
        const string docPath = "Programming/Test.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: 99,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [],
            sections: [],
            units: [],
            retrievalChunks: [],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
    }

    [Fact]
    public async Task CompleteDeleteAsync_publishes_processing_run_without_revision()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dddddddd-1111-1111-1111-111111111111");
        var docId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("ffffffff-3333-3333-3333-333333333333");
        const string docPath = "General/DeleteMe.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 3, indexedVersion: 2, action: "delete");

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteDeleteAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            version: 3,
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var processingRun = await conn.QuerySingleAsync<(string action, string status, int indexed_version_before, int indexed_version_after, Guid? revision_id)>(
            "SELECT action, status, indexed_version_before, indexed_version_after, revision_id FROM document_processing_runs LIMIT 1;");
        var docStatus = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version)>(
            "SELECT status, ingestion_version, indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
            new { tenant = tenantId, doc = docId });

        Assert.Equal(0, revisionCount);
        Assert.Equal("delete", processingRun.action);
        Assert.Equal("done", processingRun.status);
        Assert.Equal(2, processingRun.indexed_version_before);
        Assert.Equal(0, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);
        Assert.Equal("deleted", docStatus.status);
        Assert.Equal(0, docStatus.ingestion_version);
        Assert.Equal(0, docStatus.indexed_version);
    }

    [Fact]
    public async Task MarkFailedAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("12121212-1111-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-3333-3333-3333-333333333333");
        const string docPath = "General/Failure.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("failed", jobStatus);
    }

    [Fact]
    public async Task MarkCanceledAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("78787878-1111-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-3333-3333-3333-333333333333");
        const string docPath = "General/Canceled.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 2);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkCanceledAsync(ds, jobId, "canceled_by_admin_test", CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("canceled", jobStatus);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_returns_active_exact_match_hits_before_dense_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdcdcdcd-1111-1111-1111-111111111111");
        var docId = Guid.Parse("efefefef-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("ababcdcd-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281", 2, 8, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281", 8, 2, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281", 2, [3], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\nExcerpt:\nEN 15281", 34, 4, [5])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 8, 7],
            size: 42,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None);

        Assert.True(committed);

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit la norme EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(1.0, match.Score);
        Assert.Equal("EN 15281", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.Equal("Introduction", match.SectionTitle);
        Assert.Equal(1, match.IngestionVersion);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_supports_exact_match_anchor_when_it_resolves_to_a_retrieval_chunk()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("10101010-1111-1111-1111-111111111111");
        var docId = Guid.Parse("20202020-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("30303030-3333-3333-3333-333333333333");
        const string docPath = "ATEX/ExactAnchor.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281 gamma delta", 4, 21, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281", 8, 2, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta", 11, 2, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281", 2, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta", 2, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [6], "standard_ref")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [9, 9, 8],
            42,
            DateTime.UtcNow,
            2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries: [],
            CancellationToken.None));

        var exactMatches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var exactAnchor = Assert.Single(exactMatches);
        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [exactAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 2, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("Safety", linked.SectionTitle);
        Assert.Equal("Safety", linked.HeadingPath);
        Assert.True(linked.Score < exactAnchor.Score);
        Assert.True(linked.Score >= 0.75);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_honors_category_and_document_filters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaabbbb-1111-1111-1111-111111111111");
        var docAId = Guid.Parse("ccccdddd-2222-2222-2222-222222222222");
        var docBId = Guid.Parse("eeeeffff-3333-3333-3333-333333333333");
        var jobAId = Guid.Parse("11112222-4444-4444-4444-444444444444");
        var jobBId = Guid.Parse("55556666-5555-5555-5555-555555555555");
        const string docAPath = "ATEX/CEN.pdf";
        const string docBPath = "Maintenance/Guide.pdf";

        await db.SeedRunningJobAsync(tenantId, docAId, jobAId, docAPath, ingestionVersion: 1, indexedVersion: 0);
        await db.SeedRunningJobAsync(tenantId, docBId, jobBId, docBPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var docAPages = new[] { new ExtractedPdfPage(1, "EN 15281", 2, 8, [1]) };
        var docASections = new[] { new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null) };
        var docAUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281", 8, 2, [2]) };
        var docAChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281", 2, [3], "unit_exact_v1") };
        var docAExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref") };
        var docAContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\nExcerpt:\nEN 15281", 34, 4, [5]) };

        var docBPages = new[] { new ExtractedPdfPage(1, "EN 15281", 2, 8, [6]) };
        var docBSections = new[] { new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null) };
        var docBUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281", 8, 2, [7]) };
        var docBChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281", 2, [8], "unit_exact_v1") };
        var docBExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [9], "standard_ref") };
        var docBContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: Guide.pdf\nExcerpt:\nEN 15281", 36, 4, [10]) };

        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobAId, docAPath, [1, 1, 1], 10, DateTime.UtcNow, 1, docAPages, docASections, docAUnits, docAChunks, docAExact, docAContext, CancellationToken.None));
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobBId, docBPath, [2, 2, 2], 10, DateTime.UtcNow, 1, docBPages, docBSections, docBUnits, docBChunks, docBExact, docBContext, CancellationToken.None));

        var byCategory = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            null,
            null,
            10,
            CancellationToken.None);

        var byDoc = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            null,
            docBId.ToString(),
            docBPath,
            10,
            CancellationToken.None);

        Assert.Single(byCategory);
        Assert.Equal(docAId.ToString(), byCategory[0].DocId);
        Assert.Equal("ATEX/CEN.pdf", byCategory[0].DocPath);

        Assert.Single(byDoc);
        Assert.Equal(docBId.ToString(), byDoc[0].DocId);
        Assert.Equal("Maintenance/Guide.pdf", byDoc[0].DocPath);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_retrieval_chunk_links_for_adjacent_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1111aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("2222bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("3333cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/Links.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta gamma delta", 4, 22, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta", 10, 2, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta", 11, 2, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta", 2, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta", 2, [5], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: 44,
            mtimeUtc: DateTime.UtcNow,
            version: 2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var linkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunk_links;");
        Assert.Equal(3, linkCount);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_returns_enriched_results_and_accepts_linked_context_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/LinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 3, indexedVersion: 2);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta gamma delta epsilon zeta", 6, 35, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta", 10, 2, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta", 11, 2, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta", 12, 2, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta", 2, [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta", 2, [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta", 2, [7], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: 55,
            mtimeUtc: DateTime.UtcNow,
            version: 3,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        var anchor = new RagMatch(
            Score: 0.81,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "LinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta",
            IngestionVersion: 3,
            HashDoc: "hash",
            EmbedText: "alpha beta",
            EmbeddingBasis: "linked_context_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [anchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var match = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString());
        Assert.Equal("linked_context_v1", match.EmbeddingBasis);
        Assert.Equal("Safety", match.SectionTitle);
        Assert.Equal("Safety", match.HeadingPath);
        Assert.Equal("unit_exact_v1", match.ChunkType);
        Assert.NotNull(match.NextChunkId);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_supports_real_second_wave_expansion_from_linked_results()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("7777aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("8888bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("9999cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/SecondWave.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta gamma delta epsilon zeta", 6, 35, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta", 10, 2, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta", 11, 2, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta", 12, 2, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta", 2, [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta", 2, [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta", 2, [7], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [6, 6, 7],
            55,
            DateTime.UtcNow,
            4,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var denseAnchor = new RagMatch(
            Score: 0.82,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SecondWave.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta",
            IngestionVersion: 4,
            HashDoc: "hash",
            EmbedText: "alpha beta",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString());

        var firstWave = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [denseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var firstLinked = Assert.Single(firstWave, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString());
        Assert.Equal("linked_context_v1", firstLinked.EmbeddingBasis);

        var secondWave = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [firstLinked],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        Assert.Contains(secondWave, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 2).ToString());
    }

    private sealed class PostgresIntegrationDb : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        public string ConnectionString { get; }

        private PostgresIntegrationDb(string adminConnectionString, string databaseName, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public static async Task<PostgresIntegrationDb?> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable("SAAIA_TEST_PG_CONN");
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                return null;

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
            var adminConnectionString = adminBuilder.ConnectionString;
            var databaseName = $"saaia_it_{Guid.NewGuid():N}";

            await using (var adminConn = new NpgsqlConnection(adminConnectionString))
            {
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync($"CREATE DATABASE \"{databaseName}\";");
            }

            var dbBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName
            };

            var db = new PostgresIntegrationDb(adminConnectionString, databaseName, dbBuilder.ConnectionString);
            var migrationsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SAAIA.Backend", "Db", "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, CancellationToken.None);
            return db;
        }

        public async Task SeedRunningJobAsync(Guid tenantId, Guid docId, Guid jobId, string docPath, int ingestionVersion, int indexedVersion, string action = "upsert")
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "INSERT INTO tenants(tenant_id, name) VALUES(@tenant_id, @name);",
                new { tenant_id = tenantId, name = "Test tenant" });

            await conn.ExecuteAsync(
                @"INSERT INTO documents(
                    tenant_id, doc_id, doc_path, doc_name, category, status, ingestion_version, indexed_version, created_at, updated_at)
                  VALUES(
                    @tenant_id, @doc_id, @doc_path, @doc_name, @category, 'pending', @ingestion_version, @indexed_version, now(), now());",
                new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    doc_path = docPath,
                    doc_name = Path.GetFileName(docPath),
                    category = Path.GetDirectoryName(docPath)?.Replace('\\', '/') ?? ""
                        });

            await conn.ExecuteAsync(
                @"UPDATE documents
                  SET ingestion_version=@ingestion_version,
                      indexed_version=@indexed_version
                  WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
                new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    ingestion_version = ingestionVersion,
                    indexed_version = indexedVersion
                });

            var payload = JsonSerializer.Serialize(new { });
            await conn.ExecuteAsync(
                @"INSERT INTO ingestion_jobs(
                    job_id, tenant_id, action, doc_path, category, status, attempts, locked_by, locked_at, available_at, created_at, started_at, payload)
                  VALUES(
                    @job_id, @tenant_id, @action, @doc_path, @category, 'running', 1, 'test-worker', now(), now(), now(), now(), CAST(@payload AS jsonb));",
                new
                {
                    job_id = jobId,
                    tenant_id = tenantId,
                    action,
                    doc_path = docPath,
                    category = Path.GetDirectoryName(docPath)?.Replace('\\', '/') ?? "",
                    payload
                });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var adminConn = new NpgsqlConnection(_adminConnectionString);
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync(
                    $@"SELECT pg_terminate_backend(pid)
                       FROM pg_stat_activity
                       WHERE datname = '{_databaseName}'
                         AND pid <> pg_backend_pid();");
                await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\";");
            }
            catch
            {
                // Best-effort cleanup for optional integration tests.
            }
        }
    }
}
