using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int ResolveSourceBackedPlanningCandidatePoolSize(string? query, int requestedCandidateCount)
    {
        const int maxPoolSize = 64;
        requestedCandidateCount = Math.Clamp(requestedCandidateCount, 1, maxPoolSize);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return requestedCandidateCount;

        const int multiplier = 2;
        return Math.Clamp(requestedCandidateCount * multiplier, requestedCandidateCount, maxPoolSize);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> RankDistinctSourceBackedPlanningLeadCandidates(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        string? query)
        => candidates
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestSourceBackedPlanningDuplicate(group, query))
            .OrderByDescending(candidate => ComputeSourceBackedPlanningCandidateRankScore(candidate, query))
            .ThenByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(static candidate => candidate.Hit.Score)
            .ToArray();

    private static SourceBackedOptionCandidate SelectBestSourceBackedPlanningDuplicate(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        string? query)
        => candidates
            .OrderByDescending(candidate => ComputeSourceBackedPlanningCandidateRankScore(candidate, query))
            .ThenByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(static candidate => candidate.Hit.Score)
            .First();

    private static SourceBackedOptionCandidate SelectBestSourceBackedOptionDuplicate(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        string? query)
    {
        return candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(static candidate => candidate.Hit.Score)
            .First();
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectPageDiverseSourceBackedPlanningCandidates(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        int maxItems,
        string? query)
    {
        maxItems = Math.Clamp(maxItems, 0, 64);
        if (maxItems == 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var ranked = RankDistinctSourceBackedPlanningLeadCandidates(candidates, query);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return ranked.Take(maxItems).ToArray();

        var selected = new List<SourceBackedOptionCandidate>(maxItems);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ranked)
        {
            var pageKey = BuildRagHitVisiblePageMergeKey(candidate.Hit);
            if (string.IsNullOrWhiteSpace(pageKey) || !selectedPages.Add(pageKey))
                continue;

            selected.Add(candidate);
            selectedKeys.Add(BuildSourceBackedPlanningCandidateKey(candidate));
            if (selected.Count >= maxItems)
                return selected;
        }

        foreach (var candidate in ranked)
        {
            if (!selectedKeys.Add(BuildSourceBackedPlanningCandidateKey(candidate)))
                continue;

            selected.Add(candidate);
            if (selected.Count >= maxItems)
                break;
        }

        return selected;
    }

    private static int ComputeSourceBackedPlanningCandidateRankScore(
        SourceBackedOptionCandidate candidate,
        string? query)
    {
        var score = candidate.Score;
        score += ComputeSourceBackedPlanningCandidateRetrievalRouteScore(candidate, query);
        score += ComputeSourceBackedPlanningCandidateReadabilityScore(candidate);
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, normalizedTitle))
            score += 28 + Math.Min(8, ExtractPlanningAnswerSupportTerms(normalizedTitle).Distinct(StringComparer.Ordinal).Count());
        return score;
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> FilterSourceBackedPlanningCandidatesToRequestedRetrievalRoutes(
        IReadOnlyList<SourceBackedOptionCandidate> candidates,
        string? query)
    {
        if (candidates.Count == 0)
            return candidates;

        var routeTerms = ExtractSourceBackedPlanningRetrievalRouteFitTerms(query);
        if (routeTerms.Length == 0)
            return candidates;

        if (!candidates.Any(candidate => SourceBackedPlanningCandidateRetrievalRouteMatchesAny(candidate, routeTerms)))
            return candidates;

        var filtered = candidates
            .Where(candidate => string.IsNullOrWhiteSpace(candidate.Hit.RetrievalQuery)
                || SourceBackedPlanningCandidateRetrievalRouteLooksGeneric(candidate.Hit.RetrievalQuery)
                || SourceBackedPlanningCandidateRetrievalRouteMatchesAny(candidate, routeTerms))
            .ToArray();
        var hasExplicitStructuredAxes =
            DetectRequestedDayAxisLabels(query, "en").Count > 0
            && DetectRequestedPlanningSlotAxisLabels(query, "en").Count > 0;
        if (ShouldGateStructuredSourceBackedPlanningCoverage(query) && hasExplicitStructuredAxes)
        {
            var targetCount = ResolveSourceBackedPlanningTargetItemCount(query);
            var minimumUsefulCount = Math.Min(candidates.Count, Math.Max(4, targetCount));
            if (filtered.Length < minimumUsefulCount)
                return candidates;
        }

        return filtered.Length == 0 ? candidates : filtered;
    }

    private static int ComputeSourceBackedPlanningCandidateRetrievalRouteScore(
        SourceBackedOptionCandidate candidate,
        string? query)
    {
        var routeTerms = ExtractSourceBackedPlanningRetrievalRouteFitTerms(query);
        if (routeTerms.Length == 0)
            return 0;

        return SourceBackedPlanningCandidateRetrievalRouteMatchesAny(candidate, routeTerms) ? 80 : 0;
    }

    private static string[] ExtractSourceBackedPlanningRetrievalRouteFitTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return ExtractPlanningRetrievalTerms(normalized)
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
            .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }

    private static bool SourceBackedPlanningCandidateRetrievalRouteMatchesAny(
        SourceBackedOptionCandidate candidate,
        IReadOnlyCollection<string> routeTerms)
    {
        var route = NormalizeLexicalLookup(candidate.Hit.RetrievalQuery);
        return !string.IsNullOrWhiteSpace(route)
            && routeTerms.Any(term => ContainsStructuredAxisPlannerTerm(route, term));
    }

    private static bool SourceBackedPlanningCandidateRetrievalRouteLooksGeneric(string? retrievalQuery)
    {
        var route = NormalizeLexicalLookup(retrievalQuery);
        if (string.IsNullOrWhiteSpace(route))
            return true;

        var tokens = ExtractQuerySignalTerms(route)
            .Where(static token => token.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tokens.Length > 0 && tokens.All(IsGenericSourceBackedPlanningRetrievalRouteToken);
    }

    private static bool IsGenericSourceBackedPlanningRetrievalRouteToken(string token)
        => token is
            "selection" or "documente" or "documentee" or "documentes" or "documented"
            or "source" or "sources"
            or "option" or "options"
            or "candidat" or "candidats" or "candidate" or "candidates"
            or "exemple" or "exemples" or "example" or "examples"
            or "proposition" or "propositions" or "proposal" or "proposals"
            or "preparation" or "preparations"
            or "element" or "elements" or "item" or "items";

    private static int ComputeSourceBackedPlanningCandidateReadabilityScore(SourceBackedOptionCandidate candidate)
    {
        var score = 0;
        if (LooksLikeTrailingIsolatedOcrSuffixTitle(candidate.Title))
            score -= 70;

        var displayTitle = FormatSourceBackedCandidateDisplayTitle(candidate);
        if (!string.IsNullOrWhiteSpace(displayTitle)
            && !string.Equals(CollapseWhitespace(candidate.Title), displayTitle, StringComparison.Ordinal))
        {
            score += 4;
        }

        return score;
    }
}
