namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int ComputeBroadSourceBackedCoverageScore(BroadSourceBackedSynthesisCoverage coverage)
        => (coverage.UsableHitCount * 8)
           + (coverage.DistinctSourcePageCount * 5)
           + (coverage.DistinctDocumentCount * 3)
           + (coverage.RichEvidenceCount * 4)
           + Math.Min(18, coverage.EvidenceRichnessScore)
           + (coverage.IsAdequate ? 20 : 0);

    private static bool CandidateSourceBackedEvidenceAddsUsefulDiversity(
        SourceBackedEvidenceSufficiency current,
        SourceBackedEvidenceSufficiency candidate)
    {
        if (!string.Equals(current.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase))
            return candidate.Score > current.Score;

        if (candidate.CandidateCount >= candidate.MinimumCandidateCount
            && current.CandidateCount < current.MinimumCandidateCount)
        {
            return true;
        }

        if (candidate.NavigationAnchorCount > current.NavigationAnchorCount
            && candidate.Score >= current.Score - 12)
        {
            return true;
        }

        if (candidate.CandidateCount > current.CandidateCount
            && candidate.DistinctSourcePageCount >= current.DistinctSourcePageCount)
        {
            return true;
        }

        if (candidate.DistinctSourcePageCount > current.DistinctSourcePageCount
            && candidate.CandidateCount >= current.CandidateCount)
        {
            return true;
        }

        if (candidate.DistinctDocumentCount > current.DistinctDocumentCount
            && candidate.UsableHitCount >= current.UsableHitCount)
        {
            return true;
        }

        return !current.HasRequiredAnchor && candidate.HasRequiredAnchor;
    }

    private static bool CandidateSourceBackedEvidenceAddsUsefulOrientation(
        ToolResults current,
        ToolResults candidate,
        SourceBackedEvidenceSufficiency currentAnalysis,
        SourceBackedEvidenceSufficiency candidateAnalysis,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var needsExplorationMap =
            LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
        if (!needsExplorationMap)
            return false;

        var scoreTolerance = UsesSourceBackedPlanningCoverage(query) ? 24 : 18;
        if (candidateAnalysis.Score < currentAnalysis.Score - scoreTolerance)
            return false;

        var currentPivots = CountSourceBackedOrientationPivots(current, query, language);
        var candidatePivots = CountSourceBackedOrientationPivots(candidate, query, language);
        if (candidatePivots <= currentPivots)
            return false;

        var currentHasFollowups = HasSourceBackedRouteAnchorFollowupQueries(current, query, language);
        var candidateHasFollowups = HasSourceBackedRouteAnchorFollowupQueries(candidate, query, language);
        if (!currentHasFollowups && candidateHasFollowups)
            return true;

        var minimumUsefulPivots = Math.Min(8, Math.Max(3, candidateAnalysis.MinimumCandidateCount));
        if (candidatePivots >= minimumUsefulPivots)
            return true;

        return candidatePivots >= currentPivots + 2
            && candidateAnalysis.UsableHitCount >= Math.Max(0, currentAnalysis.UsableHitCount - 1);
    }

    private static bool CandidateSourceBackedEvidenceAddsExplorationMaterial(
        ToolResults current,
        ToolResults candidate,
        string? query,
        string language,
        bool forceBroadenedExploration = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var needsBroadMaterial =
            LooksLikeSourceBackedBroadResearchRequest(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
        if (!needsBroadMaterial)
            return false;

        var currentPageKeys = EnumerateRagHitSummaries(current)
            .Where(IsSourceBackedExplorationMaterialHit)
            .Select(BuildRagHitVisiblePageMergeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentDocumentKeys = EnumerateRagHitSummaries(current)
            .Where(IsSourceBackedExplorationMaterialHit)
            .Select(BuildSourceBackedExplorationMaterialDocumentKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedHits = EnumerateRagHitSummaries(candidate)
            .Where(IsSourceBackedExplorationMaterialHit)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
                .ThenByDescending(static hit => hit.Score)
                .First())
            .Where(hit => !currentPageKeys.Contains(BuildRagHitVisiblePageMergeKey(hit)))
            .Take(12)
            .ToList();
        if (addedHits.Count == 0)
            return false;

        var addedPageCount = addedHits
            .Select(BuildRagHitVisiblePageMergeKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var addedDocumentCount = addedHits
            .Select(BuildSourceBackedExplorationMaterialDocumentKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Where(key => !currentDocumentKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var richOrStructuredCount = addedHits.Count(IsRichEnoughSourceBackedExplorationMaterialHit);
        var minimumNewPages = forceBroadenedExploration || IsBroadenedSourceSearchConfirmationEnvelope(query) ? 1 : 2;

        if ((forceBroadenedExploration || IsBroadenedSourceSearchConfirmationEnvelope(query))
            && addedPageCount >= 1)
        {
            return true;
        }

        if (addedPageCount >= minimumNewPages && (richOrStructuredCount > 0 || addedDocumentCount > 0))
            return true;

        if (addedDocumentCount >= 2)
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query)
            && addedPageCount >= 2
            && richOrStructuredCount > 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsSourceBackedExplorationMaterialHit(RagHitSummary hit)
    {
        if (hit.PageStart <= 0)
            return false;

        if (LooksLikeNavigationOnlyHit(hit) || LooksLikePageReferenceOnlyHit(hit))
            return LooksLikeResolvedRouteTargetHit(hit);

        if (LooksLikeLowSignalContentCandidateHit(hit) && !BackendSelectionHintsPreferUsableEvidence(hit))
            return false;

        var evidence = CollapseWhitespace(GetBestRagEvidenceText(hit));
        return evidence.Length >= 72
            || HasContentCardEvidenceFacts(hit)
            || HasContentCardQuantityEvidence(new[] { hit })
            || HasContentCardScalableQuantityEvidence(hit)
            || ComputeSourceBackedEvidenceRichnessScore(hit) >= 3;
    }

    private static bool IsRichEnoughSourceBackedExplorationMaterialHit(RagHitSummary hit)
        => ComputeSourceBackedEvidenceRichnessScore(hit) >= 4
           || HasContentCardEvidenceFacts(hit)
           || HasContentCardQuantityEvidence(new[] { hit })
           || HasContentCardScalableQuantityEvidence(hit);

    private static string BuildSourceBackedExplorationMaterialDocumentKey(RagHitSummary hit)
    {
        var path = NormalizeVisibleSourcePathIdentity(hit.DocPath);
        if (!string.IsNullOrWhiteSpace(path))
            return $"path:{path}";

        var hash = CollapseWhitespace(hit.SourceHash ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(hash))
            return $"hash:{hash}";

        var docId = CollapseWhitespace(hit.DocId ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(docId))
            return $"id:{docId}";

        var name = NormalizeLexicalLookup(hit.DocName);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : $"name:{name}";
    }

    private static int CountSourceBackedOrientationPivots(ToolResults toolResults, string query, string language)
        => ExtractSourceBackedRouteAnchorFollowupTitles(toolResults, query)
            .Concat(ExtractSourceBackedDocumentNavigationFollowupTitles(toolResults, query))
            .Concat(ExtractSourceBackedTreeFollowupTitles(toolResults, query))
            .Concat(ExtractSourceBackedSummaryFollowupTitles(toolResults, query))
            .Select(NormalizeLexicalLookup)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(80)
            .Count();

    private static int CountSourceBackedNavigationDiscoveryAnchors(ToolResults toolResults)
        => EnumerateRagHitSummaries(toolResults)
            .Where(IsRouteDiscoveryAnchorHit)
            .GroupBy(hit =>
            {
                var title = ExtractRouteDiscoveryTitleCue(hit);
                return $"{hit.DocPath}|{hit.DocName}|{hit.PageStart}|{NormalizeLexicalLookup(title)}";
            }, StringComparer.OrdinalIgnoreCase)
            .Count();
}
