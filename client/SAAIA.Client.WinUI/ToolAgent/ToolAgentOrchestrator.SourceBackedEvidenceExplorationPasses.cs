using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> BuildSourceBackedEvidenceExplorationPasses(
        ToolResults toolResults,
        string? query,
        string language,
        bool forceBroadenedExploration = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SourceBackedEvidenceExplorationPass>();

        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language);
        forceBroadenedExploration |= IsBroadenedSourceSearchConfirmationEnvelope(query);
        if (!analysis.ShouldExplore && !forceBroadenedExploration)
            return Array.Empty<SourceBackedEvidenceExplorationPass>();

        var passes = new List<SourceBackedEvidenceExplorationPass>();
        var emittedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPass(string label, string purpose, IEnumerable<string> rawQueries, int maxQueries)
        {
            var queries = rawQueries
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(CollapseWhitespace)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(emittedQueries.Add)
                .Take(maxQueries)
                .ToArray();
            if (queries.Length == 0)
                return;

            passes.Add(new SourceBackedEvidenceExplorationPass(
                label,
                purpose,
                queries,
                Origin: "deterministic_seed"));
        }

        var usesPlanningCoverage = UsesSourceBackedPlanningCoverage(query);
        if (usesPlanningCoverage)
        {
            AddPass(
                "slot_balancing_inventory",
                "Probe each requested slot/type with generic inventory vocabulary before broader discovery.",
                BuildStructuredPlanningSlotBalancingInventoryRetrievalQueries(query),
                24);
            AddPass(
                "candidate_inventory",
                "Search generic candidate inventories without binding to placement axes.",
                BuildStructuredPlanningCandidateInventoryRetrievalQueries(query),
                18);
            AddPass(
                "planning_exploration",
                "Find more candidate units and slots for a structured source-backed plan.",
                BuildPlanningExplorationRetrievalQueries(query),
                12);
            AddPass(
                "candidate_discovery",
                "Explore adjacent candidate vocabulary when the first planning evidence is too narrow.",
                BuildSourceBackedCandidateDiscoveryRetrievalQueries(query),
                18);
            if (!ShouldDeferSparseSourceBackedPlanningAnchorFollowup(analysis, query))
            {
                AddPass(
                    "anchor_discovery",
                    "Probe requested anchors, constraints and slot terms independently.",
                    BuildSourceBackedAnchorDiscoveryRetrievalQueries(query),
                    16);
            }
        }
        else
        {
            AddPass(
                "navigation_discovery",
                "Find document profiles, indexes and title anchors before selecting concrete units.",
                BuildNavigationDiscoveryRetrievalQueries(query),
                16);
            AddPass(
                "evidence_expansion",
                "Broaden source-backed retrieval around the explicit request.",
                BuildSourceBackedEvidenceExpansionRetrievalQueries(query),
                16);
            AddPass(
                "candidate_discovery",
                "Explore option, candidate and support vocabulary for broad synthesis.",
                BuildSourceBackedCandidateDiscoveryRetrievalQueries(query),
                18);
            AddPass(
                "anchor_discovery",
                "Probe requested anchors and option kinds separately when direct evidence is sparse.",
                BuildSourceBackedAnchorDiscoveryRetrievalQueries(query),
                16);
        }

        return passes.Take(MaxSourceBackedEvidenceExplorationPasses).ToArray();
    }

    private static SourceBackedEvidenceExplorationPass? BuildSourceBackedRouteAnchorFollowupExplorationPass(
        ToolResults toolResults,
        string query,
        string language)
    {
        var queries = BuildSourceBackedRouteAnchorFollowupRetrievalQueries(toolResults, query, language);
        return queries.Length == 0
            ? null
            : new SourceBackedEvidenceExplorationPass(
                "anchor_followup",
                "Use discovered navigation/title anchors as concrete retrieval seeds.",
                queries,
                Origin: "anchor_followup");
    }

    private static string BuildSourceBackedAnchorFollowupSignature(
        IEnumerable<SourceBackedEvidenceExplorationPass> documentScopedPasses,
        SourceBackedEvidenceExplorationPass? globalPass)
    {
        var parts = new List<string>();
        foreach (var pass in documentScopedPasses
                     .Concat(globalPass is null
                         ? Enumerable.Empty<SourceBackedEvidenceExplorationPass>()
                         : new[] { globalPass }))
        {
            var queries = pass.Queries
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Select(NormalizeGeneratedSourceBackedExplorationQueryForDedup)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            if (queries.Length == 0)
                continue;

            var scope = CollapseWhitespace(
                string.Join(
                    "|",
                    pass.Label,
                    pass.CategoryScope ?? string.Empty,
                    pass.DocId ?? string.Empty,
                    pass.DocPath ?? string.Empty,
                    pass.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    pass.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
            parts.Add($"{NormalizeGeneratedSourceBackedExplorationQueryForDedup(scope)}=>{string.Join(",", queries)}");
        }

        return string.Join(
            "|",
            parts
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12));
    }

    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> BuildSourceBackedDocumentScopedRouteAnchorFollowupExplorationPasses(
        ToolResults toolResults,
        string query,
        string language)
    {
        var usesStructuredPlanning = UsesSourceBackedPlanningCoverage(query);
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query).ToArray();
        var supportTerms = BuildPlanningExplorationSupportTerms(query)
            .Concat(BuildPlanningExpansionSuffixes(query))
            .Concat(BuildCandidateExpansionSuffixes(query))
            .Where(term => !usesStructuredPlanning
                           || !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 2 : 4)
            .ToArray();
        var slotTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Concat(ExtractPlanningConstraintRetrievalTerms(NormalizeLexicalLookup(query)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 3 : 4)
            .ToArray();
        var contentTerms = BuildRouteAnchorFollowupContentTerms(query, language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 2 : 6)
            .ToArray();
        var guardSubjectlessReferenceAnchors = usesStructuredPlanning;
        var observedPlanningCandidateTitleKeys = BuildObservedStructuredPlanningCandidateAnchorTitleKeys(toolResults, query, language);
        var titlePerPageLimit = usesStructuredPlanning ? 2 : 8;
        var pageQueryLimit = usesStructuredPlanning ? 2 : 8;
        var typoVariantLimit = usesStructuredPlanning ? 1 : 2;
        var contentTermLimit = usesStructuredPlanning ? 1 : 3;
        var supportTermLimit = usesStructuredPlanning ? 1 : 2;
        var slotTermLimit = usesStructuredPlanning ? 1 : 2;
        var queryLimit = usesStructuredPlanning ? 8 : 18;

        var candidates = new List<(SourceBackedDocumentNavigationFollowupLabel Label, string Title, int Score, int Index)>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in EnumerateRecentSourceBackedNavigationItems(toolResults, query))
        {
            foreach (var candidate in ExtractDocumentNavigationFollowupLabels(item.Result))
            {
                if (string.IsNullOrWhiteSpace(candidate.DocId) && string.IsNullOrWhiteSpace(candidate.DocPath))
                    continue;

                foreach (var title in ExpandTreeNavigationAnchorLabel(candidate.Label))
                {
                    var cleaned = CleanNavigationRouteAnchorTitle(title);
                    if (!IsUsableSourceBackedOptionTitle(cleaned)
                        || LooksLikeNavigationIndexHeadingTitle(cleaned)
                        || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
                        || (usesStructuredPlanning && LooksLikeWeakStructuredPlanningAnchorFollowupTitle(cleaned))
                        || LooksLikeWeakSourceBackedOptionTitle(cleaned)
                        || StructuredPlanningAnchorTitleAlreadyObservedAsCandidate(cleaned, observedPlanningCandidateTitleKeys)
                        || (guardSubjectlessReferenceAnchors
                            && LooksLikeSubjectlessReferenceNavigationFollowupLabel(candidate, cleaned, queryTerms)))
                    {
                        continue;
                    }

                    var key = $"{candidate.DocId}|{candidate.DocPath}|{candidate.TargetPageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{candidate.TargetPageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{NormalizeLexicalLookup(cleaned)}";
                    if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                        continue;

                    var score = ComputeTreeNavigationAnchorFollowupScore(cleaned, candidate.RawLabel, queryTerms)
                                + candidate.ScoreHint;
                    candidates.Add((candidate, cleaned, score, index++));
                }
            }
        }

        foreach (var candidate in ExtractSummarySearchFollowupLabels(toolResults, query))
        {
            if (string.IsNullOrWhiteSpace(candidate.DocId) && string.IsNullOrWhiteSpace(candidate.DocPath))
                continue;

            foreach (var title in ExpandTreeNavigationAnchorLabel(candidate.Label))
            {
                var cleaned = CleanNavigationRouteAnchorTitle(title);
                if (!IsUsableSourceBackedOptionTitle(cleaned)
                    || LooksLikeNavigationIndexHeadingTitle(cleaned)
                    || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
                    || (usesStructuredPlanning && LooksLikeWeakStructuredPlanningAnchorFollowupTitle(cleaned))
                    || LooksLikeWeakSourceBackedOptionTitle(cleaned)
                    || StructuredPlanningAnchorTitleAlreadyObservedAsCandidate(cleaned, observedPlanningCandidateTitleKeys)
                    || (guardSubjectlessReferenceAnchors
                        && LooksLikeSubjectlessReferenceNavigationFollowupLabel(candidate, cleaned, queryTerms)))
                {
                    continue;
                }

                var key = $"{candidate.DocId}|{candidate.DocPath}|{candidate.TargetPageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{candidate.TargetPageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}|{NormalizeLexicalLookup(cleaned)}";
                if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                    continue;

                var score = ComputeTreeNavigationAnchorFollowupScore(cleaned, candidate.RawLabel, queryTerms)
                            + candidate.ScoreHint;
                candidates.Add((candidate, cleaned, score, index++));
            }
        }

        var passes = new List<SourceBackedEvidenceExplorationPass>();
        var grouped = candidates
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .GroupBy(
                static item =>
                {
                    var scope = string.IsNullOrWhiteSpace(item.Label.DocId)
                        ? item.Label.DocPath ?? string.Empty
                        : item.Label.DocId;
                    if (string.IsNullOrWhiteSpace(scope))
                        return string.Empty;

                    var pageStart = NormalizeSourceBackedNavigationTargetPageStart(item.Label.TargetPageStart);
                    var pageEnd = NormalizeSourceBackedNavigationTargetPageEnd(item.Label.TargetPageEnd, pageStart);
                    return $"{scope}|p:{pageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}-{pageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
                },
                StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Take(ResolveSourceBackedDocumentScopedAnchorFollowupLimit(query));

        foreach (var group in grouped)
        {
            var first = group.First().Label;
            var queries = new List<string>();
            var queryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titleCandidates = group
                .GroupBy(static candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
                .Select(static titleGroup => titleGroup
                    .OrderByDescending(static candidate => candidate.Score)
                    .ThenBy(static candidate => candidate.Index)
                    .First())
                .ToArray();
            var titleCandidateLabels = titleCandidates.Select(static candidate => candidate.Title).ToArray();
            foreach (var item in titleCandidates
                         .OrderByDescending(candidate => ComputeDocumentScopedAnchorFollowupTitleSpecificity(candidate.Title, titleCandidateLabels))
                         .ThenByDescending(static candidate => candidate.Score)
                         .ThenBy(static candidate => candidate.Index)
                         .Take(titlePerPageLimit))
            {
                var title = item.Title;
                AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, title);
                AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, QuoteLookupTitle(title));

                foreach (var pageQuery in BuildSourceBackedNavigationTargetPageQueries(title, item.Label).Take(pageQueryLimit))
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, pageQuery);

                foreach (var variant in BuildTypoTolerantQueryVariants(title).Take(typoVariantLimit))
                {
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, variant);
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, QuoteLookupTitle(variant));
                }

                foreach (var term in contentTerms.Take(contentTermLimit))
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, $"{title} {term}");

                foreach (var suffix in supportTerms.Take(supportTermLimit))
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, $"{title} {suffix}");

                foreach (var slot in slotTerms.Take(slotTermLimit))
                    AddGeneratedSourceBackedFollowupQuery(queries, queryKeys, $"{title} {slot}");
            }

            var finalQueries = queries
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(CollapseWhitespace)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(queryLimit)
                .ToArray();
            if (finalQueries.Length == 0)
                continue;

            passes.Add(new SourceBackedEvidenceExplorationPass(
                "anchor_followup_doc_scope",
                "Use resolved document navigation anchors as concrete retrieval seeds inside the same document.",
                finalQueries,
                first.CategoryPath,
                first.DocId,
                first.DocPath,
                NormalizeSourceBackedNavigationTargetPageStart(first.TargetPageStart),
                NormalizeSourceBackedNavigationTargetPageEnd(
                    first.TargetPageEnd,
                    NormalizeSourceBackedNavigationTargetPageStart(first.TargetPageStart)),
                "anchor_followup"));
        }

        return passes.ToArray();
    }

    private static int ComputeDocumentScopedAnchorFollowupTitleSpecificity(
        string? title,
        IReadOnlyList<string> siblingTitles)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return int.MinValue;

        var tokenCount = Regex.Matches(normalizedTitle, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant).Count;
        var score = Math.Min(tokenCount, 8) * 2
                    + Math.Min(normalizedTitle.Length / 8, 8);

        if (tokenCount <= 2)
            score -= 3;

        if (siblingTitles.Any(sibling =>
            {
                var normalizedSibling = NormalizeLexicalLookup(sibling);
                return normalizedSibling.Length > normalizedTitle.Length
                       && NormalizedLookupContainsWholePhrase(normalizedSibling, normalizedTitle);
            }))
        {
            score -= 30;
        }

        return score;
    }

    private static int? NormalizeSourceBackedNavigationTargetPageStart(int? page)
        => page is > 0 ? page.Value : null;

    private static int? NormalizeSourceBackedNavigationTargetPageEnd(int? pageEnd, int? pageStart)
    {
        if (pageStart is null)
            return null;

        if (pageEnd is null || pageEnd.Value < pageStart.Value)
            return pageStart.Value;

        return pageEnd.Value;
    }

    private static IEnumerable<string> BuildSourceBackedNavigationTargetPageQueries(
        string title,
        SourceBackedDocumentNavigationFollowupLabel label)
    {
        if (string.IsNullOrWhiteSpace(title)
            || label.TargetPageStart is null
            || label.TargetPageStart.Value <= 0)
        {
            yield break;
        }

        var start = label.TargetPageStart.Value;
        var end = label.TargetPageEnd is not null && label.TargetPageEnd.Value >= start
            ? label.TargetPageEnd.Value
            : start;

        yield return $"{title} page {start}";
        yield return $"{title} p {start}";

        if (end > start)
        {
            yield return $"{title} pages {start}-{end}";
            yield return $"{title} p {start}-{end}";
        }
        else
        {
            var neighborEnd = Math.Min(start + 1, 9999);
            yield return $"{title} pages {start}-{neighborEnd}";
            yield return $"{title} p {start}-{neighborEnd}";
        }
    }

    private static bool NormalizedLookupContainsWholePhrase(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
            return false;

        return $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);
    }

    private static bool LooksLikeSubjectlessReferenceNavigationFollowupLabel(
        SourceBackedDocumentNavigationFollowupLabel label,
        string title,
        IReadOnlyList<string> queryTerms)
    {
        if (SourceBackedNavigationFollowupLabelMatchesQuerySubject(label, title, queryTerms))
            return false;

        var lookup = NormalizeLexicalLookup($"{title} {label.Label} {label.RawLabel}");
        if (string.IsNullOrWhiteSpace(lookup))
            return false;

        return Regex.IsMatch(
                   lookup,
                   @"\b(?:din|en|iso|iec|sia|sn|astm|asme|bs|nf|vdi|vde)\s+(?:en\s+)?\d{2,5}(?:[-\s]\d+)?\b",
                   RegexOptions.CultureInvariant)
               || Regex.IsMatch(
                   lookup,
                   @"\b(?:standard|norme|norm|requirements?|conformity|assessment|execution)\b.{0,80}\b(?:\d{2,5}|structures?|components?|construction)\b",
                   RegexOptions.CultureInvariant);
    }

    private static bool SourceBackedNavigationFollowupLabelMatchesQuerySubject(
        SourceBackedDocumentNavigationFollowupLabel label,
        string title,
        IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count == 0)
            return true;

        var haystack = NormalizeLexicalLookup(string.Join(
            ' ',
            new[]
            {
                title,
                label.Label,
                label.RawLabel,
                label.DocPath ?? string.Empty,
                label.CategoryPath ?? string.Empty
            }));
        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        return queryTerms.Any(term =>
        {
            var normalizedTerm = NormalizeLexicalLookup(term);
            return normalizedTerm.Length >= 4
                   && haystack.Contains(normalizedTerm, StringComparison.Ordinal);
        });
    }

    private static HashSet<string> BuildObservedStructuredPlanningCandidateAnchorTitleKeys(
        ToolResults toolResults,
        string? query,
        string language)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!UsesSourceBackedPlanningCoverage(query))
            return keys;

        var stopwatch = Stopwatch.StartNew();
        const int observedHitLimit = 160;
        var observedHits = EnumerateRagHitSummaries(toolResults)
            .Take(observedHitLimit + 1)
            .ToList();
        var hitLimitReached = observedHits.Count > observedHitLimit;
        if (hitLimitReached)
            observedHits.RemoveAt(observedHits.Count - 1);
        var candidateLimit = Math.Clamp(
            Math.Max(ResolveSourceBackedPlanningTargetItemCount(query), 12),
            8,
            20);
        var shouldRunCandidateSelection = observedHits.Count <= 80;
        ClientLog.Info(
            "ToolAgent anchor followup observed title keys: stage=start"
            + $"|hits={observedHits.Count}"
            + $"|hitLimitReached={hitLimitReached}"
            + $"|candidateLimit={candidateLimit}"
            + $"|runCandidateSelection={shouldRunCandidateSelection}");
        if (shouldRunCandidateSelection)
        {
            var candidateStopwatch = Stopwatch.StartNew();
            ClientLog.Info(
                "ToolAgent anchor followup observed title keys: stage=selected_candidates.start"
                + $"|hits={observedHits.Count}"
                + $"|candidateLimit={candidateLimit}");
            foreach (var candidate in SelectSourceBackedPlanningCandidates(
                         toolResults,
                         query,
                         candidateLimit,
                         language,
                         requireStrictStructuredEvidence: false))
            {
                AddObservedStructuredPlanningCandidateAnchorTitleKey(keys, candidate.Title, query);
            }

            ClientLog.Info(
                "ToolAgent anchor followup observed title keys: stage=selected_candidates.end"
                + $"|keys={keys.Count}"
                + $"|ms={candidateStopwatch.ElapsedMilliseconds}");
        }
        else
        {
            ClientLog.Info(
                "ToolAgent anchor followup observed title keys: stage=selected_candidates.skip"
                + "|reason=large_observed_hit_set");
        }

        var hitTitleStopwatch = Stopwatch.StartNew();
        var lastHitTitleProgressMs = 0L;
        for (var observedHitIndex = 0; observedHitIndex < observedHits.Count; observedHitIndex++)
        {
            var hit = observedHits[observedHitIndex];
            if (LooksLikeNavigationOnlyHit(hit)
                || LooksLikePageReferenceOnlyHit(hit)
                || LooksLikeFinalSourceBackedPlanningOrientationSurface(hit))
            {
                continue;
            }

            var titles = ExtractSourceBackedTitleCandidates(hit)
                .Concat(hit.MatchedContentCards?.Select(static card => card.Title) ?? Enumerable.Empty<string>())
                .Concat(new[] { ExtractSourceBackedOptionTitle(hit, query) });
            foreach (var rawTitle in titles)
                AddObservedStructuredPlanningCandidateAnchorTitleKey(keys, rawTitle, query);

            var processedHits = observedHitIndex + 1;
            var elapsedMs = hitTitleStopwatch.ElapsedMilliseconds;
            if (processedHits == observedHits.Count
                || processedHits % 25 == 0
                || elapsedMs - lastHitTitleProgressMs >= 15000)
            {
                ClientLog.Info(
                    "ToolAgent anchor followup observed title keys: stage=hit_titles.progress"
                    + $"|processedHits={processedHits}"
                    + $"|totalHits={observedHits.Count}"
                    + $"|keys={keys.Count}"
                    + $"|ms={elapsedMs}"
                    + $"|lastDoc={FormatPlanningTraceValue(hit.DocName ?? hit.DocPath)}"
                    + $"|lastPage={hit.PageStart}");
                lastHitTitleProgressMs = elapsedMs;
            }
        }

        ClientLog.Info(
            "ToolAgent anchor followup observed title keys: stage=end"
            + $"|keys={keys.Count}"
            + $"|hits={observedHits.Count}"
            + $"|hitLimitReached={hitLimitReached}"
            + $"|ms={stopwatch.ElapsedMilliseconds}");
        return keys;
    }

    private static void AddObservedStructuredPlanningCandidateAnchorTitleKey(
        HashSet<string> keys,
        string? title,
        string? query)
    {
        var cleaned = CleanSourceBackedOptionTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(cleaned)
            || !LooksLikeConcreteStructuredPlanningCandidateTitle(cleaned))
        {
            return;
        }

        var key = NormalizeLexicalLookup(cleaned);
        if (!string.IsNullOrWhiteSpace(key))
            keys.Add(key);
    }

    private static bool StructuredPlanningAnchorTitleAlreadyObservedAsCandidate(
        string? title,
        IReadOnlySet<string> observedCandidateTitleKeys)
    {
        if (observedCandidateTitleKeys.Count == 0)
            return false;

        var key = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title));
        return !string.IsNullOrWhiteSpace(key)
               && observedCandidateTitleKeys.Contains(key);
    }

    private static bool HasSourceBackedRouteAnchorFollowupQueries(
        ToolResults toolResults,
        string query,
        string language)
        => BuildSourceBackedRouteAnchorFollowupRetrievalQueries(toolResults, query, language).Length > 0;

    private static string[] BuildSourceBackedRouteAnchorFollowupRetrievalQueries(
        ToolResults toolResults,
        string query,
        string language)
    {
        var usesStructuredPlanning = UsesSourceBackedPlanningCoverage(query);
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tried in EnumerateRagHitSummaries(toolResults)
                     .Select(static hit => hit.RetrievalQuery)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Select(static value => NormalizeGeneratedSourceBackedExplorationQueryForDedup(value)))
        {
            if (!string.IsNullOrWhiteSpace(tried))
                emitted.Add(tried);
        }

        var supportTerms = BuildPlanningExplorationSupportTerms(query)
            .Concat(BuildPlanningExpansionSuffixes(query))
            .Concat(BuildCandidateExpansionSuffixes(query))
            .Where(term => !usesStructuredPlanning
                           || !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 3 : 6)
            .ToArray();
        var slotTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Concat(ExtractPlanningConstraintRetrievalTerms(NormalizeLexicalLookup(query)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 4 : 6)
            .ToArray();
        var contentTerms = BuildRouteAnchorFollowupContentTerms(query, language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(usesStructuredPlanning ? 2 : 8)
            .ToArray();
        var titleLimit = usesStructuredPlanning ? 8 : 24;
        var typoVariantLimit = usesStructuredPlanning ? 1 : int.MaxValue;
        var contentTermLimit = usesStructuredPlanning ? 1 : 3;
        var supportTermLimit = usesStructuredPlanning ? 1 : 2;
        var slotTermLimit = usesStructuredPlanning ? 2 : 2;
        var queryLimit = usesStructuredPlanning ? 12 : 32;

        var titles = ExtractSourceBackedRouteAnchorFollowupTitles(toolResults, query)
            .Concat(ExtractSourceBackedDocumentNavigationFollowupTitles(toolResults, query))
            .Concat(ExtractSourceBackedTreeFollowupTitles(toolResults, query))
            .Concat(ExtractSourceBackedSummaryFollowupTitles(toolResults, query))
            .Where(title => !usesStructuredPlanning || !LooksLikeWeakStructuredPlanningAnchorFollowupTitle(title))
            .Take(titleLimit)
            .ToArray();

        foreach (var title in titles)
            AddGeneratedSourceBackedFollowupQuery(queries, emitted, title);

        foreach (var title in titles)
        {
            AddGeneratedSourceBackedFollowupQuery(queries, emitted, QuoteLookupTitle(title));

            foreach (var variant in BuildTypoTolerantQueryVariants(title).Take(typoVariantLimit))
            {
                AddGeneratedSourceBackedFollowupQuery(queries, emitted, variant);
                AddGeneratedSourceBackedFollowupQuery(queries, emitted, QuoteLookupTitle(variant));
            }

            foreach (var term in contentTerms.Take(contentTermLimit))
                AddGeneratedSourceBackedFollowupQuery(queries, emitted, $"{title} {term}");

            foreach (var suffix in supportTerms.Take(supportTermLimit))
                AddGeneratedSourceBackedFollowupQuery(queries, emitted, $"{title} {suffix}");

            foreach (var slot in slotTerms.Take(slotTermLimit))
                AddGeneratedSourceBackedFollowupQuery(queries, emitted, $"{title} {slot}");

            if (queries.Count >= queryLimit)
                break;
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(queryLimit)
            .ToArray();
    }

}
