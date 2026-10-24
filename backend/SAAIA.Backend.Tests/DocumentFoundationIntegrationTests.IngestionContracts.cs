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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task PublishFailedOcrExtractionAsync_persists_diagnostics_and_non_indexable_document_state()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task PublishFailedOcrExtractionAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task PublishFailedOcrExtractionAsync_keeps_existing_indexed_source_metadata()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "OCR", null, 20);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var failedItem = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("indexed", failedItem.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", failedItem.GetProperty("processingRunStatus").GetString());
        Assert.Equal("scanned_pdf_not_indexable", failedItem.GetProperty("failureReason").GetString());

        // A diagnostic from an earlier ingestion attempt must not replace the
        // published revision's diagnostic once another attempt is current.
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE documents SET ingestion_version=3 WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
        }
        var newerAttemptCtx = BuildAdminDocumentsHttpContext(tenantId);
        var newerAttemptResult = await InvokeExtractionQualityPagesAsync(newerAttemptCtx, ds, docId);
        await newerAttemptResult.ExecuteAsync(newerAttemptCtx);
        using var newerAttemptPayload = JsonDocument.Parse(ReadResponseBody(newerAttemptCtx));
        Assert.Equal("done", newerAttemptPayload.RootElement.GetProperty("processingRunStatus").GetString());
        Assert.DoesNotContain("failed_run", newerAttemptPayload.RootElement.GetRawText(), StringComparison.Ordinal);
    }
}
