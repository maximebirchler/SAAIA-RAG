using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldUseWriterForBroadSourceBackedPlanning(ToolResults toolResults, string? query, string language = "fr")
        => !string.IsNullOrWhiteSpace(query)
           && !LooksLikeSourceBackedCountdownPlanningRequest(query)
           && !LooksLikeSourceBackedVerificationChecklistRequest(query)
           && !LooksLikeSourceBackedPairingRecommendationRequest(query)
           && LooksLikeAnyDocumentaryPlanningRequest(query)
           && !ShouldRequireDeterministicStructuredPlanningAnswer(query)
           && EvaluateSourceBackedPlanningCoverage(toolResults, query, language).IsAdequate;

    private static bool ShouldPreferWriterForPolishedSourceBackedAnswer(ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query)
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || LooksLikeExactPassageOrCitationRequest(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(item.Result)))
        {
            return false;
        }

        if (ShouldRequireDeterministicStructuredPlanningAnswer(query))
            return false;

        return LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
            || LooksLikeSourceBackedActionRequest(query)
            || LooksLikeDocumentaryContentRequest(query)
            || ShouldUseSourceBackedExtractiveAnswer(query, toolResults);
    }

    private static bool ShouldAllowWriterForPartialSourceBackedPlanning(ToolResults toolResults, string? query, string language = "fr")
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        var resolvedFromEnvelope = !string.Equals(
            CollapseWhitespace(intentQuery),
            CollapseWhitespace(query),
            StringComparison.OrdinalIgnoreCase);
        var hasSourceBackedConfirmationEnvelopeMarkers =
            query.Contains("PREVIOUS_USER_REQUEST", StringComparison.OrdinalIgnoreCase)
            || query.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH", StringComparison.OrdinalIgnoreCase)
            || query.Contains("RESOLVED_REQUEST", StringComparison.OrdinalIgnoreCase);
        var previousEnvelopeRequest = TryExtractPreviousUserRequestFromEnvelope(query, out var previousRequest)
            ? previousRequest
            : string.Empty;
        var previousEnvelopeIsStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(previousEnvelopeRequest);

        if (LooksLikeSourceBackedCountdownPlanningRequest(intentQuery)
            || LooksLikeSourceBackedVerificationChecklistRequest(intentQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(intentQuery)
            || LooksLikeStrictCertificationOrExactProofRequest(intentQuery)
            || (!LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
                && !ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
                && !previousEnvelopeIsStructuredPlanning))
        {
            return false;
        }

        var requiresStructuredFullCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
            || previousEnvelopeIsStructuredPlanning;
        var coverageQuery = previousEnvelopeIsStructuredPlanning
            ? previousEnvelopeRequest
            : intentQuery;
        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, coverageQuery, language);
        if (coverage.CandidateCount <= 0 || coverage.DistinctSourcePages <= 0)
            return false;

        if (coverage.IsAdequate)
            return true;

        if (requiresStructuredFullCoverage)
        {
            var hasCompleteCandidateBank = coverage.CandidateCount >= coverage.MinimumCandidates
                && coverage.DistinctSourcePages >= Math.Min(coverage.MinimumCandidates, Math.Max(1, coverage.TargetSlots));
            if (!hasCompleteCandidateBank)
                hasCompleteCandidateBank = HasCompleteStrictStructuredPlanningCandidateBank(
                    toolResults,
                    coverageQuery,
                    language,
                    coverage);
            if (hasCompleteCandidateBank)
            {
                ClientLog.Info(
                    "ToolAgent writer partial planning gate: decision=allow|reason=complete_candidate_bank"
                    + $"|resolvedFromEnvelope={FormatPlanningTraceBool(resolvedFromEnvelope)}"
                    + $"|previousStructured={FormatPlanningTraceBool(previousEnvelopeIsStructuredPlanning)}"
                    + $"|candidates={coverage.CandidateCount}"
                    + $"|minimum={coverage.MinimumCandidates}"
                    + $"|sourcePages={coverage.DistinctSourcePages}");
                return true;
            }

            var hasExplicitStructuredAxes = DetectRequestedDayAxisLabels(coverageQuery, language).Count > 0
                && DetectRequestedPlanningSlotAxisLabels(coverageQuery, language).Count > 0;
            if (!hasExplicitStructuredAxes
                && HasUsefulPartialSourceBackedPlanningCoverage(
                    coverage,
                    searchWasBroadened: false,
                    searchWasExpanded: false))
            {
                ClientLog.Info(
                    "ToolAgent writer partial planning gate: decision=allow|reason=useful_ungridded_partial_bank"
                    + $"|resolvedFromEnvelope={FormatPlanningTraceBool(resolvedFromEnvelope)}"
                    + $"|previousStructured={FormatPlanningTraceBool(previousEnvelopeIsStructuredPlanning)}"
                    + $"|candidates={coverage.CandidateCount}"
                    + $"|minimum={coverage.MinimumCandidates}"
                    + $"|targetSlots={coverage.TargetSlots}"
                    + $"|sourcePages={coverage.DistinctSourcePages}");
                return true;
            }

            var searchWasBroadened = IsBroadenedSourceSearchConfirmationEnvelope(query);
            var searchWasExpanded = HasExpandedSourceBackedSearchEvidence(toolResults);
            var minimumExpandedPartialCandidates = ResolveMinimumUsefulExpandedStructuredPlanningCandidatesForWriter(coverage);
            var minimumExpandedPartialLeads = Math.Min(4, minimumExpandedPartialCandidates);
            if (searchWasExpanded
                && HasUsefulExpandedStructuredPlanningBankForWriter(coverage)
                && HasDiverseStructuredPlanningCandidateLeads(
                    toolResults,
                    coverageQuery,
                    language,
                    minimumExpandedPartialLeads))
            {
                ClientLog.Info(
                    "ToolAgent writer partial planning gate: decision=allow|reason=useful_expanded_partial_bank"
                    + $"|resolvedFromEnvelope={FormatPlanningTraceBool(resolvedFromEnvelope)}"
                    + $"|previousStructured={FormatPlanningTraceBool(previousEnvelopeIsStructuredPlanning)}"
                    + $"|expanded={FormatPlanningTraceBool(searchWasExpanded)}"
                    + $"|minimumPartial={minimumExpandedPartialCandidates}"
                    + $"|minimumLeads={minimumExpandedPartialLeads}"
                    + $"|candidates={coverage.CandidateCount}"
                    + $"|minimum={coverage.MinimumCandidates}"
                    + $"|targetSlots={coverage.TargetSlots}"
                    + $"|sourcePages={coverage.DistinctSourcePages}"
                    + $"|richEvidence={coverage.RichEvidenceCount}"
                    + $"|richness={coverage.EvidenceRichnessScore}");
                return true;
            }

            var allowConfirmedStructuredPartial = (searchWasBroadened
                    || previousEnvelopeIsStructuredPlanning
                    || resolvedFromEnvelope
                    || hasSourceBackedConfirmationEnvelopeMarkers)
                && (HasCompleteStrictStructuredPlanningCandidateBank(
                        toolResults,
                        coverageQuery,
                        language,
                        coverage,
                        minimumCandidateOverride: 2)
                    || HasUsefulPartialSourceBackedPlanningCoverage(
                        coverage,
                        searchWasBroadened: true,
                        searchWasExpanded: searchWasExpanded));
            ClientLog.Info(
                "ToolAgent writer partial planning gate: decision="
                + (allowConfirmedStructuredPartial ? "allow" : "reject")
                + "|reason=structured_partial"
                + $"|resolvedFromEnvelope={FormatPlanningTraceBool(resolvedFromEnvelope)}"
                + $"|envelopeMarkers={FormatPlanningTraceBool(hasSourceBackedConfirmationEnvelopeMarkers)}"
                + $"|previousStructured={FormatPlanningTraceBool(previousEnvelopeIsStructuredPlanning)}"
                + $"|broadened={FormatPlanningTraceBool(searchWasBroadened)}"
                + $"|expanded={FormatPlanningTraceBool(searchWasExpanded)}"
                + $"|candidates={coverage.CandidateCount}"
                + $"|minimum={coverage.MinimumCandidates}"
                + $"|sourcePages={coverage.DistinctSourcePages}");
            return allowConfirmedStructuredPartial;
        }

        var hasExplicitStructure = DetectRequestedDayAxisLabels(intentQuery, language).Count > 0
            || DetectRequestedPlanningSlotAxisLabels(intentQuery, language).Count > 0;
        var asksForSynthesis = LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(intentQuery)
            || LooksLikeBroadSourceBackedCompositionRequest(intentQuery);

        return hasExplicitStructure
            || asksForSynthesis
            || coverage.CandidateCount >= Math.Min(2, coverage.MinimumCandidates);
    }

    private static bool ShouldAllowStructuredPlanningWriterRepairFromVisibleSourceInventory(
        ToolResults toolResults,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search" && HasRagHits(item.Result)))
        {
            return false;
        }

        return BuildVisibleStructuredPlanningSourcePool(toolResults, query).Count > 0;
    }

    private static bool HasCompleteStrictStructuredPlanningCandidateBank(
        ToolResults toolResults,
        string? query,
        string language,
        SourceBackedPlanningCoverage coverage,
        int? minimumCandidateOverride = null)
    {
        var requiredCandidates = Math.Max(1, minimumCandidateOverride ?? coverage.MinimumCandidates);
        if (requiredCandidates <= 0)
            return true;

        var poolSize = ResolveSourceBackedPlanningCandidatePoolSize(
            query,
            Math.Max(coverage.TargetSlots, requiredCandidates));
        var candidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                poolSize,
                language)
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static candidate => candidate.Score)
                .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
                .ThenByDescending(static candidate => candidate.Hit.Score)
                .First())
            .Take(requiredCandidates)
            .ToList();
        if (candidates.Count < requiredCandidates)
            return false;

        var distinctSourcePages = candidates
            .Select(static candidate => BuildRagHitVisiblePageMergeKey(candidate.Hit))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinctSourcePages >= Math.Min(requiredCandidates, Math.Max(1, coverage.TargetSlots));
    }

    private static bool HasDiverseStructuredPlanningCandidateLeads(
        ToolResults toolResults,
        string? query,
        string language,
        int minimumDistinctLeads)
    {
        if (minimumDistinctLeads <= 1)
            return true;

        var targetSlots = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        var poolSize = ResolveSourceBackedPlanningCandidatePoolSize(query, Math.Max(targetSlots, minimumDistinctLeads));
        return SelectSourceBackedPlanningCandidates(toolResults, query, poolSize, language)
            .Select(BuildSourceBackedPlanningCandidateLeadKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(minimumDistinctLeads)
            .Count() >= minimumDistinctLeads;
    }

    private static int ResolveMinimumUsefulExpandedStructuredPlanningCandidatesForWriter(SourceBackedPlanningCoverage coverage)
    {
        var targetSlots = Math.Max(1, coverage.TargetSlots);
        var minimumCandidates = Math.Max(1, coverage.MinimumCandidates);
        return Math.Min(
            minimumCandidates,
            Math.Max(6, (int)Math.Ceiling(targetSlots * 0.50d)));
    }

    private static bool HasUsefulExpandedStructuredPlanningBankForWriter(SourceBackedPlanningCoverage coverage)
    {
        if (!HasUsefulPartialSourceBackedPlanningCoverage(
                coverage,
                searchWasBroadened: false,
                searchWasExpanded: true))
        {
            return false;
        }

        var targetSlots = Math.Max(1, coverage.TargetSlots);
        if (targetSlots < 10)
            return false;

        var minimumUsefulCandidates = ResolveMinimumUsefulExpandedStructuredPlanningCandidatesForWriter(coverage);
        var minimumUsefulPages = Math.Min(
            minimumUsefulCandidates,
            Math.Max(3, (int)Math.Ceiling(minimumUsefulCandidates * 0.75d)));

        return coverage.CandidateCount >= minimumUsefulCandidates
            && coverage.DistinctSourcePages >= minimumUsefulPages
            && coverage.HasRequiredAnchor;
    }

    private static bool HasUsefulPartialSourceBackedPlanningCoverage(
        SourceBackedPlanningCoverage coverage,
        bool searchWasBroadened,
        bool searchWasExpanded)
    {
        if (coverage.CandidateCount <= 0 || coverage.DistinctSourcePages <= 0)
            return false;

        if (coverage.IsAdequate)
            return true;

        var searchWasExtended = searchWasBroadened || searchWasExpanded;
        var minimumPartialCandidates = Math.Min(
            coverage.MinimumCandidates,
            searchWasBroadened
                ? Math.Max(2, (int)Math.Ceiling(coverage.MinimumCandidates * 0.25))
                : searchWasExpanded
                    ? Math.Max(4, (int)Math.Ceiling(coverage.MinimumCandidates * 0.35))
                    : Math.Max(4, (int)Math.Ceiling(coverage.MinimumCandidates * 0.5)));
        var hasRequiredAnchorOrUsefulPartialMap =
            coverage.HasRequiredAnchor
            || (searchWasExtended
                && coverage.DistinctSourcePages >= 2
                && (coverage.RichEvidenceCount >= 1
                    || coverage.EvidenceRichnessScore >= 6
                    || coverage.DistinctSourcePages >= 3));
        var hasDiverseBroadenedPartialBank =
            searchWasBroadened
            && coverage.CandidateCount >= 3
            && coverage.DistinctSourcePages >= 2;
        var hasStructuredPartialBank =
            coverage.TargetSlots >= 4
            && coverage.CandidateCount >= Math.Min(
                coverage.MinimumCandidates,
                Math.Max(3, (int)Math.Ceiling(coverage.TargetSlots * 0.35d)))
            && coverage.DistinctSourcePages >= Math.Min(3, coverage.CandidateCount)
            && (coverage.RichEvidenceCount >= Math.Min(2, coverage.CandidateCount)
                || coverage.EvidenceRichnessScore >= Math.Min(8, coverage.CandidateCount));
        var hasUsablePartialBank = coverage.CandidateCount >= minimumPartialCandidates
            && coverage.DistinctSourcePages >= Math.Min(3, minimumPartialCandidates)
            && (hasRequiredAnchorOrUsefulPartialMap || hasDiverseBroadenedPartialBank);
        var hasRichPartialBank = coverage.RichEvidenceCount >= 2
            && coverage.DistinctSourcePages >= 2
            && hasRequiredAnchorOrUsefulPartialMap;
        return hasUsablePartialBank || hasRichPartialBank || hasDiverseBroadenedPartialBank || hasStructuredPartialBank;
    }

    private static bool ShouldExpandSourceBackedPlanningRetrieval(ToolResults toolResults, string? query, string language)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(query) && !ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return false;

        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
        return !coverage.IsAdequate;
    }

    private static bool IsBetterSourceBackedPlanningCoverage(
        ToolResults current,
        ToolResults candidate,
        string? query,
        string language)
    {
        var currentCoverage = EvaluateSourceBackedPlanningCoverage(current, query, language);
        var candidateCoverage = EvaluateSourceBackedPlanningCoverage(candidate, query, language);
        return candidateCoverage.Score > currentCoverage.Score;
    }
}
