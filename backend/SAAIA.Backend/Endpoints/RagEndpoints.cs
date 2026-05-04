using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class RagEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);
        app.MapPost("/rag/search", SearchAsync);
        app.MapPost("/rag/query", QueryAsync);
        app.MapGet("/rag/debug/scroll", ScrollAsync).RequireAdminKey();
    }

    private static async Task<IResult> CategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

        const string sql = """
SELECT DISTINCT category
FROM documents
WHERE tenant_id=@tenant_id AND status='indexed'
ORDER BY category;
""";

        var cats = (await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted)
        )).ToArray();

        return Results.Ok(cats);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ds, ragOpt.Value, httpFactory, req);
        return Results.Ok(new
        {
            query = req.Query,
            category = resp.Category,
            topK = resp.TopK,
            matches = resp.Matches
        });
    }

    private static async Task<IResult> SearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ds, ragOpt.Value, httpFactory, req);
        var categoryRefsByTopLevelPath = await LoadTopCategoryRefsAsync(ds, ctx.GetTenantId(), resp.Matches, ctx.RequestAborted);
        var hypQuestionsMatchedByDocPath = await LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            ctx.GetTenantId(),
            resp.Query,
            resp.Matches,
            ctx.RequestAborted);

        var responseDto = new RagSearchResponseDto(
            RequestId: resp.RequestId,
            Query: resp.Query,
            QueryNormalized: resp.QueryNormalized,
            Category: resp.Category,
            TopK: resp.TopK,
            MinScore: resp.MinScore,
            Candidates: resp.Candidates,
            MaxPerDoc: resp.MaxPerDoc,
            MaxPerPage: resp.MaxPerPage,
            Metrics: new RagMetricsDto(
                TookMs: resp.Timings.TotalMs,
                Returned: resp.Matches.Count,
                ExactMatchReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal)),
                DenseReturned: resp.Matches.Count(m => string.Equals(ResolveRetriever(m), "dense_qdrant", StringComparison.Ordinal)),
                SparseReturned: resp.Matches.Count(m => string.Equals(ResolveRetriever(m), "sparse_bm25", StringComparison.Ordinal)),
                LinkedReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "linked_context_v1", StringComparison.Ordinal)),
                RetrieversUsed: resp.Matches
                    .Select(ResolveRetriever)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                DataHash: ComputeDataHash(resp.Matches),
                TtlSeconds: 600,
                TeiMs: resp.Timings.TeiMs,
                RerankMs: resp.Timings.RerankMs,
                SparseMs: resp.Timings.SparseMs,
                QdrantMs: resp.Timings.QdrantMs,
                CandidatesEvaluated: resp.Candidates
            ),
            Items: resp.Matches
                .Select(m =>
                {
                    var categoryPath = BuildDocumentCategoryPath(m.DocPath);
                    return new RagItemDto(
                        Score: m.Score,
                        DocId: m.DocId,
                        DocName: m.DocName ?? "Unknown",
                        DocPath: m.DocPath,
                        Category: BuildDocumentCategory(m.DocPath) ?? resp.Category,
                        CategoryRef: ResolveCategoryRef(categoryPath, categoryRefsByTopLevelPath),
                        PageStart: m.PageStart,
                        PageEnd: m.PageEnd,
                        ChunkId: m.ChunkId,
                        ChunkIndex: m.ChunkIndex,
                        Text: m.Text ?? string.Empty,
                        Retriever: ResolveRetriever(m),
                        Provenance: ResolveProvenance(m),
                        ExactMatchHit: string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal),
                        SourceHash: m.HashDoc,
                        EmbeddingBasis: m.EmbeddingBasis,
                        ChunkType: m.ChunkType,
                        SectionTitle: m.SectionTitle,
                        HeadingPath: m.HeadingPath,
                        PrevChunkId: m.PrevChunkId,
                        NextChunkId: m.NextChunkId,
                        SameSectionChunkId: m.SameSectionChunkId,
                        ProvenanceInfo: BuildProvenanceInfo(m),
                        Context: BuildContextInfo(m),
                        CategoryPath: categoryPath,
                        Snippet: BuildSnippet(m.Text, query: resp.Query),
                        RerankScore: m.RerankScore,
                        HasTable: DetectHasTable(m.Text),
                        HasWarning: DetectHasWarning(m.Text, m.ChunkType),
                        ContextualSnippet: req.IncludeContextualSnippet == true ? m.EmbedText : null,
                        HypQuestionsMatched: ResolveHypQuestionsMatched(m.DocPath, hypQuestionsMatchedByDocPath)
                    );
                })
                .ToList(),
            Guidance: BuildAnswerGuidance(resp.Query, resp.Matches)
        );

        return Results.Ok(responseDto);
    }

    internal static async Task<IReadOnlyDictionary<string, bool?>> LoadHypQuestionsMatchedByDocPathAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var docPaths = matches
            .Select(static match => match.DocPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (docPaths.Length == 0)
            return new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var docs = (await conn.QueryAsync<CapabilityAHypQuestionDocRow>(new CommandDefinition(
            """
SELECT d.doc_id AS "DocId",
       d.doc_path AS "DocPath",
       d.doc_name AS "DocName",
       d.indexed_version AS "IndexedVersion",
       COALESCE(to_jsonb(profile.hypothetical_questions)::text, '[]') AS "HypotheticalQuestionsJson"
FROM documents d
LEFT JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
LEFT JOIN LATERAL (
    SELECT p.hypothetical_questions
    FROM document_profiles p
    WHERE p.tenant_id = r.tenant_id
      AND p.revision_id = r.revision_id
    ORDER BY
        CASE p.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'deterministic_v1' THEN 1
            ELSE 2
        END,
        p.updated_at DESC
    LIMIT 1
) profile ON TRUE
WHERE d.tenant_id=@tenant
  AND d.doc_path = ANY(@docPaths)
  AND d.indexed_version > 0
ORDER BY d.doc_path;
""",
            new
            {
                tenant = tenantId,
                docPaths
            },
            cancellationToken: ct)))
            .Select(static row => new CapabilityAHypQuestionDoc(
                row.DocId,
                row.DocPath,
                row.DocName,
                row.IndexedVersion,
                ParseJsonStringArray(row.HypotheticalQuestionsJson)))
            .ToArray();

        var docsMissingStoredQuestions = docs
            .Where(static doc => doc.HypotheticalQuestions.Length == 0)
            .ToArray();
        var missingDocVersions = docsMissingStoredQuestions
            .Select(static doc => (doc.DocId, doc.IndexedVersion))
            .ToArray();
        IReadOnlyDictionary<Guid, string[]> sectionTitlesByDocId = new Dictionary<Guid, string[]>();
        IReadOnlyDictionary<Guid, string[]> excerptsByDocId = new Dictionary<Guid, string[]>();

        if (missingDocVersions.Length > 0)
        {
            sectionTitlesByDocId = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesBatchAsync(
                conn,
                tenantId,
                missingDocVersions,
                limit: 3,
                ct);
            excerptsByDocId = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
                conn,
                tenantId,
                missingDocVersions,
                limit: 2,
                ct);
        }

        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            IReadOnlyList<string> hypotheticalQuestions = doc.HypotheticalQuestions;
            if (hypotheticalQuestions.Count == 0)
            {
                sectionTitlesByDocId.TryGetValue(doc.DocId, out var sectionTitles);
                excerptsByDocId.TryGetValue(doc.DocId, out var excerpts);
                hypotheticalQuestions = RuntimeGovernanceService.BuildCapabilityAHypotheticalQuestions(
                    doc.DocName,
                    sectionTitles ?? Array.Empty<string>(),
                    excerpts ?? Array.Empty<string>());
            }

            result[doc.DocPath] = hypotheticalQuestions.Count == 0
                ? null
                : ComputeHypQuestionsMatched(query, hypotheticalQuestions);
        }

        return result;
    }

    internal static async Task<RagSearchResponse> SearchCoreAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = ctx.GetTenantId();

        if (string.IsNullOrWhiteSpace(req.Query))
            throw new BadHttpRequestException("query is required");

        var topK = req.TopK ?? rag.DefaultTopK;
        topK = Math.Clamp(topK, 1, rag.MaxTopK);

        var category = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();
        var retrievalQuery = ExpandRetrievalQuery(req.Query, category);
        var queryNorm = NormalizeQuery(req.Query);
        var retrievalQueryNorm = NormalizeQuery(retrievalQuery);

        var mode = ResolveEffectiveSearchMode(req.Mode, req.Query);
        var preferComparativeDiversity = ShouldPreferComparativeDocumentDiversity(req.Query) && mode != "focused";
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = ResolveDefaultCandidateCount(mode, preferComparativeDiversity, topK);
        // CDC v3.1 §11.3: max 3 chunks per document default
        int defMaxPerDoc = ResolveDefaultMaxPerDoc(mode, preferComparativeDiversity, topK);

        var minScore = Math.Clamp(req.MinScore ?? defMinScore, 0.0, 1.0);
        var candidates = Math.Clamp(req.Candidates ?? defCandidates, topK, Math.Max(topK, rag.MaxTopK * 20));
        var maxPerDoc = Math.Clamp(req.MaxPerDoc ?? defMaxPerDoc, 1, topK);
        var maxPerPage = Math.Clamp(req.MaxPerPage ?? 1, 1, topK);
        var hasExplicitMaxPerDoc = req.MaxPerDoc.HasValue;

        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
            {
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
                hasExplicitMaxPerDoc = true;
            }
            if (req.Diversity.PreferDistinctPages == true)
                maxPerPage = 1;
        }

        if (!hasExplicitMaxPerDoc && ShouldConstrainPreciseTitleLookup(req.Query))
            maxPerDoc = 1;

        var ct = ctx.RequestAborted;
        var swTotal = Stopwatch.StartNew();
        var hasCategoryFilter = !string.IsNullOrWhiteSpace(category);
        var hasDocScope = !string.IsNullOrWhiteSpace(req.DocId) || !string.IsNullOrWhiteSpace(req.DocPath);
        using var searchActivity = RetrievalTelemetry.StartSearchActivity(mode, hasCategoryFilter, hasDocScope, topK, candidates, req.Query);

        async Task<(T Result, long DurationMs)> MeasurePhaseAsync<T>(
            string phaseName,
            string? retriever,
            Func<Task<T>> action,
            Func<T, int>? getReturnedCount = null)
        {
            using var phaseActivity = RetrievalTelemetry.StartPhaseActivity(phaseName);
            var swPhase = Stopwatch.StartNew();

            try
            {
                var result = await action();
                swPhase.Stop();
                RetrievalTelemetry.CompletePhase(phaseActivity, getReturnedCount?.Invoke(result) ?? 0, swPhase.ElapsedMilliseconds, retriever);
                return (result, swPhase.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                swPhase.Stop();
                RetrievalTelemetry.MarkPhaseError(phaseActivity, ex);
                throw;
            }
        }

        var (exactMatches, exactMs) = await MeasurePhaseAsync(
            phaseName: "retrieval_exact_match",
            retriever: "exact_match",
            action: () => SearchExactMatchesAsync(ds, tenantId, req.Query, category, req.DocId, req.DocPath, topK, ct),
            getReturnedCount: static matches => matches.Count);
        var (quotedTitleMatches, _) = await MeasurePhaseAsync(
            phaseName: "retrieval_quoted_title",
            retriever: "quoted_title",
            action: () => SearchQuotedTitleMatchesAsync(ds, tenantId, req.Query, category, req.DocId, req.DocPath, topK, ct),
            getReturnedCount: static matches => matches.Count);
        var shortCircuitAfterExact = ShouldShortCircuitAfterExact(exactMatches);
        var selected = new List<RagMatch>(capacity: topK);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long teiMs = 0;
        long rerankMs = 0;
        long sparseMs = 0;
        long qdrantMs = 0;
        long sparsePhaseMs = 0;
        long densePhaseMs = 0;
        long linkedPhaseMs = 0;
        long profilePhaseMs = 0;
        int qdrantStatus = 0;

        if (shortCircuitAfterExact)
        {
            AddRankedMatches(selected, selectedKeys, exactMatches, topK, minScore: 0.0, maxPerDoc, maxPerPage);
        }
        else
        {
            AddRankedMatches(selected, selectedKeys, quotedTitleMatches, topK, minScore: 0.0, maxPerDoc, Math.Max(maxPerPage, 2));

            var sparseMatchesTask = MeasurePhaseAsync(
                phaseName: "retrieval_sparse",
                retriever: "sparse_bm25",
                action: () => SearchSparseMatchesAsync(
                    ds,
                    tenantId,
                    req.Query,
                    category,
                    req.DocId,
                    req.DocPath,
                    candidates,
                    ct,
                    sparseMsRef: value => sparseMs = value,
                    lexicalExpansionQuery: retrievalQuery),
                getReturnedCount: static matches => matches.Count);
            var denseMatchesTask = MeasurePhaseAsync(
                phaseName: "retrieval_dense",
                retriever: "dense_qdrant",
                action: () => SearchDenseMatchesAsync(
                    ds,
                    httpFactory,
                    rag,
                    tenantId,
                    retrievalQueryNorm,
                    category,
                    req.DocId,
                    req.DocPath,
                    candidates,
                    ct,
                    teiMsRef: value => teiMs = value,
                    qdrantMsRef: value => qdrantMs = value,
                    qdrantStatusRef: value => qdrantStatus = value),
                getReturnedCount: static matches => matches.Count);
            var profileMatchesTask = MeasurePhaseAsync(
                phaseName: "retrieval_document_profile",
                retriever: "document_profile",
                action: () => SearchDocumentProfileMatchesAsync(
                    ds,
                    tenantId,
                    retrievalQuery,
                    category,
                    req.DocId,
                    req.DocPath,
                    Math.Min(candidates, Math.Max(topK, 12)),
                    ct),
                getReturnedCount: static matches => matches.Count);

            await Task.WhenAll(sparseMatchesTask, denseMatchesTask, profileMatchesTask);

            var (sparseMatches, measuredSparsePhaseMs) = await sparseMatchesTask;
            var (denseMatches, measuredDensePhaseMs) = await denseMatchesTask;
            var (profileMatches, measuredProfilePhaseMs) = await profileMatchesTask;
            sparsePhaseMs = measuredSparsePhaseMs;
            densePhaseMs = measuredDensePhaseMs;
            profilePhaseMs = measuredProfilePhaseMs;
            var fusedMatches = FuseWithRrf(exactMatches, sparseMatches, denseMatches, profileMatches);
            fusedMatches = CalibrateFusedMatches(retrievalQuery, fusedMatches, req.Query);
            fusedMatches = SuppressNavigationalNoise(req.Query, fusedMatches);
            if (ShouldSuppressUnanchoredSpecificResults(retrievalQuery, fusedMatches))
            {
                fusedMatches = [];
            }
            else
            {
                var (rerankedMatches, _) = await MeasurePhaseAsync(
                    phaseName: "retrieval_rerank",
                    retriever: "tei_rerank",
                    action: () => TryRerankWithTeiAsync(
                        httpFactory,
                        rag,
                        retrievalQuery,
                        fusedMatches,
                        ct,
                        rerankMsRef: value => rerankMs = value),
                    getReturnedCount: static matches => matches.Count);
                fusedMatches = rerankedMatches;
                fusedMatches = CalibrateFusedMatches(retrievalQuery, fusedMatches, req.Query);
                fusedMatches = SuppressNavigationalNoise(req.Query, fusedMatches);
            }

            AddRankedMatches(selected, selectedKeys, fusedMatches, topK, minScore, maxPerDoc, maxPerPage);

            if (selected.Count < topK)
            {
                var (linkedMatches, linkedDurationMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        ds,
                        tenantId,
                        selected,
                        category,
                        req.DocId,
                        req.DocPath,
                        topK - selected.Count,
                        ct),
                    getReturnedCount: static matches => matches.Count);
                linkedPhaseMs += linkedDurationMs;

                AddRankedMatches(selected, selectedKeys, linkedMatches, topK, minScore: 0.0, Math.Max(maxPerDoc, 2), Math.Max(maxPerPage, 2));
            }

            if (selected.Count < topK)
            {
                var (secondWaveLinkedMatches, secondWaveLinkedMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        ds,
                        tenantId,
                        selected,
                        category,
                        req.DocId,
                        req.DocPath,
                        topK - selected.Count,
                        ct),
                    getReturnedCount: static matches => matches.Count);
                linkedPhaseMs += secondWaveLinkedMs;

                AddRankedMatches(selected, selectedKeys, secondWaveLinkedMatches, topK, minScore: 0.0, Math.Max(maxPerDoc, 2), Math.Max(maxPerPage, 2));
            }
        }

        // CDC v3.1 §11.3: autocut - remove trailing results after largest relative score drop
        PrioritizeExactTitleSelections(req.Query, selected);
        PrioritizeQuotedTitleSelections(req.Query, selected);
        PruneWeakTitleExpansionSelections(req.Query, selected);
        PruneWeakAdjacentSiblingSelections(req.Query, selected);
        PrunePreciseTitleTailSelections(req.Query, selected);
        PruneUnmatchedPreciseTitleSelections(req.Query, selected);
        PruneNavigationalSelections(req.Query, selected);
        ApplyAutocut(selected, minScore);

        if (ShouldBackfillEnumerativeSearch(req.Query, selected.Count, topK))
        {
            var focusedLexicalQuery = BuildFocusedLexicalBackfillQuery(req.Query);
            if (!string.IsNullOrWhiteSpace(focusedLexicalQuery)
                && !string.Equals(focusedLexicalQuery.Trim(), req.Query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var (backfillMatches, backfillDurationMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_lexical_backfill",
                    retriever: "lexical_backfill",
                    action: () => SearchSparseMatchesAsync(
                        ds,
                        tenantId,
                        focusedLexicalQuery,
                        category,
                        req.DocId,
                        req.DocPath,
                        Math.Max(candidates, topK * 4),
                        ct,
                        sparseMsRef: value => sparseMs += value,
                        lexicalExpansionQuery: focusedLexicalQuery),
                    getReturnedCount: static matches => matches.Count);
                sparsePhaseMs += backfillDurationMs;

                var calibratedBackfill = CalibrateFusedMatches(focusedLexicalQuery, backfillMatches, req.Query);
                calibratedBackfill = SuppressNavigationalNoise(req.Query, calibratedBackfill);
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    calibratedBackfill,
                    topK,
                    minScore: 0.0,
                    maxPerDoc,
                    Math.Max(maxPerPage, 2));
                PruneNavigationalSelections(req.Query, selected);
            }
        }

        swTotal.Stop();

        var response = new RagSearchResponse(
            RequestId: ctx.GetRequestId(),
            Query: req.Query,
            QueryNormalized: queryNorm,
            Category: category,
            TopK: topK,
            MinScore: minScore,
            Candidates: candidates,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            QdrantStatus: qdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: swTotal.ElapsedMilliseconds,
                TeiMs: teiMs,
                RerankMs: rerankMs,
                SparseMs: sparseMs,
                QdrantMs: qdrantMs),
            Matches: selected);

        RetrievalTelemetry.CompleteSearch(searchActivity, response, mode, hasCategoryFilter, hasDocScope);
        RetrievalTelemetry.RecordSearch(response, mode, hasCategoryFilter, hasDocScope, exactMs, sparsePhaseMs + profilePhaseMs, densePhaseMs, linkedPhaseMs);

        return response;
    }

    internal static string ResolveEffectiveSearchMode(string? requestedMode, string query)
    {
        var mode = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "focused" or "balanced" or "broad")
            return mode;

        return ShouldPreferComparativeDocumentDiversity(query)
            ? "broad"
            : "balanced";
    }

    internal static bool ShouldPreferComparativeDocumentDiversity(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(query).ToLowerInvariant() + " ";
        if (ContainsAny(normalized,
                " compare ",
                " comparer ",
                " compares ",
                " comparing ",
                " comparative ",
                " comparaison ",
                " comparatif ",
                " compara ",
                " comparar ",
                " comparacion ",
                " comparacao ",
                " confronto ",
                " confronta ",
                " confronto ",
                " vergleich ",
                " vergleichen ",
                " choisir ",
                " choose ",
                " escoge ",
                " escolher ",
                " scegliere ",
                " wahlen "))
            return true;

        if (ContainsAny(normalized, " le plus ", " la plus ", " les plus ", " most ", " mas ", " mais ", " am meisten "))
            return ContainsAny(normalized,
                " quel ",
                " quelle ",
                " quels ",
                " quelles ",
                " lequel ",
                " laquelle ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welches ",
                " welcher ",
                " welche ");

        return false;
    }

    internal static int ResolveDefaultCandidateCount(string mode, bool preferComparativeDiversity, int topK)
        => mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * (preferComparativeDiversity ? 15 : 12),
            _ => topK * 10
        };

    internal static int ResolveDefaultMaxPerDoc(string mode, bool preferComparativeDiversity, int topK)
        => preferComparativeDiversity && mode != "focused"
            ? 1
            : mode switch
            {
                "focused" => Math.Min(topK, 3),
                "broad" => 2,
                _ => Math.Min(3, Math.Max(2, topK / 2))
            };

    internal static bool ShouldConstrainPreciseTitleLookup(string query)
    {
        var tokens = ExtractTitlePruneTokens(query);
        if (tokens.Length is < 2 or > 6)
            return false;
        if (ExtractComparativeSubjectAnchorTokens(query).Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (IsRecommendationSelectionQuery(normalized))
            return false;
        if (ContainsEnumerativeLookupIntent(normalized))
            return false;

        return !ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparison ",
            " comparaison ",
            " difference ",
            " differences ",
            " differents ",
            " differentes ");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static async Task<List<RagMatch>> SearchExactMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        var normalizedTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query);
        if (normalizedTerms.Count == 0)
            return [];

        var referenceKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(query);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH lookup_terms AS (
    SELECT DISTINCT term
    FROM unnest(@normalized_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    e.page_start AS "PageStart",
    e.page_end AS "PageEnd",
    (e.metadata->>'offsetStart')::int AS "OffsetStart",
    (e.metadata->>'offsetEnd')::int AS "OffsetEnd",
    e.exact_match_entry_id AS "ExactMatchEntryId",
    e.entry_index AS "ChunkIndex",
    e.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(e.metadata->>'kind', 'verbatim_excerpt') AS "MatchKind",
    s.title AS "SectionTitle",
    lookup_terms.term AS "MatchedTerm"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN exact_match_entries e
  ON e.tenant_id = r.tenant_id
 AND e.revision_id = r.revision_id
JOIN lookup_terms
  ON lookup_terms.term = e.normalized_text
LEFT JOIN document_sections s
  ON s.section_id = e.section_id
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE COALESCE(e.metadata->>'kind', 'verbatim_excerpt')
        WHEN 'standard_ref' THEN 0
        WHEN 'code_ref' THEN 1
        ELSE 2
    END,
    LENGTH(lookup_terms.term) DESC,
    d.updated_at DESC,
    e.page_start ASC,
    e.entry_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<ExactMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            normalized_terms = normalizedTerms.ToArray(),
            category,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK
        }, cancellationToken: ct));

        var matches = rows.Select(row => new RagMatch(
            Score: ComputeExactMatchScore(row.MatchKind, row.MatchedTerm, row.Text),
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            PageStart: row.PageStart,
            PageEnd: row.PageEnd,
            OffsetStart: row.OffsetStart,
            OffsetEnd: row.OffsetEnd,
            ChunkId: row.ExactMatchEntryId.ToString(),
            ChunkIndex: row.ChunkIndex,
            Text: row.Text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: row.Text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: row.SectionTitle,
            HeadingPath: row.SectionTitle,
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null)).ToList();

        if (matches.Count < topK)
        {
            var metadataMatches = await SearchDocumentMetadataMatchesAsync(
                conn,
                tenantId,
                normalizedTerms,
                referenceKeys,
                category,
                normalizedDocId,
                normalizedDocPath,
                topK - matches.Count,
                ct);

            foreach (var metadataMatch in metadataMatches)
            {
                if (matches.Count >= topK)
                    break;

                if (!matches.Any(existing =>
                        string.Equals(existing.DocId, metadataMatch.DocId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Text, metadataMatch.Text, StringComparison.Ordinal)))
                {
                    matches.Add(metadataMatch);
                }
            }
        }

        return matches;
    }

    internal static async Task<List<RagMatch>> SearchQuotedTitleMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        var quotedTerms = ExtractQuotedLookupPhrases(query);
        if (quotedTerms.Count == 0 || topK <= 0)
            return [];

        var candidateLimit = Math.Clamp(topK * 4, topK, 64);
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH quoted_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@quoted_terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 4
),
profile_card_matches AS (
    SELECT
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT pcc.title, '; '), 500) AS card_titles,
        SUM(CASE
            WHEN LOWER(pcc.search_text) LIKE '%' || quoted_terms.term || '%'
              OR pcc.normalized_title LIKE '%' || quoted_terms.term || '%'
                THEN 10.0
            ELSE 0.0
        END) AS match_weight
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN LATERAL (
        SELECT profile.*
        FROM document_profiles profile
        WHERE profile.tenant_id = r.tenant_id
          AND profile.revision_id = r.revision_id
        ORDER BY
            CASE profile.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            profile.updated_at DESC
        LIMIT 1
    ) p ON TRUE
    JOIN document_profile_content_cards pcc
      ON pcc.tenant_id = p.tenant_id
     AND pcc.document_profile_id = p.document_profile_id
    CROSS JOIN quoted_terms
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND pcc.page_start IS NOT NULL
      AND (
          LOWER(pcc.search_text) LIKE '%' || quoted_terms.term || '%'
          OR pcc.normalized_title LIKE '%' || quoted_terms.term || '%'
      )
    GROUP BY
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)),
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        )
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    (rc.metadata->>'offsetStart')::int AS "OffsetStart",
    (rc.metadata->>'offsetEnd')::int AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched quoted title: ' || pcm.card_titles || E'\n' || cte.text_content
        WHEN COALESCE(tm.matched_phrases, '') <> ''
            THEN 'Matched quoted title: ' || tm.matched_phrases || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    (tm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 2.2))::real AS "SparseRank"
FROM documents d
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
LEFT JOIN profile_card_matches pcm
  ON pcm.revision_id = r.revision_id
 AND rc.page_start <= pcm.card_page_end
 AND rc.page_end >= pcm.card_page_start
CROSS JOIN LATERAL (
    SELECT LOWER(rc.text_content) AS text_lc
) normalized_chunk
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT quoted_terms.term, '; '), 500) AS matched_phrases,
        COALESCE(SUM(CASE WHEN normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%' THEN 10.0 ELSE 0.0 END), 0.0) AS match_weight
    FROM quoted_terms
    WHERE normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%'
) tm
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (tm.match_count > 0 OR COALESCE(pcm.match_count, 0) > 0)
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    (tm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 2.2)) DESC,
    COALESCE(pcm.match_count, 0) DESC,
    tm.match_count DESC,
    rc.chunk_index ASC
LIMIT @candidate_limit;
""";

        var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            quoted_terms = quotedTerms.ToArray(),
            category,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            candidate_limit = candidateLimit
        }, cancellationToken: ct));

        return rows
            .Select(row => new
            {
                Row = row,
                QuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedTerms,
                    string.Join("\n", new[]
                    {
                        row.EmbedText,
                        row.Text,
                        row.SectionTitle,
                        row.HeadingPath
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))))
            })
            .Where(static item => item.QuotedScore > 0.0)
            .OrderByDescending(static item => item.QuotedScore)
            .ThenByDescending(static item => item.Row.SparseRank)
            .ThenBy(static item => item.Row.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Row.ChunkIndex)
            .Take(topK)
            .Select(item =>
            {
                var row = item.Row;
                return new RagMatch(
            Score: Math.Clamp(0.88 + Math.Min(0.14, item.QuotedScore * 0.01), 0.0, 1.02),
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            PageStart: row.PageStart,
            PageEnd: row.PageEnd,
            OffsetStart: row.OffsetStart,
            OffsetEnd: row.OffsetEnd,
            ChunkId: row.ChunkId.ToString(),
            ChunkIndex: row.ChunkIndex,
            Text: row.Text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: row.EmbedText,
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: row.SectionTitle,
            HeadingPath: row.HeadingPath,
            ChunkType: row.ChunkType,
            PrevChunkId: row.PrevChunkId,
            NextChunkId: row.NextChunkId,
                    SameSectionChunkId: row.SameSectionChunkId);
            })
            .ToList();
    }

    private static async Task<List<RagMatch>> SearchDenseMatchesAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        Guid tenantId,
        string queryNorm,
        string? category,
        string? docId,
        string? docPath,
        int candidates,
        CancellationToken ct,
        Action<long> teiMsRef,
        Action<long> qdrantMsRef,
        Action<int> qdrantStatusRef)
    {
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var swTei = Stopwatch.StartNew();
        var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, [queryNorm], ct);
        swTei.Stop();
        teiMsRef(swTei.ElapsedMilliseconds);

        var qvec = emb[0];
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(category))
            filterMust.Add(new { key = "category", match = new { value = category } });
        if (!string.IsNullOrWhiteSpace(docId))
            filterMust.Add(new { key = "doc_id", match = new { value = docId.Trim() } });
        if (!string.IsNullOrWhiteSpace(docPath))
            filterMust.Add(new { key = "doc_path", match = new { value = docPath.Trim().Replace('\\', '/') } });

        var payload = new
        {
            vector = qvec,
            limit = candidates,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";
        HttpResponseMessage? resp = null;

        var swQ = Stopwatch.StartNew();
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))
                resp = await qdrant.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = null;
                await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

                using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                resp = await qdrant.PostAsync(url, content2, ct);
            }

            qdrantStatusRef((int)resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var rawMatches = QdrantClient.ParseSearchResults(doc);
            var filtered = await FilterMatchesAgainstActiveDocumentVersionsAsync(ds, tenantId, rawMatches, ct);
            return RerankDenseMatches(filtered);
        }
        finally
        {
            swQ.Stop();
            qdrantMsRef(swQ.ElapsedMilliseconds);
            resp?.Dispose();
        }
    }

    private static async Task<List<RagMatch>> TryRerankWithTeiAsync(
        IHttpClientFactory httpFactory,
        RagOptions rag,
        string query,
        IReadOnlyList<RagMatch> candidates,
        CancellationToken ct,
        Action<long> rerankMsRef)
    {
        if (!rag.EnableRerank || candidates.Count <= 1)
        {
            rerankMsRef(0);
            return candidates.ToList();
        }

        var rerankBaseUrl = string.IsNullOrWhiteSpace(rag.RerankBaseUrl)
            ? rag.EmbeddingsBaseUrl
            : rag.RerankBaseUrl!;
        var maxCandidates = Math.Clamp(rag.RerankMaxCandidates, 2, Math.Max(2, candidates.Count));
        var rerankSlice = candidates.Take(maxCandidates).ToList();
        var texts = rerankSlice
            .Select(match => string.IsNullOrWhiteSpace(match.EmbedText) ? (match.Text ?? string.Empty) : match.EmbedText!)
            .ToArray();

        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rerankBaseUrl);

        var sw = Stopwatch.StartNew();
        try
        {
            var reranked = await TeiClient.RerankAsync(tei, rag.RerankModel, query, texts, ct);
            return ApplyRerankScores(candidates, reranked, rerankSlice.Count);
        }
        catch
        {
            return candidates.ToList();
        }
        finally
        {
            sw.Stop();
            rerankMsRef(sw.ElapsedMilliseconds);
        }
    }

    internal static async Task<List<RagMatch>> SearchSparseMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        Action<long> sparseMsRef,
        string? lexicalExpansionQuery = null)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
        {
            sparseMsRef(0);
            return [];
        }

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var sparseQueryText = string.IsNullOrWhiteSpace(lexicalExpansionQuery)
            ? query.Trim()
            : lexicalExpansionQuery.Trim();

        var lexicalTerms = BuildLexicalContentFallbackTerms(sparseQueryText);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
),
lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
profile_card_matches AS (
    SELECT
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT pcc.title, '; '), 500) AS card_titles,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 8.0
                WHEN lexical_terms.term LIKE '% %' THEN 6.0
                WHEN length(lexical_terms.term) >= 10 THEN 3.0
                WHEN length(lexical_terms.term) >= 7 THEN 2.0
                ELSE 1.1
            END), 0.0) AS match_weight
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN LATERAL (
        SELECT profile.*
        FROM document_profiles profile
        WHERE profile.tenant_id = r.tenant_id
          AND profile.revision_id = r.revision_id
        ORDER BY
            CASE profile.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            profile.updated_at DESC
        LIMIT 1
    ) p ON TRUE
    JOIN document_profile_content_cards pcc
      ON pcc.tenant_id = p.tenant_id
     AND pcc.document_profile_id = p.document_profile_id
    CROSS JOIN lexical_terms
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND pcc.page_start IS NOT NULL
      AND CASE
          WHEN lexical_terms.term LIKE '% %'
              THEN LOWER(pcc.search_text) ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
                   OR pcc.normalized_title ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
          ELSE LOWER(pcc.search_text) LIKE '%' || lexical_terms.term || '%'
               OR pcc.normalized_title LIKE '%' || lexical_terms.term || '%'
      END
    GROUP BY
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)),
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        )
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    (rc.metadata->>'offsetStart')::int AS "OffsetStart",
    (rc.metadata->>'offsetEnd')::int AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched profile title: ' || pcm.card_titles || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    (
        ts_rank_cd(
            to_tsvector('simple', cte.text_content),
            sparse_query.q,
            32
        ) + ((COALESCE(pcm.match_weight, 0.0) * 0.035)::real)
    )::real AS "SparseRank"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
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
LEFT JOIN profile_card_matches pcm
  ON pcm.revision_id = r.revision_id
 AND rc.page_start <= pcm.card_page_end
 AND rc.page_end >= pcm.card_page_start
WHERE to_tsvector('simple', cte.text_content) @@ sparse_query.q
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY "SparseRank" DESC,
         COALESCE(pcm.match_count, 0) DESC,
         rc.chunk_index ASC
LIMIT @top_k;
""";

        var swSparse = Stopwatch.StartNew();
        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_text = sparseQueryText,
                lexical_terms = lexicalTerms.ToArray(),
                category,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, cancellationToken: ct))).ToList();

            if (lexicalTerms.Count > 0
                && (rows.Count == 0
                    || !string.IsNullOrWhiteSpace(category)
                    || ShouldSupplementSparseWithLexicalFallback(category, sparseQueryText)))
            {
                var fallbackRows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
                {
                    tenant_id = tenantId,
                    lexical_terms = lexicalTerms.ToArray(),
                    category,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                }, cancellationToken: ct))).ToList();

                rows = rows.Count == 0
                    ? fallbackRows
                    : MergeSparseRows(fallbackRows, rows, topK);
            }

            return rows.Select(row => new RagMatch(
                Score: NormalizeSparseScore(row.SparseRank),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                OffsetStart: row.OffsetStart,
                OffsetEnd: row.OffsetEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.EmbedText,
                EmbeddingBasis: "sparse_bm25_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId)).ToList();
        }
        catch (PostgresException)
        {
            return [];
        }
        finally
        {
            swSparse.Stop();
            sparseMsRef(swSparse.ElapsedMilliseconds);
        }
    }

private const string LexicalContentFallbackSql = """
WITH lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
profile_card_matches AS (
    SELECT
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT pcc.title, '; '), 500) AS card_titles,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 8.0
                WHEN lexical_terms.term LIKE '% %' THEN 6.0
                WHEN length(lexical_terms.term) >= 10 THEN 3.0
                WHEN length(lexical_terms.term) >= 7 THEN 2.0
                ELSE 1.1
            END), 0.0) AS match_weight
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN LATERAL (
        SELECT profile.*
        FROM document_profiles profile
        WHERE profile.tenant_id = r.tenant_id
          AND profile.revision_id = r.revision_id
        ORDER BY
            CASE profile.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            profile.updated_at DESC
        LIMIT 1
    ) p ON TRUE
    JOIN document_profile_content_cards pcc
      ON pcc.tenant_id = p.tenant_id
     AND pcc.document_profile_id = p.document_profile_id
    CROSS JOIN lexical_terms
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND pcc.page_start IS NOT NULL
      AND CASE
          WHEN lexical_terms.term LIKE '% %'
              THEN LOWER(pcc.search_text) ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
                   OR pcc.normalized_title ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
          ELSE LOWER(pcc.search_text) LIKE '%' || lexical_terms.term || '%'
               OR pcc.normalized_title LIKE '%' || lexical_terms.term || '%'
      END
    GROUP BY
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)),
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        )
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    (rc.metadata->>'offsetStart')::int AS "OffsetStart",
    (rc.metadata->>'offsetEnd')::int AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched profile title: ' || pcm.card_titles || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    (lm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 1.8))::real AS "SparseRank"
FROM documents d
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
LEFT JOIN profile_card_matches pcm
  ON pcm.revision_id = r.revision_id
 AND rc.page_start <= pcm.card_page_end
 AND rc.page_end >= pcm.card_page_start
CROSS JOIN LATERAL (
    SELECT LOWER(rc.text_content) AS text_lc
) normalized_chunk
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM lexical_terms
    WHERE CASE
        WHEN lexical_terms.term LIKE '% %'
            THEN normalized_chunk.text_lc ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
        ELSE normalized_chunk.text_lc LIKE '%' || lexical_terms.term || '%'
    END
) lm
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (lm.match_count > 0 OR COALESCE(pcm.match_count, 0) > 0)
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY (lm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 1.8)) DESC,
         COALESCE(pcm.match_count, 0) DESC,
         lm.match_count DESC,
         rc.chunk_index ASC
LIMIT @top_k;
""";

    internal static async Task<List<RagMatch>> SearchDocumentProfileMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var lexicalTerms = BuildLexicalContentFallbackTerms(query);
        if (lexicalTerms.Count == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
),
lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    p.document_profile_id AS "ProfileId",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    p.summary_text AS "Text",
    p.search_text AS "SearchText",
    COALESCE(cards.metadata_json, p.metadata::text) AS "MetadataJson",
    p.language AS "Language",
    ts_rank_cd(
        to_tsvector('simple', p.search_text),
        sparse_query.q,
        32
    ) + ((lm.match_weight + COALESCE(cm.match_weight, 0.0))::real * 0.04) AS "SparseRank",
    (lm.match_count + COALESCE(cm.match_count, 0)) AS "MatchCount"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN LATERAL (
    SELECT profile.*
    FROM document_profiles profile
    WHERE profile.tenant_id = r.tenant_id
      AND profile.revision_id = r.revision_id
    ORDER BY
        CASE profile.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'deterministic_v1' THEN 1
            ELSE 2
        END,
        profile.updated_at DESC
    LIMIT 1
) p ON TRUE
LEFT JOIN LATERAL (
    SELECT CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE jsonb_build_object(
            'contentCards',
            jsonb_agg(
                jsonb_build_object(
                    'title', card.title,
                    'pageStart', card.page_start,
                    'pageEnd', card.page_end,
                    'kind', card.kind,
                    'signals', card.signals)
                ORDER BY card.card_index))::text
        END AS metadata_json
    FROM document_profile_content_cards card
    WHERE card.tenant_id = p.tenant_id
      AND card.document_profile_id = p.document_profile_id
) cards ON TRUE
LEFT JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM document_profile_content_cards card
    CROSS JOIN lexical_terms
    WHERE card.tenant_id = p.tenant_id
      AND card.document_profile_id = p.document_profile_id
      AND CASE
        WHEN lexical_terms.term LIKE '% %'
            THEN LOWER(card.search_text) ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
                 OR card.normalized_title ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
        ELSE LOWER(card.search_text) LIKE '%' || lexical_terms.term || '%'
             OR card.normalized_title LIKE '%' || lexical_terms.term || '%'
      END
) cm ON TRUE
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM lexical_terms
    WHERE CASE
        WHEN lexical_terms.term LIKE '% %'
            THEN LOWER(p.search_text) ~ REPLACE(lexical_terms.term, ' ', '.{0,40}')
        ELSE LOWER(p.search_text) LIKE '%' || lexical_terms.term || '%'
    END
) lm
WHERE (
       to_tsvector('simple', p.search_text) @@ sparse_query.q
       OR lm.match_count > 0
       OR COALESCE(cm.match_count, 0) > 0
      )
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    (ts_rank_cd(to_tsvector('simple', p.search_text), sparse_query.q, 32)
        + ((lm.match_weight + COALESCE(cm.match_weight, 0.0))::real * 0.04)) DESC,
    d.updated_at DESC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<DocumentProfileMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_text = query.Trim(),
                lexical_terms = lexicalTerms.ToArray(),
                category,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, cancellationToken: ct))).ToList();

            return rows.Select(row => new RagMatch(
                Score: NormalizeDocumentProfileScore(row.SparseRank, (int)Math.Min(row.MatchCount, int.MaxValue)),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                PageStart: null,
                PageEnd: null,
                ChunkId: row.ProfileId.ToString(),
                ChunkIndex: -1,
                Text: BuildDocumentProfileMatchText(row.Text, row.MetadataJson, query, row.Language),
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.SearchText,
                EmbeddingBasis: "document_profile_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: "Document profile",
                HeadingPath: "Document profile",
                ChunkType: "document_profile",
                PrevChunkId: null,
                NextChunkId: null,
                SameSectionChunkId: null)).ToList();
        }
        catch (PostgresException)
        {
            return [];
        }
    }

    private static List<SparseMatchRow> MergeSparseRows(
        IReadOnlyList<SparseMatchRow> preferred,
        IReadOnlyList<SparseMatchRow> secondary,
        int topK)
    {
        var merged = new List<SparseMatchRow>(Math.Max(0, topK));
        var seen = new HashSet<Guid>();

        foreach (var row in preferred.Concat(secondary))
        {
            if (!seen.Add(row.ChunkId))
                continue;

            merged.Add(row);
            if (merged.Count >= topK)
                break;
        }

        return merged;
    }

    private static string BuildDocumentProfileMatchText(string summaryText, string? metadataJson, string query, string? language)
    {
        var summary = string.IsNullOrWhiteSpace(summaryText)
            ? string.Empty
            : summaryText.Trim();
        var contentCards = DocumentProfileProjector.ParseContentCards(metadataJson);
        if (contentCards.Count == 0)
            return summary;

        var queryTokens = ExtractLexicalQueryTokens(query);
        var rankedCards = contentCards
            .Select(card => new
            {
                Card = card,
                Coverage = ComputeLexicalCoverage(queryTokens, $"{card.Title} {string.Join(' ', card.Signals)}")
            })
            .OrderByDescending(static item => item.Coverage)
            .ThenBy(static item => item.Card.PageStart ?? int.MaxValue)
            .ThenBy(static item => item.Card.Title, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(static item => FormatDocumentProfileContentCard(item.Card))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (rankedCards.Length == 0)
            return summary;

        var cardsText = $"{BuildContentCuesLabel(language)}: {string.Join("; ", rankedCards)}.";
        return string.IsNullOrWhiteSpace(summary)
            ? cardsText
            : $"{summary} {cardsText}";
    }

    private static string BuildContentCuesLabel(string? language)
        => (language ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "en" => "Content cues",
            "es" => "Pistas de contenido",
            "pt" => "Pistas de conteudo",
            "de" => "Inhaltshinweise",
            "it" => "Indizi di contenuto",
            _ => "Reperes de contenu"
        };

    private static string FormatDocumentProfileContentCard(DocumentProfileContentCard card)
    {
        var title = card.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return card.PageStart is > 0
            ? $"{title} (p.{card.PageStart})"
            : title;
    }

    internal static bool ShouldSupplementSparseWithLexicalFallback(string? category, string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return false;

        var scoped = !string.IsNullOrWhiteSpace(category);
        var hasStrongToken = tokens.Any(static token =>
            token.Length >= 7
            || token.Any(char.IsDigit)
            || IsReferenceLikeLookupTerm(token));

        if (scoped)
            return hasStrongToken || tokens.Count <= 4;

        return hasStrongToken && tokens.Count <= 4;
    }

    internal static bool ShouldBackfillEnumerativeSearch(string query, int selectedCount, int topK)
    {
        if (selectedCount >= topK || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsEnumerativeLookupIntent(normalized))
        {
            return BuildFocusedLexicalBackfillQuery(query).Length > 0;
        }

        return false;
    }

    private static bool ContainsEnumerativeLookupIntent(string normalized)
        => ContainsAny(normalized,
            " quels ",
            " quelles ",
            " lesquels ",
            " lesquelles ",
            " liste ",
            " lister ",
            " trouve ",
            " trouver ",
            " recherche ",
            " chercher ",
            " montre ",
            " afficher ",
            " existent ",
            " disponibles ",
            " qui parlent ",
            " qui mentionnent ",
            " qui contiennent ",
            " avec ",
            " which ",
            " what ",
            " list ",
            " find ",
            " search ",
            " show ",
            " available ",
            " contain ",
            " contains ",
            " mentioning ",
            " mentions ",
            " welche ",
            " finden ",
            " zeige ",
            " cuales ",
            " lista ",
            " encuentra ",
            " quais ",
            " elenco ",
            " elenca ",
            " trova ");

    internal static string BuildFocusedLexicalBackfillQuery(string query)
    {
        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var foldedLookup = FoldDiacritics(lookup);
        var phrase = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .OrderByDescending(term => PhraseOccursInQuery(term, lookup, foldedLookup))
            .ThenByDescending(ComputeFocusedBackfillTermScore)
            .ThenBy(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
            .ThenBy(static term => term.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(phrase))
            return phrase.Trim();

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(5)
            .ToArray();

        return tokens.Length == 0
            ? string.Empty
            : string.Join(' ', tokens);
    }

    private static bool PhraseOccursInQuery(string phrase, string lookup, string foldedLookup)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        return lookup.Contains(phrase, StringComparison.Ordinal)
            || foldedLookup.Contains(FoldDiacritics(phrase), StringComparison.Ordinal);
    }

    private static int ComputeFocusedBackfillTermScore(string term)
    {
        var parts = term
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .ToArray();
        if (parts.Length == 0)
            return 0;

        return (parts.Count(static token => token.Length >= 7) * 4)
            + Math.Min(3, parts.Length)
            + Math.Min(3, parts.Sum(static token => Math.Min(1, token.Count(char.IsDigit))));
    }

    private static async Task<List<RagMatch>> FilterMatchesAgainstActiveDocumentVersionsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> rawMatches,
        CancellationToken ct)
    {
        if (rawMatches.Count == 0)
            return [];

        var docIds = rawMatches
            .Select(m => Guid.TryParse(m.DocId, out var docId) ? docId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (docIds.Length == 0)
            return [];

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT d.doc_id AS "DocId",
       d.status AS "Status",
       d.indexed_version AS "IndexedVersion",
       r.ingestion_version AS "CurrentRevisionIngestionVersion",
       LOWER(ENCODE(d.content_hash, 'hex')) AS "ContentHashHex"
FROM documents d
LEFT JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
WHERE d.tenant_id=@tenant_id
  AND d.doc_id = ANY(@doc_ids);
""";

        var rows = await conn.QueryAsync<DocVersionRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = docIds
        }, cancellationToken: ct));

        var active = rows.ToDictionary(x => x.DocId, x => x);
        var filtered = new List<RagMatch>(rawMatches.Count);
        foreach (var match in rawMatches)
        {
            if (!Guid.TryParse(match.DocId, out var docIdValue))
                continue;
            if (!active.TryGetValue(docIdValue, out var row))
                continue;
            if (!string.Equals(row.Status, "indexed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.IndexedVersion <= 0)
                continue;

            if (!IsActiveDenseMatchForRevision(match, row.CurrentRevisionIngestionVersion, row.ContentHashHex))
                continue;

            filtered.Add(match);
        }

        return filtered;
    }

    internal static bool IsActiveDenseMatchForRevision(
        RagMatch match,
        int? currentRevisionIngestionVersion,
        string? currentContentHashHex)
    {
        var matchHash = NormalizeHashHex(match.HashDoc);
        var currentHash = NormalizeHashHex(currentContentHashHex);
        var hashCanBeChecked = !string.IsNullOrWhiteSpace(matchHash) && !string.IsNullOrWhiteSpace(currentHash);
        var hashMatches = hashCanBeChecked
            && string.Equals(matchHash, currentHash, StringComparison.OrdinalIgnoreCase);

        if (match.IngestionVersion.HasValue)
        {
            if (!currentRevisionIngestionVersion.HasValue)
                return false;
            if (match.IngestionVersion.Value != currentRevisionIngestionVersion.Value)
                return false;

            return !hashCanBeChecked || hashMatches;
        }

        return hashMatches;
    }

    private static string? NormalizeHashHex(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private sealed record DocVersionRow(
        Guid DocId,
        string Status,
        int IndexedVersion,
        int? CurrentRevisionIngestionVersion,
        string? ContentHashHex);
    private sealed record ExactMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ExactMatchEntryId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string MatchKind,
        string? SectionTitle,
        string? MatchedTerm);

    private sealed record MetadataReferenceRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int IngestionVersion,
        string? HashDoc);

    private sealed record TopCategoryOrderRow(
        string Path,
        int DisplayOrder);

    // Must match the SELECT column order/names/types in SearchSparseMatchesAsync exactly —
    // Dapper materialises records by positional constructor binding, so a missing column
    // (e.g. SectionOrdinalPlaceholder, which we don't use but must declare) or a type
    // mismatch (ts_rank_cd returns float/Single, not double) makes the whole endpoint
    // throw "no matching constructor" at runtime.
    private sealed record SparseMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ChunkId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string EmbedText,
        Guid? SectionOrdinalPlaceholder,
        string? SectionTitle,
        string? HeadingPath,
        string ChunkType,
        string? PrevChunkId,
        string? NextChunkId,
        string? SameSectionChunkId,
        float SparseRank);

    private sealed record DocumentProfileMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        Guid ProfileId,
        int IngestionVersion,
        string? HashDoc,
        string Text,
        string SearchText,
        string? MetadataJson,
        string? Language,
        double SparseRank,
        long MatchCount);

    private sealed record LinkedMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ChunkId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string ChunkType,
        string? SectionTitle,
        string? HeadingPath,
        string LinkType,
        Guid AnchorSourceId,
        Guid AnchorChunkId,
        string? PrevChunkId,
        string? NextChunkId,
        string? SameSectionChunkId);

    private static void AddRankedMatches(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        int maxPerSection = 2)
    {
        var perDoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectedMatch in selected)
        {
            if (string.IsNullOrWhiteSpace(selectedMatch.DocPath))
                continue;

            var docKey = selectedMatch.DocPath!;
            perDoc[docKey] = perDoc.TryGetValue(docKey, out var docCount) ? docCount + 1 : 1;
            var pageKey = $"{docKey}:{selectedMatch.PageStart ?? -1}:{selectedMatch.PageEnd ?? -1}";
            perPage[pageKey] = perPage.TryGetValue(pageKey, out var pageCount) ? pageCount + 1 : 1;
            var sectionKey = BuildSectionKey(selectedMatch);
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secCount) ? secCount + 1 : 1;
        }

        foreach (var match in matches)
        {
            if (selected.Count >= topK)
                break;
            if (match.Score < minScore)
                continue;
            if (string.IsNullOrWhiteSpace(match.DocPath))
                continue;

            var dedupKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(dedupKey))
                continue;

            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, match)))
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            if (LooksLikeNavigationalChunk(match)
                && selected.Any(static existing => !LooksLikeNavigationalChunk(existing)))
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            var docKey = match.DocPath!;
            perDoc.TryGetValue(docKey, out var docCountCurrent);
            if (docCountCurrent >= maxPerDoc)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            var pageKey = $"{docKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCountCurrent);
            if (pageCountCurrent >= maxPerPage)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

        // CDC v3.1 §11.3: max 2 chunks per section
            var sectionKey = BuildSectionKey(match);
            if (sectionKey != null)
            {
                perSection.TryGetValue(sectionKey, out var secCountCurrent);
                if (secCountCurrent >= maxPerSection)
                {
                    selectedKeys.Remove(dedupKey);
                    continue;
                }
            }

            selected.Add(match);
            perDoc[docKey] = docCountCurrent + 1;
            perPage[pageKey] = pageCountCurrent + 1;
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secUpdated) ? secUpdated + 1 : 1;
        }
    }

    private static string? BuildSectionKey(RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.DocPath))
            return null;
        // Use SectionOrdinal if available, fallback to SectionTitle
        if (match.SectionOrdinal.HasValue)
            return $"{match.DocPath}:sec:{match.SectionOrdinal.Value}";
        if (!string.IsNullOrWhiteSpace(match.SectionTitle))
            return $"{match.DocPath}:sec:{match.SectionTitle}";
        return null;
    }

    internal static async Task<List<RagMatch>> SearchLinkedMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> anchorMatches,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        if (topK <= 0)
            return [];

        var chunkAnchors = anchorMatches
            .Where(match =>
            {
                var retriever = ResolveRetriever(match);
                return (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                        || string.Equals(retriever, "linked_context", StringComparison.Ordinal)
                        || string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    && Guid.TryParse(match.ChunkId, out _);
            })
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: ResolveRetriever(match),
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var exactAnchors = anchorMatches
            .Where(match =>
                string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal)
                && Guid.TryParse(match.ChunkId, out _))
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: "exact_match",
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var anchorCandidates = chunkAnchors
            .Concat(exactAnchors)
            .OrderByDescending(item => item.AnchorScore)
            .Take(Math.Max(1, Math.Min(topK * 2, 4)))
            .ToList();

        if (anchorCandidates.Count == 0)
            return [];

        var anchorsBySourceId = anchorCandidates.ToDictionary(item => item.SourceId);
        var denseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "dense_qdrant", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var linkedAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var sparseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "sparse_bm25", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var exactAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "exact_match", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH requested_anchors AS (
    SELECT source_anchor_id, anchor_chunk_id, anchor_retriever
    FROM (
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'dense_qdrant'::text AS anchor_retriever
        FROM unnest(@dense_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'linked_context'::text AS anchor_retriever
        FROM unnest(@linked_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'sparse_bm25'::text AS anchor_retriever
        FROM unnest(@sparse_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT
            exact_anchors.exact_match_entry_id AS source_anchor_id,
            resolved_chunks.retrieval_chunk_id AS anchor_chunk_id,
            'exact_match'::text AS anchor_retriever
        FROM unnest(@exact_anchor_entry_ids::uuid[]) AS exact_anchor_id
        JOIN exact_match_entries exact_anchors
          ON exact_anchors.tenant_id = @tenant_id
         AND exact_anchors.exact_match_entry_id = exact_anchor_id
        JOIN LATERAL (
            SELECT rc.retrieval_chunk_id
            FROM retrieval_chunks rc
            WHERE rc.revision_id = exact_anchors.revision_id
              AND (
                    (exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id)
                 OR (exact_anchors.unit_id IS NULL
                     AND exact_anchors.section_id IS NOT NULL
                     AND rc.section_id = exact_anchors.section_id
                     AND rc.page_start <= exact_anchors.page_end
                     AND rc.page_end >= exact_anchors.page_start)
              )
            ORDER BY
                CASE
                    WHEN exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id THEN 0
                    ELSE 1
                END,
                rc.chunk_index ASC
            LIMIT 1
        ) AS resolved_chunks ON TRUE
    ) anchors
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    (rc.metadata->>'offsetStart')::int AS "OffsetStart",
    (rc.metadata->>'offsetEnd')::int AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(rc.metadata->>'chunkType', 'linked_context_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    l.link_type AS "LinkType",
    a.source_anchor_id AS "AnchorSourceId",
    l.retrieval_chunk_id AS "AnchorChunkId",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId"
FROM requested_anchors a
JOIN retrieval_chunk_links l
  ON l.tenant_id = @tenant_id
 AND l.retrieval_chunk_id = a.anchor_chunk_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = l.linked_chunk_id
JOIN document_revisions r
  ON r.revision_id = rc.revision_id
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.status = 'indexed'
 AND d.indexed_version = r.indexed_version
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE l.link_type
        WHEN 'same_section' THEN 0
        WHEN 'next' THEN 1
        ELSE 2
    END,
    rc.chunk_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<LinkedMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            dense_anchor_chunk_ids = denseAnchorIds,
            linked_anchor_chunk_ids = linkedAnchorIds,
            sparse_anchor_chunk_ids = sparseAnchorIds,
            exact_anchor_entry_ids = exactAnchorIds,
            category,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK * 3
        }, cancellationToken: ct));

        return rows.Select(row =>
        {
            anchorsBySourceId.TryGetValue(row.AnchorSourceId, out var anchor);
            return new RagMatch(
                Score: ComputeLinkedMatchScore(anchor?.AnchorScore ?? 0.0, row.LinkType, anchor?.SourceRetriever),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                OffsetStart: row.OffsetStart,
                OffsetEnd: row.OffsetEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.Text,
                EmbeddingBasis: "linked_context_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId);
        }).ToList();
    }

    /// <summary>
    /// CDC v3.1 §11.3: autocut - detect the largest relative score drop between consecutive
    /// results and trim everything after the gap, provided the absolute threshold is also met.
    /// Keeps at least 1 result. Only cuts if the gap is significant (>= 15% relative drop).
    /// </summary>
    internal static void ApplyAutocut(List<RagMatch> matches, double absoluteMinScore)
    {
        if (matches.Count <= 2)
            return;

        var bestGapIndex = -1;
        var bestGapRatio = 0.0;
        const double minRelativeDrop = 0.15;
        const int minKeepBeforeAutocut = 8;

        for (var i = 1; i < matches.Count; i++)
        {
            var prev = matches[i - 1].Score;
            var curr = matches[i].Score;
            if (prev <= 0.0)
                continue;

            var drop = (prev - curr) / prev;
            if (drop > bestGapRatio)
            {
                bestGapRatio = drop;
                bestGapIndex = i;
            }
        }

        if (bestGapRatio >= minRelativeDrop && bestGapIndex >= minKeepBeforeAutocut)
            matches.RemoveRange(bestGapIndex, matches.Count - bestGapIndex);

        // Also enforce absolute minimum on remaining items (skip exact_match which always passes)
        matches.RemoveAll(m =>
            m.Score < absoluteMinScore
            && !string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal));
    }

    internal static string BuildMatchDedupKey(RagMatch match)
        => $"{match.DocId}|{match.PageStart}|{match.PageEnd}|{ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty)}";

    internal static bool IsNearDuplicatePageOverlap(RagMatch left, RagMatch right)
    {
        if (left is null || right is null)
            return false;
        if (string.IsNullOrWhiteSpace(left.DocPath) || string.IsNullOrWhiteSpace(right.DocPath))
            return false;
        if (!string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!PageRangesOverlap(left.PageStart, left.PageEnd, right.PageStart, right.PageEnd))
            return false;

        var leftText = ExactMatchEntryExtractor.NormalizeForLookup(left.Text ?? string.Empty);
        var rightText = ExactMatchEntryExtractor.NormalizeForLookup(right.Text ?? string.Empty);
        if (leftText.Length < 120 || rightText.Length < 120)
            return false;

        if (leftText.Contains(rightText, StringComparison.Ordinal) || rightText.Contains(leftText, StringComparison.Ordinal))
            return true;

        return ComputeTokenJaccard(leftText, rightText) >= 0.82;
    }

    private static bool PageRangesOverlap(int? leftStart, int? leftEnd, int? rightStart, int? rightEnd)
    {
        if (leftStart is not > 0 || rightStart is not > 0)
            return false;

        var lEnd = Math.Max(leftStart.Value, leftEnd ?? leftStart.Value);
        var rEnd = Math.Max(rightStart.Value, rightEnd ?? rightStart.Value);
        return leftStart.Value <= rEnd && rightStart.Value <= lEnd;
    }

    private static double ComputeTokenJaccard(string leftText, string rightText)
    {
        var leftTokens = leftText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = rightText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0.0;

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union <= 0 ? 0.0 : (double)intersection / union;
    }

    internal static List<RagMatch> FuseWithRrf(
        IReadOnlyList<RagMatch> exactMatches,
        IReadOnlyList<RagMatch> sparseMatches,
        IReadOnlyList<RagMatch> denseMatches,
        IReadOnlyList<RagMatch>? profileMatches = null,
        int rrfK = 60)
    {
        var accumulators = new Dictionary<string, RrfAccumulator>(StringComparer.OrdinalIgnoreCase);

        AccumulateRrf(accumulators, exactMatches, rrfK);
        AccumulateRrf(accumulators, sparseMatches, rrfK);
        AccumulateRrf(accumulators, denseMatches, rrfK);
        if (profileMatches is { Count: > 0 })
            AccumulateRrf(accumulators, profileMatches, rrfK);

        if (accumulators.Count == 0)
            return [];

        var maxRrf = accumulators.Values.Max(item => item.Score);
        return accumulators
            .Values
            .Select(item =>
            {
                var normalizedRrf = maxRrf > 0 ? item.Score / maxRrf : 0.0;
                var baseScore = Math.Clamp(item.Representative.Score, 0.0, 1.02);
                var blendedScore = Math.Min(1.02, (normalizedRrf * 0.65) + (baseScore * 0.35));
                return item.Representative with { Score = blendedScore };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ChunkIndex)
            .ToList();
    }

    internal static List<RagMatch> CalibrateFusedMatches(string query, IReadOnlyList<RagMatch> candidates, string? originalQuery = null)
    {
        if (candidates.Count <= 1)
            return candidates.ToList();

        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var referenceTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query)
            .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lexicalTokens = ExtractLexicalQueryTokens(query);
        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(16)
            .ToArray();
        var quotedPhrases = ExtractQuotedLookupPhrases(originalQuery ?? query);
        var comparativeSubjectTokens = ExtractComparativeSubjectAnchorTokens(originalQuery ?? query);
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            originalQuery ?? query,
            lexicalTokens,
            comparativeSubjectTokens);
        var hasLexicalAnchor = lexicalTokens.Count >= 1
            && candidates.Any(match =>
            {
                var retriever = ResolveRetriever(match);
                if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                    || string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                    return false;

                return match.Score >= 0.70
                    && ComputeLexicalCoverage(lexicalTokens, GetLexicalSignalText(match)) >= 0.5;
            });

        return candidates
            .Select(match =>
            {
                var retriever = ResolveRetriever(match);
                var adjusted = match.Score;
                var textForSignals = GetLexicalSignalText(match);
                var titleSignalText = GetTitleSignalText(match);
                var lexicalCoverage = lexicalTokens.Count > 0
                    ? ComputeLexicalCoverage(lexicalTokens, textForSignals)
                    : 0.0;
                var exactTitleScore = ComputeExactTitleCandidateScore(originalQuery ?? query, match);
                var quotedLookupScore = quotedPhrases.Count > 0
                    ? ComputeQuotedLookupCandidateScore(
                        quotedPhrases,
                        string.Join("\n", new[]
                        {
                            textForSignals,
                            match.Text,
                            match.SectionTitle,
                            match.HeadingPath
                        }.Where(static value => !string.IsNullOrWhiteSpace(value))))
                    : 0.0;
                var specificAnchorCount = CountSpecificLexicalAnchors(lexicalTokens, textForSignals);
                var requiresComparativeSubjectAnchor = comparativeSubjectTokens.Count > 0;
                var requiresPrimarySpecificAnchor = !requiresComparativeSubjectAnchor
                    && RequiresPrimarySpecificLexicalAnchor(lexicalTokens);
                var containsPrimarySpecificAnchor = !requiresPrimarySpecificAnchor
                    || ContainsPrimarySpecificLexicalAnchor(lexicalTokens, textForSignals);
                var containsComparativeSubjectAnchor = !requiresComparativeSubjectAnchor
                    || ContainsComparativeSubjectAnchor(comparativeSubjectTokens, textForSignals);
                var hasClosePhraseMatch = phraseTerms.Length > 0
                    && phraseTerms.Any(term => ContainsOrderedPhraseWindow(textForSignals, term, maxGapChars: 40));

                if (referenceTerms.Length > 0)
                {
                    if (string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                    {
                        adjusted += string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal)
                            ? 0.03
                            : 0.02;
                    }
                    else if (!string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted -= 0.01;
                    }
                }
                else if (lexicalTokens.Count > 0)
                {
                    if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.06,
                            >= 0.50 => 0.035,
                            >= 0.34 => 0.015,
                            _ when lexicalTokens.Count >= 4 => -0.08,
                            _ when lexicalTokens.Count >= 3 => -0.04,
                            _ => 0.0
                        };
                    }
                    else if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.025,
                            >= 0.50 => 0.012,
                            < 0.20 when lexicalTokens.Count >= 2 => -0.03,
                            _ => 0.0
                        };
                    }
                }

                if (exactTitleScore > 0.0)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => Math.Min(0.32, 0.12 + (exactTitleScore * 0.006)),
                        "dense_qdrant" => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004)),
                        "document_profile" => Math.Min(0.20, 0.08 + (exactTitleScore * 0.004)),
                        "exact_match" => Math.Min(0.18, 0.08 + (exactTitleScore * 0.004)),
                        _ => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004))
                    };

                    if (LooksLikeStructuredAnswerUnit(match))
                        adjusted += 0.06;
                }

                if (hasClosePhraseMatch)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.25,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.06
                    };
                }

                if (LooksLikeStructuredAnswerUnit(match)
                    && specificAnchorCount > 0
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.80))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.16,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (specificAnchorCount >= 2 && LooksLikeStructuredAnswerChunk(match))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.18,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (HasProfileTitleHint(match)
                    && requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor)
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.14,
                        "dense_qdrant" => 0.08,
                        "document_profile" => 0.06,
                        _ => 0.07
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.80 : 0.78);
                }

                if (requiresComparativeSubjectAnchor
                    && !containsComparativeSubjectAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.13,
                        "document_profile" => 0.10,
                        _ => 0.12
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }

                if (HasProfileTitleHint(match)
                    && specificAnchorCount >= 2
                    && containsPrimarySpecificAnchor
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.50))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.28,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                }

                if (quotedLookupScore > 0.0)
                    adjusted += Math.Min(0.24, quotedLookupScore * 0.015);

                if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                    && hasLexicalAnchor
                    && lexicalCoverage < 0.20)
                    adjusted = Math.Min(adjusted - 0.15, 0.24);

                if (LooksLikeSourceListChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.26 : 0.16;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }
                else if (LooksLikeDocumentOverviewChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.18 : 0.10;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (LooksLikeNavigationalChunk(match))
                {
                    var strongNavigation = LooksLikeStrongNavigationalChunk(match);
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" when strongNavigation => 0.32,
                        "sparse_bm25" => 0.20,
                        _ when strongNavigation => 0.18,
                        _ => 0.11
                    };
                    adjusted = Math.Min(
                        adjusted,
                        strongNavigation
                            ? (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.68 : 0.66)
                            : (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.82 : 0.78));
                }
                else if (!ShouldAllowGlossaryResults(originalQuery ?? query) && LooksLikeGlossaryChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.22 : 0.14;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.76 : 0.72);
                }

                return new
                {
                    Match = match with { Score = Math.Clamp(adjusted, 0.0, 1.02) },
                    ExactTitleScore = exactTitleScore,
                    DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(originalQuery ?? query, match),
                    QuotedLookupScore = quotedLookupScore,
                    LexicalCoverage = lexicalCoverage,
                    SpecificAnchorCount = specificAnchorCount,
                    StructuredAnswerPriority = GetStructuredAnswerPriority(match)
                };
            })
            .OrderByDescending(item => item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(item => item.ExactTitleScore)
            .ThenByDescending(item => item.Match.Score)
            .ThenByDescending(static item => item.SpecificAnchorCount)
            .ThenByDescending(static item => item.LexicalCoverage)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(item => item.QuotedLookupScore)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .ToList();
    }

    internal static bool ShouldSuppressUnanchoredSpecificResults(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates.Count == 0)
            return false;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (lexicalTokens.Length == 0 || lexicalTokens.Length > 2)
            return false;
        if (!lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken))
            return false;

        return !candidates.Any(match => HasSpecificQueryAnchor(lexicalTokens, match));
    }

    private static bool HasSpecificQueryAnchor(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        var signalText = GetLexicalSignalText(match);
        return ContainsPrimarySpecificLexicalAnchor(lexicalTokens, signalText)
            || CountSpecificLexicalAnchors(lexicalTokens, signalText) > 0
            || ComputeLexicalCoverage(lexicalTokens, signalText) >= 0.50;
    }

    internal static List<RagMatch> SuppressNavigationalNoise(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (candidates.Count <= 1 || ShouldAllowNavigationalResults(query))
            return candidates.ToList();

        var allowGlossary = ShouldAllowGlossaryResults(query);
        var hasContentCandidate = candidates.Any(match => !LooksLikeNavigationalChunk(match)
            && (allowGlossary || !LooksLikeGlossaryChunk(match)));
        if (!hasContentCandidate)
            return candidates.ToList();

        return candidates
            .Where(match => !LooksLikeNavigationalChunk(match))
            .Where(match => allowGlossary || !LooksLikeGlossaryChunk(match))
            .ToList();
    }

    internal static void PruneNavigationalSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || ShouldAllowNavigationalResults(query))
            return;

        var allowGlossary = ShouldAllowGlossaryResults(query);
        var hasContentCandidate = selected.Any(match => !LooksLikeNavigationalChunk(match)
            && (allowGlossary || !LooksLikeGlossaryChunk(match)));
        if (!hasContentCandidate)
            return;

        selected.RemoveAll(match => LooksLikeNavigationalChunk(match)
            || (!allowGlossary && LooksLikeGlossaryChunk(match)));
    }

    internal static void PrioritizeQuotedTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return;

        var ranked = selected
            .Select(match => new
            {
                Match = match,
                QuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedPhrases,
                    string.Join("\n", new[]
                    {
                        match.EmbedText,
                        match.Text,
                        match.SectionTitle,
                        match.HeadingPath
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))))
            })
            .ToList();

        if (!ranked.Any(static item => item.QuotedScore > 0.0))
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.QuotedScore)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }

    internal static void PrioritizeExactTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            query,
            lexicalTokens,
            ExtractComparativeSubjectAnchorTokens(query));
        var ranked = selected
            .Select(match => new
            {
                Match = match,
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                SpecificAnchorCount = lexicalTokens.Length > 0
                    ? CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match))
                    : 0,
                StructuredAnswerPriority = GetStructuredAnswerPriority(match)
            })
            .ToList();

        var hasExactTitleSignal = ranked.Any(static item => item.ExactTitleScore > 0.0);
        var hasFullSpecificCoverage = useSpecificCoverageTitlePriority
            && ranked.Any(static item => item.SpecificAnchorCount >= 2);
        if (!hasExactTitleSignal && !hasFullSpecificCoverage)
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }

    internal static void PruneWeakTitleExpansionSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var useTitlePriority = ExtractQuotedLookupPhrases(query).Count > 0
            || ShouldApplySpecificCoverageTitlePriority(
                query,
                lexicalTokens,
                ExtractComparativeSubjectAnchorTokens(query));
        if (!useTitlePriority)
            return;

        var ranked = selected
            .Select(match => new TitleSelectionSignal(
                match,
                ComputeExactTitleCandidateScore(query, match),
                CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)),
                HasFullTitleAnchorCoverage(lexicalTokens, match),
                GetStructuredAnswerPriority(match)))
            .ToList();

        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var strongAnchors = ranked
            .Where(item => item.SpecificAnchorCount >= requiredAnchorCount
                && item.FullTitleCoverage
                && item.StructuredAnswerPriority >= 2)
            .ToArray();
        if (strongAnchors.Length == 0)
        {
            var first = ranked.FirstOrDefault();
            if (first is null
                || first.StructuredAnswerPriority < 2
                || first.SpecificAnchorCount <= 0
                || first.Match.Score < 0.90)
            {
                return;
            }

            strongAnchors = [first];
        }

        var pruned = ranked
            .Where(item => !ShouldPruneWeakTitleExpansion(item, strongAnchors))
            .Select(static item => item.Match)
            .ToList();

        if (pruned.Count == selected.Count || pruned.Count == 0)
            return;

        selected.Clear();
        selected.AddRange(pruned);
    }

    private static bool ShouldPruneWeakTitleExpansion(
        TitleSelectionSignal item,
        IReadOnlyList<TitleSelectionSignal> strongAnchors)
    {
        if (item.FullTitleCoverage)
            return false;

        var retriever = ResolveRetriever(item.Match);
        if (string.Equals(retriever, "linked_context", StringComparison.Ordinal))
        {
            return !strongAnchors.Any(anchor =>
                string.Equals(anchor.Match.DocPath, item.Match.DocPath, StringComparison.OrdinalIgnoreCase)
                && IsNearbyPage(anchor.Match, item.Match, maxDistance: 2)
                && !LooksLikeNavigationalChunk(item.Match)
                && !LooksLikeGlossaryChunk(item.Match)
                && !IsNearDuplicatePageOverlap(anchor.Match, item.Match));
        }

        foreach (var anchor in strongAnchors)
        {
            if (!string.Equals(anchor.Match.DocPath, item.Match.DocPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (SharesSpecificSection(anchor.Match, item.Match))
                return false;
            if (IsNearbyPage(anchor.Match, item.Match, maxDistance: 2))
                return true;
        }

        return false;
    }

    private static bool HasFullTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        return ContainsOrderedTitleTokenSubstrings(titleSignalText, lexicalTokens, maxGapChars: 100)
            || ContainsTitleLikeLexicalSequence(
                FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText)),
                lexicalTokens);
    }

    private static bool IsNearbyPage(RagMatch anchor, RagMatch candidate, int maxDistance)
    {
        if (!anchor.PageStart.HasValue || !candidate.PageStart.HasValue)
            return false;

        return Math.Abs(anchor.PageStart.Value - candidate.PageStart.Value) <= maxDistance;
    }

    private static bool SharesSpecificSection(RagMatch left, RagMatch right)
    {
        if (!string.IsNullOrWhiteSpace(left.SectionTitle)
            && !string.IsNullOrWhiteSpace(right.SectionTitle)
            && !IsGenericSectionTitle(left.SectionTitle)
            && string.Equals(left.SectionTitle.Trim(), right.SectionTitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.HeadingPath)
            && !string.IsNullOrWhiteSpace(right.HeadingPath)
            && !IsGenericSectionTitle(left.HeadingPath)
            && string.Equals(left.HeadingPath.Trim(), right.HeadingPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsGenericSectionTitle(string? value)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value ?? string.Empty));
        return string.IsNullOrWhiteSpace(normalized)
            || normalized is "document" or "documents" or "root" or "page" or "pages" or "untitled" or "sans titre";
    }

    internal static void PruneWeakAdjacentSiblingSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var anchor = selected.FirstOrDefault(match =>
            match.Score >= 0.90
            && LooksLikeStructuredAnswerChunk(match)
            && CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)) > 0);
        if (anchor is null)
            return;

        selected.RemoveAll(match =>
        {
            if (string.Equals(match.ChunkId, anchor.ChunkId, StringComparison.Ordinal)
                && string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (SharesSpecificSection(anchor, match))
                return false;
            if (!IsNearbyPage(anchor, match, maxDistance: 2))
                return false;
            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && !LooksLikeNavigationalChunk(match)
                && !LooksLikeGlossaryChunk(match)
                && !IsNearDuplicatePageOverlap(anchor, match))
            {
                return false;
            }

            var coverage = ComputeLexicalCoverage(lexicalTokens, GetTitleSignalText(match));
            return coverage < 0.80;
        });
    }

    internal static void PrunePreciseTitleTailSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || !ShouldConstrainPreciseTitleLookup(query))
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var ranked = selected
            .Select(match => new
            {
                Match = match,
                StrictCoverage = HasStrictTitleAnchorCoverage(lexicalTokens, match),
                TitleAnchorCount = CountTitleAnchorTokens(lexicalTokens, GetPreciseTitleSignalText(match)),
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                StructuredAnswerPriority = GetStructuredAnswerPriority(match)
            })
            .ToList();
        var anchor = ranked
            .Where(item => item.Match.Score >= 0.90
                && item.TitleAnchorCount >= requiredAnchorCount
                && (item.StrictCoverage
                    || item.ExactTitleScore > 0.0
                    || item.StructuredAnswerPriority >= 2
                    || HasProfileTitleHint(item.Match)))
            .OrderByDescending(static item => item.StrictCoverage)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(static item => item.Match.Score)
            .Select(static item => item.Match)
            .FirstOrDefault();
        if (anchor is null)
            return;

        var anchorIsContentChunk = GetStructuredAnswerPriority(anchor) >= 2;
        selected.RemoveAll(match =>
        {
            if (IsSameChunk(match, anchor))
                return false;

            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase)
                && IsNearbyPage(anchor, match, maxDistance: 2)
                && !LooksLikeNavigationalChunk(match)
                && !LooksLikeGlossaryChunk(match)
                && !IsNearDuplicatePageOverlap(anchor, match))
            {
                return false;
            }

            return !HasStrictTitleAnchorCoverage(lexicalTokens, match)
                || (anchorIsContentChunk && string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal));
        });
    }

    internal static void PruneUnmatchedPreciseTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || !ShouldConstrainPreciseTitleLookup(query))
            return;

        var primaryTokens = ExtractTitlePruneTokens(query)
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (primaryTokens.Length == 0)
            return;

        var primaryAnchors = selected
            .Where(match => ContainsPrimarySpecificSelectionAnchor(primaryTokens, match))
            .ToArray();
        if (primaryAnchors.Length == 0)
        {
            selected.Clear();
            return;
        }

        selected.RemoveAll(match =>
        {
            if (ContainsPrimarySpecificSelectionAnchor(primaryTokens, match))
                return false;

            return !IsUsefulLinkedSelectionNearPrimaryAnchor(match, primaryAnchors);
        });
    }

    private static bool ContainsPrimarySpecificSelectionAnchor(IReadOnlyList<string> primaryTokens, RagMatch match)
    {
        if (primaryTokens.Count == 0)
            return true;

        return ContainsPrimarySpecificLexicalAnchor(primaryTokens, GetPreciseTitleSignalText(match));
    }

    private static bool IsUsefulLinkedSelectionNearPrimaryAnchor(RagMatch match, IReadOnlyList<RagMatch> primaryAnchors)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        return primaryAnchors.Any(anchor =>
            string.Equals(anchor.DocPath, match.DocPath, StringComparison.OrdinalIgnoreCase)
            && IsNearbyPage(anchor, match, maxDistance: 2)
            && !IsNearDuplicatePageOverlap(anchor, match));
    }

    private static bool IsSameChunk(RagMatch left, RagMatch right)
        => string.Equals(left.ChunkId, right.ChunkId, StringComparison.Ordinal)
           && string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);

    private static int CountTitleAnchorTokens(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 4
                    && normalized.Contains(foldedVariant, StringComparison.Ordinal))
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }

    private static bool HasStrictTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetPreciseTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsTitleLikeLexicalSequence(normalized, lexicalTokens);
    }

    private static string GetPreciseTitleSignalText(RagMatch match)
    {
        return string.Join("\n", new[]
        {
            ExtractMatchedProfileTitle(match.EmbedText),
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? ExtractMatchedProfileTitle(string? embedText)
    {
        if (string.IsNullOrWhiteSpace(embedText))
            return null;

        const string profilePrefix = "Matched profile title:";
        const string quotedPrefix = "Matched quoted title:";
        var prefix = embedText.StartsWith(profilePrefix, StringComparison.Ordinal)
            ? profilePrefix
            : embedText.StartsWith(quotedPrefix, StringComparison.Ordinal)
                ? quotedPrefix
                : null;
        if (prefix is null)
            return null;

        var start = prefix.Length;
        var lineEnd = embedText.IndexOf('\n', start);
        if (lineEnd < 0)
        {
            var documentMarker = embedText.IndexOf(" Document:", start, StringComparison.Ordinal);
            lineEnd = documentMarker >= 0 ? documentMarker : embedText.Length;
        }

        return embedText[start..lineEnd].Trim();
    }

    private static string[] ExtractTitlePruneTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => (token.Length >= 4 && token.Any(char.IsLetter)) || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static bool ShouldAllowNavigationalResults(string query)
    {
        var normalized = $" {NormalizeQuery(FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))).ToLowerInvariant()} ";
        return ContainsAny(normalized,
            " index ",
            " sommaire ",
            " table des matieres ",
            " table of contents ",
            " inventaire ",
            " corpus ",
            " vue d ensemble ",
            " overview ",
            " quels livres ",
            " quelles sources ",
            " quels documents ",
            " liste des recettes ",
            " list recipes ",
            " recipe list ");
    }

    internal static bool ContainsOrderedPhraseWindow(string? candidateText, string phrase, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase));
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        var tokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 2)
            return false;

        var searchFrom = 0;
        var previousEnd = -1;
        foreach (var token in tokens)
        {
            var index = normalizedCandidate.IndexOf(token, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (previousEnd >= 0 && index - previousEnd > maxGapChars)
                return false;

            previousEnd = index + token.Length;
            searchFrom = previousEnd;
        }

        return true;
    }

    private static string GetLexicalSignalText(RagMatch match)
        => string.IsNullOrWhiteSpace(match.EmbedText)
            ? match.Text ?? string.Empty
            : match.EmbedText!;

    private static string GetTitleSignalText(RagMatch match)
    {
        var includeEmbedText = string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal)
            || HasProfileTitleHint(match);
        return string.Join("\n", new[]
        {
            includeEmbedText ? match.EmbedText : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    internal static bool HasProfileTitleHint(RagMatch match)
        => !string.IsNullOrWhiteSpace(match.EmbedText)
            && (match.EmbedText!.StartsWith("Matched profile title:", StringComparison.Ordinal)
                || match.EmbedText!.StartsWith("Matched quoted title:", StringComparison.Ordinal));

    internal static bool ContainsSpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
        => CountSpecificLexicalAnchors(lexicalTokens, candidateText) > 0;

    internal static bool RequiresPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens)
        => lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken);

    internal static bool ContainsPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return false;

        var primaryTokens = lexicalTokens
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (primaryTokens.Length == 0)
            return true;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        foreach (var token in primaryTokens)
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 4 && normalized.Contains(foldedVariant, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ExtractComparativeSubjectAnchorTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var markerIndex = Array.FindIndex(tokens, IsComparativeCriterionMarker);
        if (markerIndex <= 0)
            return Array.Empty<string>();

        var start = Math.Max(0, markerIndex - 5);
        return tokens
            .Skip(start)
            .Take(markerIndex - start)
            .Where(IsComparativeSubjectAnchorToken)
            .TakeLast(2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool ContainsComparativeSubjectAnchor(IReadOnlyList<string> subjectTokens, string? candidateText)
    {
        if (subjectTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(candidateText))
            return false;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var token in subjectTokens)
        {
            var variants = BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Distinct(StringComparer.Ordinal);
            if (variants.Any(variant => normalizedCandidate.Contains(FoldDiacritics(variant), StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static bool IsComparativeCriterionMarker(string token)
        => token is "plus" or "most" or "mas" or "mais" or "mehr";

    private static bool IsComparativeSubjectAnchorToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !LexicalStopwords.Contains(token)
           && !ComparativeSubjectStopwords.Contains(token);

    private static bool IsPrimarySpecificLexicalAnchorToken(string token)
        => !SpecificAnchorStopwords.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token)
            && (token.Length >= 8 || token.Any(char.IsDigit) || IsReferenceLikeLookupTerm(token));

    internal static double ComputeExactTitleCandidateScore(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.0;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return 0.0;

        var titleTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        if (titleTokens.Length < 2 || titleTokens.Length > 8)
            return 0.0;

        var candidateText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var normalizedQuery = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        var normalizedChunkText = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty));
        var score = ComputeRawOrderedTitleScore(query, candidateText);
        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 18.0;
        }

        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 14.0;
        }

        var orderedPhraseMatch = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(16)
            .Any(term => ContainsOrderedPhraseWindow(candidateText, term, maxGapChars: 40));
        if (orderedPhraseMatch)
            score += 10.0;

        if (ContainsOrderedTitleTokenSubstrings(candidateText, titleTokens, maxGapChars: 60))
            score += 14.0;

        if (ContainsTitleLikeLexicalSequence(normalizedCandidate, titleTokens))
            score += 12.0;

        if (ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens))
            score += 10.0;

        if (score <= 0.0)
            return 0.0;

        if (normalizedCandidate.StartsWith(normalizedQuery, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith("matched profile title " + normalizedQuery, StringComparison.Ordinal))
        {
            score += 4.0;
        }

        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            score += 8.0;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            score += 1.0;
        else if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            score += 1.0;

        return score;
    }

    private static int ComputeDirectChunkTitleSignal(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(match.Text))
            return 0;

        var titleTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        if (titleTokens.Length < 2 || titleTokens.Length > 8)
            return 0;

        var normalizedQuery = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        var normalizedChunkText = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(match.Text));
        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 3;
        }

        if (ContainsOrderedTitleTokenSubstrings(match.Text, titleTokens, maxGapChars: 60))
            return 2;

        return ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens) ? 1 : 0;
    }

    private static bool ContainsTitleLikeLexicalSequence(string normalizedCandidate, IReadOnlyList<string> titleTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || titleTokens.Count < 2)
            return false;

        var candidateTokens = normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 0)
            .Take(1500)
            .ToArray();
        if (candidateTokens.Length < titleTokens.Count)
            return false;

        var tokenVariants = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Where(static variant => variant.Length >= 4)
                .DefaultIfEmpty(FoldDiacritics(token))
                .ToHashSet(StringComparer.Ordinal))
            .ToArray();

        for (var start = 0; start < candidateTokens.Length; start++)
        {
            if (!TokenMatchesTitleVariant(candidateTokens[start], tokenVariants[0]))
                continue;

            var current = start;
            var matchedAll = true;
            for (var expected = 1; expected < tokenVariants.Length; expected++)
            {
                var next = FindNextTitleToken(candidateTokens, current + 1, tokenVariants[expected]);
                if (next < 0)
                {
                    matchedAll = false;
                    break;
                }

                for (var i = current + 1; i < next; i++)
                {
                    if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                    {
                        matchedAll = false;
                        break;
                    }
                }

                if (!matchedAll)
                    break;

                current = next;
            }

            if (matchedAll)
                return true;
        }

        return false;
    }

    private static int FindNextTitleToken(IReadOnlyList<string> candidateTokens, int startIndex, IReadOnlySet<string> expectedVariants)
    {
        var maxExclusive = Math.Min(candidateTokens.Count, startIndex + 6);
        for (var i = startIndex; i < maxExclusive; i++)
        {
            if (TokenMatchesTitleVariant(candidateTokens[i], expectedVariants))
                return i;
            if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                break;
        }

        return -1;
    }

    private static bool TokenMatchesTitleVariant(string candidateToken, IReadOnlySet<string> expectedVariants)
    {
        if (expectedVariants.Contains(candidateToken))
            return true;

        foreach (var variant in expectedVariants)
        {
            if (variant.Length < 4
                || candidateToken.Length <= variant.Length
                || !candidateToken.StartsWith(variant, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = candidateToken[variant.Length..];
            if (TitleConnectorTokens.Contains(suffix)
                || suffix.All(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOrderedTitleTokenSubstrings(string candidateText, IReadOnlyList<string> titleTokens, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || titleTokens.Count < 2)
            return false;

        var normalizedCandidate = FoldDiacritics(candidateText).ToLowerInvariant();
        var variantGroups = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Append(FoldDiacritics(token))
                .Where(static variant => variant.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        if (variantGroups.Any(static group => group.Length == 0))
            return false;

        return TryFindOrderedTitleTokenSubstrings(
            normalizedCandidate,
            variantGroups,
            tokenIndex: 0,
            searchFrom: 0,
            previousEnd: -1,
            maxGapChars);
    }

    private static bool TryFindOrderedTitleTokenSubstrings(
        string normalizedCandidate,
        IReadOnlyList<string[]> variantGroups,
        int tokenIndex,
        int searchFrom,
        int previousEnd,
        int maxGapChars)
    {
        if (tokenIndex >= variantGroups.Count)
            return true;

        var attempts = 0;
        foreach (var occurrence in EnumerateTitleTokenOccurrences(normalizedCandidate, variantGroups[tokenIndex], searchFrom))
        {
            if (previousEnd >= 0 && occurrence.Index - previousEnd > maxGapChars)
                break;

            if (TryFindOrderedTitleTokenSubstrings(
                normalizedCandidate,
                variantGroups,
                tokenIndex + 1,
                occurrence.Index + occurrence.Length,
                occurrence.Index + occurrence.Length,
                maxGapChars))
            {
                return true;
            }

            attempts++;
            if (attempts >= 48)
                break;
        }

        return false;
    }

    private static IEnumerable<(int Index, int Length)> EnumerateTitleTokenOccurrences(
        string normalizedCandidate,
        IReadOnlyList<string> variants,
        int searchFrom)
    {
        var seen = new HashSet<int>();
        while (searchFrom < normalizedCandidate.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;
            foreach (var variant in variants)
            {
                var index = normalizedCandidate.IndexOf(variant, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                if (bestIndex < 0 || index < bestIndex || (index == bestIndex && variant.Length > bestLength))
                {
                    bestIndex = index;
                    bestLength = variant.Length;
                }
            }

            if (bestIndex < 0)
                yield break;

            if (seen.Add(bestIndex))
                yield return (bestIndex, bestLength);

            searchFrom = bestIndex + 1;
        }
    }

    private static double ComputeRawOrderedTitleScore(string query, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var queryTokens = System.Text.RegularExpressions.Regex
            .Split(FoldDiacritics(query).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
            .Select(static token => ExactMatchEntryExtractor.NormalizeForLookup(token))
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (queryTokens.Length < 2)
            return 0.0;

        return ContainsOrderedTitleTokenSubstrings(candidateText, queryTokens, maxGapChars: 80)
            ? 16.0 + Math.Min(4.0, queryTokens.Length)
            : 0.0;
    }

    private static bool ContainsExactTitleActionMarker(string query)
    {
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        return ContainsAny(
            normalized,
            " explique ",
            " expliquer ",
            " expliquez ",
            " explain ",
            " explains ",
            " summarize ",
            " summary ",
            " resume ",
            " resumer ",
            " resumee ",
            " decris ",
            " decrire ",
            " describe ",
            " detaille ",
            " detailler ",
            " pourquoi ",
            " why ",
            " comment ",
            " how ");
    }

    private static bool ShouldApplySpecificCoverageTitlePriority(
        string query,
        IReadOnlyList<string> lexicalTokens,
        IReadOnlyList<string> comparativeSubjectTokens)
    {
        if (lexicalTokens.Count is < 2 or > 6)
            return false;
        if (comparativeSubjectTokens.Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (IsRecommendationSelectionQuery(normalized))
            return false;

        return !ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparison ",
            " comparaison ",
            " difference ",
            " differences ",
            " differents ",
            " differentes ");
    }

    private static bool IsRecommendationSelectionQuery(string normalizedPaddedQuery)
        => ContainsAny(
                normalizedPaddedQuery,
                " quel ",
                " quelle ",
                " quelles ",
                " quels ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welche ")
            && ContainsAny(
                normalizedPaddedQuery,
                " choisir ",
                " choose ",
                " recommander ",
                " recommande ",
                " recommandes ",
                " recommendation ",
                " recommendations ",
                " adaptee ",
                " adaptees ",
                " adapte ",
                " adaptes ",
                " suitable ",
                " best ",
                " meilleur ",
                " meilleure ",
                " meilleurs ",
                " meilleures ");

    internal static int CountSpecificLexicalAnchors(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            if (SpecificAnchorStopwords.Contains(token))
                continue;
            if (token.Length < 6 && !token.Any(char.IsDigit))
                continue;

            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 6
                    && normalized.Contains(foldedVariant, StringComparison.Ordinal))
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }

    internal static bool LooksLikeStructuredAnswerUnit(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }

    internal static bool LooksLikeStructuredAnswerChunk(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal)
            && !string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }

    private static int GetStructuredAnswerPriority(RagMatch match)
    {
        if (LooksLikeStructuredAnswerUnit(match))
            return 3;
        if (LooksLikeStructuredAnswerChunk(match))
            return 2;
        if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            return 1;
        return 0;
    }

    private static bool LooksLikeStructuredAnswerText(string? value)
    {
        var text = FoldDiacritics(value ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("ingredient", StringComparison.Ordinal)
            || text.Contains("preparation", StringComparison.Ordinal)
            || text.Contains("procedure", StringComparison.Ordinal)
            || text.Contains("methode", StringComparison.Ordinal)
            || text.Contains("method", StringComparison.Ordinal)
            || text.Contains("instruction", StringComparison.Ordinal)
            || text.Contains("materiel", StringComparison.Ordinal)
            || text.Contains("material", StringComparison.Ordinal)
            || text.Contains("requirement", StringComparison.Ordinal)
            || text.Contains("warning", StringComparison.Ordinal)
            || text.Contains("caution", StringComparison.Ordinal)
            || text.Contains("servez avec", StringComparison.Ordinal)
            || text.Contains("servir avec", StringComparison.Ordinal)
            || text.Contains("serve with", StringComparison.Ordinal)
            || text.Contains("served with", StringComparison.Ordinal)
            || text.Contains("ajoutez", StringComparison.Ordinal)
            || text.Contains("mettez", StringComparison.Ordinal)
            || text.Contains("faites", StringComparison.Ordinal)
            || text.Contains("melangez", StringComparison.Ordinal)
            || text.Contains("versez", StringComparison.Ordinal)
            || text.Contains("laissez", StringComparison.Ordinal)
            || text.Contains("realisation", StringComparison.Ordinal)
            || LooksLikeStructuredQuantityList(value);
    }

    internal static bool LooksLikeStructuredQuantityList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var bulletCount = CountBulletMarkers(value);
        if (bulletCount < 3)
            return false;

        var measurementCount = System.Text.RegularExpressions.Regex.Matches(
            FoldDiacritics(value).ToLowerInvariant(),
            @"\b\d+(?:[\.,]\d+)?\s?(?:kg|g|mg|l|ml|cl|h|min|s|mm|cm|m|bar|v|a|w|kw|nm|%|deg|c)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        return measurementCount >= 2;
    }

    internal static bool LooksLikeSourceListChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var urlCount = CountOccurrences(lower, "http://")
            + CountOccurrences(lower, "https://")
            + CountOccurrences(lower, "www.");
        if (urlCount >= 3)
            return true;

        var domainMarkers = CountOccurrences(lower, ".com")
            + CountOccurrences(lower, ".org")
            + CountOccurrences(lower, ".net")
            + CountOccurrences(lower, ".fr")
            + CountOccurrences(lower, ".ca")
            + CountOccurrences(lower, ".ch");

        return domainMarkers >= 5 && CountInlinePageNumberBoundaries(text) <= 2;
    }

    internal static bool LooksLikeDocumentOverviewChunk(RagMatch match)
    {
        var text = FoldDiacritics(match.Text ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasOverviewLanguage =
            text.Contains("ce livre", StringComparison.Ordinal)
            || text.Contains("ce guide", StringComparison.Ordinal)
            || text.Contains("ce document", StringComparison.Ordinal)
            || text.Contains("cet ouvrage", StringComparison.Ordinal)
            || text.Contains("this book", StringComparison.Ordinal)
            || text.Contains("this guide", StringComparison.Ordinal)
            || text.Contains("this document", StringComparison.Ordinal)
            || text.Contains("contains examples", StringComparison.Ordinal)
            || text.Contains("contient quelques exemples", StringComparison.Ordinal)
            || text.Contains("vous permettra", StringComparison.Ordinal)
            || text.Contains("permettra de", StringComparison.Ordinal)
            || text.Contains("introduction", StringComparison.Ordinal);

        if (!hasOverviewLanguage)
            return false;

        return !text.Contains("preparation", StringComparison.Ordinal)
            && !text.Contains("procedure", StringComparison.Ordinal)
            && !text.Contains("ingredients", StringComparison.Ordinal)
            && !text.Contains("instructions", StringComparison.Ordinal);
    }

    internal static bool LooksLikeNavigationalChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var foldedText = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";
        var hasStrongIndexMarker = folded.Contains("index des recettes", StringComparison.Ordinal)
            || folded.Contains("recipe index", StringComparison.Ordinal)
            || folded.Contains("index of recipes", StringComparison.Ordinal);
        var hasStructuredRecipeBody = foldedText.Contains("ingredient", StringComparison.Ordinal)
            && (foldedText.Contains("preparation", StringComparison.Ordinal)
                || foldedText.Contains("cuisson", StringComparison.Ordinal)
                || foldedText.Contains("faites", StringComparison.Ordinal)
                || foldedText.Contains("faire", StringComparison.Ordinal));
        if (hasStructuredRecipeBody && !hasStrongIndexMarker)
            return false;

        if (folded.Contains("sommaire", StringComparison.Ordinal)
            || folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || hasStrongIndexMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || padded.Contains(" table des matieres ", StringComparison.Ordinal)
            || padded.Contains(" table of contents ", StringComparison.Ordinal)
            || padded.Contains(" contents ", StringComparison.Ordinal)
            || padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (!hasStrongIndexMarker
                && padded.Contains(" index ", StringComparison.Ordinal)
                && CountBulletMarkers(text) < 8
                && CountInlinePageNumberBoundaries(text) < 5
                && !LooksLikeTitleListChunk(text))
                return false;

            return true;
        }

        if (CountInlinePageNumberBoundaries(text) >= 5)
            return true;

        return LooksLikeTitleListChunk(text);
    }

    internal static bool LooksLikeGlossaryChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var definitionHeadings = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"(?:^|[\r\n\s])[\p{Lu}][\p{Lu}\p{Mn}\p{Pd}\s]{2,42}\s*:",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        if (definitionHeadings >= 4)
            return true;

        var glossaryLanguage = ContainsAny(
            $" {NormalizeQuery(folded)} ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " definition ",
            " definitions ");
        if (!glossaryLanguage)
            return false;

        var shortDefinitionSignals = CountOccurrences(folded, " signifie ")
            + CountOccurrences(folded, " designe ")
            + CountOccurrences(folded, " means ")
            + CountOccurrences(folded, " refers to ")
            + CountOccurrences(folded, " consiste a ");
        return shortDefinitionSignals >= 2 || definitionHeadings >= 2;
    }

    internal static bool ShouldAllowGlossaryResults(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {NormalizeQuery(FoldDiacritics(query).ToLowerInvariant())} ";
        return ContainsAny(
            normalized,
            " definition ",
            " definir ",
            " definis ",
            " que veut dire ",
            " signifie ",
            " meaning ",
            " define ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " terme ",
            " term ");
    }

    internal static bool LooksLikeStrongNavigationalChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";

        return folded.Contains("index des recettes", StringComparison.Ordinal)
            || folded.Contains("recipe index", StringComparison.Ordinal)
            || folded.Contains("index of recipes", StringComparison.Ordinal)
            || folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || CountInlinePageNumberBoundaries(text) >= 8;
    }

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var digitRun = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digitRun++;
                continue;
            }

            if (digitRun is > 0 and <= 4 && char.IsLetter(ch) && char.IsUpper(ch))
                count++;

            digitRun = 0;
        }

        return count;
    }

    private static bool LooksLikeTitleListChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;

        var words = 0;
        var capitalizedStarts = 0;
        var sentenceMarkers = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is '.' or '!' or '?' or ';' or ':' or '\u2022')
                sentenceMarkers++;

            if (char.IsLetter(ch))
            {
                if (!inWord)
                {
                    words++;
                    if (char.IsUpper(ch))
                        capitalizedStarts++;
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return (words >= 24
                && capitalizedStarts >= Math.Max(12, words / 3)
                && sentenceMarkers <= 2)
            || (words >= 40
                && capitalizedStarts >= 20
                && sentenceMarkers <= 2)
            || (CountLowerToUpperTransitions(text) >= 8 && sentenceMarkers <= 3);
    }

    private static int CountLowerToUpperTransitions(string text)
    {
        var count = 0;
        var previousWasLower = false;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch) && previousWasLower)
                count++;

            previousWasLower = char.IsLower(ch);
        }

        return count;
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(needle))
            return 0;

        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0)
                break;

            count++;
            index = found + needle.Length;
        }

        return count;
    }

    internal static List<RagMatch> RerankDenseMatches(IReadOnlyList<RagMatch> matches)
        => matches
            .Select(match => new
            {
                Match = match,
                Score = match.Score + ComputeDenseBoost(match)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .Select(item => item.Match with { Score = item.Score })
            .ToList();

    internal static List<RagMatch> ApplyRerankScores(
        IReadOnlyList<RagMatch> originalCandidates,
        IReadOnlyList<TeiClient.RerankItem> rerankedItems,
        int rerankedPrefixCount)
    {
        if (originalCandidates.Count == 0 || rerankedItems.Count == 0 || rerankedPrefixCount <= 0)
            return originalCandidates.ToList();

        var limitedPrefixCount = Math.Min(rerankedPrefixCount, originalCandidates.Count);
        var prefix = originalCandidates.Take(limitedPrefixCount).ToArray();
        var suffix = originalCandidates.Skip(limitedPrefixCount).ToArray();
        var byIndex = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Score).First().Score);

        if (byIndex.Count == 0)
            return originalCandidates.ToList();

        var orderedScores = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .Select(item => item.Score)
            .OrderBy(score => score)
            .ToArray();
        var minScore = orderedScores.Length == 0 ? 0.0 : orderedScores.First();
        var maxScore = orderedScores.Length == 0 ? 0.0 : orderedScores.Last();

        var rerankedPrefix = byIndex
            .Select(pair =>
            {
                var match = prefix[pair.Key];
                var normalizedRerank = NormalizeRerankScore(pair.Value, minScore, maxScore);
                var blended = Math.Min(1.02, (match.Score * 0.35) + (normalizedRerank * 0.65));
                return match with
                {
                    Score = blended,
                    RerankScore = pair.Value
                };
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.RerankScore)
            .ToList();

        var untouchedPrefix = Enumerable.Range(0, prefix.Length)
            .Where(index => !byIndex.ContainsKey(index))
            .Select(index => prefix[index])
            .ToList();

        var finalList = new List<RagMatch>(originalCandidates.Count);
        finalList.AddRange(rerankedPrefix);
        finalList.AddRange(untouchedPrefix);
        finalList.AddRange(suffix);
        return finalList;
    }

    private static double ComputeDenseBoost(RagMatch match)
    {
        double boost = 0.0;
        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            boost += 0.02;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            boost += 0.01;

        if (!string.IsNullOrWhiteSpace(match.HeadingPath))
            boost += 0.005;
        if (string.Equals(match.EmbeddingBasis, "contextual_text_v1", StringComparison.Ordinal))
            boost += 0.005;

        return boost;
    }

    internal static double NormalizeRerankScore(double rawScore, double minScore, double maxScore)
    {
        if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;
        if (maxScore <= minScore)
            return 1.0;

        var normalized = (rawScore - minScore) / (maxScore - minScore);
        return Math.Clamp(normalized, 0.0, 1.0);
    }

    internal static double NormalizeSparseScore(double rawScore)
    {
        if (rawScore <= 0.0 || double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;

        var normalized = 0.45 + Math.Min(0.42, Math.Log10(1 + (rawScore * 1000.0)) * 0.24);
        return Math.Min(0.92, normalized);
    }

    internal static double NormalizeDocumentProfileScore(double sparseRank, int matchCount)
    {
        var lexicalScore = NormalizeSparseScore(sparseRank);
        var matchScore = matchCount <= 0 ? 0.0 : Math.Min(0.28, matchCount * 0.045);
        var combined = Math.Max(lexicalScore, 0.48 + matchScore);
        return Math.Clamp(combined, 0.0, 0.86);
    }

    internal static IReadOnlyList<string> ExtractLexicalQueryTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildLexicalContentFallbackTerms(string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return Array.Empty<string>();

        var terms = new HashSet<string>(StringComparer.Ordinal);
        var canonicalTokens = new List<string>();
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            foreach (var variant in variants)
                terms.Add(variant);

            var canonical = variants
                .OrderBy(static variant => ContainsNonAscii(variant) ? 1 : 0)
                .ThenBy(static variant => variant.Length)
                .ThenBy(static variant => variant, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(canonical))
                canonicalTokens.Add(canonical);
        }

        AddAdjacentPhraseTerms(terms, canonicalTokens);

        var surfaceTokens = ExtractLexicalQuerySurfaceTokens(query);
        if (surfaceTokens.Count > 0)
        {
            foreach (var token in surfaceTokens)
                terms.Add(token);

            AddAdjacentPhraseTerms(terms, surfaceTokens);
        }

        AddAdjacentAlphaNumericPhraseTerms(terms, query);

        if (terms.Contains("inertage") || terms.Contains("inerting"))
        {
            terms.Add("inerting");
            terms.Add("inertage");
        }

        foreach (var quotedPhrase in ExtractQuotedLookupPhrases(query))
        {
            terms.Add(quotedPhrase);
            var foldedPhrase = FoldDiacritics(quotedPhrase);
            if (!string.Equals(foldedPhrase, quotedPhrase, StringComparison.Ordinal))
                terms.Add(foldedPhrase);
        }

        var filteredTerms = terms
            .Where(static term => term.Length >= 4)
            .Where(static term => !LexicalStopwords.Contains(term))
            .ToArray();

        var phraseTerms = filteredTerms
            .Where(static term => term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);
        var singleTerms = filteredTerms
            .Where(static term => !term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);

        return phraseTerms
            .Concat(singleTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractQuotedLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var matches = System.Text.RegularExpressions.Regex.Matches(
            query,
            "[\u00ab\u201c\"](?<phrase>[^\u00bb\u201d\"]{3,80})[\u00bb\u201d\"]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (matches.Count == 0)
            return Array.Empty<string>();

        return matches
            .Select(match => ExactMatchEntryExtractor.NormalizeForLookup(match.Groups["phrase"].Value))
            .Where(static phrase => phrase.Length >= 4)
            .Where(static phrase => phrase.Count(static ch => ch == ' ') >= 1)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static double ComputeQuotedLookupCandidateScore(IReadOnlyList<string> quotedPhrases, string? candidateText)
    {
        if (quotedPhrases.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty);
        var bestScore = 0.0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(quotedPhrase));
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var tokens = normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsQuotedLookupSignalToken)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var compactPhrase = normalizedPhrase.Replace(" ", string.Empty);
            var hasExactPhrase = normalizedCandidate.Contains(normalizedPhrase, StringComparison.Ordinal)
                || (compactPhrase.Length >= 8 && compactCandidate.Contains(compactPhrase, StringComparison.Ordinal));

            var matched = tokens.Count(token => ContainsQuotedLookupToken(normalizedCandidate, compactCandidate, token));
            if (!hasExactPhrase && matched < Math.Min(tokens.Length, 2))
                continue;

            var coverage = tokens.Length == 0
                ? hasExactPhrase ? 1.0 : 0.0
                : matched / (double)tokens.Length;
            var score = (hasExactPhrase ? 12.0 : 0.0)
                + (coverage * 8.0)
                + Math.Min(3, matched) * 0.5;
            score += ComputeQuotedPhrasePlacementBonus(quotedPhrase, candidateText);

            if (tokens.Length >= 4 && matched >= tokens.Length - 1)
                score += 2.0;

            bestScore = Math.Max(bestScore, score);
        }

        return bestScore;
    }

    private static double ComputeQuotedPhrasePlacementBonus(string quotedPhrase, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(quotedPhrase) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var foldedCandidate = FoldDiacritics(candidateText);
        var foldedPhrase = FoldDiacritics(quotedPhrase);
        if (foldedPhrase.Length < 4)
            return 0.0;

        var best = 0.0;
        var index = foldedCandidate.IndexOf(foldedPhrase, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var slice = foldedCandidate.Substring(index, Math.Min(foldedPhrase.Length, foldedCandidate.Length - index));
            var letters = slice.Where(char.IsLetter).ToArray();
            var uppercaseRatio = letters.Length == 0
                ? 0.0
                : letters.Count(char.IsUpper) / (double)letters.Length;
            var prefix = foldedCandidate.Substring(Math.Max(0, index - 32), Math.Min(32, index));
            var placement = uppercaseRatio >= 0.70 ? 7.0 : 0.0;
            if (LooksLikeMeasuredIngredientLead(prefix))
                placement -= 4.0;

            best = Math.Max(best, placement);
            index = foldedCandidate.IndexOf(foldedPhrase, index + foldedPhrase.Length, StringComparison.OrdinalIgnoreCase);
        }

        return best;
    }

    private static bool LooksLikeMeasuredIngredientLead(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            prefix,
            @"(?:\d+[.,]?\d*|\b(?:g|kg|mg|ml|cl|l|litre|litres|tasse|tasses|cuillere|cuilleres|cuilleree|cuillerees|c\.|oz|lb)\b)\s*(?:de|d')?\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsQuotedLookupSignalToken(string token)
        => (token.Length >= 4 || token.Any(char.IsDigit))
           && !LexicalStopwords.Contains(token);

    private static bool ContainsQuotedLookupToken(string normalizedCandidate, string compactCandidate, string token)
    {
        if (token.All(char.IsDigit))
        {
            return normalizedCandidate
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(token, StringComparer.Ordinal);
        }

        return normalizedCandidate.Contains(token, StringComparison.Ordinal)
            || (token.Length >= 4 && compactCandidate.Contains(token, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ExtractLexicalQuerySurfaceTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(FoldDiacritics(token)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddAdjacentAlphaNumericPhraseTerms(HashSet<string> terms, string query)
    {
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)));
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, ExactMatchEntryExtractor.NormalizeForLookup(query));
    }

    private static void AddAdjacentAlphaNumericPhraseTermsFromNormalized(HashSet<string> terms, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return;

        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAlphaNumericPhraseToken)
            .Take(24)
            .ToArray();
        if (tokens.Length < 2)
            return;

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var phrase = $"{tokens[i]} {tokens[i + 1]}";
            if (IsAlphaNumericPhraseTerm(phrase))
                terms.Add(phrase);
        }
    }

    private static bool IsAlphaNumericPhraseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 24)
            return false;

        var hasLetter = token.Any(char.IsLetter);
        var hasDigit = token.Any(char.IsDigit);
        if (!hasLetter && !hasDigit)
            return false;
        if (!token.All(static ch => char.IsLetterOrDigit(ch)))
            return false;

        var folded = FoldDiacritics(token);
        if (hasLetter && !hasDigit)
            return token.Length >= 3 && !LexicalStopwords.Contains(folded);
        if (hasDigit && !hasLetter)
            return token.Length <= 8;

        return token.Length >= 2 && !LexicalStopwords.Contains(folded);
    }

    private static bool IsAlphaNumericPhraseTerm(string phrase)
    {
        if (phrase.Length is < 4 or > 40)
            return false;

        var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        var hasDigit = parts.Any(static part => part.Any(char.IsDigit));
        var hasLetter = parts.Any(static part => part.Any(char.IsLetter));
        if (!hasDigit || !hasLetter)
            return false;

        return parts.Any(static part => part.Any(char.IsLetter) && part.Length >= 3);
    }

    private static IReadOnlyList<string> BuildLexicalTokenVariants(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Array.Empty<string>();

        var variants = new HashSet<string>(StringComparer.Ordinal) { token };

        if (token.Length >= 6 && token.EndsWith("es", StringComparison.Ordinal))
            variants.Add(token[..^1]);
        if (token.Length >= 6 && (token.EndsWith("s", StringComparison.Ordinal) || token.EndsWith("x", StringComparison.Ordinal)))
            variants.Add(token[..^1]);
        if (token.Length >= 7 && token.EndsWith("eux", StringComparison.Ordinal))
            variants.Add(token[..^3] + "e");
        if (token.Length >= 7 && token.EndsWith("te", StringComparison.Ordinal))
            variants.Add(token[..^1]);

        foreach (var variant in variants.ToArray())
            AddCommonLatinSurfaceVariants(variants, variant);

        return variants
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => variant.Any(char.IsLetter))
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCommonLatinSurfaceVariants(HashSet<string> variants, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;
        AddVariant(variants, token switch
        {
            "bechamel" => "b\u00e9chamel",
            "bearnaise" => "b\u00e9arnaise",
            "bearnaises" => "b\u00e9arnaises",
            "eclair" => "\u00e9clair",
            "eclairs" => "\u00e9clairs",
            "entrecote" => "entrec\u00f4te",
            _ => null
        });

        AddVariant(variants, token.Replace("oe", "œ", StringComparison.Ordinal));
        AddVariant(variants, token.Replace("ae", "æ", StringComparison.Ordinal));
        AddVariant(variants, token.Replace("ae", "aë", StringComparison.Ordinal));
        AddVariant(variants, token.Replace("aioli", "aïoli", StringComparison.Ordinal));
        AddVariant(variants, token.Replace("paella", "paëlla", StringComparison.Ordinal));

        if (token.EndsWith("ee", StringComparison.Ordinal))
        {
            var finalAcute = token[..^2] + "ée";
            AddVariant(variants, finalAcute);
            AddVariant(variants, ReplaceFirst(finalAcute, "u", "û"));
        }

        if (string.Equals(token, "creme", StringComparison.Ordinal))
            AddVariant(variants, "crème");
        if (string.Equals(token, "cremes", StringComparison.Ordinal))
            AddVariant(variants, "crèmes");
        if (string.Equals(token, "brulee", StringComparison.Ordinal))
            AddVariant(variants, "brûlée");
        if (string.Equals(token, "brulees", StringComparison.Ordinal))
            AddVariant(variants, "brûlées");
        if (string.Equals(token, "epice", StringComparison.Ordinal))
            AddVariant(variants, "épicé");
        if (string.Equals(token, "epices", StringComparison.Ordinal))
            AddVariant(variants, "épicés");
        if (string.Equals(token, "inertage", StringComparison.Ordinal))
            AddVariant(variants, "inerting");
        if (string.Equals(token, "inerting", StringComparison.Ordinal))
            AddVariant(variants, "inertage");
        if (string.Equals(token, "entrecote", StringComparison.Ordinal) || string.Equals(token, "entrecôte", StringComparison.Ordinal))
        {
            AddVariant(variants, "steak");
            AddVariant(variants, "steaks");
        }
        if (string.Equals(token, "repas", StringComparison.Ordinal))
        {
            AddVariant(variants, "menu");
            AddVariant(variants, "menus");
            AddVariant(variants, "meal");
            AddVariant(variants, "meals");
        }
        if (string.Equals(token, "semaine", StringComparison.Ordinal))
        {
            AddVariant(variants, "weekly");
            AddVariant(variants, "hebdomadaire");
        }
        if (string.Equals(token, "dessert", StringComparison.Ordinal) || string.Equals(token, "desserts", StringComparison.Ordinal))
        {
            AddVariant(variants, "sucre");
            AddVariant(variants, "sucree");
            AddVariant(variants, "sucrée");
            AddVariant(variants, "sucrees");
            AddVariant(variants, "sucrées");
            AddVariant(variants, "sweet");
            AddVariant(variants, "sweets");
        }
    }

    private static void AddVariant(HashSet<string> variants, string? variant)
    {
        if (!string.IsNullOrWhiteSpace(variant))
            variants.Add(variant);
    }

    private static string ReplaceFirst(string value, string search, string replacement)
    {
        var index = value.IndexOf(search, StringComparison.Ordinal);
        return index < 0
            ? value
            : value[..index] + replacement + value[(index + search.Length)..];
    }

    private static void AddAdjacentPhraseTerms(HashSet<string> terms, IReadOnlyList<string> canonicalTokens)
    {
        if (canonicalTokens.Count < 2)
            return;

        var clean = canonicalTokens
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Take(8)
            .ToArray();

        for (var n = 2; n <= 3; n++)
        {
            if (clean.Length < n)
                continue;

            for (var i = 0; i <= clean.Length - n; i++)
            {
                AddPhraseWithSurfaceVariants(terms, clean.Skip(i).Take(n).ToArray());
            }
        }
    }

    private static void AddPhraseWithSurfaceVariants(HashSet<string> terms, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return;

        var tokenVariants = tokens
            .Select(BuildPhraseSurfaceVariants)
            .Where(static variants => variants.Count > 0)
            .ToArray();
        if (tokenVariants.Length != tokens.Count)
            return;

        var phrases = new List<string> { string.Empty };
        foreach (var variants in tokenVariants)
        {
            var next = new List<string>();
            foreach (var prefix in phrases)
            {
                foreach (var variant in variants)
                {
                    var phrase = string.IsNullOrWhiteSpace(prefix)
                        ? variant
                        : $"{prefix} {variant}";
                    next.Add(phrase);
                    if (next.Count >= 32)
                        break;
                }

                if (next.Count >= 32)
                    break;
            }

            phrases = next;
        }

        foreach (var phrase in phrases)
        {
            if (phrase.Length is >= 9 and <= 80)
                terms.Add(phrase);
        }
    }

    private static IReadOnlyList<string> BuildPhraseSurfaceVariants(string token)
        => BuildLexicalTokenVariants(token)
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .OrderBy(variant => string.Equals(variant, token, StringComparison.Ordinal) ? 0 : ContainsNonAscii(variant) ? 1 : 2)
            .ThenBy(static variant => variant.Length)
            .ThenBy(static variant => variant, StringComparer.Ordinal)
            .Take(5)
            .ToArray();

    private static bool ContainsNonAscii(string value)
        => value.Any(static ch => ch > 127);

    internal static string ExpandRetrievalQuery(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        _ = category;

        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var normalized = FoldDiacritics(lookup);
        if (string.IsNullOrWhiteSpace(normalized))
            return query.Trim();

        var trimmed = query.Trim();
        var expandedTerms = BuildQueryExpansionTerms(normalized);
        var additions = new List<string>();
        if (!string.Equals(normalized, lookup, StringComparison.Ordinal))
            additions.Add(normalized);
        foreach (var term in expandedTerms)
        {
            if (!lookup.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
                additions.Add(term);
        }

        return additions.Count == 0
            ? trimmed
            : $"{trimmed} {string.Join(' ', additions.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }

    internal static IReadOnlyList<string> BuildQueryExpansionTerms(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return Array.Empty<string>();

        var terms = new HashSet<string>(StringComparer.Ordinal);
        var padded = $" {normalizedQuery} ";

        var hasMealContext = ContainsAny(
            padded,
            " repas ",
            " menu ",
            " menus ",
            " dejeuner ",
            " diner ",
            " souper ",
            " lunch ",
            " dinner ",
            " meal ",
            " meals ");
        var hasPlanningContext = ContainsAny(padded, " organiser ", " organise ", " planifier ", " planning ", " prevoir ", " preparer ", " semaine ", " weekly ");
        if (hasMealContext && hasPlanningContext)
        {
            terms.Add("planifier");
            terms.Add("prevoir");
            terms.Add("preparer a l avance");
            terms.Add("repas de la semaine");
            terms.Add("menus");
            terms.Add("batch cooking");
            terms.Add("meal prep");
            terms.Add("weekly meals");
        }

        var hasQuickMealContext = ContainsAny(padded, " rapide ", " rapides ", " vite ", " express ", " facile ", " faciles ", " quick ", " fast ", " easy ");
        if (hasMealContext && hasQuickMealContext)
        {
            terms.Add("recette rapide");
            terms.Add("repas rapide");
            terms.Add("facile");
            terms.Add("express");
            terms.Add("quick meal");
            terms.Add("easy lunch");
        }

        var hasSauceContext = padded.Contains(" sauce ", StringComparison.Ordinal);
        var hasMeatContext = ContainsAny(padded, " entrecote ", " steak ", " steaks ", " boeuf ", " bœuf ", " beef ", " viande ", " meat ");
        if (hasSauceContext && hasMeatContext)
        {
            terms.Add("steak");
            terms.Add("steaks");
            terms.Add("steaks poivre");
            terms.Add("poivre");
        }

        return terms.ToArray();
    }

    internal static string FoldDiacritics(string value)
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

    internal static double ComputeLexicalCoverage(IReadOnlyList<string> queryTokens, string? candidateText)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var matched = 0;
        foreach (var token in queryTokens)
        {
            var variants = new HashSet<string>(BuildLexicalTokenVariants(token), StringComparer.Ordinal);
            if (variants.Contains("inertage") || variants.Contains("inerting"))
            {
                variants.Add("inertage");
                variants.Add("inerting");
            }

            if (variants.Any(variant => normalizedCandidate.Contains(variant, StringComparison.Ordinal)))
                matched++;
        }

        return matched == 0 ? 0.0 : (double)matched / queryTokens.Count;
    }

    internal static bool ComputeHypQuestionsMatched(string query, IReadOnlyList<string>? hypotheticalQuestions)
    {
        if (hypotheticalQuestions is null || hypotheticalQuestions.Count == 0)
            return false;

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count == 0)
            return false;

        return hypotheticalQuestions.Any(question => ComputeLexicalCoverage(queryTokens, question) >= 0.5);
    }

    internal static bool? ResolveHypQuestionsMatched(string? docPath, IReadOnlyDictionary<string, bool?>? byDocPath)
    {
        if (string.IsNullOrWhiteSpace(docPath) || byDocPath is null)
            return null;

        return byDocPath.TryGetValue(docPath, out var matched)
            ? matched
            : null;
    }

    internal static double ComputeLinkedMatchScore(double anchorScore, string linkType, string? anchorRetriever = null)
    {
        var penalty = linkType switch
        {
            "same_section" => 0.02,
            "next" => 0.035,
            "prev" => 0.04,
            _ => 0.05
        };

        if (string.Equals(anchorRetriever, "linked_context", StringComparison.Ordinal))
            penalty += 0.015;
        else if (string.Equals(anchorRetriever, "exact_match", StringComparison.Ordinal))
            penalty += 0.01;
        else if (string.Equals(anchorRetriever, "sparse_bm25", StringComparison.Ordinal))
            penalty += 0.005;

        return Math.Max(0.0, anchorScore - penalty);
    }

    private sealed record LinkedAnchorCandidate(Guid SourceId, string SourceRetriever, double AnchorScore);

    private sealed record TitleSelectionSignal(
        RagMatch Match,
        double ExactTitleScore,
        int SpecificAnchorCount,
        bool FullTitleCoverage,
        int StructuredAnswerPriority);

    internal static double ComputeExactMatchScore(string? matchKind, string? matchedTerm, string? text)
    {
        var score = matchKind switch
        {
            "standard_ref" => 1.0,
            "code_ref" => 0.985,
            _ => 0.96
        };

        var normalizedMatchedTerm = ExactMatchEntryExtractor.NormalizeForLookup(matchedTerm ?? string.Empty);
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedMatchedTerm))
        {
            score += Math.Min(0.01, normalizedMatchedTerm.Length / 500.0);
            if (string.Equals(normalizedMatchedTerm, normalizedText, StringComparison.Ordinal))
                score += 0.005;
        }

        return Math.Min(1.02, score);
    }

    internal static bool ShouldShortCircuitAfterExact(IReadOnlyList<RagMatch> exactMatches)
    {
        if (exactMatches.Count == 0)
            return false;

        var top = exactMatches[0];
        if (!string.Equals(top.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal))
            return false;

        var isStrongReferenceHit =
            string.Equals(top.ChunkType, "exact_match_entry", StringComparison.Ordinal)
            || string.Equals(top.ChunkType, "document_metadata_ref", StringComparison.Ordinal);

        if (!isStrongReferenceHit || top.Score < 0.97)
            return false;
        if (LooksLikeNavigationalChunk(top))
            return false;

        if (exactMatches.Count == 1)
            return true;

        var second = exactMatches[1];
        return top.Score - second.Score >= 0.04;
    }

    internal static string ResolveRetriever(RagMatch match)
        => match.EmbeddingBasis switch
        {
            "exact_match_v1" => "exact_match",
            "sparse_bm25_v1" => "sparse_bm25",
            "document_profile_v1" => "document_profile",
            "linked_context_v1" => "linked_context",
            _ => "dense_qdrant"
        };

    private static void AccumulateRrf(
        Dictionary<string, RrfAccumulator> accumulators,
        IReadOnlyList<RagMatch> matches,
        int rrfK)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = BuildMatchDedupKey(match);
            var contribution = 1.0 / (rrfK + i + 1);
            if (accumulators.TryGetValue(key, out var existing))
            {
                accumulators[key] = existing with
                {
                    Score = existing.Score + contribution,
                    Representative = SelectRepresentative(existing.Representative, match)
                };
            }
            else
            {
                accumulators[key] = new RrfAccumulator(contribution, match);
            }
        }
    }

    private static RagMatch SelectRepresentative(RagMatch left, RagMatch right)
    {
        var leftPriority = GetRetrieverPriority(left);
        var rightPriority = GetRetrieverPriority(right);
        if (rightPriority != leftPriority)
            return rightPriority > leftPriority ? right : left;

        return right.Score > left.Score ? right : left;
    }

    private static int GetRetrieverPriority(RagMatch match)
        => ResolveRetriever(match) switch
        {
            "exact_match" => 4,
            "sparse_bm25" => 3,
            "dense_qdrant" => 2,
            _ => 1
        };

    private static readonly HashSet<string> LexicalStopwords = new(StringComparer.Ordinal)
    {
        "dans", "avec", "sans", "pour", "vers", "entre", "apres", "avant",
        "quel", "quelle", "quels", "quelles", "trouve", "trouver", "montre",
        "montrez", "ou", "sont", "sous", "plus", "moins", "comme", "cela",
        "cette", "cet", "ces", "leurs", "leur", "par", "sur", "des", "une",
        "bien", "irait", "iraient", "convient", "conviendrait",
        "les", "que", "quoi", "dont", "when", "where", "which", "with", "from",
        "would", "could", "should", "well", "goes",
        "this", "that", "those", "these", "what", "into", "pdf", "doc", "document",
        "manuel", "manual", "guide", "please", "stp", "svp", "cherche", "show",
        "need", "have", "has", "just", "juste", "moi", "peux", "avoir",
        "faire", "fais", "fait", "make", "help", "aide", "aider",
        "parle", "parler", "documents", "compare", "comparer", "comparison",
        "difference", "differences", "different", "deux", "corpus", "ingredient",
        "ingredients", "methode", "method", "style", "recette", "recettes",
        "recipe", "recipes", "lequel", "veux", "veut", "simple",
        "fiche", "claire", "clair", "detail", "details", "detailee", "detaillee",
        "etape", "etapes", "step", "steps", "source", "sources", "citation",
        "citations", "reponse", "answer", "format", "liste", "list",
        "plusieurs", "multiple", "several", "many",
        "temps", "time", "duration", "duree",
        "existe", "existes", "existent", "exister", "existing", "exist", "exists",
        "disponible", "disponibles", "available", "availability",
        "utile", "utiles", "useful", "pertinent", "pertinents", "relevant",
        "contient", "contiennent", "containing", "contain", "contains",
        "mentionne", "mentionnent", "mentioning", "mentions",
        "presente", "presentent", "present", "presents"
    };

    private static readonly HashSet<string> SpecificAnchorStopwords = new(StringComparer.Ordinal)
    {
        "ingredient", "ingredients", "preparation", "procedure", "procedures",
        "methode", "method", "methods", "instruction", "instructions",
        "materiel", "material", "materials", "requirement", "requirements",
        "warning", "warnings", "caution", "cautions", "summary", "overview",
        "checklist", "source", "sources", "citation", "citations", "detail",
        "details", "format", "answer", "reponse", "liste", "list",
        "temps", "time", "duration", "duree"
    };

    private static readonly HashSet<string> PrimaryAnchorStopwords = new(StringComparer.Ordinal)
    {
        "organiser", "organise", "organization", "organisation", "planifier",
        "planning", "prevoir", "preparer", "prepare", "prepared", "avance",
        "weekly", "hebdomadaire", "semaine", "menu", "menus", "repas", "meal",
        "meals", "batch", "cooking", "recette", "recettes", "recipe", "recipes",
        "question", "questions", "utilisateur", "client", "besoin", "conseil",
        "conseils", "recommendation", "recommendations"
    };

    private static readonly HashSet<string> TitleConnectorTokens = new(StringComparer.Ordinal)
    {
        "a", "al", "alla", "all", "an", "and", "as", "at", "au", "aux",
        "avec", "com", "con", "d", "da", "de", "del", "della", "des", "di", "do",
        "dos", "du", "e", "em", "en", "et", "for", "la", "las", "le", "les",
        "lo", "los", "mit", "of", "on", "the", "to", "und", "with", "y", "zu",
        "zum", "zur"
    };

    private static readonly HashSet<string> ComparativeSubjectStopwords = new(StringComparer.Ordinal)
    {
        "est", "sont", "etre", "etaient", "sera", "serait", "is", "are", "was", "were",
        "the", "le", "la", "les", "un", "une", "des", "du", "de", "del", "der", "die",
        "quel", "quelle", "quels", "quelles", "which", "what", "cual", "qual", "quale",
        "welches", "welcher", "welche", "plus", "most", "mas", "mais", "mehr"
    };

    private static readonly HashSet<string> DocumentHintStopwords = new(StringComparer.Ordinal)
    {
        "PDF", "DOC", "DOCUMENT", "GUIDE", "GUIDANCE", "MANUAL", "MANUEL", "NOTICE",
        "TERMINAL", "INTERFACE", "PROGRAMMATION", "PROGRAMMING", "COMMUNICATION",
        "COMMUNICATIONS", "SYSTEM", "SYSTEMS", "PROCESS", "PROCESSUS", "PROCEDURE",
        "PREVENTION", "EXPLOSION", "EXPLOSIONS", "INERTING", "INERTAGE", "WEIGHING",
        "PESAGE", "TOLEDO", "METTLER", "GENERAL", "THE", "FOR", "AND", "WITH", "SUR",
        "POUR", "DES", "LES", "UNE", "UN", "DU", "DE", "LA", "LE", "ET", "ON", "OF"
    };

    private static readonly string[] ProcessSafetyKeywords =
    [
        "inert", "oxygen", "oxygene", "explosion", "flammability", "flammabilite",
        "loc", "maoc", "hybrid", "dust", "poussier", "purge", "nitrogen", "azote",
        "carbon dioxide", "co2", "flue gas", "gaz de combustion"
    ];

    private static readonly string[] ControlIntegrationKeywords =
    [
        "plc", "automate", "profinet", "profibus", "modbus", "ethernet/ip",
        "ethernet ip", "device", "controlnet", "devicenet", "class 1", "class 3",
        "analog", "analogique", "calibration", "tare", "tolerance", "siemens",
        "rockwell", "shared data", "donnees partagees", "sortie analogique"
    ];

    private static readonly string[] HazardousAreaKeywords =
    [
        "hazardous", "zone dangereuse", "zone class", "zone 2", "zone 22",
        "division 2", "explosive atmosphere", "atmosphere explosive", "atex"
    ];

    private static readonly string[] FunctionalSafetyKeywords =
    [
        "sil", "61508", "61511", "safety instrumented", "instrumented function",
        "fonction de securite", "safety lifecycle"
    ];

    private sealed record RrfAccumulator(double Score, RagMatch Representative);
    internal sealed record MatchedRetrievalContext(
        IReadOnlyList<string> DocHints,
        bool HasProcessSafetyDomain,
        bool HasControlIntegrationDomain,
        bool HasHazardousAreaDomain,
        bool HasFunctionalSafetyDomain);

    internal static string ResolveProvenance(RagMatch match)
        => $"retriever:{ResolveRetriever(match)}";

    internal static RagAnswerGuidanceDto BuildAnswerGuidance(string query, IReadOnlyList<RagMatch> matches)
    {
        var normalized = NormalizeQueryForGuidance(query);
        var matchedContext = BuildMatchedRetrievalContext(matches);
        var matchedDocHints = matchedContext.DocHints;

        if (matches.Count == 0)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "no_relevant_source_found",
                ResponseShape: "no_source_match",
                QualificationNote: "No sufficiently relevant source was retrieved for this query.",
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsPlaceholderStandard(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "missing_standard_identifier",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "missing_standard_identifier"),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsSilCertificationQuery(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "safety_certification_requires_precise_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "safety_certification_requires_precise_scope"),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsBroadAtexComplianceQuery(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "broad_atex_compliance_requires_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "broad_atex_compliance_requires_scope"),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsSufficiencyQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer",
                Reason: "document_scope_limit_can_be_answered_directly",
                ResponseShape: "qualified_answer",
                MatchedDocHints: matchedDocHints);
        }

        var simpleDocumentSelection = ContainsSimpleDocumentSelectionQuestion(normalized);
        var needsQualification =
            ContainsOperationalRiskRecommendationQuestion(normalized)
            || ContainsQuickCustomerReplySelectionQuestion(normalized)
            || (!simpleDocumentSelection && (
                ContainsComplianceLanguage(normalized)
                || ContainsCustomerReplyLanguage(normalized)
                || ContainsProjectAssessmentLanguage(normalized)
                || ContainsCrossDomainSafetyIntegrationQuestion(normalized)
                || ContainsFireProtectionClaimQuestion(normalized)));

        if (needsQualification)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "project_or_compliance_answer_requires_qualification",
                ResponseShape: DetermineResponseShape(normalized, behavior: "answer_with_caveat"),
                QualificationNote: BuildQualificationNote(normalized, matchedContext, simpleDocumentSelection),
                MatchedDocHints: matchedDocHints);
        }

        return new RagAnswerGuidanceDto(
            Behavior: "answer",
            Reason: "documented_question_with_relevant_sources",
            ResponseShape: DetermineResponseShape(normalized, behavior: "answer"),
            MatchedDocHints: matchedDocHints);
    }

    internal static RagItemProvenanceDto BuildProvenanceInfo(RagMatch match)
        => new(
            Channel: ResolveRetriever(match),
            Label: ResolveProvenance(match),
            SourceHash: match.HashDoc,
            ChunkId: match.ChunkId,
            PageStart: match.PageStart,
            PageEnd: match.PageEnd,
            OffsetStart: match.OffsetStart,
            OffsetEnd: match.OffsetEnd);

    internal static RagItemContextDto BuildContextInfo(RagMatch match)
        => new(
            ChunkType: match.ChunkType,
            SectionTitle: match.SectionTitle,
            HeadingPath: match.HeadingPath,
            PrevChunkId: match.PrevChunkId,
            NextChunkId: match.NextChunkId,
            SameSectionChunkId: match.SameSectionChunkId);

    internal static async Task<IReadOnlyDictionary<string, string>> LoadTopCategoryRefsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var topLevelPaths = matches
            .Select(match => ExtractTopLevelCategoryPath(match.DocPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (topLevelPaths.Length == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  path          AS "Path",
  display_order AS "DisplayOrder"
FROM documents_catalog_categories
WHERE tenant_id=@tenant
  AND path = ANY(@paths);
""";

        var rows = await conn.QueryAsync<TopCategoryOrderRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, paths = topLevelPaths },
            cancellationToken: ct));

        return rows.ToDictionary(
            static row => row.Path,
            static row => BuildCategoryRef(row.DisplayOrder),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static string? BuildDocumentCategory(string? docPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(docPath);
        return string.IsNullOrWhiteSpace(topLevel)
            ? null
            : topLevel.ToLowerInvariant();
    }

    internal static string? BuildDocumentCategoryPath(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var normalized = docPath.Trim().Replace('\\', '/').Trim('/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex <= 0)
            return null;

        return normalized[..slashIndex];
    }

    internal static string? ResolveCategoryRef(string? categoryPath, IReadOnlyDictionary<string, string> categoryRefsByTopLevelPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(topLevel))
            return null;

        return categoryRefsByTopLevelPath.TryGetValue(topLevel, out var categoryRef)
            ? categoryRef
            : null;
    }

    internal static string? BuildSnippet(string? text, int maxLength = 500, string? query = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        var anchor = FindQuerySnippetAnchor(normalized, query);
        if (anchor >= 0)
            return BuildCenteredSnippet(normalized, anchor, maxLength);

        // Cut at last sentence boundary within limit
        var cutoff = normalized.LastIndexOf('.', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = normalized.LastIndexOf(' ', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return normalized[..(cutoff + 1)].TrimEnd();
    }

    private static int FindQuerySnippetAnchor(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
            return -1;

        var folded = BuildFoldedTextIndex(text);
        if (folded.Text.Length == 0 || folded.OriginalIndexes.Count == 0)
            return -1;

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !SnippetAnchorLowPriorityTerms.Contains(token))
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length == 0)
        {
            tokens = ExtractLexicalQueryTokens(query)
                .OrderByDescending(static token => token.Length)
                .ThenBy(static token => token, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var token in tokens)
        {
            var index = folded.Text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0 && index < folded.OriginalIndexes.Count)
                return folded.OriginalIndexes[index];
        }

        return -1;
    }

    private static string BuildCenteredSnippet(string text, int anchor, int maxLength)
    {
        if (maxLength <= 20 || text.Length <= maxLength)
            return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();

        var start = Math.Max(0, anchor - (maxLength / 3));
        start = MoveSnippetStartToBoundary(text, start, anchor);

        if (start + maxLength > text.Length)
            start = Math.Max(0, text.Length - maxLength);

        var length = Math.Min(maxLength, text.Length - start);
        var end = start + length;
        end = MoveSnippetEndToBoundary(text, end, start);

        var snippet = text[start..end].Trim();
        if (start > 0)
            snippet = "..." + snippet.TrimStart();
        if (end < text.Length)
            snippet = snippet.TrimEnd() + "...";

        return snippet.Length <= maxLength
            ? snippet
            : snippet[..maxLength].TrimEnd();
    }

    private static int MoveSnippetStartToBoundary(string text, int start, int anchor)
    {
        if (start <= 0)
            return 0;

        var searchLength = Math.Max(0, anchor - start);
        if (searchLength == 0)
            return start;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, start + searchLength - 1, searchLength);
        if (sentence >= start && sentence + 1 < anchor)
            return sentence + 1;

        var space = text.IndexOf(' ', start);
        return space >= 0 && space < anchor ? space + 1 : start;
    }

    private static int MoveSnippetEndToBoundary(string text, int end, int start)
    {
        if (end >= text.Length)
            return text.Length;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, end - 1, Math.Max(0, end - start));
        if (sentence > start + ((end - start) / 2))
            return sentence + 1;

        var space = text.LastIndexOf(' ', end - 1, Math.Max(0, end - start));
        return space > start + ((end - start) / 2) ? space : end;
    }

    private static FoldedTextIndex BuildFoldedTextIndex(string value)
    {
        var sb = new StringBuilder(value.Length);
        var indexes = new List<int>(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var normalized = value[i].ToString().Normalize(NormalizationForm.FormD);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                foreach (var folded in FoldSnippetChar(ch))
                {
                    sb.Append(char.ToLowerInvariant(folded));
                    indexes.Add(i);
                }
            }
        }

        return new FoldedTextIndex(sb.ToString().Normalize(NormalizationForm.FormC), indexes);
    }

    private static string FoldSnippetChar(char ch)
        => ch switch
        {
            'œ' or 'Œ' => "oe",
            'æ' or 'Æ' => "ae",
            'ß' => "ss",
            'ø' or 'Ø' => "o",
            'ł' or 'Ł' => "l",
            'đ' or 'Đ' => "d",
            _ => ch.ToString()
        };

    private static readonly HashSet<string> SnippetAnchorLowPriorityTerms = new(StringComparer.Ordinal)
    {
        "document",
        "documents",
        "recette",
        "recettes",
        "recipe",
        "recipes",
        "fiche",
        "claire",
        "faire",
        "peux",
        "veux",
        "donne",
        "trouve",
        "ingredients",
        "ingredient",
        "etapes",
        "etape",
        "temps",
        "source",
        "sources",
        "page",
        "pages"
    };

    private sealed record FoldedTextIndex(string Text, IReadOnlyList<int> OriginalIndexes);

    internal static bool DetectHasTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // Heuristic: rows with pipe separators or tab-separated columns
        var lines = text.Split('\n');
        var pipeLines = lines.Count(l => l.Contains('|') && l.Count(c => c == '|') >= 2);
        return pipeLines >= 2;
    }

    internal static bool DetectHasWarning(string? text, string? chunkType)
    {
        if (string.Equals(chunkType, "warning", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var upper = text.AsSpan();
        return upper.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("DANGER", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("CAUTION", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("AVERTISSEMENT", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ComputeDataHash(IReadOnlyList<RagMatch> matches)
    {
        if (matches.Count == 0)
            return null;

        using var sha = SHA256.Create();
        foreach (var match in matches)
        {
            var canonical = $"{match.DocId}|{match.ChunkId}|{match.IngestionVersion}|{match.HashDoc}|{match.EmbeddingBasis}";
            var bytes = Encoding.UTF8.GetBytes(canonical);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    internal static IReadOnlyList<string> ExtractMatchedDocHints(IReadOnlyList<RagMatch> matches)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            CollectDocumentHints(match.DocName, hints);
            CollectDocumentHints(match.DocPath, hints);
        }

        if (hints.Count == 0)
            return Array.Empty<string>();

        var ordered = hints.OrderBy(static h => h, StringComparer.Ordinal).ToArray();
        var filtered = ordered
            .Where(hint => !ordered.Any(other =>
                !string.Equals(other, hint, StringComparison.Ordinal)
                && other.Length < hint.Length
                && hint.EndsWith(other, StringComparison.Ordinal)))
            .ToArray();

        return filtered.Length == 0 ? ordered : filtered;
    }

    internal static MatchedRetrievalContext BuildMatchedRetrievalContext(IReadOnlyList<RagMatch> matches)
    {
        var hints = ExtractMatchedDocHints(matches);
        var hasProcessSafetyDomain = false;
        var hasControlIntegrationDomain = false;
        var hasHazardousAreaDomain = false;
        var hasFunctionalSafetyDomain = false;

        foreach (var match in matches)
        {
            var searchable = $"{match.DocName} {match.DocPath} {match.SectionTitle} {match.HeadingPath} {match.Text} {match.EmbedText}";
            hasProcessSafetyDomain |= ContainsAny(searchable, ProcessSafetyKeywords);
            hasControlIntegrationDomain |= ContainsAny(searchable, ControlIntegrationKeywords);
            hasHazardousAreaDomain |= ContainsAny(searchable, HazardousAreaKeywords);
            hasFunctionalSafetyDomain |= ContainsAny(searchable, FunctionalSafetyKeywords);
        }

        return new MatchedRetrievalContext(
            hints,
            hasProcessSafetyDomain,
            hasControlIntegrationDomain,
            hasHazardousAreaDomain,
            hasFunctionalSafetyDomain);
    }

    private static bool ContainsPlaceholderStandard(string normalizedQuery)
        => normalizedQuery.Contains("norme xxx", StringComparison.Ordinal)
           || normalizedQuery.Contains("standard xxx", StringComparison.Ordinal);

    private static bool ContainsSilCertificationQuery(string normalizedQuery)
        => normalizedQuery.Contains(" sil ", StringComparison.Ordinal)
           || normalizedQuery.StartsWith("sil ", StringComparison.Ordinal)
           || normalizedQuery.EndsWith(" sil", StringComparison.Ordinal)
           || normalizedQuery.Contains("certification sil", StringComparison.Ordinal);

    private static bool ContainsBroadAtexComplianceQuery(string normalizedQuery)
        => normalizedQuery.Contains("atex", StringComparison.Ordinal)
           && ContainsComplianceLanguage(normalizedQuery)
           && !HasSpecificReferenceLookup(normalizedQuery);

    private static bool ContainsComplianceLanguage(string normalizedQuery)
        => normalizedQuery.Contains("respecte", StringComparison.Ordinal)
           || normalizedQuery.Contains("conforme", StringComparison.Ordinal)
           || normalizedQuery.Contains("conformite", StringComparison.Ordinal)
           || normalizedQuery.Contains("compliance", StringComparison.Ordinal);

    private static bool ContainsCustomerReplyLanguage(string normalizedQuery)
        => normalizedQuery.Contains("on lui repond", StringComparison.Ordinal)
           || normalizedQuery.Contains("tu repondrais", StringComparison.Ordinal)
           || normalizedQuery.Contains("reponse prudente", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu est ce qu il faut lui demander", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu'est ce qu'il faut lui demander", StringComparison.Ordinal);

    private static bool ContainsProjectAssessmentLanguage(string normalizedQuery)
        => normalizedQuery.Contains("projet", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre systeme", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre installation", StringComparison.Ordinal);

    private static bool ContainsCrossDomainSafetyIntegrationQuestion(string normalizedQuery)
        => (normalizedQuery.Contains("surveillance oxygene", StringComparison.Ordinal)
            || normalizedQuery.Contains("oxygene", StringComparison.Ordinal)
            || normalizedQuery.Contains("atmosphere", StringComparison.Ordinal))
           && (normalizedQuery.Contains("plc", StringComparison.Ordinal)
               || normalizedQuery.Contains("automate", StringComparison.Ordinal)
               || normalizedQuery.Contains("terminal", StringComparison.Ordinal));

    private static bool ContainsSufficiencyQuestion(string normalizedQuery)
        => normalizedQuery.Contains("suffit a lui seul", StringComparison.Ordinal)
           || normalizedQuery.Contains("suffit a elle seule", StringComparison.Ordinal);

    private static bool ContainsSimpleDocumentSelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("quel document faut il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("quel document faut-il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel pour parler", StringComparison.Ordinal)
           || normalizedQuery.Contains("quel document", StringComparison.Ordinal) && normalizedQuery.Contains("integration plc", StringComparison.Ordinal);

    private static bool ContainsFireProtectionClaimQuestion(string normalizedQuery)
        => (normalizedQuery.Contains("peut dire", StringComparison.Ordinal)
            || normalizedQuery.Contains("on peut dire", StringComparison.Ordinal)
            || normalizedQuery.Contains("est protege", StringComparison.Ordinal))
           && (normalizedQuery.Contains("feu", StringComparison.Ordinal)
               || normalizedQuery.Contains("incendie", StringComparison.Ordinal));

    private static bool ContainsOperationalRiskRecommendationQuestion(string normalizedQuery)
        => (normalizedQuery.Contains("vapeur", StringComparison.Ordinal)
            || normalizedQuery.Contains("gaz de combustion", StringComparison.Ordinal)
            || normalizedQuery.Contains("niveau de fiabilite", StringComparison.Ordinal)
            || normalizedQuery.Contains("zone potentiellement explosive", StringComparison.Ordinal)
            || normalizedQuery.Contains("zone dangereuse", StringComparison.Ordinal))
           && (normalizedQuery.Contains("client", StringComparison.Ordinal)
               || normalizedQuery.Contains("on peut", StringComparison.Ordinal)
               || normalizedQuery.Contains("utiliser", StringComparison.Ordinal)
               || normalizedQuery.Contains("dit quoi", StringComparison.Ordinal)
               || normalizedQuery.Contains("demande", StringComparison.Ordinal));

    private static bool ContainsQuickCustomerReplySelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("repondre vite au client", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvrir en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvre en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel des deux docs", StringComparison.Ordinal)
           || (normalizedQuery.Contains("par lequel", StringComparison.Ordinal)
               && normalizedQuery.Contains("client", StringComparison.Ordinal));

    private static string BuildClarifyingQuestion(string normalizedQuery, MatchedRetrievalContext matchedContext, string reason)
    {
        if (string.Equals(reason, "safety_certification_requires_precise_scope", StringComparison.Ordinal))
            return "Tu parles d'une exigence SIL pour quel composant, quelle fonction de securite et quel niveau attendu ?";

        if (string.Equals(reason, "broad_atex_compliance_requires_scope", StringComparison.Ordinal))
        {
            if (matchedContext.HasControlIntegrationDomain || matchedContext.HasHazardousAreaDomain)
                return "Tu vises quelle exigence ATEX precise, sur quelle zone, pour quelle version d'equipement et avec quelles options installees ?";

            return "Tu vises quelle exigence ATEX precise, sur quelle zone et pour quelle partie de l'installation ou du process ?";
        }

        if (matchedContext.HasProcessSafetyDomain && matchedContext.HasControlIntegrationDomain)
            return "Tu parles de quelle norme exactement, et est-ce que tu vises plutot le process/inertage, le terminal/PLC, ou la zone ATEX autour de l'installation ?";

        if (matchedContext.HasControlIntegrationDomain)
            return "Tu parles de quelle norme exactement et sur quelle partie de l'equipement ou de l'integration automatisme ?";

        if (matchedContext.HasProcessSafetyDomain)
            return "Tu parles de quelle norme exactement et sur quelle partie du process ou de l'inertage ?";

        return "Tu parles de quelle norme exactement et sur quelle partie du projet ou de l'equipement ?";
    }

    private static string BuildQualificationNote(string normalizedQuery, MatchedRetrievalContext matchedContext, bool simpleDocumentSelection)
    {
        var hintLabel = FormatHintLabel(matchedContext.DocHints);

        if (matchedContext.HasProcessSafetyDomain && matchedContext.HasControlIntegrationDomain)
        {
            if (ContainsQuickCustomerReplySelectionQuestion(normalizedQuery) || simpleDocumentSelection)
                return $"Commence par la documentation process/safety{hintLabel} pour cadrer le sujet et la conformite process, puis utilise la documentation terminal/PLC pour l'equipement et l'integration; cela ne suffit pas a affirmer la conformite complete du projet.";

            return $"Les documents retrouves{hintLabel} couvrent a la fois le process/safety et le terminal ou l'integration PLC. Il faut encore cadrer le perimetre, les equipements concernes et le contexte projet avant d'affirmer une conformite complete du projet.";
        }

        if (matchedContext.HasControlIntegrationDomain && (matchedContext.HasHazardousAreaDomain
            || normalizedQuery.Contains("zone potentiellement explosive", StringComparison.Ordinal)
            || normalizedQuery.Contains("zone dangereuse", StringComparison.Ordinal)))
        {
            return $"La documentation equipement{hintLabel} aide a cadrer le terminal, mais il faut verifier la version exacte, la zone dangereuse visee et les options installees avant toute affirmation de conformite.";
        }

        if (matchedContext.HasProcessSafetyDomain && (ContainsComplianceLanguage(normalizedQuery)
            || ContainsCustomerReplyLanguage(normalizedQuery)
            || ContainsProjectAssessmentLanguage(normalizedQuery)))
        {
            return $"La documentation process{hintLabel} eclaire l'inertage, les sauvegardes et le contexte technique, mais il faut encore cadrer le perimetre, les equipements concernes et le contexte projet avant d'affirmer une conformite.";
        }

        return "Les documents peuvent eclairer le sujet, mais ils ne suffisent pas seuls a affirmer la conformite complete du projet sans contexte supplementaire.";
    }

    private static string DetermineResponseShape(string normalizedQuery, string behavior)
    {
        if (string.Equals(behavior, "ask_clarification", StringComparison.Ordinal))
            return "clarify";

        if (normalizedQuery.Contains("compare", StringComparison.Ordinal)
            || normalizedQuery.Contains("difference", StringComparison.Ordinal)
            || normalizedQuery.Contains("comparer", StringComparison.Ordinal))
            return "comparison";

        if (normalizedQuery.Contains("montre", StringComparison.Ordinal)
            || normalizedQuery.Contains("passage", StringComparison.Ordinal)
            || normalizedQuery.Contains("ou dans le document", StringComparison.Ordinal))
            return "locate_passage";

        if (normalizedQuery.Contains("quel document", StringComparison.Ordinal)
            || normalizedQuery.Contains("manuel", StringComparison.Ordinal)
            || normalizedQuery.Contains("ouvrir", StringComparison.Ordinal)
            || normalizedQuery.Contains("commencer", StringComparison.Ordinal))
            return "locate_document";

        if (normalizedQuery.Contains("resumer", StringComparison.Ordinal)
            || normalizedQuery.Contains("resume", StringComparison.Ordinal)
            || normalizedQuery.Contains("reponse courte", StringComparison.Ordinal)
            || normalizedQuery.Contains("expliquer simplement", StringComparison.Ordinal))
            return "summary";

        if (string.Equals(behavior, "answer_with_caveat", StringComparison.Ordinal))
            return "qualified_answer";

        return "direct_answer";
    }

    private static void CollectDocumentHints(string? rawText, HashSet<string> hints)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var stem = ExtractDocumentStem(rawText);
        if (string.IsNullOrWhiteSpace(stem))
            return;

        var localHints = new HashSet<string>(StringComparer.Ordinal);
        string? fallbackAlpha = null;
        string? previousAlpha = null;

        foreach (var part in stem.Split([' ', '-', '_', '/', '\\', '.', ',', ';', ':', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var compact = CompactDocumentHintToken(part);
            if (string.IsNullOrEmpty(compact))
            {
                previousAlpha = null;
                continue;
            }

            if (compact.All(char.IsDigit))
            {
                if (!string.IsNullOrWhiteSpace(previousAlpha) && compact.Length is >= 2 and <= 6)
                    localHints.Add($"{previousAlpha}{compact}");

                if (compact.Length >= 4)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (compact.Any(char.IsDigit))
            {
                if (compact.Length is >= 4 and <= 16)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (IsMeaningfulDocumentHint(compact))
            {
                fallbackAlpha ??= compact;
                previousAlpha = compact.Length <= 5 ? compact : null;
                continue;
            }

            if (IsShortReferencePrefix(compact))
            {
                previousAlpha = compact;
                continue;
            }

            previousAlpha = null;
        }

        if (localHints.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(fallbackAlpha))
                hints.Add(fallbackAlpha);
            return;
        }

        foreach (var hint in localHints)
            hints.Add(hint);
    }

    private static string ExtractDocumentStem(string rawText)
    {
        var normalized = rawText.Replace('\\', '/');
        var tail = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dotIndex = tail.LastIndexOf('.');
        return dotIndex > 0 ? tail[..dotIndex] : tail;
    }

    private static string CompactDocumentHintToken(string token)
    {
        Span<char> buffer = stackalloc char[token.Length];
        var length = 0;

        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToUpperInvariant(ch);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    private static bool IsMeaningfulDocumentHint(string token)
        => token.Length >= 4
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);

    private static bool IsShortReferencePrefix(string token)
        => token.Length is >= 2 and <= 5
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);

    private static bool ContainsAny(string? text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FormatHintLabel(IReadOnlyList<string> hints)
        => hints.Count switch
        {
            0 => string.Empty,
            1 => $" ({hints[0]})",
            2 => $" ({hints[0]} + {hints[1]})",
            _ => $" ({hints[0]} + {hints[1]} + autres sources)"
        };

    private static bool HasSpecificReferenceLookup(string normalizedQuery)
    {
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(normalizedQuery);
        return ExactMatchEntryExtractor.ExtractLookupTerms(normalizedQuery)
            .Any(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal));
    }

    private static string NormalizeQueryForGuidance(string query)
        => $" {ExactMatchEntryExtractor.NormalizeForLookup(query)} ";

    private static string NormalizeQuery(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0)
            return s;

        var sb = new StringBuilder(s.Length);
        var inWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString();
    }

    private sealed record CapabilityAHypQuestionDocRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string? HypotheticalQuestionsJson);

    private sealed record CapabilityAHypQuestionDoc(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string[] HypotheticalQuestions);

    private static string[] ParseJsonStringArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildCategoryRef(int displayOrder)
        => $"cat_{displayOrder:000}";

    private static string? ExtractTopLevelCategoryPath(string? categoryOrDocPath)
    {
        if (string.IsNullOrWhiteSpace(categoryOrDocPath))
            return null;

        var normalized = categoryOrDocPath.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static async Task<List<RagMatch>> SearchDocumentMetadataMatchesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<string> normalizedTerms,
        IReadOnlyList<string> referenceKeys,
        string? category,
        Guid? docId,
        string? docPath,
        int topK,
        CancellationToken ct)
    {
        if (topK <= 0)
            return [];

        const string sql = """
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc"
FROM documents d
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY d.updated_at DESC
LIMIT 500;
""";

        var rows = await conn.QueryAsync<MetadataReferenceRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            category,
            doc_id = docId,
            doc_path = docPath
        }, cancellationToken: ct));

        return rows
            .Select(row => BuildMetadataReferenceMatch(row, normalizedTerms, referenceKeys))
            .Where(static match => match is not null)
            .Select(static match => match!)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
    }

    private static RagMatch? BuildMetadataReferenceMatch(
        MetadataReferenceRow row,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> queryReferenceKeys)
    {
        var metadataText = $"{row.DocName} {row.DocPath}";
        var metadataTerms = ExactMatchEntryExtractor.ExtractLookupTerms(metadataText);
        var metadataKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(metadataText);
        var metadataReferenceTerms = ExactMatchEntryExtractor.ExtractTargetedReferences(metadataText)
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var queryReferenceTerms = queryTerms
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var exactReferenceMatches = queryReferenceTerms
            .Intersect(metadataReferenceTerms, StringComparer.Ordinal)
            .ToArray();
        var keyBackedReferenceMatches = metadataReferenceTerms
            .Where(term => QueryKeysFullyMatchTerm(queryReferenceKeys, term))
            .ToArray();
        var genericDirectMatches = queryTerms
            .Intersect(metadataTerms, StringComparer.Ordinal)
            .Except(exactReferenceMatches, StringComparer.Ordinal)
            .ToArray();
        var keyMatches = queryReferenceKeys
            .Intersect(metadataKeys, StringComparer.Ordinal)
            .ToArray();

        if (exactReferenceMatches.Length == 0
            && keyBackedReferenceMatches.Length == 0
            && genericDirectMatches.Length == 0
            && keyMatches.Length == 0)
            return null;

        var score = ComputeMetadataReferenceScore(
            exactReferenceMatches.Length,
            keyBackedReferenceMatches.Length,
            genericDirectMatches.Length,
            keyMatches.Length);
        var strongestReference = exactReferenceMatches.FirstOrDefault()
            ?? keyBackedReferenceMatches.FirstOrDefault();
        var text = strongestReference is not null
            ? $"{row.DocName} [{strongestReference}]"
            : keyMatches.Length > 0
                ? $"{row.DocName} [{string.Join(", ", keyMatches)}]"
                : row.DocName;

        return new RagMatch(
            Score: score,
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            PageStart: null,
            PageEnd: null,
            ChunkId: $"docmeta:{row.DocId}",
            ChunkIndex: -1,
            Text: text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "document_metadata_ref",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
    }

    internal static double ComputeMetadataReferenceScore(int exactReferenceMatches, int keyBackedReferenceMatches, int genericDirectMatches, int keyMatches)
    {
        var score = 0.90;
        if (exactReferenceMatches > 0)
            score += 0.09 + Math.Min(0.01, exactReferenceMatches * 0.004);
        else if (keyBackedReferenceMatches > 0)
            score += 0.07 + Math.Min(0.01, keyBackedReferenceMatches * 0.004);
        else if (keyMatches > 0)
            score += 0.05 + Math.Min(0.01, keyMatches * 0.004);

        if (genericDirectMatches > 0)
            score += Math.Min(0.01, genericDirectMatches * 0.0025);

        return Math.Min(1.01, score);
    }

    private static bool QueryKeysFullyMatchTerm(IReadOnlyList<string> queryReferenceKeys, string metadataReferenceTerm)
    {
        if (queryReferenceKeys.Count == 0 || string.IsNullOrWhiteSpace(metadataReferenceTerm))
            return false;

        return queryReferenceKeys.All(key =>
            metadataReferenceTerm.Contains(key, StringComparison.Ordinal));
    }

    private static bool IsReferenceLikeLookupTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        return term.Length >= 6 && term.Any(char.IsDigit);
    }

    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit,
        string? docPath)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        docPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(docPath))
            must.Add(new { key = "doc_path", match = new { value = docPath } });

        var body = new
        {
            limit = Math.Clamp(limit ?? 20, 1, 200),
            with_payload = true,
            filter = new
            {
                must = must.ToArray()
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            content,
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }
}

public sealed record RagQueryRequest(string Query, string? Category, int? TopK);

public sealed record RagSearchRequest(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null
);

    public sealed record RagSearchTimings(long TotalMs, long TeiMs, long RerankMs, long SparseMs, long QdrantMs);

public sealed record RagSearchResponse(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    int QdrantStatus,
    RagSearchTimings Timings,
    IReadOnlyList<RagMatch> Matches
);
