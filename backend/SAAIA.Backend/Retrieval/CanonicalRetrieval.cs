using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalRows;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static partial class CanonicalRetrieval
{
    /// <summary>
    /// Retrieval path used by the LLM-first source-backed agent.
    ///
    /// The caller owns semantic planning. This method preserves its query and
    /// scope, executes the same mechanical channels for every request, fuses
    /// their ranks, optionally reranks them, and applies only explicit
    /// identity/score/quota constraints. It deliberately excludes the legacy
    /// intent recognizers, query rewrites, semantic backfills, answer guidance,
    /// selection hints and implicit content-card expansion.
    /// </summary>
    internal static async Task<RagSearchResponse> SearchSourceBackedCanonicalCoreAsync(
        RetrievalExecutionContext context,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = context.TenantId;
        var ct = context.CancellationToken;
        var teiGovernor = context.TeiGovernor;
        var query = req.Query.Trim();
        var queryNormalized = NormalizeQuery(query);
        if (string.IsNullOrWhiteSpace(queryNormalized))
            queryNormalized = query;

        var requestedCategory = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();
        var categoryPath = await ResolveRagCategoryPathAsync(
            ds,
            tenantId,
            req.CategoryPath,
            req.CategoryRef,
            ct).ConfigureAwait(false);
        var category = string.IsNullOrWhiteSpace(categoryPath)
            ? requestedCategory
            : null;
        var responseCategory = requestedCategory
                               ?? NormalizeRagCategory(
                                   ExtractTopLevelCategoryPath(categoryPath));

        var requestedTopK = Math.Clamp(
            req.TopK ?? rag.DefaultTopK,
            1,
            rag.MaxTopK);
        var candidateCount = Math.Clamp(
            req.Candidates ?? Math.Max(requestedTopK * 4, 24),
            requestedTopK,
            Math.Max(requestedTopK, rag.MaxTopK * 8));
        var minScore = Math.Clamp(req.MinScore ?? 0.0, 0.0, 1.0);
        var maxPerDoc = Math.Clamp(
            req.MaxPerDoc ?? requestedTopK,
            1,
            requestedTopK);
        var maxPerPage = Math.Clamp(
            req.MaxPerPage ?? requestedTopK,
            1,
            requestedTopK);
        if (req.Diversity?.MaxChunksPerDoc is > 0)
        {
            maxPerDoc = Math.Clamp(
                req.Diversity.MaxChunksPerDoc.Value,
                1,
                requestedTopK);
        }
        if (req.Diversity?.PreferDistinctPages == true)
            maxPerPage = 1;

        int? requestedPageStart = req.PageStart is > 0
            ? req.PageStart.Value
            : null;
        int? requestedPageEnd = requestedPageStart.HasValue
            ? Math.Max(
                requestedPageStart.Value,
                req.PageEnd is > 0
                    ? req.PageEnd.Value
                    : requestedPageStart.Value)
            : null;
        var hasCategoryFilter =
            !string.IsNullOrWhiteSpace(category)
            || !string.IsNullOrWhiteSpace(categoryPath);
        var hasDocScope =
            !string.IsNullOrWhiteSpace(req.DocId)
            || !string.IsNullOrWhiteSpace(req.DocPath);
        var sparseCommandTimeoutSeconds =
            RagOptions.ResolveSearchSparseCommandTimeoutSeconds(
                rag.SearchSparseCommandTimeoutSeconds);

        var degradedRetrievers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var degradedRetrieverErrors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var degradedGate = new object();
        void MarkRetrieverDegraded(string retriever, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(retriever))
                return;

            var normalized = retriever.Trim().ToLowerInvariant();
            lock (degradedGate)
            {
                degradedRetrievers.Add(normalized);
                if (!string.IsNullOrWhiteSpace(reason))
                    degradedRetrieverErrors[normalized] = reason.Trim();
            }
        }

        async Task<(List<RagMatch> Matches, long DurationMs)> MeasureAsync(string channel,
            Func<Task<List<RagMatch>>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            using var operation = BackendDiagnostics.Start("retrieval.channel", context.Logger);
            operation.Set("channel", channel);
            try
            {
                var matches = await action().ConfigureAwait(false);
                operation.Set("returned", matches.Count);
                operation.Complete();
                return (matches, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception error) { operation.Fail(error); throw; }
        }

        long teiMs = 0;
        long sparseMs = 0;
        long qdrantMs = 0;
        var qdrantStatus = 0;
        var totalStopwatch = Stopwatch.StartNew();
        using var searchScope = RetrievalTelemetry.StartSearchScope(
            "source_backed_canonical",
            hasCategoryFilter,
            hasDocScope,
            requestedTopK,
            candidateCount,
            query);
        var searchActivity = searchScope.Activity;

        var exactTask = MeasureAsync("exact", () => SearchSourceBackedCanonicalExactMatchesAsync(
            ds,
            tenantId,
            query,
            category,
            req.DocId,
            req.DocPath,
            candidateCount,
            ct,
            categoryPath));
        var sparseTask = MeasureAsync("sparse", () => SearchSourceBackedCanonicalSparseMatchesAsync(
            ds,
            tenantId,
            query,
            category,
            req.DocId,
            req.DocPath,
            candidateCount,
            ct,
            sparseMsRef: value => sparseMs = value,
            categoryPath: categoryPath,
            degradedRetrieverRef: MarkRetrieverDegraded,
            commandTimeoutSeconds: sparseCommandTimeoutSeconds));
        var denseTask = MeasureAsync("dense", () => SearchDenseMatchesAsync(
            ds,
            httpFactory,
            rag,
            tenantId,
            queryNormalized,
            category,
            req.DocId,
            req.DocPath,
            candidateCount,
            ct,
            teiMsRef: value => teiMs = value,
            qdrantMsRef: value => qdrantMs = value,
            qdrantStatusRef: value => qdrantStatus = value,
            categoryPath: categoryPath,
            degradedRetrieverRef: MarkRetrieverDegraded,
            teiGovernor: teiGovernor,
            attachContentCards: false));

        await Task.WhenAll(exactTask, sparseTask, denseTask).ConfigureAwait(false);
        var exact = SanitizeCanonicalMatches((await exactTask).Matches);
        var sparse = SanitizeCanonicalMatches((await sparseTask).Matches);
        var dense = SanitizeCanonicalMatches((await denseTask).Matches);
        var diagnostics = new RagRetrievalDiagnosticsBuilder(query, query, query,
            "source_backed_canonical", responseCategory, categoryPath, requestedTopK, candidateCount,
            maxPerDoc, maxPerPage,
            includeContent: req.IncludeDiagnostics == true && req.ResearchMode != "advanced_analysis");
        diagnostics.CapturePhase("canonical_exact_raw", "exact_match", (await exactTask).DurationMs, exact);
        diagnostics.CapturePhase("canonical_sparse_raw", "sparse_bm25", (await sparseTask).DurationMs, sparse);
        diagnostics.CapturePhase("canonical_dense_raw", "dense_qdrant", (await denseTask).DurationMs, dense);
        exact = FilterCanonicalPageWindow(
            exact,
            requestedPageStart,
            requestedPageEnd, diagnostics);
        sparse = FilterCanonicalPageWindow(
            sparse,
            requestedPageStart,
            requestedPageEnd, diagnostics);
        dense = FilterCanonicalPageWindow(
            dense,
            requestedPageStart,
            requestedPageEnd, diagnostics);
        diagnostics.CapturePhase(
            "canonical_exact",
            "exact_match",
            (await exactTask).DurationMs,
            exact);
        diagnostics.CapturePhase(
            "canonical_sparse",
            "sparse_bm25",
            (await sparseTask).DurationMs,
            sparse);
        diagnostics.CapturePhase(
            "canonical_dense",
            "dense_qdrant",
            (await denseTask).DurationMs,
            dense);

        var fusionStopwatch = Stopwatch.StartNew();
        var fused = FuseWithRrf(exact, sparse, dense);
        fusionStopwatch.Stop();
        diagnostics.CapturePhase(
            "canonical_fusion",
            "rrf",
            fusionStopwatch.ElapsedMilliseconds,
            fused);

        long rerankMs = 0;
        var rerankStopwatch = Stopwatch.StartNew();
        var rerankAttempt = await TryRerankWithTeiAsync(
            httpFactory,
            rag,
            query,
            fused.Take(candidateCount).ToList(),
            ct,
            rerankMsRef: value => rerankMs = value,
            teiGovernor: teiGovernor,
            degradedRetrieverRef: MarkRetrieverDegraded).ConfigureAwait(false);
        rerankStopwatch.Stop();
        var reranked = rerankAttempt.Matches;
        diagnostics.CapturePhase(
            rerankAttempt.Applied
                ? "canonical_rerank"
                : "canonical_rerank_skipped",
            "tei_rerank",
            rerankStopwatch.ElapsedMilliseconds,
            reranked);

        var selectionStopwatch = Stopwatch.StartNew();
        var selected = SelectCanonicalMatches(
            reranked,
            requestedTopK,
            minScore,
            maxPerDoc,
            maxPerPage, diagnostics);
        if (req.IncludeResearchSurfaces == true)
        {
            selected = await AttachDocumentProfileContentCardsAsync(
                    ds,
                    tenantId,
                    selected,
                    query,
                    ct,
                    perMatchLimit: 4)
                .ConfigureAwait(false);
        }
        selectionStopwatch.Stop();
        totalStopwatch.Stop();
        diagnostics.CapturePhase(
            "canonical_selected",
            "mechanical_selection",
            selectionStopwatch.ElapsedMilliseconds,
            selected);

        var response = new RagSearchResponse(
            RequestId: context.RequestId,
            Query: req.Query,
            QueryNormalized: queryNormalized,
            Category: responseCategory,
            TopK: requestedTopK,
            MinScore: minScore,
            Candidates: candidateCount,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            QdrantStatus: qdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: totalStopwatch.ElapsedMilliseconds,
                TeiMs: teiMs,
                RerankMs: rerankMs,
                SparseMs: sparseMs,
                QdrantMs: qdrantMs,
                ExactMs: (await exactTask).DurationMs,
                SparsePhaseMs: (await sparseTask).DurationMs,
                DenseMs: (await denseTask).DurationMs,
                FusionMs: fusionStopwatch.ElapsedMilliseconds,
                RerankPhaseMs: rerankStopwatch.ElapsedMilliseconds,
                SelectionMs: selectionStopwatch.ElapsedMilliseconds),
            Matches: selected,
            DegradedRetrievers: degradedRetrievers.Count == 0
                ? null
                : degradedRetrievers
                    .OrderBy(static retriever => retriever, StringComparer.Ordinal)
                    .ToArray(),
            DegradedRetrieverErrors: degradedRetrieverErrors.Count == 0
                ? null
                : degradedRetrieverErrors
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal),
            Diagnostics: req.IncludeDiagnostics == true ? diagnostics.Build(selected) : null);

        using (var candidateLog = BackendDiagnostics.Start("retrieval.candidate_flow", context.Logger))
        {
            var safeSnapshot = diagnostics.Build(selected);
            candidateLog.Set("capture_mode", "metadata");
            candidateLog.Set("phase_counts", safeSnapshot.Phases.Select(phase => new { phase.Name, phase.Returned, phase.OmittedCandidates }).ToArray());
            candidateLog.Set("rejection_counts", safeSnapshot.RejectionCounts);
            candidateLog.Set("dropped_rejections", safeSnapshot.DroppedRejections);
            candidateLog.Complete();
        }

        RetrievalTelemetry.CompleteSearch(
            searchActivity,
            response,
            "source_backed_canonical",
            hasCategoryFilter,
            hasDocScope);
        RetrievalTelemetry.RecordSearch(
            response,
            "source_backed_canonical",
            hasCategoryFilter,
            hasDocScope,
            (await exactTask).DurationMs,
            (await sparseTask).DurationMs,
            (await denseTask).DurationMs,
            linkedPhaseMs: 0);
        return response;
    }


    private static List<RagMatch> SanitizeCanonicalMatches(
        IReadOnlyList<RagMatch> matches)
        => matches
            .Select(static match => match with
            {
                MatchedContentCards = null,
                ProfileSignals = null
            })
            .ToList();


    private static List<RagMatch> FilterCanonicalPageWindow(
        IReadOnlyList<RagMatch> matches,
        int? pageStart,
        int? pageEnd, RagRetrievalDiagnosticsBuilder? diagnostics = null)
    {
        if (!pageStart.HasValue || !pageEnd.HasValue)
            return matches.ToList();

        var retained = new List<RagMatch>();
        foreach (var match in matches)
        {
            if (match.PageStart is { } start && match.PageEnd is { } end
                && end >= pageStart.Value && start <= pageEnd.Value) retained.Add(match);
            else diagnostics?.Reject("page_window", "outside_requested_page_window", match);
        }
        return retained;
    }


    internal static List<RagMatch> SelectCanonicalMatches(
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage, RagRetrievalDiagnosticsBuilder? diagnostics = null)
    {
        if (topK <= 0)
            return [];

        var selected = new List<RagMatch>(topK);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perDocument =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            if (selected.Count >= topK)
            { diagnostics?.Reject("selection", "top_k_limit", match); continue; }
            if (match.Score < minScore)
            { diagnostics?.Reject("selection", "score_threshold", match); continue; }

            var documentKey = NormalizeRagDocPath(match.DocPath);
            if (string.IsNullOrWhiteSpace(documentKey))
                documentKey = match.DocId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(documentKey))
            { diagnostics?.Reject("selection", "missing_document_identity", match); continue; }

            var identityKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(identityKey))
            { diagnostics?.Reject("selection", "duplicate_identity", match); continue; }

            perDocument.TryGetValue(documentKey, out var documentCount);
            if (documentCount >= maxPerDoc)
            {
                selectedKeys.Remove(identityKey);
                diagnostics?.Reject("selection", "document_limit", match);
                continue;
            }

            var pageKey =
                $"{documentKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCount);
            if (pageCount >= maxPerPage)
            {
                selectedKeys.Remove(identityKey);
                diagnostics?.Reject("selection", "page_limit", match);
                continue;
            }

            selected.Add(match);
            perDocument[documentKey] = documentCount + 1;
            perPage[pageKey] = pageCount + 1;
        }

        return selected;
    }

    // An exact-index entry is a locator into its source unit, never a chunk.
    // Return only a current chunk that both belongs to that unit (including
    // declared composition) and actually contains the indexed source text.
    internal static async Task<List<RagMatch>> SearchSourceBackedCanonicalExactMatchesAsync(
        NpgsqlDataSource ds, Guid tenantId, string query, string? category,
        string? docId, string? docPath, int topK, CancellationToken ct,
        string? categoryPath = null)
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms(query).Take(64).ToArray();
        if (terms.Length == 0 || topK <= 0) return [];
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedId) ? parsedId : null;
        const string sql = """
WITH lookup_terms AS (SELECT DISTINCT unnest(@terms::text[]) AS term)
SELECT d.doc_id AS "DocId", d.doc_path AS "DocPath", d.doc_name AS "DocName",
    d.category AS "Category", rc.page_start AS "PageStart", rc.page_end AS "PageEnd",
    rc.retrieval_chunk_id AS "ChunkId", rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text", rc.text_content AS "EmbedText",
    d.indexed_version AS "IngestionVersion", encode(r.source_hash, 'hex') AS "HashDoc",
    CASE WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
        THEN CASE WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetStart')::int END END AS "OffsetStart",
    CASE WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
        THEN CASE WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetEnd')::int END END AS "OffsetEnd",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
        THEN (rc.metadata->>'navigationScore')::double precision END AS "NavigationScore",
    CASE WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
        THEN (rc.metadata->>'contentDensityScore')::double precision END AS "ContentDensityScore",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
        THEN (rc.metadata->>'extractionTextSparse')::boolean END AS "ExtractionTextSparse",
    CASE WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
        THEN (rc.metadata->>'extractionOcrCandidate')::boolean END AS "ExtractionOcrCandidate",
    CASE WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
        THEN (rc.metadata->'extractionQualitySignals')::text END AS "ExtractionQualitySignalsJson",
    rc.metadata->>'prevChunkId' AS "PrevChunkId", rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId"
FROM documents d
JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id
    AND r.indexed_version=d.indexed_version
JOIN retrieval_chunks rc ON rc.tenant_id=r.tenant_id AND rc.revision_id=r.revision_id
LEFT JOIN document_sections s ON s.tenant_id=rc.tenant_id AND s.revision_id=rc.revision_id AND s.section_id=rc.section_id
WHERE d.tenant_id=@tenant AND d.status='indexed' AND d.indexed_version>0
    AND (@category IS NULL OR LOWER(d.category)=@category)
    AND (@categoryPath IS NULL OR d.doc_path=@categoryPath OR d.doc_path LIKE (@categoryPath || '/%'))
    AND (@docId IS NULL OR d.doc_id=@docId)
    AND (@docPath IS NULL OR d.doc_path=@docPath)
    AND EXISTS (
        SELECT 1 FROM exact_match_entries e
        JOIN document_units u ON u.tenant_id=e.tenant_id AND u.revision_id=e.revision_id AND u.unit_id=e.unit_id
        JOIN lookup_terms t ON md5(e.normalized_text)=md5(t.term) AND e.normalized_text=t.term
        WHERE e.tenant_id=rc.tenant_id AND e.revision_id=rc.revision_id
            AND (e.unit_id=rc.unit_id OR rc.metadata->'sourceUnitOrdinals' @> jsonb_build_array(u.ordinal))
            AND e.text_content<>'' AND position(e.text_content in rc.text_content)>0
    )
ORDER BY d.doc_id, rc.chunk_index
LIMIT @topK;
""";
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            terms,
            category,
            categoryPath = NormalizeRagCategoryPathForSql(categoryPath),
            docId = normalizedDocId,
            docPath = normalizedDocPath,
            topK
        }, cancellationToken: ct));
        // Every result is a literal exact match. Source type and query intent
        // do not introduce semantic bonuses; the existing RRF combines channels.
        return BuildSparseRagMatches(rows, query, "exact_match_v1")
            .Select(static match => match with { Score = 1.0 }).ToList();
    }

    internal static async Task<IReadOnlyDictionary<string, RagDocumentSourceIdentity>> LoadRagCanonicalDocumentSourceIdentitiesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var chunkIds = matches.Select(static match => Guid.TryParse(match.ChunkId, out var id) ? id : (Guid?)null)
            .Where(static id => id.HasValue).Select(static id => id!.Value).Distinct().ToArray();
        var identities = new Dictionary<string, RagDocumentSourceIdentity>(StringComparer.OrdinalIgnoreCase);
        if (chunkIds.Length == 0)
            return identities;

        // Resolve the actual current chunk, not just the latest document header.
        // Publication can change between retrieval and response construction.
        const string sql = """
SELECT d.doc_id AS "DocId", d.doc_path AS "DocPath",
       c.retrieval_chunk_id AS "ChunkId", r.revision_id::text AS "RevisionId",
       encode(r.source_hash, 'hex') AS "SourceHash"
FROM retrieval_chunks c
JOIN document_revisions r ON r.tenant_id=c.tenant_id AND r.revision_id=c.revision_id
JOIN documents d ON d.tenant_id=r.tenant_id AND d.doc_id=r.doc_id AND d.indexed_version=r.indexed_version
WHERE c.tenant_id=@tenant AND c.retrieval_chunk_id=ANY(@chunkIds)
  AND d.status='indexed' AND d.indexed_version>0;
""";
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CanonicalChunkIdentityRow>(new CommandDefinition(sql,
            new { tenant = tenantId, chunkIds }, cancellationToken: ct));
        foreach (var row in rows)
        {
            var key = BuildCanonicalChunkIdentityKey(row.DocId.ToString(), row.DocPath, row.ChunkId.ToString());
            if (key is not null && !string.IsNullOrWhiteSpace(row.RevisionId) && !string.IsNullOrWhiteSpace(row.SourceHash))
                identities[key] = new RagDocumentSourceIdentity(row.RevisionId, row.SourceHash);
        }
        return identities;
    }


    internal static string? BuildCanonicalChunkIdentityKey(string? docId, string? docPath, string? chunkId)
    {
        var path = NormalizeDocumentSourceHashPathKey(docPath);
        if (!Guid.TryParse(docId, out var document) || !Guid.TryParse(chunkId, out var chunk) || string.IsNullOrWhiteSpace(path))
            return null;
        return "canonical-chunk:" + JsonSerializer.Serialize(new[] { document.ToString("N"), path, chunk.ToString("N") });
    }


    internal static RagSearchResponse RetainCurrentCanonicalSourceIdentities(
        RagSearchResponse response, IReadOnlyDictionary<string, RagDocumentSourceIdentity> identities)
    {
        var current = response.Matches.Where(match => ResolveDocumentSourceIdentity(match, identities) is not null).ToList();
        if (current.Count == response.Matches.Count)
            return response;
        var errors = response.DegradedRetrieverErrors?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        errors["source_identity"] = "canonical_chunk_not_current";
        return response with
        {
            Matches = current,
            DegradedRetrievers = (response.DegradedRetrievers ?? Array.Empty<string>()).Append("source_identity")
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            DegradedRetrieverErrors = errors
        };
    }


    private sealed class CanonicalChunkIdentityRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public Guid ChunkId { get; set; }
        public string RevisionId { get; set; } = "";
        public string SourceHash { get; set; } = "";
    }

    /// <summary>
    /// Indexed lexical retrieval for the LLM-led source-backed path.
    ///
    /// This deliberately has no intent recognizer, unindexed LIKE/regex
    /// fallback, profile-card expansion, or semantic backfill. The LLM owns
    /// the search query; this method only executes that query mechanically
    /// against the stored full-text index. Dense and exact retrieval remain
    /// independent parallel channels in the canonical pipeline.
    /// </summary>
    internal static async Task<List<RagMatch>> SearchSourceBackedCanonicalSparseMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        Action<long> sparseMsRef,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
        {
            sparseMsRef(0);
            return [];
        }

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId)
            ? parsedDocId
            : null;
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(
            commandTimeoutSeconds);

        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
             AND (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetStart')::int
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
             AND (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetEnd')::int
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    cte.text_content AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\\.[0-9]+)?|\\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\\.[0-9]+)?|\\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    NULL::text AS "MatchedContentCardsJson",
    ts_rank_cd(cte.search_tsv, sparse_query.q, 32)::real AS "SparseRank"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
 AND (@category IS NULL OR LOWER(d.category) = @category)
 AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
 AND (@doc_id IS NULL OR d.doc_id = @doc_id)
 AND (@doc_path IS NULL OR d.doc_path = @doc_path)
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN contextual_text_entries cte
  ON cte.tenant_id = r.tenant_id
 AND cte.revision_id = r.revision_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = cte.retrieval_chunk_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE cte.search_tsv @@ sparse_query.q
ORDER BY "SparseRank" DESC, rc.chunk_index ASC
LIMIT @top_k;
""";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await ds.OpenConnectionAsync(ct);
            var rows = (await connection.QueryAsync<SparseMatchRow>(new CommandDefinition(
                sql,
                new
                {
                    tenant_id = tenantId,
                    query_text = query.Trim(),
                    category,
                    category_path = normalizedCategoryPath,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                },
                commandTimeout: commandTimeout,
                cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(
                rows,
                query,
                embeddingBasis: "source_backed_sparse_fts_v1");
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("canonical_sparse_fts", ex);
            degradedRetrieverRef?.Invoke(
                "canonical_sparse_fts",
                FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("canonical_sparse_fts", ex);
            degradedRetrieverRef?.Invoke(
                "canonical_sparse_fts",
                FormatRetrieverError(ex));
            return [];
        }
        finally
        {
            stopwatch.Stop();
            sparseMsRef(stopwatch.ElapsedMilliseconds);
        }
    }

}
