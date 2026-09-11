using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static List<ToolMemory.SourceRef> DeriveSourcesFromMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        var closeLeads = SelectMissingExactItemCloseLeads(requestedTitle, hits);
        if (closeLeads.Count == 0)
            closeLeads = hits
                .Where(hit => !LooksLikeNavigationOnlyHit(hit))
                .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit))
                .OrderByDescending(hit => ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit)))
                .Take(3)
                .ToList();

        return closeLeads
            .Select(BuildSourceRefFromRagHit)
            .ToList();
    }

    private static List<RagHitSummary> SelectMissingExactItemCloseLeads(string requestedTitle, IReadOnlyList<RagHitSummary> hits)
    {
        return hits
            .Select(hit => new
            {
                Hit = hit,
                Score = ComputeTypoTolerantRagHitLexicalRelevance(requestedTitle, GetRagHitLookupText(hit))
            })
            .Where(item => !LooksLikeNavigationOnlyHit(item.Hit))
            .Where(item => !LooksLikeLowSignalContentCandidateHit(item.Hit))
            .Where(item => !LooksLikePageReferenceOnlyHit(item.Hit))
            .Where(item => !LooksLikeExactItemReferenceOnlyHit(requestedTitle, item.Hit))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Hit)
            .Take(2)
            .ToList();
    }

    private static int ComputeTypoTolerantRagHitLexicalRelevance(string query, string text)
    {
        var best = ComputeRagHitLexicalRelevance(query, text);
        foreach (var variant in BuildTypoTolerantQueryVariants(query))
            best = Math.Max(best, ComputeRagHitLexicalRelevance(variant, text));
        return best;
    }

    private static IReadOnlyList<RagHitSummary> SelectSourceBackedExtractiveHits(ToolResults toolResults, string query, int maxHits)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var isShortTechnicalEvidenceTopic = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery);
        var requestedTitle = isShortTechnicalEvidenceTopic
            ? null
            : TryExtractRequestedItemTitle(query);
        var requestedDocumentFileTitle = ResolveRequestedDocumentFileTitle(query, requestedTitle);
        var explicitDocumentFileReferences = ExtractExplicitDocumentFileReferenceQueries(query);
        var hasMultipleExplicitComparativeDocumentReferences = LooksLikeComparativeDocumentaryRequest(query)
            && explicitDocumentFileReferences.Count > 1;
        var shouldScopeToSingleRequestedDocumentFile = !hasMultipleExplicitComparativeDocumentReferences
            && !string.IsNullOrWhiteSpace(requestedDocumentFileTitle);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit)
                || MatchesRequestedDocumentFileIdentity(requestedDocumentFileTitle, hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query)
                || MatchesRequestedDocumentFileIdentity(requestedDocumentFileTitle, hit))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit)
                || MatchesRequestedDocumentFileIdentity(requestedDocumentFileTitle, hit)
                || MatchesExplicitComparativeDocumentReference(query, hit))
            .Select(hit => new
            {
                Hit = hit,
                DocumentTypeScore = ComputeRequestedDocumentTypeAnchorScore(evidenceQuery, hit),
                DirectTechnicalEvidence = isShortTechnicalEvidenceTopic
                    ? ComputeDirectTechnicalEvidenceScore(evidenceQuery, hit)
                    : 0,
                PrimaryRelevance = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(hit)),
                Relevance = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit))
            })
            .OrderByDescending(item => item.DocumentTypeScore)
            .ThenByDescending(item => item.DirectTechnicalEvidence)
            .ThenByDescending(item => item.PrimaryRelevance)
            .ThenByDescending(item => item.Relevance)
            .ThenByDescending(item => item.Hit.Score)
            .ToList();
        if (shouldScopeToSingleRequestedDocumentFile)
        {
            var documentScopedHits = allHits
                .Where(item => CandidateMatchesDocumentIdentity(requestedDocumentFileTitle!, item.Hit.DocName, item.Hit.DocPath))
                .ToList();
            if (documentScopedHits.Count > 0)
                allHits = documentScopedHits;
        }

        var requestedDocumentTypeHits = allHits
            .Where(static item => item.DocumentTypeScore > 0)
            .ToList();
        if (requestedDocumentTypeHits.Count > 0)
            allHits = requestedDocumentTypeHits;

        if (isShortTechnicalEvidenceTopic)
        {
            var phraseEvidenceHits = allHits
                .Where(item => HasShortTechnicalPhraseEvidence(evidenceQuery, item.Hit))
                .ToList();
            if (phraseEvidenceHits.Count > 0)
                allHits = phraseEvidenceHits;
        }

        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var timeCompatibleHits = allHits
                .Where(item =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(item.Hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (timeCompatibleHits.Count > 0)
                allHits = timeCompatibleHits;
        }

        var softChoiceOptionKindTerms = ExtractSoftChoiceRequestedOptionKindTerms(query);
        if (softChoiceOptionKindTerms.Count > 0)
        {
            var compatibleHits = allHits
                .Where(item => !SoftChoiceOptionKindContradictsHit(softChoiceOptionKindTerms, item.Hit))
                .ToList();
            if (compatibleHits.Count > 0)
                allHits = compatibleHits;

            var kindMatchedHits = allHits
                .Where(item => SoftChoiceOptionKindMatchesHit(softChoiceOptionKindTerms, item.Hit))
                .ToList();
            if (kindMatchedHits.Count > 0)
                allHits = kindMatchedHits;
        }

        if (allHits.Count == 0)
            return Array.Empty<RagHitSummary>();

        if (LooksLikeRankingDocumentaryRequest(query))
        {
            var rankedHits = RankSourceBackedRankingHits(allHits.Select(item => item.Hit), query)
                .Take(maxHits)
                .ToList();
            if (rankedHits.Count > 0)
                return rankedHits;
        }

        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            var comparativeHits = SelectComparativeDocumentaryHits(allHits.Select(item => item.Hit), query, maxHits);
            if (comparativeHits.Count > 0)
                return comparativeHits;
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var exactTitleHits = allHits
                .Select(item => item.Hit)
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!)
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                    || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                .Where(hit => !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit))
                .ToList();
            if (exactTitleHits.Count > 0)
            {
                var isStructuredItemCardRequest = LooksLikeStructuredItemCardRequest(query);
                var anchoredTitleHits = exactTitleHits
                    .Where(hit => isStructuredItemCardRequest
                        ? ExactItemEvidenceStartsWithRequestedTitle(requestedTitle!, hit) || HasStrongExactItemTitleAnchor(requestedTitle!, hit) || IsUsableExactItemNavigationOverride(requestedTitle!, hit)
                        : RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit) || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                    .ToList();
                if (isStructuredItemCardRequest)
                {
                    var leadTitleHits = anchoredTitleHits
                        .Where(hit => ExactItemEvidenceStartsWithRequestedTitle(requestedTitle!, hit))
                        .ToList();
                    if (leadTitleHits.Count > 0)
                    {
                        anchoredTitleHits = leadTitleHits;
                        exactTitleHits = anchoredTitleHits;
                    }
                    else if (anchoredTitleHits.Count == 0)
                    {
                        var recoverableStructuredHits = exactTitleHits
                            .Where(hit => HasRecoverableStructuredExactItemEvidence(requestedTitle!, hit))
                            .ToList();
                        if (recoverableStructuredHits.Count == 0)
                            return Array.Empty<RagHitSummary>();

                        exactTitleHits = recoverableStructuredHits;
                    }
                    else
                    {
                        exactTitleHits = anchoredTitleHits;
                    }
                }
                else if (anchoredTitleHits.Count == 0
                         && !exactTitleHits.Any(hit => HasRecoverableStructuredExactItemEvidence(requestedTitle!, hit)))
                {
                    return Array.Empty<RagHitSummary>();
                }
                else if (anchoredTitleHits.Count > 0)
                {
                    exactTitleHits = anchoredTitleHits;
                }

                if (LooksLikeStructuredItemCardRequest(query))
                {
                    exactTitleHits = AddComplementaryStructuredHitsForExactItem(
                            exactTitleHits,
                            allHits.Select(static item => item.Hit))
                        .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle!, hit))
                        .ThenByDescending(ComputeExactItemCardCompletenessCueScore)
                        .ThenByDescending(hit => ComputeRagHitLexicalRelevance(requestedTitle!, GetRagHitLookupText(hit)))
                        .ThenByDescending(hit => hit.Score)
                        .Take(maxHits)
                        .ToList();
                }
                else
                {
                    exactTitleHits = exactTitleHits
                        .Take(maxHits)
                        .ToList();
                }

                return exactTitleHits;
            }

            if (LooksLikeStructuredItemCardRequest(query))
                return Array.Empty<RagHitSummary>();
        }
        else
        {
            var primaryRelevantHits = allHits
                .Where(item => item.PrimaryRelevance > 0)
                .Select(item => item.Hit)
                .ToList();
            if (primaryRelevantHits.Count > 0)
                return FilterHitsToDominantTopLevel(primaryRelevantHits, query).Take(maxHits).ToList();

            var relevantHits = allHits
                .Where(item => item.Relevance > 0)
                .Select(item => item.Hit)
                .ToList();
            if (relevantHits.Count > 0)
                return FilterHitsToDominantTopLevel(relevantHits, query).Take(maxHits).ToList();

            var fallbackHits = allHits.Select(item => item.Hit).ToList();
            return FilterHitsToDominantTopLevel(fallbackHits, query).Take(maxHits).ToList();
        }

        return allHits.Select(item => item.Hit).Take(maxHits).ToList();
    }
}
