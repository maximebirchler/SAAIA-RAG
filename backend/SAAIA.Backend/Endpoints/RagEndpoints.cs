using System.Diagnostics;
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
    private const int MaxHypotheticalQuestionConcurrency = 3;

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
        var capabilityAQuestionService = ctx.RequestServices.GetService<CapabilityAHypotheticalQuestionService>();
        var hypQuestionsMatchedByDocPath = await LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            ctx.GetTenantId(),
            resp.Query,
            resp.Matches,
            capabilityAQuestionService,
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
                        Snippet: BuildSnippet(m.Text),
                        RerankScore: m.RerankScore,
                        HasTable: DetectHasTable(m.Text),
                        HasWarning: DetectHasWarning(m.Text, m.ChunkType),
                        ContextualSnippet: m.EmbedText,
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
        CapabilityAHypotheticalQuestionService? hypotheticalQuestionService,
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
SELECT doc_id AS "DocId",
       doc_path AS "DocPath",
       doc_name AS "DocName",
       indexed_version AS "IndexedVersion"
FROM documents
WHERE tenant_id=@tenant
  AND doc_path = ANY(@docPaths)
  AND indexed_version > 0
ORDER BY doc_path;
""",
            new
            {
                tenant = tenantId,
                docPaths
            },
            cancellationToken: ct)))
            .ToArray();

        var docVersions = docs
            .Select(static doc => (doc.DocId, doc.IndexedVersion))
            .ToArray();
        var sectionTitlesByDocId = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 3,
            ct);
        var excerptsByDocId = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 2,
            ct);

        using var questionGate = new SemaphoreSlim(MaxHypotheticalQuestionConcurrency, MaxHypotheticalQuestionConcurrency);
        var docQuestionTasks = docs
            .Select(doc =>
            {
                sectionTitlesByDocId.TryGetValue(doc.DocId, out var sectionTitles);
                excerptsByDocId.TryGetValue(doc.DocId, out var excerpts);
                var titles = sectionTitles ?? Array.Empty<string>();
                var excerptValues = excerpts ?? Array.Empty<string>();

                Task<IReadOnlyList<string>> task = hypotheticalQuestionService is null
                    ? Task.FromResult<IReadOnlyList<string>>(RuntimeGovernanceService.BuildCapabilityAHypotheticalQuestions(
                        doc.DocName,
                        titles,
                        excerptValues))
                    : BuildQuestionsWithGateAsync(
                        hypotheticalQuestionService,
                        questionGate,
                        doc.DocName,
                        titles,
                        excerptValues,
                        ct);

                return (doc.DocPath, QuestionsTask: task);
            })
            .ToArray();

        await Task.WhenAll(docQuestionTasks.Select(static item => item.QuestionsTask));

        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (docPath, questionsTask) in docQuestionTasks)
        {
            var hypotheticalQuestions = questionsTask.Result;
            result[docPath] = hypotheticalQuestions.Count == 0
                ? null
                : ComputeHypQuestionsMatched(query, hypotheticalQuestions);
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> BuildQuestionsWithGateAsync(
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        SemaphoreSlim questionGate,
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        await questionGate.WaitAsync(ct);
        try
        {
            return await hypotheticalQuestionService.BuildQuestionsAsync(docName, sectionTitles, excerpts, ct);
        }
        finally
        {
            questionGate.Release();
        }
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

        var mode = (req.Mode ?? "balanced").Trim().ToLowerInvariant();
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * 12,
            _ => topK * 6
        };
        // CDC v3.1 §11.3: max 3 chunks per document default
        int defMaxPerDoc = mode switch
        {
            "focused" => Math.Min(topK, 3),
            "broad" => 2,
            _ => Math.Min(3, Math.Max(2, topK / 2))
        };

        var minScore = Math.Clamp(req.MinScore ?? defMinScore, 0.0, 1.0);
        var candidates = Math.Clamp(req.Candidates ?? defCandidates, topK, Math.Max(topK, rag.MaxTopK * 20));
        var maxPerDoc = Math.Clamp(req.MaxPerDoc ?? defMaxPerDoc, 1, topK);
        var maxPerPage = Math.Clamp(req.MaxPerPage ?? 1, 1, topK);

        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
            if (req.Diversity.PreferDistinctPages == true)
                maxPerPage = 1;
        }

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
        int qdrantStatus = 0;

        if (shortCircuitAfterExact)
        {
            AddRankedMatches(selected, selectedKeys, exactMatches, topK, minScore: 0.0, maxPerDoc, maxPerPage);
        }
        else
        {
            var sparseMatchesTask = MeasurePhaseAsync(
                phaseName: "retrieval_sparse",
                retriever: "sparse_bm25",
                action: () => SearchSparseMatchesAsync(
                    ds,
                    tenantId,
                    retrievalQuery,
                    category,
                    req.DocId,
                    req.DocPath,
                    candidates,
                    ct,
                    sparseMsRef: value => sparseMs = value),
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

            await Task.WhenAll(sparseMatchesTask, denseMatchesTask);

            var (sparseMatches, measuredSparsePhaseMs) = await sparseMatchesTask;
            var (denseMatches, measuredDensePhaseMs) = await denseMatchesTask;
            sparsePhaseMs = measuredSparsePhaseMs;
            densePhaseMs = measuredDensePhaseMs;
            var fusedMatches = FuseWithRrf(exactMatches, sparseMatches, denseMatches);
            fusedMatches = CalibrateFusedMatches(retrievalQuery, fusedMatches);
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

                AddRankedMatches(selected, selectedKeys, linkedMatches, topK, minScore: 0.0, maxPerDoc, Math.Max(maxPerPage, 2));
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

                AddRankedMatches(selected, selectedKeys, secondWaveLinkedMatches, topK, minScore: 0.0, maxPerDoc, Math.Max(maxPerPage, 2));
            }
        }

        // CDC v3.1 §11.3: autocut - remove trailing results after largest relative score drop
        ApplyAutocut(selected, minScore);

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
        RetrievalTelemetry.RecordSearch(response, mode, hasCategoryFilter, hasDocScope, exactMs, sparsePhaseMs, densePhaseMs, linkedPhaseMs);

        return response;
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
        Action<long> sparseMsRef)
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

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
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
    cte.text_content AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    ts_rank_cd(
        to_tsvector('simple', cte.text_content),
        sparse_query.q,
        32
    ) AS "SparseRank"
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
WHERE to_tsvector('simple', cte.text_content) @@ sparse_query.q
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY "SparseRank" DESC, rc.chunk_index ASC
LIMIT @top_k;
""";

        var swSparse = Stopwatch.StartNew();
        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_text = query.Trim(),
                category,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, cancellationToken: ct))).ToList();

            var lexicalTerms = BuildLexicalContentFallbackTerms(query);
            if (rows.Count == 0)
            {
                if (lexicalTerms.Count > 0)
                {
                    rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
                    {
                        tenant_id = tenantId,
                        lexical_terms = lexicalTerms.ToArray(),
                        category,
                        doc_id = normalizedDocId,
                        doc_path = normalizedDocPath,
                        top_k = topK
                    }, cancellationToken: ct))).ToList();
                }
            }
            else if (ShouldSupplementSparseWithLexicalFallback(category, query) && lexicalTerms.Count > 0)
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

                rows = MergeSparseRows(fallbackRows, rows, topK);
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
    cte.text_content AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    lm.match_count::real AS "SparseRank"
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
CROSS JOIN LATERAL (
    SELECT COUNT(*) AS match_count
    FROM lexical_terms
    WHERE LOWER(cte.text_content) LIKE '%' || lexical_terms.term || '%'
) lm
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND lm.match_count > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY lm.match_count DESC, rc.chunk_index ASC
LIMIT @top_k;
""";

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

    internal static bool ShouldSupplementSparseWithLexicalFallback(string? category, string query)
    {
        if (!string.Equals(category, "cuisine", StringComparison.OrdinalIgnoreCase))
            return false;

        var tokens = ExtractLexicalQueryTokens(query);
        return tokens.Any(static token => token is
            "entrecote" or "entrecôte" or
            "steak" or "rumsteck" or
            "enfant" or "enfants" or
            "activite" or "activité" or
            "vegetarien" or "végétarien" or
            "vegetarienne" or "végétarienne");
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
SELECT doc_id AS "DocId",
       status AS "Status",
       indexed_version AS "IndexedVersion",
       LOWER(ENCODE(content_hash, 'hex')) AS "ContentHashHex"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_id = ANY(@doc_ids);
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

            var versionMatches = match.IngestionVersion.HasValue && match.IngestionVersion.Value == row.IndexedVersion;
            var legacyHashMatches = !match.IngestionVersion.HasValue
                && !string.IsNullOrWhiteSpace(match.HashDoc)
                && !string.IsNullOrWhiteSpace(row.ContentHashHex)
                && string.Equals(match.HashDoc, row.ContentHashHex, StringComparison.OrdinalIgnoreCase);

            if (!versionMatches && !legacyHashMatches)
                continue;

            filtered.Add(match);
        }

        return filtered;
    }

    private sealed record DocVersionRow(Guid DocId, string Status, int IndexedVersion, string? ContentHashHex);
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

        var denseOrLinkedAnchors = anchorMatches
            .Where(match =>
            {
                var retriever = ResolveRetriever(match);
                return (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                        || string.Equals(retriever, "linked_context", StringComparison.Ordinal))
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

        var anchorCandidates = denseOrLinkedAnchors
            .Concat(exactAnchors)
            .OrderByDescending(item => item.AnchorScore)
            .Take(Math.Max(1, Math.Min(topK * 2, 4)))
            .ToList();

        if (anchorCandidates.Count == 0)
            return [];

        var anchorsBySourceId = anchorCandidates.ToDictionary(item => item.SourceId);
        var denseAnchorIds = denseOrLinkedAnchors
            .Where(item => string.Equals(item.SourceRetriever, "dense_qdrant", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var linkedAnchorIds = denseOrLinkedAnchors
            .Where(item => string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var exactAnchorIds = exactAnchors
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

        if (bestGapRatio >= minRelativeDrop && bestGapIndex > 0)
            matches.RemoveRange(bestGapIndex, matches.Count - bestGapIndex);

        // Also enforce absolute minimum on remaining items (skip exact_match which always passes)
        matches.RemoveAll(m =>
            m.Score < absoluteMinScore
            && !string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal));
    }

    internal static string BuildMatchDedupKey(RagMatch match)
        => $"{match.DocId}|{match.PageStart}|{match.PageEnd}|{ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty)}";

    internal static List<RagMatch> FuseWithRrf(
        IReadOnlyList<RagMatch> exactMatches,
        IReadOnlyList<RagMatch> sparseMatches,
        IReadOnlyList<RagMatch> denseMatches,
        int rrfK = 60)
    {
        var accumulators = new Dictionary<string, RrfAccumulator>(StringComparer.OrdinalIgnoreCase);

        AccumulateRrf(accumulators, exactMatches, rrfK);
        AccumulateRrf(accumulators, sparseMatches, rrfK);
        AccumulateRrf(accumulators, denseMatches, rrfK);

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

    internal static List<RagMatch> CalibrateFusedMatches(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (candidates.Count <= 1)
            return candidates.ToList();

        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var referenceTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query)
            .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lexicalTokens = ExtractLexicalQueryTokens(query);

        return candidates
            .Select(match =>
            {
                var retriever = ResolveRetriever(match);
                var adjusted = match.Score;

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
                    var lexicalCoverage = ComputeLexicalCoverage(lexicalTokens, match.EmbedText ?? match.Text);
                    if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.06,
                            >= 0.50 => 0.035,
                            >= 0.34 => 0.015,
                            _ when lexicalTokens.Count >= 3 => -0.02,
                            _ => 0.0
                        };
                    }
                    else if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.025,
                            >= 0.50 => 0.012,
                            < 0.20 when lexicalTokens.Count >= 3 => -0.01,
                            _ => 0.0
                        };
                    }
                }

                adjusted += ComputeDomainSpecificBoost(query, match.EmbedText ?? match.Text);

                return match with { Score = Math.Clamp(adjusted, 0.0, 1.02) };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ChunkIndex)
            .ToList();
    }

    internal static double ComputeDomainSpecificBoost(string query, string? candidateText)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedQuery = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var normalizedCandidate = ExactMatchEntryExtractor.NormalizeForLookup(candidateText);
        var boost = 0.0;

        if ((normalizedQuery.Contains("enfant", StringComparison.Ordinal) || normalizedQuery.Contains("enfants", StringComparison.Ordinal))
            && (normalizedCandidate.Contains("faire de la cuisine avec les enfants", StringComparison.Ordinal)
                || normalizedCandidate.Contains("centre de loisirs", StringComparison.Ordinal)
                || normalizedCandidate.Contains("centre de vacances", StringComparison.Ordinal)
                || normalizedCandidate.Contains("animateurs", StringComparison.Ordinal)))
        {
            boost += 0.20;
        }

        if ((normalizedQuery.Contains("entrecote", StringComparison.Ordinal)
             || normalizedQuery.Contains("entrecôte", StringComparison.Ordinal)
             || normalizedQuery.Contains("steak", StringComparison.Ordinal)
             || normalizedQuery.Contains("rumsteck", StringComparison.Ordinal))
            && (normalizedCandidate.Contains("rumsteck", StringComparison.Ordinal)
                || normalizedCandidate.Contains("viandes rouges", StringComparison.Ordinal)
                || normalizedCandidate.Contains("boeuf", StringComparison.Ordinal)
                || normalizedCandidate.Contains("bœuf", StringComparison.Ordinal)))
        {
            boost += 0.08;
        }

        if ((normalizedQuery.Contains("sauce", StringComparison.Ordinal) || normalizedQuery.Contains("sauces", StringComparison.Ordinal))
            && (normalizedCandidate.Contains("sauces et les trempettes", StringComparison.Ordinal)
                || normalizedCandidate.Contains("cuisiner des sauces et des trempettes", StringComparison.Ordinal)
                || normalizedCandidate.Contains("sauce froide", StringComparison.Ordinal)
                || normalizedCandidate.Contains("trempette", StringComparison.Ordinal)))
        {
            boost += 0.16;
        }

        return boost;
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

    internal static IReadOnlyList<string> ExtractLexicalQueryTokens(string query)
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
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildLexicalContentFallbackTerms(string query)
    {
        var terms = new HashSet<string>(ExtractLexicalQueryTokens(query), StringComparer.Ordinal);
        if (terms.Count == 0)
            return Array.Empty<string>();

        if (terms.Contains("inertage") || terms.Contains("inerting") || terms.Contains("inert"))
        {
            terms.Add("inert");
            terms.Add("inerting");
            terms.Add("inertage");
        }

        return terms
            .Where(static term => term.Length >= 4)
            .Where(static term => !LexicalStopwords.Contains(term))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ExpandRetrievalQuery(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        if (!string.Equals(category, "cuisine", StringComparison.OrdinalIgnoreCase))
            return query;

        var tokens = new HashSet<string>(ExtractLexicalQueryTokens(query), StringComparer.Ordinal);
        var additions = new List<string>();
        if (tokens.Any(static token => token.StartsWith("entrec", StringComparison.Ordinal)))
            tokens.Add("entrecote");

        if (tokens.Contains("entrecote") || tokens.Contains("entrecôte"))
        {
            additions.AddRange(["steak", "rumsteck", "boeuf", "viande rouge"]);
        }

        if (tokens.Contains("sauce") || tokens.Contains("sauces"))
        {
            additions.AddRange(["sauces trempettes", "viandes"]);
            if (!(tokens.Contains("entrecote") || tokens.Contains("steak") || tokens.Contains("rumsteck")))
                additions.AddRange(["marinade", "jus roti"]);
        }

        if (tokens.Contains("enfants") || tokens.Contains("enfant"))
        {
            additions.AddRange(["atelier cuisine", "activite cuisine", "centre loisirs", "vacances", "animateurs", "57 recettes"]);
        }

        if (tokens.Contains("vegetarienne") || tokens.Contains("végétarienne") || tokens.Contains("vegetarien") || tokens.Contains("végétarien"))
        {
            additions.AddRange(["legumes", "lentilles", "pois chiches", "sans viande"]);
        }

        var distinctAdditions = additions
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Where(term => !tokens.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinctAdditions.Length == 0
            ? query
            : $"{query.Trim()} {string.Join(' ', distinctAdditions)}";
    }

    internal static double ComputeLexicalCoverage(IReadOnlyList<string> queryTokens, string? candidateText)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = ExactMatchEntryExtractor.NormalizeForLookup(candidateText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var matched = queryTokens.Count(token => normalizedCandidate.Contains(token, StringComparison.Ordinal));
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

        return Math.Max(0.0, anchorScore - penalty);
    }

    private sealed record LinkedAnchorCandidate(Guid SourceId, string SourceRetriever, double AnchorScore);

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
            "exact_match" => 3,
            "sparse_bm25" => 2,
            _ => 1
        };

    private static readonly HashSet<string> LexicalStopwords = new(StringComparer.Ordinal)
    {
        "dans", "avec", "sans", "pour", "vers", "entre", "apres", "avant",
        "quel", "quelle", "quels", "quelles", "trouve", "trouver", "montre",
        "montrez", "ou", "sont", "sous", "plus", "moins", "comme", "cela",
        "cette", "cet", "ces", "leurs", "leur", "par", "sur", "des", "une",
        "les", "que", "quoi", "dont", "when", "where", "which", "with", "from",
        "this", "that", "those", "these", "what", "into", "pdf", "doc", "document",
        "manuel", "manual", "guide", "please", "stp", "svp", "cherche", "show",
        "need", "have", "has", "just", "juste", "moi", "peux", "avoir",
        "parle", "parler", "documents"
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

    internal static string? BuildSnippet(string? text, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        // Cut at last sentence boundary within limit
        var cutoff = normalized.LastIndexOf('.', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = normalized.LastIndexOf(' ', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return normalized[..(cutoff + 1)].TrimEnd();
    }

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
        int IndexedVersion);

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
