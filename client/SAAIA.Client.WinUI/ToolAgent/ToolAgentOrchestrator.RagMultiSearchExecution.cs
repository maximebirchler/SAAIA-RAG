using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        if (GetRagSourceBackedCanonicalArg(args))
            return await ExecSourceBackedCanonicalMultiSearchAsync(args, ct).ConfigureAwait(false);

        var totalSw = Stopwatch.StartNew();
        // args: { queries: string[], topK: int, category: string|null, docId/docPath: string|null, mode: ... }
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
        var disableAutomaticCategoryScoping = GetRagDisableAutomaticCategoryScopingArg(args);
        var sourceBackedCanonical = GetRagSourceBackedCanonicalArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        void AddQueryCandidates(string? rawValue)
        {
            var raw = CollapseWhitespace(rawValue ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (LooksLikeQuotedLookupQuery(raw))
            {
                AddDistinctRagQuery(queries, raw);
                return;
            }

            AddDistinctRagQuery(queries, raw);

            if (sourceBackedCanonical)
                return;

            var normalized = NormalizeRagQueryForRetrieval(raw);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctRagQuery(queries, normalized);
            }
        }

        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                AddQueryCandidates(q.GetString());
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            AddQueryCandidates(q1.GetString());
        }

        queries = queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var queryBudget = ResolveRagMultiSearchQueryBudget(
            topK,
            queries.Count,
            researchMode,
            includeResearchSurfaces == true);
        var requestedCategoryScope = categoryScope;
        string? rejectedCategoryScope = null;
        string? rejectedCategoryScopeReason = null;
        if (!sourceBackedCanonical && !disableAutomaticCategoryScoping)
        {
            categoryScope = ResolveTrustedRagMultiSearchCategoryScope(
                categoryScope,
                queries,
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
        var categoryScopeRefinedFromPreviousInference = false;
        string? categoryScopeRefinedFrom = null;
        string? categoryScopeRefinementReason = null;
        string? refinedCategoryScope = null;
        if (!disableAutomaticCategoryScoping)
        {
            refinedCategoryScope = TryRefineRagMultiSearchCategoryScopeFromPreviousInference(
                categoryScope,
                rejectedCategoryScope,
                docId,
                docPath,
                pageStart,
                pageEnd,
                researchMode,
                includeResearchSurfaces == true,
                out categoryScopeRefinedFrom,
                out categoryScopeRefinementReason);
        }
        if (!string.IsNullOrWhiteSpace(refinedCategoryScope))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category scope refined: " +
                $"from={FormatRagTraceValue(categoryScopeRefinedFrom)}|refined={FormatRagTraceValue(refinedCategoryScope)}|" +
                $"reason={FormatRagTraceValue(categoryScopeRefinementReason)}");
            EmitRagTrace(
                "rag.multi_search.scope.refined",
                ("from", categoryScopeRefinedFrom),
                ("refined", refinedCategoryScope),
                ("reason", categoryScopeRefinementReason));
            categoryScope = refinedCategoryScope;
            categoryScopeRefinedFromPreviousInference = true;
            rejectedCategoryScope = null;
            rejectedCategoryScopeReason = null;
        }
        if (!string.IsNullOrWhiteSpace(rejectedCategoryScope))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category scope rejected: " +
                $"requested={FormatRagTraceValue(rejectedCategoryScope)}|reason={FormatRagTraceValue(rejectedCategoryScopeReason)}|" +
                $"queries={TruncateForPrompt(string.Join(" || ", queries.Select(q => FormatRagTraceValue(q, 90))), 520)}");
            EmitRagTrace(
                "rag.multi_search.scope.rejected",
                ("requested_category", rejectedCategoryScope),
                ("reason", rejectedCategoryScopeReason),
                ("queries", queries.Take(queryBudget).ToArray()));
        }
        ClientLog.Info(
            "ToolAgent rag.multi_search begin: " +
            $"queries={queries.Count}|budget={queryBudget}|topK={topK}|mode={FormatRagTraceValue(mode)}|" +
            $"category={FormatRagTraceValue(categoryScope)}|requestedCategory={FormatRagTraceValue(requestedCategoryScope)}|" +
            $"rejectedCategory={FormatRagTraceValue(rejectedCategoryScope)}|docId={FormatRagTraceValue(docId)}|docPath={FormatRagTraceValue(docPath, 180)}|" +
            $"pageStart={pageStart?.ToString(CultureInfo.InvariantCulture) ?? "-"}|pageEnd={pageEnd?.ToString(CultureInfo.InvariantCulture) ?? "-"}|" +
            $"maxPerDoc={maxPerDoc?.ToString(CultureInfo.InvariantCulture) ?? "-"}|maxPerPage={maxPerPage?.ToString(CultureInfo.InvariantCulture) ?? "-"}|" +
            $"researchMode={FormatRagTraceValue(researchMode)}|includeResearchSurfaces={includeResearchSurfaces?.ToString() ?? "-"}|" +
            $"trustCategoryScope={trustCategoryScope}|disableAutomaticCategoryScoping={disableAutomaticCategoryScoping}|" +
            $"sourceBackedCanonical={sourceBackedCanonical}");
        EmitRagTrace(
            "rag.multi_search.start",
            ("queries", queries.Count),
            ("query_budget", queryBudget),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope),
            ("requested_category", requestedCategoryScope),
            ("rejected_category", rejectedCategoryScope),
            ("doc_id", docId),
            ("doc_path", docPath),
            ("page_start", pageStart),
            ("page_end", pageEnd),
            ("max_per_doc", maxPerDoc),
            ("max_per_page", maxPerPage),
            ("research_mode", researchMode),
            ("include_research_surfaces", includeResearchSurfaces),
            ("trust_category_scope", trustCategoryScope),
            ("disable_automatic_category_scoping", disableAutomaticCategoryScoping),
            ("source_backed_canonical", sourceBackedCanonical));

        if (queries.Count == 0)
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search end: " +
                $"hits=0|reason=no_queries|ms={totalSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.end",
                ("hits", 0),
                ("reason", "no_queries"),
                ("ms", totalSw.ElapsedMilliseconds));
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }

        var categoryInferredSupplementalQueries = new List<string>();

        async Task<string?> TryInferCategoryScopeFromCatalogProbeAsync()
        {
            if (disableAutomaticCategoryScoping)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "automatic_category_scoping_disabled"),
                    ("queries", queries.Take(queryBudget).ToArray()));
                return null;
            }

            if (!string.IsNullOrWhiteSpace(categoryScope)
                || !string.IsNullOrWhiteSpace(docId)
                || !string.IsNullOrWhiteSpace(docPath)
                || pageStart.HasValue
                || pageEnd.HasValue
                || !IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
            {
                return null;
            }

            var probeQueries = queries
                .Take(queryBudget)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Select(static (query, index) => new
                {
                    Query = query,
                    Index = index,
                    Specificity = ComputeRagMultiSearchQuerySpecificity(query),
                    Key = NormalizeLexicalLookup(query)
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static group => group
                    .OrderByDescending(item => item.Specificity)
                    .ThenBy(item => item.Index)
                    .First())
                .OrderByDescending(static item => item.Specificity)
                .ThenBy(static item => item.Index)
                .Take(RagMultiSearchCategoryProbeMaxQueries)
                .OrderBy(static item => item.Index)
                .Select(static item => item.Query)
                .ToArray();
            if (probeQueries.Length == 0)
                return null;

            var normalizedQueryText = NormalizeLexicalLookup(string.Join(' ', queries));
            var knownCategories = EnumerateKnownCategories()
                .Select(category => new
                {
                    Category = category,
                    Scope = string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath,
                    LexicalScore = ComputeCategoryHintScore(category, normalizedQueryText)
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Scope) && item.Category.TotalDocuments > 0)
                .GroupBy(static item => item.Scope, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(item => item.LexicalScore)
                    .ThenByDescending(item => item.Category.TotalDocuments)
                    .ThenBy(item => item.Category.Ordinal)
                    .First())
                .ToArray();

            if (knownCategories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "no_catalog_categories"),
                    ("queries", probeQueries));
                return null;
            }

            var catalogHitsByScope = new Dictionary<string, RagCategoryCatalogScopeEvidence>(StringComparer.OrdinalIgnoreCase);
            var catalogProbeTermQueryMatches = probeQueries
                .SelectMany(query => ExtractQuerySignalTerms(NormalizeLexicalLookup(query)))
                .GroupBy(static term => term, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Count(),
                    StringComparer.Ordinal);
            var catalogProbeTerms = probeQueries
                .SelectMany(query => ExtractQuerySignalTerms(NormalizeLexicalLookup(query)))
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(RagMultiSearchCategoryCatalogProbeMaxTerms)
                .ToArray();

            string? ResolveKnownCategoryScopeFromDocument(ToolMemory.DocumentItem document)
            {
                var documentScopes = new[]
                    {
                        document.CategoryPath,
                        document.Category,
                        GuessCategoryPath(document.DocPath),
                        TryExtractTopLevelCategoryFromDocPath(document.DocPath)
                    }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Replace('\\', '/').Trim('/'))
                    .SelectMany(static value =>
                    {
                        var values = new List<string> { value };
                        var first = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(first) && !string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                            values.Add(first);
                        return values;
                    })
                    .Select(NormalizeLooseLookup)
                    .Where(static value => value.Length >= 3)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (documentScopes.Length == 0)
                    return null;

                foreach (var knownCategory in knownCategories)
                {
                    var names = new[]
                        {
                            knownCategory.Scope,
                            knownCategory.Category.CategoryPath,
                            knownCategory.Category.DisplayName,
                            knownCategory.Category.CategoryRef
                        }
                        .Concat(knownCategory.Category.Aliases ?? new List<string>())
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!.Replace('\\', '/').Trim('/'))
                        .SelectMany(static value =>
                        {
                            var values = new List<string> { value };
                            var first = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(first) && !string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                                values.Add(first);
                            return values;
                        })
                        .Select(NormalizeLooseLookup)
                        .Where(static value => value.Length >= 3)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    if (names.Any(name => documentScopes.Contains(name, StringComparer.Ordinal)))
                        return knownCategory.Scope;
                }

                return null;
            }

            static string? ResolveCatalogDocumentSpecificScope(ToolMemory.DocumentItem document)
            {
                var categoryScope = CollapseWhitespace(document.CategoryPath)
                    .Replace('\\', '/')
                    .Trim('/');
                var docScope = CollapseWhitespace(GuessCategoryPath(document.DocPath))
                    .Replace('\\', '/')
                    .Trim('/');

                if (!string.IsNullOrWhiteSpace(categoryScope)
                    && !string.IsNullOrWhiteSpace(docScope)
                    && docScope.StartsWith(categoryScope + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return docScope;
                }

                if (!string.IsNullOrWhiteSpace(docScope))
                    return docScope;

                return string.IsNullOrWhiteSpace(categoryScope) ? null : categoryScope;
            }

            static string? ResolveDominantCatalogSpecificScope(string fallbackScope, RagCategoryCatalogScopeEvidence? evidence)
            {
                if (evidence is null || evidence.SpecificScopes.Count == 0)
                    return null;

                var dominant = evidence.SpecificScopes
                    .OrderByDescending(static item => item.Value)
                    .ThenBy(static item => item.Key.Length)
                    .First();
                var requiredSupport = Math.Max(2, (int)Math.Ceiling(evidence.HitCount * 0.6));
                if (dominant.Value < requiredSupport)
                    return null;

                var normalizedFallback = fallbackScope.Replace('\\', '/').Trim('/');
                var normalizedDominant = dominant.Key.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(normalizedDominant)
                    || string.Equals(normalizedDominant, normalizedFallback, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return normalizedDominant.StartsWith(normalizedFallback + "/", StringComparison.OrdinalIgnoreCase)
                    ? normalizedDominant
                    : null;
            }

            static bool HasStrongRepeatedCatalogTermScopeEvidence(RagCategoryScopeProbeCandidate item)
                => item.CatalogHitCount >= 3
                   && item.CatalogDominantTermQueryMatches >= 2
                   && item.CatalogRepeatedQueryMatches >= 2;

            static string FormatCategoryProbeCandidate(RagCategoryScopeProbeCandidate item)
                => string.IsNullOrWhiteSpace(item.CatalogSuggestedScope)
                    ? $"{item.Scope}:catalog={item.CatalogHitCount},terms={item.CatalogQueryMatches},repeated={item.CatalogRepeatedQueryMatches},dominant={item.CatalogDominantTermQueryMatches},lex={item.LexicalScore}"
                    : $"{item.Scope}:catalog={item.CatalogHitCount},terms={item.CatalogQueryMatches},repeated={item.CatalogRepeatedQueryMatches},dominant={item.CatalogDominantTermQueryMatches},lex={item.LexicalScore},from={item.CatalogSuggestedScope}";

            async Task<RagCategoryScopeProbeCandidate[]> ExpandCategoryProbeCandidatesWithCatalogChildrenAsync(
                IReadOnlyList<RagCategoryScopeProbeCandidate> baseCategories)
            {
                var parentCandidates = baseCategories
                    .Where(static item => item.CatalogHitCount > 0)
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Scope))
                    .Take(RagMultiSearchCategoryChildProbeMaxParents)
                    .ToArray();
                if (parentCandidates.Length == 0)
                    return baseCategories.ToArray();

                var childSw = Stopwatch.StartNew();
                var expanded = baseCategories.ToList();
                var seen = new HashSet<string>(
                    expanded.Select(static item => item.Scope),
                    StringComparer.OrdinalIgnoreCase);
                var errors = 0;
                var added = new List<string>();

                EmitRagTrace(
                    "rag.multi_search.category_child_probe.start",
                    ("parents", parentCandidates.Select(static item => item.Scope).ToArray()),
                    ("limit", RagMultiSearchCategoryChildProbeLimit));

                foreach (var parent in parentCandidates)
                {
                    try
                    {
                        using var childTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        childTimeoutCts.CancelAfter(RagMultiSearchCategoryCatalogProbeQueryTimeout);
                        var raw = await _api.DocumentsCategoriesAsync(
                            parent.Scope,
                            null,
                            RagMultiSearchCategoryChildProbeLimit,
                            0,
                            childTimeoutCts.Token).ConfigureAwait(false);
                        var children = ParseCategoriesFromCatalogJson(raw);

                        foreach (var child in children)
                        {
                            var childScope = CollapseWhitespace(
                                string.IsNullOrWhiteSpace(child.CategoryPath)
                                    ? child.DisplayName
                                    : child.CategoryPath);
                            childScope = childScope.Replace('\\', '/').Trim('/');
                            if (string.IsNullOrWhiteSpace(childScope)
                                || string.Equals(childScope, parent.Scope, StringComparison.OrdinalIgnoreCase)
                                || !childScope.StartsWith(parent.Scope.Trim('/').Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)
                                || !seen.Add(childScope))
                            {
                                continue;
                            }

                            expanded.Add(new RagCategoryScopeProbeCandidate(
                                child,
                                childScope,
                                ComputeCategoryHintScore(child, normalizedQueryText),
                                parent.CatalogHitCount,
                                parent.CatalogQueryMatches,
                                parent.CatalogRepeatedQueryMatches,
                                parent.CatalogDominantTermQueryMatches,
                                childScope));
                            added.Add(childScope);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        errors++;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        errors++;
                    }
                }

                EmitRagTrace(
                    "rag.multi_search.category_child_probe.end",
                    ("parents", parentCandidates.Length),
                    ("added", added.Take(12).ToArray()),
                    ("added_count", added.Count),
                    ("errors", errors),
                    ("ms", childSw.ElapsedMilliseconds));

                return expanded
                    .OrderByDescending(static item => item.CatalogQueryMatches)
                    .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                    .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                    .ThenByDescending(static item => item.CatalogHitCount)
                    .ThenByDescending(static item => item.LexicalScore)
                    .ThenByDescending(static item => item.Category.TotalDocuments)
                    .ThenBy(static item => item.Category.Ordinal)
                    .Take(RagMultiSearchCategoryProbeMaxScopes)
                    .ToArray();
            }

            if (catalogProbeTerms.Length > 0)
            {
                var catalogProbeSw = Stopwatch.StartNew();
                var catalogErrors = 0;
                EmitRagTrace(
                    "rag.multi_search.category_catalog_probe.start",
                    ("terms", catalogProbeTerms),
                    ("limit", RagMultiSearchCategoryCatalogProbeLimit));

                foreach (var term in catalogProbeTerms)
                {
                    try
                    {
                        using var catalogTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        catalogTimeoutCts.CancelAfter(RagMultiSearchCategoryCatalogProbeQueryTimeout);
                        var raw = await _api.DocumentsListAsync(
                            null,
                            null,
                            term,
                            RagMultiSearchCategoryCatalogProbeLimit,
                            0,
                            catalogTimeoutCts.Token).ConfigureAwait(false);
                        var documents = _api.ParseDocumentItems(raw);
                        _mem.PromoteDocumentsToWorkspace(documents);

                        foreach (var document in documents)
                        {
                            var scope = ResolveKnownCategoryScopeFromDocument(document);
                            if (string.IsNullOrWhiteSpace(scope))
                                continue;

                            if (!catalogHitsByScope.TryGetValue(scope, out var existing))
                            {
                                existing = new RagCategoryCatalogScopeEvidence();
                                catalogHitsByScope[scope] = existing;
                            }

                            existing.HitCount++;
                            if (existing.Terms.Add(term))
                            {
                                existing.TermQueryMatches[term] =
                                    catalogProbeTermQueryMatches.TryGetValue(term, out var queryMatches)
                                        ? Math.Max(1, queryMatches)
                                        : 1;
                            }

                            var specificScope = ResolveCatalogDocumentSpecificScope(document);
                            if (!string.IsNullOrWhiteSpace(specificScope))
                            {
                                existing.SpecificScopes.TryGetValue(specificScope, out var count);
                                existing.SpecificScopes[specificScope] = count + 1;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        catalogErrors++;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        catalogErrors++;
                    }
                }

                var catalogSummary = catalogHitsByScope
                    .OrderByDescending(static item => item.Value.Terms.Count)
                    .ThenByDescending(static item => item.Value.HitCount)
                    .Take(8)
                    .Select(static item =>
                    {
                        var dominantScope = item.Value.SpecificScopes
                            .OrderByDescending(static scope => scope.Value)
                            .ThenBy(static scope => scope.Key.Length)
                            .Select(static scope => scope.Key)
                            .FirstOrDefault();
                        return string.IsNullOrWhiteSpace(dominantScope)
                            ? $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count}"
                            : $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count},specific={dominantScope}";
                    })
                    .ToArray();

                EmitRagTrace(
                    "rag.multi_search.category_catalog_probe.end",
                    ("terms", catalogProbeTerms.Length),
                    ("matched_categories", catalogHitsByScope.Count),
                    ("errors", catalogErrors),
                    ("results", catalogSummary),
                    ("ms", catalogProbeSw.ElapsedMilliseconds));
            }

            var matchedCatalogProbeTerms = catalogHitsByScope.Values
                .SelectMany(static evidence => evidence.Terms)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var effectiveProbeQueries = probeQueries
                .Concat(matchedCatalogProbeTerms)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .GroupBy(NormalizeLexicalLookup, StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(RagMultiSearchCategoryProbeMaxEffectiveQueries)
                .ToArray();

            var categories = knownCategories
                .Select(item =>
                {
                    catalogHitsByScope.TryGetValue(item.Scope, out var catalog);
                    var suggestedScope = ResolveDominantCatalogSpecificScope(item.Scope, catalog);
                    return new RagCategoryScopeProbeCandidate(
                        item.Category,
                        suggestedScope ?? item.Scope,
                        item.LexicalScore,
                        catalog?.HitCount ?? 0,
                        catalog?.Terms.Count ?? 0,
                        catalog?.RepeatedQueryMatches ?? 0,
                        catalog?.DominantTermQueryMatches ?? 0,
                        suggestedScope);
                })
                .Where(static item => item.CatalogQueryMatches >= 2
                                      || item.LexicalScore > 0
                                      || HasStrongRepeatedCatalogTermScopeEvidence(item))
                .OrderByDescending(static item => item.CatalogQueryMatches)
                .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                .ThenByDescending(static item => item.CatalogHitCount)
                .ThenByDescending(static item => item.LexicalScore)
                .ThenByDescending(static item => item.Category.TotalDocuments)
                .ThenBy(static item => item.Category.Ordinal)
                .Take(RagMultiSearchCategoryProbeMaxScopes)
                .ToArray();

            var usedFallbackCategoryProbeCandidates = false;
            if (categories.Length == 0)
            {
                usedFallbackCategoryProbeCandidates = true;
                categories = knownCategories
                    .OrderByDescending(static item => item.Category.TotalDocuments)
                    .ThenBy(static item => item.Category.Ordinal)
                    .Take(RagMultiSearchCategoryProbeMaxScopes)
                    .Select(item => new RagCategoryScopeProbeCandidate(
                        item.Category,
                        item.Scope,
                        item.LexicalScore,
                        CatalogHitCount: 0,
                        CatalogQueryMatches: 0,
                        CatalogRepeatedQueryMatches: 0,
                        CatalogDominantTermQueryMatches: 0,
                        CatalogSuggestedScope: null))
                    .ToArray();
                EmitRagTrace(
                    "rag.multi_search.category_probe.fallback_candidates",
                    ("reason", "source_exploration_without_strong_catalog_hint"),
                    ("queries", probeQueries),
                    ("categories", categories
                        .Select(FormatCategoryProbeCandidate)
                        .Take(12)
                        .ToArray()));
            }

            if (categories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "weak_catalog_or_category_hints"),
                    ("queries", probeQueries),
                    ("catalog_terms", matchedCatalogProbeTerms),
                    ("catalog_results", catalogHitsByScope
                        .OrderByDescending(static item => item.Value.Terms.Count)
                        .ThenByDescending(static item => item.Value.HitCount)
                        .Take(8)
                        .Select(static item => $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count}")
                        .ToArray()));
                return null;
            }

            categories = await ExpandCategoryProbeCandidatesWithCatalogChildrenAsync(categories).ConfigureAwait(false);

            categories = categories
                .OrderByDescending(static item => item.CatalogQueryMatches)
                .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                .ThenByDescending(static item => item.CatalogHitCount)
                .ThenByDescending(static item => item.LexicalScore)
                .ThenByDescending(static item => item.Category.TotalDocuments)
                .ThenBy(static item => item.Category.Ordinal)
                .ToArray();

            var clearCatalogChildCandidates = categories
                .Where(static item => item.CatalogHitCount > 0)
                .Where(static item => item.CatalogQueryMatches >= 2
                                      || item.LexicalScore > 0
                                      || HasStrongRepeatedCatalogTermScopeEvidence(item))
                .Where(static item => !string.IsNullOrWhiteSpace(item.CatalogSuggestedScope))
                .Where(static item => item.Scope.Contains('/', StringComparison.Ordinal))
                .ToArray();
            if (clearCatalogChildCandidates.Length == 1)
            {
                var selected = clearCatalogChildCandidates[0];
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);

                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "catalog_child_applied"),
                    ("category", selected.Scope),
                    ("catalog_hits", selected.CatalogHitCount),
                    ("catalog_terms", selected.CatalogQueryMatches),
                    ("catalog_repeated_terms", selected.CatalogRepeatedQueryMatches),
                    ("catalog_dominant_term_queries", selected.CatalogDominantTermQueryMatches),
                    ("supplemental_queries", categoryInferredSupplementalQueries.ToArray()),
                    ("candidates", categories
                        .Select(FormatCategoryProbeCandidate)
                        .Take(12)
                        .ToArray()));
                return selected.Scope;
            }

            var repeatedCatalogTermCandidates = categories
                .Where(HasStrongRepeatedCatalogTermScopeEvidence)
                .ToArray();
            if (repeatedCatalogTermCandidates.Length == 1)
            {
                var selected = repeatedCatalogTermCandidates[0];
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);

                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "catalog_repeated_term_applied"),
                    ("category", selected.Scope),
                    ("catalog_hits", selected.CatalogHitCount),
                    ("catalog_terms", selected.CatalogQueryMatches),
                    ("catalog_repeated_terms", selected.CatalogRepeatedQueryMatches),
                    ("catalog_dominant_term_queries", selected.CatalogDominantTermQueryMatches),
                    ("supplemental_queries", categoryInferredSupplementalQueries.ToArray()),
                    ("candidates", categories
                        .Select(FormatCategoryProbeCandidate)
                        .Take(12)
                        .ToArray()));
                return selected.Scope;
            }

            if (categories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "no_catalog_categories"),
                    ("queries", probeQueries));
                return null;
            }

            var probeSw = Stopwatch.StartNew();
            EmitRagTrace(
                "rag.multi_search.category_probe.start",
                ("categories", categories.Length),
                ("queries", effectiveProbeQueries),
                ("candidates", categories
                    .Select(FormatCategoryProbeCandidate)
                    .Take(12)
                    .ToArray()),
                ("top_k", RagMultiSearchCategoryProbeTopK));

            using var gate = new SemaphoreSlim(Math.Min(RagMultiSearchMaxParallelism, categories.Length));
            var results = await Task.WhenAll(categories.Select(async (item, categoryIndex) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                var scopeSw = Stopwatch.StartNew();
                var hitCount = 0;
                var matchedQueryCount = 0;
                var errorCount = 0;
                var score = 0.0;
                ClientLog.Info(
                    "ToolAgent rag.multi_search category probe scope start: " +
                    $"scope={FormatRagTraceValue(item.Scope)}|index={categoryIndex + 1}/{categories.Length}|" +
                    $"queries={effectiveProbeQueries.Length}|catalogHits={item.CatalogHitCount}|catalogTerms={item.CatalogQueryMatches}|" +
                    $"lex={item.LexicalScore}");
                EmitRagTrace(
                    "rag.multi_search.category_probe.scope.start",
                    ("scope", item.Scope),
                    ("index", categoryIndex + 1),
                    ("total", categories.Length),
                    ("queries", effectiveProbeQueries.Length),
                    ("catalog_hits", item.CatalogHitCount),
                    ("catalog_terms", item.CatalogQueryMatches),
                    ("lexical_score", item.LexicalScore));
                try
                {
                    foreach (var probeQueryEntry in effectiveProbeQueries.Select(static (query, index) => new { Query = query, Index = index }))
                    {
                        var probeQuery = probeQueryEntry.Query;
                        var querySw = Stopwatch.StartNew();
                        var queryHitCount = 0;
                        string? queryError = null;
                        ClientLog.Info(
                            "ToolAgent rag.multi_search category probe query start: " +
                            $"scope={FormatRagTraceValue(item.Scope)}|index={probeQueryEntry.Index + 1}/{effectiveProbeQueries.Length}|" +
                            $"query={FormatRagTraceValue(probeQuery, 160)}");
                        EmitRagTrace(
                            "rag.multi_search.category_probe.query.start",
                            ("scope", item.Scope),
                            ("index", probeQueryEntry.Index + 1),
                            ("total", effectiveProbeQueries.Length),
                            ("query", probeQuery));
                        try
                        {
                            using var queryTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            queryTimeoutCts.CancelAfter(RagMultiSearchCategoryProbeQueryTimeout);
                            var raw = await _api.RagSearchToolAsync(
                                probeQuery,
                                RagMultiSearchCategoryProbeTopK,
                                item.Scope,
                                "balanced",
                                queryTimeoutCts.Token).ConfigureAwait(false);
                            var norm = NormalizeRagHits(raw);
                            queryHitCount = CountRagHits(norm);
                            hitCount += queryHitCount;
                            if (queryHitCount > 0)
                                matchedQueryCount++;
                            if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var hit in hits.EnumerateArray())
                                    score += Math.Max(0, ReadRagHitScore(hit));
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = "rag_category_probe_query_timeout";
                        }
                        catch (ApiClientBackendBusyException) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = "rag_search_busy";
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = ex.GetType().Name;
                        }
                        finally
                        {
                            querySw.Stop();
                            ClientLog.Info(
                                "ToolAgent rag.multi_search category probe query end: " +
                                $"scope={FormatRagTraceValue(item.Scope)}|index={probeQueryEntry.Index + 1}/{effectiveProbeQueries.Length}|" +
                                $"hits={queryHitCount}|error={FormatRagTraceValue(queryError, 160)}|ms={querySw.ElapsedMilliseconds}|" +
                                $"query={FormatRagTraceValue(probeQuery, 140)}");
                            EmitRagTrace(
                                "rag.multi_search.category_probe.query.end",
                                ("scope", item.Scope),
                                ("index", probeQueryEntry.Index + 1),
                                ("total", effectiveProbeQueries.Length),
                                ("query", probeQuery),
                                ("hits", queryHitCount),
                                ("error", queryError),
                                ("ms", querySw.ElapsedMilliseconds));
                        }
                    }
                }
                finally
                {
                    scopeSw.Stop();
                    ClientLog.Info(
                        "ToolAgent rag.multi_search category probe scope end: " +
                        $"scope={FormatRagTraceValue(item.Scope)}|index={categoryIndex + 1}/{categories.Length}|" +
                        $"hits={hitCount}|matchedQueries={matchedQueryCount}|errors={errorCount}|score={score.ToString("0.###", CultureInfo.InvariantCulture)}|" +
                        $"ms={scopeSw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "rag.multi_search.category_probe.scope.end",
                        ("scope", item.Scope),
                        ("index", categoryIndex + 1),
                        ("total", categories.Length),
                        ("hits", hitCount),
                        ("matched_queries", matchedQueryCount),
                        ("errors", errorCount),
                        ("score", score),
                        ("ms", scopeSw.ElapsedMilliseconds));
                    gate.Release();
                }

                return new RagCategoryScopeProbeResult(
                    item.Scope,
                    hitCount,
                    matchedQueryCount,
                    score,
                    errorCount,
                    scopeSw.ElapsedMilliseconds,
                    item.LexicalScore,
                    item.CatalogHitCount,
                    item.CatalogQueryMatches);
            })).ConfigureAwait(false);

            var ranked = results
                .Where(static result => result.HitCount > 0)
                .OrderByDescending(static result => result.MatchedQueryCount)
                .ThenByDescending(static result => result.CatalogQueryMatches)
                .ThenByDescending(static result => result.CatalogHitCount)
                .ThenByDescending(static result => result.Score)
                .ThenByDescending(static result => result.HitCount)
                .ThenBy(static result => result.ErrorCount)
                .ThenBy(static result => result.ElapsedMs)
                .ToArray();
            var summary = results
                .OrderByDescending(static result => result.MatchedQueryCount)
                .ThenByDescending(static result => result.CatalogQueryMatches)
                .ThenByDescending(static result => result.CatalogHitCount)
                .ThenByDescending(static result => result.HitCount)
                .ThenByDescending(static result => result.Score)
                .Take(8)
                .Select(static result => $"{result.Scope}:hits={result.HitCount},queries={result.MatchedQueryCount},catalog={result.CatalogHitCount}")
                .ToArray();

            if (ranked.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "no_hits"),
                    ("categories", categories.Length),
                    ("results", string.Join(" | ", summary)),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            var best = ranked[0];
            var requiredMatchedQueries = Math.Min(2, effectiveProbeQueries.Length);
            var clearFallbackProbeWinner = usedFallbackCategoryProbeCandidates
                && best.HitCount > 0
                && best.MatchedQueryCount >= requiredMatchedQueries;
            if (best.LexicalScore == 0
                && (best.CatalogQueryMatches < 2 || best.MatchedQueryCount < requiredMatchedQueries)
                && !clearFallbackProbeWinner)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "weak"),
                    ("best", best.Scope),
                    ("hits", best.HitCount),
                    ("matched_queries", best.MatchedQueryCount),
                    ("required_matched_queries", requiredMatchedQueries),
                    ("catalog_terms", best.CatalogQueryMatches),
                    ("queries", effectiveProbeQueries.Length),
                    ("results", summary),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            if (ranked.Length > 1
                && best.MatchedQueryCount == ranked[1].MatchedQueryCount
                && best.CatalogQueryMatches == ranked[1].CatalogQueryMatches
                && best.CatalogHitCount == ranked[1].CatalogHitCount
                && best.HitCount == ranked[1].HitCount
                && Math.Abs(best.Score - ranked[1].Score) < 0.0001)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "ambiguous"),
                    ("best", best.Scope),
                    ("runner_up", ranked[1].Scope),
                    ("hits", best.HitCount),
                    ("matched_queries", best.MatchedQueryCount),
                    ("results", summary),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            EmitRagTrace(
                "rag.multi_search.category_probe.end",
                ("decision", "applied"),
                ("category", best.Scope),
                ("hits", best.HitCount),
                ("matched_queries", best.MatchedQueryCount),
                ("catalog_hits", best.CatalogHitCount),
                ("catalog_terms", best.CatalogQueryMatches),
                ("score", best.Score),
                ("results", summary),
                ("ms", probeSw.ElapsedMilliseconds));
            if (best.CatalogHitCount > 0)
            {
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);
            }
            return best.Scope;
        }

        async Task<JsonElement> RunMergedSearchAsync(string? scope, bool categoryInferred)
        {
            var scopeSw = Stopwatch.StartNew();
            var merged = new List<RagMultiSearchHitCandidate>();
            var queryRuns = new List<object?>();
            var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            object? selectedGuidance = null;
            string? selectedGuidanceBehavior = null;
            var selectedQueryList = queries.Take(queryBudget)
                .ToList();
            if (categoryInferred && categoryInferredSupplementalQueries.Count > 0)
            {
                foreach (var supplementalQuery in categoryInferredSupplementalQueries)
                {
                    if (selectedQueryList.Count >= queryBudget + RagMultiSearchCategorySupplementalQueryLimit)
                        break;

                    var normalizedSupplemental = NormalizeLexicalLookup(supplementalQuery);
                    if (string.IsNullOrWhiteSpace(normalizedSupplemental)
                        || selectedQueryList.Any(query => string.Equals(NormalizeLexicalLookup(query), normalizedSupplemental, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    selectedQueryList.Add(supplementalQuery);
                }
            }

            var selectedQueries = selectedQueryList.ToArray();
            var querySpecificities = selectedQueries
                .Select(ComputeRagMultiSearchQuerySpecificity)
                .ToArray();
            var fanoutParallelism = Math.Min(RagMultiSearchMaxParallelism, Math.Max(1, selectedQueries.Length));
            var perQueryTimeout = ResolveRagSourceExplorationQueryTimeout(
                researchMode,
                includeResearchSurfaces == true);
            ClientLog.Info(
                "ToolAgent rag.multi_search scope start: " +
                $"scope={FormatRagTraceValue(scope)}|categoryInferred={categoryInferred}|selectedQueries={selectedQueries.Length}|" +
                $"fanout={fanoutParallelism}|perQueryTimeoutMs={(perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : 0)}|" +
                $"queries={TruncateForPrompt(string.Join(" || ", selectedQueries.Select(q => FormatRagTraceValue(q, 90))), 520)}");
            EmitRagTrace(
                "rag.multi_search.scope.start",
                ("scope", scope),
                ("category_inferred", categoryInferred),
                ("selected_queries", selectedQueries.Length),
                ("fanout", fanoutParallelism),
                ("per_query_timeout_ms", perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : (int?)null),
                ("queries", selectedQueries));
            using var gate = new SemaphoreSlim(fanoutParallelism);
            var runs = await Task.WhenAll(selectedQueries.Select(async (q, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                var querySw = Stopwatch.StartNew();
                ClientLog.Info(
                    "ToolAgent rag.multi_search query start: " +
                    $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|" +
                    $"timeoutMs={(perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : 0)}|" +
                    $"query={FormatRagTraceValue(q, 180)}");
                EmitRagTrace(
                    "rag.multi_search.query.start",
                    ("scope", scope),
                    ("index", index + 1),
                    ("total", selectedQueries.Length),
                    ("timeout_ms", perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : (int?)null),
                    ("query", q));
                try
                {
                    JsonElement norm;
                    var queryTimeout = perQueryTimeout;
                    try
                    {
                        using var queryTimeoutCts = queryTimeout.HasValue
                            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                            : null;
                        if (queryTimeout.HasValue)
                            queryTimeoutCts!.CancelAfter(queryTimeout.Value);

                        var raw = await _api.RagSearchToolAsync(
                            q,
                            topK,
                            scope,
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
                        norm = NormalizeRagHits(raw);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && queryTimeout.HasValue)
                    {
                        norm = BuildRagSearchQueryTimeoutPayload(q, scope, mode, queryTimeout.Value);
                    }
                    catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
                    {
                        norm = BuildRagSearchBusyPayload(new[] { q }, scope, mode, ex);
                    }
                    querySw.Stop();

                    var localDegradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectRagDegradedRetrievers(norm, localDegradedRetrievers);
                    var guidance = DeserializePromptObject(norm, "guidance");
                    var guidanceBehavior = norm.TryGetProperty("guidance", out var guidanceEl) && guidanceEl.ValueKind == JsonValueKind.Object
                        ? TryGetString(guidanceEl, "behavior")
                        : null;
                    var busy = IsRagSearchBusyPayload(norm);
                    var hitCount = CountRagHits(norm);
                    var error = TryGetString(norm, "error");
                    ClientLog.Info(
                        "ToolAgent rag.multi_search query end: " +
                        $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|hits={hitCount}|" +
                        $"busy={busy}|error={FormatRagTraceValue(error, 180)}|degraded={localDegradedRetrievers.Count}|" +
                        $"ms={querySw.ElapsedMilliseconds}|query={FormatRagTraceValue(q, 140)}");
                    EmitRagTrace(
                        "rag.multi_search.query.end",
                        ("scope", scope),
                        ("index", index + 1),
                        ("total", selectedQueries.Length),
                        ("query", q),
                        ("hits", hitCount),
                        ("busy", busy),
                        ("error", error),
                        ("degraded", localDegradedRetrievers.ToArray()),
                        ("ms", querySw.ElapsedMilliseconds));
                    return new
                    {
                        Index = index,
                        Query = q,
                        Norm = norm.Clone(),
                        Busy = busy,
                        RetryAfterSeconds = TryGetInt(norm, "retryAfterSeconds") ?? 1,
                        ClientElapsedMs = querySw.ElapsedMilliseconds,
                        HitCount = hitCount,
                        Error = error,
                        Guidance = guidance,
                        GuidanceBehavior = guidanceBehavior,
                        Meta = DeserializePromptObject(norm, "meta"),
                        DegradedRetrievers = localDegradedRetrievers.ToArray()
                    };
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    querySw.Stop();
                    ClientLog.Info(
                        "ToolAgent rag.multi_search query end: " +
                        $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|ok=false|" +
                        $"error={TruncateForPrompt(ex.GetType().Name + ": " + ex.Message, 260)}|ms={querySw.ElapsedMilliseconds}|" +
                        $"query={FormatRagTraceValue(q, 140)}");
                    EmitRagTrace(
                        "rag.multi_search.query.end",
                        ("scope", scope),
                        ("index", index + 1),
                        ("total", selectedQueries.Length),
                        ("query", q),
                        ("ok", false),
                        ("error", ex.GetType().Name + ": " + ex.Message),
                        ("ms", querySw.ElapsedMilliseconds));
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent rag.multi_search scope gathered: " +
                $"scope={FormatRagTraceValue(scope)}|runs={runs.Length}|totalHits={runs.Sum(static run => run.HitCount)}|" +
                $"busyRuns={runs.Count(static run => run.Busy)}|errorRuns={runs.Count(static run => !string.IsNullOrWhiteSpace(run.Error))}|" +
                $"ms={scopeSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.scope.gathered",
                ("scope", scope),
                ("runs", runs.Length),
                ("total_hits", runs.Sum(static run => run.HitCount)),
                ("busy_runs", runs.Count(static run => run.Busy)),
                ("error_runs", runs.Count(static run => !string.IsNullOrWhiteSpace(run.Error))),
                ("ms", scopeSw.ElapsedMilliseconds));

            foreach (var run in runs.OrderBy(static x => x.Index))
            {
                foreach (var degradedRetriever in run.DegradedRetrievers)
                    degradedRetrievers.Add(degradedRetriever);

                if (ShouldPreferMultiSearchGuidance(selectedGuidanceBehavior, run.GuidanceBehavior))
                {
                    selectedGuidance = run.Guidance;
                    selectedGuidanceBehavior = run.GuidanceBehavior;
                }

                queryRuns.Add(new
                {
                    query = run.Query,
                    clientElapsedMs = run.ClientElapsedMs,
                    hitCount = run.HitCount,
                    error = string.IsNullOrWhiteSpace(run.Error) ? null : run.Error,
                    busy = run.Busy ? true : (bool?)null,
                    retryAfterSeconds = run.Busy ? run.RetryAfterSeconds : (int?)null,
                    timeoutMs = perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : (int?)null,
                    degradedRetrievers = run.DegradedRetrievers.Length == 0 ? null : run.DegradedRetrievers,
                    guidance = run.Guidance,
                    meta = run.Meta
                });

                if (run.Norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                {
                    var hitRank = 0;
                    foreach (var h in hits.EnumerateArray())
                    {
                        merged.Add(new RagMultiSearchHitCandidate(
                            h.Clone(),
                            run.Index,
                            hitRank++,
                            querySpecificities.Length > run.Index ? querySpecificities[run.Index] : 0,
                            run.Query));
                    }
                }
            }

            var busyRuns = runs.Where(static run => run.Busy).ToArray();
            var allBusy = busyRuns.Length == runs.Length;
            var retryAfterSeconds = busyRuns.Length == 0
                ? (int?)null
                : Math.Clamp(busyRuns.Max(static run => run.RetryAfterSeconds), 1, 300);

            // Dedup by docPath + pageStart + pageEnd, keeping the richest metadata variant.
            var uniq = merged
                .GroupBy(static candidate => BuildRagHitDedupeKey(candidate.Hit), StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                {
                    var bestHit = group
                        .OrderByDescending(static candidate => ComputeRagHitMetadataRichness(candidate.Hit))
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .First()
                        .Hit;
                    var primaryOrigin = group
                        .Where(static candidate => candidate.QueryIndex == 0 && candidate.HitRank <= 1)
                        .OrderBy(static candidate => candidate.HitRank)
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .FirstOrDefault();
                    var bestOrigin = primaryOrigin ?? group
                        .OrderByDescending(static candidate => candidate.QuerySpecificity)
                        .ThenBy(static candidate => candidate.HitRank)
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .First();
                    return bestOrigin with { Hit = bestHit };
                })
                .ToList();

            var outputLimit = Math.Max(10, topK * 2);
            var selected = new List<RagMultiSearchHitCandidate>(outputLimit);
            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddCandidate(RagMultiSearchHitCandidate candidate)
            {
                if (selected.Count >= outputLimit)
                    return;

                if (selectedKeys.Add(BuildRagHitDedupeKey(candidate.Hit)))
                    selected.Add(candidate);
            }

            foreach (var candidate in uniq
                         .Where(static candidate => candidate.QueryIndex == 0 && candidate.HitRank <= 1)
                         .OrderBy(static candidate => candidate.HitRank)
                         .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit)))
            {
                AddCandidate(candidate);
            }

            var perQueryKeep = Math.Clamp(topK / 2, 1, 3);
            foreach (var candidate in uniq
                         .GroupBy(static candidate => candidate.QueryIndex)
                         .SelectMany(group => group
                             .OrderBy(static candidate => candidate.HitRank)
                             .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                             .Take(perQueryKeep))
                         .OrderByDescending(static candidate => candidate.QuerySpecificity)
                         .ThenBy(static candidate => candidate.HitRank)
                         .ThenBy(static candidate => candidate.QueryIndex)
                         .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit)))
            {
                AddCandidate(candidate);
            }

            foreach (var candidate in uniq
                         .OrderByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                         .ThenByDescending(static candidate => candidate.QuerySpecificity)
                         .ThenBy(static candidate => candidate.HitRank)
                         .ThenBy(static candidate => candidate.QueryIndex))
            {
                AddCandidate(candidate);
            }

            var outputHits = selected
                .Select(static candidate => AnnotateRagMultiSearchHit(candidate))
                .ToList();
            var distinctDocs = outputHits
                .Select(static hit => TryGetString(hit, "docPath") ?? TryGetString(hit, "docName") ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var distinctPages = outputHits
                .Select(static hit =>
                {
                    var doc = TryGetString(hit, "docPath") ?? TryGetString(hit, "docName") ?? string.Empty;
                    return string.IsNullOrWhiteSpace(doc) ? string.Empty : $"{doc}#p{ReadRagHitPageStart(hit)}";
                })
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            ClientLog.Info(
                "ToolAgent rag.multi_search scope selected: " +
                $"scope={FormatRagTraceValue(scope)}|merged={merged.Count}|unique={uniq.Count}|selected={outputHits.Count}|" +
                $"distinctDocs={distinctDocs}|distinctPages={distinctPages}|busyRuns={busyRuns.Length}|" +
                $"degraded={degradedRetrievers.Count}|ms={scopeSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.scope.selected",
                ("scope", scope),
                ("merged", merged.Count),
                ("unique", uniq.Count),
                ("selected", outputHits.Count),
                ("distinct_docs", distinctDocs),
                ("distinct_pages", distinctPages),
                ("busy_runs", busyRuns.Length),
                ("degraded", degradedRetrievers.ToArray()),
                ("ms", scopeSw.ElapsedMilliseconds));

            var payload = new
            {
                hits = outputHits,
                error = allBusy ? "rag_search_busy" : null,
                busy = allBusy ? true : (bool?)null,
                retryAfterSeconds = allBusy ? retryAfterSeconds : null,
                guidance = selectedGuidance,
                meta = new
                {
                    queries = selectedQueries,
                    mode = (mode ?? "balanced"),
                    category = scope,
                    categoryPath = scope,
                    requestedCategory = requestedCategoryScope,
                    rejectedCategoryScope,
                    rejectedCategoryScopeReason,
                    docId,
                    docPath,
                    pageStart,
                    pageEnd,
                    maxPerDoc,
                    maxPerPage,
                    researchMode,
                    includeResearchSurfaces,
                    categoryInferred,
                    fanoutParallelism,
                    perQueryTimeoutMs = perQueryTimeout.HasValue ? (int)Math.Round(perQueryTimeout.Value.TotalMilliseconds) : (int?)null,
                    busyQueries = busyRuns.Length == 0 ? null : busyRuns.Select(static run => run.Query).ToArray(),
                    degradedRetrievers = degradedRetrievers.Count == 0 ? null : degradedRetrievers.ToArray(),
                    queryRuns
                }
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
        }

        var categoryScopeInferredByProbe = false;
        var probedCategoryScope = await TryInferCategoryScopeFromCatalogProbeAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(probedCategoryScope))
        {
            categoryScope = probedCategoryScope;
            categoryScopeInferredByProbe = true;
            RememberRagInferredCategoryScope(probedCategoryScope, "catalog_probe");
        }

        var result = await RunMergedSearchAsync(
            categoryScope,
            categoryScopeInferredByProbe || categoryScopeRefinedFromPreviousInference).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(categoryScope)
            && string.IsNullOrWhiteSpace(docId)
            && string.IsNullOrWhiteSpace(docPath)
            && HasRagHits(result)
            && !HasRagBusyQueries(result)
            && !disableAutomaticCategoryScoping
            && !IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
        {
            var inferenceResults = new ToolResults();
            inferenceResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = result
            });
            var inferredCategoryScope = TryInferDominantTopLevelCategoryScope(inferenceResults, string.Join(' ', queries));
            if (!string.IsNullOrWhiteSpace(inferredCategoryScope))
            {
                ClientLog.Info(
                    "ToolAgent rag.multi_search category inference: " +
                    $"inferred={FormatRagTraceValue(inferredCategoryScope)}|initialHits={CountRagHits(result)}");
                EmitRagTrace(
                    "rag.multi_search.category_inference",
                    ("decision", "candidate"),
                    ("inferred", inferredCategoryScope),
                    ("initial_hits", CountRagHits(result)));
                var scopedResult = await RunMergedSearchAsync(inferredCategoryScope, categoryInferred: true).ConfigureAwait(false);
                if (HasRagHits(scopedResult))
                {
                    ClientLog.Info(
                        "ToolAgent rag.multi_search category inference applied: " +
                        $"inferred={FormatRagTraceValue(inferredCategoryScope)}|scopedHits={CountRagHits(scopedResult)}");
                    EmitRagTrace(
                        "rag.multi_search.category_inference",
                        ("decision", "applied"),
                        ("inferred", inferredCategoryScope),
                        ("scoped_hits", CountRagHits(scopedResult)));
                    result = scopedResult;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(categoryScope)
                 && string.IsNullOrWhiteSpace(docId)
                 && string.IsNullOrWhiteSpace(docPath)
                 && HasRagHits(result)
                 && IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category inference skipped: reason=source_exploration");
            EmitRagTrace(
                "rag.multi_search.category_inference",
                ("decision", "skipped"),
                ("reason", "source_exploration"));
        }

        RememberLastRagDiagnostics(
            queries.Take(queryBudget),
            result);
        ClientLog.Info(
            "ToolAgent rag.multi_search end: " +
            $"hits={CountRagHits(result)}|busy={HasRagBusyQueries(result)}|queries={queries.Count}|budget={queryBudget}|" +
            $"ms={totalSw.ElapsedMilliseconds}");
        EmitRagTrace(
            "rag.multi_search.end",
            ("hits", CountRagHits(result)),
            ("busy", HasRagBusyQueries(result)),
            ("queries", queries.Count),
            ("query_budget", queryBudget),
            ("ms", totalSw.ElapsedMilliseconds));
        return result;
    }
}
