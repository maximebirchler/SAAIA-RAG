using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedPlanningCandidateSelectionCacheMaxEntries = 48;
    private static readonly object SourceBackedPlanningCandidateSelectionCacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<SourceBackedOptionCandidate>> SourceBackedPlanningCandidateSelectionCache =
        new(StringComparer.Ordinal);

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedPlanningCandidates(
        ToolResults toolResults,
        string? query,
        int maxItems,
        string language = "",
        bool requireStrictStructuredEvidence = true)
    {
        var cacheKey = BuildSourceBackedPlanningCandidateSelectionCacheKey(
            toolResults,
            query,
            maxItems,
            language,
            requireStrictStructuredEvidence);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            lock (SourceBackedPlanningCandidateSelectionCacheGate)
            {
                if (SourceBackedPlanningCandidateSelectionCache.TryGetValue(cacheKey, out var cached))
                {
                    ClientLog.Info(
                        "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=cache.hit|stage=cache.hit"
                        + $"|maxItems={maxItems}"
                        + $"|strict={requireStrictStructuredEvidence}"
                        + $"|candidates={cached.Count}");
                    return cached;
                }
            }
        }

        var selected = SelectSourceBackedPlanningCandidatesUncached(
                toolResults,
                query,
                maxItems,
                language,
                requireStrictStructuredEvidence)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            lock (SourceBackedPlanningCandidateSelectionCacheGate)
            {
                if (SourceBackedPlanningCandidateSelectionCache.Count >= SourceBackedPlanningCandidateSelectionCacheMaxEntries)
                    SourceBackedPlanningCandidateSelectionCache.Clear();
                SourceBackedPlanningCandidateSelectionCache[cacheKey] = selected;
            }
        }

        return selected;
    }

    private static string BuildSourceBackedPlanningCandidateSelectionCacheKey(
        ToolResults toolResults,
        string? query,
        int maxItems,
        string language,
        bool requireStrictStructuredEvidence)
    {
        if (toolResults.Items.Count == 0)
            return string.Empty;

        var hash = new HashCode();
        hash.Add(NormalizeLanguageCode(language), StringComparer.Ordinal);
        hash.Add(NormalizeLexicalLookup(query), StringComparer.Ordinal);
        hash.Add(maxItems);
        hash.Add(requireStrictStructuredEvidence);
        hash.Add(toolResults.Items.Count);
        foreach (var item in toolResults.Items)
        {
            hash.Add(item.ToolName ?? string.Empty, StringComparer.Ordinal);
            hash.Add(item.Error ?? string.Empty, StringComparer.Ordinal);
            hash.Add(item.DurationMs);
            hash.Add(item.Result.ValueKind);
            var raw = item.Result.ValueKind == JsonValueKind.Undefined
                ? string.Empty
                : item.Result.GetRawText();
            hash.Add(raw.Length);
            hash.Add(raw, StringComparer.Ordinal);
        }

        return hash.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedPlanningCandidatesUncached(
        ToolResults toolResults,
        string? query,
        int maxItems,
        string language = "",
        bool requireStrictStructuredEvidence = true)
    {
        var requireDirectPageEvidence = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var traceSelection = requireDirectPageEvidence || requireStrictStructuredEvidence || maxItems >= 20;
        var selectionStopwatch = traceSelection ? Stopwatch.StartNew() : null;
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=start|stage=start"
                + $"|maxItems={maxItems}|strict={requireStrictStructuredEvidence}|directPageEvidence={requireDirectPageEvidence}");
        }

        var dominantTopLevelScope = requireDirectPageEvidence
            ? TryInferDominantTopLevelCategoryScope(toolResults, query)
            : null;
        var optionCandidates = SelectSourceBackedOptionCandidates(
                toolResults,
                query,
                keepOverRequestedDuration: true,
                language: language,
                allowPartialStructuredPlanningCandidates: !requireStrictStructuredEvidence)
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=option_candidates.end|stage=option_candidates.end"
                + $"|candidates={optionCandidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", optionCandidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=primary_filter.start|stage=primary_filter.start"
                + $"|optionCandidates={optionCandidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        var primaryFilterInput = optionCandidates
            .Select(candidate => new
            {
                Candidate = candidate,
                RejectionReason = ExplainSourceBackedPlanningCandidateRejection(
                    candidate,
                    query,
                    requireDirectPageEvidence,
                    requireStrictStructuredEvidence,
                    dominantTopLevelScope)
            })
            .ToList();
        if (traceSelection)
        {
            var removedSamples = primaryFilterInput
                .Where(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))
                .Take(6)
                .Select(static item => $"{item.RejectionReason}:{item.Candidate.Title}");
            var reasonCounts = primaryFilterInput
                .Where(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))
                .GroupBy(static item => item.RejectionReason, StringComparer.OrdinalIgnoreCase)
                .Select(static group => $"{group.Key}={group.Count()}");
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=primary_filter.rejections|stage=primary_filter.rejections"
                + $"|removed={primaryFilterInput.Count(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))}"
                + $"|kept={primaryFilterInput.Count(static item => string.IsNullOrWhiteSpace(item.RejectionReason))}"
                + $"|reasons={FormatPlanningTraceValue(string.Join(",", reasonCounts))}"
                + $"|samples={FormatPlanningTraceValue(string.Join("; ", removedSamples))}"
                + $"|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        var filteredCandidates = primaryFilterInput
            .Where(static item => string.IsNullOrWhiteSpace(item.RejectionReason))
            .Select(static item => item.Candidate)
            .GroupBy(BuildSourceBackedPlanningCandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestSourceBackedPlanningDuplicate(group, query))
            .ToArray();
        var rankedPrimaryCandidates = FilterSourceBackedPlanningCandidatesToRequestedRetrievalRoutes(filteredCandidates, query)
            .OrderByDescending(candidate => ComputeSourceBackedPlanningCandidateRankScore(candidate, query))
            .ThenByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(static candidate => candidate.Hit.Score)
            .ToList();
        var candidates = RemoveSourceBackedPlanningPartialTitleDuplicates(rankedPrimaryCandidates)
            .Take(maxItems)
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=primary_filter.end|stage=primary_filter.end"
                + $"|candidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", candidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        candidates = candidates
            .Take(maxItems)
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=primary_dedup.end|stage=primary_dedup.end"
                + $"|candidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", candidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        if (candidates.Count >= maxItems)
        {
            if (traceSelection)
            {
                ClientLog.Info(
                    "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=end|stage=end"
                    + $"|reason=primary_full|candidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", candidates.Take(10).Select(static candidate => candidate.Title))}");
            }

            return candidates;
        }

        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit)
                || (requireDirectPageEvidence && RagHitHasSelfContainedStructuredPlanningCardProof(hit)))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => LooksLikeResolvedRouteTargetHit(hit)
                || !LooksLikeLowSignalContentCandidateHit(hit)
                || (requireDirectPageEvidence && RagHitHasSelfContainedStructuredPlanningCardProof(hit)))
            .ToList();
        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_hits.end|stage=fallback_hits.end"
                + $"|hits={sourceHits.Count}|currentCandidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_candidates.raw.start|stage=fallback_candidates.raw.start"
                + $"|hits={sourceHits.Count}|currentCandidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        var rawFallbackCandidates = new List<SourceBackedOptionCandidate>();
        var lastFallbackProgressMs = selectionStopwatch?.ElapsedMilliseconds ?? 0;
        for (var sourceHitIndex = 0; sourceHitIndex < sourceHits.Count; sourceHitIndex++)
        {
            var hit = sourceHits[sourceHitIndex];
            var title = ExtractSourceBackedOptionTitle(hit, query);
            var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
            rawFallbackCandidates.Add(new SourceBackedOptionCandidate(
                hit,
                title,
                ComputeSourceBackedOptionHitScore(hit, title, query, requestedMaxMinutes: null, visibleMinutes),
                visibleMinutes));

            if (traceSelection)
            {
                var processedHits = sourceHitIndex + 1;
                var elapsedMs = selectionStopwatch!.ElapsedMilliseconds;
                if (processedHits == sourceHits.Count
                    || processedHits % 10 == 0
                    || elapsedMs - lastFallbackProgressMs >= 15000)
                {
                    ClientLog.Info(
                        "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_candidates.raw.progress|stage=fallback_candidates.raw.progress"
                        + $"|processedHits={processedHits}"
                        + $"|totalHits={sourceHits.Count}"
                        + $"|rawFallbackCandidates={rawFallbackCandidates.Count}"
                        + $"|currentCandidates={candidates.Count}"
                        + $"|ms={elapsedMs}"
                        + $"|lastDoc={FormatPlanningTraceValue(hit.DocName ?? hit.DocPath)}"
                        + $"|lastPage={hit.PageStart}");
                    lastFallbackProgressMs = elapsedMs;
                }
            }
        }

        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_candidates.raw.end|stage=fallback_candidates.raw.end"
                + $"|rawFallbackCandidates={rawFallbackCandidates.Count}|currentCandidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", rawFallbackCandidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackAfterOrientation = rawFallbackCandidates
            .Where(candidate => !ShouldRejectSourceBackedPlanningOrientationSurfaceCandidate(candidate))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.orientation|stage=fallback_filter.orientation"
                + $"|remaining={fallbackAfterOrientation.Count}|removed={rawFallbackCandidates.Count - fallbackAfterOrientation.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        var fallbackWithTitle = fallbackAfterOrientation
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.title|stage=fallback_filter.title"
                + $"|remaining={fallbackWithTitle.Count}|removed={fallbackAfterOrientation.Count - fallbackWithTitle.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackWithTitle.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackUsable = fallbackWithTitle
            .Where(candidate => IsUsableSourceBackedPlanningCandidate(candidate)
                && !LooksLikeRequestedPlanningSlotAxisLabelCandidate(candidate, query))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.usable|stage=fallback_filter.usable"
                + $"|remaining={fallbackUsable.Count}|removed={fallbackWithTitle.Count - fallbackUsable.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackUsable.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackNotNoisy = fallbackUsable
            .Where(candidate => !LooksLikeNoisyStructuredPlanningCandidateTitle(candidate.Title))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.noisy_title|stage=fallback_filter.noisy_title"
                + $"|remaining={fallbackNotNoisy.Count}|removed={fallbackUsable.Count - fallbackNotNoisy.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackNotNoisy.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackDominantScope = fallbackNotNoisy
            .Where(candidate => SourceBackedPlanningCandidateMatchesDominantTopLevel(candidate, dominantTopLevelScope))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.dominant_scope|stage=fallback_filter.dominant_scope"
                + $"|remaining={fallbackDominantScope.Count}|removed={fallbackNotNoisy.Count - fallbackDominantScope.Count}|scope={FormatPlanningTraceValue(dominantTopLevelScope)}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackDominantScope.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackConcreteTitle = fallbackDominantScope
            .Where(candidate => !requireDirectPageEvidence
                || LooksLikeConcreteStructuredPlanningCandidateTitle(candidate.Title)
                || SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(
                    candidate,
                    NormalizeLexicalLookup(candidate.Title)))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.concrete_title|stage=fallback_filter.concrete_title"
                + $"|remaining={fallbackConcreteTitle.Count}|removed={fallbackDominantScope.Count - fallbackConcreteTitle.Count}|required={requireDirectPageEvidence}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackConcreteTitle.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackStructuredEvidence = fallbackConcreteTitle
            .Where(candidate => !requireDirectPageEvidence
                || (requireStrictStructuredEvidence
                    ? HasStrictStructuredPlanningCandidateEvidence(candidate)
                    : HasDirectSourceBackedPlanningCandidateEvidence(candidate)))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.structured_evidence|stage=fallback_filter.structured_evidence"
                + $"|remaining={fallbackStructuredEvidence.Count}|removed={fallbackConcreteTitle.Count - fallbackStructuredEvidence.Count}|strict={requireStrictStructuredEvidence}|required={requireDirectPageEvidence}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackStructuredEvidence.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackPositiveScore = fallbackStructuredEvidence
            .Where(candidate => string.IsNullOrWhiteSpace(query) || candidate.Score > 0)
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_filter.score|stage=fallback_filter.score"
                + $"|remaining={fallbackPositiveScore.Count}|removed={fallbackStructuredEvidence.Count - fallbackPositiveScore.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackPositiveScore.Take(8).Select(static candidate => candidate.Title))}");
        }

        var fallbackCandidates = FilterSourceBackedPlanningCandidatesToRequestedRetrievalRoutes(fallbackPositiveScore, query)
            .OrderByDescending(candidate => ComputeSourceBackedPlanningCandidateRankScore(candidate, query))
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(BuildSourceBackedPlanningCandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestSourceBackedPlanningDuplicate(group, query))
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=fallback_candidates.end|stage=fallback_candidates.end"
                + $"|fallbackCandidates={fallbackCandidates.Count}|currentCandidates={candidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", fallbackCandidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        var combinedCandidates = candidates
            .Concat(fallbackCandidates)
            .GroupBy(BuildSourceBackedPlanningCandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestSourceBackedPlanningDuplicate(group, query))
            .OrderByDescending(candidate => ComputeSourceBackedPlanningCandidateRankScore(candidate, query))
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => ComputeSourceBackedEvidenceRichnessScore(candidate.Hit))
            .ThenByDescending(candidate => candidate.Hit.Score)
            .ToList();

        var finalFilterInput = FilterSourceBackedPlanningCandidatesToRequestedRetrievalRoutes(combinedCandidates, query)
            .Select(candidate => new
            {
                Candidate = candidate,
                RejectionReason = ExplainSourceBackedPlanningCandidateRejection(
                    candidate,
                    query,
                    requireDirectPageEvidence,
                    requireStrictStructuredEvidence,
                    dominantTopLevelScope)
            })
            .ToList();
        if (traceSelection)
        {
            var removedSamples = finalFilterInput
                .Where(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))
                .Take(6)
                .Select(static item => $"{item.RejectionReason}:{item.Candidate.Title}");
            var reasonCounts = finalFilterInput
                .Where(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))
                .GroupBy(static item => item.RejectionReason, StringComparer.OrdinalIgnoreCase)
                .Select(static group => $"{group.Key}={group.Count()}");
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=final_filter.rejections|stage=final_filter.rejections"
                + $"|removed={finalFilterInput.Count(static item => !string.IsNullOrWhiteSpace(item.RejectionReason))}"
                + $"|kept={finalFilterInput.Count(static item => string.IsNullOrWhiteSpace(item.RejectionReason))}"
                + $"|reasons={FormatPlanningTraceValue(string.Join(",", reasonCounts))}"
                + $"|samples={FormatPlanningTraceValue(string.Join("; ", removedSamples))}"
                + $"|ms={selectionStopwatch!.ElapsedMilliseconds}");
        }

        var finalCandidates = RemoveSourceBackedPlanningPartialTitleDuplicates(finalFilterInput
                .Where(static item => string.IsNullOrWhiteSpace(item.RejectionReason))
                .Select(static item => item.Candidate))
            .Take(maxItems)
            .ToList();
        if (traceSelection)
        {
            ClientLog.Info(
                "ToolAgent planning candidate selection: trace_path=rag.planning.candidate_selection|trace_step=end|stage=end"
                + $"|candidates={finalCandidates.Count}|combinedCandidates={combinedCandidates.Count}|ms={selectionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", finalCandidates.Take(10).Select(static candidate => candidate.Title))}");
        }

        return finalCandidates;
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> RemoveSourceBackedPlanningPartialTitleDuplicates(
        IEnumerable<SourceBackedOptionCandidate> candidates)
    {
        var list = candidates.ToArray();
        if (list.Length <= 1)
            return list;

        return list
            .Where(candidate => !list.Any(other =>
                !ReferenceEquals(candidate, other)
                && ShouldDropSourceBackedPlanningPartialTitleDuplicate(candidate, other)))
            .ToArray();
    }

    private static bool ShouldDropSourceBackedPlanningPartialTitleDuplicate(
        SourceBackedOptionCandidate candidate,
        SourceBackedOptionCandidate other)
    {
        var sameVisiblePage = string.Equals(
            BuildRagHitVisiblePageMergeKey(candidate.Hit),
            BuildRagHitVisiblePageMergeKey(other.Hit),
            StringComparison.OrdinalIgnoreCase);
        if (sameVisiblePage
            && LooksLikePartialDuplicatePlanningTitle(candidate.Title, other.Title))
        {
            return !CandidateHasExactAnchoredContentCardTitle(candidate)
                || CandidateHasExactAnchoredContentCardTitle(other);
        }

        if (sameVisiblePage
            && CandidateHasExactAnchoredContentCardTitle(other)
            && LooksLikeContextExtendedDuplicatePlanningTitle(candidate.Title, other.Title))
        {
            return true;
        }

        return LooksLikeCrossPageTruncatedPlanningTitle(candidate.Title, other.Title);
    }

    private static bool CandidateHasExactAnchoredContentCardTitle(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || candidate.Hit.MatchedContentCards is not { Count: > 0 } cards)
        {
            return false;
        }

        return cards.Any(card =>
            SourceBackedContentCardOrContextHasExplicitPageAnchor(candidate.Hit, card)
            && ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            && ExtractSourceBackedCardTitleVariants(card.Title)
                .Select(title => NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title)))
                .Any(title => string.Equals(title, normalizedTitle, StringComparison.Ordinal)));
    }

    private static bool LooksLikeContextExtendedDuplicatePlanningTitle(string candidateTitle, string anchoredTitle)
    {
        var candidate = NormalizeLexicalLookup(candidateTitle);
        var anchored = NormalizeLexicalLookup(anchoredTitle);
        if (anchored.Length < 8
            || candidate.Length <= anchored.Length
            || string.Equals(candidate, anchored, StringComparison.Ordinal))
        {
            return false;
        }

        var index = candidate.IndexOf(anchored, StringComparison.Ordinal);
        if (index < 0)
            return false;

        var before = CollapseWhitespace(candidate[..index]);
        var after = CollapseWhitespace(candidate[(index + anchored.Length)..]);
        var extra = CollapseWhitespace(before + " " + after);
        if (extra.Length is 0 or > 32)
            return false;

        var anchoredTerms = ExtractPlanningAnswerSupportTerms(anchored)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (anchoredTerms.Length < 2)
            return false;

        var extraTerms = ExtractPlanningAnswerSupportTerms(extra)
            .Where(static term => !IsGenericStructuredPlanningEvidenceBridgeTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return extraTerms.Length <= 3;
    }

    private static bool LooksLikePartialDuplicatePlanningTitle(string candidateTitle, string otherTitle)
    {
        var candidate = NormalizeLexicalLookup(candidateTitle);
        var other = NormalizeLexicalLookup(otherTitle);
        if (candidate.Length < 8
            || other.Length <= candidate.Length
            || string.Equals(candidate, other, StringComparison.Ordinal))
        {
            return false;
        }

        var candidateTerms = ExtractPlanningAnswerSupportTerms(candidate)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateTerms.Length == 0)
            return false;

        if (candidateTerms.Length == 1)
        {
            return candidate.Length >= 8
                && (other.StartsWith(candidate, StringComparison.Ordinal)
                    || other.EndsWith(candidate, StringComparison.Ordinal)
                    || other.IndexOf(candidate, StringComparison.Ordinal) is > 0 and <= 10);
        }

        var otherTerms = ExtractPlanningAnswerSupportTerms(other)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var matchedTerms = candidateTerms.Count(term => otherTerms.Contains(term) || other.Contains(term, StringComparison.Ordinal));
        if (matchedTerms < candidateTerms.Length)
            return false;

        if (other.StartsWith(candidate, StringComparison.Ordinal)
            || other.EndsWith(candidate, StringComparison.Ordinal))
        {
            return true;
        }

        var index = other.IndexOf(candidate, StringComparison.Ordinal);
        return index is > 0 and <= 10;
    }

    private static bool LooksLikeCrossPageTruncatedPlanningTitle(string candidateTitle, string otherTitle)
    {
        var candidate = NormalizeLexicalLookup(candidateTitle);
        var other = NormalizeLexicalLookup(otherTitle);
        if (candidate.Length < 8
            || other.Length <= candidate.Length
            || !other.StartsWith(candidate, StringComparison.Ordinal))
        {
            return false;
        }

        var candidateTerms = ExtractPlanningAnswerSupportTerms(candidate)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var otherTerms = ExtractPlanningAnswerSupportTerms(other)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidateTerms.Length < 2 || otherTerms.Length < candidateTerms.Length)
            return false;

        for (var i = 0; i < candidateTerms.Length - 1; i++)
        {
            if (!string.Equals(candidateTerms[i], otherTerms[i], StringComparison.Ordinal))
                return false;
        }

        var lastCandidateTerm = candidateTerms[^1];
        var correspondingOtherTerm = otherTerms[candidateTerms.Length - 1];
        return lastCandidateTerm.Length >= 4
            && correspondingOtherTerm.StartsWith(lastCandidateTerm, StringComparison.Ordinal)
            && correspondingOtherTerm.Length >= lastCandidateTerm.Length + 2;
    }
}
