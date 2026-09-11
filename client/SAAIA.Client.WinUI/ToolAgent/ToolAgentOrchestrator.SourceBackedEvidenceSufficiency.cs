namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record SourceBackedEvidenceSufficiency(
        bool IsSufficient,
        bool ShouldExplore,
        string Kind,
        string Reason,
        int Score,
        int UsableHitCount,
        int DistinctDocumentCount,
        int DistinctSourcePageCount,
        int NavigationAnchorCount,
        int CandidateCount,
        int MinimumCandidateCount,
        int TargetSlotCount,
        bool HasRequiredAnchor);

    private static SourceBackedEvidenceSufficiency AnalyzeSourceBackedEvidenceSufficiency(
        ToolResults toolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query))
        {
            return new SourceBackedEvidenceSufficiency(
                true,
                false,
                "none",
                "not_explorable_request",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                true);
        }

        if (UsesSourceBackedPlanningCoverage(query))
        {
            var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
            var reason = coverage.IsAdequate
                ? "adequate_planning_coverage"
                : coverage.CandidateCount <= 0
                    ? "no_planning_candidates"
                    : !coverage.HasRequiredAnchor
                        ? "missing_requested_anchor"
                        : coverage.CandidateCount < coverage.MinimumCandidates
                            ? "too_few_distinct_candidates"
                            : coverage.DistinctSourcePages < Math.Min(3, coverage.MinimumCandidates)
                                ? "low_source_page_diversity"
                                : "partial_planning_evidence";

            return new SourceBackedEvidenceSufficiency(
                coverage.IsAdequate,
                !coverage.IsAdequate,
                "planning",
                reason,
                coverage.Score,
                coverage.CandidateCount,
                0,
                coverage.DistinctSourcePages,
                CountSourceBackedNavigationDiscoveryAnchors(toolResults),
                coverage.CandidateCount,
                coverage.MinimumCandidates,
                coverage.TargetSlots,
                coverage.HasRequiredAnchor);
        }

        var canBenefitFromDiversity =
            LooksLikeSourceBackedActionRequest(query)
            || LooksLikeDocumentaryContentRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
        if (!canBenefitFromDiversity)
        {
            return new SourceBackedEvidenceSufficiency(
                true,
                false,
                "broad",
                "request_not_diversity_sensitive",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                true);
        }

        var broadCoverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        var broadScore = ComputeBroadSourceBackedCoverageScore(broadCoverage);
        var broadReason = broadCoverage.IsAdequate
            ? "adequate_broad_coverage"
            : broadCoverage.UsableHitCount <= 0
                ? "no_usable_hits"
                : broadCoverage.DistinctSourcePageCount < Math.Min(2, ResolveMinimumBroadSourceBackedSynthesisHitCount(query))
                    ? "low_source_page_diversity"
                    : broadCoverage.RichEvidenceCount <= 0
                        ? "low_evidence_richness"
                        : "partial_broad_evidence";

        return new SourceBackedEvidenceSufficiency(
            broadCoverage.IsAdequate,
            !broadCoverage.IsAdequate,
            "broad",
            broadReason,
            broadScore,
            broadCoverage.UsableHitCount,
            broadCoverage.DistinctDocumentCount,
            broadCoverage.DistinctSourcePageCount,
            CountSourceBackedNavigationDiscoveryAnchors(toolResults),
            broadCoverage.UsableHitCount,
            ResolveMinimumBroadSourceBackedSynthesisHitCount(query),
            0,
            true);
    }

    private static bool HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(
        SourceBackedEvidenceSufficiency analysis,
        string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || !string.Equals(analysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!analysis.IsSufficient)
            return false;

        var target = Math.Max(
            Math.Max(1, analysis.TargetSlotCount),
            Math.Max(1, analysis.MinimumCandidateCount));
        if (analysis.CandidateCount < target || analysis.UsableHitCount < target)
            return false;

        if (!analysis.HasRequiredAnchor)
            return false;

        return analysis.DistinctSourcePageCount >= target;
    }

    private static bool ShouldDeferSparseSourceBackedPlanningAnchorFollowup(
        SourceBackedEvidenceSufficiency analysis,
        string? query)
    {
        if (!UsesSourceBackedPlanningCoverage(query)
            || !string.Equals(analysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(analysis, query))
            return false;

        var target = Math.Max(
            Math.Max(1, analysis.MinimumCandidateCount),
            Math.Max(1, analysis.TargetSlotCount));
        if (target <= 1)
            return false;

        if (analysis.CandidateCount <= 0 && analysis.UsableHitCount <= 0)
            return true;

        var sparseCandidateThreshold = Math.Min(
            target,
            Math.Max(4, (int)Math.Ceiling(target * 0.70d)));
        if (analysis.CandidateCount < sparseCandidateThreshold)
            return true;

        var sparsePageThreshold = Math.Min(
            sparseCandidateThreshold,
            Math.Max(3, (int)Math.Ceiling(target * 0.35d)));
        return analysis.CandidateCount < target
               && analysis.DistinctSourcePageCount < sparsePageThreshold;
    }

    private static bool ShouldAttemptSourceBackedAnchorFollowupOutsideCommittedPass(
        SourceBackedEvidenceSufficiency analysis,
        string? query,
        bool acceptedAnyExplorationPass)
    {
        if (!UsesSourceBackedPlanningCoverage(query))
            return true;

        if (!string.Equals(analysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(analysis, query))
            return false;

        if (ShouldDeferSparseSourceBackedPlanningAnchorFollowup(analysis, query))
            return false;

        return acceptedAnyExplorationPass;
    }

    private static bool ShouldExpandSourceBackedEvidenceRetrieval(ToolResults toolResults, string? query, string language)
        => AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language).ShouldExplore;

    private static bool IsBetterSourceBackedEvidenceCoverage(
        ToolResults current,
        ToolResults candidate,
        string? query,
        string language)
    {
        if (UsesSourceBackedPlanningCoverage(query))
            return IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

        var currentCoverage = EvaluateBroadSourceBackedSynthesisCoverage(current, query);
        var candidateCoverage = EvaluateBroadSourceBackedSynthesisCoverage(candidate, query);
        return ComputeBroadSourceBackedCoverageScore(candidateCoverage) > ComputeBroadSourceBackedCoverageScore(currentCoverage);
    }
}
