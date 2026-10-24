using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDocumentStatusOperands
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (plan.Backfills.DocumentStatusOperands.Count > 0 && !plan.Scope.HasExplicitFileDocumentHint)
        {
            var (documentStatusOperandMatches, documentStatusOperandMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_document_status_operand",
                retriever: "document_status_operand",
                action: () => SearchDocumentStatusOperandContentMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    plan.Backfills.DocumentStatusOperands,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.TitleAnchorRoutePhaseMs += documentStatusOperandMs;

            if (documentStatusOperandMatches.Count > 0)
            {
                var hasStatusCompanionBackfillMatches = false;
                var statusCompanionBackfillQueries = BuildDocumentStatusOperandCompanionBackfillQueries(
                    plan.Request.Query,
                    plan.Backfills.DocumentStatusOperands,
                    documentStatusOperandMatches);
                if (statusCompanionBackfillQueries.Count > 0)
                {
                    var (statusCompanionBackfillMatches, statusCompanionBackfillMs) = await flow.Session.Phases.MeasureAsync(
                        phaseName: "retrieval_document_status_companion_backfill",
                        retriever: "document_status_companion_backfill",
                        action: async () =>
                        {
                            var backfill = new List<RagMatch>();
                            foreach (var focusedQuery in statusCompanionBackfillQueries)
                            {
                                backfill.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                                    resources.DataSource,
                                    resources.Context.TenantId,
                                    focusedQuery,
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                                    resources.Context.CancellationToken,
                                    plan.Scope.CategoryPath,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark));
                                backfill.AddRange(await SearchDenseMatchesAsync(
                                    resources.DataSource,
                                    resources.HttpClients,
                                    resources.Options,
                                    resources.Context.TenantId,
                                    NormalizeQuery(focusedQuery),
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                                    resources.Context.CancellationToken,
                                    teiMsRef: value => flow.Session.Timings.TeiMs += value,
                                    qdrantMsRef: value => flow.Session.Timings.QdrantMs += value,
                                    qdrantStatusRef: value => flow.Session.Timings.QdrantStatus = value,
                                    categoryPath: plan.Scope.CategoryPath,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                                    teiGovernor: resources.Context.TeiGovernor));
                            }

                            return backfill
                                .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
                                .Select(static group => group
                                    .OrderByDescending(match => match.Score)
                                    .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(match => match.ChunkIndex)
                                    .First())
                                .ToList();
                        },
                        getReturnedCount: static matches => matches.Count);
                    flow.Session.Timings.TitleAnchorRoutePhaseMs += statusCompanionBackfillMs;
                    if (statusCompanionBackfillMatches.Count > 0)
                    {
                        hasStatusCompanionBackfillMatches = true;
                        documentStatusOperandMatches.AddRange(statusCompanionBackfillMatches);
                        documentStatusOperandMatches = DeduplicatePreciseTitleBackfillMatches(documentStatusOperandMatches);
                    }
                }

                var shouldMergeDocumentStatusOperandSelections = ShouldMergeDocumentStatusOperandSelectionsForComparativeVersionRoute(
                    plan.Request.Query,
                    plan.Backfills.DocumentStatusOperands);
                if (shouldMergeDocumentStatusOperandSelections)
                {
                    var counterpartBackfillQueries = BuildComparativeVersionCounterpartBackfillQueries(plan.Request.Query);
                    if (counterpartBackfillQueries.Count > 0)
                    {
                        var (counterpartBackfillMatches, counterpartBackfillMs) = await flow.Session.Phases.MeasureAsync(
                            phaseName: "retrieval_comparative_version_counterpart_sparse",
                            retriever: "sparse_bm25",
                            action: async () =>
                            {
                                var backfill = new List<RagMatch>();
                                foreach (var focusedQuery in counterpartBackfillQueries)
                                {
                                    backfill.AddRange(await SearchSparseMatchesAsync(
                                        resources.DataSource,
                                        resources.Context.TenantId,
                                        focusedQuery,
                                        plan.Scope.Category,
                                        plan.Request.DocId,
                                        plan.Request.DocPath,
                                        Math.Min(Math.Max(plan.Selection.TopK, 12), 32),
                                        resources.Context.CancellationToken,
                                        sparseMsRef: value => flow.Session.Timings.SparseMs += value,
                                        lexicalExpansionQuery: focusedQuery,
                                        categoryPath: plan.Scope.CategoryPath,
                                        includeProfileCardMatches: false,
                                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                                }

                                return backfill
                                    .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
                                    .Select(static group => group
                                        .OrderByDescending(match => match.Score)
                                        .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(match => match.ChunkIndex)
                                        .First())
                                    .ToList();
                            },
                            getReturnedCount: static matches => matches.Count);
                        flow.Session.Timings.SparsePhaseMs += counterpartBackfillMs;
                        documentStatusOperandMatches.AddRange(counterpartBackfillMatches);
                    }
                }

                var mergedStatusSelections = CalibrateFusedMatches(
                    plan.Request.Query,
                    documentStatusOperandMatches.Concat(flow.Selection.Matches).ToList(),
                    plan.Request.Query);
                mergedStatusSelections = PrioritizeDocumentStatusOperandSelections(
                    plan.Request.Query,
                    plan.Backfills.DocumentStatusOperands,
                    mergedStatusSelections);
                flow.Selection.DocumentStatusOperandCandidates = mergedStatusSelections;
                if (!shouldMergeDocumentStatusOperandSelections && !hasStatusCompanionBackfillMatches)
                {
                    mergedStatusSelections = mergedStatusSelections
                        .Where(static match => string.Equals(
                            ResolveRetriever(match),
                            "document_status_operand",
                            StringComparison.Ordinal))
                        .ToList();
                }

                if (shouldMergeDocumentStatusOperandSelections)
                {
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            mergedStatusSelections,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 3),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                    EnsureComparativeBackfillDocumentDiversity(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        mergedStatusSelections,
                        plan.Selection.TopK,
                        minScore: 0.0,
                        maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                        query: plan.Request.Query);
                }
                else
                {
                    flow.Selection.Matches.Clear();
                    flow.Selection.Keys.Clear();
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            mergedStatusSelections,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 3),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                }
            }
        }
    }
}
