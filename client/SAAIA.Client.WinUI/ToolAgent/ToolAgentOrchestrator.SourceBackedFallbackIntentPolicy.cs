using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string ResolveSourceBackedFallbackIntentQuery(string query)
        => IsBroadenedSourceSearchConfirmationEnvelope(query)
            && TryExtractPreviousUserRequestFromEnvelope(query, out var previousRequest)
            && !string.IsNullOrWhiteSpace(previousRequest)
                ? previousRequest
                : query;

    private static bool ShouldAvoidRawSourceBackedFallback(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return IsBroadenedSourceSearchConfirmationEnvelope(query)
            || RequiresStructuredSourceBackedPlanningCoverage(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query);
    }

    private static bool ShouldAllowReadableSourceBackedPartialFallback(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        return IsBroadenedSourceSearchConfirmationEnvelope(query)
            || LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery);
    }

    private static bool ShouldRequireWriterForBroadDocumentaryFinal(ToolResults toolResults, string? query, string language = "fr")
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        var isBroadenedSourceSearchConfirmation = IsBroadenedSourceSearchConfirmationEnvelope(query);
        var previousEnvelopeRequest = isBroadenedSourceSearchConfirmation
            && TryExtractPreviousUserRequestFromEnvelope(query, out var previousRequest)
                ? previousRequest
                : string.Empty;
        var previousEnvelopeIsStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(previousEnvelopeRequest);
        var previousEnvelopeRequiresDeterministicPlanning = ShouldRequireDeterministicStructuredPlanningAnswer(previousEnvelopeRequest);
        var intentRequiresDeterministicPlanning = ShouldRequireDeterministicStructuredPlanningAnswer(intentQuery);
        var structuredCoverageQuery = previousEnvelopeIsStructuredPlanning ? previousEnvelopeRequest : intentQuery;
        if (!isBroadenedSourceSearchConfirmation
            && (LooksLikeExactPassageOrCitationRequest(intentQuery)
                || LooksLikeStrictCertificationOrExactProofRequest(intentQuery)
                || LooksLikeCorpusClaimVerificationRequest(intentQuery)
                || LooksLikeSourceBackedCountdownPlanningRequest(intentQuery)
                || LooksLikeSourceBackedVerificationChecklistRequest(intentQuery)))
        {
            return false;
        }

        if (previousEnvelopeIsStructuredPlanning || ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery))
        {
            return toolResults.Items.Any(static item => item.ToolName is ("rag.search" or "rag.multi_search") && HasRagHits(item.Result))
                && (ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, structuredCoverageQuery, language)
                    || ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language));
        }

        if (isBroadenedSourceSearchConfirmation
            && HasAtLeastDistinctUsableSourcePages(toolResults, 2))
        {
            return true;
        }

        if (ShouldAvoidRawSourceBackedFallback(query) || ShouldAvoidRawSourceBackedFallback(intentQuery))
            return true;

        if (!toolResults.Items.Any(static item => item.ToolName is ("rag.search" or "rag.multi_search") && HasRagHits(item.Result)))
            return false;

        return ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, intentQuery)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, intentQuery)
            || ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, intentQuery, language);
    }

    private static bool ShouldRouteSourceBackedAnswerThroughWriter(ToolResults toolResults, string? query, string language = "fr")
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        var isBroadenedSourceSearchConfirmation = IsBroadenedSourceSearchConfirmationEnvelope(query);
        var previousEnvelopeRequest = isBroadenedSourceSearchConfirmation
            && TryExtractPreviousUserRequestFromEnvelope(query, out var previousRequest)
                ? previousRequest
                : string.Empty;
        var previousEnvelopeIsStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(previousEnvelopeRequest);
        var previousEnvelopeRequiresDeterministicPlanning = ShouldRequireDeterministicStructuredPlanningAnswer(previousEnvelopeRequest);
        var intentRequiresDeterministicPlanning = ShouldRequireDeterministicStructuredPlanningAnswer(intentQuery);
        var structuredCoverageQuery = previousEnvelopeIsStructuredPlanning ? previousEnvelopeRequest : intentQuery;
        if (!isBroadenedSourceSearchConfirmation
            && (LooksLikeExactPassageOrCitationRequest(intentQuery)
                || LooksLikeStrictCertificationOrExactProofRequest(intentQuery)
                || LooksLikeCorpusClaimVerificationRequest(intentQuery)
                || LooksLikeSourceBackedCountdownPlanningRequest(intentQuery)
                || LooksLikeSourceBackedVerificationChecklistRequest(intentQuery)))
        {
            return false;
        }

        if (!toolResults.Items.Any(static item => item.ToolName is ("rag.search" or "rag.multi_search") && HasRagHits(item.Result)))
            return false;

        if (previousEnvelopeIsStructuredPlanning || ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery))
        {
            return ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, structuredCoverageQuery, language)
                || ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language);
        }

        if (isBroadenedSourceSearchConfirmation
            && HasAtLeastDistinctUsableSourcePages(toolResults, 2))
        {
            return true;
        }

        if (ShouldAvoidRawSourceBackedFallback(query)
            || ShouldAvoidRawSourceBackedFallback(intentQuery)
            || ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, intentQuery)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, intentQuery)
            || ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, intentQuery, language))
        {
            return true;
        }

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return false;

        var distinctPages = usableHits
            .Select(BuildRagHitVisiblePageMergeKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasRichEvidence = usableHits.Any(HasRichSourceBackedEvidence);
        var expectsSynthesis =
            LooksLikeSourceBackedActionRequest(intentQuery)
            || LooksLikeDocumentaryContentRequest(intentQuery)
            || LooksLikeComparativeDocumentaryRequest(intentQuery)
            || LooksLikeBroadSynthesisRequestShape(intentQuery)
            || LooksLikeBroadSourceBackedCompositionRequest(intentQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(intentQuery)
            || LooksLikeSoftChoiceRecommendationRequest(intentQuery)
            || LooksLikeSourceBackedOptionRequest(intentQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(intentQuery)
            || LooksLikeGenericCollectionOrListRequest(intentQuery)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery);

        if (expectsSynthesis)
            return usableHits.Count >= 2 || distinctPages >= 2 || hasRichEvidence;

        return ShouldUseSourceBackedExtractiveAnswer(intentQuery, toolResults)
            && (usableHits.Count >= 2 || distinctPages >= 2);
    }

    private static bool HasAtLeastDistinctUsableSourcePages(ToolResults toolResults, int minimumPages)
    {
        if (minimumPages <= 0)
            return true;

        return EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Select(static hit =>
            {
                var key = BuildRagHitVisiblePageMergeKey(hit);
                if (!string.IsNullOrWhiteSpace(key))
                    return key;

                var source = !string.IsNullOrWhiteSpace(hit.DocPath)
                    ? hit.DocPath
                    : !string.IsNullOrWhiteSpace(hit.DocName)
                        ? hit.DocName
                        : !string.IsNullOrWhiteSpace(hit.SourceHash)
                            ? hit.SourceHash
                            : hit.ChunkId;
                return string.IsNullOrWhiteSpace(source)
                    ? string.Empty
                    : $"{NormalizeLooseLookup(source)}#{Math.Max(1, hit.PageStart)}";
            })
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(minimumPages)
            .Count() >= minimumPages;
    }
}
