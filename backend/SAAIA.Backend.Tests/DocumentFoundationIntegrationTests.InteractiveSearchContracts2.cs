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
    public async Task Resolve_category_endpoint_resolves_category_ref_and_returns_aliases()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd2ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using (var catalogDs = NpgsqlDataSource.Create(db.ConnectionString))
        {
            var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
                catalogDs, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            Assert.True(built.Success);
        }

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resolve_source_endpoint_matches_filename_reference_to_indexed_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['source resolve pressure envelope', 'source resolve audit']::text[],
              entities=ARRAY['Mettler Toledo IND570']::text[],
              topics=ARRAY['source resolve enrichment']::text[],
              hypothetical_questions=ARRAY['Which source resolve diagnostic should be opened?']::text[],
              limits=ARRAY['Use page chunks for exact wiring.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

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
        Assert.NotNull(response.Source.ProfileSignals);
        Assert.Equal("en", response.Source.ProfileSignals!.Language);
        Assert.Contains("source resolve pressure envelope", response.Source.ProfileSignals.Keywords!);
        Assert.Contains("Mettler Toledo IND570", response.Source.ProfileSignals.Entities!);
        Assert.Contains("source resolve enrichment", response.Source.ProfileSignals.Topics!);
        Assert.Contains("Which source resolve diagnostic should be opened?", response.Source.ProfileSignals.HypotheticalQuestions!);
        Assert.Contains("Use page chunks for exact wiring.", response.Source.ProfileSignals.Limits!);

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
        Assert.Contains("source resolve audit", refResponse.Source.ProfileSignals!.Keywords!);
        Assert.Contains(refResponse.Source.MatchedContentCards!, card =>
            string.Equals(card.ContentCardId, enrichedCard.content_card_id, StringComparison.OrdinalIgnoreCase)
            && card.Evidence.HasValue);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resolve_source_endpoint_keeps_profile_identity_when_profile_lists_are_empty()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd3ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_name, string profile_version)>(
            """
            SELECT d.doc_id, d.doc_name, p.profile_version
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            JOIN document_profiles p
              ON p.tenant_id=d.tenant_id
             AND p.doc_id=d.doc_id
             AND p.revision_id=r.revision_id
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            ORDER BY p.updated_at DESC
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET language='en',
                keywords=ARRAY[]::text[],
                entities=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[],
                limits=ARRAY[]::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

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

        Assert.NotNull(response?.Source?.ProfileSignals);
        Assert.Equal(expected.profile_version, response!.Source!.ProfileSignals!.ProfileVersion);
        Assert.Equal("en", response.Source.ProfileSignals.Language);
        Assert.Null(response.Source.ProfileSignals.Keywords);
        Assert.Null(response.Source.ProfileSignals.Topics);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resolve_source_endpoint_exposes_compact_ocr_diagnostics_without_raw_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resolve_source_endpoint_infers_quality_from_native_status_and_page_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Get_summary_endpoint_exposes_same_enriched_source_as_sources_resolve()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['summary source resolve enrichment']::text[],
              topics=ARRAY['summary profile signal propagation']::text[],
              limits=ARRAY['Do not use document profiles as exact numeric evidence.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

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
        Assert.Equal(resolveResponse.Source.ProfileSignals!.Language, typedSummary.ProfileSignals!.Language);
        Assert.Contains("summary source resolve enrichment", typedSummary.ProfileSignals.Keywords!);
        Assert.Contains("summary profile signal propagation", typedSummary.ProfileSignals.Topics!);
        Assert.Equal(resolveResponse.Source.ProfileSignals.Language, source.GetProperty("profileSignals").GetProperty("language").GetString());

        Assert.Equal(source.GetProperty("profileLanguage").GetString(), root.GetProperty("profileLanguage").GetString());
        Assert.Equal(source.GetProperty("category").GetString(), root.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("categoryPath").GetString(), root.GetProperty("categoryPath").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), root.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(source.GetProperty("profileSignals").GetProperty("language").GetString(), root.GetProperty("profileSignals").GetProperty("language").GetString());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Search_summary_endpoint_exposes_enriched_source_projection_for_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
              @tenant,
              @docId,
              'medium',
              'en',
              @sourceHash,
              'Stored technical summary with PLC integration.',
              '{"generator":"capability_b_worker_v2","strategy":"llm_document_foundation","outputLanguage":"en","fallbackUsed":false,"extractionQuality":{"requiresCaution":true,"documentQualityStatus":"ocr_applied_ok"}}'::jsonb,
              now(),
              now()
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
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['summary search enrichment']::text[],
              topics=ARRAY['summary search profile propagation']::text[],
              limits=ARRAY['Treat profile signals as routing hints.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

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
        Assert.Equal("Programmation/Mettler", item.GetProperty("categoryPath").GetString());
        Assert.True(item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object);
        Assert.Equal(item.GetProperty("sourceHash").GetString(), source.GetProperty("sourceHash").GetString());
        Assert.Equal(item.GetProperty("profileLanguage").GetString(), source.GetProperty("profileLanguage").GetString());
        Assert.Equal(item.GetProperty("category").GetString(), source.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString(), item.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), item.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString(), item.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal("en", item.GetProperty("profileSignals").GetProperty("language").GetString());
        Assert.Equal("summary search enrichment", item.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
        Assert.Equal(source.GetProperty("profileSignals").GetProperty("topics")[0].GetString(), item.GetProperty("profileSignals").GetProperty("topics")[0].GetString());
        var meta = item.GetProperty("meta");
        Assert.Equal("capability_b_worker_v2", meta.GetProperty("generator").GetString());
        Assert.Equal("llm_document_foundation", meta.GetProperty("strategy").GetString());
        Assert.Equal("en", meta.GetProperty("outputLanguage").GetString());
        Assert.False(meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.True(meta.GetProperty("extractionQuality").GetProperty("requiresCaution").GetBoolean());
        Assert.Equal("ocr_applied_ok", meta.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Get_summary_endpoint_keeps_stale_summary_behavior_before_source_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Resolve_source_endpoint_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Rag_search_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item =>
        {
            Assert.NotNull(item.ExtractionQuality);
            Assert.Equal("extraction_ok", item.ExtractionQuality!.DocumentQualityStatus);
            Assert.DoesNotContain("stale_revision_should_not_leak", item.ExtractionQuality.Signals ?? []);
            Assert.False(item.ExtractionQuality.OcrRecommended.GetValueOrDefault());
        });
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Rag_query_legacy_endpoint_returns_enriched_match_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        Assert.InRange(matches.GetArrayLength(), 1, 3);
        var documentItems = matches.EnumerateArray().Where(
            static match => string.Equals(match.GetProperty("docPath").GetString(), "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item =>
        {
            Assert.True(item.TryGetProperty("sourceHash", out var sourceHash));
            Assert.False(string.IsNullOrWhiteSpace(sourceHash.GetString()));
            Assert.True(item.TryGetProperty("docLanguage", out var docLanguage));
            Assert.Equal("en", docLanguage.GetString());
            Assert.True(item.TryGetProperty("extractionQuality", out var quality));
            Assert.Equal("extraction_ok", quality.GetProperty("documentQualityStatus").GetString());
            Assert.True(item.TryGetProperty("matchedContentCards", out _));
        });
        Assert.True(root.TryGetProperty("guidance", out var guidance));
        Assert.Equal("answer", guidance.GetProperty("behavior").GetString());
        Assert.True(root.TryGetProperty("metrics", out var metrics));
        Assert.Equal(matches.GetArrayLength(), metrics.GetProperty("returned").GetInt32());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Document_language_falls_back_to_ingestion_run_when_profiles_are_temporarily_missing()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
            entry => string.Equals(entry.GetProperty("DocPath").GetString(), docPath, StringComparison.Ordinal));
        Assert.Equal("nl", listItem.GetProperty("DocLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, listItem.GetProperty("ProfileLanguage").ValueKind);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Admin_extraction_quality_uses_processing_run_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Extraction_quality_admin_endpoint_reports_llm_enrichment_pending_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

            await conn.ExecuteAsync(
                """
                UPDATE document_processing_runs
                SET payload = jsonb_set(
                  COALESCE(payload, '{}'::jsonb),
                  '{retrievalChunkQuality}',
                  '{
                    "totalChunkCount": 12,
                    "searchableChunkCount": 0,
                    "rejectedChunkCount": 12,
                    "manualReviewRecommended": true,
                    "rejectionReasons": {
                      "sparse_text": 12
                    }
                  }'::jsonb,
                  true
                )
                WHERE tenant_id=@tenant
                  AND doc_id=@docId
                  AND action='upsert'
                  AND status='done';
                """,
                new { tenant = tenantId, docId = pendingDocId });
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
        Assert.Equal(1, summary.GetProperty("documentsWithRejectedChunks").GetInt32());
        Assert.Equal(1, summary.GetProperty("documentsWithNoSearchableChunks").GetInt32());
        Assert.Equal(1, summary.GetProperty("documentsWithRetrievalReviewRecommended").GetInt32());

        var category = Assert.Single(payload.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal("Diagnostics", category.GetProperty("categoryPath").GetString());
        Assert.Equal(1, category.GetProperty("llmEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("summaryEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("profileEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("contentCardEvidencePendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithRejectedChunks").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithNoSearchableChunks").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithRetrievalReviewRecommended").GetInt32());

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
        var retrievalChunkQuality = pendingItem.GetProperty("retrievalChunkQuality");
        Assert.Equal(12, retrievalChunkQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, retrievalChunkQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(12, retrievalChunkQuality.GetProperty("rejectedChunkCount").GetInt32());
        Assert.True(retrievalChunkQuality.GetProperty("manualReviewRecommended").GetBoolean());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Extraction_quality_admin_endpoints_tolerate_malformed_processing_payload_values()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd8ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd80000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd80000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/MalformedPayload.pdf";
        var pageText = string.Join(' ', Enumerable.Repeat("A documented inspection records operating conditions and confirms that every valve remains accessible.", 8));

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
            CancellationToken.None, leaseOwner: "test-worker");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Extraction_quality_pages_endpoint_reports_page_level_review_signals()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd40000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd40000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/Quality.pdf";
        var pageOne = string.Join(' ', Enumerable.Repeat("Alpha records the inspection outcome and identifies the operating conditions for each accessible valve.", 3));
        var pageTwoNoise = "yo o | | i Ne | a \\\"------- abgearbeitet __/ sel 48 a) |b";
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(pageTwoNoise), "This fixture must contain a published unit classified as probable OCR noise.");
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
                new ExtractedPdfPage(2, pageTwo, CountWords(pageTwo), pageTwo.Length, [2], ImageCount: 1)
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
                ]), leaseOwner: "test-worker");

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

                INSERT INTO document_page_index(tenant_id, revision_id, page_number, char_count, metadata)
                VALUES(@tenant, 'cdd40000-aaaa-bbbb-cccc-dddddddddddd', 99, 100,
                  '{"wordCount":20,"extractionQuality":{"manualReviewRecommended":true}}'::jsonb);

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
            preview => preview.GetString() == pageTwo.Replace(Environment.NewLine, " "));

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
            static item => string.Equals(item.DocPath, "Diagnostics/Quality.pdf", StringComparison.Ordinal) && item.PageStart == 2);
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
        Assert.Equal(1, diagnostics.PageWarningCount);
        Assert.Equal(1, diagnostics.PageReviewRecommendedCount);
        Assert.Contains("diagnosticSummary", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("imagePageDiagnostics", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("candidatePages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("attemptedPages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr", ragPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret-should-not-leak", ragPayload, StringComparison.Ordinal);

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "Diagnostics", null, 20);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var diagnosticItem = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, diagnosticItem.GetProperty("pageWarningCount").GetInt32());
        Assert.Equal(1, diagnosticItem.GetProperty("pageReviewRecommendedCount").GetInt32());
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_populates_category_ref_and_category_path_from_matched_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("dedeffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using (var catalogDs = NpgsqlDataSource.Create(db.ConnectionString))
        {
            var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
                catalogDs, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            Assert.True(built.Success);
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        var expectedCategoryRef = $"cat_{displayOrder:000}";
        var expectedSourceHash = await conn.ExecuteScalarAsync<string>(
            """
            SELECT LOWER(ENCODE(content_hash, 'hex'))
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
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, match =>
        {
            Assert.Equal("atex", match.Category);
            Assert.Equal("ATEX", match.CategoryPath);
            Assert.Equal(expectedCategoryRef, match.CategoryRef);
            Assert.Equal(expectedSourceHash, match.SourceHash);
            Assert.NotNull(match.ProvenanceInfo);
            Assert.Equal(expectedSourceHash, match.ProvenanceInfo!.SourceHash);
            Assert.False(string.IsNullOrWhiteSpace(match.ProvenanceInfo.Channel));
            Assert.False(string.IsNullOrWhiteSpace(match.RevisionId));
            Assert.True(match.PageStart > 0);
            Assert.True(match.PageEnd >= match.PageStart);
        });
        // Search can legitimately return several channels for a document. Exact
        // offsets are covered by the dedicated SearchExactMatchesAsync scenario.
        var item = documentItems[0];

        Assert.Equal("atex", item.Category);
        Assert.Equal("ATEX", item.CategoryPath);
        Assert.Equal(expectedCategoryRef, item.CategoryRef);
        Assert.Equal(expectedSourceHash, item.SourceHash);
        Assert.False(string.IsNullOrWhiteSpace(item.Snippet));
        Assert.NotNull(item.Context);
        Assert.Equal(item.ChunkType, item.Context!.ChunkType);
        Assert.Equal(item.SectionTitle, item.Context.SectionTitle);
        Assert.Equal(item.HeadingPath, item.Context.HeadingPath);
        Assert.False(item.HypQuestionsMatched);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_sets_hyp_questions_matched_when_query_overlaps_capability_a_questions()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item => Assert.True(item.HypQuestionsMatched));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_merges_hyp_questions_from_all_current_profiles()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task LoadHypQuestionsMatchedByDocPathAsync_normalizes_legacy_match_paths()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matchedByPath = await RetrievalResultProjector.LoadHypQuestionsMatchedByDocPathAsync(
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

        var matched = RetrievalNavigation.ResolveHypQuestionsMatched("\\Generic\\LegacyPathQuestions.pdf", matchedByPath);
        Assert.True(matched.HasValue);
        Assert.True(matched.Value);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchAsync_does_not_call_capability_a_llm_questions_during_interactive_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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
        var items = response!.Items
            .Where(static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.NotNull(item.HypQuestionsMatched));
        Assert.Equal(0, llmFactory.LlmRequestCount);
    }
}
