using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedOptionCandidate> BuildStructuredSourceBackedSlotAwareGrid(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        IReadOnlyList<string> periodLabels,
        int requiredSlots,
        string? query,
        bool allowSourcedRotation,
        bool requireDistinctItems,
        out StructuredPlanningSlotFitSummary summary)
    {
        summary = StructuredPlanningSlotFitSummary.Empty;
        if (planItems.Count == 0 || periodLabels.Count == 0 || requiredSlots <= 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var slotGroups = BuildStructuredPlanningSlotTermGroups(periodLabels, query);
        if (slotGroups.Count == 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var distinctItems = planItems
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (distinctItems.Length == 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var primaryPools = slotGroups.Select(static _ => new List<SourceBackedOptionCandidate>()).ToArray();
        var alternativePools = slotGroups.Select(static _ => new List<SourceBackedOptionCandidate>()).ToArray();
        var neutralPool = new List<SourceBackedOptionCandidate>();
        var routedPool = 0;
        foreach (var candidate in distinctItems)
        {
            var primaryMatches = FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: false);
            var alternativeMatches = primaryMatches.Length == 0
                ? FindStructuredPlanningCandidateSlotMatches(candidate, slotGroups, useAlternativeTerms: true)
                : Array.Empty<int>();

            if (primaryMatches.Length > 0)
            {
                routedPool++;
                foreach (var index in primaryMatches)
                    primaryPools[index].Add(candidate);
            }
            else if (alternativeMatches.Length > 0)
            {
                routedPool++;
                foreach (var index in alternativeMatches)
                    alternativePools[index].Add(candidate);
            }
            else
            {
                neutralPool.Add(candidate);
            }
        }

        var hasRouteEvidence = routedPool > 0;
        summary = new StructuredPlanningSlotFitSummary(
            hasRouteEvidence,
            AssignedSlots: 0,
            RoutedPool: routedPool,
            NeutralPool: neutralPool.Count,
            PrimaryPools: primaryPools.Select(static pool => pool.Count).ToArray(),
            AlternativePools: alternativePools.Select(static pool => pool.Count).ToArray());
        if (!hasRouteEvidence)
            return Array.Empty<SourceBackedOptionCandidate>();

        var emptySlotGroupCount = primaryPools
            .Zip(alternativePools, static (primary, alternative) => primary.Count == 0 && alternative.Count == 0)
            .Count(static isEmpty => isEmpty);
        var requiredNeutralFallbackItems = emptySlotGroupCount <= 0
            ? 0
            : emptySlotGroupCount * Math.Max(1, requiredSlots / Math.Max(1, periodLabels.Count));
        var distinctNeutralFallbackItems = neutralPool
            .Select(BuildSourceBackedPlanningCandidateLeadKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var allowNeutralSlotFallback = !RequiresExplicitStructuredPlanningSlotEvidence(query)
            || requiredNeutralFallbackItems == 0
            || distinctNeutralFallbackItems >= requiredNeutralFallbackItems;
        var grid = allowSourcedRotation && !requireDistinctItems
            ? BuildRotatingStructuredPlanningSlotAwareGrid(requiredSlots, periodLabels.Count, primaryPools, alternativePools, neutralPool, allowNeutralSlotFallback)
            : BuildDistinctStructuredPlanningSlotAwareGrid(requiredSlots, periodLabels.Count, primaryPools, alternativePools, neutralPool, allowNeutralSlotFallback);
        summary = summary with { AssignedSlots = grid.Count };
        return grid.Count == requiredSlots ? grid : Array.Empty<SourceBackedOptionCandidate>();
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> BuildDistinctStructuredPlanningSlotAwareGrid(
        int requiredSlots,
        int periodCount,
        IReadOnlyList<SourceBackedOptionCandidate>[] primaryPools,
        IReadOnlyList<SourceBackedOptionCandidate>[] alternativePools,
        IReadOnlyList<SourceBackedOptionCandidate> neutralPool,
        bool allowNeutralSlotFallback)
    {
        var grid = new List<SourceBackedOptionCandidate>(requiredSlots);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var slotIndex = 0; slotIndex < requiredSlots; slotIndex++)
        {
            var periodIndex = slotIndex % periodCount;
            var candidate = PickNextUnusedStructuredPlanningSlotCandidate(primaryPools[periodIndex], used)
                ?? PickNextUnusedStructuredPlanningSlotCandidate(alternativePools[periodIndex], used)
                ?? (primaryPools[periodIndex].Count == 0 && alternativePools[periodIndex].Count == 0
                    && allowNeutralSlotFallback
                    ? PickNextUnusedStructuredPlanningSlotCandidate(neutralPool, used)
                    : null);
            if (candidate is null)
                break;

            used.Add(BuildSourceBackedPlanningCandidateLeadKey(candidate));
            grid.Add(candidate);
        }

        return grid;
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> BuildRotatingStructuredPlanningSlotAwareGrid(
        int requiredSlots,
        int periodCount,
        IReadOnlyList<SourceBackedOptionCandidate>[] primaryPools,
        IReadOnlyList<SourceBackedOptionCandidate>[] alternativePools,
        IReadOnlyList<SourceBackedOptionCandidate> neutralPool,
        bool allowNeutralSlotFallback)
    {
        var grid = new List<SourceBackedOptionCandidate>(requiredSlots);
        var cursors = new int[periodCount];
        for (var slotIndex = 0; slotIndex < requiredSlots; slotIndex++)
        {
            var periodIndex = slotIndex % periodCount;
            var pool = primaryPools[periodIndex].Count > 0
                ? primaryPools[periodIndex]
                : alternativePools[periodIndex].Count > 0
                    ? alternativePools[periodIndex]
                    : allowNeutralSlotFallback
                        ? neutralPool
                        : Array.Empty<SourceBackedOptionCandidate>();
            if (pool.Count == 0)
                break;

            grid.Add(pool[cursors[periodIndex] % pool.Count]);
            cursors[periodIndex]++;
        }

        return grid;
    }

    private static SourceBackedOptionCandidate? PickNextUnusedStructuredPlanningSlotCandidate(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        HashSet<string> used)
    {
        foreach (var candidate in candidates)
        {
            var key = BuildSourceBackedPlanningCandidateLeadKey(candidate);
            if (!used.Contains(key))
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<StructuredPlanningSlotTermGroup> BuildStructuredPlanningSlotTermGroups(
        IReadOnlyList<string> periodLabels,
        string? query)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var rawTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var groups = new List<StructuredPlanningSlotTermGroup>();
        foreach (var label in periodLabels)
        {
            var normalizedLabel = NormalizeLexicalLookup(label);
            if (string.IsNullOrWhiteSpace(normalizedLabel))
                continue;

            var primary = new HashSet<string>(StringComparer.Ordinal);
            AddStructuredPlanningSlotTermVariants(primary, normalizedLabel);
            var alternatives = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in rawTerms)
            {
                if (string.Equals(term, normalizedLabel, StringComparison.Ordinal))
                    continue;
                if (PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedQuery, term, normalizedLabel)
                    || PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedQuery, normalizedLabel, term))
                {
                    AddStructuredPlanningSlotTermVariants(alternatives, term);
                }
            }

            groups.Add(new StructuredPlanningSlotTermGroup(
                normalizedLabel,
                primary.ToArray(),
                alternatives.Except(primary, StringComparer.Ordinal).ToArray()));
        }

        return groups;
    }

    private static void AddStructuredPlanningSlotTermVariants(HashSet<string> terms, string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        terms.Add(normalized);
        foreach (var token in ExtractQuerySignalTerms(normalized))
        {
            if (token.Length < 4)
                continue;
            terms.Add(token);
            if (token.EndsWith('s') && token.Length > 4)
                terms.Add(token[..^1]);
            else
                terms.Add(token + "s");
        }
    }

    private static int[] FindStructuredPlanningCandidateSlotMatches(
        SourceBackedOptionCandidate candidate,
        IReadOnlyList<StructuredPlanningSlotTermGroup> slotGroups,
        bool useAlternativeTerms)
    {
        var routeText = NormalizeLexicalLookup(candidate.Hit.RetrievalQuery);
        if (string.IsNullOrWhiteSpace(routeText))
            return Array.Empty<int>();

        var matches = new List<int>();
        for (var i = 0; i < slotGroups.Count; i++)
        {
            var terms = useAlternativeTerms ? slotGroups[i].AlternativeTerms : slotGroups[i].PrimaryTerms;
            if (terms.Any(term => ContainsStructuredAxisPlannerTerm(routeText, term)))
                matches.Add(i);
        }

        return matches.ToArray();
    }

    private static string FormatStructuredPlanningAxisDisplayLabel(string? label)
    {
        var value = CollapseWhitespace(label ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return char.ToUpperInvariant(value[0]) + (value.Length == 1 ? string.Empty : value[1..]);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> BuildStructuredSourceBackedRotatingGrid(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        IReadOnlyList<string> periodLabels,
        int requiredSlots,
        string? query)
    {
        if (planItems.Count == 0 || periodLabels.Count == 0 || requiredSlots <= 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var distinctItems = planItems
            .GroupBy(BuildSourceBackedPlanningCandidateLeadKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (distinctItems.Length == 0)
            return Array.Empty<SourceBackedOptionCandidate>();

        var grid = new List<SourceBackedOptionCandidate>(requiredSlots);
        for (var slotIndex = 0; slotIndex < requiredSlots; slotIndex++)
            grid.Add(distinctItems[slotIndex % distinctItems.Length]);

        return grid;
    }

    private static bool HasEnoughSourceBackedCandidatesForStructuredPlan(
        IReadOnlyList<SourceBackedOptionCandidate> planItems,
        int requiredDistinctItems)
    {
        if (requiredDistinctItems <= 1)
            return planItems.Count > 0;

        if (planItems.Count < requiredDistinctItems)
            return false;

        var distinctTitles = planItems
            .Select(static candidate => NormalizeLexicalLookup(candidate.Title))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctTitles < requiredDistinctItems)
            return false;

        if (planItems.Any(static candidate => !HasStrictStructuredPlanningCandidateEvidence(candidate)))
            return false;

        return planItems.Any(static candidate => !string.IsNullOrWhiteSpace(BuildRagHitVisiblePageMergeKey(candidate.Hit)));
    }

    private static bool HasStrictStructuredPlanningCandidateEvidence(SourceBackedOptionCandidate candidate)
    {
        var title = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var hasAnchoredContextProof = SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, title);
        if (!LooksLikeConcreteStructuredPlanningCandidateTitle(candidate.Title)
            && !hasAnchoredContextProof)
            return false;

        if (hasAnchoredContextProof)
            return true;

        if (!HasPageLocalStructuredPlanningCandidateSupport(candidate))
            return false;

        if (HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, title))
            return true;

        var evidence = NormalizeLexicalLookup(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        if (evidence.Length < 16)
            return false;

        if (PrimaryPageEvidenceContainsExactPlanningCandidateTitle(candidate))
            return true;

        if (evidence.Contains(title, StringComparison.Ordinal))
            return true;

        var terms = ExtractPlanningAnswerSupportTerms(title)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0)
            return title.Length >= 6;

        var matched = terms.Count(term => evidence.Contains(term, StringComparison.Ordinal));
        return terms.Length switch
        {
            1 => terms[0].Length >= 6 && matched == 1,
            <= 3 => matched == terms.Length,
            _ => matched >= Math.Max(3, (int)Math.Ceiling(terms.Length * 0.85))
        };
    }

    private sealed record StructuredPlanningSlotTermGroup(
        string Label,
        IReadOnlyList<string> PrimaryTerms,
        IReadOnlyList<string> AlternativeTerms);

    private sealed record StructuredPlanningSlotFitSummary(
        bool HasRouteEvidence,
        int AssignedSlots,
        int RoutedPool,
        int NeutralPool,
        IReadOnlyList<int> PrimaryPools,
        IReadOnlyList<int> AlternativePools)
    {
        public static StructuredPlanningSlotFitSummary Empty { get; } = new(
            HasRouteEvidence: false,
            AssignedSlots: 0,
            RoutedPool: 0,
            NeutralPool: 0,
            PrimaryPools: Array.Empty<int>(),
            AlternativePools: Array.Empty<int>());
    }
}
