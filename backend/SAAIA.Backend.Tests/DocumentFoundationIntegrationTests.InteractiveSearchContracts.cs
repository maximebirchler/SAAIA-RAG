using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{

    [Fact]
    public void Stable_content_card_id_uses_normalized_title_not_card_order()
    {
        var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var first = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "  Control   Source  ");
        var second = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "control source");
        var different = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "Other source");

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_revision_foundation_and_artifact_summaries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Pressure verification", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.",
                CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."),
                [3],
                "unit_exact_v1",
                SourceUnitOrdinals: [0],
                SourceUnitStartOrdinal: 0,
                SourceUnitEndOrdinal: 0,
                SourceUnitCount: 1,
                ChunkComposition: "single_unit")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), [4], "verbatim_excerpt")
        };
        var contextualTextEntries = ContextualTextProjector.Project(docPath, sections, units, retrievalChunks);

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
            CancellationToken.None, leaseOwner: "test-worker");

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
        var profileCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profiles;");
        var profileCardCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profile_content_cards;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(1, revisionCount);
        Assert.Equal(1, pageCount);
        Assert.Equal(1, sectionCount);
        Assert.Equal(1, unitCount);
        Assert.Equal(1, chunkCount);
        Assert.Equal(1, exactCount);
        Assert.Equal(1, contextualCount);
        Assert.Equal(1, profileCount);
        Assert.Equal(1, profileCardCount);
        Assert.Equal(10, artifactCount);

        var contextualMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM contextual_text_entries LIMIT 1;");
        using var contextualMetadata = JsonDocument.Parse(contextualMetadataJson ?? "{}");
        var metadataRoot = contextualMetadata.RootElement;
        Assert.Equal("contextual_text_v3", metadataRoot.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, metadataRoot.GetProperty("chunkIndex").GetInt32());
        Assert.Equal("unit_exact_v1", metadataRoot.GetProperty("chunkType").GetString());
        Assert.Equal("content", metadataRoot.GetProperty("contentRole").GetString());
        Assert.Equal("Pressure verification", metadataRoot.GetProperty("sectionTitle").GetString());
        Assert.Equal("Pressure verification", metadataRoot.GetProperty("headingPath").GetString());
        Assert.Equal("single_unit", metadataRoot.GetProperty("chunkComposition").GetString());
        Assert.Equal([0], metadataRoot.GetProperty("sourceUnitOrdinals").EnumerateArray().Select(item => item.GetInt32()).ToArray());

        var unitMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM document_units LIMIT 1;");
        using var unitMetadata = JsonDocument.Parse(unitMetadataJson ?? "{}");
        Assert.Equal("content", unitMetadata.RootElement.GetProperty("contentRole").GetString());
        Assert.Equal(0.0, unitMetadata.RootElement.GetProperty("navigationScore").GetDouble());

        var revision = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM document_revisions LIMIT 1;");
        Assert.Equal(1, revision.ingestion_version);
        Assert.Equal(1, revision.indexed_version);

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='exact_match_entries';");
        using var artifactMetadata = JsonDocument.Parse(artifactPayload!);
        Assert.Equal(1, artifactMetadata.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("canonical_text_v1", artifactMetadata.RootElement.GetProperty("hashBasis").GetString());

        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using (var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}"))
        {
            Assert.Equal("pdf_text", processingMetadata.RootElement.GetProperty("extractionSource").GetString());
            Assert.False(processingMetadata.RootElement.GetProperty("ocrAttempted").GetBoolean());
            Assert.False(processingMetadata.RootElement.GetProperty("ocrApplied").GetBoolean());
            Assert.Equal(JsonValueKind.Null, processingMetadata.RootElement.GetProperty("ocrDurationMs").ValueKind);
            Assert.True(processingMetadata.RootElement.TryGetProperty("documentLanguage", out _));
            Assert.Equal("document_profile_projector", processingMetadata.RootElement.GetProperty("documentLanguageSource").GetString());
            Assert.Equal("deterministic_v1", processingMetadata.RootElement.GetProperty("documentProfileVersion").GetString());
            Assert.Equal(JsonValueKind.Null, processingMetadata.RootElement.GetProperty("nativeExtractionQuality").ValueKind);
            var extractionQuality = processingMetadata.RootElement.GetProperty("extractionQuality");
            Assert.Equal(1, extractionQuality.GetProperty("pageCount").GetInt32());
            Assert.Equal(1, extractionQuality.GetProperty("textPageCount").GetInt32());
            Assert.Equal("ok", extractionQuality.GetProperty("textStatus").GetString());
            Assert.False(extractionQuality.GetProperty("ocrRecommended").GetBoolean());
            var retrievalChunkQuality = processingMetadata.RootElement.GetProperty("retrievalChunkQuality");
            Assert.Equal(1, retrievalChunkQuality.GetProperty("totalChunkCount").GetInt32());
            Assert.Equal(1, retrievalChunkQuality.GetProperty("searchableChunkCount").GetInt32());
            Assert.Equal(0, retrievalChunkQuality.GetProperty("rejectedChunkCount").GetInt32());
            Assert.False(retrievalChunkQuality.GetProperty("manualReviewRecommended").GetBoolean());
        }

        var pageMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM document_page_index LIMIT 1;");
        using (var pageMetadata = JsonDocument.Parse(pageMetadataJson ?? "{}"))
        {
            Assert.Equal(pages[0].WordCount, pageMetadata.RootElement.GetProperty("wordCount").GetInt32());
            var extractionQuality = pageMetadata.RootElement.GetProperty("extractionQuality");
            Assert.Equal("page_extraction_quality_v2", extractionQuality.GetProperty("diagnosticVersion").GetString());
            Assert.Equal("page_ok", extractionQuality.GetProperty("qualityStatus").GetString());
            Assert.Equal(1.0, extractionQuality.GetProperty("extractionConfidence").GetDouble());
            Assert.False(extractionQuality.GetProperty("manualReviewRecommended").GetBoolean());
            Assert.Equal("ok", extractionQuality.GetProperty("textStatus").GetString());
            Assert.False(extractionQuality.GetProperty("textSparse").GetBoolean());
            Assert.False(extractionQuality.GetProperty("ocrCandidate").GetBoolean());
            Assert.Equal(1, extractionQuality.GetProperty("unitCount").GetInt32());
            Assert.Equal(1, extractionQuality.GetProperty("chunkCount").GetInt32());
        }

        var profile = await conn.QuerySingleAsync<(string summary_text, string search_text, string[] keywords, string metadata)>(
            "SELECT summary_text, search_text, keywords, metadata::text FROM document_profiles LIMIT 1;");
        Assert.Contains("CEN.pdf", profile.summary_text, StringComparison.Ordinal);
        Assert.Contains("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", profile.search_text, StringComparison.Ordinal);
        Assert.Contains("pressure", profile.keywords);
        using (var profileMetadata = JsonDocument.Parse(profile.metadata))
        {
            Assert.Equal(1, profileMetadata.RootElement.GetProperty("contentCardCount").GetInt32());
            Assert.Contains(
                profileMetadata.RootElement.GetProperty("contentCards").EnumerateArray(),
                card => string.Equals(card.GetProperty("title").GetString(), "Pressure verification", StringComparison.Ordinal));
        }

        var profileCard = await conn.QuerySingleAsync<(string title, string search_text, string[] signals)>(
            "SELECT title, search_text, signals FROM document_profile_content_cards LIMIT 1;");
        Assert.Equal("Pressure verification", profileCard.title);
        Assert.Contains(profileCard.title, profileCard.search_text, StringComparison.Ordinal);
        Assert.Contains("pressure", profileCard.signals);

        var profileMatches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "pressure verification",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal(docId.ToString(), profileMatch.DocId);
        Assert.Equal(docPath, profileMatch.DocPath);
        Assert.Equal(1, profileMatch.IngestionVersion);
        Assert.Equal("090909", profileMatch.HashDoc);
        Assert.Contains("Pressure verification", profileMatch.Text, StringComparison.OrdinalIgnoreCase);

        var retrievalChunkMetadata = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM retrieval_chunks LIMIT 1;");
        using var chunkMetadata = JsonDocument.Parse(retrievalChunkMetadata!);
        Assert.Equal("unit_exact_v1", chunkMetadata.RootElement.GetProperty("chunkType").GetString());
        Assert.Equal("Pressure verification", chunkMetadata.RootElement.GetProperty("sectionTitle").GetString());
        Assert.Equal("Pressure verification", chunkMetadata.RootElement.GetProperty("headingPath").GetString());

        var retrievalChunkId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT retrieval_chunk_id FROM retrieval_chunks LIMIT 1;");
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 0), retrievalChunkId);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_only_searchable_retrieval_chunks_and_contextual_entries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111112");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333334");
        const string docPath = "Ops/Manual.pdf";
        var noisyText = string.Join(
            ' ',
            Enumerable.Repeat(
                "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A 0 OQ =| Zz UO | W = cr O = 0",
                3));

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Reliable commissioning procedure with enough semantic body text.", 7, 62, [1]),
            new ExtractedPdfPage(2, noisyText, 120, noisyText.Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Commissioning", 1, 1, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].CharCount, pages[0].WordCount, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, noisyText, noisyText.Length, 120, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pages[0].Text, 7, [5], "unit_exact_v1", ExtractionTextStatus: "ok"),
            new ProjectedRetrievalChunk(1, 0, 1, 2, 2, noisyText, 120, [6], "section_window_v1", ExtractionTextStatus: "ok")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "excerpt:\n" + pages[0].Text, 72, 8, [7]),
            new ProjectedContextualTextEntry(1, 0, 1, 1, 2, 2, "excerpt:\n" + noisyText, noisyText.Length + 9, 121, [8])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunks;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contextual_text_entries;"));
        Assert.DoesNotContain(
            "iS) m =|",
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM retrieval_chunks LIMIT 1;"),
            StringComparison.Ordinal);

        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}");
        var retrievalQuality = processingMetadata.RootElement.GetProperty("retrievalChunkQuality");
        Assert.Equal(2, retrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("ocrNoiseRejectedChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("rejectionReasons").GetProperty("ocrNoise").GetInt32());

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='retrieval_chunks';");
        using var artifactMetadata = JsonDocument.Parse(artifactPayload!);
        Assert.Equal(1, artifactMetadata.RootElement.GetProperty("count").GetInt32());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("{}")]
    [InlineData("{\"quality\":{\"existing\":\"preserved\"},\"traceTag\":\"preserved\"}")]
    [InlineData("{\"quality\":null}")]
    [InlineData("{\"quality\":7}")]
    public async Task CompleteUpsertAsync_does_not_publish_when_no_searchable_chunks_exist(string initialPayload)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111113");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222225");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333337");
        const string docPath = "Generic/navigation-only.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using (var seedConnection = new NpgsqlConnection(db.ConnectionString))
        {
            await seedConnection.OpenAsync();
            await seedConnection.ExecuteAsync(
                "UPDATE ingestion_jobs SET payload=@initialPayload::jsonb WHERE job_id=@jobId;",
                new { initialPayload, jobId });
        }

        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 8, 36, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 36, 8, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "1 Introduction 3\n2 Scope 8\n3 Terms 12",
                8,
                [3],
                RetrievalContentClassifier.NavigationChunkType,
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationScore: 0.95,
                ContentDensityScore: 0.1)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 8],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, int indexed_version, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            "SELECT status, indexed_version, auto_ingest_paused, auto_ingest_pause_reason FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("error", document.status);
        Assert.Equal(0, document.indexed_version);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("manual_review_no_searchable_chunks", document.auto_ingest_pause_reason);

        var job = await conn.QuerySingleAsync<(string status, string last_error, string payload)>(
            "SELECT status, last_error, payload::text FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });
        Assert.Equal("failed", job.status);
        Assert.Equal("manual_review_no_searchable_chunks", job.last_error);

        using var payload = JsonDocument.Parse(job.payload);
        Assert.Equal("failed_no_searchable_chunks", payload.RootElement.GetProperty("progress").GetProperty("phase").GetString());
        using var originalPayload = JsonDocument.Parse(initialPayload);
        if (originalPayload.RootElement.TryGetProperty("traceTag", out var traceTag))
        {
            Assert.Equal(traceTag.GetString(), payload.RootElement.GetProperty("traceTag").GetString());
            Assert.Equal("preserved", payload.RootElement.GetProperty("quality").GetProperty("existing").GetString());
        }
        var retrievalQuality = payload.RootElement.GetProperty("quality").GetProperty("retrieval");
        Assert.Equal(1, retrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, retrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("navigationChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("rejectionReasons").GetProperty("navigationOnly").GetInt32());

        var processingRun = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version_before, int indexed_version_after, Guid? revision_id, string payload)>(
            """
            SELECT status, ingestion_version, indexed_version_before, indexed_version_after, revision_id, payload::text
            FROM document_processing_runs
            WHERE tenant_id=@tenant_id AND job_id=@job_id;
            """,
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal("failed", processingRun.status);
        Assert.Equal(1, processingRun.ingestion_version);
        Assert.Equal(0, processingRun.indexed_version_before);
        Assert.Equal(0, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);

        using var processingPayload = JsonDocument.Parse(processingRun.payload);
        Assert.False(processingPayload.RootElement.GetProperty("published").GetBoolean());
        Assert.False(processingPayload.RootElement.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("retrieval_quality_failure_v1", processingPayload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal("manual_review_no_searchable_chunks", processingPayload.RootElement.GetProperty("failureReason").GetString());
        var processingRetrievalQuality = processingPayload.RootElement.GetProperty("retrievalChunkQuality");
        Assert.Equal(1, processingRetrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, processingRetrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, processingRetrievalQuality.GetProperty("rejectionReasons").GetProperty("navigationOnly").GetInt32());
        var pageDiagnostics = processingPayload.RootElement.GetProperty("pageDiagnostics").EnumerateArray().ToArray();
        var firstPageDiagnostic = Assert.Single(pageDiagnostics);
        Assert.Equal(1, firstPageDiagnostic.GetProperty("unitCount").GetInt32());
        Assert.Equal(1, firstPageDiagnostic.GetProperty("chunkCount").GetInt32());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        var retrievalChunkCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM retrieval_chunks c
            JOIN document_revisions r ON r.revision_id=c.revision_id
            WHERE r.tenant_id=@tenant_id AND r.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
        Assert.Equal(0, retrievalChunkCount);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_preserves_previous_indexed_version_when_new_run_has_no_searchable_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111114");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222226");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333338");
        const string docPath = "Generic/previous-version-navigation-only.pdf";
        byte[] oldHash = [1, 2, 3, 4];
        byte[] newHash = [9, 9, 8, 8];
        var oldMtime = DateTime.UtcNow.AddDays(-3);
        var newMtime = DateTime.UtcNow;

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using (var seedConn = new NpgsqlConnection(db.ConnectionString))
        {
            await seedConn.OpenAsync();
            await seedConn.ExecuteAsync(
                """
                UPDATE documents
                SET status='indexed',
                    content_hash=@old_hash,
                    file_size=1234,
                    file_mtime=@old_mtime,
                    page_count=9
                WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
                """,
                new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    old_hash = oldHash,
                    old_mtime = DateTime.SpecifyKind(oldMtime, DateTimeKind.Utc)
                });
        }

        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 8, 36, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 36, 8, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "1 Introduction 3\n2 Scope 8\n3 Terms 12",
                8,
                [3],
                RetrievalContentClassifier.NavigationChunkType,
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationScore: 0.95,
                ContentDensityScore: 0.1)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            newHash,
            size: 9876,
            mtimeUtc: newMtime,
            version: 2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, string hash, long file_size, int ingestion_version, int indexed_version, int page_count, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            """
            SELECT status,
                   LOWER(ENCODE(content_hash, 'hex')) AS hash,
                   file_size,
                   ingestion_version,
                   indexed_version,
                   page_count,
                   auto_ingest_paused,
                   auto_ingest_pause_reason
            FROM documents
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal("indexed", document.status);
        Assert.Equal(Convert.ToHexString(oldHash).ToLowerInvariant(), document.hash);
        Assert.Equal(1234, document.file_size);
        Assert.Equal(2, document.ingestion_version);
        Assert.Equal(1, document.indexed_version);
        Assert.Equal(9, document.page_count);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("manual_review_no_searchable_chunks", document.auto_ingest_pause_reason);

        var processingRun = await conn.QuerySingleAsync<(string status, int indexed_version_before, int indexed_version_after, Guid? revision_id, string payload)>(
            """
            SELECT status, indexed_version_before, indexed_version_after, revision_id, payload::text
            FROM document_processing_runs
            WHERE tenant_id=@tenant_id AND job_id=@job_id;
            """,
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal("failed", processingRun.status);
        Assert.Equal(1, processingRun.indexed_version_before);
        Assert.Equal(1, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);

        using var processingPayload = JsonDocument.Parse(processingRun.payload);
        Assert.Equal("retrieval_quality_failure_v1", processingPayload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal(0, processingPayload.RootElement.GetProperty("retrievalChunkQuality").GetProperty("searchableChunkCount").GetInt32());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_job_version_when_indexed_version_has_gap()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-aaaa-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-bbbb-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-cccc-3333-3333-333333333333");
        const string docPath = "Generic/RetryGap.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Retry gap content preserves the original source across successful ingestion versions.", CountWords("Retry gap content preserves the original source across successful ingestion versions."), "Retry gap content preserves the original source across successful ingestion versions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Retry gap", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Retry gap content preserves the original source across successful ingestion versions.", "Retry gap content preserves the original source across successful ingestion versions.".Length, CountWords("Retry gap content preserves the original source across successful ingestion versions."), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Retry gap content preserves the original source across successful ingestion versions.", CountWords("Retry gap content preserves the original source across successful ingestion versions."), [3], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [5, 5, 5],
            size: 55,
            mtimeUtc: DateTime.UtcNow,
            version: 5,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var documentVersion = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM documents WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(5, documentVersion.ingestion_version);
        Assert.Equal(5, documentVersion.indexed_version);

        var revisionVersion = await conn.QuerySingleAsync<(Guid revision_id, int ingestion_version, int indexed_version)>(
            "SELECT revision_id, ingestion_version, indexed_version FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 5), revisionVersion.revision_id);
        Assert.Equal(5, revisionVersion.ingestion_version);
        Assert.Equal(5, revisionVersion.indexed_version);

        var processingRun = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version_before, int indexed_version_after)>(
            "SELECT ingestion_version, indexed_version_before, indexed_version_after FROM document_processing_runs WHERE tenant_id=@tenant_id AND job_id=@job_id;",
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal(5, processingRun.ingestion_version);
        Assert.Equal(3, processingRun.indexed_version_before);
        Assert.Equal(5, processingRun.indexed_version_after);

        var retrievalChunkId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT c.retrieval_chunk_id FROM retrieval_chunks c JOIN document_revisions r ON r.revision_id=c.revision_id WHERE c.tenant_id=@tenant_id AND r.doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0), retrievalChunkId);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_ocr_diagnostics_in_processing_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333334");
        const string docPath = "OCR/image-diagnostics.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Native text plus image label OCR PANEL", 7, 38, [1], ImageCount: 1),
            new ExtractedPdfPage(2, "Native text without applied OCR", 5, 31, [2], ImageCount: 1)
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "OCR diagnostics", 1, 1, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].CharCount, pages[0].WordCount, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, pages[1].Text, pages[1].CharCount, pages[1].WordCount, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 2, string.Join('\n', pages.Select(static page => page.Text)), 12, [5], "section_unit_v1")
        };
        var exactMatchEntries = ExactMatchEntryExtractor.Extract(units);
        var contextualTextEntries = ContextualTextProjector.Project(docPath, sections, units, retrievalChunks);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "image_page",
            CandidatePageCount: 7,
            AttemptedPageCount: 3,
            SkippedPageCount: 4,
            MaxPages: 3,
            CandidatePages: [1, 2, 5, 8, 13, 21, 34],
            AttemptedPages: [1, 13, 34],
            SkippedPages: [2, 5, 8, 21],
            PagesWithOcrText: [1, 13],
            PagesWithNovelText: [1],
            ImagePageDiagnostics:
            [
                new PdfImagePageOcrDiagnostic(1, "novel_text_applied", OcrWordCount: 6, OcrCharCount: 38, ExitCode: 0),
                new PdfImagePageOcrDiagnostic(13, "no_novel_text", "duplicate_or_below_threshold", OcrWordCount: 4, OcrCharCount: 24, ExitCode: 0),
                new PdfImagePageOcrDiagnostic(34, "render_failed", "exit_code_non_zero", ExitCode: 1),
                new PdfImagePageOcrDiagnostic(2, "skipped", "budget")
            ]);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None,
            extractionSource: "pdf_text_plus_image_ocr",
            ocrAttempted: true,
            ocrApplied: true,
            ocrLanguages: "eng",
            ocrDurationMs: 321,
            ocrDiagnostics: diagnostics, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}");
        var ocrDiagnostics = processingMetadata.RootElement.GetProperty("ocrDiagnostics");
        Assert.Equal("image_page", ocrDiagnostics.GetProperty("mode").GetString());
        Assert.Equal(7, ocrDiagnostics.GetProperty("candidatePageCount").GetInt32());
        Assert.Equal(3, ocrDiagnostics.GetProperty("attemptedPageCount").GetInt32());
        Assert.Equal(4, ocrDiagnostics.GetProperty("skippedPageCount").GetInt32());
        Assert.Equal([1, 13, 34], ocrDiagnostics.GetProperty("attemptedPages").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        Assert.Equal([1], ocrDiagnostics.GetProperty("pagesWithNovelText").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        var imagePageDiagnostics = ocrDiagnostics.GetProperty("imagePageDiagnostics").EnumerateArray().ToArray();
        Assert.Contains(imagePageDiagnostics, item =>
            item.GetProperty("pageNumber").GetInt32() == 1
            && item.GetProperty("status").GetString() == "novel_text_applied"
            && item.GetProperty("ocrWordCount").GetInt32() == 6);
        Assert.Contains(imagePageDiagnostics, item =>
            item.GetProperty("pageNumber").GetInt32() == 34
            && item.GetProperty("status").GetString() == "render_failed"
            && item.GetProperty("reason").GetString() == "exit_code_non_zero");

        var pageMetadataRows = (await conn.QueryAsync<(int page_number, string metadata)>(
            "SELECT page_number, metadata::text FROM document_page_index ORDER BY page_number;"))
            .ToDictionary(static row => row.page_number, static row => row.metadata);
        using (var pageOneMetadata = JsonDocument.Parse(pageMetadataRows[1]))
        {
            Assert.Equal("novel_text_applied", pageOneMetadata.RootElement.GetProperty("imageOcrStatus").GetString());
            Assert.Equal(6, pageOneMetadata.RootElement.GetProperty("imageOcrWordCount").GetInt32());
            Assert.Equal(38, pageOneMetadata.RootElement.GetProperty("imageOcrCharCount").GetInt32());
            Assert.Equal(0, pageOneMetadata.RootElement.GetProperty("imageOcrExitCode").GetInt32());
            Assert.False(pageOneMetadata.RootElement.GetProperty("imageOcrTimedOut").GetBoolean());
        }
        using (var pageTwoMetadata = JsonDocument.Parse(pageMetadataRows[2]))
        {
            Assert.Equal("skipped", pageTwoMetadata.RootElement.GetProperty("imageOcrStatus").GetString());
            Assert.Equal("budget", pageTwoMetadata.RootElement.GetProperty("imageOcrReason").GetString());
            Assert.True(pageTwoMetadata.RootElement.GetProperty("imageOcrWordCount").ValueKind is JsonValueKind.Null);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Extraction_quality_admin_endpoints_include_failed_non_indexable_ocr_documents()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111112");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222224");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333336");
        const string docPath = "OCR/scanned-ocr-disabled.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "ocr_disabled",
            CandidatePageCount: 1,
            AttemptedPageCount: 0,
            SkippedPageCount: 1,
            MaxPages: 0,
            CandidatePages: [1],
            AttemptedPages: [],
            SkippedPages: [1],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            FailureReason: "ocr_required_but_disabled",
            AppliedReason: "ocr_disabled");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [7, 7, 7],
            sourceSize: 321,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: false,
            ocrLanguages: null,
            ocrDurationMs: null,
            ocrDiagnostics: diagnostics,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "ocr_required_but_disabled",
            CancellationToken.None,
            extractedPages: [page], leaseOwner: "test-worker");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE document_processing_runs SET payload = payload - 'documentIndexable' WHERE tenant_id=@tenantId AND job_id=@jobId;",
                new { tenantId, jobId });
        }

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "OCR", null, 20);
        await listResult.ExecuteAsync(listCtx);

        Assert.Equal(StatusCodes.Status200OK, listCtx.Response.StatusCode);
        using (var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx)))
        {
            var summary = listPayload.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("totalDocuments").GetInt32());
            Assert.Equal(1, summary.GetProperty("emptyTextDocuments").GetInt32());
            Assert.Equal(1, summary.GetProperty("manualReviewRecommendedDocuments").GetInt32());
            Assert.Equal(0, summary.GetProperty("llmEnrichmentPendingDocuments").GetInt32());

            var item = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(docPath, item.GetProperty("docPath").GetString());
            Assert.Equal("error", item.GetProperty("documentStatus").GetString());
            Assert.Equal("failed", item.GetProperty("processingRunStatus").GetString());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("qualityStatus").GetString());
            Assert.False(item.GetProperty("documentIndexable").GetBoolean());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("failureReason").GetString());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("ocrFailureReason").GetString());
            Assert.True(item.GetProperty("manualReviewRecommended").GetBoolean());
            Assert.True(item.GetProperty("ocrRecommended").GetBoolean());
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.Equal("error", root.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", root.GetProperty("processingRunStatus").GetString());
        Assert.False(root.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("ocr_required_but_disabled", root.GetProperty("failureReason").GetString());
        Assert.Equal("failed_run", root.GetProperty("diagnosticScope").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("manualReviewRecommendedPages").GetInt32());
        var pageItem = Assert.Single(root.GetProperty("pages").EnumerateArray());
        Assert.Equal(1, pageItem.GetProperty("pageNumber").GetInt32());
        Assert.Equal("manual_review_empty_text", pageItem.GetProperty("qualityStatus").GetString());
        Assert.True(pageItem.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.True(pageItem.GetProperty("ocrCandidate").GetBoolean());
        Assert.Contains(
            pageItem.GetProperty("signals").EnumerateArray().Select(static item => item.GetString()),
            value => string.Equals(value, "no_chunks_on_page", StringComparison.Ordinal));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_strips_nul_characters_before_publishing_text_artifacts()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("10101010-1111-1111-1111-111111111111");
        var docId = Guid.Parse("20202020-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("30303030-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/NulText.pdf";
        const string rawText = "Intro\0 text with embedded nul";
        const string rawSection = "Intro\0 section";
        const string rawContext = "Document: NulText.pdf\nExcerpt:\nIntro\0 text with embedded nul";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: rawText.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, rawText, 5, rawText.Length, [1])],
            sections: [new ExtractedDocumentSection(0, rawSection, 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, rawText, rawText.Length, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, rawText, 5, [3], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, rawText, "intro\0 text", rawText.Length, 5, [4], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, rawContext, rawContext.Length, 8, [5])],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var storedValues = new[]
        {
            await conn.ExecuteScalarAsync<string>("SELECT title FROM document_sections LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM document_units LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM retrieval_chunks LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM exact_match_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT normalized_text FROM exact_match_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM contextual_text_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT summary_text FROM document_profiles LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT search_text FROM document_profiles LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT title FROM document_profile_content_cards LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT search_text FROM document_profile_content_cards LIMIT 1;")
        };

        foreach (var value in storedValues)
        {
            Assert.NotNull(value);
            Assert.DoesNotContain("\0", value!, StringComparison.Ordinal);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_keeps_profile_content_cards_active_per_document_profile()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("44444444-1111-1111-1111-111111111111");
        var docId = Guid.Parse("55555555-2222-2222-2222-222222222222");
        var firstJobId = Guid.Parse("66666666-3333-3333-3333-333333333333");
        var secondJobId = Guid.Parse("77777777-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/ActiveCards.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, firstJobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var firstCommitted = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            firstJobId,
            docPath,
            hash: [1, 1, 1],
            size: 64,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, "OLD ACTIVE CARD\nOld details.", 5, 28, [1])],
            sections: [new ExtractedDocumentSection(0, "Old active heading", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", 28, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", 5, [3], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", "old active card old details", 28, 5, [4], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: ActiveCards.pdf\n\nExcerpt:\nOLD ACTIVE CARD\nOld details.", 62, 8, [5])],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(firstCommitted);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(
            @"UPDATE documents
              SET status='pending',
                  ingestion_version=2,
                  indexed_version=1,
                  updated_at=now()
              WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });

        var payload = JsonSerializer.Serialize(new { });
        await conn.ExecuteAsync(
            @"INSERT INTO ingestion_jobs(
                  job_id, tenant_id, action, doc_path, category, status, attempts,
                  locked_by, locked_at, available_at, created_at, started_at, payload)
              VALUES(
                  @job_id, @tenant_id, 'upsert', @doc_path, 'Cuisine', 'running', 1,
                  'test-worker', now(), now(), now(), now(), CAST(@payload AS jsonb));",
            new { job_id = secondJobId, tenant_id = tenantId, doc_path = docPath, payload });

        var secondCommitted = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            secondJobId,
            docPath,
            hash: [2, 2, 2],
            size: 72,
            mtimeUtc: DateTime.UtcNow,
            version: 2,
            pages: [new ExtractedPdfPage(1, "NEW ACTIVE CARD\nFresh details.", 5, 30, [6])],
            sections: [new ExtractedDocumentSection(0, "New active heading", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", 30, 5, [7])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", 5, [8], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", "new active card fresh details", 30, 5, [9], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: ActiveCards.pdf\n\nExcerpt:\nNEW ACTIVE CARD\nFresh details.", 64, 8, [10])],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(secondCommitted);

        var profileCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profiles;");
        var cardRows = (await conn.QueryAsync<(string title, Guid revision_id)>(
            "SELECT title, revision_id FROM document_profile_content_cards ORDER BY title;")).ToArray();

        Assert.Equal(2, profileCount);
        var currentRevision = await conn.QuerySingleAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=2;",
            new { tenant = tenantId, docId });
        var currentCards = cardRows.Where(row => row.revision_id == currentRevision).ToArray();
        Assert.Contains(currentCards, row => string.Equals(row.title, "New active heading", StringComparison.Ordinal));
        Assert.DoesNotContain(currentCards, row => row.title.Contains("OLD", StringComparison.OrdinalIgnoreCase));
        // Published revisions keep their own cards. Updating one profile must
        // not purge cards owned by the earlier revision's profile.
        Assert.Contains(cardRows, row => row.revision_id != currentRevision && row.title == "Old active heading");
        Assert.Equal(2, cardRows.Select(row => row.revision_id).Distinct().Count());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_merges_capability_a_seed_without_unproved_content_cards()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("88888888-1111-1111-1111-888888888888");
        var docId = Guid.Parse("99999999-2222-2222-2222-888888888888");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-888888888888");
        const string docPath = "Generic/CapabilityASeed.pdf";
        const string text = "Generic extracted text for a baseline document.";
        var seed = new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: ["When should the pressure envelope validation be reviewed?"],
            SuggestedTags: ["pressure-envelope", "validation-review"],
            KeySectionTitles: ["Pressure envelope validation"],
            PreviewText: "The preview explains pressure envelope validation and review ownership.",
            BasedOnIndexedVersion: 0);

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Baseline", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            ct: CancellationToken.None,
            capabilityAProfileSeed: seed, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var profile = await conn.QuerySingleAsync<(string summary_text, string search_text, string[] keywords, string[] topics, string[] hypothetical_questions)>(
            """
            SELECT summary_text, search_text, keywords, topics, hypothetical_questions
            FROM document_profiles
            WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';
            """,
            new { tenant = tenantId, docId });
        var unprovedSeedCardCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_profile_content_cards
            WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1'
              AND normalized_title='pressure envelope validation';
            """,
            new { tenant = tenantId, docId });

        Assert.Contains("pressure envelope validation", profile.summary_text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pressure-envelope", profile.keywords);
        Assert.Contains("validation-review", profile.topics);
        Assert.Contains("When should the pressure envelope validation be reviewed?", profile.hypothetical_questions);
        Assert.Contains("pressure envelope validation", profile.search_text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unprovedSeedCardCount);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task EnqueueUpsertAsync_preserves_capability_a_seed_until_dequeue()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("88888888-1111-1111-1111-999999999999");
        const string docPath = "Generic/QueuedCapabilityASeed.pdf";
        var seed = new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: ["Which review owns the queued validation window?"],
            SuggestedTags: ["queued-validation", "profile-seed"],
            KeySectionTitles: ["Queued validation window"],
            PreviewText: "Queued preview for profile seed transport.",
            BasedOnIndexedVersion: 0);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'Test tenant');",
            new { tenant = tenantId });

        var queued = await IngestionEnqueue.EnqueueUpsertAsync(
            conn,
            tenantId,
            docPath,
            "Generic",
            fi: null,
            CancellationToken.None,
            enqueueSource: "capability_a",
            capabilityAProfileSeed: seed);

        var payloadJson = await conn.ExecuteScalarAsync<string>(
            """
            SELECT payload::text
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND job_id=@jobId;
            """,
            new { tenant = tenantId, jobId = queued.JobId });

        Assert.Contains("\"source\": \"capability_a\"", payloadJson);
        Assert.Contains("\"capabilityAProfileSeed\"", payloadJson);
        Assert.Contains("Which review owns the queued validation window?", payloadJson);
        Assert.Contains("queued-validation", payloadJson);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var job = await JobRepo.TryDequeueAsync(ds, "seed-worker", CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(queued.DocId, job!.DocId);
        Assert.Equal(queued.Version, job.Version);
        Assert.NotNull(job.CapabilityAProfileSeed);
        Assert.Equal(0, job.CapabilityAProfileSeed!.BasedOnIndexedVersion);
        Assert.Equal(["Which review owns the queued validation window?"], job.CapabilityAProfileSeed.HypotheticalQuestions);
        Assert.Equal(["queued-validation", "profile-seed"], job.CapabilityAProfileSeed.SuggestedTags);
        Assert.Equal(["Queued validation window"], job.CapabilityAProfileSeed.KeySectionTitles);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task EnqueueUpsertAsync_while_running_job_is_superseded_queues_current_version_after_commit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("88888888-2222-2222-2222-999999999999");
        var docId = Guid.Parse("99999999-3333-3333-3333-aaaaaaaaaaaa");
        var runningJobId = Guid.Parse("aaaaaaaa-4444-4444-4444-bbbbbbbbbbbb");
        const string docPath = "Generic/SupersededRunning.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, runningJobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var enqueued = await IngestionEnqueue.EnqueueUpsertAsync(
            conn,
            tenantId,
            docPath,
            "Generic",
            fi: null,
            CancellationToken.None,
            enqueueSource: "admin");

        Assert.Equal(runningJobId, enqueued.JobId);
        Assert.Equal(2, enqueued.Version);

        var runningPayloadVersion = await conn.ExecuteScalarAsync<string?>(
            "SELECT payload #>> '{version}' FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId = runningJobId });
        Assert.Null(runningPayloadVersion);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            runningJobId,
            docPath,
            hash: [1, 2, 3],
            size: 10,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [],
            sections: [],
            units: [],
            retrievalChunks: [],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.False(committed);

        var oldStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId = runningJobId });
        var followUp = await conn.QuerySingleAsync<(Guid job_id, string status, string? version, string? source)>(
            """
            SELECT job_id, status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldStatus);
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Delete_summary_endpoint_refreshes_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("88888888-1111-1111-1111-242424242424");
        var docId = Guid.Parse("99999999-2222-2222-2222-242424242424");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-242424242424");
        const string docPath = "Operations/DeleteSummaryProjection.pdf";
        const string text = "Generic operational baseline text.";
        const string summaryOnlyNeedle = "capstanquorinx vectraloz pyranelix";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: DeleteSummaryProjection.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO document_summaries(
                  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
                )
                SELECT
                  d.tenant_id,
                  d.doc_id,
                  'medium',
                  'en',
                  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version),
                  @summaryText,
                  '{}'::jsonb,
                  now(),
                  now()
                FROM documents d
                WHERE d.tenant_id=@tenant AND d.doc_id=@docId;
                """,
                new { tenant = tenantId, docId, summaryText = summaryOnlyNeedle });

            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });
            await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
                conn,
                null,
                tenantId,
                revisionId,
                CancellationToken.None);
        }

        var beforeDelete = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            summaryOnlyNeedle,
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        Assert.Contains(beforeDelete, match => match.DocPath == docPath);

        var deleteCtx = BuildAdminDocumentsHttpContext(tenantId);
        var deleteResult = await InvokeDeleteSummaryAsync(deleteCtx, ds, docId, "medium");
        await deleteResult.ExecuteAsync(deleteCtx);
        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);

        var afterDelete = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            summaryOnlyNeedle,
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        Assert.DoesNotContain(afterDelete, match => match.DocPath == docPath);
        await using var projectionConn = await ds.OpenConnectionAsync();
        var projection = await projectionConn.QuerySingleAsync<string>(
            "SELECT search_text FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
            new { tenant = tenantId, docId });
        Assert.DoesNotContain(summaryOnlyNeedle, projection, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteDeleteAsync_publishes_processing_run_without_revision()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
            CancellationToken.None, leaseOwner: "test-worker");

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
        Assert.Equal(3, docStatus.ingestion_version);
        Assert.Equal(0, docStatus.indexed_version);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task MarkFailedAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("12121212-1111-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-3333-3333-3333-333333333333");
        const string docPath = "General/Failure.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None, leaseOwner: "test-worker");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("failed", jobStatus);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task DeferTransientAsync_requeues_running_job_without_publishing_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("12121212-aaaa-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-bbbb-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-cccc-3333-3333-333333333333");
        const string docPath = "General/Deferred.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.DeferTransientAsync(ds, jobId, "OCR bulkhead: wait timeout after 1800s (max=1).", TimeSpan.FromSeconds(90), CancellationToken.None, leaseOwner: "test-worker");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var job = await conn.QuerySingleAsync<(string status, string last_error, string? locked_by, DateTimeOffset? locked_at, DateTimeOffset? started_at, DateTimeOffset? finished_at, DateTimeOffset available_at)>(
            "SELECT status, last_error, locked_by, locked_at, started_at, finished_at, available_at FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("queued", job.status);
        Assert.Equal("OCR bulkhead: wait timeout after 1800s (max=1).", job.last_error);
        Assert.Null(job.locked_by);
        Assert.Null(job.locked_at);
        Assert.Null(job.started_at);
        Assert.Null(job.finished_at);
        Assert.True(job.available_at > DateTimeOffset.UtcNow.AddSeconds(30));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task MarkFailedAsync_when_running_upsert_payload_version_is_superseded_queues_current_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("12121212-4444-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-5555-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-6666-3333-3333-333333333333");
        const string docPath = "General/SupersededFailure.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE ingestion_jobs
                SET payload=CAST(@payload AS jsonb)
                WHERE tenant_id=@tenant AND job_id=@jobId;
                """,
                new
                {
                    tenant = tenantId,
                    jobId,
                    payload = IngestionJobPayloadJson.Serialize(
                        docId,
                        version: 1,
                        source: "test",
                        indexedVersionBefore: 1)
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None, leaseOwner: "test-worker");

        await using var verifyConn = new NpgsqlConnection(db.ConnectionString);
        await verifyConn.OpenAsync();
        var oldJob = await verifyConn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId });
        var followUp = await verifyConn.QuerySingleAsync<(string status, string? version, string? source)>(
            """
            SELECT status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldJob.status);
        Assert.Equal("superseded_failed_before_commit", oldJob.last_error);
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task MarkFailedAsync_when_superseded_current_version_is_already_indexed_does_not_queue_redundant_follow_up()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("13131313-4444-1111-1111-111111111111");
        var docId = Guid.Parse("35353535-5555-2222-2222-222222222222");
        var jobId = Guid.Parse("57575757-6666-3333-3333-333333333333");
        const string docPath = "General/SupersededAlreadyIndexed.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 2);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET status='indexed'
                WHERE tenant_id=@tenant AND doc_path=@docPath;

                UPDATE ingestion_jobs
                SET payload=CAST(@payload AS jsonb)
                WHERE tenant_id=@tenant AND job_id=@jobId;
                """,
                new
                {
                    tenant = tenantId,
                    docPath,
                    jobId,
                    payload = IngestionJobPayloadJson.Serialize(
                        docId,
                        version: 1,
                        source: "test",
                        indexedVersionBefore: 1)
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None, leaseOwner: "test-worker");

        await using var verifyConn = new NpgsqlConnection(db.ConnectionString);
        await verifyConn.OpenAsync();
        var oldJob = await verifyConn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId });
        var queuedFollowUps = await verifyConn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldJob.status);
        Assert.Equal("superseded_failed_before_commit", oldJob.last_error);
        Assert.Equal(0, queuedFollowUps);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task MarkCanceledAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("78787878-1111-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-3333-3333-3333-333333333333");
        const string docPath = "General/Canceled.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 2);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkCanceledAsync(ds, jobId, "canceled_by_admin_test", CancellationToken.None, leaseOwner: "test-worker");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("canceled", jobStatus);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task MarkCanceledAsync_keeps_published_revision_and_monotone_ingestion_generation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("78787878-1212-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-3434-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-5656-3333-3333-333333333333");
        const string docPath = "General/CanceledIndexed.pdf";

        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), docPath, 3,
            "Control", "The published instrument control remains available after an unrelated ingestion attempt is canceled.",
            "Published instrument control with its source citation.");
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        const string currentRevisionSql = """
SELECT r.revision_id FROM documents d JOIN document_revisions r
ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version
WHERE d.tenant_id=@tenant AND d.doc_path=@docPath;
""";
        var publishedRevision = await conn.ExecuteScalarAsync<Guid>(currentRevisionSql, new { tenant = tenantId, docPath });
        await conn.ExecuteAsync(
            "UPDATE documents SET status='indexed' WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkCanceledAsync(ds, jobId, "canceled_by_admin_fixture", CancellationToken.None,
            leaseOwner: "test-worker", stabilizeDocument: true);

        var document = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version, bool auto_ingest_paused)>(
            "SELECT status, ingestion_version, indexed_version, auto_ingest_paused FROM documents WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        Assert.Equal("indexed", document.status);
        Assert.Equal(5, document.ingestion_version);
        Assert.Equal(3, document.indexed_version);
        Assert.False(document.auto_ingest_paused);
        Assert.Equal(publishedRevision, await conn.ExecuteScalarAsync<Guid>(currentRevisionSql, new { tenant = tenantId, docPath }));
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 3), publishedRevision);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant = tenantId, doc = docId }));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchExactMatchesAsync_returns_active_exact_match_hits_before_dense_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdcdcdcd-1111-1111-1111-111111111111");
        var docId = Guid.Parse("efefefef-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("ababcdcd-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "EN 15281 applies to the documented safety procedure and its operating conditions.",
                "EN 15281 applies to the documented safety procedure and its operating conditions.".Length,
                CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."),
                [2],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [3], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref",
                OffsetStart: 0,
                OffsetEnd: "EN 15281".Length,
                ExtractionTextStatus: units[0].ExtractionTextStatus,
                ExtractionTextSparse: units[0].ExtractionTextSparse,
                ExtractionOcrCandidate: units[0].ExtractionOcrCandidate,
                ExtractionQualitySignals: units[0].ExtractionQualitySignals)
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
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(committed);

        var matches = await RetrievalChannels.SearchExactMatchesAsync(
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
        Assert.Equal(0, match.OffsetStart);
        Assert.Equal("EN 15281".Length, match.OffsetEnd);
        Assert.Equal(1, match.IngestionVersion);
        Assert.Equal("low_text", match.ExtractionTextStatus);
        Assert.True(match.ExtractionTextSparse);
        Assert.True(match.ExtractionOcrCandidate);
        Assert.Contains("sparse_text_on_page", match.ExtractionQualitySignals!);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchExactMatchesAsync_matches_quoted_heading_when_extraction_glues_following_text()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("aa55aa55-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bb66bb66-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cc77cc77-3333-3333-3333-333333333333");
        const string docPath = "Ops/Inspection.pdf";
        const string gluedHeading = "Safety valve inspectionThe inspection schedule starts here.";
        const string body = "Inspect the valve body, record the set pressure, verify the seal, confirm the technician name, capture the inspection date, and keep the signed checklist for audit.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(4, $"{gluedHeading}\n{body}", 24, gluedHeading.Length + body.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inspection", Level: 1, PageStart: 4, PageEnd: 4, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 4, 4, gluedHeading, gluedHeading.Length, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 4, 4, body, body.Length, 17, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 4, 4, gluedHeading, 7, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 4, 4, body, 17, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 4, 4, gluedHeading, ExactMatchEntryExtractor.NormalizeForLookup(gluedHeading), gluedHeading.Length, 7, [6], "verbatim_excerpt"),
            new ExtractedExactMatchEntry(1, 0, 0, 4, 4, gluedHeading, ExactMatchEntryExtractor.NormalizeForLookup(gluedHeading), gluedHeading.Length, 7, [7], "verbatim_excerpt")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 4, 4, $"Document: Inspection.pdf\nExcerpt:\n{gluedHeading}\n{body}", 180, 25, [8])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            64,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None, leaseOwner: "test-worker"));

        var matches = await RetrievalChannels.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me a clear sheet for \"Safety valve inspection\".",
            "ops",
            null,
            null,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(4, match.PageStart);
        Assert.Contains("Safety valve inspectionThe inspection schedule starts here.", match.Text);
        Assert.Contains("Inspect the valve body", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.InRange(match.Score, 0.96, 0.969);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchExactMatchesAsync_matches_short_heading_inside_glued_extraction_excerpt()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("da55da55-1111-1111-1111-111111111111");
        var docId = Guid.Parse("eb66eb66-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("fc77fc77-3333-3333-3333-333333333333");
        const string docPath = "Ops/Maintenance.pdf";
        const string gluedExcerpt = "Allow 12 hours before start.Mode A JD1.";
        const string body = "Inspect the valve body, record the set pressure, verify the seal, confirm the technician name, capture the inspection date, and keep the signed checklist for audit.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(6, $"{gluedExcerpt}\n{body}", 24, gluedExcerpt.Length + body.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Maintenance", Level: 1, PageStart: 6, PageEnd: 6, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 6, 6, gluedExcerpt, gluedExcerpt.Length, 8, [2]),
            new ExtractedDocumentUnit(1, 0, 6, 6, body, body.Length, 17, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 6, 6, gluedExcerpt, 8, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 6, 6, body, 17, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 6, 6, gluedExcerpt, ExactMatchEntryExtractor.NormalizeForLookup(gluedExcerpt), gluedExcerpt.Length, 8, [6], "verbatim_excerpt")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 6, 6, $"Document: Maintenance.pdf\nExcerpt:\n{gluedExcerpt}\n{body}", 190, 25, [7])
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [7, 7, 7],
            64,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None, leaseOwner: "test-worker"));

        var matches = await RetrievalChannels.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me Mode A JD.",
            "ops",
            null,
            null,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(6, match.PageStart);
        Assert.Contains("Mode A JD1", match.Text);
        Assert.Contains("Inspect the valve body", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchExactMatchesAsync_falls_back_to_document_metadata_for_reference_visible_in_filename()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
            CancellationToken.None, leaseOwner: "test-worker"));

        var matches = await RetrievalChannels.SearchExactMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task RuntimeGovernance_batch_loaders_return_per_doc_titles_and_excerpts_without_cross_mix()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("01010101-1111-1111-1111-111111111111");
        var doc1Id = Guid.Parse("02020202-2222-2222-2222-222222222222");
        var doc2Id = Guid.Parse("03030303-3333-3333-3333-333333333333");
        var job1Id = Guid.Parse("04040404-4444-4444-4444-444444444444");
        var job2Id = Guid.Parse("05050505-5555-5555-5555-555555555555");

        const string doc1A = "Document one describes the initial inspection and records the measured operating pressure.";
        const string doc1B = "Document one lists the safety checks required before opening the isolation valve.";
        const string doc2A = "Document two introduces the maintenance schedule and identifies the responsible service team.";
        const string doc2B = "Document two provides the annex describing replacement intervals and required maintenance records.";
        await PublishIndexedDocumentAsync(db, tenantId, doc1Id, job1Id, "ATEX/Doc1.pdf", 1,
            [new RuntimeSeedSection("Doc1 Intro", doc1A), new RuntimeSeedSection("Doc1 Safety", doc1B)]);
        await PublishIndexedDocumentAsync(db, tenantId, doc2Id, job2Id, "ATEX/Doc2.pdf", 1,
            [new RuntimeSeedSection("Doc2 Overview", doc2A), new RuntimeSeedSection("Doc2 Annex", doc2B)]);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var docVersions = new[]
        {
            (doc1Id, 1),
            (doc2Id, 1)
        };

        var sectionTitlesByDocId = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 2,
            CancellationToken.None);
        var excerptsByDocId = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 2,
            CancellationToken.None);

        Assert.Equal(new[] { "Doc1 Intro", "Doc1 Safety" }, sectionTitlesByDocId[doc1Id]);
        Assert.Equal(new[] { "Doc2 Overview", "Doc2 Annex" }, sectionTitlesByDocId[doc2Id]);
        Assert.Equal(new[] { doc1A, doc1B }, excerptsByDocId[doc1Id]);
        Assert.Equal(new[] { doc2A, doc2B }, excerptsByDocId[doc2Id]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task RuntimeGovernance_excerpt_loaders_prefer_content_chunks_over_navigation_units()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("71717171-2222-4444-8888-111111111112");
        var docId = Guid.Parse("72727272-3333-5555-9999-222222222223");
        var jobId = Guid.Parse("73737373-4444-6666-aaaa-333333333334");
        const string docPath = "Knowledge/ChunkRoles.pdf";
        var navigation = "Contents Safety 3 Operation 8 Maintenance 12 Appendix 20";
        var firstContent = "The operating section explains setup validation and repeatable checks.";
        var secondContent = "The maintenance section explains inspection intervals and follow-up records.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3, 4, 5],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, navigation, 5, navigation.Length, [1]),
                new ExtractedPdfPage(2, firstContent, 10, firstContent.Length, [2]),
                new ExtractedPdfPage(3, secondContent, 10, secondContent.Length, [3])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Operation", Level: 1, PageStart: 2, PageEnd: 2, StartLine: 1, EndLine: null),
                new ExtractedDocumentSection(2, "Maintenance", Level: 1, PageStart: 3, PageEnd: 3, StartLine: 1, EndLine: null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, navigation, navigation.Length, 8, [4]),
                new ExtractedDocumentUnit(1, 1, 2, 2, firstContent, firstContent.Length, 9, [5]),
                new ExtractedDocumentUnit(2, 2, 3, 3, secondContent, secondContent.Length, 9, [6])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, navigation, 8, [7], "navigation_index_v1", ContentRole: RetrievalContentClassifier.NavigationRole, NavigationScore: 0.95, ContentDensityScore: 0.10),
                new ProjectedRetrievalChunk(1, 1, 1, 2, 2, firstContent, 9, [8], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.05, ContentDensityScore: 0.95),
                new ProjectedRetrievalChunk(2, 2, 2, 3, 3, secondContent, 9, [9], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.04, ContentDensityScore: 0.94)
            ],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var excerpts = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsAsync(
            conn,
            tenantId,
            docId,
            indexedVersion: 1,
            limit: 2,
            CancellationToken.None);
        var batch = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
            conn,
            tenantId,
            [(docId, 1)],
            limit: 2,
            CancellationToken.None);

        Assert.Equal([firstContent, secondContent], excerpts);
        Assert.Equal([firstContent, secondContent], batch[docId]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task LoadCapabilityBRepresentativeUnitExcerptsAsync_prefers_content_over_low_value_long_units()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("71717171-2222-4444-8888-111111111111");
        var docId = Guid.Parse("72727272-3333-5555-9999-222222222222");
        var jobId = Guid.Parse("73737373-4444-6666-aaaa-333333333333");
        const string docPath = "Knowledge/Guide.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var navigationList = "Safety overview 3 Operation details 8 Maintenance records 12 Validation checks 16 Follow-up actions 20";
        var lowValueCredits = "Writing and acknowledgements: editorial contributors, correction credits, copyright notices, ISBN details, and production thanks should not dominate the representative summary sample.";
        var firstContentExcerpt = "This guide explains how to plan weekly work, prepare reusable materials, and organize recurring tasks.";
        var lowValueReferences = "References: https://example.invalid/source-one https://example.invalid/source-two Long bibliography entry with many details that should not dominate the representative sample.";
        var secondContentExcerpt = "The practical section describes setup steps, validation checks, and follow-up actions for repeatable execution.";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3, 4],
            size: 123,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, firstContentExcerpt, 10, firstContentExcerpt.Length, [1]),
                new ExtractedPdfPage(2, secondContentExcerpt, 10, secondContentExcerpt.Length, [2])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Planning", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Execution", Level: 1, PageStart: 2, PageEnd: 2, StartLine: 1, EndLine: null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, navigationList, navigationList.Length, 20, [3]),
                new ExtractedDocumentUnit(1, 0, 1, 1, lowValueCredits, lowValueCredits.Length, 18, [4]),
                new ExtractedDocumentUnit(2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [5]),
                new ExtractedDocumentUnit(3, 1, 2, 2, lowValueReferences, lowValueReferences.Length, 18, [6]),
                new ExtractedDocumentUnit(4, 1, 2, 2, secondContentExcerpt, secondContentExcerpt.Length, 12, [7])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, navigationList, 20, [8], "navigation_index_v1", ContentRole: RetrievalContentClassifier.NavigationRole, NavigationScore: 0.95, ContentDensityScore: 0.10),
                new ProjectedRetrievalChunk(1, 0, 2, 1, 1, firstContentExcerpt, 12, [9], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.05, ContentDensityScore: 0.95),
                new ProjectedRetrievalChunk(2, 1, 4, 2, 2, secondContentExcerpt, 12, [10], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.04, ContentDensityScore: 0.94)
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 2, 1, 1, firstContentExcerpt, firstContentExcerpt.ToLowerInvariant(), firstContentExcerpt.Length, 12, [10], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [11])
            ],
            CancellationToken.None, leaseOwner: "test-worker");

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var excerpts = await RuntimeGovernanceService.LoadCapabilityBRepresentativeUnitExcerptsAsync(
            conn,
            tenantId,
            docId,
            indexedVersion: 1,
            limit: 2,
            CancellationToken.None);

        Assert.Equal([firstContentExcerpt, secondContentExcerpt], excerpts);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchSparseMatchesAsync_returns_contextual_chunk_hits_for_business_query()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
            CancellationToken.None, leaseOwner: "test-worker"));

        long measuredSparseMs = -1;
        var matches = await RetrievalChannels.SearchSparseMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchSparseMatchesAsync_lexical_fallback_matches_accents_and_ligatures()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("74747474-1111-1111-1111-111111111111");
        var docId = Guid.Parse("75757575-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("76767676-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/AccentLigature.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        const string text = "Recette: Bœuf à l'aïoli épicé. Servir avec une crème brûlée.\nSalade \nde lentilles.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, text.Length, 12, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recettes accentuées", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 12, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 12, [3], "unit_exact_v1")
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
                $"Document: AccentLigature.pdf\nHeading Path: Recettes accentuées\nExcerpt:\n{text}",
                text.Length + 70,
                18,
                [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [6, 6, 6],
            66,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None, leaseOwner: "test-worker"));

        var matches = await RetrievalChannels.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "boeuf aioli epice creme brulee",
            "cuisine",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            _ => { });

        var match = Assert.Single(matches);
        Assert.Equal("sparse_bm25_v1", match.EmbeddingBasis);
        Assert.Contains("Bœuf", match.Text!, StringComparison.Ordinal);
        Assert.Contains("crème brûlée", match.Text!, StringComparison.Ordinal);

        var splitTitleMatches = await RetrievalChannels.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "salade de lentilles",
            "cuisine",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            _ => { });

        var splitTitleMatch = Assert.Single(splitTitleMatches);
        Assert.Contains("Salade", splitTitleMatch.Text!, StringComparison.Ordinal);
        Assert.Contains("lentilles", splitTitleMatch.Text!, StringComparison.Ordinal);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchSparseMatchesAsync_uses_profile_cards_from_non_preferred_profiles_as_recall_anchors()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("74747474-aaaa-bbbb-cccc-111111111111");
        var docId = Guid.Parse("75757575-aaaa-bbbb-cccc-222222222222");
        var jobId = Guid.Parse("76767676-aaaa-bbbb-cccc-333333333333");
        const string docPath = "Generic/ProfileCardRecall.pdf";
        const string hiddenCardPhrase = "hydraulic accumulator pressure envelope";
        const string sourceText = "The target page contains ordinary operational notes. The operator checks the equipment and records each maintenance action before the supervisor reviews the completed inspection record.";
        Assert.DoesNotContain(hiddenCardPhrase, sourceText, StringComparison.OrdinalIgnoreCase);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Maintenance inspection",
            sourceText,
            "Document: ProfileCardRecall.pdf\nHeading Path: Maintenance inspection\nExcerpt:\n" + sourceText);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

            var seedCardCount = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM document_profile_content_cards
                WHERE tenant_id=@tenant AND revision_id=@revision AND profile_version='deterministic_v1';
                """, new { tenant = tenantId, revision = revisionId });
            Assert.True(seedCardCount > 0, "The recall fixture has no deterministic profile card to update.");

            await conn.ExecuteAsync(
                """
                UPDATE document_profile_content_cards
                SET
                  title = 'Hydraulic accumulator pressure envelope',
                  normalized_title = 'hydraulic accumulator pressure envelope',
                  search_text = 'Hydraulic accumulator pressure envelope validation window',
                  page_start = 1,
                  page_end = 1,
                  signals = ARRAY['recall_anchor']::text[]
                WHERE tenant_id=@tenant
                  AND revision_id=@revision
                  AND profile_version='deterministic_v1';

                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'Short backoffice profile without content cards.',
                  ARRAY['generic']::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                  'Short backoffice profile without content cards.',
                  6, decode(repeat('42', 32), 'hex'), '{}'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId = Guid.Parse("78787878-aaaa-bbbb-cccc-444444444444")
                });
        }

        long measuredSparseMs = -1;
        var sparseMatches = await RetrievalChannels.SearchSparseMatchesAsync(
            ds,
            tenantId,
            hiddenCardPhrase,
            category: null,
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None,
            value => measuredSparseMs = value);

        var sparseMatch = Assert.Single(sparseMatches);
        Assert.Equal("sparse_bm25_v1", sparseMatch.EmbeddingBasis);
        Assert.Equal(docPath, sparseMatch.DocPath);
        Assert.Contains("ordinary operational notes", sparseMatch.Text!, StringComparison.Ordinal);
        Assert.Contains("Matched profile title", sparseMatch.EmbedText!, StringComparison.Ordinal);
        Assert.True(sparseMatch.Score > 0.0);
        Assert.True(measuredSparseMs >= 0);

        var profileMatches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            hiddenCardPhrase,
            category: null,
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None);

        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(profileMatch));
        Assert.Equal(docPath, profileMatch.DocPath);
        Assert.NotNull(profileMatch.MatchedContentCards);
        Assert.Contains(
            profileMatch.MatchedContentCards!,
            card => string.Equals(card.Title, "Hydraulic accumulator pressure envelope", StringComparison.Ordinal));
    }

    [Fact]
    public void AddRankedMatches_prefers_real_chunks_over_document_profile_when_slots_are_constrained()
    {
        var selected = new List<RagMatch>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = new RagMatch(
            Score: 0.99,
            DocId: "doc-1",
            DocPath: "Generic/ProfileCardRecall.pdf",
            DocName: "ProfileCardRecall.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: Guid.NewGuid().ToString(),
            ChunkIndex: -1,
            Text: "Document profile summary",
            IngestionVersion: 1,
            HashDoc: null,
            EmbedText: "Document profile summary",
            EmbeddingBasis: "document_profile_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Document profile",
            HeadingPath: "Document profile",
            ChunkType: "document_profile",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var chunk = new RagMatch(
            Score: 0.80,
            DocId: "doc-1",
            DocPath: "Generic/ProfileCardRecall.pdf",
            DocName: "ProfileCardRecall.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: Guid.NewGuid().ToString(),
            ChunkIndex: 0,
            Text: "Concrete chunk evidence",
            IngestionVersion: 1,
            HashDoc: null,
            EmbedText: "Concrete chunk evidence",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Operations",
            HeadingPath: "Operations",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var method = typeof(RetrievalSelection).GetMethod("AddRankedMatches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [selected, selectedKeys, new[] { profile, chunk }, 1, 0.0, 1, 1, 2, false, null]);

        var selectedMatch = Assert.Single(selected);
        Assert.Equal("Concrete chunk evidence", selectedMatch.Text);
        Assert.Equal("sparse_bm25", RetrievalNavigation.ResolveRetriever(selectedMatch));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_supports_exact_match_anchor_when_it_resolves_to_a_retrieval_chunk()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("10101010-1111-1111-1111-111111111111");
        var docId = Guid.Parse("20202020-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("30303030-3333-3333-3333-333333333333");
        const string docPath = "ATEX/ExactAnchor.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence."), "EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [5], "unit_exact_v1")
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
            CancellationToken.None, leaseOwner: "test-worker"));

        var exactMatches = await RetrievalChannels.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var exactAnchor = Assert.Single(exactMatches);
        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_falls_back_to_same_page_chunks_for_exact_heading_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("1111aaaa-2222-3333-4444-555555555555");
        var docId = Guid.Parse("2222bbbb-3333-4444-5555-666666666666");
        var jobId = Guid.Parse("3333cccc-4444-5555-6666-777777777777");
        const string docPath = "Ops/ExactHeading.pdf";
        const string title = "Safety valve inspectionThe inspection schedule starts here.";
        const string body = "Inspect the valve body, record the set pressure, and keep the signed checklist.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[] { new ExtractedPdfPage(4, $"{title}\n{body}", 18, title.Length + body.Length, [1]) };
        var sections = new[] { new ExtractedDocumentSection(0, "Inspection", Level: 1, PageStart: 4, PageEnd: 4, StartLine: 1, EndLine: null) };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 4, 4, title, title.Length, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 4, 4, body, body.Length, 13, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 4, 4, title, 7, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 4, 4, body, 13, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 4, 4, title, ExactMatchEntryExtractor.NormalizeForLookup(title), title.Length, 7, [6], "verbatim_excerpt")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [3, 3, 7],
            42,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker"));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM retrieval_chunk_links WHERE tenant_id=@tenant;", new { tenant = tenantId });
            await conn.ExecuteAsync(
                "UPDATE exact_match_entries SET unit_id=NULL, section_id=NULL WHERE tenant_id=@tenant AND text_content=@title;",
                new { tenant = tenantId, title });
        }

        var exactAnchor = Assert.Single(await RetrievalChannels.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me a clear sheet for \"Safety valve inspection\".",
            "ops",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None));

        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [exactAnchor],
            category: "ops",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var fallback = linkedMatches.FirstOrDefault(
            item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 1).ToString()
                    && item.Text == body
                    && item.EmbeddingBasis == "linked_context_v1");
        Assert.NotNull(fallback);
        Assert.Equal("linked_context_v1", fallback.ChunkType);
        Assert.Equal("content", fallback.ContentRole);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchExactMatchesAsync_honors_category_and_document_filters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var docAPages = new[] { new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [1]) };
        var docASections = new[] { new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null) };
        var docAUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [2]) };
        var docAChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [3], "unit_exact_v1") };
        var docAExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref") };
        var docAContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\nExcerpt:\nEN 15281", 34, 4, [5]) };

        var docBPages = new[] { new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [6]) };
        var docBSections = new[] { new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null) };
        var docBUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [7]) };
        var docBChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [8], "unit_exact_v1") };
        var docBExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [9], "standard_ref") };
        var docBContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: Guide.pdf\nExcerpt:\nEN 15281", 36, 4, [10]) };

        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobAId, docAPath, [1, 1, 1], 10, DateTime.UtcNow, 1, docAPages, docASections, docAUnits, docAChunks, docAExact, docAContext, CancellationToken.None, leaseOwner: "test-worker"));
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobBId, docBPath, [2, 2, 2], 10, DateTime.UtcNow, 1, docBPages, docBSections, docBUnits, docBChunks, docBExact, docBContext, CancellationToken.None, leaseOwner: "test-worker"));

        var byCategory = await RetrievalChannels.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            null,
            null,
            10,
            CancellationToken.None);

        var byDoc = await RetrievalChannels.SearchExactMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_honors_category_path_scope()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("aaaabbbb-1212-3434-5656-111111111111");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("ccccdddd-1212-3434-5656-222222222222"),
            Guid.Parse("11112222-1212-3434-5656-333333333333"),
            "ATEX/Guidance/CEN.pdf",
            1,
            "Guidance",
            "shared scope token from guidance document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from guidance document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("eeeeffff-1212-3434-5656-444444444444"),
            Guid.Parse("55556666-1212-3434-5656-555555555555"),
            "ATEX/Installation/CEN.pdf",
            1,
            "Installation",
            "shared scope token from installation document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from installation document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("9999aaaa-1212-3434-5656-666666666666"),
            Guid.Parse("77778888-1212-3434-5656-777777777777"),
            "Safety/Guidance/CEN.pdf",
            1,
            "Safety guidance",
            "shared scope token from safety document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from safety document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("shared scope token", CategoryPath: "ATEX/Guidance", TopK: 5));

        Assert.NotEmpty(response.Matches);
        Assert.All(response.Matches, match =>
            Assert.StartsWith("ATEX/Guidance/", match.DocPath ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(response.Matches, match =>
            string.Equals(match.DocPath, "ATEX/Installation/CEN.pdf", StringComparison.Ordinal));

        var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
            ds, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.True(built.Success);
        await using var conn = await ds.OpenConnectionAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        var refScoped = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("shared scope token", CategoryRef: $"cat_{displayOrder:000}", TopK: 5));

        Assert.NotEmpty(refScoped.Matches);
        Assert.All(refScoped.Matches, match =>
            Assert.StartsWith("ATEX/", match.DocPath ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(refScoped.Matches, match =>
            string.Equals(match.DocPath, "Safety/Guidance/CEN.pdf", StringComparison.Ordinal));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_degrades_dense_retriever_when_embedding_service_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("abcddcba-1212-3434-5656-111111111111");
        const string docPath = "Ops/DenseFallback.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-1212-3434-5656-222222222222"),
            Guid.Parse("abcddcba-1212-3434-5656-333333333333"),
            docPath,
            1,
            "Hydraulics",
            "Hydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.",
            "Document: DenseFallback.pdf\nExcerpt:\nHydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto("hydraulic accumulator pressure verification", Category: "ops", TopK: 5));

        Assert.Contains(response.Matches, match => string.Equals(match.DocPath, docPath, StringComparison.Ordinal));
        Assert.Contains("dense_qdrant", response.DegradedRetrievers ?? Array.Empty<string>());
        Assert.NotNull(response.DegradedRetrieverErrors);
        Assert.True(response.DegradedRetrieverErrors!.ContainsKey("dense_qdrant"));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("hydraulic accumulator pressure verification", false, "title_lookup_combined", "final_selected", null)]
    [InlineData("recorded gauge inspection", false, "sparse_bm25", "final_selected", "fusion_rrf")]
    [InlineData("hydraulic accumulator pressure verification", true, "canonical_sparse", "canonical_selected", "canonical_fusion")]
    public async Task SearchCoreAsync_with_admin_diagnostics_captures_bounded_retrieval_phases(
        string query, bool canonical, string retrievalPhase, string finalPhase, string? fusionPhase)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("abcddcba-4545-6767-8989-111111111111");
        const string docPath = "Ops/AdminDiagnostics.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-4545-6767-8989-222222222222"),
            Guid.Parse("abcddcba-4545-6767-8989-333333333333"),
            docPath,
            1,
            "Admin diagnostics",
            "Hydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.",
            "Document: AdminDiagnostics.pdf\nExcerpt:\nHydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto(
                query,
                Category: "ops",
                TopK: 5,
                IncludeDiagnostics: true,
                SourceBackedCanonical: canonical));

        Assert.NotEmpty(response.Matches);
        Assert.NotNull(response.Diagnostics);
        var diagnostics = response.Diagnostics!;
        Assert.Equal(query, diagnostics.Query);
        Assert.Equal(5, diagnostics.TopK);
        var phaseNames = diagnostics.Phases.Select(phase => phase.Name).ToArray();
        Assert.Contains(retrievalPhase, phaseNames);
        Assert.Contains(finalPhase, phaseNames);
        if (fusionPhase is not null)
            Assert.Contains(fusionPhase, phaseNames);
        else
        {
            // A title lookup may complete before the hybrid retrievers run.
            // Diagnostics must describe executed work, not fabricate phases.
            Assert.DoesNotContain("sparse_bm25", phaseNames);
            Assert.DoesNotContain("fusion_rrf", phaseNames);
        }
        Assert.All(diagnostics.Phases, static phase => Assert.True(phase.TopCandidates.Count <= 12));
        Assert.Equal(response.Matches.Count, diagnostics.Selection.Returned);
        Assert.Contains(diagnostics.Selection.Items, static item => string.Equals(item.DocPath, docPath, StringComparison.Ordinal));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_does_not_return_admin_diagnostics_from_public_endpoint()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("abcddcba-5656-7878-9090-111111111111");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-5656-7878-9090-222222222222"),
            Guid.Parse("abcddcba-5656-7878-9090-333333333333"),
            "Ops/PublicNoDiagnostics.pdf",
            1,
            "Public diagnostics guard",
            "Public endpoint retrieval diagnostics should stay hidden from normal search callers.",
            "Document: PublicNoDiagnostics.pdf\nExcerpt:\nPublic endpoint retrieval diagnostics should stay hidden from normal search callers.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto(
                "Public endpoint retrieval diagnostics",
                TopK: 3,
                IncludeDiagnostics: true));

        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(response);
        Assert.Null(response!.Diagnostics);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_returns_extraction_quality_for_rag_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("aaaabbbb-2323-4545-6767-111111111111");
        const string sourceText = "Amber instruments require calibration before each recorded daily measurement check.";
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("ccccdddd-2323-4545-6767-222222222222"),
            Guid.Parse("11112222-2323-4545-6767-333333333333"),
            "ATEX/CEN.pdf",
            1,
            "Calibration record",
            sourceText,
            "Document: CEN.pdf\nExcerpt:\n" + sourceText,
            [BuildExactMatchEntry(0, sourceText, sourceText.ToLowerInvariant(), "verbatim_excerpt")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto(sourceText, TopK: 3));

        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(response);

        Assert.True(response!.Items.Count > 0, ReadResponseBody(ctx));
        var documentItems = response.Items
            .Where(static match => string.Equals(
                match.DocPath,
                "ATEX/CEN.pdf",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item =>
        {
            Assert.NotNull(item.ExtractionQuality);
            var quality = item.ExtractionQuality!;
            Assert.Equal("pdf_text", quality.ExtractionSource);
            Assert.False(quality.OcrAttempted);
            Assert.False(quality.OcrApplied);
            Assert.Equal(
                "manual_review_low_text",
                quality.DocumentQualityStatus);
            Assert.Equal(0.35, quality.DocumentExtractionConfidence);
            Assert.True(quality.DocumentManualReviewRecommended);
            Assert.Equal("page_ok_low_value_text", quality.PageQualityStatus);
            Assert.False(quality.PageManualReviewRecommended);
            Assert.Equal("low_text", quality.TextStatus);
            Assert.True(quality.OcrRecommended);
            Assert.Contains("ocr_recommended", quality.Signals ?? []);
        });
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_retrieval_chunk_links_for_adjacent_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("1111aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("2222bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("3333cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/Links.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [5], "unit_exact_v1")
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
            CancellationToken.None, leaseOwner: "test-worker");

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var linkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunk_links;");
        Assert.Equal(3, linkCount);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_accepts_sparse_bm25_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-333333333333");
        const string docPath = "ATEX/SparseLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 4);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins.", CountWords("recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins."), "recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recipe", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "recipe title describes the preparation steps and their required checks.", "recipe title describes the preparation steps and their required checks.".Length, CountWords("recipe title describes the preparation steps and their required checks."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "ingredient list specifies the supplies needed before the preparation begins.", "ingredient list specifies the supplies needed before the preparation begins.".Length, CountWords("ingredient list specifies the supplies needed before the preparation begins."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "recipe title describes the preparation steps and their required checks.", CountWords("recipe title describes the preparation steps and their required checks."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "ingredient list specifies the supplies needed before the preparation begins.", CountWords("ingredient list specifies the supplies needed before the preparation begins."), [5], "section_window_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [7, 7, 7],
            65,
            DateTime.UtcNow,
            5,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker"));

        var sparseAnchor = new RagMatch(
            Score: 0.90,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SparseLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0).ToString(),
            ChunkIndex: 0,
            Text: "recipe title describes the preparation steps and their required checks.",
            IngestionVersion: 5,
            HashDoc: "hash",
            EmbedText: "recipe title describes the preparation steps and their required checks.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Recipe",
            HeadingPath: "Recipe",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString());

        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [sparseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("section_window_v1", linked.ChunkType);
        Assert.True(linked.Score < sparseAnchor.Score);
        Assert.Equal(0.875, linked.Score, 3);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_recovers_inverse_same_section_companion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("4444aaaa-7777-1111-2222-111111111111");
        var docId = Guid.Parse("5555bbbb-8888-2222-3333-222222222222");
        var jobId = Guid.Parse("6666cccc-9999-3333-4444-333333333333");
        const string docPath = "ATEX/InverseSameSection.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 8, indexedVersion: 7);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "The inspection sheet identifies the equipment and records its operating conditions.", CountWords("The inspection sheet identifies the equipment and records its operating conditions."), "The inspection sheet identifies the equipment and records its operating conditions.".Length, [1]),
            new ExtractedPdfPage(2, "The preparation details specify the required materials and the order of inspection steps.", CountWords("The preparation details specify the required materials and the order of inspection steps."), "The preparation details specify the required materials and the order of inspection steps.".Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inspection Sheet", Level: 1, PageStart: 1, PageEnd: 2, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "The inspection sheet identifies the equipment and records its operating conditions.", "The inspection sheet identifies the equipment and records its operating conditions.".Length, CountWords("The inspection sheet identifies the equipment and records its operating conditions."), [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "The preparation details specify the required materials and the order of inspection steps.", "The preparation details specify the required materials and the order of inspection steps.".Length, CountWords("The preparation details specify the required materials and the order of inspection steps."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "The inspection sheet identifies the equipment and records its operating conditions.", CountWords("The inspection sheet identifies the equipment and records its operating conditions."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 2, 2, "The preparation details specify the required materials and the order of inspection steps.", CountWords("The preparation details specify the required materials and the order of inspection steps."), [6], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            80,
            DateTime.UtcNow,
            8,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker"));

        var anchorChunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 0);
        var companionChunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 1);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM retrieval_chunk_links WHERE tenant_id=@tenantId;", new { tenantId });
            await conn.ExecuteAsync(
                """
                UPDATE retrieval_chunks
                SET metadata = COALESCE(metadata, '{}'::jsonb) - 'sameSectionChunkId' - 'nextChunkId' - 'prevChunkId'
                WHERE retrieval_chunk_id = @anchorChunkId;

                UPDATE retrieval_chunks
                SET metadata = jsonb_set(
                    COALESCE(metadata, '{}'::jsonb) - 'nextChunkId' - 'prevChunkId',
                    '{sameSectionChunkId}',
                    to_jsonb(CAST(@anchorChunkId AS text)),
                    true)
                WHERE retrieval_chunk_id = @companionChunkId;
                """,
                new { anchorChunkId, companionChunkId });
        }

        var sparseAnchor = new RagMatch(
            Score: 0.90,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "InverseSameSection.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: anchorChunkId.ToString(),
            ChunkIndex: 0,
            Text: "The inspection sheet identifies the equipment and records its operating conditions.",
            IngestionVersion: 8,
            HashDoc: "hash",
            EmbedText: "The inspection sheet identifies the equipment and records its operating conditions.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Inspection Sheet",
            HeadingPath: "Inspection Sheet",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [sparseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == companionChunkId.ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal(anchorChunkId.ToString(), linked.SameSectionChunkId);
        Assert.True(linked.Score < sparseAnchor.Score);
        Assert.Equal(0.875, linked.Score, 3);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_accepts_direct_title_token_route_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-222222222222");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-444444444444");
        const string docPath = "ATEX/DirectRouteLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 6, indexedVersion: 5);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion.", CountWords("alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion."), "alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta target body describes the valve inspection and its required measurements.", "alpha beta target body describes the valve inspection and its required measurements.".Length, CountWords("alpha beta target body describes the valve inspection and its required measurements."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "The follow up detail explains how to record the measurements and verify completion.", "The follow up detail explains how to record the measurements and verify completion.".Length, CountWords("The follow up detail explains how to record the measurements and verify completion."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta target body describes the valve inspection and its required measurements.", CountWords("alpha beta target body describes the valve inspection and its required measurements."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "The follow up detail explains how to record the measurements and verify completion.", CountWords("The follow up detail explains how to record the measurements and verify completion."), [5], "section_window_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            70,
            DateTime.UtcNow,
            6,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None, leaseOwner: "test-worker"));

        var routeAnchor = new RagMatch(
            Score: 0.91,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "DirectRouteLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta target body describes the valve inspection and its required measurements.",
            IngestionVersion: 6,
            HashDoc: "hash",
            EmbedText: "Matched direct_title_token_route: alpha beta target\nalpha beta target body describes the valve inspection and its required measurements.",
            EmbeddingBasis: "direct_title_token_route_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Procedure",
            HeadingPath: "Procedure",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString());

        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [routeAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("section_window_v1", linked.ChunkType);
        Assert.True(linked.Score < routeAnchor.Score);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resume_checkpoint_round_trips_embedding_model_and_input_format()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("5e5e0000-1111-2222-3333-444444444444");
        var docId = Guid.Parse("6f6f0000-2222-3333-4444-555555555555");
        var jobId = Guid.Parse("70700000-3333-4444-5555-666666666666");
        const string docPath = "Operations/Checkpoint.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await JobRepo.UpdateProgressAsync(
            ds,
            jobId,
            "embedding",
            current: 16,
            total: 32,
            details: new
            {
                heartbeat = 3,
                elapsedSeconds = 61,
                pageCount = 12,
                languages = "eng+fra",
                forceOcr = true
            },
            CancellationToken.None, leaseOwner: "test-worker");
        await JobRepo.StoreResumeCheckpointAsync(
            ds,
            jobId,
            "abc123",
            1234,
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            CancellationToken.None, "plan", leaseOwner: "test-worker");

        var checkpoint = await JobRepo.GetResumeCheckpointAsync(ds, jobId, CancellationToken.None);

        Assert.NotNull(checkpoint);
        Assert.Equal(16, checkpoint!.ProgressCurrent);
        Assert.Equal(32, checkpoint.ProgressTotal);
        Assert.Equal("abc123", checkpoint.SourceHash);
        Assert.Equal(1234, checkpoint.FileSize);
        Assert.Equal(32, checkpoint.ChunkTotal);
        Assert.Equal("intfloat/multilingual-e5-base", checkpoint.EmbeddingModel);
        Assert.Equal("e5_passage_v1", checkpoint.EmbeddingInputFormat);
        Assert.Equal("plan", checkpoint.VectorPlanFingerprint);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var payloadJson = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });
        using var payloadDoc = JsonDocument.Parse(payloadJson ?? "{}");
        var details = payloadDoc.RootElement.GetProperty("progress").GetProperty("details");
        Assert.Equal(3, details.GetProperty("heartbeat").GetInt32());
        Assert.Equal(61, details.GetProperty("elapsedSeconds").GetInt32());
        Assert.Equal(12, details.GetProperty("pageCount").GetInt32());
        Assert.Equal("eng+fra", details.GetProperty("languages").GetString());
        Assert.True(details.GetProperty("forceOcr").GetBoolean());

        Assert.True(IngestionVectorPlan.CanResume(
            checkpoint,
            "ABC123",
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1", "plan"));
        Assert.False(IngestionVectorPlan.CanResume(
            checkpoint,
            "abc123",
            32,
            "sentence-transformers/all-MiniLM-L6-v2",
            "raw_passage_v1", "plan"));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_returns_enriched_results_and_accepts_linked_context_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("4444aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/LinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 3, indexedVersion: 2);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta records the final verification and reporting instructions.", "epsilon zeta records the final verification and reporting instructions.".Length, CountWords("epsilon zeta records the final verification and reporting instructions."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta records the final verification and reporting instructions.", CountWords("epsilon zeta records the final verification and reporting instructions."), [7], "unit_exact_v1")
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
            CancellationToken.None, leaseOwner: "test-worker");

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
            Text: "alpha beta explains the first recorded operational step.",
            IngestionVersion: 3,
            HashDoc: "hash",
            EmbedText: "alpha beta explains the first recorded operational step.",
            EmbeddingBasis: "linked_context_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString());

        var linkedMatches = await RetrievalSelection.SearchLinkedMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchLinkedMatchesAsync_supports_real_second_wave_expansion_from_linked_results()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("7777aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("8888bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("9999cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/SecondWave.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta records the final verification and reporting instructions.", "epsilon zeta records the final verification and reporting instructions.".Length, CountWords("epsilon zeta records the final verification and reporting instructions."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta records the final verification and reporting instructions.", CountWords("epsilon zeta records the final verification and reporting instructions."), [7], "unit_exact_v1")
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
            CancellationToken.None, leaseOwner: "test-worker"));

        var denseAnchor = new RagMatch(
            Score: 0.82,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SecondWave.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta explains the first recorded operational step.",
            IngestionVersion: 4,
            HashDoc: "hash",
            EmbedText: "alpha beta explains the first recorded operational step.",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString());

        var firstWave = await RetrievalSelection.SearchLinkedMatchesAsync(
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

        var secondWave = await RetrievalSelection.SearchLinkedMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_returns_answer_with_caveat_for_customer_compliance_question_on_en_15281()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.NotEmpty(response.Matches);
        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(response.Matches, match => (match.DocPath ?? string.Empty).Contains("15281", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_uses_sparse_matches_for_cross_document_customer_selection_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);
        var docPaths = response.Matches.Select(m => m.DocPath ?? string.Empty).ToArray();

        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(docPaths, path => path.Contains("15281", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(docPaths, path => path.Contains("IND570", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_returns_ask_clarification_for_placeholder_standard_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.False(string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_returns_exact_ind570_document_for_manual_lookup()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var canonicalExactMatches = await CanonicalRetrieval.SearchSourceBackedCanonicalExactMatchesAsync(
            ds,
            tenantId,
            "Est ce que tu as le manuel du terminal IND570 ?",
            category: null,
            docId: null,
            docPath: null,
            topK: 3,
            CancellationToken.None);

        var canonicalExact = Assert.Single(canonicalExactMatches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(
            Guid.Parse("7777dddd-dddd-dddd-dddd-dddddddddddd"),
            1,
            0).ToString(), canonicalExact.ChunkId);
        Assert.Equal("exact_match_v1", canonicalExact.EmbeddingBasis);
        Assert.Contains("installation, configuration and diagnostics", canonicalExact.Text, StringComparison.Ordinal);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Est ce que tu as le manuel du terminal IND570 ?", TopK: 3));

        var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);
        Assert.NotEmpty(response.Matches);
        var first = response.Matches[0];

        await ExportRetrievalObservationAsync("named-manual-observation.json", new { response, guidance });
        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", $"{first.DocName} {first.DocPath}", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exact_match_v1", first.EmbeddingBasis);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_emits_retrieval_metrics_and_phase_activities()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [Fact(Skip = "Historical legacy-retrieval oracle: product paths use source-backed canonical retrieval and LLM-owned semantic decisions.")]
    public async Task SearchCoreAsync_runtime_ready_v5_cases_match_expected_behavior_and_primary_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

            var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);

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

    [Trait("Profile", "IntegrationPg")]
    [Fact(Skip = "Historical deterministic-writer oracle: semantic phrasing is validated by live LLM evaluation, not backend keyword rules.")]
    public async Task SearchCoreAsync_runtime_ready_writer_cases_keep_prudent_guidance_shape()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

            var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);

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
                    if (guidance.QualificationNote?.Contains(token, StringComparison.OrdinalIgnoreCase) != true)
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
                    if (guidance.ClarifyingQuestion?.Contains(token, StringComparison.OrdinalIgnoreCase) != true)
                        failures.Add($"{testCase.Name}: clarifying question should contain '{token}', got '{guidance.ClarifyingQuestion}'.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchCoreAsync_runtime_ready_customer_questions_keep_valid_sources_through_late_selection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("acacffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = new[]
        {
            new
            {
                Query = "Quelles normes de securite instrumentee sont citees autour de l'inertage dans ce document ?",
                ExpectedSection = "Normative references"
            },
            new
            {
                Query = "Avant de dire au client que son inertage respecte EN 15281, qu'est-ce qu'il faut lui demander ?",
                ExpectedSection = "Definitions"
            }
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            Assert.NotEmpty(response.Matches);
            Assert.Contains(response.Matches, match =>
                string.Equals(ResolveDocHint(match), "15281", StringComparison.Ordinal));
            Assert.Contains(response.Matches, match =>
                string.Equals(match.SectionTitle, testCase.ExpectedSection, StringComparison.OrdinalIgnoreCase));

            var guidance = RetrievalAnswerGuidance.BuildAnswerGuidance(response.Query, response.Matches);
            Assert.NotEqual("no_source_match", guidance.ResponseShape);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SnapshotAsync_returns_304_when_if_none_match_matches_snapshot_etag()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "INSERT INTO documents_catalog_summary(tenant_id, computed_at, total_docs) VALUES(@tenant, now(), 1);",
                new { tenant = tenantId });
        }

        var publishedContext = BuildRagHttpContext(tenantId);
        publishedContext.Request.Headers.IfNoneMatch = etag;
        var publishedResult = await InvokeDocumentsSnapshotAsync(publishedContext, ds);
        await publishedResult.ExecuteAsync(publishedContext);
        Assert.Equal(StatusCodes.Status200OK, publishedContext.Response.StatusCode);
        Assert.NotEqual(etag, publishedContext.Response.Headers.ETag.ToString());
        using var publishedPayload = JsonDocument.Parse(ReadResponseBody(publishedContext));
        Assert.Equal(1, publishedPayload.RootElement.GetProperty("totals").GetProperty("documents").GetInt32());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData("catalog_categories", "value")]
    [InlineData("catalog_documents", "value")]
    [InlineData("documents_stats", "totalDocuments")]
    [InlineData("catalog_stats", "totalDocuments")]
    public async Task Catalog_cache_endpoints_return_304_when_if_none_match_matches_etag(string endpoint, string expectedBodyToken)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdceffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ingestionOptions = Options.Create(new IngestionOptions
        {
            DocumentsRoot = AppContext.BaseDirectory
        });

        var firstContext = BuildRagHttpContext(tenantId);
        var firstResult = await InvokeCatalogCacheEndpointAsync(endpoint, firstContext, ds, ingestionOptions);
        await firstResult.ExecuteAsync(firstContext);

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Contains(expectedBodyToken, ReadResponseBody(firstContext), StringComparison.Ordinal);

        var secondContext = BuildRagHttpContext(tenantId);
        secondContext.Request.Headers.IfNoneMatch = etag;

        var secondResult = await InvokeCatalogCacheEndpointAsync(endpoint, secondContext, ds, ingestionOptions);
        await secondResult.ExecuteAsync(secondContext);

        Assert.Equal(etag, secondContext.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Rag_categories_merges_dynamic_indexed_categories_when_snapshot_is_partial()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdcf0000-ffff-ffff-ffff-ffffffffffff");
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("cdcf0001-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("cdcf0002-ffff-ffff-ffff-ffffffffffff"),
            "SnapshotOnly/Guide.pdf",
            1,
            "Guide",
            "Snapshot category source text.",
            "Document: Guide.pdf\nExcerpt:\nSnapshot category source text.");
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("cdcf0003-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("cdcf0004-ffff-ffff-ffff-ffffffffffff"),
            "DynamicOnly/Manual.pdf",
            1,
            "Manual",
            "Dynamic category source text.",
            "Document: Manual.pdf\nExcerpt:\nDynamic category source text.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                DELETE FROM documents_catalog_categories WHERE tenant_id=@tenant;
                INSERT INTO documents_catalog_categories(
                  tenant_id, path, name, display_order, doc_count, direct_doc_count, subfolder_count
                )
                VALUES (@tenant, 'SnapshotOnly', 'Snapshot Only', 10, 1, 1, 0);
                """,
                new { tenant = tenantId });
        }

        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagCategoriesAsync(ctx, ds);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var categories = JsonSerializer.Deserialize<string[]>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(categories);
        Assert.Contains("SnapshotOnly", categories!);
        Assert.Contains("DynamicOnly", categories);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Unified_documents_route_returns_user_safe_catalog_for_user_and_legacy_shape_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd0ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var tombstone = await conn.QuerySingleAsync<(Guid DocId, string DocPath)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
            ORDER BY doc_path ASC
            LIMIT 1;
            """,
            new { tenant = tenantId });
        await conn.ExecuteAsync(
            """
            UPDATE documents
            SET status='deleted', updated_at=now()
            WHERE tenant_id=@tenant AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = tombstone.DocId });
        var enriched = await SeedLegacyCatalogMetadataAsync(db, tenantId, tombstone.DocId);

        var userContext = BuildRagHttpContext(tenantId);
        var userResult = await InvokeUnifiedDocumentsListAsync(userContext, ds, limit: 10, offset: 0);
        await userResult.ExecuteAsync(userContext);
        var userBody = ReadResponseBody(userContext);

        Assert.Equal(StatusCodes.Status200OK, userContext.Response.StatusCode);
        Assert.Contains("CategoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("SourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("DocLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("ProfileLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("CategoryRef", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSize", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            var item = Assert.Single(
                userJson.RootElement.GetProperty("items").EnumerateArray(),
                entry => string.Equals(entry.GetProperty("DocPath").GetString(), enriched.DocPath, StringComparison.Ordinal));
            Assert.Equal(enriched.SourceHash, item.GetProperty("SourceHash").GetString());
            Assert.Equal("de", item.GetProperty("DocLanguage").GetString());
            Assert.Equal("de", item.GetProperty("ProfileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, item.GetProperty("CategoryRef").GetString());
        }

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsListAsync(adminContext, ds, limit: 10, offset: 0);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("FileSize", adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, adminBody, StringComparison.Ordinal);

        var deletedContext = BuildAdminDocumentsHttpContext(tenantId);
        var deletedResult = await InvokeUnifiedDocumentsListAsync(deletedContext, ds, limit: 10, offset: 0, status: "deleted");
        await deletedResult.ExecuteAsync(deletedContext);
        var deletedBody = ReadResponseBody(deletedContext);

        Assert.Equal(StatusCodes.Status200OK, deletedContext.Response.StatusCode);
        Assert.Contains(tombstone.DocPath, deletedBody, StringComparison.Ordinal);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Unified_document_detail_route_returns_user_safe_detail_for_user_and_admin_detail_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd1ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var enriched = await SeedLegacyCatalogMetadataAsync(db, tenantId);

        var userContext = BuildRagHttpContext(tenantId);
        var userResult = await InvokeUnifiedDocumentsGetAsync(userContext, ds, enriched.DocId);
        await userResult.ExecuteAsync(userContext);
        var userBody = ReadResponseBody(userContext);

        Assert.Equal(StatusCodes.Status200OK, userContext.Response.StatusCode);
        Assert.Contains("CategoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("SourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("DocLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("ProfileLanguage", userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            Assert.Equal(enriched.SourceHash, userJson.RootElement.GetProperty("SourceHash").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("DocLanguage").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("ProfileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, userJson.RootElement.GetProperty("CategoryRef").GetString());
        }
        Assert.DoesNotContain("ContentHash", userBody, StringComparison.Ordinal);

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsGetAsync(adminContext, ds, enriched.DocId);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("ContentHash", adminBody, StringComparison.Ordinal);
    }
}
