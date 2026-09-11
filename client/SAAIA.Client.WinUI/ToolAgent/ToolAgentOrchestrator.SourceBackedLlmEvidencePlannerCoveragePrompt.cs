using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedLlmWeakRetrievalAxesForPrompt(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language,
        IReadOnlyList<string> alreadyTriedQueries,
        int maxLines)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            && !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
        {
            return "none";
        }

        language = NormalizeLanguageCode(language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        if (slotAxis.Count == 0)
            return "none";

        var targetSlots = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(
            effectiveUserMessage,
            targetSlots,
            hasStructuredAxes: DetectRequestedDayAxisLabels(effectiveUserMessage, language).Count > 0);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var poolSize = strictStructuredPlanning
            ? Math.Min(
                ResolveSourceBackedPlanningCandidatePoolSize(effectiveUserMessage, Math.Max(targetSlots, minimumCandidates)),
                Math.Max(32, targetSlots))
            : Math.Max(20, targetSlots);
        var candidates = SelectSourceBackedPlanningCandidates(
                toolResults,
                effectiveUserMessage,
                poolSize,
                language)
            .ToArray();
        var hits = EnumerateRagHitSummaries(toolResults).ToArray();
        var queryRuns = EnumerateSourceBackedRetrievalQueryRuns(toolResults).ToArray();
        var triedQueries = alreadyTriedQueries
            .Concat(queryRuns.Select(static run => run.Query))
            .Concat(hits.Select(static hit => hit.RetrievalQuery ?? string.Empty))
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedUser = NormalizeLexicalLookup(effectiveUserMessage);
        var lines = new List<string>();

        for (var axisIndex = 0; axisIndex < slotAxis.Count; axisIndex++)
        {
            var axis = slotAxis[axisIndex];
            var axisTerms = BuildStructuredAxisPromptTerms(axis, effectiveUserMessage)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (axisTerms.Length == 0)
                continue;

            var axisTriedQueries = triedQueries
                .Where(query => QueryMentionsAnyStructuredAxisTerm(query, axisTerms))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            var axisRuns = queryRuns
                .Where(run => QueryMentionsAnyStructuredAxisTerm(run.Query, axisTerms))
                .ToArray();
            var axisHitCount = axisRuns.Length > 0
                ? axisRuns.Sum(static run => Math.Max(0, run.HitCount))
                : hits.Count(hit => QueryMentionsAnyStructuredAxisTerm(hit.RetrievalQuery, axisTerms));
            var requiredSlots = CountRequiredStructuredAxisSlots(slotAxis, axisIndex, targetSlots);
            var routeCandidateCount = candidates.Count(candidate =>
                QueryMentionsAnyStructuredAxisTerm(candidate.Hit.RetrievalQuery, axisTerms));
            var titleCandidateCount = candidates.Count(candidate =>
                QueryMentionsAnyStructuredAxisTerm(candidate.Title, axisTerms));
            var candidatePool = Math.Max(routeCandidateCount, titleCandidateCount);
            var routeFitPool = routeCandidateCount;
            var titlePool = titleCandidateCount;

            var hasLowRuns = axisRuns.Any(static run => run.HitCount <= 1 || run.Busy || !string.IsNullOrWhiteSpace(run.Error));
            var requiredProbeFloor = Math.Min(Math.Max(1, requiredSlots), 3);
            var underCovered = candidatePool < requiredProbeFloor
                               || routeFitPool == 0
                               || (axisTriedQueries.Length > 0 && axisHitCount <= 1)
                               || hasLowRuns;
            if (!underCovered)
                continue;

            var userTerms = axisTerms
                .Where(term => ContainsStructuredAxisPlannerTerm(normalizedUser, term))
                .Take(8)
                .ToArray();
            var unusedPivots = axisTerms
                .Where(term => !axisTriedQueries.Any(query => QueryMentionsAnyStructuredAxisTerm(query, new[] { term })))
                .Take(8)
                .ToArray();
            var suggestedPivots = unusedPivots.Length > 0
                ? unusedPivots
                : axisTerms
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
            var failedOrLow = axisRuns
                .Where(static run => run.HitCount <= 1 || run.Busy || !string.IsNullOrWhiteSpace(run.Error))
                .OrderBy(static run => run.HitCount)
                .ThenBy(static run => run.Query.Length)
                .Select(static run => $"{run.Query} => {run.HitCount} hit(s)")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            lines.Add(
                "axis="
                + FormatPlanningTraceValue(axis)
                + $"|required_slots={requiredSlots}"
                + $"|candidate_pool={candidatePool}"
                + $"|route_fit_pool={routeFitPool}"
                + $"|title_pool={titlePool}"
                + $"|retrieval_hits={axisHitCount}"
                + $"|user_terms={FormatPlanningTraceValue(string.Join(", ", userTerms))}"
                + $"|suggested_pivots={FormatPlanningTraceValue(string.Join(", ", suggestedPivots))}"
                + $"|failed_or_low_hit_queries={FormatPlanningTraceValue(string.Join("; ", failedOrLow))}"
                + $"|tried_queries={FormatPlanningTraceValue(string.Join("; ", axisTriedQueries))}"
                + "|next_action=diversify_this_axis_before_repeating_failed_terms");
        }

        return lines.Count == 0
            ? "none"
            : FormatPromptList(lines, maxLines, maxItemLength: 360);
    }

    private static IEnumerable<string> BuildStructuredAxisPromptTerms(string? axisLabel, string? userMessage)
    {
        foreach (var variant in ExpandPlanningSlotRetrievalTermVariants(axisLabel))
        {
            var normalized = NormalizeLexicalLookup(variant);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }

        var normalizedAxis = NormalizeLexicalLookup(axisLabel);
        if (!string.IsNullOrWhiteSpace(normalizedAxis))
            yield return normalizedAxis;

        var normalizedUser = NormalizeLexicalLookup(userMessage);
        foreach (var term in ExtractPlanningSlotRetrievalTerms(userMessage))
        {
            var normalized = NormalizeLexicalLookup(term);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            var variants = ExpandPlanningSlotRetrievalTermVariants(normalized)
                .Select(NormalizeLexicalLookup)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (variants.Any(variant => ContainsStructuredAxisPlannerTerm(normalizedAxis, variant))
                || variants.Any(variant => PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedUser, variant, normalizedAxis))
                || variants.Any(variant => PlanningSlotAxisLabelsAreExplicitAlternatives(normalizedUser, normalizedAxis, variant))
                || variants.Any(variant => ContainsStructuredAxisPlannerTerm(normalizedUser, variant)
                                           && ContainsStructuredAxisPlannerTerm(normalizedAxis, normalized)))
            {
                foreach (var variant in variants)
                    yield return variant;
            }
        }
    }

    private static bool QueryMentionsAnyStructuredAxisTerm(string? query, IReadOnlyCollection<string> normalizedTerms)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        return !string.IsNullOrWhiteSpace(normalizedQuery)
               && normalizedTerms.Any(term => ContainsStructuredAxisPlannerTerm(normalizedQuery, term));
    }

    private static int CountRequiredStructuredAxisSlots(IReadOnlyList<string> periodAxis, int axisIndex, int targetSlots)
    {
        if (periodAxis.Count == 0 || targetSlots <= 0)
            return 0;

        var normalizedTarget = NormalizeLexicalLookup(periodAxis[axisIndex]);
        var count = 0;
        for (var i = 0; i < targetSlots; i++)
        {
            var normalized = NormalizeLexicalLookup(periodAxis[i % periodAxis.Count]);
            if (string.Equals(normalized, normalizedTarget, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static IEnumerable<(string Query, int HitCount, bool Busy, string? Error)> EnumerateSourceBackedRetrievalQueryRuns(
        ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            var meta = TryGetObject(item.Result, "meta") ?? TryGetObject(item.Result, "Meta");
            if (meta is null)
                continue;

            var runs = TryGetArray(meta.Value, "queryRuns")
                       ?? TryGetArray(meta.Value, "QueryRuns")
                       ?? TryGetArray(meta.Value, "query_runs");
            if (!runs.HasValue || runs.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var run in runs.Value.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object)
                    continue;

                var query = TryGetString(run, "query") ?? TryGetString(run, "Query");
                if (string.IsNullOrWhiteSpace(query))
                    continue;

                yield return (
                    CollapseWhitespace(query),
                    Math.Max(0, TryGetInt(run, "hitCount") ?? TryGetInt(run, "HitCount") ?? TryGetInt(run, "hits") ?? 0),
                    TryGetBool(run, "busy") ?? TryGetBool(run, "Busy") ?? false,
                    TryGetString(run, "error") ?? TryGetString(run, "Error"));
            }
        }
    }

    private static string BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language,
        int maxLines)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            && !LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
        {
            return "none";
        }

        language = NormalizeLanguageCode(language);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
        var hasStructuredAxes = dayAxis.Count > 0 && slotAxis.Count > 0;
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(
            effectiveUserMessage,
            targetSlots,
            hasStructuredAxes);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var candidatePoolSize = strictStructuredPlanning
            ? Math.Min(
                ResolveSourceBackedPlanningCandidatePoolSize(effectiveUserMessage, Math.Max(targetSlots, minimumCandidates)),
                Math.Max(32, targetSlots))
            : Math.Max(20, targetSlots);
        var acceptedPool = SelectSourceBackedPlanningCandidates(
                toolResults,
                effectiveUserMessage,
                candidatePoolSize,
                language)
            .ToList();
        var lines = new List<string>
        {
            $"stage=scope|target_slots={targetSlots}|minimum_candidates={minimumCandidates}|structured_axes={FormatPlanningTraceBool(hasStructuredAxes)}|strict={FormatPlanningTraceBool(strictStructuredPlanning)}",
            "stage=candidate_pool"
            + $"|raw_candidates={acceptedPool.Count}"
            + $"|top_titles={FormatPlanningTraceValue(string.Join("; ", acceptedPool.Take(12).Select(static candidate => candidate.Title)))}"
        };

        var accepted = strictStructuredPlanning
            ? SelectPageDiverseSourceBackedPlanningCandidates(
                    acceptedPool,
                    Math.Max(targetSlots, minimumCandidates),
                    effectiveUserMessage)
                .ToList()
            : acceptedPool;

        var reservedTailLines = hasStructuredAxes ? 2 : 1;
        foreach (var candidate in accepted.Take(Math.Max(0, maxLines - lines.Count - reservedTailLines)))
        {
            lines.Add(
                "stage=candidate|decision=accepted"
                + $"|retrieval_query={FormatPlanningTraceValue(candidate.Hit.RetrievalQuery)}"
                + $"|title={FormatPlanningTraceValue(candidate.Title)}"
                + $"|doc={FormatPlanningTraceValue(candidate.Hit.DocPath)}"
                + $"|page={candidate.Hit.PageStart}");
        }

        if (hasStructuredAxes)
        {
            var slotLabels = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language);
            _ = BuildStructuredSourceBackedSlotAwareGrid(
                accepted,
                slotLabels,
                targetSlots,
                effectiveUserMessage,
                allowSourcedRotation: false,
                requireDistinctItems: true,
                out var routeAwareFit);
            lines.Add(
                "stage=slot_fit"
                + $"|assigned_slots={routeAwareFit.AssignedSlots}"
                + $"|required_slots={targetSlots}"
                + $"|route_evidence={FormatPlanningTraceBool(routeAwareFit.HasRouteEvidence)}"
                + $"|routed_pool={routeAwareFit.RoutedPool}"
                + $"|neutral_pool={routeAwareFit.NeutralPool}"
                + $"|primary_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(slotLabels, routeAwareFit.PrimaryPools))}"
                + $"|alternative_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(slotLabels, routeAwareFit.AlternativePools))}");
        }

        lines.Add(
            "stage=summary"
            + $"|accepted_candidates={accepted.Count}"
            + $"|raw_candidates={acceptedPool.Count}");
        return FormatPromptList(lines, maxLines, maxItemLength: 260);
    }
}
