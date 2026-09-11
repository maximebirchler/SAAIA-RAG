using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string TryBuildInsufficientStructuredPlanningBeforeWriterAnswer(ToolResults toolResults, string? query, string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
            || !toolResults.Items.Any(static item => item.ToolName is ("rag.search" or "rag.multi_search") && HasRagHits(item.Result)))
        {
            return string.Empty;
        }

        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, intentQuery, language);
        var nearbyHitCount = EnumerateRagHitSummaries(toolResults)
            .Count(ShouldExposeHitForSourceBackedEvidenceDiscovery);
        var searchWasBroadened = IsBroadenedSourceSearchConfirmationEnvelope(query);
        var searchWasExpanded = HasExpandedSourceBackedSearchEvidence(toolResults);
        if (ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language))
            return string.Empty;

        if (!coverage.IsAdequate)
        {
            if (HasUsefulPartialSourceBackedPlanningCoverage(coverage, searchWasBroadened, searchWasExpanded))
                return string.Empty;
        }

        if (coverage.IsAdequate
            || (searchWasBroadened
                && HasUsefulPartialSourceBackedPlanningCoverage(coverage, searchWasBroadened, searchWasExpanded)))
        {
            return string.Empty;
        }

        return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query,
                intentQuery,
                nearbyHitCount,
                searchWasExpanded);
    }

    private static string? BuildUsefulPartialStructuredPlanningAnswerBeforeWriter(
        ToolResults toolResults,
        string? intentQuery,
        string language,
        SourceBackedPlanningCoverage coverage,
        bool searchWasBroadened,
        bool searchWasExpanded)
    {
        if (!HasUsefulPartialSourceBackedPlanningCoverage(coverage, searchWasBroadened, searchWasExpanded))
            return null;

        var draft = BuildSourceBackedPlanningDraft(
            toolResults,
            language,
            minItems: 1,
            query: intentQuery,
            allowPartialStructuredPlanningDraft: true);
        return HasTrustedPartialSourceBackedPlanningDraftCoverage(
                draft,
                coverage,
                intentQuery,
                searchWasBroadened,
                searchWasExpanded)
            ? draft.Answer
            : null;
    }

    private static (ToolResults ToolResults, SourceBackedPlanningCoverage? RawCoverage, SourceBackedPlanningCoverage? WriterCoverage, string Basis) ResolveStructuredPlanningWriterGuardToolResults(
        ToolResults rawToolResults,
        ToolResults writerToolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || !rawToolResults.Items.Any(static item => item.ToolName is ("rag.search" or "rag.multi_search") && HasRagHits(item.Result)))
        {
            return (writerToolResults, null, null, "writer_tool_results");
        }

        var rawCoverage = EvaluateSourceBackedPlanningCoverage(rawToolResults, query, language);
        var writerCoverage = EvaluateSourceBackedPlanningCoverage(writerToolResults, query, language);
        if (rawCoverage.IsAdequate)
            return (rawToolResults, rawCoverage, writerCoverage, "raw_tool_results");

        var searchWasBroadened = IsBroadenedSourceSearchConfirmationEnvelope(query);
        var rawHasUsefulPartial = HasUsefulPartialSourceBackedPlanningCoverage(
            rawCoverage,
            searchWasBroadened,
            HasExpandedSourceBackedSearchEvidence(rawToolResults));
        var writerHasUsefulPartial = HasUsefulPartialSourceBackedPlanningCoverage(
            writerCoverage,
            searchWasBroadened,
            HasExpandedSourceBackedSearchEvidence(writerToolResults));
        var rawHasBetterPartialEvidence =
            rawHasUsefulPartial
            && (!writerHasUsefulPartial
                || rawCoverage.CandidateCount > writerCoverage.CandidateCount
                || rawCoverage.DistinctSourcePages > writerCoverage.DistinctSourcePages);

        return rawHasBetterPartialEvidence
            ? (rawToolResults, rawCoverage, writerCoverage, "raw_tool_results_partial")
            : (writerToolResults, rawCoverage, writerCoverage, "writer_tool_results");
    }
}
