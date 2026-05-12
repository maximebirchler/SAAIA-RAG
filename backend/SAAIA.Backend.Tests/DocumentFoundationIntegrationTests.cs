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
using Xunit;
namespace SAAIA.Backend.Tests;

public sealed class DocumentFoundationIntegrationTests
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
        Assert.Equal(8, artifactCount);

        var revision = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM document_revisions LIMIT 1;");
        Assert.Equal(1, revision.ingestion_version);
        Assert.Equal(1, revision.indexed_version);

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='exact_match_entries';");
        Assert.Contains("\"count\":1", artifactPayload, StringComparison.Ordinal);
        Assert.Contains("\"hashBasis\":\"canonical_text_v1\"", artifactPayload, StringComparison.Ordinal);

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
            Assert.Equal("low_text", extractionQuality.GetProperty("textStatus").GetString());
            Assert.True(extractionQuality.GetProperty("ocrRecommended").GetBoolean());
        }

        var pageMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM document_page_index LIMIT 1;");
        using (var pageMetadata = JsonDocument.Parse(pageMetadataJson ?? "{}"))
        {
            Assert.Equal(2, pageMetadata.RootElement.GetProperty("wordCount").GetInt32());
            var extractionQuality = pageMetadata.RootElement.GetProperty("extractionQuality");
            Assert.Equal("page_extraction_quality_v2", extractionQuality.GetProperty("diagnosticVersion").GetString());
            Assert.Equal("page_ok_low_value_text", extractionQuality.GetProperty("qualityStatus").GetString());
            Assert.Equal(0.78, extractionQuality.GetProperty("extractionConfidence").GetDouble());
            Assert.False(extractionQuality.GetProperty("manualReviewRecommended").GetBoolean());
            Assert.Equal("low_text", extractionQuality.GetProperty("textStatus").GetString());
            Assert.True(extractionQuality.GetProperty("textSparse").GetBoolean());
            Assert.True(extractionQuality.GetProperty("ocrCandidate").GetBoolean());
            Assert.Equal(1, extractionQuality.GetProperty("unitCount").GetInt32());
            Assert.Equal(1, extractionQuality.GetProperty("chunkCount").GetInt32());
        }

        var profile = await conn.QuerySingleAsync<(string summary_text, string search_text, string[] keywords, string metadata)>(
            "SELECT summary_text, search_text, keywords, metadata::text FROM document_profiles LIMIT 1;");
        Assert.Contains("CEN.pdf", profile.summary_text, StringComparison.Ordinal);
        Assert.Contains("Intro text", profile.search_text, StringComparison.Ordinal);
        Assert.Contains("intro", profile.keywords);
        using (var profileMetadata = JsonDocument.Parse(profile.metadata))
        {
            Assert.Equal(1, profileMetadata.RootElement.GetProperty("contentCardCount").GetInt32());
            Assert.Contains(
                profileMetadata.RootElement.GetProperty("contentCards").EnumerateArray(),
                card => string.Equals(card.GetProperty("title").GetString(), "Intro text", StringComparison.Ordinal));
        }

        var profileCard = await conn.QuerySingleAsync<(string title, string search_text, string[] signals)>(
            "SELECT title, search_text, signals FROM document_profile_content_cards LIMIT 1;");
        Assert.Equal("Intro text", profileCard.title);
        Assert.Contains("Intro text", profileCard.search_text, StringComparison.Ordinal);
        Assert.Contains("intro", profileCard.signals);

        var profileMatches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "intro text",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(profileMatch));
        Assert.Contains("CEN.pdf", profileMatch.Text, StringComparison.Ordinal);

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
    public async Task CompleteUpsertAsync_publishes_job_version_when_indexed_version_has_gap()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-aaaa-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-bbbb-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-cccc-3333-3333-333333333333");
        const string docPath = "Generic/RetryGap.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Retry gap content", 3, 17, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Retry gap", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Retry gap content", 17, 3, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Retry gap content", 3, [3], "unit_exact_v1")
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
            CancellationToken.None);

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
            "SELECT retrieval_chunk_id FROM retrieval_chunks WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0), retrievalChunkId);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_ocr_diagnostics_in_processing_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            ocrDiagnostics: diagnostics);

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
    }

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_persists_diagnostics_and_non_indexable_document_state()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333335");
        const string docPath = "OCR/scanned-non-indexable.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "full_document",
            CandidatePageCount: 0,
            AttemptedPageCount: 0,
            SkippedPageCount: 0,
            MaxPages: 0,
            CandidatePages: [],
            AttemptedPages: [],
            SkippedPages: [],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            ExitCode: 2,
            TimedOut: false,
            TimeoutSeconds: 30,
            Stderr: PdfOcrTextExtractor.TruncateOcrProcessOutputForDiagnostics(new string('e', 5000)),
            FailureReason: "exit_code_non_zero",
            AppliedReason: "ocr_extraction_failed");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [9, 9, 9],
            sourceSize: 123,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 456,
            ocrDiagnostics: diagnostics,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None,
            extractedPages: [page]);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            "SELECT status, auto_ingest_paused, auto_ingest_pause_reason FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("error", document.status);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("scanned_pdf_not_indexable", document.auto_ingest_pause_reason);

        var run = await conn.QuerySingleAsync<(string status, string payload)>(
            "SELECT status, payload::text FROM document_processing_runs WHERE job_id=@job_id;",
            new { job_id = jobId });
        Assert.Equal("failed", run.status);
        using var payload = JsonDocument.Parse(run.payload);
        Assert.False(payload.RootElement.GetProperty("published").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("scanned_pdf_not_indexable", payload.RootElement.GetProperty("failureReason").GetString());
        Assert.Equal("extraction_failure_v1", payload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal("failed_run", payload.RootElement.GetProperty("diagnosticScope").GetString());

        var ocrDiagnostics = payload.RootElement.GetProperty("ocrDiagnostics");
        Assert.Equal("full_document", ocrDiagnostics.GetProperty("mode").GetString());
        Assert.Equal(2, ocrDiagnostics.GetProperty("exitCode").GetInt32());
        Assert.Equal(30, ocrDiagnostics.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("exit_code_non_zero", ocrDiagnostics.GetProperty("failureReason").GetString());
        Assert.Equal("ocr_extraction_failed", ocrDiagnostics.GetProperty("appliedReason").GetString());
        Assert.True(ocrDiagnostics.GetProperty("stderr").GetString()!.Length <= 4096);

        var pageDiagnostics = payload.RootElement.GetProperty("pageDiagnostics").EnumerateArray().ToArray();
        var pageDiagnostic = Assert.Single(pageDiagnostics);
        Assert.Equal(1, pageDiagnostic.GetProperty("pageNumber").GetInt32());
        Assert.Equal("manual_review_empty_text", pageDiagnostic.GetProperty("qualityStatus").GetString());
        Assert.True(pageDiagnostic.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.True(pageDiagnostic.GetProperty("ocrCandidate").GetBoolean());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        var pageIndexCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_page_index pi
            JOIN document_revisions r ON r.revision_id=pi.revision_id
            WHERE r.tenant_id=@tenant_id AND r.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
        Assert.Equal(0, pageIndexCount);
    }

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1212-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-3434-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-5656-3333-3333-333333333335");
        const string docPath = "OCR/stale-non-indexable.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var published = await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [9, 8, 7],
            sourceSize: 123,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 456,
            ocrDiagnostics: null,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version, bool auto_ingest_paused)>(
            "SELECT status, ingestion_version, indexed_version, auto_ingest_paused FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        var runCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_processing_runs WHERE job_id=@job_id;",
            new { job_id = jobId });
        var job = await conn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });

        Assert.Equal("pending", document.status);
        Assert.Equal(2, document.ingestion_version);
        Assert.Equal(0, document.indexed_version);
        Assert.False(document.auto_ingest_paused);
        Assert.False(published);
        Assert.Equal(0, runCount);
        Assert.Equal("canceled", job.status);
        Assert.Equal("superseded_failed_ocr_publish", job.last_error);

        var followUp = await conn.QuerySingleAsync<(string status, string? version, string? source)>(
            """
            SELECT status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant_id AND doc_path=@doc_path AND action='upsert' AND status='queued';
            """,
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoints_include_failed_non_indexable_ocr_documents()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            extractedPages: [page]);

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

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_keeps_existing_indexed_source_metadata()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-444444444444");
        var docId = Guid.Parse("22222222-2222-2222-2222-444444444444");
        var firstJobId = Guid.Parse("33333333-3333-3333-3333-444444444444");
        var failedJobId = Guid.Parse("33333333-3333-3333-3333-444444444445");
        const string docPath = "OCR/already-indexed.pdf";
        const string text = "Previously indexed text remains the published source.";
        var originalHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var failedHash = Enumerable.Repeat((byte)0x22, 32).ToArray();

        await db.SeedRunningJobAsync(tenantId, docId, firstJobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            firstJobId,
            docPath,
            originalHash,
            size: 1234,
            mtimeUtc: DateTime.UtcNow.AddDays(-1),
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Published", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET ingestion_version=2
                WHERE tenant_id=@tenant AND doc_id=@docId;

                INSERT INTO ingestion_jobs(
                  job_id, tenant_id, action, doc_path, category, status,
                  attempts, locked_by, locked_at, available_at, created_at, started_at, payload
                )
                VALUES(
                  @jobId, @tenant, 'upsert', @docPath, 'OCR', 'running',
                  1, 'test-worker', now(), now(), now(), now(), '{}'::jsonb
                );
                """,
                new { tenant = tenantId, docId, jobId = failedJobId, docPath });
        }

        var failedPage = new ExtractedPdfPage(1, "", 0, 0, [9], ImageCount: 1);
        var failedQuality = PdfExtractionQualitySummary.FromPages([failedPage]);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            failedJobId,
            docPath,
            sourceHash: failedHash,
            sourceSize: 9876,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 2,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 55,
            ocrDiagnostics: new PdfOcrDiagnostics(
                Mode: "full_document",
                CandidatePageCount: 1,
                AttemptedPageCount: 1,
                SkippedPageCount: 0,
                MaxPages: 1,
                CandidatePages: [1],
                AttemptedPages: [1],
                SkippedPages: [],
                PagesWithOcrText: [],
                PagesWithNovelText: [],
                FailureReason: "exit_code_non_zero",
                AppliedReason: "ocr_extraction_failed"),
            extractionQuality: failedQuality,
            nativeExtractionQuality: failedQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None,
            extractedPages: [failedPage]);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var document = await conn.QuerySingleAsync<(string hash, long file_size, int ingestion_version, int indexed_version, string status, bool auto_ingest_paused)>(
                """
                SELECT LOWER(ENCODE(content_hash, 'hex')) AS hash,
                       file_size,
                       ingestion_version,
                       indexed_version,
                       status,
                       auto_ingest_paused
                FROM documents
                WHERE tenant_id=@tenant AND doc_id=@docId;
                """,
                new { tenant = tenantId, docId });

            Assert.Equal(Convert.ToHexString(originalHash).ToLowerInvariant(), document.hash);
            Assert.Equal(1234, document.file_size);
            Assert.Equal(2, document.ingestion_version);
            Assert.Equal(1, document.indexed_version);
            Assert.Equal("indexed", document.status);
            Assert.True(document.auto_ingest_paused);
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.Equal("indexed", root.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", root.GetProperty("processingRunStatus").GetString());
        Assert.False(root.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("scanned_pdf_not_indexable", root.GetProperty("failureReason").GetString());
        Assert.Equal("failed_run", root.GetProperty("diagnosticScope").GetString());
        var publishedRevision = root.GetProperty("publishedRevision");
        Assert.True(publishedRevision.GetProperty("searchable").GetBoolean());
        Assert.Equal(1, publishedRevision.GetProperty("indexedVersion").GetInt32());
        var failedPageItem = Assert.Single(root.GetProperty("pages").EnumerateArray());
        Assert.Equal("manual_review_empty_text", failedPageItem.GetProperty("qualityStatus").GetString());
        Assert.True(failedPageItem.GetProperty("manualReviewRecommended").GetBoolean());
    }

    [Fact]
    public async Task CompleteUpsertAsync_strips_nul_characters_before_publishing_text_artifacts()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            CancellationToken.None);

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

    [Fact]
    public async Task UpsertDocumentProfileAsync_purges_legacy_cards_with_stale_profile_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("40404040-1111-1111-1111-111111111111");
        var docId = Guid.Parse("50505050-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("60606060-3333-3333-3333-333333333333");
        const string docPath = "Operations/LegacyCards.pdf";
        const string text = "LEGACY CARD TITLE\nFresh details.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 5, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Legacy card title", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 5, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionId = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 1);
        var documentProfileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "deterministic_v1");
        await conn.ExecuteAsync(
            """
            UPDATE document_profile_content_cards
            SET profile_version='legacy_profile_mismatch'
            WHERE document_profile_id=@documentProfileId;
            """,
            new { documentProfileId });

        const string searchText = "Updated legacy profile search text";
        var profile = new ProjectedDocumentProfile(
            ProfileVersion: "deterministic_v1",
            Language: "en",
            SummaryText: "Updated legacy profile summary.",
            Keywords: ["legacy"],
            Entities: [],
            Topics: [],
            HypotheticalQuestions: [],
            Limits: [],
            SearchText: searchText,
            TokenCount: 5,
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(searchText)),
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "LEGACY CARD TITLE",
                    PageStart: 1,
                    PageEnd: 1,
                    Kind: "section",
                    Signals: ["legacy"])
            ]);

        await DocumentFoundationRepo.UpsertDocumentProfileAsync(
            conn,
            tx: null,
            tenantId,
            docId,
            revisionId,
            profile,
            CancellationToken.None);

        var cardRows = (await conn.QueryAsync<(string profile_version, int card_index, string title)>(
            """
            SELECT profile_version, card_index, title
            FROM document_profile_content_cards
            WHERE document_profile_id=@documentProfileId
            ORDER BY card_index;
            """,
            new { documentProfileId })).ToArray();

        var card = Assert.Single(cardRows);
        Assert.Equal("deterministic_v1", card.profile_version);
        Assert.Equal(0, card.card_index);
        Assert.Equal("LEGACY CARD TITLE", card.title);
    }

    [Fact]
    public async Task CompleteUpsertAsync_keeps_profile_content_cards_active_per_document_profile()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            CancellationToken.None);

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
            CancellationToken.None);

        Assert.True(secondCommitted);

        var profileCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profiles;");
        var cardRows = (await conn.QueryAsync<(string title, Guid revision_id)>(
            "SELECT title, revision_id FROM document_profile_content_cards ORDER BY title;")).ToArray();

        Assert.Equal(2, profileCount);
        Assert.Contains(cardRows, row => string.Equals(row.title, "NEW ACTIVE CARD", StringComparison.Ordinal));
        Assert.DoesNotContain(cardRows, row => row.title.Contains("OLD", StringComparison.OrdinalIgnoreCase));
        Assert.Single(cardRows.Select(static row => row.revision_id).Distinct());
    }

    [Fact]
    public async Task CompleteUpsertAsync_merges_capability_a_seed_into_published_profile()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            capabilityAProfileSeed: seed);

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
        var card = await conn.QuerySingleAsync<(string title, string kind, string[] signals, string search_text)>(
            """
            SELECT title, kind, signals, search_text
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
        Assert.Equal("Pressure envelope validation", card.title);
        Assert.Equal("capability_a_hint", card.kind);
        Assert.Contains("pressure-envelope", card.signals);
        Assert.Contains("validation-review", card.search_text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueUpsertAsync_preserves_capability_a_seed_until_dequeue()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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

    [Fact]
    public async Task EnqueueUpsertAsync_while_running_job_is_superseded_queues_current_version_after_commit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            CancellationToken.None);

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

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_materialized_content_cards_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-111111111111");
        var docId = Guid.Parse("99999999-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333");
        const string docPath = "Operations/WeeklyPlan.pdf";
        const string text = "WEEKLY MAINTENANCE PLAN\nInspection schedule and spare-part checklist.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [3, 3, 3],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Weekly Maintenance Plan", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, $"Document: WeeklyPlan.pdf\nExcerpt:\n{text}", text.Length + 35, 10, [4])],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic administrative note.',
                search_text='generic administrative note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "weekly maintenance plan",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Equal(1, match.PageStart);
        Assert.Equal(1, match.PageEnd);
        Assert.Contains("Weekly Maintenance Plan", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_ignores_pruned_cards_left_only_in_profile_search_text()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-555555555555");
        var docId = Guid.Parse("99999999-2222-2222-2222-555555555555");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-666666666666");
        const string docPath = "Operations/CurrentManual.pdf";
        const string text = "Current operating note. Daily checks and operator handover.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [5, 5, 5],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Current operating note", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Current operating note.',
                search_text='retired phantom calibration',
                keywords=ARRAY[]::text[],
                entities=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;

            DELETE FROM document_profile_content_cards
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "retired phantom calibration",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_fresh_stored_summary_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-222222222222");
        var docId = Guid.Parse("99999999-2222-2222-2222-444444444444");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-444444444444");
        const string docPath = "Operations/StoredSummary.pdf";
        const string text = "Generic operational source text.";
        const string storedSummary = "The stored server summary explains orbital calibration cadence and night audit ownership.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 4, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Generic", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 4, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 4, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic administrative note.',
                search_text='generic administrative note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;

            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            SELECT
              d.tenant_id,
              d.doc_id,
              'medium',
              'en',
              saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version),
              @storedSummary,
              '{"strategy":"llm","runtimeCapabilityStatus":"selected"}'::jsonb,
              now(),
              now()
            FROM documents d
            WHERE d.tenant_id=@tenant_id AND d.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId, storedSummary });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "orbital calibration cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains("orbital calibration cadence", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orbital calibration cadence", match.EmbedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_hypothetical_questions_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-333333333333");
        var docId = Guid.Parse("99999999-2222-2222-2222-333333333333");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-555555555555");
        const string docPath = "Operations/Audit.pdf";
        const string text = "Operational control note. The document itself uses generic wording.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [4, 4, 4],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Operational note", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, $"Document: Audit.pdf\nExcerpt:\n{text}", text.Length + 31, 10, [4])],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic operational note.',
                search_text='generic operational note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY['When should the night audit cadence be reviewed?']::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "night audit cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_llm_profile_terms_and_cards_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-777777777777");
        var docId = Guid.Parse("99999999-2222-2222-2222-777777777777");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-777777777777");
        const string docPath = "Generic/LlmProfileRecall.pdf";
        const string text = "Ordinary operational page text without the distinctive enrichment phrase.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: LlmProfileRecall.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });
            var llmProfileId = Guid.Parse("bbbbbbbb-4444-4444-4444-777777777777");

            await conn.ExecuteAsync(
                """
                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'LLM profile: the document explains pressure envelope validation and maintenance governance.',
                  ARRAY['pressure envelope validation']::text[],
                  ARRAY['accumulator cluster']::text[],
                  ARRAY['maintenance governance']::text[],
                  ARRAY['When should the pressure envelope validation be reviewed?']::text[],
                  ARRAY['Use page chunks for exact thresholds.']::text[],
                  'LLM profile pressure envelope validation accumulator cluster maintenance governance reviewed',
                  10,
                  decode(repeat('47', 32), 'hex'),
                  '{}'::jsonb
                );

                INSERT INTO document_profile_content_cards(
                  content_card_id, tenant_id, document_profile_id, revision_id, doc_id, profile_version,
                  card_index, title, normalized_title, page_start, page_end, kind, signals,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @cardId, @tenant, @llmProfileId, @revision, @docId, 'llm_backoffice_v1',
                  0,
                  'Pressure envelope validation',
                  'pressure envelope validation',
                  1,
                  1,
                  'llm_content_card',
                  ARRAY['accumulator cluster','validation review']::text[],
                  'Pressure envelope validation accumulator cluster validation review',
                  7,
                  decode(repeat('48', 32), 'hex'),
                  '{"generatedBy":"llm_backoffice_v1"}'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId,
                    cardId = Guid.Parse("cccccccc-5555-5555-5555-777777777777")
                });
        }

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "pressure envelope validation",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains("LLM profile", match.Text, StringComparison.Ordinal);
        Assert.Contains("maintenance governance", match.EmbedText, StringComparison.OrdinalIgnoreCase);
        var card = Assert.Single(match.MatchedContentCards!);
        Assert.Equal("Pressure envelope validation", card.Title);
        Assert.Equal("llm_content_card", card.Kind);
        Assert.Contains("accumulator cluster", card.Signals ?? []);

        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto(
                Query: "pressure envelope validation",
                CategoryPath: "Generic",
                TopK: 2,
                Mode: "balanced"));
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(response);
        var item = Assert.Single(response!.Items, static candidate => candidate.DocPath == docPath);
        Assert.Equal("document_profile", item.Retriever);
        Assert.Equal("en", item.DocLanguage);
        Assert.Equal("en", item.ProfileLanguage);
        Assert.False(string.IsNullOrWhiteSpace(item.SourceHash));
        var itemCard = Assert.Single(item.MatchedContentCards!);
        Assert.Equal("Pressure envelope validation", itemCard.Title);
        Assert.Equal("llm_content_card", itemCard.Kind);
        Assert.NotNull(item.SelectionHints);
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
    public async Task MarkFailedAsync_when_running_upsert_payload_version_is_superseded_queues_current_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None);

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
    public async Task StabilizeDocumentAfterCancelAsync_reverts_indexed_document_to_published_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("78787878-1212-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-3434-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-5656-3333-3333-333333333333");
        const string docPath = "General/CanceledIndexed.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE documents SET status='indexed' WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.StabilizeDocumentAfterCancelAsync(ds, tenantId, docPath, CancellationToken.None);

        var document = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version, bool auto_ingest_paused)>(
            "SELECT status, ingestion_version, indexed_version, auto_ingest_paused FROM documents WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        Assert.Equal("indexed", document.status);
        Assert.Equal(3, document.ingestion_version);
        Assert.Equal(3, document.indexed_version);
        Assert.False(document.auto_ingest_paused);
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
    public async Task RuntimeGovernance_batch_loaders_return_per_doc_titles_and_excerpts_without_cross_mix()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("01010101-1111-1111-1111-111111111111");
        var doc1Id = Guid.Parse("02020202-2222-2222-2222-222222222222");
        var doc2Id = Guid.Parse("03030303-3333-3333-3333-333333333333");
        var job1Id = Guid.Parse("04040404-4444-4444-4444-444444444444");
        var job2Id = Guid.Parse("05050505-5555-5555-5555-555555555555");

        await db.SeedRunningJobAsync(tenantId, doc1Id, job1Id, "ATEX/Doc1.pdf", ingestionVersion: 1, indexedVersion: 0);
        await db.SeedRunningJobAsync(tenantId, doc2Id, job2Id, "ATEX/Doc2.pdf", ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            job1Id,
            "ATEX/Doc1.pdf",
            hash: [1, 2, 3],
            size: 100,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, "Doc1 page", 4, 20, [1])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Doc1 Intro", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Doc1 Safety", 1, 1, 1, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, "Doc1 excerpt A", 12, 3, [2]),
                new ExtractedDocumentUnit(1, 1, 1, 1, "Doc1 excerpt B", 12, 3, [3])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Doc1 excerpt A", 12, [4], "unit_exact_v1")
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "Doc1 excerpt A", "doc1 excerpt a", 12, 3, [5], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: Doc1.pdf\nExcerpt:\nDoc1 excerpt A", 40, 6, [6])
            ],
            CancellationToken.None);

        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            job2Id,
            "ATEX/Doc2.pdf",
            hash: [7, 8, 9],
            size: 100,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, "Doc2 page", 4, 20, [1])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Doc2 Overview", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Doc2 Annex", 1, 1, 1, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, "Doc2 excerpt A", 12, 3, [2]),
                new ExtractedDocumentUnit(1, 1, 1, 1, "Doc2 excerpt B", 12, 3, [3])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Doc2 excerpt A", 12, [4], "unit_exact_v1")
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "Doc2 excerpt A", "doc2 excerpt a", 12, 3, [5], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: Doc2.pdf\nExcerpt:\nDoc2 excerpt A", 40, 6, [6])
            ],
            CancellationToken.None);

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
        Assert.Equal(new[] { "Doc1 excerpt A", "Doc1 excerpt B" }, excerptsByDocId[doc1Id]);
        Assert.Equal(new[] { "Doc2 excerpt A", "Doc2 excerpt B" }, excerptsByDocId[doc2Id]);
    }

    [Fact]
    public async Task LoadCapabilityBRepresentativeUnitExcerptsAsync_prefers_content_over_low_value_long_units()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("71717171-2222-4444-8888-111111111111");
        var docId = Guid.Parse("72727272-3333-5555-9999-222222222222");
        var jobId = Guid.Parse("73737373-4444-6666-aaaa-333333333333");
        const string docPath = "Knowledge/Guide.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var lowValueTableOfContents = "Contents Chapter one ................................................................ 4 Chapter two ................................................................ 8 Chapter three ................................................................ 12";
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
                new ExtractedDocumentSection(1, "Execution", 2, 2, 1, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, lowValueTableOfContents, lowValueTableOfContents.Length, 20, [3]),
                new ExtractedDocumentUnit(1, 0, 1, 1, lowValueCredits, lowValueCredits.Length, 18, [4]),
                new ExtractedDocumentUnit(2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [5]),
                new ExtractedDocumentUnit(3, 1, 2, 2, lowValueReferences, lowValueReferences.Length, 18, [6]),
                new ExtractedDocumentUnit(4, 1, 2, 2, secondContentExcerpt, secondContentExcerpt.Length, 12, [7])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 2, 2, 1, 1, firstContentExcerpt, 12, [8], "unit_exact_v1"),
                new ProjectedRetrievalChunk(1, 4, 4, 2, 2, secondContentExcerpt, 12, [9], "unit_exact_v1")
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 2, 2, 1, 1, firstContentExcerpt, firstContentExcerpt.ToLowerInvariant(), firstContentExcerpt.Length, 12, [10], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [11])
            ],
            CancellationToken.None);

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
    public async Task SearchSparseMatchesAsync_lexical_fallback_matches_accents_and_ligatures()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            CancellationToken.None));

        var matches = await RagEndpoints.SearchSparseMatchesAsync(
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

        var splitTitleMatches = await RagEndpoints.SearchSparseMatchesAsync(
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

    [Fact]
    public async Task SearchSparseMatchesAsync_uses_profile_cards_from_non_preferred_profiles_as_recall_anchors()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("74747474-aaaa-bbbb-cccc-111111111111");
        var docId = Guid.Parse("75757575-aaaa-bbbb-cccc-222222222222");
        var jobId = Guid.Parse("76767676-aaaa-bbbb-cccc-333333333333");
        const string docPath = "Generic/ProfileCardRecall.pdf";
        const string hiddenCardPhrase = "hydraulic accumulator pressure envelope";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operations",
            "The target page contains ordinary operational notes without the distinctive card phrase.",
            "Document: ProfileCardRecall.pdf\nHeading Path: Operations\nExcerpt:\nThe target page contains ordinary operational notes without the distinctive card phrase.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

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
        var sparseMatches = await RagEndpoints.SearchSparseMatchesAsync(
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

        var profileMatches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            hiddenCardPhrase,
            category: null,
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None);

        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(profileMatch));
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

        var method = typeof(RagEndpoints).GetMethod("AddRankedMatches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [selected, selectedKeys, new[] { profile, chunk }, 1, 0.0, 1, 1, 2, false]);

        var selectedMatch = Assert.Single(selected);
        Assert.Equal("Concrete chunk evidence", selectedMatch.Text);
        Assert.Equal("sparse_bm25", RagEndpoints.ResolveRetriever(selectedMatch));
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
    public async Task SearchCoreAsync_honors_category_path_scope()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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

    [Fact]
    public async Task SearchAsync_returns_extraction_quality_for_rag_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaabbbb-2323-4545-6767-111111111111");
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("ccccdddd-2323-4545-6767-222222222222"),
            Guid.Parse("11112222-2323-4545-6767-333333333333"),
            "ATEX/CEN.pdf",
            1,
            "Introduction",
            "Intro text",
            "Document: CEN.pdf\nExcerpt:\nIntro text",
            [BuildExactMatchEntry(0, "Intro text", "intro text", "verbatim_excerpt")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Intro text", TopK: 3));

        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(response);

        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, "ATEX/CEN.pdf", StringComparison.Ordinal));
        Assert.NotNull(item.ExtractionQuality);
        var quality = item.ExtractionQuality!;
        Assert.Equal("pdf_text", quality.ExtractionSource);
        Assert.False(quality.OcrAttempted);
        Assert.False(quality.OcrApplied);
        Assert.Equal("manual_review_low_text", quality.DocumentQualityStatus);
        Assert.Equal(0.35, quality.DocumentExtractionConfidence);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.Equal("page_ok_low_value_text", quality.PageQualityStatus);
        Assert.False(quality.PageManualReviewRecommended);
        Assert.Equal("low_text", quality.TextStatus);
        Assert.True(quality.OcrRecommended);
        Assert.Contains("ocr_recommended", quality.Signals ?? []);
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
    public async Task SearchLinkedMatchesAsync_accepts_sparse_bm25_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-333333333333");
        const string docPath = "ATEX/SparseLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 4);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "recipe title preparation details ingredient list", 6, 45, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recipe", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "recipe title preparation details", 10, 3, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "ingredient list", 11, 2, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "recipe title preparation details", 3, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "ingredient list", 2, [5], "section_window_v1")
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
            CancellationToken.None));

        var sparseAnchor = new RagMatch(
            Score: 0.90,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SparseLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0).ToString(),
            ChunkIndex: 0,
            Text: "recipe title preparation details",
            IngestionVersion: 5,
            HashDoc: "hash",
            EmbedText: "recipe title preparation details",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Recipe",
            HeadingPath: "Recipe",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
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

    [Fact]
    public async Task SearchLinkedMatchesAsync_accepts_direct_title_token_route_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-222222222222");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-444444444444");
        const string docPath = "ATEX/DirectRouteLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 6, indexedVersion: 5);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta target body follow up detail", 6, 39, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta target body", 10, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "follow up detail", 11, 3, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta target body", 4, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "follow up detail", 3, [5], "section_window_v1")
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
            CancellationToken.None));

        var routeAnchor = new RagMatch(
            Score: 0.91,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "DirectRouteLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta target body",
            IngestionVersion: 6,
            HashDoc: "hash",
            EmbedText: "Matched direct_title_token_route: alpha beta target\nalpha beta target body",
            EmbeddingBasis: "direct_title_token_route_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Procedure",
            HeadingPath: "Procedure",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
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

    [Fact]
    public async Task SearchTitleAnchorRouteMatchesAsync_direct_route_handles_accents_and_ignores_navigation_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1212aaaa-7777-1111-1111-222222222222");
        var docId = Guid.Parse("3434bbbb-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("5656cccc-9999-3333-3333-444444444444");
        const string docPath = "ATEX/AccentRoute.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 7, indexedVersion: 6);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "ÉPICE DOUCE index entry ÉPICE DOUCE operating details", 7, 54, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "ÉPICE DOUCE index entry", 10, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "ÉPICE DOUCE operating details", 11, 4, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "ÉPICE DOUCE index entry",
                4,
                [4],
                "navigation_index_v1",
                ContentRole: "navigation",
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.95,
                ContentDensityScore: 0.10),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                1,
                1,
                "ÉPICE DOUCE operating details and concrete execution steps.",
                7,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.70)
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [9, 8, 7],
            80,
            DateTime.UtcNow,
            7,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            "Donne-moi le epice douce.",
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 7, 1).ToString(), match.ChunkId);
        Assert.Equal("direct_title_token_route_v1", match.EmbeddingBasis);
        Assert.Equal("direct_title_token_route", RagEndpoints.ResolveRetriever(match));
    }

    [Fact]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_content_card_full_title_lead_over_dense_partial_page_chunk()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1a1a0000-7777-1111-1111-222222222222");
        var docId = Guid.Parse("2b2b0000-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("3c3c0000-9999-3333-3333-444444444444");
        const string docPath = "Operations/ContentCardAnchorRoute.pdf";
        const string title = "Alpha Beta Control Matrix";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 8, indexedVersion: 7);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Alpha dense OCR fragment. Alpha Beta Control Matrix lead. Later Alpha Beta Control Matrix reference.", 13, 93, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Alpha dense OCR fragment.", 10, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Alpha Beta Control Matrix lead with the actionable body.", 11, 9, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Later reference before Alpha Beta Control Matrix appears again.", 12, 9, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Dense OCR partial page with Alpha only and unrelated high-density operational filler.",
                11,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.99),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                1,
                1,
                "Alpha Beta Control Matrix lead with the actionable body and concrete execution detail.",
                12,
                [6],
                "unit_exact_v1",
                ContentRole: "mixed_navigation_content",
                NavigationReason: "numeric_title_catalog",
                NavigationScore: 0.50,
                ContentDensityScore: 0.50),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                1,
                1,
                "Later reference text. Extra context first, then Alpha Beta Control Matrix appears after the lead.",
                12,
                [7],
                "unit_exact_v1",
                ContentDensityScore: 0.85)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [1, 2, 3],
            93,
            DateTime.UtcNow,
            8,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=8;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
INSERT INTO document_title_anchors(
    title_anchor_id,
    tenant_id,
    revision_id,
    doc_id,
    anchor_index,
    source_kind,
    source_ordinal,
    section_id,
    unit_id,
    retrieval_chunk_id,
    content_card_id,
    title,
    normalized_title,
    title_tokens,
    page_start,
    page_end,
    confidence,
    metadata)
VALUES(
    @anchorId,
    @tenant,
    @revisionId,
    @docId,
    100,
    'content_card',
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    @title,
    @normalizedTitle,
    @titleTokens,
    1,
    1,
    0.82,
    '{}'::jsonb);
""",
                new
                {
                    anchorId = Guid.Parse("4d4d0000-aaaa-4444-4444-555555555555"),
                    tenant = tenantId,
                    revisionId,
                    docId,
                    title,
                    normalizedTitle = TitleAnchorNormalizer.NormalizeTitle(title),
                    titleTokens = TitleAnchorNormalizer.BuildTitleTokens(title)
                });
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 1).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.Equal("title_anchor_route", RagEndpoints.ResolveRetriever(match));
        Assert.Contains(title, match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dense OCR partial", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_anchor_page_over_adjacent_partial_token_hit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1a1a0000-7777-5555-5555-222222222222");
        var docId = Guid.Parse("2b2b0000-8888-6666-6666-333333333333");
        var jobId = Guid.Parse("3c3c0000-9999-7777-7777-444444444444");
        const string docPath = "Operations/ContentCardAdjacentRoute.pdf";
        const string title = "Alpha Beta Control Matrix";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 9, indexedVersion: 8);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Alpha Beta footer continuation unrelated to the target body.", 8, 56, [1]),
            new ExtractedPdfPage(2, "Control Matrix operating body and the actionable content.", 8, 58, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 2, 1, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Alpha Beta footer continuation unrelated to the target body.", 10, 8, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "Control Matrix operating body and the actionable content.", 11, 8, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Alpha Beta adjacent footer with unrelated high-density operational filler.",
                10,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.99),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                2,
                2,
                "Control Matrix operating body and the actionable content for the anchored page.",
                11,
                [6],
                "unit_exact_v1",
                ContentDensityScore: 0.40)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [2, 4, 6],
            114,
            DateTime.UtcNow,
            9,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=9;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
INSERT INTO document_title_anchors(
    title_anchor_id,
    tenant_id,
    revision_id,
    doc_id,
    anchor_index,
    source_kind,
    source_ordinal,
    section_id,
    unit_id,
    retrieval_chunk_id,
    content_card_id,
    title,
    normalized_title,
    title_tokens,
    page_start,
    page_end,
    confidence,
    metadata)
VALUES(
    @anchorId,
    @tenant,
    @revisionId,
    @docId,
    101,
    'content_card',
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    @title,
    @normalizedTitle,
    @titleTokens,
    2,
    2,
    0.82,
    '{}'::jsonb);
""",
                new
                {
                    anchorId = Guid.Parse("4d4d0000-aaaa-8888-8888-555555555555"),
                    tenant = tenantId,
                    revisionId,
                    docId,
                    title,
                    normalizedTitle = TitleAnchorNormalizer.NormalizeTitle(title),
                    titleTokens = TitleAnchorNormalizer.BuildTitleTokens(title)
                });
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 9, 1).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.Contains("anchored page", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adjacent footer", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_checkpoint_round_trips_embedding_model_and_input_format()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("5e5e0000-1111-2222-3333-444444444444");
        var docId = Guid.Parse("6f6f0000-2222-3333-4444-555555555555");
        var jobId = Guid.Parse("70700000-3333-4444-5555-666666666666");
        const string docPath = "Operations/Checkpoint.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await JobRepo.UpdateProgressAsync(ds, jobId, "embedding", current: 16, total: 32, CancellationToken.None);
        await JobRepo.StoreResumeCheckpointAsync(
            ds,
            jobId,
            "abc123",
            1234,
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            CancellationToken.None);

        var checkpoint = await JobRepo.GetResumeCheckpointAsync(ds, jobId, CancellationToken.None);

        Assert.NotNull(checkpoint);
        Assert.Equal(16, checkpoint!.ProgressCurrent);
        Assert.Equal(32, checkpoint.ProgressTotal);
        Assert.Equal("abc123", checkpoint.SourceHash);
        Assert.Equal(1234, checkpoint.FileSize);
        Assert.Equal(32, checkpoint.ChunkTotal);
        Assert.Equal("intfloat/multilingual-e5-base", checkpoint.EmbeddingModel);
        Assert.Equal("e5_passage_v1", checkpoint.EmbeddingInputFormat);
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

    [Theory]
    [InlineData("catalog_categories", "value")]
    [InlineData("catalog_documents", "value")]
    [InlineData("documents_stats", "totalDocuments")]
    [InlineData("catalog_stats", "totalDocuments")]
    public async Task Catalog_cache_endpoints_return_304_when_if_none_match_matches_etag(string endpoint, string expectedBodyToken)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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

    [Fact]
    public async Task Rag_categories_merges_dynamic_indexed_categories_when_snapshot_is_partial()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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

    [Fact]
    public async Task Unified_documents_route_returns_user_safe_catalog_for_user_and_legacy_shape_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
        Assert.Contains("categoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("sourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("docLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("profileLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("categoryRef", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain("fileSize", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            var item = Assert.Single(
                userJson.RootElement.GetProperty("items").EnumerateArray(),
                entry => string.Equals(entry.GetProperty("docPath").GetString(), enriched.DocPath, StringComparison.Ordinal));
            Assert.Equal(enriched.SourceHash, item.GetProperty("sourceHash").GetString());
            Assert.Equal("de", item.GetProperty("docLanguage").GetString());
            Assert.Equal("de", item.GetProperty("profileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, item.GetProperty("categoryRef").GetString());
        }

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsListAsync(adminContext, ds, limit: 10, offset: 0);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("fileSize", adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, adminBody, StringComparison.Ordinal);

        var deletedContext = BuildAdminDocumentsHttpContext(tenantId);
        var deletedResult = await InvokeUnifiedDocumentsListAsync(deletedContext, ds, limit: 10, offset: 0, status: "deleted");
        await deletedResult.ExecuteAsync(deletedContext);
        var deletedBody = ReadResponseBody(deletedContext);

        Assert.Equal(StatusCodes.Status200OK, deletedContext.Response.StatusCode);
        Assert.Contains(tombstone.DocPath, deletedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unified_document_detail_route_returns_user_safe_detail_for_user_and_admin_detail_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd1ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var enriched = await SeedLegacyCatalogMetadataAsync(db, tenantId);

        var userContext = BuildRagHttpContext(tenantId);
        var userResult = await InvokeUnifiedDocumentsGetAsync(userContext, ds, enriched.DocId);
        await userResult.ExecuteAsync(userContext);
        var userBody = ReadResponseBody(userContext);

        Assert.Equal(StatusCodes.Status200OK, userContext.Response.StatusCode);
        Assert.Contains("categoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("sourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("docLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("profileLanguage", userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            Assert.Equal(enriched.SourceHash, userJson.RootElement.GetProperty("sourceHash").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("docLanguage").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("profileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, userJson.RootElement.GetProperty("categoryRef").GetString());
        }
        Assert.DoesNotContain("contentHash", userBody, StringComparison.Ordinal);

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsGetAsync(adminContext, ds, enriched.DocId);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("contentHash", adminBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_category_endpoint_resolves_category_ref_and_returns_aliases()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd2ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        await conn.ExecuteAsync(
            """
            INSERT INTO documents_catalog_category_aliases(
              tenant_id, path, alias, alias_key, language, source, priority
            )
            VALUES (@tenant, 'ATEX', @alias, @aliasKey, 'fr', 'test', 1)
            ON CONFLICT (tenant_id, path, alias_key)
            DO UPDATE SET alias = EXCLUDED.alias, language = EXCLUDED.language, source = EXCLUDED.source, priority = EXCLUDED.priority;
            """,
            new
            {
                tenant = tenantId,
                alias = "Atmospheres explosibles",
                aliasKey = "atmospheres explosibles"
            });

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveCategoryAsync(
            ctx,
            ds,
            new ResolveCategoryRequest
            {
                CategoryRef = $"cat_{displayOrder:000}"
            });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<ResolveCategoryResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(response!.Items);
        Assert.Equal($"cat_{displayOrder:000}", item.CategoryRef);
        Assert.Equal("ATEX", item.CategoryPath);
        Assert.Equal("ATEX", item.DisplayName);
        Assert.Equal(displayOrder, item.Ordinal);
        Assert.True(item.TotalDocuments > 0);
        Assert.Contains("Atmospheres explosibles", item.Aliases);
    }

    [Fact]
    public async Task Resolve_source_endpoint_matches_filename_reference_to_indexed_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd3ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, int page_count, string category, string category_path, string source_hash)>(
            """
            SELECT
              doc_id,
              doc_path,
              doc_name,
              page_count,
              category,
              CASE WHEN doc_path LIKE '%/%' THEN regexp_replace(doc_path, '/[^/]+$', '') ELSE '' END AS category_path,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });
        var enrichedCard = await conn.QuerySingleAsync<(string content_card_id, string title)>(
            """
            WITH selected AS (
              SELECT content_card_id
              FROM document_profile_content_cards
              WHERE tenant_id=@tenant
                AND doc_id=@docId
              ORDER BY card_index
              LIMIT 1
            )
            UPDATE document_profile_content_cards c
            SET metadata = jsonb_set(
              COALESCE(c.metadata, '{}'::jsonb),
              '{evidence}',
              @evidence::jsonb,
              true)
            FROM selected
            WHERE c.content_card_id = selected.content_card_id
            RETURNING c.content_card_id::text AS content_card_id, c.title;
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                evidence = """
                {
                  "schemaVersion": "source_resolve_test_v1",
                  "scaleBasis": { "label": "control basis", "value": 2, "unit": "units" },
                  "quantityFacts": [
                    { "label": "Control points", "value": 4, "unit": "checks" }
                  ],
                  "confidence": 0.88
                }
                """
            });

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest
            {
                PdfRef = expected.doc_name
            });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<SourceResolveResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        Assert.Equal(expected.doc_name, response.RequestedRef);
        Assert.NotNull(response.Source);
        Assert.Equal(expected.doc_id, response.Source!.DocId);
        Assert.Equal(expected.doc_path, response.Source.DocPath);
        Assert.Equal(expected.doc_name, response.Source.DocName);
        Assert.Equal(1, response.Source.PageStart);
        Assert.Equal(Math.Max(expected.page_count, 1), response.Source.PageEnd);
        Assert.Equal(expected.doc_name, response.Source.Label);
        Assert.Equal(expected.category, response.Source.Category);
        Assert.Equal(expected.category_path, response.Source.CategoryPath);
        Assert.Equal(expected.source_hash, response.Source.SourceHash);
        Assert.Equal("en", response.Source.DocLanguage);
        Assert.Equal("en", response.Source.ProfileLanguage);
        Assert.NotNull(response.Source.ExtractionQuality);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality!.DocumentQualityStatus);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality.PageQualityStatus);
        Assert.False(response.Source.ExtractionQuality.OcrAttempted);
        Assert.False(response.Source.ExtractionQuality.OcrApplied);
        Assert.NotNull(response.Source.MatchedContentCards);
        Assert.NotEmpty(response.Source.MatchedContentCards!);
        var resolvedCard = response.Source.MatchedContentCards!.Single(card =>
            string.Equals(card.ContentCardId, enrichedCard.content_card_id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(enrichedCard.title, resolvedCard.Title);
        Assert.True(resolvedCard.Evidence.HasValue);
        Assert.Equal("source_resolve_test_v1", resolvedCard.Evidence!.Value.GetProperty("schemaVersion").GetString());
        Assert.Equal("control basis", resolvedCard.Evidence.Value.GetProperty("scaleBasis").GetProperty("label").GetString());
        Assert.NotNull(response.Source.SelectionHints);
        Assert.Equal("supporting_context", response.Source.SelectionHints!.EvidenceRole);
        Assert.True(response.Source.SelectionHints.SupportScore > 0);

        var docRef = $"doc_{expected.doc_id:N}";
        var refCtx = BuildRagHttpContext(tenantId);
        var refResult = await InvokeResolveSourceAsync(
            refCtx,
            ds,
            new SourceResolveRequest
            {
                Ref = docRef
            });

        await refResult.ExecuteAsync(refCtx);

        var refResponse = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(refCtx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(refResponse);
        Assert.Null(refResponse!.Error);
        Assert.Equal(docRef, refResponse.RequestedRef);
        Assert.NotNull(refResponse.Source);
        Assert.Equal(expected.doc_id, refResponse.Source!.DocId);
        Assert.Equal(expected.source_hash, refResponse.Source.SourceHash);
        Assert.Contains(refResponse.Source.MatchedContentCards!, card =>
            string.Equals(card.ContentCardId, enrichedCard.content_card_id, StringComparison.OrdinalIgnoreCase)
            && card.Evidence.HasValue);
    }

    [Fact]
    public async Task Resolve_source_endpoint_exposes_compact_ocr_diagnostics_without_raw_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, Guid revision_id)>(
            """
            SELECT d.doc_id, d.doc_path, d.doc_name, r.revision_id
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_page_index
            SET char_count = 0,
                metadata = jsonb_set(
                    jsonb_set(metadata, '{wordCount}', '0'::jsonb, true),
                    '{imageCount}',
                    '1'::jsonb,
                    true)
            WHERE tenant_id=@tenant
              AND revision_id=@revisionId
              AND page_number=1;

            UPDATE document_processing_runs
            SET payload = jsonb_build_object(
                'extractionSource', 'pdf_text',
                'ocrAttempted', true,
                'ocrApplied', false,
                'ocrLanguages', 'fra+eng',
                'ocrDurationMs', 1234,
                'nativeExtractionQuality', jsonb_build_object(
                    'textStatus', 'empty_text',
                    'ocrRecommended', true),
                'ocrDiagnostics', jsonb_build_object(
                    'mode', 'image_page',
                    'attemptedPageCount', 3,
                    'skippedPageCount', 2,
                    'pagesWithNovelText', jsonb_build_array(2),
                    'candidatePages', jsonb_build_array(1, 2, 3, 4, 5),
                    'attemptedPages', jsonb_build_array(1, 2, 3),
                    'failureReason', 'ocr_failed',
                    'appliedReason', 'image_ocr_no_novel_text',
                    'timedOut', true,
                    'imagePageDiagnostics', jsonb_build_array(
                        jsonb_build_object(
                            'pageNumber', 1,
                            'status', 'ocr_failed',
                            'reason', 'timeout',
                            'timedOut', true,
                            'stderr', 'raw-secret-should-not-leak'))),
                'extractionQuality', jsonb_build_object(
                    'textStatus', 'low_text',
                    'ocrRecommended', true,
                    'signals', jsonb_build_array('low_text_extraction', 'ocr_recommended'),
                    'pageCount', 5,
                    'textPageCount', 1,
                    'emptyPageCount', 2,
                    'sparsePageCount', 2))
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND revision_id=@revisionId
              AND action='upsert'
              AND status='done';
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                revisionId = expected.revision_id
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<SourceResolveResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality?.DiagnosticSummary);
        var quality = response!.Source!.ExtractionQuality!;
        Assert.Equal("ocr_failed_or_insufficient", quality.DocumentQualityStatus);
        Assert.Equal("ocr_failed_or_insufficient", quality.PageQualityStatus);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.True(quality.OcrAttempted);
        Assert.False(quality.OcrApplied);
        Assert.True(quality.OcrRecommended);

        var diagnostics = quality.DiagnosticSummary!;
        Assert.Equal("empty_text", diagnostics.NativeTextStatus);
        Assert.True(diagnostics.NativeOcrRecommended);
        Assert.Equal("image_page", diagnostics.OcrMode);
        Assert.Equal("fra+eng", diagnostics.OcrLanguages);
        Assert.Equal(1234, diagnostics.OcrDurationMs);
        Assert.Equal("ocr_failed", diagnostics.OcrFailureReason);
        Assert.Equal("image_ocr_no_novel_text", diagnostics.OcrAppliedReason);
        Assert.True(diagnostics.OcrTimedOut);
        Assert.Equal(3, diagnostics.OcrAttemptedPageCount);
        Assert.Equal(2, diagnostics.OcrSkippedPageCount);
        Assert.Equal(1, diagnostics.OcrPagesWithNovelTextCount);
        Assert.Equal(5, diagnostics.PageCount);
        Assert.Equal(1, diagnostics.TextPageCount);
        Assert.Equal(2, diagnostics.EmptyPageCount);
        Assert.Equal(2, diagnostics.SparsePageCount);
        Assert.True(diagnostics.ImagePageCount.GetValueOrDefault() >= 1);
        Assert.True(diagnostics.PageReviewRecommendedCount.GetValueOrDefault() >= 1);

        Assert.Contains("diagnosticSummary", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("imagePageDiagnostics", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("candidatePages", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("attemptedPages", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-should-not-leak", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_source_endpoint_infers_quality_from_native_status_and_page_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_name, Guid revision_id)>(
            """
            SELECT d.doc_id, d.doc_name, r.revision_id
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_page_index
            SET char_count = 0,
                metadata = jsonb_set(metadata, '{wordCount}', '0'::jsonb, true)
            WHERE tenant_id=@tenant
              AND revision_id=@revisionId;

            UPDATE document_processing_runs
            SET payload = jsonb_build_object(
                'extractionSource', 'pdf_text',
                'ocrAttempted', true,
                'ocrApplied', false,
                'ocrLanguages', 'eng',
                'nativeExtractionQuality', jsonb_build_object(
                    'textStatus', 'empty_text',
                    'ocrRecommended', true),
                'ocrDiagnostics', jsonb_build_object(
                    'mode', 'native_text',
                    'attemptedPageCount', 0,
                    'skippedPageCount', 0,
                    'failureReason', 'not_attempted'))
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND revision_id=@revisionId
              AND action='upsert'
              AND status='done';
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                revisionId = expected.revision_id
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await result.ExecuteAsync(ctx);

        var response = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality);
        var quality = response!.Source!.ExtractionQuality!;
        Assert.Equal("empty_text", quality.TextStatus);
        Assert.Equal("manual_review_empty_text", quality.DocumentQualityStatus);
        Assert.Equal(0.15, quality.DocumentExtractionConfidence);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.NotNull(quality.DiagnosticSummary);
        Assert.Equal("empty_text", quality.DiagnosticSummary!.NativeTextStatus);
        Assert.True(quality.DiagnosticSummary.NativeOcrRecommended);
        Assert.True(quality.DiagnosticSummary.PageCount.GetValueOrDefault() > 0);
        Assert.Equal(0, quality.DiagnosticSummary.TextPageCount.GetValueOrDefault());
    }

    [Fact]
    public async Task Get_summary_endpoint_exposes_same_enriched_source_as_sources_resolve()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd5ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, string category, string source_hash)>(
            """
            SELECT
              doc_id,
              doc_path,
              doc_name,
              category,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant, @docId, 'medium', 'en', @sourceHash, 'Stored technical summary.', '{"kind":"test"}'::jsonb, now(), now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              doc_language=EXCLUDED.doc_language,
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              summary_meta=EXCLUDED.summary_meta,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId = expected.doc_id, sourceHash = expected.source_hash });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var resolveCtx = BuildRagHttpContext(tenantId);
        var resolveResult = await InvokeResolveSourceAsync(
            resolveCtx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await resolveResult.ExecuteAsync(resolveCtx);
        var resolvePayload = ReadResponseBody(resolveCtx);
        var resolveResponse = JsonSerializer.Deserialize<SourceResolveResponse>(resolvePayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(resolveResponse?.Source);

        var summaryCtx = BuildRagHttpContext(tenantId);
        var summaryResult = await InvokeGetSummaryAsync(summaryCtx, ds, expected.doc_id, "medium");
        await summaryResult.ExecuteAsync(summaryCtx);

        Assert.Equal(StatusCodes.Status200OK, summaryCtx.Response.StatusCode);
        var summaryPayload = ReadResponseBody(summaryCtx);
        var typedSummary = JsonSerializer.Deserialize<SummaryGetResponse>(summaryPayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(typedSummary);
        Assert.Equal(expected.doc_id, typedSummary!.DocId);
        Assert.Equal("Stored technical summary.", typedSummary.SummaryText);
        Assert.Equal(expected.source_hash, typedSummary.SourceHash);
        Assert.Equal(expected.category, typedSummary.Category);
        Assert.NotNull(typedSummary.Source);
        Assert.Equal(resolveResponse!.Source!.SourceHash, typedSummary.Source!.SourceHash);

        using var summaryDoc = JsonDocument.Parse(summaryPayload);
        var root = summaryDoc.RootElement;
        Assert.Equal("Stored technical summary.", root.GetProperty("summaryText").GetString());
        Assert.Equal(expected.source_hash, root.GetProperty("sourceHash").GetString());
        Assert.Equal("en", root.GetProperty("docLanguage").GetString());
        Assert.Equal(expected.category, root.GetProperty("category").GetString());
        Assert.True(root.GetProperty("isFresh").GetBoolean());

        var source = root.GetProperty("source");
        Assert.Equal(resolveResponse.Source!.DocId.ToString(), source.GetProperty("docId").GetString());
        Assert.Equal(resolveResponse.Source.DocPath, source.GetProperty("docPath").GetString());
        Assert.Equal(resolveResponse.Source.DocName, source.GetProperty("docName").GetString());
        Assert.Equal(resolveResponse.Source.SourceHash, source.GetProperty("sourceHash").GetString());
        Assert.Equal(resolveResponse.Source.DocLanguage, source.GetProperty("docLanguage").GetString());
        Assert.Equal(resolveResponse.Source.ProfileLanguage, source.GetProperty("profileLanguage").GetString());
        Assert.Equal(resolveResponse.Source.Category, source.GetProperty("category").GetString());
        Assert.Equal(resolveResponse.Source.CategoryPath, source.GetProperty("categoryPath").GetString());
        Assert.Equal(resolveResponse.Source.ExtractionQuality!.DocumentQualityStatus, source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal(resolveResponse.Source.MatchedContentCards![0].Title, source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(resolveResponse.Source.SelectionHints!.EvidenceRole, source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());

        Assert.Equal(source.GetProperty("profileLanguage").GetString(), root.GetProperty("profileLanguage").GetString());
        Assert.Equal(source.GetProperty("category").GetString(), root.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("categoryPath").GetString(), root.GetProperty("categoryPath").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), root.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Search_summary_endpoint_exposes_enriched_source_projection_for_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd5ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string category, string source_hash)>(
            """
            SELECT
              doc_id,
              category,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant, @docId, 'medium', 'en', @sourceHash, 'Stored technical summary with PLC integration.', '{"kind":"test"}'::jsonb, now(), now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              doc_language=EXCLUDED.doc_language,
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              summary_meta=EXCLUDED.summary_meta,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId = expected.doc_id, sourceHash = expected.source_hash });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeSearchSummariesAsync(ctx, ds, "PLC integration", limit: 10, offset: 0);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(expected.source_hash, item.GetProperty("sourceHash").GetString());
        Assert.Equal("en", item.GetProperty("docLanguage").GetString());
        Assert.Equal("en", item.GetProperty("profileLanguage").GetString());
        Assert.Equal(expected.category, item.GetProperty("category").GetString());
        Assert.Equal("Programmation", item.GetProperty("categoryPath").GetString());
        Assert.True(item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object);
        Assert.Equal(item.GetProperty("sourceHash").GetString(), source.GetProperty("sourceHash").GetString());
        Assert.Equal(item.GetProperty("profileLanguage").GetString(), source.GetProperty("profileLanguage").GetString());
        Assert.Equal(item.GetProperty("category").GetString(), source.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString(), item.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), item.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString(), item.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
    }

    [Fact]
    public async Task Get_summary_endpoint_keeps_stale_summary_behavior_before_source_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd6ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var docId = await conn.QuerySingleAsync<Guid>(
            """
            SELECT doc_id
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant, @docId, 'medium', 'en', 'stale-source-hash', 'Stale summary.', '{}'::jsonb, now(), now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeGetSummaryAsync(ctx, ds, docId, "medium");
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        Assert.Equal("summary_stale", payload.RootElement.GetProperty("error").GetString());

        var remaining = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_summaries
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND level='medium';
            """,
            new { tenant = tenantId, docId });
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task Resolve_source_endpoint_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd7ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name)>(
            """
            SELECT doc_id, doc_path, doc_name
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd70000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd70000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd70000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('AA', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('AA', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });
        await result.ExecuteAsync(ctx);

        var response = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality);
        Assert.Equal("extraction_ok", response!.Source!.ExtractionQuality!.DocumentQualityStatus);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality.PageQualityStatus);
        Assert.DoesNotContain("stale_revision_should_not_leak", response.Source.ExtractionQuality.Signals ?? []);
        Assert.False(response.Source.ExtractionQuality.OcrRecommended.GetValueOrDefault());
    }

    [Fact]
    public async Task Rag_search_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd8ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd80000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd80000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd80000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('BB', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('BB', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal));
        Assert.NotNull(item.ExtractionQuality);
        Assert.Equal("extraction_ok", item.ExtractionQuality!.DocumentQualityStatus);
        Assert.DoesNotContain("stale_revision_should_not_leak", item.ExtractionQuality.Signals ?? []);
        Assert.False(item.ExtractionQuality.OcrRecommended.GetValueOrDefault());
    }

    [Fact]
    public async Task Rag_query_legacy_endpoint_returns_enriched_match_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdda0000-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagQueryAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("matches", out var matches));
        var item = Assert.Single(
            matches.EnumerateArray(),
            static match => string.Equals(match.GetProperty("docPath").GetString(), "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal));

        Assert.True(item.TryGetProperty("sourceHash", out var sourceHash));
        Assert.False(string.IsNullOrWhiteSpace(sourceHash.GetString()));
        Assert.True(item.TryGetProperty("docLanguage", out var docLanguage));
        Assert.Equal("en", docLanguage.GetString());
        Assert.True(item.TryGetProperty("extractionQuality", out var quality));
        Assert.Equal("extraction_ok", quality.GetProperty("documentQualityStatus").GetString());
        Assert.True(item.TryGetProperty("matchedContentCards", out _));
        Assert.True(root.TryGetProperty("guidance", out var guidance));
        Assert.Equal("answer", guidance.GetProperty("behavior").GetString());
        Assert.True(root.TryGetProperty("metrics", out var metrics));
        Assert.True(metrics.GetProperty("returned").GetInt32() > 0);
    }

    [Fact]
    public async Task Document_language_falls_back_to_ingestion_run_when_profiles_are_temporarily_missing()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cddb0000-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cddb0000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cddb0000-5555-6666-7777-888888888888");
        const string docPath = "Kennisbank/Handleiding.pdf";
        const string text = "De handleiding beschrijft onderhoud en veiligheidscontroles voor de installatie. Het document noemt deze inspectie en deze procedure.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Onderhoud",
            text,
            $"Document: Handleiding.pdf\nHeading Path: Onderhoud\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var runLanguage = await conn.ExecuteScalarAsync<string>(
                """
SELECT payload ->> 'documentLanguage'
FROM document_processing_runs
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND status='done'
LIMIT 1;
""",
                new { tenant = tenantId, docId });
            Assert.Equal("nl", runLanguage);

            await conn.ExecuteAsync(
                """
DELETE FROM document_profile_content_cards WHERE tenant_id=@tenant AND doc_id=@docId;
DELETE FROM document_profiles WHERE tenant_id=@tenant AND doc_id=@docId;
DELETE FROM document_summaries WHERE tenant_id=@tenant AND doc_id=@docId;
""",
                new { tenant = tenantId, docId });
        }

        var resolveCtx = BuildRagHttpContext(tenantId);
        var resolveResult = await InvokeResolveSourceAsync(
            resolveCtx,
            ds,
            new SourceResolveRequest { PdfRef = "Handleiding.pdf" });
        await resolveResult.ExecuteAsync(resolveCtx);

        var resolveResponse = JsonSerializer.Deserialize<SourceResolveResponse>(
            ReadResponseBody(resolveCtx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resolveResponse?.Source);
        Assert.Equal("nl", resolveResponse!.Source!.DocLanguage);
        Assert.Null(resolveResponse.Source.ProfileLanguage);

        var searchCtx = BuildRagHttpContext(tenantId);
        var searchResult = await InvokeRagSearchAsync(
            searchCtx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("onderhoud installatie", TopK: 3));
        await searchResult.ExecuteAsync(searchCtx);

        var searchResponse = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(searchCtx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var searchItem = Assert.Single(
            searchResponse!.Items,
            static item => string.Equals(item.DocPath, docPath, StringComparison.Ordinal));
        Assert.Equal("nl", searchItem.DocLanguage);
        Assert.Null(searchItem.ProfileLanguage);

        var listCtx = BuildRagHttpContext(tenantId);
        var listResult = await InvokeUnifiedDocumentsListAsync(listCtx, ds, limit: 10, offset: 0);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var listItem = Assert.Single(
            listPayload.RootElement.GetProperty("items").EnumerateArray(),
            entry => string.Equals(entry.GetProperty("docPath").GetString(), docPath, StringComparison.Ordinal));
        Assert.Equal("nl", listItem.GetProperty("docLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, listItem.GetProperty("profileLanguage").ValueKind);
    }

    [Fact]
    public async Task Admin_extraction_quality_uses_processing_run_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd9ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd90000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd90000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd90000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('CC', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('CC', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityAsync(ctx, ds, "Programmation", null, 50);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var item = Assert.Single(
            payload.RootElement.GetProperty("items").EnumerateArray(),
            entry => string.Equals(entry.GetProperty("docPath").GetString(), expected.doc_path, StringComparison.Ordinal));

        Assert.Equal("extraction_ok", item.GetProperty("qualityStatus").GetString());
        Assert.False(item.GetProperty("ocrRecommended").GetBoolean());
        Assert.DoesNotContain(
            item.GetProperty("signals").EnumerateArray(),
            signal => signal.GetString() == "stale_revision_should_not_leak");
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoint_reports_llm_enrichment_pending_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cddeffff-ffff-ffff-ffff-ffffffffffff");
        var readyDocId = Guid.Parse("cdde0000-1111-2222-3333-444444444444");
        var pendingDocId = Guid.Parse("cdde0000-5555-6666-7777-888888888888");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            readyDocId,
            Guid.Parse("cdde0000-aaaa-bbbb-cccc-dddddddddddd"),
            "Diagnostics/ReadyForBackoffice.pdf",
            1,
            "Diagnostics",
            "Ready document has enough stable text for extraction quality diagnostics.",
            "document_name: ReadyForBackoffice.pdf\nsection_title: Diagnostics\nexcerpt:\nReady document has enough stable text for extraction quality diagnostics.");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            pendingDocId,
            Guid.Parse("cdde0000-eeee-ffff-aaaa-bbbbbbbbbbbb"),
            "Diagnostics/PendingBackoffice.pdf",
            1,
            "Diagnostics",
            "Pending document has enough stable text but no server-side profile or summary yet.",
            "document_name: PendingBackoffice.pdf\nsection_title: Diagnostics\nexcerpt:\nPending document has enough stable text but no server-side profile or summary yet.");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var ready = await conn.QuerySingleAsync<(Guid revision_id, string source_hash)>(
                """
                SELECT
                  r.revision_id,
                  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS source_hash
                FROM documents d
                JOIN document_revisions r
                  ON r.tenant_id=d.tenant_id
                 AND r.doc_id=d.doc_id
                 AND r.indexed_version=d.indexed_version
                WHERE d.tenant_id=@tenant
                  AND d.doc_id=@docId
                LIMIT 1;
                """,
                new { tenant = tenantId, docId = readyDocId });

            await conn.ExecuteAsync(
                """
                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1', 'en',
                  'Backoffice profile ready.', ARRAY['diagnostic']::text[], ARRAY[]::text[], ARRAY[]::text[],
                  ARRAY[]::text[], ARRAY[]::text[], 'Backoffice profile ready.', 3, decode('CDDE', 'hex'),
                  '{"contentCardEvidenceSchemaVersion":2}'::jsonb
                )
                ON CONFLICT (revision_id, profile_version) DO UPDATE
                SET metadata=excluded.metadata,
                    updated_at=now();

                INSERT INTO document_summaries(
                  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
                )
                VALUES(
                  @tenant, @docId, 'medium', 'en', @sourceHash, 'Backoffice summary ready.', '{}'::jsonb, now(), now()
                )
                ON CONFLICT (tenant_id, doc_id, level) DO UPDATE
                SET source_hash=excluded.source_hash,
                    updated_at=now();
                """,
                new
                {
                    tenant = tenantId,
                    docId = readyDocId,
                    revisionId = ready.revision_id,
                    sourceHash = ready.source_hash,
                    profileId = Guid.Parse("cdde0000-9999-8888-7777-666666666666")
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityAsync(ctx, ds, "Diagnostics", null, 50);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));

        var summary = payload.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("llmEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("summaryEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("profileEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("contentCardEvidencePendingDocuments").GetInt32());

        var category = Assert.Single(payload.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal("Diagnostics", category.GetProperty("categoryPath").GetString());
        Assert.Equal(1, category.GetProperty("llmEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("summaryEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("profileEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("contentCardEvidencePendingDocuments").GetInt32());

        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var readyItem = Assert.Single(
            items,
            item => string.Equals(
                item.GetProperty("docPath").GetString(),
                "Diagnostics/ReadyForBackoffice.pdf",
                StringComparison.Ordinal));
        Assert.False(readyItem.GetProperty("llmEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("summaryEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("profileEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("contentCardEvidencePending").GetBoolean());

        var pendingItem = Assert.Single(
            items,
            item => string.Equals(
                item.GetProperty("docPath").GetString(),
                "Diagnostics/PendingBackoffice.pdf",
                StringComparison.Ordinal));
        Assert.True(pendingItem.GetProperty("llmEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("summaryEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("profileEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("contentCardEvidencePending").GetBoolean());
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoints_tolerate_malformed_processing_payload_values()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd8ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd80000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd80000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/MalformedPayload.pdf";
        var pageText = string.Join(' ', Enumerable.Range(0, 80).Select(index => $"signal{index}"));

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 1],
            size: 1024,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, pageText, CountWords(pageText), pageText.Length, [1])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Diagnostics", 1, 1, 1, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, pageText, pageText.Length, CountWords(pageText), [2])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pageText, CountWords(pageText), [3], "unit_exact_v1")
            ],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE document_processing_runs
                SET payload = '{
                  "extractionSource": "pdf_text",
                  "ocrAttempted": "not-a-bool",
                  "ocrApplied": "also-not-a-bool",
                  "ocrDurationMs": "not-a-number",
                  "nativeExtractionQuality": {
                    "textStatus": "ok",
                    "ocrRecommended": "maybe"
                  },
                  "extractionQuality": {
                    "ocrRecommended": "maybe",
                    "pageCount": "many",
                    "textPageCount": "enough",
                    "emptyPageCount": "none",
                    "sparsePageCount": "few",
                    "totalWordCount": "lots",
                    "totalCharCount": "lots",
                    "averageWordsPerPage": "NaN",
                    "textPageRatio": "almost"
                  }
                }'::jsonb
                WHERE tenant_id=@tenant
                  AND doc_id=@docId
                  AND action='upsert'
                  AND status='done';
                """,
                new { tenant = tenantId, docId });
        }

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "Diagnostics", null, 20);
        await listResult.ExecuteAsync(listCtx);

        Assert.Equal(StatusCodes.Status200OK, listCtx.Response.StatusCode);
        using (var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx)))
        {
            var item = Assert.Single(
                listPayload.RootElement.GetProperty("items").EnumerateArray(),
                entry => string.Equals(entry.GetProperty("docPath").GetString(), docPath, StringComparison.Ordinal));
            Assert.False(item.GetProperty("ocrAttempted").GetBoolean());
            Assert.False(item.GetProperty("ocrApplied").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("ocrDurationMs").ValueKind);
            Assert.Equal(1, item.GetProperty("pageCount").GetInt32());
            Assert.Equal("extraction_ok", item.GetProperty("qualityStatus").GetString());
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.False(root.GetProperty("ocrAttempted").GetBoolean());
        Assert.False(root.GetProperty("ocrApplied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ocrDurationMs").ValueKind);
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
    }

    [Fact]
    public async Task Extraction_quality_pages_endpoint_reports_page_level_review_signals()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd40000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd40000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/Quality.pdf";
        var pageOne = string.Join(' ', Enumerable.Range(0, 90).Select(index => $"alpha{index}"));
        var pageTwoNoise = "yo o | | i Ne | a \\\"------- abgearbeitet __/ sel 48 a) |b.";
        var pageTwo = pageOne + Environment.NewLine + pageTwoNoise;

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3],
            size: 2048,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, pageOne, CountWords(pageOne), pageOne.Length, [1], null, ImageCount: 1),
                new ExtractedPdfPage(2, pageTwo, CountWords(pageTwo), pageTwo.Length, [2])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Diagnostics", 1, 1, 2, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, pageOne, pageOne.Length, CountWords(pageOne), [3]),
                new ExtractedDocumentUnit(1, 0, 2, 2, pageTwoNoise, pageTwoNoise.Length, CountWords(pageTwoNoise), [4])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pageOne, CountWords(pageOne), [5], "unit_exact_v1"),
                new ProjectedRetrievalChunk(1, 0, 1, 2, 2, pageTwo, CountWords(pageTwo), [6], "unit_exact_v1")
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 0, 1, 1, pageOne, "alpha", pageOne.Length, CountWords(pageOne), [7], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, pageOne, CountWords(pageOne), CountWords(pageOne), [8]),
                new ProjectedContextualTextEntry(1, 0, 1, 1, 2, 2, pageTwo, CountWords(pageTwo), CountWords(pageTwo), [9])
            ],
            CancellationToken.None,
            extractionSource: "pdf_text_plus_image_ocr",
            ocrAttempted: true,
            ocrApplied: true,
            ocrLanguages: "eng",
            ocrDurationMs: 123,
            ocrDiagnostics: new PdfOcrDiagnostics(
                Mode: "image_page",
                CandidatePageCount: 2,
                AttemptedPageCount: 1,
                SkippedPageCount: 1,
                MaxPages: 1,
                CandidatePages: [1, 2],
                AttemptedPages: [1],
                SkippedPages: [2],
                PagesWithOcrText: [1],
                PagesWithNovelText: [1],
                Stderr: "raw-secret-should-not-leak",
                ImagePageDiagnostics:
                [
                    new PdfImagePageOcrDiagnostic(1, "novel_text_applied", OcrWordCount: 90, OcrCharCount: pageOne.Length, ExitCode: 0),
                    new PdfImagePageOcrDiagnostic(2, "skipped", "budget")
                ]));

        Assert.True(committed);

        await using (var staleConn = new NpgsqlConnection(db.ConnectionString))
        {
            await staleConn.OpenAsync();
            await staleConn.ExecuteAsync(
                """
                INSERT INTO document_revisions(
                  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
                  ingestion_version, indexed_version, published_at, created_at
                )
                VALUES(
                  'cdd40000-aaaa-bbbb-cccc-dddddddddddd', @tenant, @docId, @docPath, decode('DD', 'hex'), 123, now() - interval '2 days',
                  1, 0, now() - interval '2 days', now() - interval '2 days'
                )
                ON CONFLICT DO NOTHING;

                INSERT INTO document_processing_runs(
                  processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
                  ingestion_version, indexed_version_before, indexed_version_after, source_hash,
                  started_at, finished_at, payload
                )
                VALUES(
                  'cdd40000-eeee-ffff-aaaa-bbbbbbbbbbbb', @tenant, 'cdd40000-cccc-dddd-eeee-ffffffffffff', @docId, @docPath,
                  'cdd40000-aaaa-bbbb-cccc-dddddddddddd', 'upsert', 'done',
                  1, 0, 0, decode('DD', 'hex'),
                  now() + interval '1 day', now() + interval '1 day',
                  '{
                    "extractionSource": "pdf_text",
                    "ocrAttempted": true,
                    "ocrApplied": true,
                    "ocrDiagnostics": {
                      "mode": "image_page",
                      "imagePageDiagnostics": [
                        { "pageNumber": 1, "status": "stale_should_not_leak", "reason": "stale" },
                        { "pageNumber": 2, "status": "stale_should_not_leak", "reason": "stale" }
                      ]
                    }
                  }'::jsonb
                )
                ON CONFLICT DO NOTHING;
                """,
                new { tenant = tenantId, docId, docPath });
        }

        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityPagesAsync(ctx, ds, docId);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var root = payload.RootElement;
        Assert.DoesNotContain("stale_should_not_leak", root.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(docPath, root.GetProperty("docPath").GetString());
        Assert.True(root.GetProperty("ocrApplied").GetBoolean());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("probableOcrNoisePages").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("indexedByContextPages").GetInt32());

        var pages = root.GetProperty("pages").EnumerateArray().ToArray();
        var first = Assert.Single(pages, page => page.GetProperty("pageNumber").GetInt32() == 1);
        Assert.Equal("page_ok_with_images", first.GetProperty("qualityStatus").GetString());
        Assert.False(first.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.Contains(
            first.GetProperty("signals").EnumerateArray(),
            signal => signal.GetString() == "page_contains_images");
        Assert.Equal("novel_text_applied", first.GetProperty("imageOcrStatus").GetString());
        Assert.Equal(90, first.GetProperty("imageOcrWordCount").GetInt32());
        Assert.Equal(0, first.GetProperty("imageOcrExitCode").GetInt32());
        Assert.False(first.GetProperty("imageOcrTimedOut").GetBoolean());
        Assert.Empty(first.GetProperty("unitPreviews").EnumerateArray());
        Assert.Empty(first.GetProperty("chunkPreviews").EnumerateArray());

        var second = Assert.Single(pages, page => page.GetProperty("pageNumber").GetInt32() == 2);
        Assert.Equal("manual_review_probable_ocr_noise", second.GetProperty("qualityStatus").GetString());
        Assert.True(second.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.Equal("skipped", second.GetProperty("imageOcrStatus").GetString());
        Assert.Equal("budget", second.GetProperty("imageOcrReason").GetString());
        Assert.Equal(1, second.GetProperty("suspiciousUnitCount").GetInt32());
        Assert.Contains(
            second.GetProperty("unitPreviews").EnumerateArray(),
            preview => preview.GetString() == pageTwoNoise);
        Assert.Contains(
            second.GetProperty("chunkPreviews").EnumerateArray(),
            preview => preview.GetString() == pageTwo);

        var ragCtx = BuildRagHttpContext(tenantId);
        var ragResult = await InvokeRagSearchAsync(
            ragCtx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("abgearbeitet", TopK: 3));
        await ragResult.ExecuteAsync(ragCtx);

        Assert.Equal(StatusCodes.Status200OK, ragCtx.Response.StatusCode);
        var ragPayload = ReadResponseBody(ragCtx);
        var ragResponse = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ragPayload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(ragResponse);
        var ragItem = Assert.Single(
            ragResponse!.Items,
            static item => string.Equals(item.DocPath, "Diagnostics/Quality.pdf", StringComparison.Ordinal));
        Assert.NotNull(ragItem.ExtractionQuality);
        Assert.Equal("manual_review_probable_ocr_noise", ragItem.ExtractionQuality!.PageQualityStatus);
        Assert.True(ragItem.ExtractionQuality.PageManualReviewRecommended);
        Assert.Contains("probable_ocr_noise_units", ragItem.ExtractionQuality.Signals ?? []);
        Assert.NotNull(ragItem.ExtractionQuality.DiagnosticSummary);
        var diagnostics = ragItem.ExtractionQuality.DiagnosticSummary!;
        Assert.Equal("image_page", diagnostics.OcrMode);
        Assert.Equal("eng", diagnostics.OcrLanguages);
        Assert.Equal(123, diagnostics.OcrDurationMs);
        Assert.Equal(1, diagnostics.OcrAttemptedPageCount);
        Assert.Equal(1, diagnostics.OcrSkippedPageCount);
        Assert.Equal(1, diagnostics.OcrPagesWithNovelTextCount);
        Assert.Equal(2, diagnostics.PageCount);
        Assert.Equal(2, diagnostics.ImagePageCount);
        Assert.True(diagnostics.PageWarningCount.GetValueOrDefault() >= 1);
        Assert.True(diagnostics.PageReviewRecommendedCount.GetValueOrDefault() >= 1);
        Assert.Contains("diagnosticSummary", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("imagePageDiagnostics", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("candidatePages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("attemptedPages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr", ragPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret-should-not-leak", ragPayload, StringComparison.Ordinal);
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
        var expectedSourceHash = await conn.ExecuteScalarAsync<string>(
            """
            SELECT saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version)
            FROM documents
            WHERE tenant_id=@tenant
              AND doc_path='ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

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
        Assert.Equal(expectedSourceHash, item.SourceHash);
        Assert.Equal(expectedSourceHash, item.ProvenanceInfo.SourceHash);
        Assert.NotNull(item.ProvenanceInfo.OffsetStart);
        Assert.NotNull(item.ProvenanceInfo.OffsetEnd);
        Assert.True(item.ProvenanceInfo.OffsetEnd > item.ProvenanceInfo.OffsetStart);
        Assert.False(string.IsNullOrWhiteSpace(item.Snippet));
        Assert.False(string.IsNullOrWhiteSpace(item.ContextualSnippet));
        Assert.NotNull(item.Context);
        Assert.Equal(item.ChunkType, item.Context!.ChunkType);
        Assert.Equal(item.SectionTitle, item.Context.SectionTitle);
        Assert.Equal(item.HeadingPath, item.Context.HeadingPath);
        Assert.False(item.HypQuestionsMatched);
    }

    [Fact]
    public async Task SearchAsync_sets_hyp_questions_matched_when_query_overlaps_capability_a_questions()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal));

        Assert.True(item.HypQuestionsMatched);
    }

    [Fact]
    public async Task SearchAsync_merges_hyp_questions_from_all_current_profiles()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-eeeeeeeeeeee");
        var docId = Guid.Parse("dededede-aaaa-bbbb-cccc-111111111111");
        var jobId = Guid.Parse("dededede-aaaa-bbbb-cccc-222222222222");
        const string docPath = "Generic/MergedProfileQuestions.pdf";
        const string text = "Generic operational source text with no night audit cadence phrase.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: MergedProfileQuestions.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
                UPDATE document_profiles
                SET summary_text='Generic operational note.',
                    search_text='generic operational note when should the night audit cadence be reviewed',
                    keywords=ARRAY[]::text[],
                    topics=ARRAY[]::text[],
                    hypothetical_questions=ARRAY['When should the night audit cadence be reviewed?']::text[]
                WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';

                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'LLM profile without generated questions yet.',
                  ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                  'LLM profile without generated questions yet.',
                  6,
                  decode(repeat('49', 32), 'hex'),
                  '{}'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId = Guid.Parse("dededede-aaaa-bbbb-cccc-333333333333")
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("night audit cadence", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, docPath, StringComparison.Ordinal));

        Assert.True(item.HypQuestionsMatched);
        Assert.Equal("en", item.ProfileLanguage);
    }

    [Fact]
    public async Task LoadHypQuestionsMatchedByDocPathAsync_normalizes_legacy_match_paths()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-eeeeeeeeeeef");
        var docId = Guid.Parse("dededede-aaaa-bbbb-cccc-111111111112");
        var jobId = Guid.Parse("dededede-aaaa-bbbb-cccc-222222222223");
        const string docPath = "Generic/LegacyPathQuestions.pdf";
        const string text = "Generic source text without the readiness cadence wording.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: LegacyPathQuestions.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                UPDATE document_profiles
                SET hypothetical_questions=ARRAY['When should the readiness cadence be reviewed?']::text[]
                WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';
                """,
                new { tenant = tenantId, docId });
        }

        var matchedByPath = await RagEndpoints.LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            tenantId,
            "readiness cadence",
            [
                new RagMatch(
                    0.9,
                    docId.ToString(),
                    "\\Generic\\LegacyPathQuestions.pdf",
                    "LegacyPathQuestions.pdf",
                    1,
                    1,
                    "legacy-path-chunk",
                    0,
                    text,
                    1,
                    null,
                    text,
                    "unit_exact_v1",
                    0,
                    0,
                    "Operational baseline",
                    "Operational baseline",
                    "unit_exact",
                    null,
                    null,
                    null)
            ],
            CancellationToken.None);

        var matched = RagEndpoints.ResolveHypQuestionsMatched("\\Generic\\LegacyPathQuestions.pdf", matchedByPath);
        Assert.True(matched.HasValue);
        Assert.True(matched.Value);
    }

    [Fact]
    public async Task SearchAsync_does_not_call_capability_a_llm_questions_during_interactive_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("faf0ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var (requestServices, llmFactory) = BuildCapabilityARequestServices();
        var ctx = BuildRagHttpContext(tenantId, requestServices);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("When should the IND570 PLC integration guidance be retrieved?", Category: "programmation", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal));

        Assert.NotNull(item.HypQuestionsMatched);
        Assert.Equal(0, llmFactory.LlmRequestCount);
    }

    private static DefaultHttpContext BuildRagHttpContext(Guid tenantId, IServiceProvider? requestServices = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.Items[RequestIdMiddleware.RequestIdItemKey] = $"it-{tenantId:N}";
        ctx.Response.Body = new MemoryStream();
        if (requestServices is not null)
            ctx.RequestServices = requestServices;
        return ctx;
    }

    private static DefaultHttpContext BuildAdminDocumentsHttpContext(Guid tenantId, IServiceProvider? requestServices = null)
    {
        var ctx = BuildRagHttpContext(tenantId, requestServices);
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        return ctx;
    }

    private static async Task<IResult> InvokeDocumentsSnapshotAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("SnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds])!;
    }

    private static async Task<IResult> InvokeUnifiedDocumentsListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        int limit,
        int offset,
        string? status = null)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("UnifiedListAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, null, status, null, null, null, null, limit, offset])!;
    }

    private static async Task<IResult> InvokeUnifiedDocumentsGetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("UnifiedGetAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId])!;
    }

    private static async Task<IResult> InvokeResolveCategoryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ResolveCategoryRequest request)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ResolveCategoryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, request])!;
    }

    private static async Task<IResult> InvokeResolveSourceAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        SourceResolveRequest request)
    {
        var method = typeof(SourcesEndpoints).GetMethod("ResolveSourceAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, request])!;
    }

    private static async Task<IResult> InvokeGetSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId,
        string? level)
    {
        var method = typeof(SummaryEndpoints).GetMethod("GetSummaryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId, level])!;
    }

    private static async Task<IResult> InvokeSearchSummariesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? q,
        int? limit,
        int? offset)
    {
        var method = typeof(SummaryEndpoints).GetMethod("SearchSummariesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, q, limit, offset])!;
    }

    private static async Task<IResult> InvokeExtractionQualityPagesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ExtractionQualityPagesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId])!;
    }

    private static async Task<IResult> InvokeExtractionQualityAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? limit)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ExtractionQualityAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, path, categoryRef, limit])!;
    }

    private static async Task<IResult> InvokeCatalogCacheEndpointAsync(
        string endpoint,
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestionOptions)
    {
        var (methodName, args) = endpoint switch
        {
            "catalog_categories" => (
                "CatalogCategoriesAsync",
                new object?[] { ctx, ds, null, null, 100, null, null }),
            "catalog_documents" => (
                "CatalogDocumentsAsync",
                new object?[] { ctx, ds, null, null, null, null, null, 50, null, null }),
            "documents_stats" => (
                "StatsAsync",
                new object?[] { ctx, ds, ingestionOptions, null, null }),
            "catalog_stats" => (
                "CatalogStatsAsync",
                new object?[] { ctx, ds, ingestionOptions, null, null }),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown catalog cache endpoint.")
        };

        var method = typeof(DocumentsEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, args)!;
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

    private static async Task<IResult> InvokeRagQueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOptions,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto request)
    {
        var method = typeof(RagEndpoints).GetMethod("QueryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, ragOptions, httpFactory, request])!;
    }

    private static async Task<IResult> InvokeRagCategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var method = typeof(RagEndpoints).GetMethod("CategoriesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds])!;
    }

    private static (IServiceProvider Services, CountingLlmHttpClientFactory LlmFactory) BuildCapabilityARequestServices()
    {
        var llmFactory = new CountingLlmHttpClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton(new CapabilityAHypotheticalQuestionService(
            new LocalLlmChatClient(
                llmFactory,
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "local"
                })));
        return (services.BuildServiceProvider(), llmFactory);
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

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

    private sealed class LegacyCatalogMetadataSeed
    {
        public Guid DocId { get; set; }
        public Guid RevisionId { get; set; }
        public string DocPath { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string? CategoryRef { get; set; }
    }

    private static async Task<LegacyCatalogMetadataSeed> SeedLegacyCatalogMetadataAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid? excludeDocId = null)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var seed = await conn.QuerySingleAsync<LegacyCatalogMetadataSeed>(
            """
            SELECT
              d.doc_id AS "DocId",
              d.doc_path AS "DocPath",
              r.revision_id AS "RevisionId",
              saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS "SourceHash",
              CASE WHEN c.display_order IS NULL THEN NULL ELSE ('cat_' || lpad(c.display_order::text, 3, '0')) END AS "CategoryRef"
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id = d.tenant_id
             AND r.doc_id = d.doc_id
             AND r.indexed_version = d.indexed_version
            LEFT JOIN documents_catalog_categories c
              ON c.tenant_id = d.tenant_id
             AND c.path = split_part(d.doc_path, '/', 1)
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND (@excludeDocId::uuid IS NULL OR d.doc_id <> @excludeDocId::uuid)
            ORDER BY d.doc_path ASC
            LIMIT 1;
            """,
            new { tenant = tenantId, excludeDocId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_profiles (
              document_profile_id,
              tenant_id,
              revision_id,
              doc_id,
              profile_version,
              language,
              summary_text,
              search_text,
              token_count,
              checksum
            )
            VALUES (
              @profileId,
              @tenant,
              @revisionId,
              @docId,
              'llm_backoffice_v1',
              'DE',
              'Profil de test.',
              'Profil de test.',
              3,
              decode('AB', 'hex')
            )
            ON CONFLICT (revision_id, profile_version) DO UPDATE
            SET language=excluded.language,
                updated_at=now();
            """,
            new { profileId = Guid.NewGuid(), tenant = tenantId, revisionId = seed.RevisionId, docId = seed.DocId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries (
              tenant_id,
              doc_id,
              level,
              doc_language,
              source_hash,
              summary_text,
              summary_meta,
              created_at,
              updated_at
            )
            VALUES (
              @tenant,
              @docId,
              'medium',
              'IT',
              @sourceHash,
              'Riassunto di test.',
              '{}'::jsonb,
              now(),
              now()
            )
            ON CONFLICT (tenant_id, doc_id, level) DO UPDATE
            SET doc_language=excluded.doc_language,
                source_hash=excluded.source_hash,
                updated_at=now();
            """,
            new { tenant = tenantId, docId = seed.DocId, sourceHash = seed.SourceHash });

        return seed;
    }

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
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class CountingLlmHttpClientFactory : IHttpClientFactory
    {
        private readonly CountingLlmHttpMessageHandler _handler = new();

        public int LlmRequestCount => _handler.LlmRequestCount;

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class CountingLlmHttpMessageHandler : HttpMessageHandler
    {
        private int _llmRequestCount;

        public int LlmRequestCount => _llmRequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if ((request.RequestUri?.AbsolutePath ?? string.Empty).EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                System.Threading.Interlocked.Increment(ref _llmRequestCount);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "{\"questions\":[\"When should the IND570 PLC integration guidance be retrieved?\"]}"
                          }
                        }
                      ]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
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

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"questions\":[\"When should the IND570 PLC integration guidance be retrieved?\",\"How should operators apply the Control Loop Overview in IND570?\"]}"
                      }
                    }
                  ]
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
