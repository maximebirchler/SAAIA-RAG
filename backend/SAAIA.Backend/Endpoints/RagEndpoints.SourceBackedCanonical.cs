using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
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
    private static async Task<RagSearchResponse> SearchSourceBackedCanonicalCoreAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        var teiGovernor = ctx.RequestServices.GetService<TeiWorkloadGovernor>();
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

        static async Task<(List<RagMatch> Matches, long DurationMs)> MeasureAsync(
            Func<Task<List<RagMatch>>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            var matches = await action().ConfigureAwait(false);
            stopwatch.Stop();
            return (matches, stopwatch.ElapsedMilliseconds);
        }

        long teiMs = 0;
        long sparseMs = 0;
        long qdrantMs = 0;
        var qdrantStatus = 0;
        var totalStopwatch = Stopwatch.StartNew();
        using var searchActivity = RetrievalTelemetry.StartSearchActivity(
            "source_backed_canonical",
            hasCategoryFilter,
            hasDocScope,
            requestedTopK,
            candidateCount,
            query);

        var exactTask = MeasureAsync(() => SearchSourceBackedCanonicalExactMatchesAsync(
            ds,
            tenantId,
            query,
            category,
            req.DocId,
            req.DocPath,
            candidateCount,
            ct,
            categoryPath));
        var sparseTask = MeasureAsync(() => SearchSourceBackedCanonicalSparseMatchesAsync(
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
        var denseTask = MeasureAsync(() => SearchDenseMatchesAsync(
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
        exact = FilterCanonicalPageWindow(
            exact,
            requestedPageStart,
            requestedPageEnd);
        sparse = FilterCanonicalPageWindow(
            sparse,
            requestedPageStart,
            requestedPageEnd);
        dense = FilterCanonicalPageWindow(
            dense,
            requestedPageStart,
            requestedPageEnd);

        var diagnostics = req.IncludeDiagnostics == true
            ? new RagRetrievalDiagnosticsBuilder(
                query,
                query,
                query,
                "source_backed_canonical",
                responseCategory,
                categoryPath,
                requestedTopK,
                candidateCount,
                maxPerDoc,
                maxPerPage)
            : null;
        diagnostics?.CapturePhase(
            "canonical_exact",
            "exact_match",
            (await exactTask).DurationMs,
            exact);
        diagnostics?.CapturePhase(
            "canonical_sparse",
            "sparse_bm25",
            (await sparseTask).DurationMs,
            sparse);
        diagnostics?.CapturePhase(
            "canonical_dense",
            "dense_qdrant",
            (await denseTask).DurationMs,
            dense);

        var fusionStopwatch = Stopwatch.StartNew();
        var fused = FuseWithRrf(exact, sparse, dense);
        fusionStopwatch.Stop();
        diagnostics?.CapturePhase(
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
        diagnostics?.CapturePhase(
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
            maxPerPage);
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
        diagnostics?.CapturePhase(
            "canonical_selected",
            "mechanical_selection",
            selectionStopwatch.ElapsedMilliseconds,
            selected);

        var response = new RagSearchResponse(
            RequestId: ctx.GetRequestId(),
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
            Diagnostics: diagnostics?.Build(selected));

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
        int? pageEnd)
    {
        if (!pageStart.HasValue || !pageEnd.HasValue)
            return matches.ToList();

        return matches
            .Where(match =>
                match.PageStart is { } matchStart
                && match.PageEnd is { } matchEnd
                && matchEnd >= pageStart.Value
                && matchStart <= pageEnd.Value)
            .ToList();
    }

    internal static List<RagMatch> SelectCanonicalMatches(
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage)
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
                break;
            if (match.Score < minScore)
                continue;

            var documentKey = NormalizeRagDocPath(match.DocPath);
            if (string.IsNullOrWhiteSpace(documentKey))
                documentKey = match.DocId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(documentKey))
                continue;

            var identityKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(identityKey))
                continue;

            perDocument.TryGetValue(documentKey, out var documentCount);
            if (documentCount >= maxPerDoc)
            {
                selectedKeys.Remove(identityKey);
                continue;
            }

            var pageKey =
                $"{documentKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCount);
            if (pageCount >= maxPerPage)
            {
                selectedKeys.Remove(identityKey);
                continue;
            }

            selected.Add(match);
            perDocument[documentKey] = documentCount + 1;
            perPage[pageKey] = pageCount + 1;
        }

        return selected;
    }
}
