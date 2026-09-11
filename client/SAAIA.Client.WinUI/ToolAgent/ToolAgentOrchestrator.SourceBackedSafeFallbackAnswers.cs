using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedSafeFallbackAnswer(
        ToolResults sourceToolResults,
        string userMessage,
        string language,
        bool shouldAvoidRaw)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
        var mustAvoidRaw =
            shouldAvoidRaw
            || ShouldAvoidRawSourceBackedFallback(userMessage)
            || ShouldAvoidRawSourceBackedFallback(intentQuery)
            || LooksLikeSourceBackedBroadResearchRequest(intentQuery);

        if (ShouldReturnInsufficientAfterConfirmedBroadPlanningFallback(
                sourceToolResults,
                userMessage,
                intentQuery,
                language,
                out var confirmedBroadPlanningHitCount))
        {
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                userMessage,
                intentQuery,
                confirmedBroadPlanningHitCount,
                searchAlreadyExpanded: true);
        }

        if (RequiresStructuredSourceBackedPlanningCoverage(intentQuery)
            && EvaluateSourceBackedPlanningCoverage(sourceToolResults, intentQuery, language).IsAdequate is false)
        {
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                userMessage,
                intentQuery,
                EnumerateRagHitSummaries(sourceToolResults).Count(),
                HasMergedOrMultipleRagEvidence(sourceToolResults));
        }

        var canUseReadableFallback =
            !mustAvoidRaw
            || shouldAvoidRaw
            || ShouldAllowReadableSourceBackedPartialFallback(userMessage)
            || ShouldAllowReadableSourceBackedPartialFallback(intentQuery)
            || LooksLikeGenericCollectionOrListRequest(intentQuery)
            || LooksLikeMultipleCandidateSynthesisRequest(intentQuery)
            || LooksLikeSourceBackedOptionRequest(intentQuery)
            || LooksLikeSoftChoiceRecommendationRequest(intentQuery)
            || LooksLikeSourceBackedPairingRecommendationRequest(intentQuery)
            || IsBroadenedSourceSearchConfirmationEnvelope(userMessage);
        if (canUseReadableFallback)
        {
            var readableFallback = BuildReadableSourceBackedFallbackIfUseful(sourceToolResults, userMessage, language);
            if (!string.IsNullOrWhiteSpace(readableFallback))
                return SuppressBroadenedSearchOfferIfAlreadyConfirmed(readableFallback, userMessage, language);
        }

        return BuildBroadEvidenceStillInsufficientAnswer(
            language,
            userMessage,
            intentQuery,
            EnumerateRagHitSummaries(sourceToolResults).Count(),
            HasMergedOrMultipleRagEvidence(sourceToolResults));
    }

    private static bool ShouldReturnInsufficientAfterConfirmedBroadPlanningFallback(
        ToolResults sourceToolResults,
        string userMessage,
        string intentQuery,
        string language,
        out int nearbyHitCount)
    {
        nearbyHitCount = 0;
        if (!IsBroadenedSourceSearchConfirmationEnvelope(userMessage)
            || !RequiresStructuredSourceBackedPlanningCoverage(intentQuery))
        {
            return false;
        }

        var coverage = EvaluateSourceBackedPlanningCoverage(sourceToolResults, intentQuery, language);
        nearbyHitCount = Math.Max(
            coverage.CandidateCount,
            EnumerateRagHitSummaries(sourceToolResults).Count());
        return !coverage.IsAdequate
            && !HasUsefulPartialSourceBackedPlanningCoverage(
                coverage,
                searchWasBroadened: true,
                searchWasExpanded: HasExpandedSourceBackedSearchEvidence(sourceToolResults));
    }

    private static string BuildSourceBackedSafeFallbackAfterRejectedWriter(
        ToolResults sourceToolResults,
        string userMessage,
        string writerUserMessage,
        string language)
    {
        var fallback = BuildSourceBackedSafeFallbackAnswer(
            sourceToolResults,
            userMessage,
            language,
            shouldAvoidRaw: true);
        fallback = RemoveTrailingModelEmittedSourceList(fallback ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(fallback)
               || ShouldFallbackFromNoRagDataAnswer(fallback)
               || LooksLikeWriterControlLeak(fallback)
               || LooksLikePoorPlanningFallbackAnswer(fallback, writerUserMessage)
            ? string.Empty
            : fallback;
    }
}
