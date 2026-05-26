using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

internal static class DocumentFoundationRepo
{
    internal const int ContentCardEvidenceSchemaVersion = 2;

    public static async Task PublishUpsertCompletionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        byte[] sourceHash,
        long sourceSize,
        DateTime sourceMtimeUtc,
        int ingestionVersion,
        int indexedVersionBefore,
        int indexedVersionAfter,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        IReadOnlyList<ProjectedContextualTextEntry> contextualTextEntries,
        CancellationToken ct,
        string extractionSource = "pdf_text",
        bool ocrAttempted = false,
        bool ocrApplied = false,
        string? ocrLanguages = null,
        long? ocrDurationMs = null,
        PdfOcrDiagnostics? ocrDiagnostics = null,
        PdfExtractionQualitySummary? nativeExtractionQuality = null,
        IngestionCapabilityAProfileSeed? capabilityAProfileSeed = null)
    {
        var revisionId = BuildStableRevisionId(tenantId, docId, indexedVersionAfter);
        var extractionQuality = PdfExtractionQualitySummary.FromPages(pages);
        var retrievalChunkQuality = IngestionWorker.BuildRetrievalChunkQualitySummary(retrievalChunks);
        var publishableRetrievalChunks = retrievalChunks
            .Where(IngestionWorker.ShouldPublishRetrievalChunk)
            .ToArray();
        var publishableRetrievalChunkIndexes = publishableRetrievalChunks
            .Select(static chunk => chunk.ChunkIndex)
            .ToHashSet();
        var publishableContextualTextEntries = contextualTextEntries
            .Where(entry => publishableRetrievalChunkIndexes.Contains(entry.ChunkIndex))
            .ToArray();
        var documentProfile = DocumentProfileProjector.Project(docPath, pages, sections, units, exactMatchEntries);
        documentProfile = MergeCapabilityAProfileSeed(
            documentProfile,
            capabilityAProfileSeed,
            indexedVersionBefore,
            docPath,
            Path.GetFileName(docPath.Replace('\\', '/')));

        const string revisionSql = @"
INSERT INTO document_revisions(
    revision_id,
    tenant_id,
    doc_id,
    doc_path,
    source_hash,
    source_size,
    source_mtime,
    ingestion_version,
    indexed_version,
    published_at)
VALUES(
    @revision_id,
    @tenant_id,
    @doc_id,
    @doc_path,
    @source_hash,
    @source_size,
    @source_mtime,
    @ingestion_version,
    @indexed_version,
    now())
ON CONFLICT (tenant_id, doc_id, indexed_version) DO UPDATE
SET doc_path = EXCLUDED.doc_path,
    source_hash = EXCLUDED.source_hash,
    source_size = EXCLUDED.source_size,
    source_mtime = EXCLUDED.source_mtime,
    ingestion_version = EXCLUDED.ingestion_version,
    published_at = EXCLUDED.published_at;";

        await conn.ExecuteAsync(new CommandDefinition(revisionSql, new
        {
            revision_id = revisionId,
            tenant_id = tenantId,
            doc_id = docId,
            doc_path = docPath,
            source_hash = sourceHash,
            source_size = sourceSize,
            source_mtime = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
            ingestion_version = ingestionVersion,
            indexed_version = indexedVersionAfter
        }, transaction: tx, cancellationToken: ct));

        await InsertProcessingRunAsync(
            conn,
            tx,
            processingRunId: BuildStableProcessingRunId(jobId),
            tenantId,
            jobId,
            docId,
            docPath,
            revisionId,
            action: "upsert",
            status: "done",
            ingestionVersion,
            indexedVersionBefore,
            indexedVersionAfter,
            sourceHash,
            payload: JsonSerializer.SerializeToElement(new
            {
                published = true,
                sourceSize,
                sourceMtimeUtc = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
                extractionSource,
                ocrAttempted,
                ocrApplied,
                ocrLanguages,
                ocrDurationMs,
                documentLanguage = documentProfile.Language,
                documentLanguageSource = "document_profile_projector",
                documentProfileVersion = documentProfile.ProfileVersion,
                ocrDiagnostics = ocrDiagnostics is null
                    ? null
                    : BuildOcrDiagnosticsPayload(ocrDiagnostics),
                nativeExtractionQuality = nativeExtractionQuality is null
                    ? null
                    : BuildExtractionQualityPayload(nativeExtractionQuality),
                extractionQuality = BuildExtractionQualityPayload(extractionQuality),
                retrievalChunkQuality = BuildRetrievalChunkQualityPayload(retrievalChunkQuality)
            }),
            ct);

        await UpsertPageIndexAsync(conn, tx, tenantId, revisionId, pages, units, retrievalChunks, ocrDiagnostics, ct);
        await UpsertSectionsAsync(conn, tx, tenantId, revisionId, sections, ct);
        await UpsertUnitsAsync(conn, tx, tenantId, revisionId, sections, units, ct);
        await UpsertRetrievalChunksAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, sections, units, publishableRetrievalChunks, ct);
        await UpsertRetrievalChunkLinksAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, publishableRetrievalChunks, ct);
        await UpsertExactMatchEntriesAsync(conn, tx, tenantId, revisionId, sections, units, exactMatchEntries, ct);
        await UpsertContextualTextEntriesAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, sections, units, publishableContextualTextEntries, ct);
        await UpsertDocumentProfileAsync(conn, tx, tenantId, docId, revisionId, documentProfile, ct);
        var titleNavigationIndex = DocumentTitleNavigationProjector.Project(sections, units, publishableRetrievalChunks, documentProfile);
        await UpsertDocumentTitleNavigationIndexAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, documentProfile, titleNavigationIndex, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "page_index", pages, p => $"page:{p.PageNumber}:{p.CharCount}:{Convert.ToHexString(p.Checksum)}", p => p.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "sections", sections, s => $"section:{s.Ordinal}:{s.Level}:{s.PageStart}:{s.PageEnd}:{s.Title}", _ => 0, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "units", units, u => $"unit:{u.Ordinal}:{u.PageStart}:{u.PageEnd}:{u.TokenCount}:{u.Text}", u => u.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "retrieval_chunks", publishableRetrievalChunks, c => $"chunk:{c.ChunkIndex}:{c.PageStart}:{c.PageEnd}:{c.TokenCount}:{c.ChunkType}:{c.Text}", c => c.Text.Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "exact_match_entries", exactMatchEntries, e => $"exact:{e.EntryIndex}:{e.PageStart}:{e.PageEnd}:{e.NormalizedText}", e => e.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "contextual_text_entries", publishableContextualTextEntries, e => $"contextual:{e.EntryIndex}:{e.PageStart}:{e.PageEnd}:{e.Text}", e => e.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_profile", new[] { documentProfile }, p => $"profile:{p.ProfileVersion}:{p.Language}:{p.SearchText}", p => p.SearchText.Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_profile_content_cards", documentProfile.ContentCards, c => $"card:{c.Title}:{c.PageStart}:{c.PageEnd}:{c.Kind}:{string.Join('|', c.Signals)}", c => BuildContentCardSearchText(c).Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_title_anchors", titleNavigationIndex.TitleAnchors, a => $"title-anchor:{a.AnchorIndex}:{a.SourceKind}:{a.SourceOrdinal}:{a.NormalizedTitle}:{a.PageStart}:{a.PageEnd}:{a.Confidence}", a => a.Title.Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_navigation_entries", titleNavigationIndex.NavigationEntries, e => $"navigation-entry:{e.EntryIndex}:{e.SourcePage}:{e.NormalizedLabel}:{e.TargetPageStart}:{e.TargetPageEnd}:{e.ResolutionMethod}:{e.Confidence}", e => e.Label.Length, ct);
    }

    public static async Task<bool> PublishUnsearchableRetrievalDiagnosticsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        byte[] sourceHash,
        long sourceSize,
        DateTime sourceMtimeUtc,
        int ingestionVersion,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        IngestionRetrievalChunkQualitySummary retrievalChunkQuality,
        string extractionSource,
        bool ocrAttempted,
        bool ocrApplied,
        string? ocrLanguages,
        long? ocrDurationMs,
        PdfOcrDiagnostics? ocrDiagnostics,
        PdfExtractionQualitySummary extractionQuality,
        PdfExtractionQualitySummary? nativeExtractionQuality,
        string failureReason,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string stateSql = """
SELECT
    doc_id AS "DocId",
    COALESCE(ingestion_version, 0) AS "IngestionVersion",
    COALESCE(indexed_version, 0) AS "IndexedVersion"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
FOR UPDATE;
""";
        var state = await conn.QueryFirstOrDefaultAsync<FailedExtractionDocumentState>(
            new CommandDefinition(
                stateSql,
                new { tenant_id = tenantId, doc_path = docPath },
                transaction: tx,
                cancellationToken: ct));

        var persistedDocId = state?.DocId ?? docId;
        var indexedVersionBefore = Math.Max(0, state?.IndexedVersion ?? 0);
        if (state is null || state.IngestionVersion != ingestionVersion)
        {
            await tx.CommitAsync(ct);
            await JobRepo.MarkSupersededAndQueueCurrentAsync(
                ds,
                tenantId,
                jobId,
                docPath,
                ingestionVersion,
                ct,
                lastError: "superseded_unsearchable_retrieval_publish");
            return false;
        }

        await PublishUnsearchableRetrievalDiagnosticsInTransactionAsync(
            conn,
            tx,
            tenantId,
            persistedDocId,
            jobId,
            docPath,
            sourceHash,
            sourceSize,
            sourceMtimeUtc,
            ingestionVersion,
            indexedVersionBefore,
            pages,
            units,
            retrievalChunks,
            retrievalChunkQuality,
            extractionSource,
            ocrAttempted,
            ocrApplied,
            ocrLanguages,
            ocrDurationMs,
            ocrDiagnostics,
            extractionQuality,
            nativeExtractionQuality,
            failureReason,
            ct);

        await tx.CommitAsync(ct);
        return true;
    }

    internal static async Task PublishUnsearchableRetrievalDiagnosticsInTransactionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        byte[] sourceHash,
        long sourceSize,
        DateTime sourceMtimeUtc,
        int ingestionVersion,
        int indexedVersionBefore,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        IngestionRetrievalChunkQualitySummary retrievalChunkQuality,
        string extractionSource,
        bool ocrAttempted,
        bool ocrApplied,
        string? ocrLanguages,
        long? ocrDurationMs,
        PdfOcrDiagnostics? ocrDiagnostics,
        PdfExtractionQualitySummary extractionQuality,
        PdfExtractionQualitySummary? nativeExtractionQuality,
        string failureReason,
        CancellationToken ct)
    {
        var pageCount = Math.Max(0, extractionQuality.PageCount);

        const string documentSql = """
UPDATE documents
SET content_hash=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN content_hash
        ELSE @source_hash
    END,
    file_size=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN file_size
        ELSE @source_size
    END,
    file_mtime=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN file_mtime
        ELSE @source_mtime
    END,
    page_count=CASE
        WHEN COALESCE(indexed_version, 0) <= 0 AND @page_count > 0 THEN @page_count
        ELSE page_count
    END,
    status=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE 'error'
    END,
    auto_ingest_paused=true,
    auto_ingest_paused_at=COALESCE(auto_ingest_paused_at, now()),
    auto_ingest_pause_reason=@pause_reason,
    ingestion_version=GREATEST(COALESCE(ingestion_version, 0), @ingestion_version),
    updated_at=now()
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND COALESCE(status, '') NOT IN ('missing','deleted');
""";
        await conn.ExecuteAsync(new CommandDefinition(
            documentSql,
            new
            {
                tenant_id = tenantId,
                doc_path = docPath,
                source_hash = sourceHash,
                source_size = sourceSize,
                source_mtime = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
                page_count = pageCount,
                pause_reason = failureReason,
                ingestion_version = ingestionVersion
            },
            transaction: tx,
            cancellationToken: ct));

        await InsertProcessingRunAsync(
            conn,
            tx,
            processingRunId: BuildStableProcessingRunId(jobId),
            tenantId,
            jobId,
            docId,
            docPath,
            revisionId: null,
            action: "upsert",
            status: "failed",
            ingestionVersion,
            indexedVersionBefore,
            indexedVersionAfter: indexedVersionBefore,
            sourceHash,
            payload: JsonSerializer.SerializeToElement(new
            {
                published = false,
                documentIndexable = false,
                diagnosticVersion = "retrieval_quality_failure_v1",
                diagnosticScope = "failed_run",
                failureReason,
                sourceSize,
                sourceMtimeUtc = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
                extractionSource,
                ocrAttempted,
                ocrApplied,
                ocrLanguages,
                ocrDurationMs,
                ocrDiagnostics = ocrDiagnostics is null
                    ? null
                    : BuildOcrDiagnosticsPayload(ocrDiagnostics),
                nativeExtractionQuality = nativeExtractionQuality is null
                    ? null
                    : BuildExtractionQualityPayload(nativeExtractionQuality),
                extractionQuality = BuildExtractionQualityPayload(extractionQuality),
                retrievalChunkQuality = BuildRetrievalChunkQualityPayload(retrievalChunkQuality),
                pageDiagnostics = BuildFailedPageDiagnosticsPayload(pages, ocrDiagnostics, units, retrievalChunks)
            }),
            ct);

        const string failedJobSql = @"UPDATE ingestion_jobs
SET status='failed',
    finished_at=now(),
    last_error=@last_error,
    payload=jsonb_set(
        jsonb_set(
            COALESCE(payload, '{}'::jsonb),
            '{quality,retrieval}',
            CAST(@retrieval_quality AS jsonb),
            true),
        '{progress}',
        jsonb_build_object(
            'phase', 'failed_no_searchable_chunks',
            'current', 0,
            'total', @total_chunks,
            'percent', 0),
        true),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
        await conn.ExecuteAsync(new CommandDefinition(
            failedJobSql,
            new
            {
                job_id = jobId,
                last_error = failureReason,
                total_chunks = retrievalChunkQuality.TotalChunkCount,
                retrieval_quality = SerializePostgresJsonForStorage(BuildRetrievalChunkQualityPayload(retrievalChunkQuality))
            },
            transaction: tx,
            cancellationToken: ct));

        await JobRepo.FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
    }

    public static async Task PublishDeleteCompletionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        int ingestionVersion,
        int indexedVersionBefore,
        CancellationToken ct)
    {
        await InsertProcessingRunAsync(
            conn,
            tx,
            processingRunId: BuildStableProcessingRunId(jobId),
            tenantId,
            jobId,
            docId,
            docPath,
            revisionId: null,
            action: "delete",
            status: "done",
            ingestionVersion,
            indexedVersionBefore,
            indexedVersionAfter: 0,
            sourceHash: null,
            payload: JsonSerializer.SerializeToElement(new
            {
                published = false,
                deleted = true
            }),
            ct);
    }

    public static async Task<bool> PublishFailedOcrExtractionAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        byte[] sourceHash,
        long sourceSize,
        DateTime sourceMtimeUtc,
        int ingestionVersion,
        string extractionSource,
        bool ocrAttempted,
        string? ocrLanguages,
        long? ocrDurationMs,
        PdfOcrDiagnostics? ocrDiagnostics,
        PdfExtractionQualitySummary extractionQuality,
        PdfExtractionQualitySummary? nativeExtractionQuality,
        string failureReason,
        CancellationToken ct,
        IReadOnlyList<ExtractedPdfPage>? extractedPages = null)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string stateSql = """
SELECT
    doc_id AS "DocId",
    COALESCE(ingestion_version, 0) AS "IngestionVersion",
    COALESCE(indexed_version, 0) AS "IndexedVersion"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
FOR UPDATE;
""";
        var state = await conn.QueryFirstOrDefaultAsync<FailedExtractionDocumentState>(
            new CommandDefinition(
                stateSql,
                new { tenant_id = tenantId, doc_path = docPath },
                transaction: tx,
                cancellationToken: ct));

        var persistedDocId = state?.DocId ?? docId;
        var indexedVersionBefore = Math.Max(0, state?.IndexedVersion ?? 0);
        if (state is null || state.IngestionVersion != ingestionVersion)
        {
            await tx.CommitAsync(ct);
            await JobRepo.MarkSupersededAndQueueCurrentAsync(
                ds,
                tenantId,
                jobId,
                docPath,
                ingestionVersion,
                ct,
                lastError: "superseded_failed_ocr_publish");
            return false;
        }

        var pageCount = Math.Max(0, extractionQuality.PageCount);

        const string documentSql = """
UPDATE documents
SET content_hash=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN content_hash
        ELSE @source_hash
    END,
    file_size=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN file_size
        ELSE @source_size
    END,
    file_mtime=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN file_mtime
        ELSE @source_mtime
    END,
    page_count=CASE
        WHEN COALESCE(indexed_version, 0) <= 0 AND @page_count > 0 THEN @page_count
        ELSE page_count
    END,
    status=CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE 'error'
    END,
    auto_ingest_paused=true,
    auto_ingest_paused_at=COALESCE(auto_ingest_paused_at, now()),
    auto_ingest_pause_reason=@pause_reason,
    ingestion_version=GREATEST(COALESCE(ingestion_version, 0), @ingestion_version),
    updated_at=now()
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND COALESCE(status, '') NOT IN ('missing','deleted');
""";
        await conn.ExecuteAsync(new CommandDefinition(
            documentSql,
            new
            {
                tenant_id = tenantId,
                doc_path = docPath,
                source_hash = sourceHash,
                source_size = sourceSize,
                source_mtime = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
                page_count = pageCount,
                pause_reason = failureReason,
                ingestion_version = ingestionVersion
            },
            transaction: tx,
            cancellationToken: ct));

        await InsertProcessingRunAsync(
            conn,
            tx,
            processingRunId: BuildStableProcessingRunId(jobId),
            tenantId,
            jobId,
            persistedDocId,
            docPath,
            revisionId: null,
            action: "upsert",
            status: "failed",
            ingestionVersion,
            indexedVersionBefore,
            indexedVersionAfter: indexedVersionBefore,
            sourceHash,
            payload: JsonSerializer.SerializeToElement(new
            {
                published = false,
                documentIndexable = false,
                diagnosticVersion = "extraction_failure_v1",
                diagnosticScope = "failed_run",
                failureReason,
                sourceSize,
                sourceMtimeUtc = DateTime.SpecifyKind(sourceMtimeUtc, DateTimeKind.Utc),
                extractionSource,
                ocrAttempted,
                ocrApplied = false,
                ocrLanguages,
                ocrDurationMs,
                ocrDiagnostics = ocrDiagnostics is null
                    ? null
                    : BuildOcrDiagnosticsPayload(ocrDiagnostics),
                nativeExtractionQuality = nativeExtractionQuality is null
                    ? null
                    : BuildExtractionQualityPayload(nativeExtractionQuality),
                extractionQuality = BuildExtractionQualityPayload(extractionQuality),
                pageDiagnostics = BuildFailedPageDiagnosticsPayload(extractedPages, ocrDiagnostics)
            }),
            ct);

        await tx.CommitAsync(ct);
        return true;
    }

    internal static Guid BuildStableRevisionId(Guid tenantId, Guid docId, int indexedVersion)
        => IdUtil.DeterministicGuid($"{tenantId:N}|{docId:N}|rev|{indexedVersion}");

    internal static Guid BuildStableProcessingRunId(Guid jobId)
        => IdUtil.DeterministicGuid($"{jobId:N}|processing-run");

    internal static Guid BuildStableArtifactId(Guid revisionId, string artifactType)
        => IdUtil.DeterministicGuid($"{revisionId:N}|artifact|{artifactType}");

    internal static Guid BuildStableDocumentProfileId(Guid revisionId, string profileVersion)
        => IdUtil.DeterministicGuid($"{revisionId:N}|document-profile|{profileVersion}");

    internal static Guid BuildStableDocumentProfileContentCardId(Guid documentProfileId, string normalizedTitle)
        => IdUtil.DeterministicGuid($"{documentProfileId:N}|content-card|{NormalizeContentCardLookupText(normalizedTitle)}");

    internal static Guid BuildStableDocumentTitleAnchorId(Guid revisionId, int anchorIndex)
        => IdUtil.DeterministicGuid($"{revisionId:N}|title-anchor|{anchorIndex}");

    internal static Guid BuildStableDocumentNavigationEntryId(Guid revisionId, int entryIndex)
        => IdUtil.DeterministicGuid($"{revisionId:N}|navigation-entry|{entryIndex}");

    internal static string NormalizePostgresTextForStorage(string? text)
        => PostgresTextSanitizer.Clean(text);

    private static string? NormalizeOptionalPostgresTextForStorage(string? text)
        => PostgresTextSanitizer.CleanOrNull(text);

    private static string[] NormalizePostgresTextArrayForStorage(IEnumerable<string>? values)
        => PostgresTextSanitizer.CleanArray(values);

    private static string SerializePostgresJsonForStorage<T>(T value)
        => PostgresTextSanitizer.CleanJson(JsonSerializer.Serialize(value)) ?? "{}";

    private static byte[] ComputeStoredTextChecksum(string text)
        => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static async Task InsertProcessingRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid processingRunId,
        Guid tenantId,
        Guid jobId,
        Guid docId,
        string docPath,
        Guid? revisionId,
        string action,
        string status,
        int ingestionVersion,
        int indexedVersionBefore,
        int indexedVersionAfter,
        byte[]? sourceHash,
        JsonElement payload,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO document_processing_runs(
    processing_run_id,
    tenant_id,
    job_id,
    doc_id,
    doc_path,
    revision_id,
    action,
    status,
    ingestion_version,
    indexed_version_before,
    indexed_version_after,
    source_hash,
    started_at,
    finished_at,
    payload)
SELECT
    @processing_run_id,
    @tenant_id,
    @job_id,
    @doc_id,
    @doc_path,
    @revision_id,
    @action,
    @status,
    @ingestion_version,
    @indexed_version_before,
    @indexed_version_after,
    @source_hash,
    j.started_at,
    COALESCE(j.finished_at, now()),
    CAST(@payload AS jsonb)
FROM ingestion_jobs j
WHERE j.job_id=@job_id
ON CONFLICT (tenant_id, job_id) DO UPDATE
SET revision_id = EXCLUDED.revision_id,
    status = EXCLUDED.status,
    ingestion_version = EXCLUDED.ingestion_version,
    indexed_version_before = EXCLUDED.indexed_version_before,
    indexed_version_after = EXCLUDED.indexed_version_after,
    source_hash = EXCLUDED.source_hash,
    started_at = EXCLUDED.started_at,
    finished_at = EXCLUDED.finished_at,
    payload = EXCLUDED.payload;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            processing_run_id = processingRunId,
            tenant_id = tenantId,
            job_id = jobId,
            doc_id = docId,
            doc_path = docPath,
            revision_id = revisionId,
            action,
            status,
            ingestion_version = ingestionVersion,
            indexed_version_before = indexedVersionBefore,
            indexed_version_after = indexedVersionAfter,
            source_hash = sourceHash,
            payload = SerializePostgresJsonForStorage(payload)
        }, transaction: tx, cancellationToken: ct));
    }

    private static async Task UpsertArtifactSummaryAsync<T>(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        string artifactType,
        IReadOnlyList<T> items,
        Func<T, string> canonicalSelector,
        Func<T, long> byteSizeSelector,
        CancellationToken ct)
    {
        var artifactId = BuildStableArtifactId(revisionId, artifactType);
        byte[]? contentHash = null;
        long? byteSize = null;
        long charCountTotal = 0;

        if (items.Count > 0)
        {
            using var sha = SHA256.Create();
            foreach (var item in items)
            {
                var canonical = canonicalSelector(item);
                var bytes = Encoding.UTF8.GetBytes(canonical);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                charCountTotal += canonical.Length;
            }

            var terminalBytes = Encoding.UTF8.GetBytes($"{artifactType}:{items.Count}");
            sha.TransformFinalBlock(terminalBytes, 0, terminalBytes.Length);
            contentHash = sha.Hash;
            byteSize = items.Sum(byteSizeSelector);
        }

        var payload = SerializePostgresJsonForStorage(new
        {
            count = items.Count,
            hashBasis = "canonical_text_v1",
            charCountTotal
        });

        const string sql = @"
INSERT INTO document_revision_artifacts(
    artifact_id,
    tenant_id,
    revision_id,
    artifact_type,
    content_hash,
    byte_size,
    payload)
VALUES(
    @artifact_id,
    @tenant_id,
    @revision_id,
    @artifact_type,
    @content_hash,
    @byte_size,
    CAST(@payload AS jsonb))
ON CONFLICT (revision_id, artifact_type) DO UPDATE
SET content_hash = EXCLUDED.content_hash,
    byte_size = EXCLUDED.byte_size,
    payload = EXCLUDED.payload;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            artifact_id = artifactId,
            tenant_id = tenantId,
            revision_id = revisionId,
            artifact_type = artifactType,
            content_hash = contentHash,
            byte_size = byteSize,
            payload
        }, transaction: tx, cancellationToken: ct));
    }

    private static ProjectedDocumentProfile MergeCapabilityAProfileSeed(
        ProjectedDocumentProfile profile,
        IngestionCapabilityAProfileSeed? seed,
        int indexedVersionBefore,
        string docPath,
        string docName)
    {
        seed = IngestionJobPayloadJson.NormalizeCapabilityAProfileSeed(seed);
        if (seed is null)
            return profile;

        if (seed.BasedOnIndexedVersion.HasValue && seed.BasedOnIndexedVersion.Value != indexedVersionBefore)
            return profile;

        var summary = MergeSummaryPreview(profile.SummaryText, seed.PreviewText);
        var suggestedTags = seed.SuggestedTags ?? [];
        var keySectionTitles = seed.KeySectionTitles ?? [];
        var seedQuestions = seed.HypotheticalQuestions ?? [];

        return DocumentProfileProjector.BuildProfile(
            profileVersion: profile.ProfileVersion,
            language: profile.Language,
            summaryText: summary,
            keywords: suggestedTags.Concat(profile.Keywords),
            entities: profile.Entities,
            topics: keySectionTitles.Concat(suggestedTags).Concat(profile.Topics),
            hypotheticalQuestions: seedQuestions.Concat(profile.HypotheticalQuestions),
            limits: profile.Limits,
            docPath: docPath,
            docName: docName,
            contentCards: profile.ContentCards);
    }

    private static string MergeSummaryPreview(string summary, string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
            return summary;

        var normalizedPreview = NormalizePostgresTextForStorage(preview);
        if (string.IsNullOrWhiteSpace(normalizedPreview))
            return summary;

        if (!string.IsNullOrWhiteSpace(summary)
            && summary.Contains(normalizedPreview, StringComparison.OrdinalIgnoreCase))
        {
            return summary;
        }

        return string.IsNullOrWhiteSpace(summary)
            ? normalizedPreview
            : $"{summary.Trim()} {normalizedPreview}";
    }

    internal static async Task<DocumentProfileSnapshot?> LoadDocumentProfileAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        string profileVersion,
        CancellationToken ct)
    {
        var row = await conn.QueryFirstOrDefaultAsync<DocumentProfileSnapshotRow>(new CommandDefinition(
            """
SELECT
    p.revision_id AS "RevisionId",
    p.doc_id AS "DocId",
    p.profile_version AS "ProfileVersion",
    COALESCE(p.language, 'und') AS "Language",
    p.summary_text AS "SummaryText",
    p.keywords AS "Keywords",
    p.entities AS "Entities",
    p.topics AS "Topics",
    p.hypothetical_questions AS "HypotheticalQuestions",
    p.limits AS "Limits",
    p.search_text AS "SearchText",
    p.token_count AS "TokenCount",
    p.metadata::text AS "MetadataJson"
FROM document_profiles p
JOIN document_revisions r
  ON r.revision_id = p.revision_id
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.indexed_version = r.indexed_version
WHERE p.tenant_id = @tenant_id
  AND p.doc_id = @doc_id
  AND p.profile_version = @profile_version
  AND d.status = 'indexed'
LIMIT 1;
""",
            new
            {
                tenant_id = tenantId,
                doc_id = docId,
                profile_version = profileVersion
            },
            cancellationToken: ct));

        if (row is null)
            return null;

        var contentCards = await LoadDocumentProfileContentCardsAsync(
            conn,
            row.RevisionId,
            row.ProfileVersion ?? profileVersion,
            ct);
        if (contentCards.Count == 0)
            contentCards = DocumentProfileProjector.ParseContentCards(row.MetadataJson);

        return new DocumentProfileSnapshot(
            row.RevisionId,
            row.DocId,
            row.ProfileVersion ?? profileVersion,
            row.Language ?? "und",
            row.SummaryText ?? string.Empty,
            row.Keywords ?? [],
            row.Entities ?? [],
            row.Topics ?? [],
            row.HypotheticalQuestions ?? [],
            row.Limits ?? [],
            row.SearchText ?? string.Empty,
            row.TokenCount,
            contentCards);
    }

    private static async Task<IReadOnlyList<DocumentProfileContentCard>> LoadDocumentProfileContentCardsAsync(
        NpgsqlConnection conn,
        Guid revisionId,
        string profileVersion,
        CancellationToken ct)
    {
        try
        {
            var rows = await conn.QueryAsync<DocumentProfileContentCardRow>(new CommandDefinition(
                """
SELECT
    title AS "Title",
    content_card_id AS "ContentCardId",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    kind AS "Kind",
    signals AS "Signals",
    metadata::text AS "MetadataJson"
FROM document_profile_content_cards
WHERE revision_id = @revision_id
  AND profile_version = @profile_version
ORDER BY card_index;
""",
                new
                {
                    revision_id = revisionId,
                    profile_version = profileVersion
                },
                cancellationToken: ct));

            return rows
                .Select(static row => new DocumentProfileContentCard(
                    row.Title ?? string.Empty,
                    row.PageStart,
                    row.PageEnd,
                    row.Kind ?? "content_item",
                    row.Signals ?? [],
                    DocumentProfileProjector.ParseContentCardEvidenceFromMetadata(row.MetadataJson),
                    row.ContentCardId?.ToString()))
                .ToArray();
        }
        catch (PostgresException)
        {
            return [];
        }
    }

    internal static async Task UpsertDocumentProfileAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        ProjectedDocumentProfile profile,
        CancellationToken ct)
    {
        if (tx is null)
        {
            await using var localTx = await conn.BeginTransactionAsync(ct);
            await UpsertDocumentProfileAsync(conn, localTx, tenantId, docId, revisionId, profile, ct);
            await localTx.CommitAsync(ct);
            return;
        }

        const string sql = @"
INSERT INTO document_profiles(
    document_profile_id,
    tenant_id,
    revision_id,
    doc_id,
    profile_version,
    language,
    summary_text,
    keywords,
    entities,
    topics,
    hypothetical_questions,
    limits,
    search_text,
    token_count,
    checksum,
    metadata)
VALUES(
    @document_profile_id,
    @tenant_id,
    @revision_id,
    @doc_id,
    @profile_version,
    @language,
    @summary_text,
    @keywords,
    @entities,
    @topics,
    @hypothetical_questions,
    @limits,
    @search_text,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, profile_version) DO UPDATE
SET language = EXCLUDED.language,
    summary_text = EXCLUDED.summary_text,
    keywords = EXCLUDED.keywords,
    entities = EXCLUDED.entities,
    topics = EXCLUDED.topics,
    hypothetical_questions = EXCLUDED.hypothetical_questions,
    limits = EXCLUDED.limits,
    search_text = EXCLUDED.search_text,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata,
    updated_at = now();";

        var documentProfileId = BuildStableDocumentProfileId(revisionId, profile.ProfileVersion);
        var profileVersion = NormalizePostgresTextForStorage(profile.ProfileVersion);
        var profileLanguage = string.Equals(profile.Language, "und", StringComparison.Ordinal)
            ? null
            : NormalizeOptionalPostgresTextForStorage(profile.Language);
        var summaryText = NormalizePostgresTextForStorage(profile.SummaryText);
        var keywords = NormalizePostgresTextArrayForStorage(profile.Keywords);
        var entities = NormalizePostgresTextArrayForStorage(profile.Entities);
        var topics = NormalizePostgresTextArrayForStorage(profile.Topics);
        var hypotheticalQuestions = NormalizePostgresTextArrayForStorage(profile.HypotheticalQuestions);
        var limits = NormalizePostgresTextArrayForStorage(profile.Limits);
        var searchText = NormalizePostgresTextForStorage(profile.SearchText);

        var metadata = SerializePostgresJsonForStorage(new
        {
            generatedBy = "document_profile_projector",
            ProfileVersion = profileVersion,
            contentCardEvidenceSchemaVersion = ContentCardEvidenceSchemaVersion,
            keywordCount = keywords.Length,
            entityCount = entities.Length,
            topicCount = topics.Length,
            hypotheticalQuestionCount = hypotheticalQuestions.Length,
            contentCardCount = profile.ContentCards.Count,
            contentCards = profile.ContentCards.Select(card => new
            {
                title = NormalizePostgresTextForStorage(card.Title),
                pageStart = card.PageStart,
                pageEnd = card.PageEnd,
                kind = NormalizePostgresTextForStorage(card.Kind),
                signals = NormalizePostgresTextArrayForStorage(card.Signals),
                evidence = card.Evidence
            })
        });

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            document_profile_id = documentProfileId,
            tenant_id = tenantId,
            revision_id = revisionId,
            doc_id = docId,
            profile_version = profileVersion,
            language = profileLanguage,
            summary_text = summaryText,
            keywords,
            entities,
            topics,
            hypothetical_questions = hypotheticalQuestions,
            limits,
            search_text = searchText,
            token_count = CountTokens(searchText),
            checksum = ComputeStoredTextChecksum(searchText),
            metadata
        }, transaction: tx, cancellationToken: ct));

        await UpsertDocumentProfileContentCardsAsync(
            conn,
            tx,
            tenantId,
            docId,
            revisionId,
            documentProfileId,
            profile,
            ct);
        await RefreshDocumentProfileSearchEntryAsync(conn, tx, tenantId, revisionId, ct);
    }

    internal static Task RefreshDocumentProfileSearchEntryAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid tenantId,
        Guid revisionId,
        CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "SELECT saaia_refresh_document_profile_search_entry(@tenant_id, @revision_id);",
            new { tenant_id = tenantId, revision_id = revisionId },
            transaction: tx,
            cancellationToken: ct));

    private static async Task UpsertDocumentProfileContentCardsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        Guid documentProfileId,
        ProjectedDocumentProfile profile,
        CancellationToken ct)
    {
        const string purgeSql = @"
DELETE FROM document_profile_content_cards
WHERE tenant_id = @tenant_id
  AND (
    document_profile_id = @document_profile_id
    OR (revision_id = @revision_id AND profile_version = @profile_version)
  );";
        await conn.ExecuteAsync(new CommandDefinition(
            purgeSql,
            new
            {
                tenant_id = tenantId,
                document_profile_id = documentProfileId,
                revision_id = revisionId,
                profile_version = NormalizePostgresTextForStorage(profile.ProfileVersion)
            },
            transaction: tx,
            cancellationToken: ct));

        if (profile.ContentCards.Count == 0)
            return;

        const string sql = @"
INSERT INTO document_profile_content_cards(
    content_card_id,
    tenant_id,
    document_profile_id,
    revision_id,
    doc_id,
    profile_version,
    card_index,
    title,
    normalized_title,
    page_start,
    page_end,
    kind,
    signals,
    search_text,
    token_count,
    checksum,
    metadata)
VALUES(
    @content_card_id,
    @tenant_id,
    @document_profile_id,
    @revision_id,
    @doc_id,
    @profile_version,
    @card_index,
    @title,
    @normalized_title,
    @page_start,
    @page_end,
    @kind,
    @signals,
    @search_text,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, profile_version, normalized_title) DO UPDATE
SET card_index = EXCLUDED.card_index,
    title = EXCLUDED.title,
    document_profile_id = EXCLUDED.document_profile_id,
    doc_id = EXCLUDED.doc_id,
    normalized_title = EXCLUDED.normalized_title,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    kind = EXCLUDED.kind,
    signals = EXCLUDED.signals,
    search_text = EXCLUDED.search_text,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata,
    updated_at = now();";

        var seenNormalizedTitles = new HashSet<string>(StringComparer.Ordinal);
        var cardIndex = 0;
        foreach (var card in profile.ContentCards)
        {
            var title = NormalizePostgresTextForStorage(card.Title);
            var kind = NormalizePostgresTextForStorage(card.Kind);
            var signals = NormalizePostgresTextArrayForStorage(card.Signals);
            var storedCard = card with
            {
                Title = title,
                Kind = kind,
                Signals = signals
            };
            var searchText = BuildContentCardSearchText(storedCard);
            var normalizedTitle = NormalizeContentCardLookupText(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle) || !seenNormalizedTitles.Add(normalizedTitle))
                continue;
            var contentCardId = BuildStableDocumentProfileContentCardId(documentProfileId, normalizedTitle);
            storedCard = storedCard with { ContentCardId = contentCardId.ToString() };

            var metadata = SerializePostgresJsonForStorage(new
            {
                generatedBy = "document_profile_projector",
                ProfileVersion = NormalizePostgresTextForStorage(profile.ProfileVersion),
                contentCardEvidenceSchemaVersion = ContentCardEvidenceSchemaVersion,
                signalCount = signals.Length,
                contentCardId = contentCardId.ToString(),
                evidence = card.Evidence
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                content_card_id = contentCardId,
                tenant_id = tenantId,
                document_profile_id = documentProfileId,
                revision_id = revisionId,
                doc_id = docId,
                profile_version = NormalizePostgresTextForStorage(profile.ProfileVersion),
                card_index = cardIndex,
                title,
                normalized_title = normalizedTitle,
                page_start = card.PageStart,
                page_end = card.PageEnd,
                kind = string.IsNullOrWhiteSpace(kind) ? "content_item" : kind,
                signals,
                search_text = searchText,
                token_count = CountTokens(searchText),
                checksum = ComputeStoredTextChecksum(searchText),
                metadata
            }, transaction: tx, cancellationToken: ct));

            cardIndex++;
        }
    }

    private static async Task UpsertDocumentTitleNavigationIndexAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        int ingestionVersion,
        ProjectedDocumentProfile documentProfile,
        ProjectedDocumentTitleNavigationIndex index,
        CancellationToken ct)
    {
        const string purgeNavigationSql = @"
DELETE FROM document_navigation_entries
WHERE tenant_id = @tenant_id
  AND revision_id = @revision_id;";
        await conn.ExecuteAsync(new CommandDefinition(
            purgeNavigationSql,
            new { tenant_id = tenantId, revision_id = revisionId },
            transaction: tx,
            cancellationToken: ct));

        const string purgeAnchorsSql = @"
DELETE FROM document_title_anchors
WHERE tenant_id = @tenant_id
  AND revision_id = @revision_id;";
        await conn.ExecuteAsync(new CommandDefinition(
            purgeAnchorsSql,
            new { tenant_id = tenantId, revision_id = revisionId },
            transaction: tx,
            cancellationToken: ct));

        var documentProfileId = BuildStableDocumentProfileId(revisionId, documentProfile.ProfileVersion);
        var anchorIdsByIndex = new Dictionary<int, Guid>();
        if (index.TitleAnchors.Count > 0)
        {
            const string anchorSql = @"
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
    @title_anchor_id,
    @tenant_id,
    @revision_id,
    @doc_id,
    @anchor_index,
    @source_kind,
    @source_ordinal,
    @section_id,
    @unit_id,
    @retrieval_chunk_id,
    @content_card_id,
    @title,
    @normalized_title,
    @title_tokens,
    @page_start,
    @page_end,
    @confidence,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, anchor_index) DO UPDATE
SET source_kind = EXCLUDED.source_kind,
    source_ordinal = EXCLUDED.source_ordinal,
    section_id = EXCLUDED.section_id,
    unit_id = EXCLUDED.unit_id,
    retrieval_chunk_id = EXCLUDED.retrieval_chunk_id,
    content_card_id = EXCLUDED.content_card_id,
    title = EXCLUDED.title,
    normalized_title = EXCLUDED.normalized_title,
    title_tokens = EXCLUDED.title_tokens,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    confidence = EXCLUDED.confidence,
    metadata = EXCLUDED.metadata,
    updated_at = now();";

            foreach (var anchor in index.TitleAnchors)
            {
                var titleAnchorId = BuildStableDocumentTitleAnchorId(revisionId, anchor.AnchorIndex);
                anchorIdsByIndex[anchor.AnchorIndex] = titleAnchorId;
                var normalizedTitle = NormalizePostgresTextForStorage(anchor.NormalizedTitle);
                var contentCardId = anchor.ContentCardIndex is null || string.IsNullOrWhiteSpace(normalizedTitle)
                    ? (Guid?)null
                    : BuildStableDocumentProfileContentCardId(documentProfileId, normalizedTitle);
                var metadata = SerializePostgresJsonForStorage(new
                {
                    generatedBy = "document_title_navigation_projector",
                    schemaVersion = "title_navigation_v1",
                    ingestionVersion,
                    documentProfileVersion = NormalizePostgresTextForStorage(documentProfile.ProfileVersion),
                    sourceKind = NormalizePostgresTextForStorage(anchor.SourceKind),
                    sourceOrdinal = anchor.SourceOrdinal,
                    contentCardIndex = anchor.ContentCardIndex,
                    tokenCount = anchor.TitleTokens.Count
                });

                await conn.ExecuteAsync(new CommandDefinition(anchorSql, new
                {
                    title_anchor_id = titleAnchorId,
                    tenant_id = tenantId,
                    revision_id = revisionId,
                    doc_id = docId,
                    anchor_index = anchor.AnchorIndex,
                    source_kind = NormalizePostgresTextForStorage(anchor.SourceKind),
                    source_ordinal = anchor.SourceOrdinal,
                    section_id = anchor.SectionOrdinal is null ? (Guid?)null : BuildStableSectionId(revisionId, anchor.SectionOrdinal.Value),
                    unit_id = anchor.UnitOrdinal is null ? (Guid?)null : BuildStableUnitId(revisionId, anchor.UnitOrdinal.Value),
                    retrieval_chunk_id = anchor.ChunkIndex is null ? (Guid?)null : BuildStableRetrievalChunkId(docId, ingestionVersion, anchor.ChunkIndex.Value),
                    content_card_id = contentCardId,
                    title = NormalizePostgresTextForStorage(anchor.Title),
                    normalized_title = normalizedTitle,
                    title_tokens = NormalizePostgresTextArrayForStorage(anchor.TitleTokens),
                    page_start = anchor.PageStart,
                    page_end = anchor.PageEnd,
                    confidence = anchor.Confidence,
                    metadata
                }, transaction: tx, cancellationToken: ct));
            }
        }

        if (index.NavigationEntries.Count == 0)
            return;

        const string navigationSql = @"
INSERT INTO document_navigation_entries(
    navigation_entry_id,
    tenant_id,
    revision_id,
    doc_id,
    entry_index,
    source_page,
    source_chunk_id,
    source_unit_id,
    label,
    normalized_label,
    label_tokens,
    target_anchor_id,
    target_chunk_id,
    target_page_start,
    target_page_end,
    resolution_method,
    confidence,
    metadata)
VALUES(
    @navigation_entry_id,
    @tenant_id,
    @revision_id,
    @doc_id,
    @entry_index,
    @source_page,
    @source_chunk_id,
    @source_unit_id,
    @label,
    @normalized_label,
    @label_tokens,
    @target_anchor_id,
    @target_chunk_id,
    @target_page_start,
    @target_page_end,
    @resolution_method,
    @confidence,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, entry_index) DO UPDATE
SET source_page = EXCLUDED.source_page,
    source_chunk_id = EXCLUDED.source_chunk_id,
    source_unit_id = EXCLUDED.source_unit_id,
    label = EXCLUDED.label,
    normalized_label = EXCLUDED.normalized_label,
    label_tokens = EXCLUDED.label_tokens,
    target_anchor_id = EXCLUDED.target_anchor_id,
    target_chunk_id = EXCLUDED.target_chunk_id,
    target_page_start = EXCLUDED.target_page_start,
    target_page_end = EXCLUDED.target_page_end,
    resolution_method = EXCLUDED.resolution_method,
    confidence = EXCLUDED.confidence,
    metadata = EXCLUDED.metadata,
    updated_at = now();";

        foreach (var entry in index.NavigationEntries)
        {
            Guid? targetAnchorId = null;
            if (entry.TargetAnchorIndex is not null
                && anchorIdsByIndex.TryGetValue(entry.TargetAnchorIndex.Value, out var resolvedAnchorId))
            {
                targetAnchorId = resolvedAnchorId;
            }

            var metadata = SerializePostgresJsonForStorage(new
            {
                generatedBy = "document_title_navigation_projector",
                schemaVersion = "title_navigation_v1",
                ingestionVersion,
                resolutionMethod = NormalizePostgresTextForStorage(entry.ResolutionMethod),
                targetAnchorIndex = entry.TargetAnchorIndex,
                targetChunkIndex = entry.TargetChunkIndex,
                tokenCount = entry.LabelTokens.Count
            });

            await conn.ExecuteAsync(new CommandDefinition(navigationSql, new
            {
                navigation_entry_id = BuildStableDocumentNavigationEntryId(revisionId, entry.EntryIndex),
                tenant_id = tenantId,
                revision_id = revisionId,
                doc_id = docId,
                entry_index = entry.EntryIndex,
                source_page = entry.SourcePage,
                source_chunk_id = entry.SourceChunkIndex is null ? (Guid?)null : BuildStableRetrievalChunkId(docId, ingestionVersion, entry.SourceChunkIndex.Value),
                source_unit_id = entry.SourceUnitOrdinal is null ? (Guid?)null : BuildStableUnitId(revisionId, entry.SourceUnitOrdinal.Value),
                label = NormalizePostgresTextForStorage(entry.Label),
                normalized_label = NormalizePostgresTextForStorage(entry.NormalizedLabel),
                label_tokens = NormalizePostgresTextArrayForStorage(entry.LabelTokens),
                target_anchor_id = targetAnchorId,
                target_chunk_id = entry.TargetChunkIndex is null ? (Guid?)null : BuildStableRetrievalChunkId(docId, ingestionVersion, entry.TargetChunkIndex.Value),
                target_page_start = entry.TargetPageStart,
                target_page_end = entry.TargetPageEnd,
                resolution_method = NormalizePostgresTextForStorage(entry.ResolutionMethod),
                confidence = entry.Confidence,
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static string BuildContentCardSearchText(DocumentProfileContentCard card)
        => string.Join(
            ' ',
            new[]
            {
                card.Title,
                NormalizeContentCardLookupText(card.Title),
                card.Kind,
                string.Join(' ', card.Signals),
                NormalizeContentCardLookupText(string.Join(' ', card.Signals)),
                BuildContentCardEvidenceSearchText(card.Evidence)
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildContentCardEvidenceSearchText(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();
        if (evidence.ScaleBasis is { Count: > 0 } basis)
        {
            parts.Add("scale_basis");
            parts.Add($"scale_basis_count:{basis.Count}");
            if (!string.IsNullOrWhiteSpace(basis.Label))
                parts.Add($"scale_basis_label:{basis.Label}");
        }

        if (evidence.QuantityFacts.Count >= 2)
            parts.Add("quantity_list");
        if (evidence.ScaleBasis is { Count: > 0 }
            && evidence.QuantityFacts.Count >= 2
            && evidence.NonScalableReasons.Count == 0)
        {
            parts.Add("scalable_quantities");
        }

        if (!string.IsNullOrWhiteSpace(evidence.Language))
            parts.Add($"language:{evidence.Language}");
        if ((evidence.Facts ?? []).Count > 0)
            parts.Add("structured_facts");
        parts.AddRange(evidence.QuantityFacts.Select(static fact => $"{fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Unit} {fact.Label}"));
        parts.AddRange(evidence.NonScalableReasons);
        parts.AddRange((evidence.Facts ?? []).Select(static fact => string.Join(' ', new[]
        {
            fact.Kind,
            fact.Label,
            fact.Value,
            fact.Unit,
            fact.SourceText
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
        return NormalizeContentCardLookupText(string.Join(' ', parts));
    }

    private static string NormalizeContentCardLookupText(string text)
        => FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(text));

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                _ = ch switch
                {
                    'œ' => sb.Append("oe"),
                    'Œ' => sb.Append("OE"),
                    'æ' => sb.Append("ae"),
                    'Æ' => sb.Append("AE"),
                    'ß' => sb.Append("ss"),
                    'ø' => sb.Append('o'),
                    'Ø' => sb.Append('O'),
                    'ł' => sb.Append('l'),
                    'Ł' => sb.Append('L'),
                    'đ' => sb.Append('d'),
                    'Đ' => sb.Append('D'),
                    _ => sb.Append(ch)
                };
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static async Task UpsertPageIndexAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        PdfOcrDiagnostics? ocrDiagnostics,
        CancellationToken ct)
    {
        if (pages.Count == 0)
            return;

        const string sql = @"
INSERT INTO document_page_index(
    tenant_id,
    revision_id,
    page_number,
    char_count,
    checksum,
    metadata)
VALUES(
    @tenant_id,
    @revision_id,
    @page_number,
    @char_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, page_number) DO UPDATE
SET char_count = EXCLUDED.char_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        var imageDiagnosticsByPage = (ocrDiagnostics?.ImagePageDiagnostics ?? [])
            .GroupBy(static item => item.PageNumber)
            .ToDictionary(static group => group.Key, static group => group.Last());

        foreach (var page in pages)
        {
            var pageQuality = page.Quality ?? PdfPageExtractionQuality.FromCounts(page.WordCount, page.CharCount);
            var unitsOnPage = units.Count(unit => unit.PageStart <= page.PageNumber && page.PageNumber <= unit.PageEnd);
            var suspiciousUnitCount = units.Count(unit =>
                unit.PageStart <= page.PageNumber
                && page.PageNumber <= unit.PageEnd
                && OcrNoiseFilter.LooksLikeProbableNoiseText(unit.Text));
            var chunksOnPage = retrievalChunks.Count(chunk => chunk.PageStart <= page.PageNumber && page.PageNumber <= chunk.PageEnd);
            var pageReview = ExtractionQualityDiagnostics.AssessPage(
                page.WordCount,
                page.CharCount,
                page.ImageCount,
                unitsOnPage,
                suspiciousUnitCount,
                chunksOnPage,
                pageQuality.Signals);
            imageDiagnosticsByPage.TryGetValue(page.PageNumber, out var imageDiagnostic);
            var metadata = SerializePostgresJsonForStorage(new
            {
                wordCount = page.WordCount,
                textLength = page.Text.Length,
                imageCount = page.ImageCount,
                extractionQuality = BuildPageExtractionQualityPayload(pageReview, unitsOnPage, suspiciousUnitCount, chunksOnPage),
                imageOcrStatus = imageDiagnostic?.Status,
                imageOcrReason = imageDiagnostic?.Reason,
                imageOcrWordCount = imageDiagnostic?.OcrWordCount,
                imageOcrCharCount = imageDiagnostic?.OcrCharCount,
                imageOcrExitCode = imageDiagnostic?.ExitCode,
                imageOcrTimedOut = imageDiagnostic?.TimedOut ?? false
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                revision_id = revisionId,
                page_number = page.PageNumber,
                char_count = page.CharCount,
                checksum = page.Checksum,
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static object BuildExtractionQualityPayload(PdfExtractionQualitySummary quality)
        => new
        {
            pageCount = quality.PageCount,
            textPageCount = quality.TextPageCount,
            emptyPageCount = quality.EmptyPageCount,
            sparsePageCount = quality.SparsePageCount,
            totalWordCount = quality.TotalWordCount,
            totalCharCount = quality.TotalCharCount,
            averageWordsPerPage = quality.AverageWordsPerPage,
            averageCharsPerPage = quality.AverageCharsPerPage,
            textPageRatio = quality.TextPageRatio,
            emptyPageRatio = quality.EmptyPageRatio,
            sparsePageRatio = quality.SparsePageRatio,
            textStatus = quality.TextStatus,
            ocrRecommended = quality.OcrRecommended,
            signals = quality.Signals
        };

    private static object BuildRetrievalChunkQualityPayload(IngestionRetrievalChunkQualitySummary summary)
        => new
        {
            totalChunkCount = summary.TotalChunkCount,
            searchableChunkCount = summary.SearchableChunkCount,
            rejectedChunkCount = summary.RejectedChunkCount,
            navigationChunkCount = summary.NavigationChunkCount,
            qualityRejectedChunkCount = summary.QualityRejectedChunkCount,
            sparseRejectedChunkCount = summary.SparseRejectedChunkCount,
            replacementCharRejectedChunkCount = summary.ReplacementCharRejectedChunkCount,
            emptyTextRejectedChunkCount = summary.EmptyTextRejectedChunkCount,
            ocrNoiseRejectedChunkCount = summary.OcrNoiseRejectedChunkCount,
            otherRejectedChunkCount = summary.OtherRejectedChunkCount,
            manualReviewRecommended = summary.ManualReviewRecommended,
            rejectionReasons = new
            {
                navigationOnly = summary.NavigationChunkCount,
                sparseText = summary.SparseRejectedChunkCount,
                replacementCharsRemaining = summary.ReplacementCharRejectedChunkCount,
                emptyText = summary.EmptyTextRejectedChunkCount,
                ocrNoise = summary.OcrNoiseRejectedChunkCount,
                other = summary.OtherRejectedChunkCount
            }
        };

    private static object BuildPageExtractionQualityPayload(
        ExtractionPageReview review,
        int unitCount,
        int suspiciousUnitCount,
        int chunkCount)
        => new
        {
            diagnosticVersion = "page_extraction_quality_v2",
            qualityStatus = review.Status,
            extractionConfidence = review.ExtractionConfidence,
            manualReviewRecommended = review.ManualReviewRecommended,
            textStatus = review.TextStatus,
            textEmpty = review.TextEmpty,
            textSparse = review.TextSparse,
            ocrCandidate = review.OcrCandidate,
            averageCharsPerWord = review.AverageCharsPerWord,
            unitCount,
            suspiciousUnitCount,
            chunkCount,
            signals = review.Signals
        };

    private static object[] BuildFailedPageDiagnosticsPayload(
        IReadOnlyList<ExtractedPdfPage>? pages,
        PdfOcrDiagnostics? ocrDiagnostics,
        IReadOnlyList<ExtractedDocumentUnit>? units = null,
        IReadOnlyList<ProjectedRetrievalChunk>? retrievalChunks = null)
    {
        if (pages is null || pages.Count == 0)
            return [];

        var imageDiagnosticsByPage = (ocrDiagnostics?.ImagePageDiagnostics ?? [])
            .GroupBy(static item => item.PageNumber)
            .ToDictionary(static group => group.Key, static group => group.Last());

        return pages
            .OrderBy(static page => page.PageNumber)
            .Select(page =>
            {
                var pageQuality = page.Quality ?? PdfPageExtractionQuality.FromCounts(page.WordCount, page.CharCount);
                var unitsOnPage = units?.Count(unit => unit.PageStart <= page.PageNumber && page.PageNumber <= unit.PageEnd) ?? 0;
                var suspiciousUnitCount = units?.Count(unit =>
                    unit.PageStart <= page.PageNumber
                    && page.PageNumber <= unit.PageEnd
                    && OcrNoiseFilter.LooksLikeProbableNoiseText(unit.Text)) ?? 0;
                var chunksOnPage = retrievalChunks?.Count(chunk => chunk.PageStart <= page.PageNumber && page.PageNumber <= chunk.PageEnd) ?? 0;
                var review = ExtractionQualityDiagnostics.AssessPage(
                    page.WordCount,
                    page.CharCount,
                    page.ImageCount,
                    unitsOnPage,
                    suspiciousUnitCount,
                    chunksOnPage,
                    pageQuality.Signals);
                imageDiagnosticsByPage.TryGetValue(page.PageNumber, out var imageDiagnostic);

                return new
                {
                    pageNumber = page.PageNumber,
                    charCount = page.CharCount,
                    wordCount = page.WordCount,
                    imageCount = page.ImageCount,
                    unitCount = unitsOnPage,
                    suspiciousUnitCount,
                    chunkCount = chunksOnPage,
                    qualityStatus = review.Status,
                    extractionConfidence = review.ExtractionConfidence,
                    manualReviewRecommended = review.ManualReviewRecommended,
                    textStatus = review.TextStatus,
                    textEmpty = review.TextEmpty,
                    textSparse = review.TextSparse,
                    ocrCandidate = review.OcrCandidate,
                    averageCharsPerWord = review.AverageCharsPerWord,
                    signals = review.Signals,
                    imageOcrStatus = imageDiagnostic?.Status,
                    imageOcrReason = imageDiagnostic?.Reason,
                    imageOcrWordCount = imageDiagnostic?.OcrWordCount,
                    imageOcrCharCount = imageDiagnostic?.OcrCharCount,
                    imageOcrExitCode = imageDiagnostic?.ExitCode,
                    imageOcrTimedOut = imageDiagnostic?.TimedOut ?? false
                };
            })
            .ToArray();
    }

    private static object BuildOcrDiagnosticsPayload(PdfOcrDiagnostics diagnostics)
        => new
        {
            mode = diagnostics.Mode,
            candidatePageCount = diagnostics.CandidatePageCount,
            attemptedPageCount = diagnostics.AttemptedPageCount,
            skippedPageCount = diagnostics.SkippedPageCount,
            maxPages = diagnostics.MaxPages,
            candidatePages = diagnostics.CandidatePages,
            attemptedPages = diagnostics.AttemptedPages,
            skippedPages = diagnostics.SkippedPages,
            pagesWithOcrText = diagnostics.PagesWithOcrText,
            pagesWithNovelText = diagnostics.PagesWithNovelText,
            exitCode = diagnostics.ExitCode,
            timedOut = diagnostics.TimedOut,
            timeoutSeconds = diagnostics.TimeoutSeconds,
            stderr = diagnostics.Stderr,
            failureReason = diagnostics.FailureReason,
            appliedReason = diagnostics.AppliedReason,
            coverageStatus = diagnostics.CoverageStatus,
            imagePageDiagnostics = diagnostics.ImagePageDiagnostics?.Select(static page => new
            {
                pageNumber = page.PageNumber,
                status = page.Status,
                reason = page.Reason,
                ocrWordCount = page.OcrWordCount,
                ocrCharCount = page.OcrCharCount,
                exitCode = page.ExitCode,
                timedOut = page.TimedOut
            }).ToArray()
        };

    private static async Task UpsertSectionsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        IReadOnlyList<ExtractedDocumentSection> sections,
        CancellationToken ct)
    {
        if (sections.Count == 0)
            return;

        const string sql = @"
INSERT INTO document_sections(
    section_id,
    tenant_id,
    revision_id,
    ordinal,
    title,
    section_level,
    page_start,
    page_end,
    start_line,
    end_line,
    metadata)
VALUES(
    @section_id,
    @tenant_id,
    @revision_id,
    @ordinal,
    @title,
    @section_level,
    @page_start,
    @page_end,
    @start_line,
    @end_line,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, ordinal) DO UPDATE
SET title = EXCLUDED.title,
    section_level = EXCLUDED.section_level,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    start_line = EXCLUDED.start_line,
    end_line = EXCLUDED.end_line,
    metadata = EXCLUDED.metadata;";

        foreach (var section in sections)
        {
            var title = NormalizePostgresTextForStorage(section.Title);
            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                section_id = BuildStableSectionId(revisionId, section.Ordinal),
                tenant_id = tenantId,
                revision_id = revisionId,
                ordinal = section.Ordinal,
                title,
                section_level = section.Level,
                page_start = section.PageStart,
                page_end = section.PageEnd,
                start_line = section.StartLine,
                end_line = section.EndLine,
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task UpsertRetrievalChunkLinksAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        int ingestionVersion,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        CancellationToken ct)
    {
        if (retrievalChunks.Count < 2)
            return;

        const string sql = @"
INSERT INTO retrieval_chunk_links(
    retrieval_chunk_id,
    linked_chunk_id,
    tenant_id,
    revision_id,
    link_type)
VALUES(
    @retrieval_chunk_id,
    @linked_chunk_id,
    @tenant_id,
    @revision_id,
    @link_type)
ON CONFLICT (retrieval_chunk_id, linked_chunk_id, link_type) DO NOTHING;";

        var ordered = retrievalChunks.OrderBy(chunk => chunk.ChunkIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var currentId = BuildStableRetrievalChunkId(docId, ingestionVersion, current.ChunkIndex);

            if (i > 0)
            {
                var previous = ordered[i - 1];
                var previousId = BuildStableRetrievalChunkId(docId, ingestionVersion, previous.ChunkIndex);
                await conn.ExecuteAsync(new CommandDefinition(sql, new
                {
                    retrieval_chunk_id = currentId,
                    linked_chunk_id = previousId,
                    tenant_id = tenantId,
                    revision_id = revisionId,
                    link_type = "prev"
                }, transaction: tx, cancellationToken: ct));
            }

            if (i + 1 < ordered.Count)
            {
                var next = ordered[i + 1];
                var nextId = BuildStableRetrievalChunkId(docId, ingestionVersion, next.ChunkIndex);
                await conn.ExecuteAsync(new CommandDefinition(sql, new
                {
                    retrieval_chunk_id = currentId,
                    linked_chunk_id = nextId,
                    tenant_id = tenantId,
                    revision_id = revisionId,
                    link_type = "next"
                }, transaction: tx, cancellationToken: ct));
            }

            var sameSectionNext = ordered
                .Skip(i + 1)
                .FirstOrDefault(candidate =>
                    candidate.SectionOrdinal.HasValue
                    && current.SectionOrdinal.HasValue
                    && candidate.SectionOrdinal == current.SectionOrdinal);

            if (sameSectionNext is not null)
            {
                var sameSectionId = BuildStableRetrievalChunkId(docId, ingestionVersion, sameSectionNext.ChunkIndex);
                await conn.ExecuteAsync(new CommandDefinition(sql, new
                {
                    retrieval_chunk_id = currentId,
                    linked_chunk_id = sameSectionId,
                    tenant_id = tenantId,
                    revision_id = revisionId,
                    link_type = "same_section"
                }, transaction: tx, cancellationToken: ct));
            }
        }
    }

    internal static Guid BuildStableSectionId(Guid revisionId, int ordinal)
        => IdUtil.DeterministicGuid($"{revisionId:N}|section|{ordinal}");

    internal static Guid BuildStableUnitId(Guid revisionId, int ordinal)
        => IdUtil.DeterministicGuid($"{revisionId:N}|unit|{ordinal}");

    internal static Guid BuildStableRetrievalChunkId(Guid docId, int ingestionVersion, int chunkIndex)
        => IdUtil.DeterministicGuid($"{docId:N}|retrieval-chunk|v{ingestionVersion}|{chunkIndex}");

    internal static Guid BuildStableExactMatchEntryId(Guid revisionId, int entryIndex)
        => IdUtil.DeterministicGuid($"{revisionId:N}|exact-match|{entryIndex}");

    internal static Guid BuildStableContextualTextEntryId(Guid revisionId, int entryIndex)
        => IdUtil.DeterministicGuid($"{revisionId:N}|contextual-text|{entryIndex}");

    private static async Task UpsertUnitsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        CancellationToken ct)
    {
        if (units.Count == 0)
            return;

        var sectionIdsByOrdinal = sections.ToDictionary(
            s => s.Ordinal,
            s => BuildStableSectionId(revisionId, s.Ordinal));

        const string sql = @"
INSERT INTO document_units(
    unit_id,
    tenant_id,
    revision_id,
    section_id,
    ordinal,
    page_start,
    page_end,
    text_content,
    char_count,
    token_count,
    checksum,
    metadata)
VALUES(
    @unit_id,
    @tenant_id,
    @revision_id,
    @section_id,
    @ordinal,
    @page_start,
    @page_end,
    @text_content,
    @char_count,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, ordinal) DO UPDATE
SET section_id = EXCLUDED.section_id,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    text_content = EXCLUDED.text_content,
    char_count = EXCLUDED.char_count,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        foreach (var unit in units)
        {
            var text = NormalizePostgresTextForStorage(unit.Text);
            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true,
                offsetStart = unit.OffsetStart,
                offsetEnd = unit.OffsetEnd,
                extractionTextStatus = NormalizeOptionalPostgresTextForStorage(unit.ExtractionTextStatus),
                extractionTextSparse = unit.ExtractionTextSparse,
                extractionOcrCandidate = unit.ExtractionOcrCandidate,
                extractionQualitySignals = NormalizePostgresTextArrayForStorage(unit.ExtractionQualitySignals ?? Array.Empty<string>())
            });

            sectionIdsByOrdinal.TryGetValue(unit.SectionOrdinal ?? -1, out var sectionId);
            Guid? persistedSectionId = sectionId == Guid.Empty ? null : sectionId;

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                unit_id = BuildStableUnitId(revisionId, unit.Ordinal),
                tenant_id = tenantId,
                revision_id = revisionId,
                section_id = persistedSectionId,
                ordinal = unit.Ordinal,
                page_start = unit.PageStart,
                page_end = unit.PageEnd,
                text_content = text,
                char_count = text.Length,
                token_count = CountTokens(text),
                checksum = ComputeStoredTextChecksum(text),
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task UpsertRetrievalChunksAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        int ingestionVersion,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        CancellationToken ct)
    {
        if (retrievalChunks.Count == 0)
            return;

        var sectionIdsByOrdinal = sections.ToDictionary(
            s => s.Ordinal,
            s => BuildStableSectionId(revisionId, s.Ordinal));
        var unitIdsByOrdinal = units.ToDictionary(
            u => u.Ordinal,
            u => BuildStableUnitId(revisionId, u.Ordinal));
        var sectionTitleByOrdinal = sections.ToDictionary(s => s.Ordinal, s => s.Title);
        var headingPathBySectionOrdinal = ContextualTextProjector.BuildHeadingPathMap(sections);
        var chunkLinkMap = IngestionWorker.BuildChunkLinkMap(docId, ingestionVersion, retrievalChunks);

        // FK fix: delete existing chunks for this revision before re-inserting.
        // This prevents the ON CONFLICT from preserving a stale retrieval_chunk_id (PK)
        // when the same (revision_id, chunk_index) is re-used with a different ingestionVersion.
        // Cascade: retrieval_chunk_links are deleted; contextual_text_entries.retrieval_chunk_id is SET NULL.
        const string purgeSql = "DELETE FROM retrieval_chunks WHERE revision_id = @revision_id;";
        await conn.ExecuteAsync(new CommandDefinition(purgeSql, new { revision_id = revisionId }, transaction: tx, cancellationToken: ct));

        const string sql = @"
INSERT INTO retrieval_chunks(
    retrieval_chunk_id,
    tenant_id,
    revision_id,
    section_id,
    unit_id,
    chunk_index,
    page_start,
    page_end,
    text_content,
    token_count,
    checksum,
    metadata)
VALUES(
    @retrieval_chunk_id,
    @tenant_id,
    @revision_id,
    @section_id,
    @unit_id,
    @chunk_index,
    @page_start,
    @page_end,
    @text_content,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, chunk_index) DO UPDATE
SET retrieval_chunk_id = EXCLUDED.retrieval_chunk_id,
    section_id = EXCLUDED.section_id,
    unit_id = EXCLUDED.unit_id,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    text_content = EXCLUDED.text_content,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        foreach (var chunk in retrievalChunks)
        {
            sectionIdsByOrdinal.TryGetValue(chunk.SectionOrdinal ?? -1, out var sectionIdValue);
            unitIdsByOrdinal.TryGetValue(chunk.UnitOrdinal ?? -1, out var unitIdValue);
            Guid? sectionId = sectionIdValue == Guid.Empty ? null : sectionIdValue;
            Guid? unitId = unitIdValue == Guid.Empty ? null : unitIdValue;
            sectionTitleByOrdinal.TryGetValue(chunk.SectionOrdinal ?? -1, out var sectionTitle);
            var headingPath = ContextualTextProjector.ResolveHeadingPath(chunk.SectionOrdinal, headingPathBySectionOrdinal);
            chunkLinkMap.TryGetValue(chunk.ChunkIndex, out var chunkLinks);
            var text = NormalizePostgresTextForStorage(chunk.Text);
            var storedSectionTitle = NormalizeOptionalPostgresTextForStorage(sectionTitle);
            var storedHeadingPath = NormalizeOptionalPostgresTextForStorage(headingPath);

            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true,
                chunkType = NormalizePostgresTextForStorage(chunk.ChunkType),
                contentRole = NormalizePostgresTextForStorage(chunk.ContentRole),
                navigationReason = NormalizeOptionalPostgresTextForStorage(chunk.NavigationReason),
                originalChunkType = NormalizeOptionalPostgresTextForStorage(chunk.OriginalChunkType),
                navigationScore = Math.Round(chunk.NavigationScore, 4),
                contentDensityScore = Math.Round(chunk.ContentDensityScore, 4),
                extractionTextStatus = NormalizeOptionalPostgresTextForStorage(chunk.ExtractionTextStatus),
                extractionTextSparse = chunk.ExtractionTextSparse,
                extractionOcrCandidate = chunk.ExtractionOcrCandidate,
                extractionQualitySignals = NormalizePostgresTextArrayForStorage(chunk.ExtractionQualitySignals ?? Array.Empty<string>()),
                sectionTitle = storedSectionTitle,
                headingPath = storedHeadingPath,
                offsetStart = chunk.OffsetStart,
                offsetEnd = chunk.OffsetEnd,
                prevChunkId = chunkLinks?.PreviousChunkId?.ToString(),
                nextChunkId = chunkLinks?.NextChunkId?.ToString(),
                sameSectionChunkId = chunkLinks?.SameSectionChunkId?.ToString()
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                retrieval_chunk_id = BuildStableRetrievalChunkId(docId, ingestionVersion, chunk.ChunkIndex),
                tenant_id = tenantId,
                revision_id = revisionId,
                section_id = sectionId,
                unit_id = unitId,
                chunk_index = chunk.ChunkIndex,
                page_start = chunk.PageStart,
                page_end = chunk.PageEnd,
                text_content = text,
                token_count = CountTokens(text),
                checksum = ComputeStoredTextChecksum(text),
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task UpsertExactMatchEntriesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid revisionId,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        CancellationToken ct)
    {
        if (exactMatchEntries.Count == 0)
            return;

        var sectionIdsByOrdinal = sections.ToDictionary(
            section => section.Ordinal,
            section => BuildStableSectionId(revisionId, section.Ordinal));
        var unitIdsByOrdinal = units.ToDictionary(
            unit => unit.Ordinal,
            unit => BuildStableUnitId(revisionId, unit.Ordinal));

        const string sql = @"
INSERT INTO exact_match_entries(
    exact_match_entry_id,
    tenant_id,
    revision_id,
    section_id,
    unit_id,
    entry_index,
    page_start,
    page_end,
    text_content,
    normalized_text,
    char_count,
    token_count,
    checksum,
    metadata)
VALUES(
    @exact_match_entry_id,
    @tenant_id,
    @revision_id,
    @section_id,
    @unit_id,
    @entry_index,
    @page_start,
    @page_end,
    @text_content,
    @normalized_text,
    @char_count,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, entry_index) DO UPDATE
SET section_id = EXCLUDED.section_id,
    unit_id = EXCLUDED.unit_id,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    text_content = EXCLUDED.text_content,
    normalized_text = EXCLUDED.normalized_text,
    char_count = EXCLUDED.char_count,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        foreach (var entry in exactMatchEntries)
        {
            sectionIdsByOrdinal.TryGetValue(entry.SectionOrdinal ?? -1, out var sectionIdValue);
            unitIdsByOrdinal.TryGetValue(entry.UnitOrdinal, out var unitIdValue);
            Guid? sectionId = sectionIdValue == Guid.Empty ? null : sectionIdValue;
            Guid? unitId = unitIdValue == Guid.Empty ? null : unitIdValue;
            var text = NormalizePostgresTextForStorage(entry.Text);
            var normalizedText = NormalizePostgresTextForStorage(entry.NormalizedText);

            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true,
                kind = NormalizePostgresTextForStorage(entry.Kind),
                offsetStart = entry.OffsetStart,
                offsetEnd = entry.OffsetEnd
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                exact_match_entry_id = BuildStableExactMatchEntryId(revisionId, entry.EntryIndex),
                tenant_id = tenantId,
                revision_id = revisionId,
                section_id = sectionId,
                unit_id = unitId,
                entry_index = entry.EntryIndex,
                page_start = entry.PageStart,
                page_end = entry.PageEnd,
                text_content = text,
                normalized_text = normalizedText,
                char_count = text.Length,
                token_count = CountTokens(text),
                checksum = ComputeStoredTextChecksum(normalizedText),
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task UpsertContextualTextEntriesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        int ingestionVersion,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedContextualTextEntry> contextualTextEntries,
        CancellationToken ct)
    {
        if (contextualTextEntries.Count == 0)
            return;

        var sectionIdsByOrdinal = sections.ToDictionary(
            section => section.Ordinal,
            section => BuildStableSectionId(revisionId, section.Ordinal));
        var unitIdsByOrdinal = units.ToDictionary(
            unit => unit.Ordinal,
            unit => BuildStableUnitId(revisionId, unit.Ordinal));

        const string sql = @"
INSERT INTO contextual_text_entries(
    contextual_text_entry_id,
    tenant_id,
    revision_id,
    section_id,
    unit_id,
    retrieval_chunk_id,
    entry_index,
    page_start,
    page_end,
    text_content,
    char_count,
    token_count,
    checksum,
    metadata)
VALUES(
    @contextual_text_entry_id,
    @tenant_id,
    @revision_id,
    @section_id,
    @unit_id,
    @retrieval_chunk_id,
    @entry_index,
    @page_start,
    @page_end,
    @text_content,
    @char_count,
    @token_count,
    @checksum,
    CAST(@metadata AS jsonb))
ON CONFLICT (revision_id, entry_index) DO UPDATE
SET section_id = EXCLUDED.section_id,
    unit_id = EXCLUDED.unit_id,
    retrieval_chunk_id = EXCLUDED.retrieval_chunk_id,
    page_start = EXCLUDED.page_start,
    page_end = EXCLUDED.page_end,
    text_content = EXCLUDED.text_content,
    char_count = EXCLUDED.char_count,
    token_count = EXCLUDED.token_count,
    checksum = EXCLUDED.checksum,
    metadata = EXCLUDED.metadata;";

        foreach (var entry in contextualTextEntries)
        {
            sectionIdsByOrdinal.TryGetValue(entry.SectionOrdinal ?? -1, out var sectionIdValue);
            unitIdsByOrdinal.TryGetValue(entry.UnitOrdinal ?? -1, out var unitIdValue);
            Guid? sectionId = sectionIdValue == Guid.Empty ? null : sectionIdValue;
            Guid? unitId = unitIdValue == Guid.Empty ? null : unitIdValue;
            Guid retrievalChunkId = BuildStableRetrievalChunkId(docId, ingestionVersion, entry.ChunkIndex);
            var text = NormalizePostgresTextForStorage(entry.Text);

            var metadata = SerializePostgresJsonForStorage(new
            {
                inferred = true
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                contextual_text_entry_id = BuildStableContextualTextEntryId(revisionId, entry.EntryIndex),
                tenant_id = tenantId,
                revision_id = revisionId,
                section_id = sectionId,
                unit_id = unitId,
                retrieval_chunk_id = retrievalChunkId,
                entry_index = entry.EntryIndex,
                page_start = entry.PageStart,
                page_end = entry.PageEnd,
                text_content = text,
                char_count = text.Length,
                token_count = CountTokens(text),
                checksum = ComputeStoredTextChecksum(text),
                metadata
            }, transaction: tx, cancellationToken: ct));
        }
    }

}

internal sealed record DocumentProfileSnapshot(
    Guid RevisionId,
    Guid DocId,
    string ProfileVersion,
    string Language,
    string SummaryText,
    string[] Keywords,
    string[] Entities,
    string[] Topics,
    string[] HypotheticalQuestions,
    string[] Limits,
    string SearchText,
    int TokenCount,
    IReadOnlyList<DocumentProfileContentCard>? ContentCards = null);

internal sealed class DocumentProfileSnapshotRow
{
    public Guid RevisionId { get; set; }
    public Guid DocId { get; set; }
    public string? ProfileVersion { get; set; }
    public string? Language { get; set; }
    public string? SummaryText { get; set; }
    public string[]? Keywords { get; set; }
    public string[]? Entities { get; set; }
    public string[]? Topics { get; set; }
    public string[]? HypotheticalQuestions { get; set; }
    public string[]? Limits { get; set; }
    public string? SearchText { get; set; }
    public int TokenCount { get; set; }
    public string? MetadataJson { get; set; }
}

internal sealed class DocumentProfileContentCardRow
{
    public string? Title { get; set; }
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public string? Kind { get; set; }
    public string[]? Signals { get; set; }
    public string? MetadataJson { get; set; }
    public Guid? ContentCardId { get; set; }
}

internal sealed class FailedExtractionDocumentState
{
    public Guid DocId { get; set; }
    public int IngestionVersion { get; set; }
    public int IndexedVersion { get; set; }
}
