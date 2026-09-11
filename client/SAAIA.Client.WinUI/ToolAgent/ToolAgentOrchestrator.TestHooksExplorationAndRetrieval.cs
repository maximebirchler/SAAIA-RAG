#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static bool ShouldExpandSourceBackedEvidenceRetrievalForTests(
        ToolResults toolResults,
        string query,
        string language)
        => ShouldExpandSourceBackedEvidenceRetrieval(toolResults, query, language);

    internal static string AnalyzeSourceBackedEvidenceSufficiencyReasonForTests(
        ToolResults toolResults,
        string query,
        string language)
        => AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language).Reason;

    internal static string[] BuildSourceBackedEvidenceExplorationPassLabelsForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedEvidenceExplorationPasses(toolResults, query, language)
            .Select(static pass => pass.Label)
            .ToArray();

    internal static string[] BuildSourceBackedEvidenceExplorationPassQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedEvidenceExplorationPasses(toolResults, query, language)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static string[] BuildSourceBackedRouteAnchorFollowupRetrievalQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedRouteAnchorFollowupRetrievalQueries(toolResults, query, language);

    internal static (string Label, string? DocId, string? DocPath, string? CategoryScope, int? PageStart, int? PageEnd, string[] Queries)[] BuildSourceBackedDocumentScopedRouteAnchorFollowupPassesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(toolResults, query, language)
            .Select(static pass => (pass.Label, pass.DocId, pass.DocPath, pass.CategoryScope, pass.PageStart, pass.PageEnd, pass.Queries))
            .ToArray();

    internal static (string Label, string? ToolName, string? DocId, string? DocPath, string? ChunkId, int? PageStart, int? PageEnd)[] BuildSourceBackedContextReadExplorationPassesForTests(
        ToolResults toolResults,
        string query,
        string language,
        int maxPasses = 8)
        => BuildSourceBackedContextReadExplorationPasses(toolResults, query, language, maxPasses)
            .Select(static pass => (pass.Label, pass.ToolName, pass.DocId, pass.DocPath, pass.ChunkId, pass.PageStart, pass.PageEnd))
            .ToArray();

    internal static int ResolveSourceBackedEvidenceExplorationTopKForTests(string? query, string passLabel)
        => ResolveSourceBackedEvidenceExplorationTopK(query, passLabel);

    internal static int ResolveSourceBackedDocumentScopedExplorationMaxPerPageForTests(string? query, string passLabel)
        => ResolveSourceBackedDocumentScopedExplorationMaxPerPage(query, passLabel);

    internal static string BuildSourceBackedAnchorFollowupSignatureForTests(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedAnchorFollowupSignature(
            BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(toolResults, query, language),
            BuildSourceBackedRouteAnchorFollowupExplorationPass(toolResults, query, language));

    internal static (string? DocId, string? DocPath, string? CategoryPath, string DisplayName, int Score)[] SelectSourceBackedDocumentNavigationSeedsForTests(
        ToolResults toolResults,
        int maxDocuments = 3)
        => SelectSourceBackedDocumentNavigationSeeds(toolResults, maxDocuments)
            .Select(static seed => (seed.DocId, seed.DocPath, seed.CategoryPath, seed.DisplayName, seed.Score))
            .ToArray();

    internal static bool HasSourceBackedRouteAnchorFollowupQueriesForTests(
        ToolResults toolResults,
        string query,
        string language)
        => HasSourceBackedRouteAnchorFollowupQueries(toolResults, query, language);

    internal static bool ShouldDeferSparseSourceBackedPlanningAnchorFollowupForTests(
        ToolResults toolResults,
        string query,
        string language)
        => ShouldDeferSparseSourceBackedPlanningAnchorFollowup(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query);

    internal static bool ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPassForTests(
        ToolResults toolResults,
        string query,
        string language,
        bool acceptedAnyExplorationPass)
        => ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPass(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query,
            acceptedAnyExplorationPass);

    internal static string[] ParseSourceBackedLlmEvidenceExplorationPassLabelsForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.Label)
            .ToArray();

    internal static string[] ParseSourceBackedLlmEvidenceExplorationQueriesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static string[] ParseAndFilterSourceBackedLlmEvidenceExplorationQueriesForTests(
        string rawJson,
        string query,
        string language = "fr",
        IEnumerable<string>? alreadyTriedQueries = null,
        string? plannedCategoryScope = null)
        => FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
                ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries),
                query,
                language,
                plannedCategoryScope,
                out _,
                out _)
            .SelectMany(static pass => pass.Queries)
            .ToArray();

    internal static string[] DetectMissingStructuredRouterSearchAxesForTests(
        string query,
        string language,
        params string[] routerQueries)
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = language,
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = routerQueries,
                        topK = 8,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    })
                }
            }
        };

        return DetectMissingStructuredRouterSearchAxes(plan, query, language);
    }

    internal static string[] FindStructuredRouterSearchAxisRegressionsForTests(
        IReadOnlyList<string> missingBefore,
        IReadOnlyList<string> missingAfter)
        => FindStructuredRouterSearchAxisRegressions(missingBefore, missingAfter);

    internal static (bool Apply, string Reason, string[] MissingBefore, string[] MissingAfter, string[] RegressedAxes)
        ShouldApplyInitialLlmPlannerQueriesForTests(
            string query,
            string language,
            string[] currentQueries,
            string[] plannerQueries)
    {
        var apply = ShouldApplyInitialLlmPlannerQueries(
            currentQueries,
            plannerQueries,
            query,
            language,
            out var reason,
            out var missingBefore,
            out var missingAfter,
            out var regressedAxes);
        return (apply, reason, missingBefore, missingAfter, regressedAxes);
    }

    internal static (string[] Queries, string[] MissingAfter) BuildStructuredRouterSearchAxisFallbackQueriesForTests(
        string query,
        string language,
        params string[] routerQueries)
    {
        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = language,
            Origin = RouterPlanOrigin.Llm,
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "rag.multi_search",
                    Args = CreateJsonArgs(new
                    {
                        queries = routerQueries,
                        topK = 8,
                        mode = "broad",
                        researchMode = "source_exploration",
                        includeResearchSurfaces = true
                    })
                }
            }
        };

        var missing = DetectMissingStructuredRouterSearchAxes(plan, query, language);
        return TryBuildStructuredRouterSearchAxisFallbackPlan(
                plan,
                query,
                language,
                missing,
                out _,
                out var fallbackQueries,
                out var missingAfter)
            ? (fallbackQueries, missingAfter)
            : (routerQueries, missing);
    }

    internal static string?[] ParseSourceBackedLlmEvidenceExplorationOriginsForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.Origin)
            .ToArray();

    internal static string BuildSourceBackedAvailableResearchSurfacesForTests()
        => BuildSourceBackedAvailableResearchSurfacesForPrompt();

    internal static string BuildSourceBackedRequestShapeForTests(string query, string language)
        => BuildSourceBackedRequestShapeForPrompt(query, language);

    internal static string BuildSourceBackedLlmEvidenceExplorationSystemPromptForTests(string language)
        => BuildSourceBackedLlmEvidenceExplorationSystemPrompt(language);

    internal static string BuildSourceBackedResearchTopicKeyForTests(string query, string language)
        => BuildSourceBackedResearchTopicKey(query, language);

    internal static string BuildSourceBackedResearchShapeKeyForTests(string query)
        => BuildSourceBackedResearchShapeKey(query);

    internal static string BuildSourceBackedLlmCategoryScopeSystemPromptForTests(string language)
        => BuildSourceBackedLlmCategoryScopeSystemPrompt(language);

    internal static string BuildSourceBackedLlmEvidenceExplorationUserPromptForTests(
        ToolResults toolResults,
        string query,
        string language,
        ToolMemory? memory = null)
    {
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, memory ?? new ToolMemory());
        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language);
        var alreadyTriedQueries = BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(toolResults, query, language);
        return sut.BuildSourceBackedLlmEvidenceExplorationUserPrompt(
            toolResults,
            analysis,
            query,
            language,
            alreadyTriedQueries);
    }

    internal static string[] BuildSourceBackedSummaryOrientationQueriesForTests(string query, string? categoryScope = null)
        => BuildSourceBackedSummaryOrientationQueries(query, categoryScope);

    internal static bool ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPassForTests(
        ToolResults toolResults,
        string query,
        string language,
        int remainingPlannerRounds,
        long elapsedMs = 0,
        int explorationTimeoutMs = 540000)
        => ShouldDeferAnchorFollowupAfterAcceptedLlmPlannerPass(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query,
            remainingPlannerRounds,
            elapsedMs,
            explorationTimeoutMs);

    internal static string[] BuildSourceBackedNavigationOrientationQueriesForTests(string query, string? categoryScope = null)
        => BuildSourceBackedNavigationOrientationQueries(query, categoryScope);

    internal static bool HasExpandedSourceBackedSearchEvidenceForTests(ToolResults toolResults)
        => HasExpandedSourceBackedSearchEvidence(toolResults);

    internal static string?[] ParseSourceBackedLlmEvidenceExplorationCategoriesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => pass.CategoryScope)
            .ToArray();

    internal static (string? CategoryScope, string? Decision, string? Confidence, string? Reason) ParseSourceBackedLlmEvidenceExplorationCategoryDecisionForTests(
        string rawJson)
    {
        var decision = ParseSourceBackedLlmEvidenceExplorationCategoryScopeDecision(rawJson);
        return (decision.CategoryScope, decision.Decision, decision.Confidence, decision.Reason);
    }

    internal static (string?[] Categories, int UpdatedPassCount) ApplySourceBackedLlmCategoryScopeDecisionForTests(
        string rawJson,
        string categoryScope)
    {
        var passes = ParseSourceBackedLlmEvidenceExplorationPasses(rawJson);
        var updated = ApplySourceBackedLlmCategoryScopeDecision(passes, categoryScope, out var updatedPassCount);
        return (updated.Select(static pass => pass.CategoryScope).ToArray(), updatedPassCount);
    }

    internal static (
        string[] Labels,
        string?[] Categories,
        int QueryCount,
        int UpdatedPassCount,
        bool AddedScopeOnlyPass) FilterAndApplySourceBackedLlmCategoryScopeDecisionForTests(
            string rawJson,
            string query,
            string language,
            string categoryScope)
    {
        var passes = FilterLowQualityStructuredAxisLlmEvidenceExplorationPasses(
            ParseSourceBackedLlmEvidenceExplorationPasses(rawJson),
            query,
            language,
            categoryScope,
            out _,
            out _);
        var updated = ApplySourceBackedLlmCategoryScopeDecisionOrCreateScopeOnlyPass(
            passes,
            categoryScope,
            "llm_planner",
            out var updatedPassCount,
            out var addedScopeOnlyPass);
        return (
            updated.Select(static pass => pass.Label).ToArray(),
            updated.Select(static pass => pass.CategoryScope).ToArray(),
            updated.Sum(static pass => pass.Queries.Length),
            updatedPassCount,
            addedScopeOnlyPass);
    }

    internal static bool ShouldRunLlmSourceBackedCategoryScopeAdjudicationForTests(
        string rawJson,
        string query,
        string language,
        string categoryHints,
        ToolResults? toolResults = null)
    {
        var passes = ParseSourceBackedLlmEvidenceExplorationPasses(rawJson);
        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults ?? new ToolResults(), query, language);
        return ShouldRunLlmSourceBackedCategoryScopeAdjudication(
            passes,
            analysis,
            query,
            language,
            categoryHints);
    }

    internal static bool ShouldRunInitialLlmSourceBackedCategoryScopeAdjudicationForTests(
        string rawArgsJson,
        string query,
        string language = "fr",
        bool llmOrigin = true)
    {
        var args = JsonDocument.Parse(rawArgsJson).RootElement.Clone();
        var plan = new RouterPlan
        {
            Language = language,
            Origin = llmOrigin ? RouterPlanOrigin.Llm : RouterPlanOrigin.LocalFallback
        };
        return ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(plan, args, query);
    }

    internal static (string? Category, string? CategoryPath, bool TrustCategoryScope, string[] Queries) ApplyResolvedInitialSourceBackedCategoryScopeForTests(
        string rawArgsJson,
        string resolvedCategoryScope)
    {
        var args = JsonDocument.Parse(rawArgsJson).RootElement.Clone();
        var updated = ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(args, resolvedCategoryScope);
        return (
            TryGetStringArg(updated, "category"),
            TryGetStringArg(updated, "categoryPath"),
            GetRagTrustCategoryScopeArg(updated),
            TryGetStringArrayArg(updated, "queries").ToArray());
    }

    internal static (string Label, string? DocId, string? DocPath, int? PageStart, int? PageEnd)[] ParseSourceBackedLlmEvidenceExplorationScopesForTests(
        string rawJson,
        IEnumerable<string>? alreadyTriedQueries = null)
        => ParseSourceBackedLlmEvidenceExplorationPasses(rawJson, alreadyTriedQueries)
            .Select(static pass => (pass.Label, pass.DocId, pass.DocPath, pass.PageStart, pass.PageEnd))
            .ToArray();

    internal static bool IsBetterSourceBackedEvidenceCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedEvidenceCoverage(current, candidate, query, language);

    internal static bool CandidateSourceBackedEvidenceAddsUsefulDiversityForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => CandidateSourceBackedEvidenceAddsUsefulDiversity(
            AnalyzeSourceBackedEvidenceSufficiency(current, query, language),
            AnalyzeSourceBackedEvidenceSufficiency(candidate, query, language));

    internal static bool CandidateSourceBackedEvidenceAddsUsefulOrientationForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => CandidateSourceBackedEvidenceAddsUsefulOrientation(
            current,
            candidate,
            AnalyzeSourceBackedEvidenceSufficiency(current, query, language),
            AnalyzeSourceBackedEvidenceSufficiency(candidate, query, language),
            query,
            language);

    internal static bool CandidateSourceBackedEvidenceAddsExplorationMaterialForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language,
        bool forceBroadenedExploration = false)
        => CandidateSourceBackedEvidenceAddsExplorationMaterial(
            current,
            candidate,
            query,
            language,
            forceBroadenedExploration);

    internal static int ResolveSourceBackedEvidenceExplorationRagCallBudgetForTests(
        string query,
        string language,
        bool forceBroadenedExploration = false)
    {
        var toolResults = new ToolResults();
        return ResolveSourceBackedEvidenceExplorationRagCallBudget(
            query,
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            forceBroadenedExploration);
    }

    internal static int ResolveSourceBackedEvidenceExplorationTimeoutMsForTests(string query, string language)
    {
        var toolResults = new ToolResults();
        return ResolveSourceBackedEvidenceExplorationTimeoutMs(
            query,
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language));
    }

    internal static int ResolveSourceBackedContextReadPassLimitForTests(string query)
        => ResolveSourceBackedContextReadPassLimit(query);

    internal static int ResolveSourceBackedContextReadPassBatchLimitForTests(string query)
        => ResolveSourceBackedContextReadPassBatchLimit(query);

    internal static string[] BuildDocumentaryProbeRetrievalQueriesForTests(string query)
        => BuildDocumentaryProbeRetrievalQueries(query);

    internal static bool ShouldExpandDocumentaryProbeRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandDocumentaryProbeRetrieval(toolResults, query, language);

    internal static bool ShouldUseWriterForDocumentaryProbeAnswerForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForDocumentaryProbeAnswer(toolResults, query);

    internal static bool ShouldDeferDocumentaryProbeWriterForBroaderExplorationForTests(
        ToolResults toolResults,
        string query,
        string language = "fr")
        => ShouldDeferDocumentaryProbeWriterForBroaderExploration(toolResults, query, language);

    internal static bool IsBetterDocumentaryProbeCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterDocumentaryProbeCoverage(current, candidate, query, language);

    internal static string[] BuildSourceBackedEvidenceExpansionRetrievalQueriesForTests(string query)
        => BuildSourceBackedEvidenceExpansionRetrievalQueries(query);

    internal static int NormalizeSourceBackedPlanningTopKForTests(int? requestedTopK, string query)
        => NormalizeSourceBackedPlanningTopK(requestedTopK, query);

    internal static int ResolveSourceBackedPlanningTargetItemCountForTests(string query)
        => ResolveSourceBackedPlanningTargetItemCount(query);

    internal static int ResolveSourceBackedDocumentScopedAnchorFollowupLimitForTests(string query)
        => ResolveSourceBackedDocumentScopedAnchorFollowupLimit(query);

    internal static int ResolveMinimumSourceBackedPlanningCandidateCountForTests(string query, int targetSlots, bool hasStructuredAxes)
        => ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);
}
#endif
