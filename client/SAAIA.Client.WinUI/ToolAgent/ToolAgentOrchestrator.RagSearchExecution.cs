using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var sourceBackedCanonical = GetRagSourceBackedCanonicalArg(args);
        var disableAutomaticCategoryScoping =
            GetRagDisableAutomaticCategoryScopingArg(args);
        var rawQuery = args.GetProperty("query").GetString() ?? "";
        var query = sourceBackedCanonical
            ? CollapseWhitespace(rawQuery)
            : ResolveRagSearchExecutionQuery(rawQuery);
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryScope = GetRagCategoryScopeArg(args);
        var docId = GetRagDocIdArg(args);
        var docPath = GetRagDocPathArg(args);
        var maxPerDoc = GetRagMaxPerDocArg(args);
        var maxPerPage = GetRagMaxPerPageArg(args);
        var pageStart = GetRagPageStartArg(args);
        var pageEnd = GetRagPageEndArg(args);
        var researchMode = GetRagResearchModeArg(args);
        var includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args);
        var trustCategoryScope = GetRagTrustCategoryScopeArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";
        var sourceExplorationTimeout = ResolveRagSourceExplorationQueryTimeout(
            researchMode,
            includeResearchSurfaces == true);
        var requestedCategoryScope = categoryScope;
        string? rejectedCategoryScope = null;
        string? rejectedCategoryScopeReason = null;
        if (!sourceBackedCanonical && !disableAutomaticCategoryScoping)
        {
            categoryScope = ResolveTrustedRagSourceExplorationCategoryScope(
                categoryScope,
                new[] { query },
                docId,
                docPath,
                pageStart,
                pageEnd,
                researchMode,
                includeResearchSurfaces == true,
                trustCategoryScope,
                out rejectedCategoryScope,
                out rejectedCategoryScopeReason);
        }
        if (!string.IsNullOrWhiteSpace(rejectedCategoryScope))
        {
            ClientLog.Info(
                "ToolAgent rag.search category scope rejected: " +
                $"requested={FormatRagTraceValue(rejectedCategoryScope)}|reason={FormatRagTraceValue(rejectedCategoryScopeReason)}|" +
                $"query={FormatRagTraceValue(query, 180)}");
            EmitRagTrace(
                "rag.search.scope.rejected",
                ("requested_category", rejectedCategoryScope),
                ("reason", rejectedCategoryScopeReason),
                ("query", query));
        }
        var sw = Stopwatch.StartNew();
        EmitRagTrace(
            "rag.search.start",
            ("query", query),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope),
            ("requested_category", requestedCategoryScope),
            ("rejected_category", rejectedCategoryScope),
            ("doc_id", docId),
            ("doc_path", docPath),
            ("page_start", pageStart),
            ("page_end", pageEnd),
            ("research_mode", researchMode),
            ("include_research_surfaces", includeResearchSurfaces),
            ("trust_category_scope", trustCategoryScope),
            ("source_backed_canonical", sourceBackedCanonical),
            ("disable_automatic_category_scoping", disableAutomaticCategoryScoping),
            ("timeout_ms", sourceExplorationTimeout.HasValue
                ? (int)Math.Round(sourceExplorationTimeout.Value.TotalMilliseconds)
                : (int?)null));

        if (!sourceBackedCanonical && LooksLikeComparativeDocumentaryRequest(query))
        {
            EmitRagTrace(
                "rag.search.redirect",
                ("reason", "comparative_documentary_request"),
                ("target", "rag.multi_search"),
                ("query", query));
            var multiArgs = CreateJsonArgs(new
            {
                queries = BuildComparativeRetrievalQueries(query),
                topK,
                categoryPath = categoryScope,
                docId,
                docPath,
                maxPerDoc,
                maxPerPage,
                pageStart,
                pageEnd,
                mode,
                researchMode,
                includeResearchSurfaces
            });
            return await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
        }

        JsonElement raw;
        try
        {
            using var queryTimeoutCts = sourceExplorationTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (sourceExplorationTimeout.HasValue)
                queryTimeoutCts!.CancelAfter(sourceExplorationTimeout.Value);

            raw = await _api.RagSearchToolAsync(
                query,
                topK,
                categoryScope,
                mode,
                queryTimeoutCts?.Token ?? ct,
                docId,
                docPath,
                maxPerDoc,
                maxPerPage,
                pageStart,
                pageEnd,
                researchMode,
                includeResearchSurfaces,
                sourceBackedCanonical).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && sourceExplorationTimeout.HasValue)
        {
            var timeout = BuildRagSearchQueryTimeoutPayload(
                query,
                categoryScope,
                mode,
                sourceExplorationTimeout.Value);
            RememberLastRagDiagnostics(new[] { query }, timeout);
            EmitRagTrace(
                "rag.search.end",
                ("query", query),
                ("hits", 0),
                ("busy", false),
                ("error", "rag_search_query_timeout"),
                ("timeout_ms", (int)Math.Round(sourceExplorationTimeout.Value.TotalMilliseconds)),
                ("ms", sw.ElapsedMilliseconds));
            return timeout;
        }
        catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
        {
            var busy = BuildRagSearchBusyPayload(new[] { query }, categoryScope, mode, ex);
            RememberLastRagDiagnostics(new[] { query }, busy);
            EmitRagTrace(
                "rag.search.end",
                ("query", query),
                ("hits", 0),
                ("busy", true),
                ("error", "rag_search_busy"),
                ("retry_after_seconds", ex.RetryAfterSeconds),
                ("ms", sw.ElapsedMilliseconds));
            return busy;
        }

        EmitRagSearchBackendMetrics(
            raw,
            query,
            sw.ElapsedMilliseconds);
        var normalized = NormalizeRagHits(raw, sourceBackedCanonical);
        if (sourceBackedCanonical)
        {
            normalized = await EnrichSourceBackedEvidenceWindowsAsync(
                    normalized,
                    ct)
                .ConfigureAwait(false);
        }
        RememberLastRagDiagnostics(new[] { query }, normalized);
        EmitRagTrace(
            "rag.search.end",
            ("query", query),
            ("hits", CountRagHits(normalized)),
            ("busy", IsRagSearchBusyPayload(normalized)),
            ("error", TryGetString(normalized, "error")),
            ("ms", sw.ElapsedMilliseconds));
        return normalized;
    }

    private void EmitRagSearchBackendMetrics(
        JsonElement raw,
        string query,
        long clientElapsedMilliseconds)
    {
        var timings = TryGetObject(raw, "timings")
                      ?? TryGetObject(raw, "metrics");
        var degradedRetrievers = (TryGetArray(raw, "degradedRetrievers")
                                  ?? (timings is { } metrics
                                      ? TryGetArray(
                                          metrics,
                                          "degradedRetrievers")
                                      : null))
            ?.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        EmitRagTrace(
            "rag.search.backend_metrics",
            ("query", query),
            ("available", timings is not null),
            ("client_elapsed_ms", clientElapsedMilliseconds),
            ("total_ms", timings is { } values
                ? TryGetLong(values, "totalMs")
                  ?? TryGetLong(values, "tookMs")
                : null),
            ("exact_ms", timings is { } exact
                ? TryGetLong(exact, "exactMs")
                : null),
            ("sparse_phase_ms", timings is { } sparsePhase
                ? TryGetLong(sparsePhase, "sparsePhaseMs")
                : null),
            ("sparse_service_ms", timings is { } sparseService
                ? TryGetLong(sparseService, "sparseMs")
                : null),
            ("dense_phase_ms", timings is { } dense
                ? TryGetLong(dense, "denseMs")
                : null),
            ("tei_ms", timings is { } tei
                ? TryGetLong(tei, "teiMs")
                : null),
            ("qdrant_ms", timings is { } qdrant
                ? TryGetLong(qdrant, "qdrantMs")
                : null),
            ("fusion_ms", timings is { } fusion
                ? TryGetLong(fusion, "fusionMs")
                : null),
            ("rerank_phase_ms", timings is { } rerankPhase
                ? TryGetLong(rerankPhase, "rerankPhaseMs")
                : null),
            ("rerank_service_ms", timings is { } rerankService
                ? TryGetLong(rerankService, "rerankMs")
                : null),
            ("selection_ms", timings is { } selection
                ? TryGetLong(selection, "selectionMs")
                : null),
            ("degraded_retrievers", degradedRetrievers));
    }

    private static string ResolveRagSearchExecutionQuery(string? rawQuery)
    {
        var raw = CollapseWhitespace(rawQuery ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (TryExtractDelimitedUserDemandTopic(raw, out _))
            return raw;

        var normalized = NormalizeRagQueryForRetrieval(raw);
        return string.IsNullOrWhiteSpace(normalized) ? raw : normalized;
    }

    private async Task<JsonElement> ExecRagSearchRawAsync(string query, int topK, string? categoryScope, string? mode, CancellationToken ct)
    {
        query = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(query))
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        var sw = Stopwatch.StartNew();
        EmitRagTrace(
            "rag.search.raw.start",
            ("query", query),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope));
        JsonElement raw;
        try
        {
            raw = await _api.RagSearchToolAsync(query, topK, categoryScope, mode, ct).ConfigureAwait(false);
        }
        catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
        {
            var busy = BuildRagSearchBusyPayload(new[] { query }, categoryScope, mode, ex);
            RememberLastRagDiagnostics(new[] { query }, busy);
            EmitRagTrace(
                "rag.search.raw.end",
                ("query", query),
                ("hits", 0),
                ("busy", true),
                ("error", "rag_search_busy"),
                ("retry_after_seconds", ex.RetryAfterSeconds),
                ("ms", sw.ElapsedMilliseconds));
            return busy;
        }

        var normalized = NormalizeRagHits(raw);
        RememberLastRagDiagnostics(new[] { query }, normalized);
        EmitRagTrace(
            "rag.search.raw.end",
            ("query", query),
            ("hits", CountRagHits(normalized)),
            ("busy", IsRagSearchBusyPayload(normalized)),
            ("error", TryGetString(normalized, "error")),
            ("ms", sw.ElapsedMilliseconds));
        return normalized;
    }

    private static JsonElement BuildRagSearchBusyPayload(
        IEnumerable<string> queries,
        string? categoryScope,
        string? mode,
        ApiClientBackendBusyException ex)
    {
        var payload = new
        {
            hits = Array.Empty<object>(),
            error = "rag_search_busy",
            busy = true,
            retryAfterSeconds = ex.RetryAfterSeconds,
            guidance = new
            {
                behavior = "retry_later",
                qualificationNote = "RAG search is temporarily busy; ask the user to retry shortly instead of claiming no documents were found."
            },
            meta = new
            {
                queries = queries.Where(static q => !string.IsNullOrWhiteSpace(q)).Take(8).ToArray(),
                mode = string.IsNullOrWhiteSpace(mode) ? "balanced" : mode,
                category = categoryScope,
                categoryPath = categoryScope,
                degradedRetrievers = new[] { "rag_search_busy" }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildRagSearchQueryTimeoutPayload(
        string query,
        string? categoryScope,
        string? mode,
        TimeSpan timeout)
    {
        var payload = new
        {
            hits = Array.Empty<object>(),
            error = "rag_search_query_timeout",
            busy = false,
            guidance = new
            {
                behavior = "continue_without_timed_out_query",
                qualificationNote = "One retrieval sub-query timed out; keep usable results from other queries and avoid treating the timeout as evidence absence."
            },
            meta = new
            {
                queries = new[] { query },
                mode = string.IsNullOrWhiteSpace(mode) ? "balanced" : mode,
                category = categoryScope,
                categoryPath = categoryScope,
                timeoutMs = (int)Math.Round(timeout.TotalMilliseconds),
                degradedRetrievers = new[] { "rag_search_query_timeout" }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private static TimeSpan? ResolveRagSourceExplorationQueryTimeout(
        string? researchMode,
        bool includeResearchSurfaces)
    {
        return IsRagSourceExploration(researchMode, includeResearchSurfaces)
            ? RagSourceExplorationQueryTimeout
            : null;
    }

    private static bool IsRagSourceExploration(string? researchMode, bool includeResearchSurfaces)
        => includeResearchSurfaces
           || string.Equals(researchMode, "source_exploration", StringComparison.OrdinalIgnoreCase);

    private static bool IsRagCategoryScopeSupportedByQueryTerms(string? categoryScope, IReadOnlyList<string> queries)
    {
        var normalizedScope = NormalizeLexicalLookup(categoryScope ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedScope))
            return true;

        var categoryTokens = Regex.Matches(normalizedScope, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakCategoryScopeToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categoryTokens.Length == 0)
            return false;

        var normalizedQueries = NormalizeLexicalLookup(string.Join(' ', queries));
        if (string.IsNullOrWhiteSpace(normalizedQueries))
            return false;

        return categoryTokens.Any(token => Regex.IsMatch(
            normalizedQueries,
            $@"(?:^|\s){Regex.Escape(token)}(?:\s|$)",
            RegexOptions.CultureInvariant));
    }

    private static bool IsWeakCategoryScopeToken(string token)
        => token is "pdf" or "doc" or "docs" or "document" or "documents" or "general" or "generale" or "misc" or "miscellaneous";

    private string? ResolveTrustedRagMultiSearchCategoryScope(
        string? categoryScope,
        IReadOnlyList<string> queries,
        string? docId,
        string? docPath,
        int? pageStart,
        int? pageEnd,
        string? researchMode,
        bool includeResearchSurfaces,
        bool trustCategoryScope,
        out string? rejectedCategoryScope,
        out string? rejectedCategoryScopeReason)
        => ResolveTrustedRagSourceExplorationCategoryScope(
            categoryScope,
            queries,
            docId,
            docPath,
            pageStart,
            pageEnd,
            researchMode,
            includeResearchSurfaces,
            trustCategoryScope,
            out rejectedCategoryScope,
            out rejectedCategoryScopeReason);

    private string? ResolveTrustedRagSourceExplorationCategoryScope(
        string? categoryScope,
        IReadOnlyList<string> queries,
        string? docId,
        string? docPath,
        int? pageStart,
        int? pageEnd,
        string? researchMode,
        bool includeResearchSurfaces,
        bool trustCategoryScope,
        out string? rejectedCategoryScope,
        out string? rejectedCategoryScopeReason)
    {
        rejectedCategoryScope = null;
        rejectedCategoryScopeReason = null;

        if (string.IsNullOrWhiteSpace(categoryScope)
            || !IsRagSourceExploration(researchMode, includeResearchSurfaces)
            || !string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath)
            || pageStart.HasValue
            || pageEnd.HasValue)
        {
            return categoryScope;
        }

        if (trustCategoryScope)
            return categoryScope;

        var resolvedCategoryScope = ResolveLlmPlannedRagCategoryScope(categoryScope);
        if (!string.IsNullOrWhiteSpace(resolvedCategoryScope))
            return resolvedCategoryScope;

        if (IsRagCategoryScopeSupportedByQueryTerms(categoryScope, queries))
        {
            rejectedCategoryScope = categoryScope;
            rejectedCategoryScopeReason = "source_exploration_scope_not_resolved";
            return null;
        }

        rejectedCategoryScope = categoryScope;
        rejectedCategoryScopeReason = "source_exploration_scope_not_supported_by_query";
        return null;
    }

    private string? TryRefineRagMultiSearchCategoryScopeFromPreviousInference(
        string? categoryScope,
        string? rejectedCategoryScope,
        string? docId,
        string? docPath,
        int? pageStart,
        int? pageEnd,
        string? researchMode,
        bool includeResearchSurfaces,
        out string? refinedFrom,
        out string? reason)
    {
        refinedFrom = null;
        reason = null;

        if (!IsRagSourceExploration(researchMode, includeResearchSurfaces)
            || !string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath)
            || pageStart.HasValue
            || pageEnd.HasValue)
        {
            return null;
        }

        var effectiveCategoryScope = NormalizeCategoryPathArg(categoryScope);
        var requestedCategoryScope = effectiveCategoryScope ?? NormalizeCategoryPathArg(rejectedCategoryScope);
        var inferredCategoryScope = NormalizeCategoryPathArg(_mem.Execution.LastRagInferredCategoryScope);
        if (string.IsNullOrWhiteSpace(requestedCategoryScope)
            || string.IsNullOrWhiteSpace(inferredCategoryScope))
        {
            return null;
        }

        if (string.Equals(effectiveCategoryScope, inferredCategoryScope, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(requestedCategoryScope, inferredCategoryScope, StringComparison.OrdinalIgnoreCase)
            || inferredCategoryScope.StartsWith(requestedCategoryScope + "/", StringComparison.OrdinalIgnoreCase))
        {
            refinedFrom = requestedCategoryScope;
            reason = _mem.Execution.LastRagInferredCategoryReason ?? "previous_inferred_category";
            return inferredCategoryScope;
        }

        return null;
    }

    private void RememberRagInferredCategoryScope(string? categoryScope, string reason)
    {
        var normalizedCategoryScope = NormalizeCategoryPathArg(categoryScope);
        if (string.IsNullOrWhiteSpace(normalizedCategoryScope))
            return;

        _mem.Execution.LastRagInferredCategoryScope = normalizedCategoryScope;
        _mem.Execution.LastRagInferredCategoryReason = reason;
        EmitRagTrace(
            "rag.multi_search.category_scope.remembered",
            ("category", normalizedCategoryScope),
            ("reason", reason));
    }

    private static string? TryGetRagMultiSearchResultCategoryPath(JsonElement result)
    {
        var meta = TryGetObject(result, "meta")
                   ?? TryGetObject(result, "Meta");
        if (!meta.HasValue)
            return null;

        return NormalizeCategoryPathArg(
            TryGetString(meta.Value, "categoryPath")
            ?? TryGetString(meta.Value, "CategoryPath")
            ?? TryGetString(meta.Value, "category")
            ?? TryGetString(meta.Value, "Category"));
    }

    private static string? ChooseCommittedSourceBackedCategoryScope(string? plannedScope, string? executedScope)
    {
        var planned = NormalizeCategoryPathArg(plannedScope);
        var executed = NormalizeCategoryPathArg(executedScope);
        if (string.IsNullOrWhiteSpace(executed))
            return planned;
        if (string.IsNullOrWhiteSpace(planned))
            return executed;
        if (string.Equals(planned, executed, StringComparison.OrdinalIgnoreCase)
            || executed.StartsWith(planned + "/", StringComparison.OrdinalIgnoreCase))
        {
            return executed;
        }

        return planned;
    }

    private async Task<JsonElement> TryExecRagSearchOrEmptyAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return await ExecRagSearchAsync(args, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private async Task<JsonElement> TryExecRagSearchRawOrEmptyAsync(string query, int topK, string? categoryScope, string? mode, CancellationToken ct)
    {
        try
        {
            return await ExecRagSearchRawAsync(query, topK, categoryScope, mode, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private async Task<JsonElement> TryExecRagMultiSearchOrEmptyAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

}
