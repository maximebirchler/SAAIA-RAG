using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
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
        Assert.True(match.Score >= 1.0);
        Assert.Equal("EN 15281", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.Equal("Introduction", match.SectionTitle);
        Assert.Equal(1, match.IngestionVersion);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_falls_back_to_document_metadata_for_reference_visible_in_filename()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("41414141-1111-1111-1111-111111111111");
        var docId = Guid.Parse("51515151-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("61616161-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Guidance on inerting and safety controls.", 5, 41, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Overview", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Guidance on inerting and safety controls.", 41, 6, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Guidance on inerting and safety controls.", 6, [3], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [4, 4, 4],
            55,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Ou trouve-t-on EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(docPath, match.DocPath);
        Assert.Equal("document_metadata_ref", match.ChunkType);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.Contains("15281", match.Text, StringComparison.Ordinal);
        Assert.True(match.Score >= 0.95);
    }

    [Fact]
    public async Task SearchSparseMatchesAsync_returns_contextual_chunk_hits_for_business_query()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("71717171-1111-1111-1111-111111111111");
        var docId = Guid.Parse("72727272-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("73737373-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Guidance on inerting and safety controls.", 5, 41, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Overview", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Guidance on inerting and safety controls.", 41, 6, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Guidance on inerting and safety controls.", 6, [3], "unit_exact_v1")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(
                0,
                0,
                0,
                0,
                1,
                1,
                "Document: CEN TR 15281 2006 Guidance on inerting.pdf\nHeading Path: Overview\nExcerpt:\nGuidance on inerting and safety controls.",
                124,
                17,
                [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [5, 5, 5],
            55,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None));

        long measuredSparseMs = -1;
        var matches = await RagEndpoints.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "inerting safety controls",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            value => measuredSparseMs = value);

        var match = Assert.Single(matches);
        Assert.Equal("sparse_bm25_v1", match.EmbeddingBasis);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Contains("inerting and safety controls", match.Text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Heading Path", match.EmbedText!, StringComparison.OrdinalIgnoreCase);
        Assert.True(match.Score > 0.0);
        Assert.True(measuredSparseMs >= 0);
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

    [Fact]
    public async Task SearchCoreAsync_returns_answer_with_caveat_for_customer_compliance_question_on_en_15281()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cccc3333-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            version: 1,
            sectionTitle: "Scope",
            chunkText: "EN 15281 guidance on inerting for the prevention of explosion.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Scope\nExcerpt:\nEN 15281 guidance on inerting for the prevention of explosion.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("EN 15281", "en 15281", "standard_ref")
            ]);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("J ai une discussion avec un client qui me demande si le projet respecte EN 15281, tu peux m en dire plus ?", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.NotEmpty(response.Matches);
        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(response.Matches, match => (match.DocPath ?? string.Empty).Contains("15281", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCoreAsync_uses_sparse_matches_for_cross_document_customer_selection_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dddd4444-4444-4444-4444-444444444444");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("eeee5555-5555-5555-5555-555555555555"),
            Guid.Parse("ffff6666-6666-6666-6666-666666666666"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            version: 1,
            sectionTitle: "Inerting",
            chunkText: "Inerting guidance covers oxygen concentration monitoring and explosion prevention.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Inerting\nExcerpt:\nInerting guidance covers oxygen concentration monitoring and explosion prevention.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("EN 15281", "en 15281", "standard_ref")
            ]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("11117777-7777-7777-7777-777777777777"),
            Guid.Parse("22228888-8888-8888-8888-888888888888"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            version: 1,
            sectionTitle: "PLC integration",
            chunkText: "IND570 manual explains PLC integration, PROFINET, EtherNet/IP and Modbus TCP.",
            contextualText: "Document: MettlerToledo_IND570.pdf\nSection: PLC integration\nExcerpt:\nIND570 manual explains PLC integration, PROFINET, EtherNet/IP and Modbus TCP.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("IND570", "ind570", "code_ref")
            ]);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Quel document faut il citer au client pour parler d inerting et d integration plc ?", TopK: 4));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);
        var docPaths = response.Matches.Select(m => m.DocPath ?? string.Empty).ToArray();

        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(docPaths, path => path.Contains("15281", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(docPaths, path => path.Contains("IND570", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCoreAsync_returns_ask_clarification_for_placeholder_standard_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("33339999-9999-9999-9999-999999999999");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("4444aaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("5555bbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            version: 1,
            sectionTitle: "Reliability",
            chunkText: "Reliability of inerting systems depends on monitoring and maintenance.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Reliability\nExcerpt:\nReliability of inerting systems depends on monitoring and maintenance.");

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("J ai une discussion avec un client qui me demande si le projet respecte la norme xxx.", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.False(string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion));
    }

    [Fact]
    public async Task SearchCoreAsync_returns_exact_ind570_document_for_manual_lookup()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("6666cccc-cccc-cccc-cccc-cccccccccccc");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("7777dddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("8888eeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            version: 1,
            sectionTitle: "Manual",
            chunkText: "The IND570 terminal manual includes installation, configuration and diagnostics.",
            contextualText: "Document: MettlerToledo_IND570.pdf\nSection: Manual\nExcerpt:\nThe IND570 terminal manual includes installation, configuration and diagnostics.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("IND570", "ind570", "code_ref")
            ]);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Est ce que tu as le manuel du terminal IND570 ?", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);
        Assert.NotEmpty(response.Matches);
        var first = response.Matches[0];

        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", $"{first.DocName} {first.DocPath}", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exact_match_v1", first.EmbeddingBasis);
    }

    [Fact]
    public async Task SearchCoreAsync_emits_retrieval_metrics_and_phase_activities()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("16161616-1616-1616-1616-161616161616");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var metricNames = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RetrievalTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.Start();

        var stoppedActivities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Ou trouve-t-on EN 15281 ?", Category: "atex", TopK: 4));

        Assert.NotEmpty(response.Matches);
        Assert.Contains("saaia.retrieval.requests", metricNames);
        Assert.Contains("saaia.retrieval.duration", metricNames);
        Assert.Contains("saaia.retrieval.exact_match.duration", metricNames);
        Assert.Contains(stoppedActivities, activity => activity.OperationName == "rag.search");
        Assert.Contains(stoppedActivities, activity => activity.OperationName == "retrieval_exact_match");
    }

    [Fact]
    public async Task SearchCoreAsync_runtime_ready_v5_cases_match_expected_behavior_and_primary_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("9999ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = RetrievalQuestionBankFixture.LoadV5().QuestionCases
            .Where(static testCase => testCase.RuntimeReady)
            .ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

            if (!string.Equals(testCase.ExpectedBehavior, guidance.Behavior, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected behavior '{testCase.ExpectedBehavior}' but got '{guidance.Behavior}'.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedResponseShape)
                && !string.Equals(testCase.ExpectedResponseShape, guidance.ResponseShape, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected response shape '{testCase.ExpectedResponseShape}' but got '{guidance.ResponseShape ?? "<null>"}'.");
            }

            if (response.Matches.Count == 0)
            {
                failures.Add($"{testCase.Name}: expected at least one match.");
                continue;
            }

            var primary = response.Matches[0];
            var primaryDocHint = ResolveDocHint(primary);
            if (!string.Equals(testCase.ExpectedPrimaryDocHint, primaryDocHint, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected primary doc '{testCase.ExpectedPrimaryDocHint}' but got '{primaryDocHint ?? "<none>"}'.");
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedPrimarySectionHint))
            {
                var primaryText = $"{primary.SectionTitle} {primary.HeadingPath} {primary.Text} {primary.EmbedText}";
                if (!primaryText.Contains(testCase.ExpectedPrimarySectionHint, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{testCase.Name}: expected primary section hint '{testCase.ExpectedPrimarySectionHint}' but top match was '{primary.SectionTitle ?? "<no-section>"}'.");
                }
            }

            Assert.NotEmpty(guidance.MatchedDocHints ?? Array.Empty<string>());
            Assert.Contains(testCase.ExpectedPrimaryDocHint!, guidance.MatchedDocHints!, StringComparer.Ordinal);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task SearchCoreAsync_runtime_ready_writer_cases_keep_prudent_guidance_shape()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("ababffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = RetrievalQuestionBankFixture.LoadV5().QuestionCases
            .Where(static testCase => testCase.RuntimeReady)
            .Where(static testCase =>
                string.Equals(testCase.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal)
                || string.Equals(testCase.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal))
            .ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

            if (string.Equals(testCase.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(guidance.QualificationNote)
                    || (!guidance.QualificationNote.Contains("contexte", StringComparison.OrdinalIgnoreCase)
                        && !guidance.QualificationNote.Contains("conformite", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{testCase.Name}: expected a prudent qualification note, got '{guidance.QualificationNote ?? "<null>"}'.");
                }

                foreach (var token in testCase.ExpectedQualificationTokens ?? Array.Empty<string>())
                {
                    if (!guidance.QualificationNote!.Contains(token, StringComparison.OrdinalIgnoreCase))
                        failures.Add($"{testCase.Name}: qualification note should contain '{token}', got '{guidance.QualificationNote}'.");
                }
            }

            if (string.Equals(testCase.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion)
                    || !guidance.ClarifyingQuestion.Contains('?', StringComparison.Ordinal)
                    || (!guidance.ClarifyingQuestion.Contains("quelle", StringComparison.OrdinalIgnoreCase)
                        && !guidance.ClarifyingQuestion.Contains("quel", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{testCase.Name}: expected a useful clarification question, got '{guidance.ClarifyingQuestion ?? "<null>"}'.");
                }

                foreach (var token in testCase.ExpectedClarificationTokens ?? Array.Empty<string>())
                {
                    if (!guidance.ClarifyingQuestion!.Contains(token, StringComparison.OrdinalIgnoreCase))
                        failures.Add($"{testCase.Name}: clarifying question should contain '{token}', got '{guidance.ClarifyingQuestion}'.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task SnapshotAsync_returns_304_when_if_none_match_matches_snapshot_etag()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdcdffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var firstContext = BuildRagHttpContext(tenantId);
        var firstResult = await InvokeDocumentsSnapshotAsync(firstContext, ds);
        await firstResult.ExecuteAsync(firstContext);

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Contains("snapshotId", ReadResponseBody(firstContext), StringComparison.Ordinal);

        var secondContext = BuildRagHttpContext(tenantId);
        secondContext.Request.Headers.IfNoneMatch = etag;

        var secondResult = await InvokeDocumentsSnapshotAsync(secondContext, ds);
        await secondResult.ExecuteAsync(secondContext);

        Assert.Equal(etag, secondContext.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext));
    }

    [Fact]
    public async Task SearchAsync_populates_category_ref_and_category_path_from_matched_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dedeffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        var expectedCategoryRef = $"cat_{displayOrder:000}";

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Ou trouve-t-on EN 15281 ?", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf", StringComparison.Ordinal));

        Assert.Equal("atex", item.Category);
        Assert.Equal("ATEX", item.CategoryPath);
        Assert.Equal(expectedCategoryRef, item.CategoryRef);
        Assert.Equal("exact_match", item.ProvenanceInfo!.Channel);
        Assert.Null(item.ProvenanceInfo.OffsetStart);
        Assert.Null(item.ProvenanceInfo.OffsetEnd);
    }

    private static DefaultHttpContext BuildRagHttpContext(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.Items[RequestIdMiddleware.RequestIdItemKey] = $"it-{tenantId:N}";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<IResult> InvokeDocumentsSnapshotAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("SnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds])!;
    }

    private static async Task<IResult> InvokeRagSearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOptions,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto request)
    {
        var method = typeof(RagEndpoints).GetMethod("SearchAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, ragOptions, httpFactory, request])!;
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static RagOptions CreateTestRagOptions()
        => new()
        {
            EmbeddingsBaseUrl = "http://tei.test/",
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = "knowledge_base",
            DefaultTopK = 5,
            MaxTopK = 20,
            EnableRerank = false
        };

    private static ExtractedExactMatchEntry BuildExactMatchEntry(string text, string normalizedText, string kind)
        => BuildExactMatchEntry(0, text, normalizedText, kind);

    private static ExtractedExactMatchEntry BuildExactMatchEntry(int entryIndex, string text, string normalizedText, string kind)
        => new(
            EntryIndex: entryIndex,
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            PageStart: 1,
            PageEnd: 1,
            Text: text,
            NormalizedText: normalizedText,
            CharCount: text.Length,
            TokenCount: Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
            Checksum: [1, 2, 3],
            Kind: kind);

    private static string? ResolveDocHint(RagMatch match)
    {
        var docText = $"{match.DocName} {match.DocPath}";
        if (docText.Contains("15281", StringComparison.OrdinalIgnoreCase))
            return "15281";
        if (docText.Contains("ind570", StringComparison.OrdinalIgnoreCase))
            return "IND570";
        if (docText.Contains("accord", StringComparison.OrdinalIgnoreCase))
            return "ACCORD";
        return null;
    }

    private sealed record RuntimeSeedSection(string Title, string Text);

    private static async Task PublishIndexedDocumentAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        int version,
        string sectionTitle,
        string chunkText,
        string contextualText,
        ExtractedExactMatchEntry[]? exactMatchEntries = null)
    {
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: version, indexedVersion: Math.Max(0, version - 1));

        var pages = new[]
        {
            new ExtractedPdfPage(1, chunkText, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), chunkText.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, sectionTitle, 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, chunkText, chunkText.Length, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, chunkText, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [3], "unit_exact_v1")
        };
        var contextualEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, contextualText, contextualText.Length, Math.Max(1, contextualText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 9],
            size: chunkText.Length,
            mtimeUtc: DateTime.UtcNow,
            version: version,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries ?? [],
            contextualEntries,
            CancellationToken.None);

        Assert.True(committed);
    }

    private static async Task PublishIndexedDocumentAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        int version,
        RuntimeSeedSection[] sections,
        ExtractedExactMatchEntry[]? exactMatchEntries = null)
    {
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: version, indexedVersion: Math.Max(0, version - 1));

        var pages = sections
            .Select((section, index) => new ExtractedPdfPage(
                index + 1,
                section.Text,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                section.Text.Length,
                [(byte)(10 + index)]))
            .ToArray();
        var extractedSections = sections
            .Select((section, index) => new ExtractedDocumentSection(index, section.Title, index + 1, index + 1, index + 1, index + 1, null))
            .ToArray();
        var units = sections
            .Select((section, index) => new ExtractedDocumentUnit(
                index,
                index,
                index + 1,
                index + 1,
                section.Text,
                section.Text.Length,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                [(byte)(20 + index)]))
            .ToArray();
        var retrievalChunks = sections
            .Select((section, index) => new ProjectedRetrievalChunk(
                index,
                index,
                index,
                index + 1,
                index + 1,
                section.Text,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                [(byte)(30 + index)],
                "unit_exact_v1"))
            .ToArray();
        var contextualEntries = sections
            .Select((section, index) =>
            {
                var contextualText = $"Document: {Path.GetFileName(docPath)}\nSection: {section.Title}\nHeading Path: {section.Title}\nExcerpt:\n{section.Text}";
                return new ProjectedContextualTextEntry(
                    index,
                    index,
                    index,
                    index,
                    index + 1,
                    index + 1,
                    contextualText,
                    contextualText.Length,
                    Math.Max(1, contextualText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                    [(byte)(40 + index)]);
            })
            .ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: sections.Sum(static section => section.Text.Length),
            mtimeUtc: DateTime.UtcNow,
            version: version,
            pages,
            extractedSections,
            units,
            retrievalChunks,
            exactMatchEntries ?? [],
            contextualEntries,
            CancellationToken.None);

        Assert.True(committed);
    }

    private static async Task PublishRuntimeReadyQuestionBankDocumentsAsync(PostgresIntegrationDb db, Guid tenantId)
    {
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("1212aaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("1313bbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            1,
            [
                new RuntimeSeedSection("Definitions", "Absolute inerting means replacing air with an inert gas and maintaining oxygen concentration below the limiting oxygen concentration. LOC and MAOC are used to describe the safe oxygen threshold below which the mixture should no longer explode."),
                new RuntimeSeedSection("Normative references", "Normative references around inerting include EN 61508 and EN 61511 for safety instrumented functions and safety lifecycle context."),
                new RuntimeSeedSection("Inert gases", "Nitrogen, carbon dioxide, steam, flue gases and noble gases are discussed as inerting media with different practical constraints."),
                new RuntimeSeedSection("Inerting methods", "Pressure-swing, vacuum-swing, flow-through and displacement inerting are compared for explosion prevention and process safety. Flow-through inerting is useful for long pipelines or vessels when gas feed and venting are remote from each other."),
                new RuntimeSeedSection("Process scope", "The guide covers gas, vapour, dust, mist and hybrid mixtures when assessing inerting and explosion prevention."),
                new RuntimeSeedSection("Process parameters", "Limiting oxygen concentration depends on temperature, pressure, fuel concentration, particle size and process conditions."),
                new RuntimeSeedSection("Safety margin", "The safety margin is the difference between the set point and the trip point so the control system can react before the oxygen concentration becomes unsafe."),
                new RuntimeSeedSection("System components", "The inerting system includes inert gas supply, monitoring, control, alarms, shutdown logic and protective devices."),
                new RuntimeSeedSection("Reliability", "Reliability of inerting systems depends on monitoring, alarms, maintenance, trip point definition and equipment performance. The guide distinguishes direct oxygen measurement from inferential approaches based on purge flow, pressure or time."),
                new RuntimeSeedSection("Oxygen monitoring", "Oxygen monitoring technologies, oxygen analyzers, set point and trip point strategy are described for inerting systems.")
            ],
            [
                BuildExactMatchEntry(0, "EN 15281", "en 15281", "standard_ref")
            ]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("1414cccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("1515dddd-dddd-dddd-dddd-dddddddddddd"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            1,
            [
                new RuntimeSeedSection("PLC integration", "The IND570 terminal communicates with PLC systems and supports integration through industrial network interfaces so the scale can exchange data with the automate."),
                new RuntimeSeedSection("PROFINET", "The PROFINET interface supports Siemens environments, unique IP assignment and topology considerations for the IND570 terminal."),
                new RuntimeSeedSection("Supported protocols", "Supported protocols include EtherNet/IP, PROFINET, PROFIBUS, Modbus TCP, Modbus RTU, ControlNet and DeviceNet."),
                new RuntimeSeedSection("EtherNet/IP", "The IND570 guide documents EtherNet/IP Class 1 and Class 3 messaging for PLC integration scenarios."),
                new RuntimeSeedSection("DeviceNet", "DeviceNet configuration requires a unique node address and a suitable network speed such as 125 Kb, 250 Kb or 500 Kb."),
                new RuntimeSeedSection("Modbus TCP", "Modbus TCP configuration covers the IND570 IP address, master and slave register references, and communication settings."),
                new RuntimeSeedSection("Hazardous area", "Not all IND570 versions are approved for hazardous areas and relay options are not intended for hazardous zone use."),
                new RuntimeSeedSection("Analog output", "Analog output options include current and voltage outputs such as 4-20 mA and 0-10 V with installation guidance for sending weight or rate information."),
                new RuntimeSeedSection("Analog calibration", "Analog calibration of the IND570 output is performed through setup steps that define range, scaling and verification."),
                new RuntimeSeedSection("Programming examples", "The guide includes programming examples with Siemens S7-300 and explains shared data access, integer format, byte ordering and floating point exchanges for PLC integration. Floating point is recommended when possible to avoid small conversion errors."),
                new RuntimeSeedSection("Shared data", "Shared data access allows read and write operations from the PLC through explicit or Class 3 style messaging using category, instance, attribute and length information."),
                new RuntimeSeedSection("Data integrity", "PLC programs should filter values with Data_OK, Update_In_Progress and integrity bits before using returned data so invalid values are not consumed."),
                new RuntimeSeedSection("PLC commands", "The PLC interface can exchange tare, target values and tolerances with the IND570 in supported integration profiles.")
            ],
            [
                BuildExactMatchEntry(0, "IND570", "ind570", "code_ref")
            ]);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "tei" => new Uri("http://tei.test/"),
                    "qdrant" => new Uri("http://qdrant.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "data": [
                    { "embedding": [0.1, 0.2, 0.3, 0.4] }
                  ]
                }
                """));
            }

            if (path.EndsWith("/points/search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "result": []
                }
                """));
            }

            if (path.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "results": []
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
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
