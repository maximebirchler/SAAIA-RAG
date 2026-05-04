using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

internal static class DocumentFoundationRepo
{
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
        CancellationToken ct)
    {
        var revisionId = BuildStableRevisionId(tenantId, docId, indexedVersionAfter);
        var extractionQuality = PdfExtractionQualitySummary.FromPages(pages);

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
                extractionQuality = BuildExtractionQualityPayload(extractionQuality)
            }),
            ct);

        await UpsertPageIndexAsync(conn, tx, tenantId, revisionId, pages, ct);
        await UpsertSectionsAsync(conn, tx, tenantId, revisionId, sections, ct);
        await UpsertUnitsAsync(conn, tx, tenantId, revisionId, sections, units, ct);
        await UpsertRetrievalChunksAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, sections, units, retrievalChunks, ct);
        await UpsertRetrievalChunkLinksAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, retrievalChunks, ct);
        await UpsertExactMatchEntriesAsync(conn, tx, tenantId, revisionId, sections, units, exactMatchEntries, ct);
        await UpsertContextualTextEntriesAsync(conn, tx, tenantId, docId, revisionId, ingestionVersion, sections, units, contextualTextEntries, ct);
        var documentProfile = DocumentProfileProjector.Project(docPath, pages, sections, units, exactMatchEntries);
        await UpsertDocumentProfileAsync(conn, tx, tenantId, docId, revisionId, documentProfile, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "page_index", pages, p => $"page:{p.PageNumber}:{p.CharCount}:{Convert.ToHexString(p.Checksum)}", p => p.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "sections", sections, s => $"section:{s.Ordinal}:{s.Level}:{s.PageStart}:{s.PageEnd}:{s.Title}", _ => 0, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "units", units, u => $"unit:{u.Ordinal}:{u.PageStart}:{u.PageEnd}:{u.TokenCount}:{u.Text}", u => u.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "retrieval_chunks", retrievalChunks, c => $"chunk:{c.ChunkIndex}:{c.PageStart}:{c.PageEnd}:{c.TokenCount}:{c.ChunkType}:{c.Text}", c => c.Text.Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "exact_match_entries", exactMatchEntries, e => $"exact:{e.EntryIndex}:{e.PageStart}:{e.PageEnd}:{e.NormalizedText}", e => e.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "contextual_text_entries", contextualTextEntries, e => $"contextual:{e.EntryIndex}:{e.PageStart}:{e.PageEnd}:{e.Text}", e => e.CharCount, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_profile", new[] { documentProfile }, p => $"profile:{p.ProfileVersion}:{p.Language}:{p.SearchText}", p => p.SearchText.Length, ct);
        await UpsertArtifactSummaryAsync(conn, tx, tenantId, revisionId, "document_profile_content_cards", documentProfile.ContentCards, c => $"card:{c.Title}:{c.PageStart}:{c.PageEnd}:{c.Kind}:{string.Join('|', c.Signals)}", c => BuildContentCardSearchText(c).Length, ct);
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

    internal static Guid BuildStableRevisionId(Guid tenantId, Guid docId, int indexedVersion)
        => IdUtil.DeterministicGuid($"{tenantId:N}|{docId:N}|rev|{indexedVersion}");

    internal static Guid BuildStableProcessingRunId(Guid jobId)
        => IdUtil.DeterministicGuid($"{jobId:N}|processing-run");

    internal static Guid BuildStableArtifactId(Guid revisionId, string artifactType)
        => IdUtil.DeterministicGuid($"{revisionId:N}|artifact|{artifactType}");

    internal static Guid BuildStableDocumentProfileId(Guid revisionId, string profileVersion)
        => IdUtil.DeterministicGuid($"{revisionId:N}|document-profile|{profileVersion}");

    internal static Guid BuildStableDocumentProfileContentCardId(Guid documentProfileId, int cardIndex)
        => IdUtil.DeterministicGuid($"{documentProfileId:N}|content-card|{cardIndex}");

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
            payload = JsonSerializer.Serialize(payload)
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

        var payload = JsonSerializer.Serialize(new
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
    page_start AS "PageStart",
    page_end AS "PageEnd",
    kind AS "Kind",
    signals AS "Signals"
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
                    row.Signals ?? []))
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

        var metadata = JsonSerializer.Serialize(new
        {
            generatedBy = "document_profile_projector",
            profile.ProfileVersion,
            keywordCount = profile.Keywords.Count,
            entityCount = profile.Entities.Count,
            topicCount = profile.Topics.Count,
            hypotheticalQuestionCount = profile.HypotheticalQuestions.Count,
            contentCardCount = profile.ContentCards.Count,
            contentCards = profile.ContentCards.Select(card => new
            {
                title = card.Title,
                pageStart = card.PageStart,
                pageEnd = card.PageEnd,
                kind = card.Kind,
                signals = card.Signals
            })
        });

        var documentProfileId = BuildStableDocumentProfileId(revisionId, profile.ProfileVersion);

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            document_profile_id = documentProfileId,
            tenant_id = tenantId,
            revision_id = revisionId,
            doc_id = docId,
            profile_version = profile.ProfileVersion,
            language = string.Equals(profile.Language, "und", StringComparison.Ordinal) ? null : profile.Language,
            summary_text = profile.SummaryText,
            keywords = profile.Keywords.ToArray(),
            entities = profile.Entities.ToArray(),
            topics = profile.Topics.ToArray(),
            hypothetical_questions = profile.HypotheticalQuestions.ToArray(),
            limits = profile.Limits.ToArray(),
            search_text = profile.SearchText,
            token_count = profile.TokenCount,
            checksum = profile.Checksum,
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
    }

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
  AND doc_id = @doc_id
  AND profile_version = @profile_version;";
        await conn.ExecuteAsync(new CommandDefinition(
            purgeSql,
            new
            {
                tenant_id = tenantId,
                doc_id = docId,
                profile_version = profile.ProfileVersion
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
ON CONFLICT (document_profile_id, card_index) DO UPDATE
SET title = EXCLUDED.title,
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

        for (var i = 0; i < profile.ContentCards.Count; i++)
        {
            var card = profile.ContentCards[i];
            var searchText = BuildContentCardSearchText(card);
            var metadata = JsonSerializer.Serialize(new
            {
                generatedBy = "document_profile_projector",
                profile.ProfileVersion,
                signalCount = card.Signals.Count
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                content_card_id = BuildStableDocumentProfileContentCardId(documentProfileId, i),
                tenant_id = tenantId,
                document_profile_id = documentProfileId,
                revision_id = revisionId,
                doc_id = docId,
                profile_version = profile.ProfileVersion,
                card_index = i,
                title = card.Title,
                normalized_title = NormalizeContentCardLookupText(card.Title),
                page_start = card.PageStart,
                page_end = card.PageEnd,
                kind = string.IsNullOrWhiteSpace(card.Kind) ? "content_item" : card.Kind,
                signals = card.Signals.ToArray(),
                search_text = searchText,
                token_count = CountTokens(searchText),
                checksum = SHA256.HashData(Encoding.UTF8.GetBytes(searchText)),
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
                NormalizeContentCardLookupText(string.Join(' ', card.Signals))
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));

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
                sb.Append(ch);
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

        foreach (var page in pages)
        {
            var pageQuality = page.Quality ?? PdfPageExtractionQuality.FromCounts(page.WordCount, page.CharCount);
            var metadata = JsonSerializer.Serialize(new
            {
                wordCount = page.WordCount,
                textLength = page.Text.Length,
                extractionQuality = BuildPageExtractionQualityPayload(pageQuality)
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

    private static object BuildPageExtractionQualityPayload(PdfPageExtractionQuality quality)
        => new
        {
            textStatus = quality.TextStatus,
            textEmpty = quality.TextEmpty,
            textSparse = quality.TextSparse,
            ocrCandidate = quality.OcrCandidate,
            averageCharsPerWord = quality.AverageCharsPerWord,
            signals = quality.Signals
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
            var metadata = JsonSerializer.Serialize(new
            {
                inferred = true
            });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                section_id = BuildStableSectionId(revisionId, section.Ordinal),
                tenant_id = tenantId,
                revision_id = revisionId,
                ordinal = section.Ordinal,
                title = section.Title,
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
            var metadata = JsonSerializer.Serialize(new
            {
                inferred = true,
                offsetStart = unit.OffsetStart,
                offsetEnd = unit.OffsetEnd
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
                text_content = unit.Text,
                char_count = unit.CharCount,
                token_count = unit.TokenCount,
                checksum = unit.Checksum,
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

            var metadata = JsonSerializer.Serialize(new
            {
                inferred = true,
                chunkType = chunk.ChunkType,
                sectionTitle,
                headingPath,
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
                text_content = chunk.Text,
                token_count = chunk.TokenCount,
                checksum = chunk.Checksum,
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

            var metadata = JsonSerializer.Serialize(new
            {
                inferred = true,
                kind = entry.Kind,
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
                text_content = entry.Text,
                normalized_text = entry.NormalizedText,
                char_count = entry.CharCount,
                token_count = entry.TokenCount,
                checksum = entry.Checksum,
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

            var metadata = JsonSerializer.Serialize(new
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
                text_content = entry.Text,
                char_count = entry.CharCount,
                token_count = entry.TokenCount,
                checksum = entry.Checksum,
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
}
