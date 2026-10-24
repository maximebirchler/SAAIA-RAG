using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalRows;
using static SAAIA.Backend.Retrieval.RetrievalText;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalSelection
{

    internal static void AddRankedMatches(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        int maxPerSection = 2,
        bool prioritizeDocumentProfiles = false,
        string? query = null)
        => AddRankedMatchesDiagnosed(null, selected, selectedKeys, matches, topK, minScore,
            maxPerDoc, maxPerPage, maxPerSection, prioritizeDocumentProfiles, query);

    internal static void AddRankedMatchesDiagnosed(
        RagRetrievalDiagnosticsBuilder? diagnostics,
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        int maxPerSection = 2,
        bool prioritizeDocumentProfiles = false,
        string? query = null)
    {
        var perDoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectedMatch in selected)
        {
            if (string.IsNullOrWhiteSpace(selectedMatch.DocPath))
                continue;
            if (!CountsAgainstChunkQuota(selectedMatch))
                continue;

            var docKey = ResolveMatchDedupDocumentKey(selectedMatch);
            perDoc[docKey] = perDoc.TryGetValue(docKey, out var docCount) ? docCount + 1 : 1;
            var pageKey = $"{docKey}:{selectedMatch.PageStart ?? -1}:{selectedMatch.PageEnd ?? -1}";
            perPage[pageKey] = perPage.TryGetValue(pageKey, out var pageCount) ? pageCount + 1 : 1;
            var sectionKey = BuildSectionKey(selectedMatch);
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secCount) ? secCount + 1 : 1;
        }

        var input = diagnostics is null ? null : matches.ToArray();
        var prioritizedMatches = OrderMatchesForSelection(input ?? matches, prioritizeDocumentProfiles, query);
        if (input is not null)
        {
            var retained = prioritizedMatches.Select(BuildMatchDedupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var excluded in input.Where(match => !retained.Contains(BuildMatchDedupKey(match))))
                diagnostics!.Reject("ordering", "ordering_policy_not_retained", excluded);
            diagnostics!.CapturePhase("interactive_ordered", "selection_policy", null, prioritizedMatches);
        }

        foreach (var match in prioritizedMatches)
        {
            if (selected.Count >= topK)
            {
                if (diagnostics is null) break;
                diagnostics.Reject("selection", "top_k_limit", match);
                continue;
            }
            if (match.Score < minScore)
            { diagnostics?.Reject("selection", "score_threshold", match); continue; }
            if (string.IsNullOrWhiteSpace(match.DocPath))
            { diagnostics?.Reject("selection", "missing_document_path", match); continue; }

            var dedupKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(dedupKey))
            { diagnostics?.Reject("selection", "duplicate_identity", match); continue; }

            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, match)))
            {
                selectedKeys.Remove(dedupKey);
                diagnostics?.Reject("selection", "near_duplicate_page_overlap", match);
                continue;
            }

            if (!IsResolvedTitleOrNavigationRoute(match)
                && LooksLikeNavigationalChunk(match)
                && selected.Any(static existing => IsContentSelectionCandidate(existing)))
            {
                selectedKeys.Remove(dedupKey);
                diagnostics?.Reject("selection", "navigation_noise_with_content_present", match);
                continue;
            }

            var countsAgainstChunkQuota = CountsAgainstChunkQuota(match);
            var docKey = ResolveMatchDedupDocumentKey(match);
            perDoc.TryGetValue(docKey, out var docCountCurrent);
            if (countsAgainstChunkQuota && docCountCurrent >= maxPerDoc)
            {
                selectedKeys.Remove(dedupKey);
                diagnostics?.Reject("selection", "document_limit", match);
                continue;
            }

            var pageKey = $"{docKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCountCurrent);
            if (countsAgainstChunkQuota && pageCountCurrent >= maxPerPage)
            {
                selectedKeys.Remove(dedupKey);
                diagnostics?.Reject("selection", "page_limit", match);
                continue;
            }

            // CDC v3.1 §11.3: max 2 chunks per section
            var sectionKey = BuildSectionKey(match);
            if (countsAgainstChunkQuota && sectionKey != null)
            {
                perSection.TryGetValue(sectionKey, out var secCountCurrent);
                if (secCountCurrent >= maxPerSection)
                {
                    selectedKeys.Remove(dedupKey);
                    diagnostics?.Reject("selection", "section_limit", match);
                    continue;
                }
            }

            selected.Add(match);
            if (countsAgainstChunkQuota)
            {
                perDoc[docKey] = docCountCurrent + 1;
                perPage[pageKey] = pageCountCurrent + 1;
                if (sectionKey != null)
                    perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secUpdated) ? secUpdated + 1 : 1;
            }
        }
    }


    internal static void MergeComparativeSubjectBackfillMatches(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IReadOnlyList<RagMatch> backfillMatches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        bool prioritizeDocumentProfiles,
        string query)
    {
        if (backfillMatches.Count == 0 || topK <= 0)
            return;

        var backfillKeys = backfillMatches
            .Select(BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count < topK)
        {
            var selectedDocPaths = selected
                .Select(static match => NormalizeRagDocPath(match.DocPath))
                .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unseenBackfillMatches = backfillMatches
                .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
                .Where(match =>
                {
                    var normalizedDocPath = NormalizeRagDocPath(match.DocPath);
                    return !string.IsNullOrWhiteSpace(normalizedDocPath)
                        && !selectedDocPaths.Contains(normalizedDocPath);
                })
                .ToList();

            if (unseenBackfillMatches.Count > 0)
            {
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    unseenBackfillMatches,
                    topK,
                    minScore: 0.0,
                    maxPerDoc: 1,
                    maxPerPage,
                    prioritizeDocumentProfiles: false,
                    query: query);
                if (selected.Count >= topK)
                    return;
            }
        }

        var mergedCandidates = selected
            .Concat(backfillMatches)
            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(match => backfillKeys.Contains(BuildMatchDedupKey(match)))
                .ThenByDescending(match => match.Score)
                .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.ChunkIndex)
                .First())
            .ToList();

        selected.Clear();
        selectedKeys.Clear();
        AddRankedMatches(
            selected,
            selectedKeys,
            mergedCandidates,
            topK,
            minScore,
            maxPerDoc,
            maxPerPage,
            prioritizeDocumentProfiles: prioritizeDocumentProfiles,
            query: query);
        EnsureComparativeBackfillDocumentDiversity(
            selected,
            selectedKeys,
            backfillMatches,
            topK,
            minScore,
            maxPerPage,
            query);
    }


    internal static void EnsureComparativeBackfillDocumentDiversity(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IReadOnlyList<RagMatch> backfillMatches,
        int topK,
        double minScore,
        int maxPerPage,
        string query)
    {
        if (selected.Count == 0
            || topK <= 0
            || backfillMatches.Count == 0
            || !ShouldPreferComparativeDocumentDiversity(query)
            || !ShouldTreatAsBroadDiversityQuery(query))
        {
            return;
        }

        var reserveCount = topK <= 3
            ? topK
            : Math.Min(topK, Math.Clamp(topK / 2, 5, 6));
        var reservedCandidates = OrderMatchesForSelection(backfillMatches, prioritizeDocumentProfiles: false, query)
            .Where(match => match.Score >= minScore)
            .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
            .Where(static match => !IsDocumentProfileMatch(match))
            .Where(static match => IsContentSelectionCandidate(match))
            .GroupBy(static match => NormalizeRagDocPath(match.DocPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => group.First())
            .Take(reserveCount)
            .ToList();
        if (reservedCandidates.Count == 0)
            return;

        var protectedDocPaths = reservedCandidates
            .Select(static match => NormalizeRagDocPath(match.DocPath))
            .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
            .Select(static docPath => docPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in reservedCandidates)
        {
            var candidateDocPath = NormalizeRagDocPath(candidate.DocPath);
            if (string.IsNullOrWhiteSpace(candidateDocPath))
                continue;
            if (selected.Any(match => string.Equals(
                    NormalizeRagDocPath(match.DocPath),
                    candidateDocPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidateKey = BuildMatchDedupKey(candidate);
            if (selectedKeys.Contains(candidateKey))
                continue;
            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, candidate)))
                continue;

            if (selected.Count < topK)
            {
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    [candidate],
                    topK,
                    minScore,
                    maxPerDoc: 1,
                    maxPerPage,
                    prioritizeDocumentProfiles: false,
                    query: query);
                continue;
            }

            var replacementIndex = FindComparativeBackfillReplacementIndex(selected, protectedDocPaths, query);
            if (replacementIndex < 0)
                continue;

            var replaced = selected[replacementIndex];
            selectedKeys.Remove(BuildMatchDedupKey(replaced));
            selected[replacementIndex] = candidate;
            selectedKeys.Add(candidateKey);
        }
    }


    internal static void EnsureBroadDiversityCandidateCoverage(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IReadOnlyList<RagMatch> candidatePool,
        int topK,
        double minScore,
        int maxPerPage,
        string query)
    {
        if (topK < 4
            || selected.Count == 0
            || candidatePool.Count == 0
            || !ShouldRunBroadDiversityCandidateCoverage(query, topK))
        {
            return;
        }

        var reserveCount = topK <= 4
            ? topK
            : Math.Min(topK, Math.Clamp(topK - 1, 5, 8));
        var reservedCandidates = OrderMatchesForSelection(candidatePool, prioritizeDocumentProfiles: false, query)
            .Where(match => match.Score >= minScore)
            .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
            .Where(static match => !IsDocumentProfileMatch(match))
            .Where(static match => IsContentSelectionCandidate(match))
            .Where(static match => !LooksLikeNavigationalChunk(match))
            .Where(static match => !LooksLikeGlossaryChunk(match))
            .GroupBy(static match => NormalizeRagDocPath(match.DocPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => group.First())
            .Take(reserveCount)
            .ToList();
        if (reservedCandidates.Count == 0)
            return;

        foreach (var candidate in reservedCandidates)
        {
            var candidateDocPath = NormalizeRagDocPath(candidate.DocPath);
            if (string.IsNullOrWhiteSpace(candidateDocPath))
                continue;
            if (selected.Any(match => string.Equals(
                    NormalizeRagDocPath(match.DocPath),
                    candidateDocPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var candidateKey = BuildMatchDedupKey(candidate);
            if (selectedKeys.Contains(candidateKey))
                continue;
            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, candidate)))
                continue;

            if (selected.Count < topK)
            {
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    [candidate],
                    topK,
                    minScore,
                    maxPerDoc: 1,
                    maxPerPage,
                    prioritizeDocumentProfiles: false,
                    query: query);
                continue;
            }

            var replacementIndex = FindBroadDiversityCoverageReplacementIndex(selected, query);
            if (replacementIndex < 0)
                continue;

            var replaced = selected[replacementIndex];
            selectedKeys.Remove(BuildMatchDedupKey(replaced));
            selected[replacementIndex] = candidate;
            selectedKeys.Add(candidateKey);
        }
    }


    internal static void PrioritizeResponseDiversityCoverage(string query, List<RagMatch> selected, int responseTopK)
    {
        if (responseTopK < 4
            || selected.Count <= responseTopK
            || !ShouldRunBroadDiversityCandidateCoverage(query, responseTopK))
        {
            return;
        }

        var head = selected.Take(responseTopK).ToList();
        var headDocPaths = head
            .Select(static match => NormalizeRagDocPath(match.DocPath))
            .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
            .Select(static docPath => docPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var headHasFullDistinctCoverage = headDocPaths.Count >= responseTopK;

        var tailCandidates = selected
            .Skip(responseTopK)
            .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
            .Where(static match => !IsDocumentProfileMatch(match))
            .Where(static match => IsContentSelectionCandidate(match))
            .Where(static match => !LooksLikeNavigationalChunk(match))
            .Where(static match => !LooksLikeGlossaryChunk(match))
            .GroupBy(static match => NormalizeRagDocPath(match.DocPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && !headDocPaths.Contains(group.Key))
            .Select(static group => group.First())
            .ToList();
        if (tailCandidates.Count == 0)
            return;

        foreach (var candidate in tailCandidates)
        {
            var wasFullDistinctCoverage = headHasFullDistinctCoverage;
            var replacementIndex = headHasFullDistinctCoverage
                ? FindWeakResponseDiversityReplacementIndex(head, candidate, query)
                : FindBroadDiversityCoverageReplacementIndex(head, query);
            if (replacementIndex < 0)
                return;

            selected[replacementIndex] = candidate;
            head[replacementIndex] = candidate;
            var docPath = NormalizeRagDocPath(candidate.DocPath);
            if (!string.IsNullOrWhiteSpace(docPath))
                headDocPaths.Add(docPath);
            headHasFullDistinctCoverage = headDocPaths.Count >= responseTopK;
            if (wasFullDistinctCoverage)
                return;
            if (headDocPaths.Count >= responseTopK)
            {
                continue;
            }
        }
    }


    private static int FindWeakResponseDiversityReplacementIndex(
        IReadOnlyList<RagMatch> head,
        RagMatch candidate,
        string query)
    {
        var candidateDocPath = NormalizeRagDocPath(candidate.DocPath);
        if (string.IsNullOrWhiteSpace(candidateDocPath))
            return -1;

        var candidateEvidence = ComputeContentEvidencePriority(candidate);
        var candidateScore = candidate.Score;
        var replacement = head
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                Evidence = ComputeContentEvidencePriority(match),
                RemovalBucket = ComputeBroadDiversityCoverageRemovalBucket(match, query)
            })
            .Where(item => !string.Equals(
                NormalizeRagDocPath(item.Match.DocPath),
                candidateDocPath,
                StringComparison.OrdinalIgnoreCase))
            .Where(item =>
                !IsDocumentProfileMatch(item.Match)
                && (item.Match.Score + 0.10 < candidateScore
                    || item.Evidence + 0.30 < candidateEvidence
                    || item.RemovalBucket <= 2))
            .OrderBy(item => item.RemovalBucket)
            .ThenBy(item => item.Evidence)
            .ThenBy(item => item.Match.Score)
            .ThenByDescending(item => item.Index)
            .FirstOrDefault();

        return replacement?.Index ?? -1;
    }


    private static int FindBroadDiversityCoverageReplacementIndex(
        IReadOnlyList<RagMatch> selected,
        string query)
    {
        var docCounts = selected
            .Select(static match => NormalizeRagDocPath(match.DocPath))
            .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
            .GroupBy(static docPath => docPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var replacement = selected
            .Select((match, index) => new { Match = match, Index = index })
            .Where(item =>
            {
                var docPath = NormalizeRagDocPath(item.Match.DocPath);
                return string.IsNullOrWhiteSpace(docPath)
                    || !docCounts.TryGetValue(docPath, out var count)
                    || count > 1
                    || !IsContentSelectionCandidate(item.Match);
            })
            .OrderByDescending(item =>
            {
                var docPath = NormalizeRagDocPath(item.Match.DocPath);
                return !string.IsNullOrWhiteSpace(docPath)
                       && docCounts.TryGetValue(docPath, out var count)
                       && count > 1
                    ? 1
                    : 0;
            })
            .ThenBy(item => ComputeBroadDiversityCoverageRemovalBucket(item.Match, query))
            .ThenBy(item => ComputeContentEvidencePriority(item.Match))
            .ThenBy(item => item.Match.Score)
            .ThenByDescending(item => item.Index)
            .FirstOrDefault();

        return replacement?.Index ?? -1;
    }


    private static int ComputeBroadDiversityCoverageRemovalBucket(RagMatch match, string query)
    {
        if (IsDocumentProfileMatch(match))
            return HasMeaningfulDocumentProfileSelectionSupport(match, query) ? 2 : 0;
        if (!IsContentSelectionCandidate(match))
            return 0;
        if (IsDirectTitleTokenRouteMatch(match) && !DirectTitleTokenRouteHasStrongLeadEvidence(match))
            return 1;
        if (ComputeWeakMatchedContentCardTitlePenalty(
                match,
                fullTitleCoverage: false,
                exactTitleScore: 0.0,
                directChunkTitleSignal: 0,
                quotedTitleAnchorSignal: 0.0,
                allowIndependentTitleSignalWaiver: false) > 0)
        {
            return 1;
        }
        if (ComputeContentEvidencePriority(match) < 0.70)
            return 2;

        return 3;
    }


    private static int FindComparativeBackfillReplacementIndex(
        IReadOnlyList<RagMatch> selected,
        HashSet<string> protectedDocPaths,
        string query)
    {
        var replacement = selected
            .Select((match, index) => new { Match = match, Index = index })
            .Where(item =>
            {
                var docPath = NormalizeRagDocPath(item.Match.DocPath);
                return string.IsNullOrWhiteSpace(docPath)
                    || !protectedDocPaths.Contains(docPath);
            })
            .OrderBy(item => ComputeComparativeBackfillReplacementBucket(item.Match, query))
            .ThenBy(item => ComputeContentEvidencePriority(item.Match))
            .ThenBy(item => item.Match.Score)
            .ThenByDescending(item => item.Index)
            .FirstOrDefault();

        return replacement?.Index ?? -1;
    }


    private static int ComputeComparativeBackfillReplacementBucket(RagMatch match, string query)
    {
        if (IsDocumentProfileMatch(match))
            return HasMeaningfulDocumentProfileSelectionSupport(match, query) ? 1 : 0;
        if (!IsContentSelectionCandidate(match))
            return 2;
        if (ComputeContentEvidencePriority(match) < 0.70)
            return 3;
        return 4;
    }


    internal static void RebuildSelectedKeys(IReadOnlyList<RagMatch> selected, HashSet<string> selectedKeys)
    {
        selectedKeys.Clear();
        foreach (var match in selected)
            selectedKeys.Add(BuildMatchDedupKey(match));
    }


    internal static List<RagMatch> PreferRequestedPageWindowMatches(
        IEnumerable<RagMatch> matches,
        int? requestedPageStart,
        int? requestedPageEnd)
    {
        var list = matches as IReadOnlyList<RagMatch> ?? matches.ToList();
        if (list.Count == 0 || requestedPageStart is null || requestedPageStart.Value <= 0)
            return list.ToList();

        var start = requestedPageStart.Value;
        var end = requestedPageEnd is > 0 && requestedPageEnd.Value >= start
            ? requestedPageEnd.Value
            : start;

        return list
            .Select((match, index) =>
            {
                var relation = ComputeRequestedPageWindowRelation(match, start, end);
                var distance = ComputeRequestedPageWindowDistance(match, start, end);
                var boosted = relation > 0
                    ? match with { Score = Math.Min(2.0, match.Score + (relation * 0.18)) }
                    : match;
                return new
                {
                    Match = boosted,
                    Relation = relation,
                    Distance = distance,
                    Index = index
                };
            })
            .OrderByDescending(static item => item.Relation)
            .ThenBy(static item => item.Distance)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match)
            .ToList();
    }


    private static int ComputeRequestedPageWindowRelation(RagMatch match, int requestedPageStart, int requestedPageEnd)
    {
        if (match.PageStart is null && match.PageEnd is null)
            return 0;

        var matchStart = match.PageStart ?? match.PageEnd!.Value;
        var matchEnd = match.PageEnd is > 0 && match.PageEnd.Value >= matchStart
            ? match.PageEnd.Value
            : matchStart;

        if (matchStart <= requestedPageEnd && requestedPageStart <= matchEnd)
            return 3;

        var distance = ComputeRequestedPageWindowDistance(match, requestedPageStart, requestedPageEnd);
        if (distance <= 1)
            return 2;
        if (distance <= 3)
            return 1;
        return 0;
    }


    private static int ComputeRequestedPageWindowDistance(RagMatch match, int requestedPageStart, int requestedPageEnd)
    {
        if (match.PageStart is null && match.PageEnd is null)
            return int.MaxValue;

        var matchStart = match.PageStart ?? match.PageEnd!.Value;
        var matchEnd = match.PageEnd is > 0 && match.PageEnd.Value >= matchStart
            ? match.PageEnd.Value
            : matchStart;

        if (matchStart <= requestedPageEnd && requestedPageStart <= matchEnd)
            return 0;

        return matchEnd < requestedPageStart
            ? requestedPageStart - matchEnd
            : matchStart - requestedPageEnd;
    }


    internal static IReadOnlyList<RagMatch> OrderMatchesForSelection(
        IEnumerable<RagMatch> matches,
        bool prioritizeDocumentProfiles,
        string? query = null)
    {
        var orderedMatches = matches as IReadOnlyList<RagMatch> ?? matches.ToList();
        var quotedPhrases = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : ExtractTitleLookupPhrases(query);
        var prioritizeSituationalConstraintCoverage =
            !string.IsNullOrWhiteSpace(query)
            && ShouldPrioritizeSituationalSelectionConstraintCoverage(query);
        var profileConstraintTokens = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : BuildDocumentProfileSpecificityTokens(query);
        var directSpecificContentTokens = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : BuildDirectSpecificContentCoverageTokens(query);
        var requiredDirectSpecificContentCoverage =
            ResolveRequiredFocusedSpecificContentCoverage(directSpecificContentTokens);
        var prioritizeReferenceVersionOperandTitleSignal =
            !string.IsNullOrWhiteSpace(query)
            && ShouldUseReferenceVersionOperandRoute(query);
        if (quotedPhrases.Count > 0)
        {
            return OrderQuotedLookupMatchesForSelection(orderedMatches, quotedPhrases, prioritizeDocumentProfiles);
        }

        var prioritizeRequestedDocumentSurface =
            !string.IsNullOrWhiteSpace(query)
            && (QueryRequestsSafetyDataSheetDocumentType(query)
                || QueryRequestsHistoricalDocumentVersion(query));
        if (prioritizeRequestedDocumentSurface)
        {
            return orderedMatches
                .OrderByDescending(match => ComputeRequestedDocumentTypeTitleSignal(query!, GetDocumentTypeTitleSignalText(match)))
                .ThenByDescending(match => ComputeRequestedDocumentVersionTitleSignal(query!, match))
                .ThenByDescending(match => prioritizeDocumentProfiles && IsDocumentProfileMatch(match) ? 1 : 0)
                .ThenByDescending(match => string.IsNullOrWhiteSpace(query)
                    ? 0
                    : ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty))
                .ThenByDescending(static match => ComputeSelectionPriorityBucket(match))
                .ThenByDescending(static match => ComputeContentEvidencePriority(match))
                .ThenBy(static match => ComputeChunkExtractionQualityPenalty(match))
                .ThenByDescending(static match => match.Score)
                .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query)
            && orderedMatches.Any(match => ShouldPenalizeWeakExactMatchForBroadQuery(query, match)))
        {
            var ranked = orderedMatches
                .Select(match => new
                {
                    Match = match,
                    IsProfile = IsDocumentProfileMatch(match),
                    WeakLowInformationExactMatch = ShouldPenalizeWeakExactMatchForBroadQuery(query, match),
                    SituationalSelectionConstraintCoverage = prioritizeSituationalConstraintCoverage
                        ? ComputeSituationalSelectionConstraintCoverage(query, match)
                        : 0.0,
                    DirectSpecificContentCoverage = ComputeDirectSpecificContentCoverage(
                        directSpecificContentTokens,
                        match),
                    ShortTechnicalDirectEvidencePriority = ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty),
                    SelectionPriorityBucket = ComputeSelectionPriorityBucket(match),
                    ContentEvidencePriority = ComputeContentEvidencePriority(match)
                })
                .ToList();

            if (prioritizeDocumentProfiles)
            {
                return ranked
                    .OrderByDescending(item => ComputeDocumentProfileConcreteEvidenceSelectionBucket(item.Match, orderedMatches, query))
                    .ThenByDescending(item => ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, item.Match))
                    .ThenByDescending(static item => item.SituationalSelectionConstraintCoverage)
                    .ThenBy(static item => item.WeakLowInformationExactMatch ? 1 : 0)
                    .ThenByDescending(static item => item.ShortTechnicalDirectEvidencePriority)
                    .ThenByDescending(item => item.DirectSpecificContentCoverage >= requiredDirectSpecificContentCoverage
                        && item.ContentEvidencePriority >= 0.70
                            ? 1
                            : 0)
                    .ThenByDescending(static item => item.DirectSpecificContentCoverage)
                    .ThenByDescending(static item => item.SelectionPriorityBucket)
                    .ThenByDescending(static item => item.ContentEvidencePriority)
                    .ThenBy(static item => ComputeChunkExtractionQualityPenalty(item.Match))
                    .ThenByDescending(static item => item.Match.Score)
                    .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Match.ChunkIndex)
                    .Select(static item => item.Match)
                    .ToList();
            }

            return ranked
                .Where(static item => !item.IsProfile && !item.WeakLowInformationExactMatch)
                .OrderByDescending(static item => item.SituationalSelectionConstraintCoverage)
                .ThenByDescending(static item => item.ShortTechnicalDirectEvidencePriority)
                .ThenByDescending(item => item.DirectSpecificContentCoverage >= requiredDirectSpecificContentCoverage
                    && item.ContentEvidencePriority >= 0.70
                        ? 1
                        : 0)
                .ThenByDescending(static item => item.DirectSpecificContentCoverage)
                .ThenByDescending(static item => item.SelectionPriorityBucket)
                .ThenByDescending(static item => item.ContentEvidencePriority)
                .ThenBy(static item => ComputeChunkExtractionQualityPenalty(item.Match))
                .ThenByDescending(static item => item.Match.Score)
                .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Match.ChunkIndex)
                .Select(static item => item.Match)
                .Concat(ranked
                    .Where(static item => item.IsProfile && !item.WeakLowInformationExactMatch)
                    .OrderByDescending(item => ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, item.Match))
                    .ThenByDescending(static item => item.Match.Score)
                    .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Match.ChunkIndex)
                    .Select(static item => item.Match))
                .Concat(ranked
                    .Where(static item => item.WeakLowInformationExactMatch)
                    .OrderByDescending(static item => item.Match.Score)
                    .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Match.ChunkIndex)
                    .Select(static item => item.Match))
                .ToList();
        }

        return prioritizeDocumentProfiles
            ? orderedMatches
                .OrderByDescending(match => ComputeDocumentProfileConcreteEvidenceSelectionBucket(match, orderedMatches, query))
                .ThenByDescending(match => ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, match))
                .ThenByDescending(match => prioritizeSituationalConstraintCoverage
                    ? ComputeSituationalSelectionConstraintCoverage(query!, match)
                    : 0.0)
                .ThenByDescending(match => string.IsNullOrWhiteSpace(query)
                    ? 0
                    : ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty))
                .ThenByDescending(match => ComputeDirectSpecificContentCoverage(directSpecificContentTokens, match) >= requiredDirectSpecificContentCoverage
                    && ComputeContentEvidencePriority(match) >= 0.70
                        ? 1
                        : 0)
                .ThenByDescending(match => ComputeDirectSpecificContentCoverage(directSpecificContentTokens, match))
                .ThenByDescending(static match => ComputeSelectionPriorityBucket(match))
                .ThenByDescending(static match => ComputeContentEvidencePriority(match))
                .ThenBy(static match => ComputeChunkExtractionQualityPenalty(match))
                .ThenByDescending(static match => match.Score)
                .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex)
                .ToList()
            : orderedMatches
                .Where(static match => !IsDocumentProfileMatch(match))
                .OrderByDescending(match => prioritizeSituationalConstraintCoverage
                    ? ComputeSituationalSelectionConstraintCoverage(query!, match)
                    : 0.0)
                .ThenByDescending(match => prioritizeReferenceVersionOperandTitleSignal
                    ? ComputeReferenceVersionOperandTitleSignal(query!, GetTitleSignalText(match))
                    : 0)
                .ThenByDescending(match => string.IsNullOrWhiteSpace(query)
                    ? 0
                    : ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty))
                .ThenByDescending(match => ComputeDirectSpecificContentCoverage(directSpecificContentTokens, match) >= requiredDirectSpecificContentCoverage
                    && ComputeContentEvidencePriority(match) >= 0.70
                        ? 1
                        : 0)
                .ThenByDescending(match => ComputeDirectSpecificContentCoverage(directSpecificContentTokens, match))
                .ThenByDescending(static match => ComputeSelectionPriorityBucket(match))
                .ThenByDescending(static match => ComputeContentEvidencePriority(match))
                .ThenBy(static match => ComputeChunkExtractionQualityPenalty(match))
                .ThenByDescending(static match => match.Score)
                .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex)
                .Concat(orderedMatches
                    .Where(IsDocumentProfileMatch)
                    .OrderByDescending(match => ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, match))
                    .ThenByDescending(static match => match.Score)
                    .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex))
                .ToList();
    }


    internal static bool ShouldPrioritizeSituationalSelectionConstraintCoverage(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return ContainsSituationalBroadSelectionIntent(normalized)
            && BuildSituationalSelectionConstraintTokens(query).Count >= 2;
    }


    internal static IReadOnlyList<string> BuildSituationalSelectionConstraintTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return ExtractLexicalQueryTokens(query)
            .Where(static token => !BroadDiversityGenericSubjectTokens.Contains(token))
            .Where(static token => !BroadDiversityAbstractAssistTokens.Contains(token))
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !ComparativeSubjectStopwords.Contains(token))
            .Where(static token => !IsGenericDocumentProfileSpecificityToken(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    internal static IReadOnlyList<string> BuildDirectSpecificContentCoverageTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return ExtractLexicalQueryTokens(query)
            .Where(static token => !BroadDiversityGenericSubjectTokens.Contains(token))
            .Where(static token => !BroadDiversityAbstractAssistTokens.Contains(token))
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !ComparativeSubjectStopwords.Contains(token))
            .Where(static token => !IsGenericDocumentProfileSpecificityToken(token))
            .Where(static token => token.Length >= 5 || token.Any(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }


    private static int ComputeDirectSpecificContentCoverage(
        IReadOnlyList<string> directSpecificContentTokens,
        RagMatch match)
    {
        if (directSpecificContentTokens.Count < 2
            || IsDocumentProfileMatch(match)
            || !IsContentSelectionCandidate(match)
            || ComputeContentEvidencePriority(match) < 0.55)
        {
            return 0;
        }

        return CountSpecificLexicalAnchors(directSpecificContentTokens, GetDirectChunkSignalText(match));
    }


    internal static double ComputeSituationalSelectionConstraintCoverage(string query, RagMatch match)
    {
        var tokens = BuildSituationalSelectionConstraintTokens(query);
        if (tokens.Count == 0)
            return 0.0;

        var normalizedText = NormalizeForLexicalSignal(BuildSituationalSelectionConstraintText(match));
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0.0;

        var matchedWeight = 0.0;
        var totalWeight = 0.0;
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Count == 0)
                continue;

            var weight = token.Length >= 9 ? 1.35 : token.Length >= 7 ? 1.18 : 1.0;
            totalWeight += weight;
            if (variants.Any(variant =>
                    ContainsExactNormalizedToken(normalizedText, variant)
                    || normalizedText.Contains(variant, StringComparison.Ordinal)))
            {
                matchedWeight += weight;
            }
        }

        return totalWeight <= 0.0
            ? 0.0
            : matchedWeight / totalWeight;
    }


    private static string BuildSituationalSelectionConstraintText(RagMatch match)
    {
        var parts = new List<string?>
        {
            match.Text,
            match.EmbedText,
            match.SectionTitle,
            match.HeadingPath,
            match.DocName,
            match.DocPath
        };

        if (match.MatchedContentCards is { Count: > 0 })
        {
            foreach (var card in match.MatchedContentCards)
            {
                parts.Add(card.Title);
                if (!string.IsNullOrWhiteSpace(card.Kind))
                    parts.Add(card.Kind);
                if (card.Signals is { Count: > 0 })
                    parts.Add(string.Join(' ', card.Signals));
            }
        }

        if (match.ProfileSignals is not null)
        {
            parts.Add(match.ProfileSignals.ProfileVersion);
            parts.Add(match.ProfileSignals.Language);
            AddSignalParts(parts, match.ProfileSignals.Keywords);
            AddSignalParts(parts, match.ProfileSignals.Entities);
            AddSignalParts(parts, match.ProfileSignals.Topics);
            AddSignalParts(parts, match.ProfileSignals.HypotheticalQuestions);
            AddSignalParts(parts, match.ProfileSignals.Limits);
            AddSignalParts(parts, match.ProfileSignals.MatchedTerms);
        }

        return string.Join(' ', parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }


    private static void AddSignalParts(List<string?> parts, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
            parts.Add(string.Join(' ', values));
    }


    private static int ComputeDocumentProfileSelectionBucket(RagMatch match, string? query)
    {
        if (!IsDocumentProfileMatch(match))
            return 2;
        if (string.IsNullOrWhiteSpace(query))
            return 3;
        if (HasMeaningfulDocumentProfileSelectionSupport(match, query))
            return 3;

        return 1;
    }


    private static int ComputeDocumentProfileConcreteEvidenceSelectionBucket(
        RagMatch match,
        IReadOnlyList<RagMatch> peerMatches,
        string? query)
    {
        if (IsProfileCardConcreteEvidenceCompanion(match, peerMatches, query))
            return 4;

        return ComputeDocumentProfileSelectionBucket(match, query);
    }


    private static bool IsProfileCardConcreteEvidenceCompanion(
        RagMatch match,
        IReadOnlyList<RagMatch> peerMatches,
        string? query)
    {
        if (IsDocumentProfileMatch(match)
            || !IsContentSelectionCandidate(match)
            || LooksLikeNavigationalChunk(match)
            || LooksLikeGlossaryChunk(match)
            || string.IsNullOrWhiteSpace(match.DocPath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query)
            && ShouldPreferComparativeDocumentDiversity(query)
            && ComputeContentEvidencePriority(match) < 0.55
            && GetStructuredAnswerPriority(match) <= 0)
        {
            return false;
        }

        var docKey = NormalizeRagDocPath(match.DocPath);
        if (string.IsNullOrWhiteSpace(docKey))
            return false;

        foreach (var profile in peerMatches)
        {
            if (!IsDocumentProfileMatch(profile)
                || profile.MatchedContentCards is not { Count: > 0 }
                || !string.Equals(docKey, NormalizeRagDocPath(profile.DocPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query)
                && !HasMeaningfulDocumentProfileSelectionSupport(profile, query))
            {
                continue;
            }

            foreach (var card in profile.MatchedContentCards)
            {
                if (card.PageStart is > 0
                    && PageRangesOverlap(match.PageStart, match.PageEnd, card.PageStart, card.PageEnd ?? card.PageStart))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(card.Title)
                    && ContainsOrderedPhraseWindow(
                        GetDirectChunkSignalText(match),
                        ExactMatchEntryExtractor.NormalizeForLookup(card.Title),
                        maxGapChars: 35))
                {
                    return true;
                }
            }
        }

        return false;
    }


    private static bool HasMeaningfulDocumentProfileSelectionSupport(RagMatch match, string query)
    {
        if (!IsDocumentProfileMatch(match))
            return false;
        if (match.MatchedContentCards is { Count: > 0 })
            return true;

        var tokens = BuildDocumentProfileSpecificityTokens(query);
        return ComputeDocumentProfileSelectionConstraintCoverage(tokens, match) > 0.0;
    }


    private static IReadOnlyList<RagMatch> OrderQuotedLookupMatchesForSelection(
        IReadOnlyList<RagMatch> matches,
        IReadOnlyList<string> quotedPhrases,
        bool prioritizeDocumentProfiles)
    {
        var ranked = matches
            .Select(match =>
            {
                var localQuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedPhrases,
                    GetQuotedLookupSelectionSignalText(match, includeSyntheticRouteTitle: false));
                var contentEvidencePriority = ComputeContentEvidencePriority(match);
                return new
                {
                    Match = match,
                    IsProfile = IsDocumentProfileMatch(match),
                    IsResolvedRoute = IsResolvedTitleOrNavigationRoute(match),
                    IsWeakResolvedRoute = IsWeakResolvedRouteTarget(match),
                    MatchesQuotedDocumentName = ComputeQuotedLookupCandidateScore(
                        quotedPhrases,
                        string.Join(' ', new[] { match.DocName, match.DocPath }
                            .Where(static value => !string.IsNullOrWhiteSpace(value)))) > 0.0,
                    IsExplicitDocumentTitleRoute = IsExplicitDocumentTitleRouteMatch(match),
                    HasTitlePlacementEvidence = HasQuotedTitlePlacementEvidence(quotedPhrases, match),
                    ExactQuotedTitlePriority = ComputeExactQuotedTitlePlacementPriority(quotedPhrases, match),
                    DirectQuotedTitleTokenCoverage = ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match),
                    LocalQuotedScore = localQuotedScore,
                    LocalQuotedRankingScore = ComputeQuotedLookupSelectionRankingScore(
                        match,
                        localQuotedScore,
                        contentEvidencePriority),
                    ContentEvidencePriority = contentEvidencePriority
                };
            })
            .ToList();

        if (prioritizeDocumentProfiles)
        {
            return ranked
                .OrderByDescending(static item => item.IsProfile)
                .ThenByDescending(static item => item.MatchesQuotedDocumentName)
                .ThenByDescending(static item => item.IsExplicitDocumentTitleRoute)
                .ThenByDescending(static item => item.HasTitlePlacementEvidence)
                .ThenByDescending(static item => item.ExactQuotedTitlePriority)
                .ThenByDescending(static item => item.DirectQuotedTitleTokenCoverage)
                .ThenByDescending(static item => item.IsWeakResolvedRoute ? 0 : 1)
                .ThenByDescending(static item => item.LocalQuotedRankingScore > 0.0)
                .ThenByDescending(static item => item.LocalQuotedRankingScore)
                .ThenByDescending(static item => item.LocalQuotedScore)
                .ThenByDescending(static item => item.IsResolvedRoute)
                .ThenByDescending(static item => item.ContentEvidencePriority)
                .ThenByDescending(static item => item.Match.Score)
                .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Match.ChunkIndex)
                .Select(static item => item.Match)
                .ToList();
        }

        return ranked
            .Where(static item => !item.IsProfile)
            .OrderByDescending(static item => item.MatchesQuotedDocumentName)
            .ThenByDescending(static item => item.IsExplicitDocumentTitleRoute)
            .ThenByDescending(static item => item.HasTitlePlacementEvidence)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenByDescending(static item => item.DirectQuotedTitleTokenCoverage)
            .ThenByDescending(static item => item.IsWeakResolvedRoute ? 0 : 1)
            .ThenByDescending(static item => item.LocalQuotedRankingScore > 0.0)
            .ThenByDescending(static item => item.LocalQuotedRankingScore)
            .ThenByDescending(static item => item.LocalQuotedScore)
            .ThenByDescending(static item => item.IsResolvedRoute)
            .ThenByDescending(static item => item.ContentEvidencePriority)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .Concat(ranked
                .Where(static item => item.IsProfile)
                .OrderByDescending(static item => item.MatchesQuotedDocumentName)
                .ThenByDescending(static item => item.HasTitlePlacementEvidence)
                .ThenByDescending(static item => item.ExactQuotedTitlePriority)
                .ThenByDescending(static item => item.DirectQuotedTitleTokenCoverage)
                .ThenByDescending(static item => item.IsWeakResolvedRoute ? 0 : 1)
                .ThenByDescending(static item => item.LocalQuotedRankingScore > 0.0)
                .ThenByDescending(static item => item.LocalQuotedRankingScore)
                .ThenByDescending(static item => item.LocalQuotedScore)
                .ThenByDescending(static item => item.Match.Score)
                .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Match.ChunkIndex)
                .Select(static item => item.Match))
            .ToList();
    }


    internal static bool ShouldPrioritizeDocumentProfilesForSelection(
        bool preferDocumentDiversity,
        bool allowSparseAssistForScopedProfileFallback,
        bool allowComparativeDocumentProfileAssist = false)
        => preferDocumentDiversity
           && (!allowSparseAssistForScopedProfileFallback || allowComparativeDocumentProfileAssist);


    internal static bool ShouldDeferDocumentProfileSearch(
        bool canSearchDocumentProfiles,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback,
        bool allowSparseAssistForScopedProfileFallback)
        => canSearchDocumentProfiles
           && !skipChunkRetrieversForDocumentOverview
           && useScopedProfileFallback
           && allowSparseAssistForScopedProfileFallback;


    internal static bool ShouldRunDeferredDocumentProfileSearch(
        IReadOnlyList<RagMatch> selected,
        int topK,
        bool preferDocumentDiversity,
        bool useScopedProfileFallback,
        bool allowSparseAssistForScopedProfileFallback)
    {
        if (!useScopedProfileFallback || !allowSparseAssistForScopedProfileFallback)
            return false;
        if (selected.Count == 0)
            return true;
        if (selected.Count < Math.Min(topK, 4))
            return true;
        if (!preferDocumentDiversity)
            return false;

        var distinctDocuments = selected
            .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
            .Select(static match => match.DocPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return distinctDocuments < Math.Min(selected.Count, 3);
    }


    internal static IReadOnlyList<RagMatch> RankDeferredDocumentProfileMatches(IEnumerable<RagMatch> matches)
        => matches
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .ToArray();


    private static string? ResolveLinkedContextAnchorTitle(RagMatch match)
    {
        var retriever = ResolveRetriever(match);
        var isTrustedTitleAnchor =
            string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            || string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
            || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal)
            || string.Equals(retriever, "local_title_token_route", StringComparison.Ordinal);
        if (isTrustedTitleAnchor && HasProfileTitleHint(match))
        {
            var title = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
            if (!string.IsNullOrWhiteSpace(title))
                return title.Trim();
        }

        if (match.MatchedContentCards is { Count: > 0 })
        {
            var card = match.MatchedContentCards
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Title))
                .OrderByDescending(static candidate => HasStrongMatchedContentCardSupport(candidate))
                .FirstOrDefault();
            if (card is not null && HasStrongMatchedContentCardSupport(card))
                return card.Title.Trim();
        }

        return null;
    }


    private static string BuildLinkedContextEmbedText(string? text, string? anchorTitle)
    {
        if (string.IsNullOrWhiteSpace(anchorTitle))
            return text ?? string.Empty;

        return $"Matched linked_anchor_title: {anchorTitle.Trim()}\n{text ?? string.Empty}";
    }


    internal static bool IsDocumentProfileMatch(RagMatch match)
        => string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal)
           || string.Equals(ResolveRetriever(match), "document_profile", StringComparison.Ordinal);


    internal static int ComputeSelectionPriorityBucket(RagMatch match)
    {
        if (IsResolvedTitleOrNavigationRoute(match))
            return IsWeakResolvedRouteTarget(match) ? 2 : 4;

        if (!LooksLikeNavigationalChunk(match))
            return ComputeContentEvidencePriority(match) >= 0.80 ? 3 : 1;

        return 0;
    }


    private static bool CountsAgainstChunkQuota(RagMatch match)
        => !IsDocumentProfileMatch(match);


    private static string? BuildSectionKey(RagMatch match)
    {
        var docKey = ResolveMatchDedupDocumentKey(match);
        // Use SectionOrdinal if available, fallback to SectionTitle
        if (match.SectionOrdinal.HasValue)
            return $"{docKey}:sec:{match.SectionOrdinal.Value}";
        if (!string.IsNullOrWhiteSpace(match.SectionTitle))
            return $"{docKey}:sec:{match.SectionTitle}";
        return null;
    }


    internal static string BuildRagMatchIdentityKey(RagMatch match)
        => !string.IsNullOrWhiteSpace(match.ChunkId)
            ? $"chunk|{match.DocPath}|{match.ChunkId}"
            : $"range|{match.DocPath}|{match.PageStart}|{match.PageEnd}|{match.OffsetStart}|{match.OffsetEnd}";


    internal static async Task<List<RagMatch>> SearchLinkedMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> anchorMatches,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        bool includeSamePageForLinkedAnchors = false,
        int? maxAnchorCandidates = null)
    {
        if (topK <= 0)
            return [];

        var chunkAnchors = anchorMatches
            .Where(match =>
            {
                var retriever = ResolveRetriever(match);
                return (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                        || string.Equals(retriever, "linked_context", StringComparison.Ordinal)
                        || string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal)
                        || string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "local_title_token_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal))
                    && Guid.TryParse(match.ChunkId, out _);
            })
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: ResolveRetriever(match),
                AnchorScore: match.Score,
                AnchorTitle: ResolveLinkedContextAnchorTitle(match)))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var exactAnchors = anchorMatches
            .Where(match =>
                string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal)
                && Guid.TryParse(match.ChunkId, out _))
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: "exact_match",
                AnchorScore: match.Score,
                AnchorTitle: null))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var anchorCandidates = chunkAnchors
            .Concat(exactAnchors)
            .OrderByDescending(item => item.AnchorScore)
            .Take(Math.Max(1, Math.Min(topK * 2, Math.Clamp(maxAnchorCandidates.GetValueOrDefault(4), 1, 32))))
            .ToList();

        if (anchorCandidates.Count == 0)
            return [];

        var anchorsBySourceId = anchorCandidates.ToDictionary(item => item.SourceId);
        var denseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "dense_qdrant", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var linkedAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var sparseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "sparse_bm25", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var routeAnchorIds = anchorCandidates
            .Where(item =>
                string.Equals(item.SourceRetriever, "title_anchor_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "navigation_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "local_title_token_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "direct_title_token_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "fuzzy_title_lead", StringComparison.Ordinal)
                || (includeSamePageForLinkedAnchors
                    && string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal)))
            .Select(item => item.SourceId)
            .ToArray();
        var exactAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "exact_match", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH requested_anchors AS (
    SELECT source_anchor_id, anchor_chunk_id, anchor_retriever
    FROM (
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'dense_qdrant'::text AS anchor_retriever
        FROM unnest(@dense_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'linked_context'::text AS anchor_retriever
        FROM unnest(@linked_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'sparse_bm25'::text AS anchor_retriever
        FROM unnest(@sparse_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'route_context'::text AS anchor_retriever
        FROM unnest(@route_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT
            exact_anchors.exact_match_entry_id AS source_anchor_id,
            resolved_chunks.retrieval_chunk_id AS anchor_chunk_id,
            'exact_match'::text AS anchor_retriever
        FROM unnest(@exact_anchor_entry_ids::uuid[]) AS exact_anchor_id
        JOIN exact_match_entries exact_anchors
          ON exact_anchors.tenant_id = @tenant_id
         AND exact_anchors.exact_match_entry_id = exact_anchor_id
        LEFT JOIN LATERAL (
            SELECT rc.retrieval_chunk_id
            FROM retrieval_chunks rc
            WHERE rc.revision_id = exact_anchors.revision_id
              AND (
                    (exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id)
                 OR (exact_anchors.unit_id IS NULL
                     AND exact_anchors.section_id IS NOT NULL
                     AND rc.section_id = exact_anchors.section_id
                     AND rc.page_start <= exact_anchors.page_end
                     AND rc.page_end >= exact_anchors.page_start)
              )
            ORDER BY
                CASE
                    WHEN exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id THEN 0
                    ELSE 1
                END,
                rc.chunk_index ASC
            LIMIT 1
        ) AS resolved_chunks ON TRUE
    ) anchors
),
linked_candidates AS (
    SELECT DISTINCT ON (source_anchor_id, linked_chunk_id)
        source_anchor_id,
        anchor_chunk_id,
        anchor_retriever,
        linked_chunk_id,
        link_type
    FROM (
        SELECT
            a.source_anchor_id,
            l.retrieval_chunk_id AS anchor_chunk_id,
            a.anchor_retriever,
            l.linked_chunk_id,
            l.link_type
        FROM requested_anchors a
        JOIN retrieval_chunk_links l
          ON l.tenant_id = @tenant_id
         AND l.retrieval_chunk_id = a.anchor_chunk_id
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            (anchor_rc.metadata->>'sameSectionChunkId')::uuid AS linked_chunk_id,
            'same_section'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        WHERE NULLIF(anchor_rc.metadata->>'sameSectionChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            rc.retrieval_chunk_id AS linked_chunk_id,
            'same_section'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        JOIN retrieval_chunks rc
          ON rc.tenant_id = @tenant_id
         AND rc.revision_id = anchor_rc.revision_id
         AND rc.retrieval_chunk_id <> anchor_rc.retrieval_chunk_id
         AND rc.page_start <= anchor_rc.page_end + 2
         AND rc.page_end >= anchor_rc.page_start - 2
         AND NULLIF(rc.metadata->>'sameSectionChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
         AND (rc.metadata->>'sameSectionChunkId')::uuid = anchor_rc.retrieval_chunk_id
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            (anchor_rc.metadata->>'nextChunkId')::uuid AS linked_chunk_id,
            'next'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        WHERE NULLIF(anchor_rc.metadata->>'nextChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            rc.retrieval_chunk_id AS linked_chunk_id,
            'next'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        JOIN retrieval_chunks rc
          ON rc.tenant_id = @tenant_id
         AND rc.revision_id = anchor_rc.revision_id
         AND rc.retrieval_chunk_id <> anchor_rc.retrieval_chunk_id
         AND rc.page_start <= anchor_rc.page_end + 2
         AND rc.page_end >= anchor_rc.page_start - 2
         AND NULLIF(rc.metadata->>'prevChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
         AND (rc.metadata->>'prevChunkId')::uuid = anchor_rc.retrieval_chunk_id
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            (anchor_rc.metadata->>'prevChunkId')::uuid AS linked_chunk_id,
            'prev'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        WHERE NULLIF(anchor_rc.metadata->>'prevChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            rc.retrieval_chunk_id AS linked_chunk_id,
            'prev'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        JOIN retrieval_chunks rc
          ON rc.tenant_id = @tenant_id
         AND rc.revision_id = anchor_rc.revision_id
         AND rc.retrieval_chunk_id <> anchor_rc.retrieval_chunk_id
         AND rc.page_start <= anchor_rc.page_end + 2
         AND rc.page_end >= anchor_rc.page_start - 2
         AND NULLIF(rc.metadata->>'nextChunkId', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
         AND (rc.metadata->>'nextChunkId')::uuid = anchor_rc.retrieval_chunk_id
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            rc.retrieval_chunk_id AS linked_chunk_id,
            'same_page_route'::text AS link_type
        FROM requested_anchors a
        JOIN retrieval_chunks anchor_rc
          ON anchor_rc.tenant_id = @tenant_id
         AND anchor_rc.retrieval_chunk_id = a.anchor_chunk_id
        JOIN retrieval_chunks rc
          ON rc.tenant_id = @tenant_id
         AND rc.revision_id = anchor_rc.revision_id
         AND rc.page_start <= anchor_rc.page_end
         AND rc.page_end >= anchor_rc.page_start
         AND rc.page_start >= anchor_rc.page_start
         AND rc.page_start <= anchor_rc.page_end
        WHERE a.anchor_retriever = 'route_context'
        UNION ALL
        SELECT
            a.source_anchor_id,
            a.anchor_chunk_id,
            a.anchor_retriever,
            rc.retrieval_chunk_id AS linked_chunk_id,
            'same_page_exact'::text AS link_type
        FROM requested_anchors a
        JOIN exact_match_entries exact_anchors
          ON exact_anchors.tenant_id = @tenant_id
         AND exact_anchors.exact_match_entry_id = a.source_anchor_id
        JOIN retrieval_chunks rc
          ON rc.tenant_id = @tenant_id
         AND rc.revision_id = exact_anchors.revision_id
         AND rc.page_start <= exact_anchors.page_end
         AND rc.page_end >= exact_anchors.page_start
         AND rc.page_start >= exact_anchors.page_start
         AND rc.page_start <= exact_anchors.page_end
        WHERE a.anchor_retriever = 'exact_match'
    ) candidates
    ORDER BY
        source_anchor_id,
        linked_chunk_id,
        CASE link_type
            WHEN 'same_section' THEN 0
            WHEN 'next' THEN 1
            WHEN 'prev' THEN 2
            WHEN 'same_page_route' THEN 3
            WHEN 'same_page_exact' THEN 3
            ELSE 4
        END
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN l.link_type IN ('same_page_exact', 'same_page_route') THEN 'linked_context_v1'
        ELSE COALESCE(rc.metadata->>'chunkType', 'linked_context_v1')
    END AS "ChunkType",
    CASE
        WHEN l.link_type IN ('same_page_exact', 'same_page_route') THEN 'content'
        ELSE COALESCE(rc.metadata->>'contentRole', 'content')
    END AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    l.link_type AS "LinkType",
    a.source_anchor_id AS "AnchorSourceId",
    COALESCE(l.anchor_chunk_id, l.linked_chunk_id) AS "AnchorChunkId",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson"
FROM requested_anchors a
JOIN linked_candidates l
  ON l.source_anchor_id = a.source_anchor_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = l.linked_chunk_id
JOIN document_revisions r
  ON r.revision_id = rc.revision_id
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.status = 'indexed'
 AND d.indexed_version = r.indexed_version
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE l.link_type
        WHEN 'same_section' THEN 0
        WHEN 'next' THEN 1
        WHEN 'same_page_route' THEN 2
        ELSE 2
    END,
    CASE
        WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'content' THEN 0
        WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'mixed_navigation_content' THEN 1
        ELSE 2
    END,
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE 0.0
    END DESC,
    rc.chunk_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<LinkedMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            dense_anchor_chunk_ids = denseAnchorIds,
            linked_anchor_chunk_ids = linkedAnchorIds,
            sparse_anchor_chunk_ids = sparseAnchorIds,
            route_anchor_chunk_ids = routeAnchorIds,
            exact_anchor_entry_ids = exactAnchorIds,
            category,
            category_path = normalizedCategoryPath,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK * 3
        }, cancellationToken: ct));

        var linkedMatches = rows.Select(row =>
        {
            anchorsBySourceId.TryGetValue(row.AnchorSourceId, out var anchor);
            return new RagMatch(
                Score: ComputeLinkedMatchScore(anchor?.AnchorScore ?? 0.0, row.LinkType, anchor?.SourceRetriever),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                Category: row.Category,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                OffsetStart: row.OffsetStart,
                OffsetEnd: row.OffsetEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: BuildLinkedContextEmbedText(row.Text, anchor?.AnchorTitle),
                EmbeddingBasis: "linked_context_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                ContentRole: row.ContentRole,
                NavigationReason: row.NavigationReason,
                OriginalChunkType: row.OriginalChunkType,
                NavigationScore: row.NavigationScore,
                ContentDensityScore: row.ContentDensityScore,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId,
                ExtractionTextStatus: row.ExtractionTextStatus,
                ExtractionTextSparse: row.ExtractionTextSparse,
                ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson));
        }).ToList();

        var cardQuery = string.Join(
            ' ',
            anchorMatches
                .OrderByDescending(static match => match.Score)
                .Take(6)
                .Select(static match => $"{match.SectionTitle} {match.HeadingPath} {match.Text}"));
        return await AttachDocumentProfileContentCardsAsync(ds, tenantId, linkedMatches, cardQuery, ct);
    }


    /// <summary>
    /// CDC v3.1 §11.3: autocut - detect the largest relative score drop between consecutive
    /// results and trim everything after the gap, provided the absolute threshold is also met.
    /// Lists with two or fewer items are unchanged. For larger lists, a significant
    /// gap (at least 15%) may cut the tail after at least eight retained items;
    /// the absolute floor then removes non-exact hits and can leave no result.
    /// </summary>
    internal static void ApplyAutocut(List<RagMatch> matches, double absoluteMinScore, string? query = null)
    {
        if (matches.Count <= 2)
            return;

        var bestGapIndex = -1;
        var bestGapRatio = 0.0;
        const double minRelativeDrop = 0.15;
        const int minKeepBeforeAutocut = 8;

        for (var i = 1; i < matches.Count; i++)
        {
            var prev = matches[i - 1].Score;
            var curr = matches[i].Score;
            if (prev <= 0.0)
                continue;

            var drop = (prev - curr) / prev;
            if (drop > bestGapRatio)
            {
                bestGapRatio = drop;
                bestGapIndex = i;
            }
        }

        if (bestGapRatio >= minRelativeDrop
            && bestGapIndex >= minKeepBeforeAutocut
            && !WouldAutocutRemoveAllActionableComparativeEvidence(matches, bestGapIndex, query))
        {
            matches.RemoveRange(bestGapIndex, matches.Count - bestGapIndex);
        }

        // Also enforce absolute minimum on remaining items (skip exact_match which always passes)
        matches.RemoveAll(m =>
            m.Score < absoluteMinScore
            && !string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal));
    }


    private static bool WouldAutocutRemoveAllActionableComparativeEvidence(
        IReadOnlyList<RagMatch> matches,
        int cutIndex,
        string? query)
    {
        if (cutIndex <= 0
            || cutIndex >= matches.Count
            || string.IsNullOrWhiteSpace(query)
            || !ShouldPreferComparativeDocumentDiversity(query)
            || !ShouldAllowDocumentProfileAssistForComparativeLookup(query))
        {
            return false;
        }

        var kept = matches.Take(cutIndex).ToArray();
        if (kept.Any(static match => !IsDocumentProfileMatch(match)))
            return false;

        return matches
            .Skip(cutIndex)
            .Any(static match => !IsDocumentProfileMatch(match) && IsContentSelectionCandidate(match));
    }


    internal static string BuildMatchDedupKey(RagMatch match)
    {
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty);
        var textKey = normalizedText.Length <= 512
            ? normalizedText
            : $"{normalizedText.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)))}";
        var documentKey = ResolveMatchDedupDocumentKey(match);
        return $"{documentKey}|{match.PageStart}|{match.PageEnd}|{textKey}";
    }


    internal static bool IsNearDuplicatePageOverlap(RagMatch left, RagMatch right)
    {
        if (left is null || right is null)
            return false;
        if (string.IsNullOrWhiteSpace(left.DocPath) || string.IsNullOrWhiteSpace(right.DocPath))
            return false;
        if (!IsSameDedupDocumentFingerprint(left, right))
            return false;
        if (!PageRangesOverlap(left.PageStart, left.PageEnd, right.PageStart, right.PageEnd))
            return false;

        var leftText = ExactMatchEntryExtractor.NormalizeForLookup(left.Text ?? string.Empty);
        var rightText = ExactMatchEntryExtractor.NormalizeForLookup(right.Text ?? string.Empty);
        if (leftText.Length < 120 || rightText.Length < 120)
            return false;

        if (leftText.Contains(rightText, StringComparison.Ordinal) || rightText.Contains(leftText, StringComparison.Ordinal))
            return true;

        return ComputeTokenJaccard(leftText, rightText) >= 0.82;
    }


    internal static bool PageRangesOverlap(int? leftStart, int? leftEnd, int? rightStart, int? rightEnd)
    {
        if (leftStart is not > 0 || rightStart is not > 0)
            return false;

        var lEnd = Math.Max(leftStart.Value, leftEnd ?? leftStart.Value);
        var rEnd = Math.Max(rightStart.Value, rightEnd ?? rightStart.Value);
        return leftStart.Value <= rEnd && rightStart.Value <= lEnd;
    }


    private static double ComputeTokenJaccard(string leftText, string rightText)
    {
        var leftTokens = leftText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = rightText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0.0;

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union <= 0 ? 0.0 : (double)intersection / union;
    }


    internal static List<RagMatch> FuseWithRrf(
        IReadOnlyList<RagMatch> exactMatches,
        IReadOnlyList<RagMatch> sparseMatches,
        IReadOnlyList<RagMatch> denseMatches,
        IReadOnlyList<RagMatch>? profileMatches = null,
        IReadOnlyList<RagMatch>? titleAnchorRouteMatches = null,
        int rrfK = 60)
    {
        var accumulators = new Dictionary<string, RrfAccumulator>(StringComparer.OrdinalIgnoreCase);

        AccumulateRrf(accumulators, exactMatches, rrfK);
        if (titleAnchorRouteMatches is { Count: > 0 })
            AccumulateRrf(accumulators, titleAnchorRouteMatches, rrfK);
        AccumulateRrf(accumulators, sparseMatches, rrfK);
        AccumulateRrf(accumulators, denseMatches, rrfK);
        if (profileMatches is { Count: > 0 })
            AccumulateRrf(accumulators, profileMatches, rrfK);

        if (accumulators.Count == 0)
            return [];

        var maxRrf = accumulators.Values.Max(item => item.Score);
        return accumulators
            .Values
            .Select(item =>
            {
                var normalizedRrf = maxRrf > 0 ? item.Score / maxRrf : 0.0;
                var baseScore = Math.Clamp(item.Representative.Score, 0.0, 1.02);
                var blendedScore = Math.Min(1.02, (normalizedRrf * 0.65) + (baseScore * 0.35));
                return item.Representative with { Score = blendedScore };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ChunkIndex)
            .ToList();
    }


    private sealed record CalibrationCandidate(
        RagMatch Match,
        string Retriever,
        string TextForSignals,
        string TitleSignalText,
        string NormalizedTextForSignals,
        string NormalizedTitleSignalText,
        string NormalizedMatchText);


    internal static bool QueryRequestsSafetyDataSheetDocumentType(string? query)
    {
        var normalized = $" {NormalizeForLexicalSignal(query ?? string.Empty)} ";
        return ContainsAny(
            normalized,
            " msds ",
            " sds ",
            " fds ",
            " safety data sheet ",
            " safety data sheets ",
            " material safety data sheet ",
            " material safety data sheets ",
            " fiche de donnees de securite ",
            " fiches de donnees de securite ",
            " fiche donnees securite ",
            " fiches donnees securite ",
            " fiche de securite ",
            " fiches de securite ");
    }


    internal static string ExpandGenericTechnicalRetrievalQuery(string originalQuery, string retrievalQuery)
    {
        var baseQuery = CollapseWhitespace(string.IsNullOrWhiteSpace(retrievalQuery) ? originalQuery : retrievalQuery);
        if (string.IsNullOrWhiteSpace(baseQuery))
            return baseQuery;

        var normalized = $" {NormalizeForLexicalSignal($"{originalQuery} {retrievalQuery}")} ";
        var terms = new List<string> { baseQuery };

        void Add(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var normalizedValue = NormalizeForLexicalSignal(value);
            if (string.IsNullOrWhiteSpace(normalizedValue)
                || terms.Any(term => string.Equals(NormalizeForLexicalSignal(term), normalizedValue, StringComparison.Ordinal)))
            {
                return;
            }

            terms.Add(value);
        }

        if (QueryRequestsSafetyDataSheetDocumentType(normalized))
        {
            Add("msds");
            Add("sds");
            Add("fds");
            Add("safety data sheet");
            Add("material safety data sheet");
            Add("fiche de donnees de securite");
            Add("fiche de securite");
        }

        if (ContainsAny(
                normalized,
                " ancienne ",
                " anciennes ",
                " ancien ",
                " anciens ",
                " precedente ",
                " precedent ",
                " anterieure ",
                " anterieur ",
                " old ",
                " older ",
                " previous ",
                " prior ",
                " archived ",
                " archive ",
                " archives ",
                " historical ",
                " legacy ",
                " obsolete "))
        {
            Add("old version");
            Add("old versions");
            Add("previous version");
            Add("archived version");
            Add("ancienne version");
            Add("version precedente");
        }

        if (ContainsAny(
                normalized,
                " actuelle ",
                " actuel ",
                " courante ",
                " courant ",
                " derniere ",
                " nouvelle ",
                " nouveau ",
                " current ",
                " latest ",
                " newest ",
                " recent "))
        {
            Add("current version");
            Add("latest version");
            Add("version courante");
            Add("derniere version");
        }

        return CollapseWhitespace(string.Join(' ', terms));
    }


    internal static bool TitleSignalMatchesSafetyDataSheetDocumentType(string? titleSignalText)
    {
        var normalized = $" {NormalizeForLexicalSignal(titleSignalText ?? string.Empty)} ";
        return ContainsAny(
            normalized,
            " msds ",
            " sds ",
            " fds ",
            " safety data sheet ",
            " safety data sheets ",
            " material safety data sheet ",
            " material safety data sheets ",
            " fiche de donnees de securite ",
            " fiches de donnees de securite ",
            " fiche donnees securite ",
            " fiches donnees securite ",
            " fiche de securite ",
            " fiches de securite ");
    }


    private static int ComputeRequestedDocumentTypeTitleSignal(string query, string? titleSignalText)
    {
        if (!QueryRequestsSafetyDataSheetDocumentType(query))
            return 0;

        if (!TitleSignalMatchesSafetyDataSheetDocumentType(titleSignalText))
            return 0;

        var normalized = $" {NormalizeForLexicalSignal(titleSignalText ?? string.Empty)} ";
        if (ContainsAny(normalized, " msds ", " sds ", " fds "))
            return 3;
        if (ContainsAny(normalized, " material safety data sheet ", " safety data sheet "))
            return 3;
        return 2;
    }


    private static bool QueryRequestsHistoricalDocumentVersion(string? query)
    {
        var normalized = $" {NormalizeForLexicalSignal(query ?? string.Empty)} ";
        return ContainsAny(
            normalized,
            " ancienne ",
            " anciennes ",
            " ancien ",
            " anciens ",
            " precedente ",
            " precedent ",
            " anterieure ",
            " anterieur ",
            " old ",
            " older ",
            " previous ",
            " prior ",
            " archived ",
            " archive ",
            " archives ",
            " historical ",
            " legacy ",
            " obsolete ");
    }


    private static int ComputeRequestedDocumentVersionTitleSignal(string query, RagMatch match)
    {
        if (!QueryRequestsHistoricalDocumentVersion(query))
            return 0;

        var normalized = $" {NormalizeForLexicalSignal(GetDocumentTypeTitleSignalText(match))} ";
        if (ContainsAny(
                normalized,
                " old versions ",
                " old version ",
                " older ",
                " previous ",
                " prior ",
                " archived ",
                " archive ",
                " archives ",
                " historical ",
                " legacy ",
                " obsolete ",
                " anciennes ",
                " ancienne ",
                " anciens ",
                " ancien ",
                " precedente ",
                " precedent ",
                " anterieure ",
                " anterieur "))
        {
            return 3;
        }

        return 0;
    }


    internal static string GetDocumentTypeTitleSignalText(RagMatch match)
        => string.Join('\n', new[]
        {
            match.DocName,
            match.DocPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));


    internal static List<RagMatch> CalibrateFusedMatches(string query, IReadOnlyList<RagMatch> candidates, string? originalQuery = null)
    {
        if (candidates.Count <= 1)
            return candidates.ToList();

        query = RepairLikelyMojibakeText(query);
        var rankingQuery = RepairLikelyMojibakeText(originalQuery ?? query);
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var referenceTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query)
            .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lexicalTokens = ExtractLexicalQueryTokens(query);
        var profileConstraintTokens = BuildDocumentProfileSpecificityTokens(rankingQuery);
        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Concat(BuildSupplementalSparseLexicalFallbackTerms(rankingQuery))
            .Where(static term => term.Contains(' '))
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
        var quotedPhrases = ExtractQuotedLookupPhrases(rankingQuery);
        var structuredTitleLookup = IsStructuredTitleLookupQuery(rankingQuery);
        var documentHintTokens = ExtractDocumentHintTokens(rankingQuery);
        var hasExplicitFileDocumentHint = HasExplicitDocumentNameHint(rankingQuery);
        var comparativeSubjectTokens = ExtractComparativeSubjectAnchorTokens(rankingQuery);
        var focusedHeadAnchorTokens = ExtractFocusedLookupPrimaryHeadTokens(rankingQuery);
        var titleScoringQuery = string.IsNullOrWhiteSpace(query) ? rankingQuery : query;
        var titleTokens = ExtractTitleScoringTokens(titleScoringQuery);
        var unrequestedQualifiedTitleSubjectTokens =
            BuildUnrequestedQualifiedTitleSubjectTokens(rankingQuery, titleTokens);
        var applyUnrequestedQualifiedTitlePenalty =
            ShouldPreferComparativeDocumentDiversity(rankingQuery)
            && unrequestedQualifiedTitleSubjectTokens.Count is >= 1 and <= 3;
        var normalizedTitleScoringQuery = NormalizeForLexicalSignal(titleScoringQuery);
        var titleScoringEnabled = !(quotedPhrases.Count == 0 && ContainsExactTitleActionMarker(rankingQuery));
        var hasReferenceLikeQueryToken = HasReferenceLikeQueryToken(rankingQuery);
        var prioritizeFastDuration = ContainsFastDurationPreference(rankingQuery);
        var prioritizeComparativeProfileCoverage =
            ShouldPreferComparativeDocumentDiversity(rankingQuery)
            && ShouldAllowDocumentProfileAssistForComparativeLookup(rankingQuery);
        var prioritizeComparativeOperandTitleSignal = ShouldUseBareVsComparativeOperandRoute(rankingQuery);
        var prioritizeReferenceVersionOperandTitleSignal = ShouldUseReferenceVersionOperandRoute(rankingQuery);
        var preserveCatalogOverviewProfiles = ShouldUseCatalogDocumentOverviewFallback(rankingQuery);
        var requestedSafetyDataSheetDocumentType = QueryRequestsSafetyDataSheetDocumentType(rankingQuery);
        var prioritizeContentEvidenceBeforeFocusedHead =
            ShouldConstrainPreciseTitleLookup(rankingQuery)
            || ShouldPreferComparativeDocumentDiversity(rankingQuery);
        var prioritizeExactTitleBeforeScore =
            prioritizeContentEvidenceBeforeFocusedHead
            || quotedPhrases.Count > 0
            || hasReferenceLikeQueryToken
            || referenceTerms.Length > 0;
        var prioritizeLexicalCoverageBeforeRawScore =
            ContainsDocumentOverviewIntent(rankingQuery)
            || ContainsSummarizationIntent(rankingQuery);
        var prioritizePrimarySpecificAnchorBeforeRawScore =
            !prioritizeExactTitleBeforeScore
            && !ContainsDocumentOverviewIntent(rankingQuery)
            && lexicalTokens.Count >= 3
            && RequiresPrimarySpecificLexicalAnchor(lexicalTokens);
        var prioritizeTitleSignalsBeforeRawScore = titleScoringEnabled || prioritizeExactTitleBeforeScore;
        var calibrationCandidates = candidates
            .Select(match =>
            {
                var textForSignals = GetLexicalSignalText(match);
                var titleSignalText = GetTitleSignalText(match);
                return new CalibrationCandidate(
                    match,
                    ResolveRetriever(match),
                    textForSignals,
                    titleSignalText,
                    NormalizeForLexicalSignal(textForSignals),
                    NormalizeForLexicalSignal(titleSignalText),
                    NormalizeForLexicalSignal(match.Text));
            })
            .ToList();
        var hasExplicitFileDocumentHintMatch = hasExplicitFileDocumentHint
            && documentHintTokens.Count > 0
            && calibrationCandidates.Any(item => DocumentMatchesHint(documentHintTokens, item.Match));
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            rankingQuery,
            lexicalTokens,
            comparativeSubjectTokens);
        var hasLexicalAnchor = lexicalTokens.Count >= 1
            && calibrationCandidates.Any(item =>
            {
                if (string.Equals(item.Retriever, "dense_qdrant", StringComparison.Ordinal)
                    || string.Equals(item.Retriever, "linked_context", StringComparison.Ordinal))
                    return false;

                return item.Match.Score >= 0.70
                    && ComputeLexicalCoverageInNormalizedText(lexicalTokens, item.NormalizedTextForSignals) >= 0.5;
            });

        return calibrationCandidates
            .Select(item =>
            {
                var match = item.Match;
                var retriever = item.Retriever;
                var adjusted = match.Score;
                var lexicalCoverage = lexicalTokens.Count > 0
                    ? ComputeLexicalCoverageInNormalizedText(lexicalTokens, item.NormalizedTextForSignals)
                    : 0.0;
                var comparativeProfileConstraintCoverage =
                    prioritizeComparativeProfileCoverage
                    && string.Equals(retriever, "document_profile", StringComparison.Ordinal)
                        ? ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, match)
                        : 0.0;
                var comparativeOperandTitleSignal = prioritizeComparativeOperandTitleSignal
                    ? ComputeComparativeOperandTitleSignal(rankingQuery, item.TitleSignalText)
                    : 0;
                var referenceVersionOperandTitleSignal = prioritizeReferenceVersionOperandTitleSignal
                    ? ComputeReferenceVersionOperandTitleSignal(rankingQuery, item.TitleSignalText)
                    : 0;
                var requestedDocumentTypeTitleSignal = requestedSafetyDataSheetDocumentType
                    ? ComputeRequestedDocumentTypeTitleSignal(
                        rankingQuery,
                        GetDocumentTypeTitleSignalText(match))
                    : 0;
                var requestedDocumentVersionTitleSignal = ComputeRequestedDocumentVersionTitleSignal(rankingQuery, match);
                var exactTitleScore = titleScoringEnabled
                    ? ComputeExactTitleCandidateScoreCore(
                        match,
                        item.NormalizedTitleSignalText,
                        item.NormalizedMatchText,
                        titleTokens,
                        phraseTerms,
                        normalizedTitleScoringQuery)
                    : 0.0;
                var hasQuotedTitlePlacementEvidence = quotedPhrases.Count > 0
                    && HasQuotedTitlePlacementEvidence(quotedPhrases, match);
                var exactQuotedTitlePriority = quotedPhrases.Count > 0
                    ? ComputeExactQuotedTitlePlacementPriority(quotedPhrases, match)
                    : 0;
                var directQuotedTitleTokenCoverage = quotedPhrases.Count > 0
                    ? ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match)
                    : 0.0;
                var discountSyntheticRouteTitle =
                    ShouldDiscountSyntheticRouteTitleForQuotedLookup(
                        quotedPhrases,
                        match,
                        hasQuotedTitlePlacementEvidence,
                        directQuotedTitleTokenCoverage);
                var weakDirectTitleTokenRoute = IsDirectTitleTokenRouteMatch(match)
                    && !DirectTitleTokenRouteHasStrongLeadEvidence(match);
                if (weakDirectTitleTokenRoute
                    || discountSyntheticRouteTitle
                    || (quotedPhrases.Count > 0
                        && string.Equals(retriever, "exact_match", StringComparison.Ordinal)
                        && !hasQuotedTitlePlacementEvidence))
                {
                    exactTitleScore = 0.0;
                }
                var quotedLookupScore = quotedPhrases.Count > 0
                    ? ComputeQuotedLookupCandidateScore(
                        quotedPhrases,
                        string.Join("\n", new[]
                        {
                            item.TextForSignals,
                            match.Text,
                            match.SectionTitle,
                            match.HeadingPath
                        }.Where(static value => !string.IsNullOrWhiteSpace(value))))
                    : 0.0;
                if (quotedPhrases.Count > 0
                    && (discountSyntheticRouteTitle
                        || (string.Equals(retriever, "exact_match", StringComparison.Ordinal)
                            && !hasQuotedTitlePlacementEvidence)))
                {
                    quotedLookupScore = 0.0;
                }
                var specificAnchorCount = CountSpecificLexicalAnchorsInNormalizedText(lexicalTokens, item.NormalizedTextForSignals);
                var requiresComparativeSubjectAnchor = comparativeSubjectTokens.Count > 0;
                var requiresPrimarySpecificAnchor = !requiresComparativeSubjectAnchor
                    && RequiresPrimarySpecificLexicalAnchor(lexicalTokens);
                var containsPrimarySpecificAnchor = !requiresPrimarySpecificAnchor
                    || ContainsPrimarySpecificLexicalAnchorInNormalizedText(lexicalTokens, item.NormalizedTextForSignals);
                var containsComparativeSubjectAnchor = !requiresComparativeSubjectAnchor
                    || ContainsComparativeSubjectAnchorInNormalizedText(comparativeSubjectTokens, item.NormalizedTextForSignals);
                var hasClosePhraseMatch = phraseTerms.Length > 0
                    && phraseTerms.Any(term => ContainsOrderedPhraseWindowInNormalizedText(item.NormalizedTextForSignals, term, maxGapChars: 40));
                var matchedCardTitleSignal = ComputeMatchedContentCardTitleSignal(
                    match,
                    titleTokens,
                    normalizedTitleScoringQuery);
                var unrequestedQualifiedTitlePenalty = applyUnrequestedQualifiedTitlePenalty
                    ? ComputeUnrequestedQualifiedTitlePenalty(unrequestedQualifiedTitleSubjectTokens, lexicalTokens, match)
                    : 0;
                var focusedHeadAnchorSignal = ComputeFocusedHeadAnchorSignal(focusedHeadAnchorTokens, match);
                var matchesDocumentHint = DocumentMatchesHint(documentHintTokens, match);
                var strongExactReferenceSignal = hasReferenceLikeQueryToken
                    && referenceTerms.Length > 0
                    && string.Equals(retriever, "exact_match", StringComparison.Ordinal)
                    && (string.Equals(match.ChunkType, "exact_match_entry", StringComparison.Ordinal)
                        || string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal));
                double? insufficientQuotedTitleEvidenceCap = null;

                if (referenceTerms.Length > 0)
                {
                    if (string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                    {
                        adjusted += string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal)
                            ? 0.03
                            : 0.02;
                    }
                    else if (!string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted -= 0.01;
                    }
                }

                if (documentHintTokens.Count > 0)
                {
                    if (matchesDocumentHint)
                    {
                        adjusted += retriever switch
                        {
                            "exact_match" => 0.20,
                            "explicit_document_title_route" => 0.28,
                            "title_anchor_route" => 0.18,
                            "navigation_route" => 0.16,
                            "direct_title_token_route" => 0.16,
                            "fuzzy_title_lead" => 0.15,
                            "sparse_bm25" => 0.18,
                            "dense_qdrant" => 0.14,
                            "document_profile" => 0.12,
                            _ => 0.10
                        };
                    }
                    else if (!string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                    {
                        adjusted -= retriever switch
                        {
                            "dense_qdrant" => 0.10,
                            "sparse_bm25" => 0.08,
                            "document_profile" => 0.06,
                            "title_anchor_route" when hasExplicitFileDocumentHintMatch => 0.14,
                            "navigation_route" when hasExplicitFileDocumentHintMatch => 0.14,
                            "local_title_token_route" when hasExplicitFileDocumentHintMatch => 0.14,
                            _ => 0.05
                        };
                        if (hasExplicitFileDocumentHintMatch)
                            adjusted = Math.Min(adjusted, 0.82);
                    }
                }

                if (lexicalTokens.Count > 0)
                {
                    if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.06,
                            >= 0.50 => 0.035,
                            >= 0.34 => 0.015,
                            _ when lexicalTokens.Count >= 4 => -0.08,
                            _ when lexicalTokens.Count >= 3 => -0.04,
                            _ => 0.0
                        };
                    }
                    else if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.025,
                            >= 0.50 => 0.012,
                            < 0.20 when lexicalTokens.Count >= 2 => -0.03,
                            _ => 0.0
                        };
                    }
                }

                if (prioritizeExactTitleBeforeScore && exactTitleScore > 0.0)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => Math.Min(0.32, 0.12 + (exactTitleScore * 0.006)),
                        "explicit_document_title_route" => Math.Min(0.34, 0.14 + (exactTitleScore * 0.006)),
                        "title_anchor_route" => Math.Min(0.30, 0.12 + (exactTitleScore * 0.006)),
                        "navigation_route" => Math.Min(0.26, 0.10 + (exactTitleScore * 0.005)),
                        "direct_title_token_route" => Math.Min(0.28, 0.11 + (exactTitleScore * 0.005)),
                        "fuzzy_title_lead" => Math.Min(0.24, 0.09 + (exactTitleScore * 0.005)),
                        "dense_qdrant" => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004)),
                        "document_profile" => Math.Min(0.20, 0.08 + (exactTitleScore * 0.004)),
                        "exact_match" => Math.Min(0.18, 0.08 + (exactTitleScore * 0.004)),
                        _ => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004))
                    };

                    if (LooksLikeStructuredAnswerUnit(match))
                        adjusted += 0.06;
                }

                if (comparativeOperandTitleSignal > 0)
                {
                    adjusted += retriever switch
                    {
                        "title_anchor_route" => Math.Min(0.26, 0.035 * comparativeOperandTitleSignal),
                        "sparse_bm25" => Math.Min(0.22, 0.030 * comparativeOperandTitleSignal),
                        "document_profile" => Math.Min(0.18, 0.025 * comparativeOperandTitleSignal),
                        "dense_qdrant" => Math.Min(0.12, 0.018 * comparativeOperandTitleSignal),
                        _ => Math.Min(0.16, 0.022 * comparativeOperandTitleSignal)
                    };
                }

                if (referenceVersionOperandTitleSignal > 0)
                {
                    adjusted += retriever switch
                    {
                        "reference_version_operand_document_name" => Math.Min(0.32, 0.035 * referenceVersionOperandTitleSignal),
                        "title_anchor_route" => Math.Min(0.28, 0.030 * referenceVersionOperandTitleSignal),
                        "sparse_bm25" => Math.Min(0.24, 0.028 * referenceVersionOperandTitleSignal),
                        "document_profile" => Math.Min(0.12, 0.014 * referenceVersionOperandTitleSignal),
                        "dense_qdrant" => Math.Min(0.10, 0.012 * referenceVersionOperandTitleSignal),
                        _ => Math.Min(0.16, 0.018 * referenceVersionOperandTitleSignal)
                    };
                }

                if (requestedDocumentTypeTitleSignal > 0)
                {
                    adjusted += retriever switch
                    {
                        "title_anchor_route" => Math.Min(0.34, 0.10 * requestedDocumentTypeTitleSignal),
                        "direct_title_token_route" => Math.Min(0.32, 0.09 * requestedDocumentTypeTitleSignal),
                        "explicit_document_title_route" => Math.Min(0.34, 0.10 * requestedDocumentTypeTitleSignal),
                        "sparse_bm25" => Math.Min(0.30, 0.085 * requestedDocumentTypeTitleSignal),
                        "document_profile" => Math.Min(0.22, 0.060 * requestedDocumentTypeTitleSignal),
                        "exact_match" => Math.Min(0.20, 0.055 * requestedDocumentTypeTitleSignal),
                        "dense_qdrant" => Math.Min(0.16, 0.045 * requestedDocumentTypeTitleSignal),
                        _ => Math.Min(0.18, 0.050 * requestedDocumentTypeTitleSignal)
                    };
                }
                else if (requestedSafetyDataSheetDocumentType
                         && !string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "title_anchor_route" => 0.18,
                        "direct_title_token_route" => 0.16,
                        "explicit_document_title_route" => 0.18,
                        "sparse_bm25" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.06
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.82 : 0.84);
                }

                if (requestedDocumentVersionTitleSignal > 0)
                {
                    adjusted += retriever switch
                    {
                        "document_profile" => 0.24,
                        "sparse_bm25" => 0.20,
                        "title_anchor_route" => 0.18,
                        "dense_qdrant" => 0.14,
                        _ => 0.12
                    };
                }
                else if (QueryRequestsHistoricalDocumentVersion(rankingQuery)
                         && !string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "document_profile" => 0.10,
                        "sparse_bm25" => 0.08,
                        "title_anchor_route" => 0.10,
                        _ => 0.05
                    };
                    adjusted = Math.Min(adjusted, 0.90);
                }

                if (matchedCardTitleSignal > 0)
                    adjusted += Math.Min(0.20, 0.05 * matchedCardTitleSignal);
                if (prioritizeContentEvidenceBeforeFocusedHead && focusedHeadAnchorSignal > 0)
                    adjusted += Math.Min(0.12, 0.06 * focusedHeadAnchorSignal);

                if (quotedPhrases.Count > 0
                    && directQuotedTitleTokenCoverage < 0.75
                    && (!hasQuotedTitlePlacementEvidence || discountSyntheticRouteTitle))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.22,
                        "exact_match" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                    insufficientQuotedTitleEvidenceCap =
                        string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.84 : 0.86;
                }

                if (prioritizeExactTitleBeforeScore && hasClosePhraseMatch)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.25,
                        "explicit_document_title_route" => 0.28,
                        "title_anchor_route" => 0.22,
                        "navigation_route" => 0.18,
                        "direct_title_token_route" => 0.20,
                        "fuzzy_title_lead" => 0.18,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.06
                    };
                }

                if (prioritizeExactTitleBeforeScore
                    && LooksLikeStructuredAnswerUnit(match)
                    && specificAnchorCount > 0
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.80))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.16,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (prioritizeExactTitleBeforeScore
                    && specificAnchorCount >= 2
                    && LooksLikeStructuredAnswerChunk(match))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.18,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (HasProfileTitleHint(match)
                    && requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor)
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.14,
                        "dense_qdrant" => 0.08,
                        "document_profile" => 0.06,
                        _ => 0.07
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.80 : 0.78);
                }

                if (requiresComparativeSubjectAnchor
                    && !containsComparativeSubjectAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.13,
                        "document_profile" => 0.10,
                        _ => 0.12
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }

                if (HasProfileTitleHint(match)
                    && specificAnchorCount >= 2
                    && containsPrimarySpecificAnchor
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.50))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.28,
                        "title_anchor_route" => 0.24,
                        "navigation_route" => 0.18,
                        "direct_title_token_route" => 0.20,
                        "fuzzy_title_lead" => 0.18,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                }

                if (quotedLookupScore > 0.0)
                    adjusted += Math.Min(0.24, quotedLookupScore * 0.015);

                if (insufficientQuotedTitleEvidenceCap is double scoreCap)
                    adjusted = Math.Min(adjusted, scoreCap);

                if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                    && hasLexicalAnchor
                    && lexicalCoverage < 0.20)
                    adjusted = Math.Min(adjusted - 0.15, 0.24);

                if (LooksLikeSourceListChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.26 : 0.16;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }
                else if (LooksLikeDocumentOverviewChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.18 : 0.10;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (LooksLikeNavigationalChunk(match))
                {
                    var strongNavigation = LooksLikeStrongNavigationalChunk(match);
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" when strongNavigation => 0.32,
                        "sparse_bm25" => 0.20,
                        _ when strongNavigation => 0.18,
                        _ => 0.11
                    };
                    adjusted = Math.Min(
                        adjusted,
                        strongNavigation
                            ? (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.68 : 0.66)
                            : (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.82 : 0.78));
                }
                else if (!ShouldAllowGlossaryResults(originalQuery ?? query) && LooksLikeGlossaryChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.22 : 0.14;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.76 : 0.72);
                }
                else if (weakDirectTitleTokenRoute)
                {
                    adjusted -= 0.18;
                    adjusted = Math.Min(adjusted, 0.86);
                }

                if (unrequestedQualifiedTitlePenalty > 0)
                {
                    adjusted -= 0.18 * Math.Min(2, unrequestedQualifiedTitlePenalty);
                    adjusted = Math.Min(adjusted, unrequestedQualifiedTitlePenalty >= 2 ? 0.84 : 0.90);
                }

                var directChunkTitleSignal = weakDirectTitleTokenRoute
                    || (quotedPhrases.Count > 0 && !hasQuotedTitlePlacementEvidence)
                    ? 0
                    : ComputeDirectChunkTitleSignalCore(
                        item.NormalizedMatchText,
                        titleTokens,
                        normalizedTitleScoringQuery);
                var quotedTitleAnchorSignal = ComputeQuotedTitleAnchorSignal(rankingQuery, match);
                var structuredTitleAnswerPriority = ComputeStructuredTitleAnswerPriority(
                    structuredTitleLookup,
                    match,
                    exactTitleScore,
                    matchedCardTitleSignal,
                    quotedTitleAnchorSignal,
                    hasQuotedTitlePlacementEvidence,
                    directChunkTitleSignal);
                var weakExactStructuredTitleMention = IsWeakExactStructuredTitleMention(
                    structuredTitleLookup,
                    match,
                    retriever,
                    hasQuotedTitlePlacementEvidence,
                    structuredTitleAnswerPriority,
                    strongExactReferenceSignal);
                var weakLowInformationExactMatch = ShouldPenalizeWeakExactMatchForBroadQuery(
                    rankingQuery,
                    match,
                    lexicalCoverage,
                    exactTitleScore,
                    quotedLookupScore,
                    matchedCardTitleSignal);
                var fastDurationPriority = prioritizeFastDuration
                    && HasFocusedTitlePhraseEvidenceForPreference(rankingQuery, match)
                        ? ComputeFastDurationPreferencePriority(match)
                        : 0;
                var shortTechnicalDirectEvidencePriority = ComputeShortTechnicalDirectEvidencePriority(
                    rankingQuery,
                    match,
                    item.NormalizedTextForSignals,
                    item.NormalizedTitleSignalText);

                if (structuredTitleAnswerPriority > 0)
                    adjusted += Math.Min(0.22, 0.045 * structuredTitleAnswerPriority);
                if (fastDurationPriority > 0)
                    adjusted += Math.Min(0.18, 0.035 * fastDurationPriority);
                if (shortTechnicalDirectEvidencePriority > 0)
                    adjusted += Math.Min(0.30, 0.075 * shortTechnicalDirectEvidencePriority);

                if (weakExactStructuredTitleMention)
                {
                    adjusted -= 0.22;
                    adjusted = Math.Min(adjusted, 0.74);
                }

                if (weakLowInformationExactMatch)
                {
                    adjusted -= 0.40;
                    adjusted = Math.Min(adjusted, 0.58);
                }

                if (preserveCatalogOverviewProfiles && IsDocumentProfileMatch(match))
                    adjusted = Math.Max(adjusted, 0.46);

                if (insufficientQuotedTitleEvidenceCap is double finalScoreCap)
                    adjusted = Math.Min(adjusted, finalScoreCap);

                adjusted = ApplyChunkExtractionQualityScorePenalty(adjusted, match);

                return new
                {
                    Match = match with { Score = Math.Clamp(adjusted, 0.0, 1.02) },
                    Retriever = retriever,
                    OriginalScore = match.Score,
                    DiscountSyntheticRouteTitle = discountSyntheticRouteTitle,
                    ExactTitleScore = exactTitleScore,
                    ExactQuotedTitlePriority = exactQuotedTitlePriority,
                    DirectChunkTitleSignal = directChunkTitleSignal,
                    FocusedHeadAnchorSignal = focusedHeadAnchorSignal,
                    HasTitlePlacementEvidence = hasQuotedTitlePlacementEvidence,
                    DirectQuotedTitleTokenCoverage = directQuotedTitleTokenCoverage,
                    MatchedCardTitleSignal = matchedCardTitleSignal,
                    UnrequestedQualifiedTitlePenalty = unrequestedQualifiedTitlePenalty,
                    QuotedLookupScore = quotedLookupScore,
                    LexicalCoverage = lexicalCoverage,
                    ComparativeProfileConstraintCoverage = comparativeProfileConstraintCoverage,
                    ComparativeOperandTitleSignal = comparativeOperandTitleSignal,
                    ReferenceVersionOperandTitleSignal = referenceVersionOperandTitleSignal,
                    RequestedDocumentTypeTitleSignal = requestedDocumentTypeTitleSignal,
                    RequestedDocumentVersionTitleSignal = requestedDocumentVersionTitleSignal,
                    SpecificAnchorCount = specificAnchorCount,
                    RequiresComparativeSubjectAnchor = requiresComparativeSubjectAnchor,
                    ContainsComparativeSubjectAnchor = containsComparativeSubjectAnchor,
                    ContainsPrimarySpecificAnchor = containsPrimarySpecificAnchor,
                    StrongExactReferenceSignal = strongExactReferenceSignal,
                    WeakExactStructuredTitleMention = weakExactStructuredTitleMention,
                    WeakLowInformationExactMatch = weakLowInformationExactMatch,
                    FastDurationPriority = fastDurationPriority,
                    ShortTechnicalDirectEvidencePriority = shortTechnicalDirectEvidencePriority,
                    StructuredTitleAnswerPriority = structuredTitleAnswerPriority,
                    DocumentHintMatched = matchesDocumentHint,
                    StructuredAnswerPriority = GetStructuredAnswerPriority(match),
                    ContentEvidencePriority = ComputeContentEvidencePriority(match)
                };
            })
            .OrderByDescending(static item => item.StrongExactReferenceSignal)
            .ThenByDescending(item => prioritizeReferenceVersionOperandTitleSignal ? item.ReferenceVersionOperandTitleSignal : 0)
            .ThenByDescending(item => prioritizeComparativeOperandTitleSignal ? item.ComparativeOperandTitleSignal : 0)
            .ThenByDescending(item => prioritizeComparativeProfileCoverage ? item.ComparativeProfileConstraintCoverage : 0.0)
            .ThenBy(static item => item.WeakExactStructuredTitleMention ? 1 : 0)
            .ThenBy(static item => item.WeakLowInformationExactMatch ? 1 : 0)
            .ThenBy(static item => item.DiscountSyntheticRouteTitle ? 1 : 0)
            .ThenByDescending(static item => item.RequestedDocumentTypeTitleSignal)
            .ThenByDescending(static item => item.RequestedDocumentVersionTitleSignal)
            .ThenByDescending(static item => item.ShortTechnicalDirectEvidencePriority)
            .ThenByDescending(item => prioritizeLexicalCoverageBeforeRawScore ? item.LexicalCoverage : 0.0)
            .ThenByDescending(static item => item.DocumentHintMatched)
            .ThenByDescending(static item => item.FastDurationPriority)
            .ThenByDescending(item =>
                !prioritizeContentEvidenceBeforeFocusedHead
                    ? 0.0
                    : item.RequiresComparativeSubjectAnchor && !item.ContainsComparativeSubjectAnchor
                    ? 0.0
                    : item.ContentEvidencePriority)
            .ThenByDescending(item => prioritizeContentEvidenceBeforeFocusedHead ? item.FocusedHeadAnchorSignal : 0)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenByDescending(static item => item.HasTitlePlacementEvidence)
            .ThenByDescending(item => prioritizeTitleSignalsBeforeRawScore ? item.StructuredTitleAnswerPriority : 0)
            .ThenByDescending(static item => item.DirectQuotedTitleTokenCoverage)
            .ThenBy(static item => item.UnrequestedQualifiedTitlePenalty)
            .ThenByDescending(item => prioritizeTitleSignalsBeforeRawScore ? item.DirectChunkTitleSignal : 0)
            .ThenByDescending(item => prioritizeTitleSignalsBeforeRawScore ? item.MatchedCardTitleSignal : 0)
            .ThenByDescending(item => prioritizeExactTitleBeforeScore && item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(static item => item.DocumentHintMatched)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(item => prioritizeExactTitleBeforeScore ? item.ExactTitleScore : 0.0)
            .ThenByDescending(item => prioritizePrimarySpecificAnchorBeforeRawScore && item.ContainsPrimarySpecificAnchor)
            .ThenByDescending(item =>
                prioritizeTitleSignalsBeforeRawScore
                && (item.DirectChunkTitleSignal > 0
                    || item.MatchedCardTitleSignal > 0)
                || item.ExactTitleScore > 0.0
                    ? item.ContentEvidencePriority
                    : 0.0)
            .ThenByDescending(item => !prioritizeExactTitleBeforeScore ? item.OriginalScore : 0.0)
            .ThenByDescending(item => item.Match.Score)
            .ThenByDescending(static item => string.Equals(item.Retriever, "local_title_token_route", StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(static item => item.SpecificAnchorCount)
            .ThenByDescending(static item => item.LexicalCoverage)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(item => item.QuotedLookupScore)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .ToList();
    }


    internal static bool ShouldPenalizeWeakExactMatchForBroadQuery(
        string query,
        RagMatch match,
        double lexicalCoverage = 0.0,
        double exactTitleScore = 0.0,
        double quotedLookupScore = 0.0,
        int matchedCardTitleSignal = 0)
    {
        if (!string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal))
            return false;
        if (HasReferenceLikeQueryToken(query))
            return false;
        if (!ShouldPruneWeakExactLookupTermsForQuery(query))
            return false;
        if (lexicalCoverage >= 0.34
            || exactTitleScore > 0.0
            || quotedLookupScore > 0.0
            || matchedCardTitleSignal > 0)
        {
            return false;
        }

        var text = match.Text?.Trim() ?? string.Empty;
        if (IsWeakCountQuantityExactLookupTerm(text))
            return true;

        return text.Length is > 0 and < 40
            && !LooksLikeStructuredAnswerText(text);
    }


    internal static bool IsStructuredTitleLookupQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {NormalizeForLexicalSignal(query)} ";
        var asksStructuredDetails = HasStructuredContinuationBackfillIntent(query)
            || ContainsStructuredSheetRequest(normalized)
            || (ContainsStructuredInventoryRequest(normalized) && ContainsStructuredProcedureRequest(normalized))
            || (ContainsStructuredProcedureRequest(normalized) && ContainsStructuredTimeRequest(normalized));
        if (!asksStructuredDetails)
            return false;

        return ExtractTitleLookupPhrases(query).Count > 0
            || ExtractFocusedLookupPhrases(query).Count > 0
            || ContainsStructuredSheetRequest(normalized);
    }


    internal static int ComputeStructuredTitleAnswerPriority(
        bool structuredTitleLookup,
        RagMatch match,
        double exactTitleScore,
        int matchedCardTitleSignal,
        double quotedTitleAnchorSignal,
        bool hasQuotedTitlePlacementEvidence,
        int directChunkTitleSignal)
    {
        if (!structuredTitleLookup)
            return 0;
        if (LooksLikeSourceListChunk(match) || LooksLikeGlossaryChunk(match))
            return 0;

        var hasRouteOrLinkedTitleEvidence =
            !string.IsNullOrWhiteSpace(ExtractMatchedRouteOrProfileTitle(match.EmbedText))
            && quotedTitleAnchorSignal > 0.0;
        var hasTitleEvidence =
            exactTitleScore > 0.0
            || hasRouteOrLinkedTitleEvidence
            || hasQuotedTitlePlacementEvidence
            || directChunkTitleSignal > 0;
        if (!hasTitleEvidence)
            return 0;

        var hasInventory = ContainsStructuredInventoryCue(match);
        var hasProcedure = ContainsStructuredProcedureCue(match);
        var hasTime = ContainsStructuredTimeCue(match);
        var structuredPriority = GetStructuredAnswerPriority(match);
        var contentEvidence = ComputeContentEvidencePriority(match);

        if (IsResolvedTitleOrNavigationRoute(match)
            && !hasQuotedTitlePlacementEvidence
            && directChunkTitleSignal <= 0
            && matchedCardTitleSignal <= 0
            && !(hasRouteOrLinkedTitleEvidence
                && (LooksLikeStructuredAnswerChunk(match) || hasProcedure || hasTime)))
        {
            return 0;
        }

        if (hasInventory && hasProcedure)
            return hasTime ? 6 : 5;
        if (hasRouteOrLinkedTitleEvidence && hasProcedure)
            return hasTime ? 4 : 3;
        if (structuredPriority >= 2 && hasProcedure)
            return 4;
        if (structuredPriority >= 2 && hasInventory)
            return 3;
        if (structuredPriority >= 2)
            return 2;

        return contentEvidence >= 0.95 && (hasInventory || hasProcedure || hasTime) ? 1 : 0;
    }


    internal static bool IsWeakExactStructuredTitleMention(
        bool structuredTitleLookup,
        RagMatch match,
        string retriever,
        bool hasQuotedTitlePlacementEvidence,
        int structuredTitleAnswerPriority,
        bool strongExactReferenceSignal)
    {
        if (!structuredTitleLookup
            || strongExactReferenceSignal
            || !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal)
            || string.Equals(match.ChunkType, "exact_match_entry", StringComparison.Ordinal))
        {
            return false;
        }

        if (hasQuotedTitlePlacementEvidence || structuredTitleAnswerPriority > 0)
            return false;

        return !LooksLikeStructuredAnswerChunk(match)
            || ComputeContentEvidencePriority(match) < 0.80;
    }


    internal static bool ShouldSuppressUnanchoredSpecificResults(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates.Count == 0)
            return false;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (lexicalTokens.Length == 0 || lexicalTokens.Length > 2)
            return false;
        if (!lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken))
            return false;

        return !candidates.Any(match => HasSpecificQueryAnchor(lexicalTokens, match));
    }


    private static bool HasSpecificQueryAnchor(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        var signalText = GetLexicalSignalText(match);
        return ContainsPrimarySpecificLexicalAnchor(lexicalTokens, signalText)
            || CountSpecificLexicalAnchors(lexicalTokens, signalText) > 0
            || ComputeLexicalCoverage(lexicalTokens, signalText) >= 0.50;
    }


    internal static bool ShouldRunPreciseTitleLinkedBackfill(string query, IReadOnlyList<RagMatch> selected)
    {
        if (selected.Count == 0)
            return false;

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return false;

        return selected
            .Take(8)
            .Any(match => IsPreciseTitleBackfillScopeCandidate(quotedPhrases, match));
    }


    internal static bool IsPreciseTitleBackfillScopeCandidate(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return false;
        if (string.IsNullOrWhiteSpace(match.DocPath) || IsDocumentProfileMatch(match))
            return false;
        if (!IsResolvedTitleOrNavigationRoute(match)
            && match.Score >= 0.70
            && IsContentSelectionCandidate(match)
            && ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match) < 0.75)
        {
            return true;
        }
        if (NeedsPreciseTitleContextBackfill(quotedPhrases, match))
            return true;
        if (HasQuotedTitlePlacementEvidence(quotedPhrases, match))
            return false;

        return match.Score >= 0.70 && IsContentSelectionCandidate(match);
    }


    private static bool NeedsPreciseTitleContextBackfill(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return false;
        if (HasQuotedTitlePlacementEvidence(quotedPhrases, match)
            || ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match) >= 0.75)
        {
            return false;
        }

        return match.Score >= 0.70
            || ComputeQuotedLookupCandidateScore(quotedPhrases, GetLexicalSignalText(match)) > 0.0;
    }


    internal static List<RagMatch> PrioritizePreciseTitleBackfillSelections(
        string query,
        IReadOnlyList<RagMatch> candidates,
        IReadOnlyList<RagMatch> backfillMatches)
    {
        if (candidates.Count <= 1 || backfillMatches.Count == 0)
            return candidates.ToList();

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return candidates.ToList();

        var highConfidenceBackfillKeys = backfillMatches
            .Where(static match => IsResolvedTitleOrNavigationRoute(match))
            .Where(match => HasQuotedTitlePlacementEvidence(quotedPhrases, match))
            .Select(BuildMatchDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (highConfidenceBackfillKeys.Count == 0)
            return candidates.ToList();

        return candidates
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                IsHighConfidenceBackfill = highConfidenceBackfillKeys.Contains(BuildMatchDedupKey(match)),
                ExactQuotedTitlePriority = ComputeExactQuotedTitlePlacementPriority(quotedPhrases, match)
            })
            .OrderByDescending(static item => item.IsHighConfidenceBackfill)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match)
            .ToList();
    }


    internal static int ComputeExactQuotedTitlePlacementPriority(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return 0;

        var rawText = match.Text;
        var normalizedText = NormalizeForLexicalSignal(rawText);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var best = 0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = NormalizeForLexicalSignal(quotedPhrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var exactPhraseIndex = normalizedText.IndexOf(normalizedPhrase, StringComparison.Ordinal);
            if (exactPhraseIndex < 0)
                continue;

            var hasFooterTitleEvidence = HasLocalTitleTokenFooterTitleEvidence(
                normalizedText,
                exactPhraseIndex,
                normalizedPhrase.Length);
            var hasPlacementEvidence =
                LooksLikeStandaloneLeadTitlePlacement(rawText, normalizedText, normalizedPhrase, exactPhraseIndex)
                || hasFooterTitleEvidence;
            if (!hasPlacementEvidence)
                continue;

            best = Math.Max(best, hasFooterTitleEvidence || HasRawExactQuotedTitleEndBoundary(rawText, normalizedPhrase) ? 2 : 1);
        }

        return best;
    }


    internal static int ComputePreciseQuotedTitleSpecificityPriority(
        IReadOnlyList<string> quotedPhrases,
        RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return 0;

        var best = 0;
        foreach (var signalText in new[] { match.SectionTitle, match.HeadingPath, match.Text })
            best = Math.Max(best, ComputePreciseQuotedLeadTitleSpecificityPriority(quotedPhrases, signalText));

        if (match.MatchedContentCards is { Count: > 0 })
        {
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
                best = Math.Max(best, ComputePreciseQuotedLeadTitleSpecificityPriority(quotedPhrases, card.Title));
        }

        return best;
    }


    internal static int ComputePreciseQuotedLeadTitleSpecificityPriority(
        IReadOnlyList<string> quotedPhrases,
        string? signalText)
    {
        var normalizedText = NormalizeForLexicalSignal(signalText);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var candidateTokens = normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 0)
            .Take(16)
            .ToArray();
        if (candidateTokens.Length == 0)
            return 0;

        candidateTokens = SkipLeadingQuotedTitleNoiseTokens(candidateTokens);
        if (candidateTokens.Length == 0)
            return 0;

        var best = 0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var quotedTokens = ExtractQuotedTitleCoverageTokens(quotedPhrase);
            if (quotedTokens.Length < 2 || candidateTokens.Length < quotedTokens.Length)
                continue;

            var matched = true;
            for (var i = 0; i < quotedTokens.Length; i++)
            {
                if (!QuotedTitleTokenMatchesCandidateLead(quotedTokens[i], candidateTokens[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            best = Math.Max(
                best,
                HasQuotedTitleConnectorQualifier(candidateTokens, quotedTokens.Length) ? 1 : 3);
        }

        return best;
    }


    private static string[] SkipLeadingQuotedTitleNoiseTokens(string[] candidateTokens)
    {
        var index = 0;
        while (index < candidateTokens.Length
               && (candidateTokens[index].All(char.IsDigit)
                   || candidateTokens[index] is "p" or "page"))
        {
            index++;
        }

        return index == 0 ? candidateTokens : candidateTokens[index..];
    }


    private static bool QuotedTitleTokenMatchesCandidateLead(string quotedToken, string candidateToken)
    {
        if (string.IsNullOrWhiteSpace(quotedToken) || string.IsNullOrWhiteSpace(candidateToken))
            return false;

        foreach (var variant in BuildLexicalTokenVariants(quotedToken).DefaultIfEmpty(quotedToken))
        {
            var foldedVariant = FoldDiacritics(variant);
            if (string.Equals(candidateToken, foldedVariant, StringComparison.Ordinal))
                return true;

            if (foldedVariant.Length >= 8
                && candidateToken.Length >= 6
                && Math.Abs(candidateToken.Length - foldedVariant.Length) <= 2
                && ComputeBoundedEditDistance(candidateToken, foldedVariant, maxDistance: 2) <= 2)
            {
                return true;
            }
        }

        return false;
    }


    private static string ResolveMatchDedupDocumentKey(RagMatch match)
    {
        var stableHash = ResolveStableContentHash(match.HashDoc);
        if (stableHash is not null)
            return $"hash:{stableHash}";

        if (!string.IsNullOrWhiteSpace(match.DocId))
            return $"doc:{match.DocId.Trim()}";

        var normalizedPath = NormalizeRagDocPath(match.DocPath);
        return string.IsNullOrWhiteSpace(normalizedPath)
            ? "doc:unknown"
            : $"path:{normalizedPath}";
    }


    private static bool IsSameDedupDocumentFingerprint(RagMatch left, RagMatch right)
    {
        var leftHash = ResolveStableContentHash(left.HashDoc);
        var rightHash = ResolveStableContentHash(right.HashDoc);
        if (leftHash is not null && rightHash is not null)
            return string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase);

        return string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);
    }


    private static string? ResolveStableContentHash(string? value)
    {
        var normalized = NormalizeHashHex(value);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 16)
            return null;

        return normalized.All(Uri.IsHexDigit) ? normalized : null;
    }


    private static bool HasQuotedTitleConnectorQualifier(IReadOnlyList<string> candidateTokens, int quotedTokenCount)
    {
        if (quotedTokenCount < 1 || candidateTokens.Count <= quotedTokenCount + 1)
            return false;
        if (!TitleConnectorTokens.Contains(candidateTokens[quotedTokenCount]))
            return false;

        return candidateTokens
            .Skip(quotedTokenCount + 1)
            .Take(3)
            .Any(IsFocusedLookupSignalToken);
    }


    internal static List<RagMatch> SuppressNavigationalNoise(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (candidates.Count == 0 || ShouldAllowNavigationalResults(query))
            return candidates.ToList();

        var allowGlossary = ShouldAllowGlossaryResults(query);
        var classified = candidates
            .Select(match =>
            {
                var keepResolvedRoute = IsResolvedTitleOrNavigationRoute(match);
                var isNavigational = !keepResolvedRoute && LooksLikeNavigationalChunk(match);
                var isGlossary = !allowGlossary && LooksLikeGlossaryChunk(match);
                return new
                {
                    Match = match,
                    IsNavigational = isNavigational,
                    IsGlossary = isGlossary
                };
            })
            .ToList();
        return classified
            .Where(static item => !item.IsNavigational && !item.IsGlossary)
            .Select(static item => item.Match)
            .ToList();
    }


    internal static void PruneNavigationalSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || ShouldAllowNavigationalResults(query))
            return;

        var allowGlossary = ShouldAllowGlossaryResults(query);
        var hasContentCandidate = selected.Any(static match => IsContentSelectionCandidate(match));
        selected.RemoveAll(match =>
            (IsWeakResolvedRouteTarget(match) && hasContentCandidate)
            || (!IsResolvedTitleOrNavigationRoute(match)
                && (LooksLikeNavigationalChunk(match)
                    || (!allowGlossary && LooksLikeGlossaryChunk(match)))));
    }


    internal static void CleanupLateSelectionBackfills(string query, List<RagMatch> selected)
    {
        PruneNavigationalSelections(query, selected);
        PruneResidualNavigationalSelectionsWhenContentExists(query, selected);
        PromoteActionableSameDocumentEvidence(selected);
    }


    internal static void PruneSupersededReferenceVersionSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var versionedSelections = selected
            .Select(match => new
            {
                Match = match,
                GroupKey = TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null,
                Year = ExtractLatestDocumentVersionYear(match)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.GroupKey) && item.Year is > 0)
            .ToArray();
        if (versionedSelections.Length == 0)
            return;

        if (!ShouldApplyReferenceVersionFreshness(query)
            && !ShouldApplyFallbackReferenceVersionFreshness(query))
        {
            return;
        }

        var latestByGroup = versionedSelections
            .GroupBy(static item => item.GroupKey!, StringComparer.Ordinal)
            .Where(static group => group.Select(static item => item.Year!.Value).Distinct().Count() > 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(static item => item.Year!.Value),
                StringComparer.Ordinal);
        if (latestByGroup.Count == 0)
            return;

        selected.RemoveAll(match =>
        {
            if (!TryExtractReferenceVersionGroupKey(match, out var groupKey))
                return false;
            if (!latestByGroup.TryGetValue(groupKey, out var latestYear))
                return false;

            var year = ExtractLatestDocumentVersionYear(match);
            return year is > 0 && year.Value < latestYear;
        });
    }


    internal static void PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || !QueryExplicitlyRequestsHistoricalDocumentVersion(query))
            return;

        var versionedSelections = selected
            .Select(match => new
            {
                Match = match,
                GroupKey = TryExtractReferenceVersionGroupKey(match, out var groupKey) ? groupKey : null,
                Year = ExtractLatestDocumentVersionYear(match)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.GroupKey) && item.Year is > 0)
            .ToArray();
        if (versionedSelections.Length == 0)
            return;

        var oldestByGroup = versionedSelections
            .GroupBy(static item => item.GroupKey!, StringComparer.Ordinal)
            .Where(static group => group.Select(static item => item.Year!.Value).Distinct().Count() > 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Min(static item => item.Year!.Value),
                StringComparer.Ordinal);
        if (oldestByGroup.Count == 0)
            return;

        selected.RemoveAll(match =>
        {
            if (!TryExtractReferenceVersionGroupKey(match, out var groupKey))
                return false;
            if (!oldestByGroup.TryGetValue(groupKey, out var oldestYear))
                return false;

            var year = ExtractLatestDocumentVersionYear(match);
            return year is > 0 && year.Value > oldestYear;
        });
    }


    internal static void PruneUnrelatedReferenceVersionGroupSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var groupKeys = ExtractReferenceVersionGroupKeysFromQuery(query).ToList();
        if (groupKeys.Count == 0 && ShouldUseReferenceVersionOperandRoute(query))
        {
            var inferredGroupKeys = InferReferenceVersionOperandGroupKeys(query, selected).ToList();
            var multiDocumentInferredGroupKeys = inferredGroupKeys
                .Where(groupKey => CountDistinctReferenceVersionGroupDocuments(selected, groupKey) >= 2)
                .ToList();
            groupKeys.AddRange(multiDocumentInferredGroupKeys.Count > 0
                ? multiDocumentInferredGroupKeys
                : inferredGroupKeys);
        }
        if (groupKeys.Count == 0)
            return;

        groupKeys = groupKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToList();
        if (groupKeys.Count == 0)
            return;

        var hasCompatibleSelection = selected.Any(match =>
            TryExtractReferenceVersionGroupKey(match, out var groupKey)
            && ReferenceVersionGroupKeyIsCompatible(groupKeys, groupKey));
        if (!hasCompatibleSelection)
            return;

        selected.RemoveAll(match =>
            TryExtractReferenceVersionGroupKey(match, out var groupKey)
            && !ReferenceVersionGroupKeyIsCompatible(groupKeys, groupKey));
    }


    internal static bool ReferenceVersionGroupKeyIsCompatible(IReadOnlyList<string> groupKeys, string candidateGroupKey)
    {
        if (string.IsNullOrWhiteSpace(candidateGroupKey))
            return false;

        return groupKeys.Any(groupKey =>
            string.Equals(candidateGroupKey, groupKey, StringComparison.Ordinal)
            || candidateGroupKey.StartsWith(groupKey + " ", StringComparison.Ordinal)
            || groupKey.StartsWith(candidateGroupKey + " ", StringComparison.Ordinal));
    }


    private static int CountDistinctReferenceVersionGroupDocuments(IReadOnlyList<RagMatch> selected, string groupKey)
        => selected
            .Where(match =>
                TryExtractReferenceVersionGroupKey(match, out var candidateGroupKey)
                && ReferenceVersionGroupKeyIsCompatible([groupKey], candidateGroupKey))
            .Select(static match => NormalizeRagDocPath(match.DocPath) ?? match.DocPath ?? string.Empty)
            .Where(static docPath => !string.IsNullOrWhiteSpace(docPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();


    internal static void PruneUnrequestedReferenceVersionYearSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || ShouldUseReferenceVersionOperandRoute(query))
            return;

        var normalizedQuery = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (ShouldPreserveMultipleReferenceVersions(query, normalizedQuery))
            return;

        var requestedYears = ExtractVersionYears(query);
        if (requestedYears.Count != 1)
            return;

        var requestedYear = requestedYears[0];
        var groupKeys = ExtractReferenceVersionGroupKeysFromQuery(query);
        if (groupKeys.Count == 0)
            return;

        var hasRequestedSelection = selected.Any(match =>
            TryExtractReferenceVersionGroupKey(match, out var groupKey)
            && ReferenceVersionGroupKeyIsCompatible(groupKeys, groupKey)
            && ExtractLatestDocumentVersionYear(match) == requestedYear);
        if (!hasRequestedSelection)
            return;

        selected.RemoveAll(match =>
            TryExtractReferenceVersionGroupKey(match, out var groupKey)
            && ReferenceVersionGroupKeyIsCompatible(groupKeys, groupKey)
            && ExtractLatestDocumentVersionYear(match) is { } year
            && year != requestedYear);
    }


    internal static bool SelectionsCoverReferenceVersionOperandContent(string query, IReadOnlyList<RagMatch> selected)
    {
        if (selected.Count == 0 || !ShouldUseReferenceVersionOperandRoute(query))
            return true;

        var requestedYears = ExtractReferenceVersionComparisonOperands(query)
            .Where(IsReferenceVersionYearToken)
            .Select(static operand => int.TryParse(operand, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0)
            .Where(static year => year is >= 1900 and <= 2099)
            .Distinct()
            .ToArray();
        if (requestedYears.Length < 2)
            return true;

        var groupKeys = ExtractReferenceVersionGroupKeysFromQuery(query);
        var contentYears = selected
            .Where(static match => !IsDocumentProfileMatch(match))
            .Where(match =>
                groupKeys.Count == 0
                || (TryExtractReferenceVersionGroupKey(match, out var groupKey)
                    && ReferenceVersionGroupKeyIsCompatible(groupKeys, groupKey)))
            .Select(ExtractLatestDocumentVersionYear)
            .Where(static year => year is > 0)
            .Select(static year => year!.Value)
            .Distinct()
            .ToHashSet();

        return requestedYears.All(contentYears.Contains);
    }


    private static bool ShouldApplyReferenceVersionFreshness(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || QueryMentionsDocumentVersionYear(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (QueryExplicitlyRequestsHistoricalDocumentVersion(query))
            return false;
        if (ShouldAllowReferenceLikeComparativeDiversity(query, normalized))
            return true;

        return HasReferenceLikeQueryToken(query)
               && ExtractStandardReferenceTitleLookupPhrases(query).Count > 0
               && !ShouldPreserveMultipleReferenceVersions(query, normalized);
    }


    private static bool ShouldApplyFallbackReferenceVersionFreshness(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || QueryMentionsDocumentVersionYear(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return !QueryExplicitlyRequestsHistoricalDocumentVersion(query)
               && !ShouldPreserveMultipleReferenceVersions(query, normalized);
    }


    private static bool ShouldPreserveMultipleReferenceVersions(string query, string normalizedQuery)
    {
        if (ShouldUseReferenceVersionOperandRoute(query))
            return true;
        if (ExtractSlashSeparatedStandardPartLookupPhrases(query).Count > 0
            && !QueryMentionsDocumentVersionYear(query))
        {
            return false;
        }

        return normalizedQuery.Contains(" citer separement ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" cite separement ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" sources separees ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" sources distinctes ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" sources dois tu citer ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" deux fichiers ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" two files ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" separate sources ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" cite separately ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" ne contiennent pas la meme ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" pas la meme information ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" fusionner ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" fusionne ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" toutes les versions ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" all versions ", StringComparison.Ordinal)
               || normalizedQuery.Contains(" merge ", StringComparison.Ordinal);
    }


    private static bool QueryExplicitlyRequestsHistoricalDocumentVersion(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (ContainsAny(
                normalized,
                " compare ",
                " comparer ",
                " comparaison ",
                " vs ",
                " versus ",
                " difference entre ",
                " differences entre ",
                " difference between ",
                " differences between "))
        {
            return false;
        }

        return ContainsAny(
            normalized,
            " ancienne version ",
            " anciennes versions ",
            " ancien document ",
            " ancienne edition ",
            " version precedente ",
            " version anterieure ",
            " old version ",
            " older version ",
            " previous version ",
            " prior version ",
            " archived version ",
            " archived document ",
            " archive ",
            " archives ",
            " legacy version ",
            " obsolete version ",
            " historical version ");
    }


    private static bool QueryMentionsDocumentVersionYear(string query)
        => ExtractVersionYears(query).Count > 0;


    private static int? ExtractLatestDocumentVersionYear(RagMatch match)
    {
        var signal = string.Join(' ', new[] { match.DocName, match.DocPath, match.EmbedText }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        var years = ExtractVersionYears(signal);
        return years.Count == 0 ? null : years.Max();
    }


    internal static IReadOnlyList<int> ExtractVersionYears(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<int>();

        return SafeRegexMatches(
                value,
                @"(?<!\d)(?:19|20)\d{2}(?!\d)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
            .Select(static match => int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                ? year
                : 0)
            .Where(static year => year is >= 1900 and <= 2099)
            .Distinct()
            .ToArray();
    }


    internal static bool TryExtractReferenceVersionGroupKey(RagMatch match, [NotNullWhen(true)] out string? groupKey)
    {
        groupKey = null;
        var signal = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(
                string.Join(' ', new[] { match.DocName, match.DocPath }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)))))
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(signal))
            return false;

        var matchResult = SafeRegexMatches(
            signal,
            @"\b(?<prefix>en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp)\s+(?<number>\d{2,6})(?:\s+(?<part>\d{1,4}[a-z0-9]*))?",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100))
            .FirstOrDefault();
        if (matchResult is null || !matchResult.Success)
            return TryExtractGenericReferenceVersionGroupKey(match, out groupKey);

        var tokens = new List<string>
        {
            matchResult.Groups["prefix"].Value,
            matchResult.Groups["number"].Value
        };
        var part = matchResult.Groups["part"].Value;
        if (!string.IsNullOrWhiteSpace(part))
            tokens.Add(part);

        groupKey = string.Join(' ', tokens);
        return true;
    }


    private static bool TryExtractGenericReferenceVersionGroupKey(RagMatch match, [NotNullWhen(true)] out string? groupKey)
    {
        groupKey = null;
        var title = !string.IsNullOrWhiteSpace(match.DocName)
            ? match.DocName
            : Path.GetFileName((match.DocPath ?? string.Empty).Replace('\\', '/'));
        title = Path.GetFileNameWithoutExtension(title);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalizedTitle = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title)).ToLowerInvariant();
        if (ExtractVersionYears(normalizedTitle).Count == 0)
            return false;

        normalizedTitle = Regex.Replace(
            normalizedTitle,
            @"\b(?:19|20)\d{2}\b",
            " ",
            RegexOptions.CultureInvariant);
        normalizedTitle = Regex.Replace(
            normalizedTitle,
            @"\b(?:v|ver|version|rev|revision|edition|ed|draft|final|current|latest|old|older|previous|archive|archived|legacy|obsolete|ancienne|ancien|precedente|anterieure|ac|a\d{1,2}|pra\d{1,2}|amd\d{0,2}|amdt\d{0,2}|corr(?:igendum)?\d{0,2}|pdf|doc|docx|xlsx|pptx|txt|md)\b",
            " ",
            RegexOptions.CultureInvariant);

        var tokens = normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .Take(12)
            .ToArray();
        if (tokens.Length < 2 || tokens.Sum(static token => token.Length) < 8)
            return false;

        groupKey = "generic:" + string.Join(' ', tokens);
        return true;
    }


    internal static void PruneResidualNavigationalSelectionsWhenContentExists(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || ShouldExplicitlyRequestNavigationArtifacts(query))
            return;

        var hasContentCandidate = selected.Any(static match => IsContentSelectionCandidate(match));
        if (!hasContentCandidate)
            return;

        selected.RemoveAll(static match =>
            !IsResolvedTitleOrNavigationRoute(match)
            && LooksLikeNavigationalChunk(match));
    }


    internal static void PromoteActionableSameDocumentEvidence(List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        for (var index = 0; index < selected.Count - 1; index++)
        {
            var lead = selected[index];
            if (!IsWeakSameDocumentLeadCandidate(lead))
                continue;

            var leadDocKey = BuildSameDocumentPromotionKey(lead);
            if (leadDocKey is null)
                continue;

            var maxIndex = Math.Min(selected.Count - 1, index + 5);
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;
            for (var candidateIndex = index + 1; candidateIndex <= maxIndex; candidateIndex++)
            {
                var candidate = selected[candidateIndex];
                if (!string.Equals(leadDocKey, BuildSameDocumentPromotionKey(candidate), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate.Score < lead.Score - 0.08)
                    continue;
                if (!IsNearbySameDocumentEvidence(lead, candidate))
                    continue;
                if (!IsActionableSameDocumentEvidenceCandidate(lead, candidate))
                    continue;

                var candidateScore = ComputeSameDocumentPromotionScore(lead, candidate);
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore;
                    bestIndex = candidateIndex;
                }
            }

            if (bestIndex <= index)
                continue;

            var promoted = selected[bestIndex];
            selected.RemoveAt(bestIndex);
            selected.Insert(index, promoted);
        }
    }


    internal static string? BuildSameDocumentPromotionKey(RagMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.DocPath))
            return match.DocPath.Trim().Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(match.DocId))
            return match.DocId.Trim();
        return null;
    }


    private static bool IsWeakSameDocumentLeadCandidate(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return false;
        if (IsWeakResolvedRouteTarget(match) || LooksLikeStrongNavigationalChunk(match))
            return true;
        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return true;

        var tokenCount = EstimateNormalizedTokenCount(match.Text);
        if (tokenCount is > 0 and <= 24)
            return true;

        return tokenCount is > 0 and <= 32
            && (LooksLikeNavigationalChunk(match)
                || LooksLikeSourceListChunk(match)
                || LooksLikeGlossaryChunk(match));
    }


    private static bool IsActionableSameDocumentEvidenceCandidate(RagMatch lead, RagMatch candidate)
    {
        if (IsDocumentProfileMatch(candidate))
            return false;
        if (string.Equals(candidate.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsWeakResolvedRouteTarget(candidate))
            return false;
        if (LooksLikeSourceListChunk(candidate) || LooksLikeGlossaryChunk(candidate))
            return false;

        var leadTokenCount = EstimateNormalizedTokenCount(lead.Text);
        var candidateTokenCount = EstimateNormalizedTokenCount(candidate.Text);
        if (candidateTokenCount < Math.Max(24, leadTokenCount * 2))
            return false;

        if (candidateTokenCount >= 48)
            return true;

        return candidateTokenCount >= 24
            && (StructuredContentLexicon.ContainsStructuredAnswerCue(candidate.Text)
                || LooksLikeStructuredAnswerChunk(candidate)
                || ComputeContentEvidencePriority(candidate) >= 0.95);
    }


    private static bool IsNearbySameDocumentEvidence(RagMatch lead, RagMatch candidate)
    {
        if (!lead.PageStart.HasValue || !candidate.PageStart.HasValue)
            return true;

        var leadStart = lead.PageStart.Value;
        var leadEnd = lead.PageEnd ?? leadStart;
        var candidateStart = candidate.PageStart.Value;
        var candidateEnd = candidate.PageEnd ?? candidateStart;
        if (candidateStart <= leadEnd && candidateEnd >= leadStart)
            return true;

        var distance = candidateStart > leadEnd
            ? candidateStart - leadEnd
            : leadStart - candidateEnd;
        return distance <= 3;
    }


    private static double ComputeSameDocumentPromotionScore(RagMatch lead, RagMatch candidate)
    {
        var score = EstimateNormalizedTokenCount(candidate.Text) * 0.01;
        score += Math.Max(0.0, ComputeContentEvidencePriority(candidate)) * 2.0;
        if (IsResolvedTitleOrNavigationRoute(candidate))
            score += 1.0;
        if (candidate.PageStart.HasValue && lead.PageStart.HasValue)
            score -= Math.Abs(candidate.PageStart.Value - lead.PageStart.Value) * 0.05;
        return score;
    }


    internal static bool IsResolvedTitleOrNavigationRoute(RagMatch match)
    {
        var retriever = ResolveRetriever(match);
        if (string.IsNullOrWhiteSpace(match.ChunkId))
            return false;

        if (string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            || string.Equals(retriever, "explicit_document_title_route", StringComparison.Ordinal)
            || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "local_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "standard_reference_document_name", StringComparison.Ordinal)
            || string.Equals(retriever, "reference_version_operand_document_name", StringComparison.Ordinal)
            || string.Equals(retriever, "date_document_name", StringComparison.Ordinal)
            || string.Equals(retriever, "document_status_operand", StringComparison.Ordinal)
            || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal))
        {
            if (string.Equals(retriever, "explicit_document_title_route", StringComparison.Ordinal))
                return true;
            if (string.Equals(retriever, "standard_reference_document_name", StringComparison.Ordinal)
                || string.Equals(retriever, "reference_version_operand_document_name", StringComparison.Ordinal)
                || string.Equals(retriever, "date_document_name", StringComparison.Ordinal)
                || string.Equals(retriever, "document_status_operand", StringComparison.Ordinal))
                return true;

            return string.Equals(retriever, "local_title_token_route", StringComparison.Ordinal)
                || HasProfileTitleHint(match);
        }

        return string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
            && HasProfileTitleHint(match)
            && NavigationRouteHasTargetTitleEvidence(match);
    }


    internal static bool HasResolvedPreciseTitleSelection(IReadOnlyList<string> titlePhrases, RagMatch match)
    {
        if (titlePhrases.Count == 0)
            return false;

        if (IsDirectTitleTokenRouteMatch(match)
            && DirectTitleTokenRouteHasStrongLeadEvidence(match)
            && titlePhrases.Any(phrase =>
            {
                var titleTokens = ExtractTitleScoringTokens(phrase);
                return titleTokens.Count is >= 2 and <= 8
                    && HasStrictTitleAnchorCoverage(titleTokens, match);
            }))
        {
            return true;
        }

        if (!HasQuotedTitlePlacementEvidence(titlePhrases, match))
            return false;

        if (IsResolvedTitleOrNavigationRoute(match))
            return true;

        return IsDirectTitleTokenRouteMatch(match)
            && DirectTitleTokenRouteHasStrongLeadEvidence(match);
    }


    internal static List<RagMatch> BuildReusablePreciseTitleRouteMatches(IReadOnlyList<RagMatch> routeMatches)
    {
        if (routeMatches.Count == 0)
            return [];

        return DeduplicatePreciseTitleBackfillMatches(routeMatches
                .Where(IsReusablePreciseTitleRouteMatch))
            .Take(12)
            .ToList();
    }


    internal static List<RagMatch> BuildReusablePreciseTitleBackfillMatches(
        string query,
        IReadOnlyList<string> titlePhrases,
        IReadOnlyList<RagMatch> routeMatches)
    {
        if (titlePhrases.Count == 0 || routeMatches.Count == 0)
            return [];

        return DeduplicatePreciseTitleBackfillMatches(routeMatches
                .Where(match => ReusablePreciseTitleBackfillContentCarriesTitle(query, titlePhrases, match))
                .Where(match => HasSufficientPreciseContentSelection(query, titlePhrases, [match])))
            .Take(8)
            .ToList();
    }


    internal static bool HasReusablePreciseTitleBackfillSelection(
        string query,
        IReadOnlyList<string> titlePhrases,
        IReadOnlyList<RagMatch> selected,
        IReadOnlyList<RagMatch> reusableBackfill)
    {
        if (titlePhrases.Count == 0 || reusableBackfill.Count == 0)
            return false;

        var candidates = reusableBackfill
            .Concat(selected.Take(8))
            .ToList();
        return HasSufficientPreciseContentSelection(query, titlePhrases, candidates);
    }


    internal static bool ShouldContinuePreciseTitleBackfillAfterReusableSelection(
        string query,
        IReadOnlyList<string> titlePhrases,
        IReadOnlyList<RagMatch> reusableBackfill)
    {
        if (titlePhrases.Count == 0 || reusableBackfill.Count == 0)
            return false;

        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        if (!RequiresReliablePreciseTitlePlacementForStructuredDetails(query, normalizedQuery, titlePhrases))
            return false;

        return titlePhrases.Any(static phrase =>
        {
            var tokens = ExtractTitleScoringTokens(phrase);
            return tokens.Count is >= 2 and <= 4;
        });
    }


    private static bool IsReusablePreciseTitleRouteMatch(RagMatch match)
    {
        var retriever = ResolveRetriever(match);
        if (!string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            && !string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal))
        {
            return false;
        }

        return !IsDocumentProfileMatch(match)
            && !LooksLikeNavigationalChunk(match)
            && !LooksLikeGlossaryChunk(match)
            && !LooksLikeSourceListChunk(match)
            && !IsWeakResolvedRouteTarget(match)
            && IsContentSelectionCandidate(match);
    }


    private static bool ReusablePreciseTitleBackfillContentCarriesTitle(
        string query,
        IReadOnlyList<string> titlePhrases,
        RagMatch match)
    {
        if (IsDirectTitleTokenRouteMatch(match)
            && DirectTitleTokenRouteHasStrongLeadEvidence(match))
        {
            return true;
        }

        var textSignal = string.Join(
            "\n",
            new[] { match.Text, match.SectionTitle, match.HeadingPath }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(textSignal))
            return false;

        foreach (var phrase in titlePhrases)
        {
            if (ReusablePreciseTitleBackfillContentCarriesTitleTokens(phrase, textSignal))
                return true;
        }

        var focusedQuery = BuildFocusedLexicalBackfillQuery(query);
        return ReusablePreciseTitleBackfillContentCarriesTitleTokens(focusedQuery, textSignal);
    }


    private static bool ReusablePreciseTitleBackfillContentCarriesTitleTokens(string phrase, string textSignal)
    {
        var titleTokens = ExtractTitleScoringTokens(phrase);
        if (titleTokens.Count is < 2 or > 8)
            return false;

        var requiredCoverage = titleTokens.Count <= 2
            ? titleTokens.Count
            : Math.Min(3, titleTokens.Count);
        return CountTitleAnchorTokens(titleTokens, textSignal) >= requiredCoverage;
    }


    internal static List<RagMatch> DeduplicatePreciseTitleBackfillMatches(IEnumerable<RagMatch> matches)
        => matches
            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.ChunkIndex)
                .First())
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.ChunkIndex)
            .ToList();


    internal static bool HasSufficientPreciseContentSelection(
        string query,
        IReadOnlyList<string> titlePhrases,
        IReadOnlyList<RagMatch> selected)
    {
        if (titlePhrases.Count == 0 || selected.Count == 0)
            return false;

        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        var requiresStructuredDetails = HasStructuredContinuationBackfillIntent(query)
            || ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalizedQuery);
        var requiresStructuredTitlePlacement = RequiresReliablePreciseTitlePlacementForStructuredDetails(
            query,
            normalizedQuery,
            titlePhrases);
        var documentHintTokens = ExtractDocumentHintTokens(query);
        var hasDocumentHintMatchedContent = documentHintTokens.Count > 0
            && selected.Take(8).Any(match =>
                IsStrongPreciseContentSelectionCandidate(match)
                && SatisfiesRequestedStructuredPreciseDetails(match, normalizedQuery, requiresStructuredDetails)
                && (!requiresStructuredTitlePlacement
                    || HasReliableStructuredPreciseTitlePlacement(titlePhrases, match))
                && DocumentMatchesHint(documentHintTokens, match));

        foreach (var match in selected.Take(3))
        {
            if (!IsStrongPreciseContentSelectionCandidate(match))
                continue;
            if (!SatisfiesRequestedStructuredPreciseDetails(match, normalizedQuery, requiresStructuredDetails))
                continue;
            if (requiresStructuredTitlePlacement
                && !HasReliableStructuredPreciseTitlePlacement(titlePhrases, match))
            {
                continue;
            }
            if (documentHintTokens.Count > 0
                && hasDocumentHintMatchedContent
                && !DocumentMatchesHint(documentHintTokens, match))
            {
                continue;
            }

            if (IsHighConfidenceResolvedPreciseContentRoute(match))
                return true;

            if (!PreciseContentSelectionRespectsDocumentHint(
                query,
                titlePhrases,
                documentHintTokens,
                hasDocumentHintMatchedContent,
                match))
            {
                continue;
            }

            if (HasResolvedPreciseTitleSelection(titlePhrases, match)
                || HasSufficientPreciseSelectionTitleCoverage(query, titlePhrases, match))
            {
                return true;
            }
        }

        return false;
    }


    private static bool RequiresReliablePreciseTitlePlacementForStructuredDetails(
        string query,
        string normalizedQuery,
        IReadOnlyList<string> titlePhrases)
        => titlePhrases.Count > 0
           && (HasStructuredContinuationBackfillIntent(query)
               || ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalizedQuery));


    private static bool HasReliableStructuredPreciseTitlePlacement(
        IReadOnlyList<string> titlePhrases,
        RagMatch match)
    {
        if (IsHighConfidenceResolvedPreciseContentRoute(match))
            return true;
        if (IsDirectTitleTokenRouteMatch(match)
            && DirectTitleTokenRouteHasStrongLeadEvidence(match)
            && ComputeDirectQuotedTitleTokenCoverage(titlePhrases, match) >= 0.75)
        {
            return true;
        }

        return HasQuotedTitlePlacementEvidence(titlePhrases, match);
    }


    private static bool SatisfiesRequestedStructuredPreciseDetails(
        RagMatch match,
        string normalizedQuery,
        bool requiresStructuredDetails)
    {
        if (!requiresStructuredDetails)
            return true;

        var asksInventory = ContainsStructuredInventoryRequest(normalizedQuery);
        var asksProcedure = ContainsStructuredProcedureRequest(normalizedQuery);
        if (!asksInventory && !asksProcedure)
            return true;

        if (asksInventory && !ContainsStructuredInventoryCue(match))
            return false;
        if (asksProcedure && !ContainsStructuredProcedureCue(match) && !LooksLikeStructuredAnswerChunk(match))
            return false;

        return true;
    }


    private static bool IsStrongPreciseContentSelectionCandidate(RagMatch match)
    {
        if (IsDocumentProfileMatch(match)
            || LooksLikeNavigationalChunk(match)
            || LooksLikeGlossaryChunk(match)
            || LooksLikeSourceListChunk(match)
            || IsWeakResolvedRouteTarget(match)
            || !IsContentSelectionCandidate(match)
            || match.Score < 0.70)
        {
            return false;
        }

        return GetStructuredAnswerPriority(match) >= 2
            || ComputeContentEvidencePriority(match) >= 0.95;
    }


    private static bool IsHighConfidenceResolvedPreciseContentRoute(RagMatch match)
    {
        if (IsWeakResolvedRouteTarget(match))
            return false;

        var retriever = ResolveRetriever(match);
        var isResolvedContentRoute =
            string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            || string.Equals(retriever, "local_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal)
            || (IsDirectTitleTokenRouteMatch(match) && DirectTitleTokenRouteHasStrongLeadEvidence(match));
        if (!isResolvedContentRoute)
            return false;

        return match.Score >= 0.82
            && ComputeContentEvidencePriority(match) >= 0.95;
    }


    private static bool PreciseContentSelectionRespectsDocumentHint(
        string query,
        IReadOnlyList<string> titlePhrases,
        IReadOnlyList<string> documentHintTokens,
        bool hasDocumentHintMatchedContent,
        RagMatch match)
    {
        if (documentHintTokens.Count == 0)
            return true;
        if (DocumentMatchesHint(documentHintTokens, match))
            return true;
        if (hasDocumentHintMatchedContent)
            return false;

        return match.Score >= 0.90
            && HasSufficientPreciseSelectionTitleCoverage(query, titlePhrases, match)
            && (HasResolvedPreciseTitleSelection(titlePhrases, match)
                || (IsDirectTitleTokenRouteMatch(match) && DirectTitleTokenRouteHasStrongLeadEvidence(match)));
    }


    private static bool HasSufficientPreciseSelectionTitleCoverage(
        string query,
        IReadOnlyList<string> titlePhrases,
        RagMatch match)
    {
        var titleishSignal = BuildPreciseSelectionTitleishSignal(match);
        if (string.IsNullOrWhiteSpace(titleishSignal))
            return false;

        foreach (var phrase in titlePhrases)
        {
            var titleTokens = ExtractTitleScoringTokens(phrase);
            if (titleTokens.Count is < 2 or > 8)
                continue;

            var requiredTitleishCoverage = Math.Min(2, titleTokens.Count);
            if (HasStrictTitleAnchorCoverage(titleTokens, match)
                && CountTitleAnchorTokens(titleTokens, titleishSignal) >= requiredTitleishCoverage)
            {
                return true;
            }
        }

        var focusedQuery = BuildFocusedLexicalBackfillQuery(query);
        if (string.IsNullOrWhiteSpace(focusedQuery))
            return false;

        var focusedTokens = ExtractTitleScoringTokens(focusedQuery);
        if (focusedTokens.Count is < 2 or > 8)
            return false;

        var requiredFullCoverage = focusedTokens.Count <= 2
            ? focusedTokens.Count
            : Math.Min(3, focusedTokens.Count);
        var requiredTitleishFocusedCoverage = Math.Min(2, requiredFullCoverage);
        return CountTitleAnchorTokens(focusedTokens, GetPreciseTitleSignalText(match)) >= requiredFullCoverage
            && CountTitleAnchorTokens(focusedTokens, titleishSignal) >= requiredTitleishFocusedCoverage;
    }


    private static string BuildPreciseSelectionTitleishSignal(
        RagMatch match,
        bool includeSyntheticRouteTitle = true)
    {
        var parts = new List<string?>(capacity: 7);
        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (includeSyntheticRouteTitle
            && !string.IsNullOrWhiteSpace(routeTitle)
            && (!IsDirectTitleTokenRouteMatch(match) || DirectTitleTokenRouteHasStrongLeadEvidence(match)))
        {
            parts.Add(routeTitle);
        }

        if (match.MatchedContentCards is { Count: > 0 })
        {
            parts.AddRange(match.MatchedContentCards
                .Take(MaxCalibrationCardCount)
                .Select(static card => card.Title));
        }

        parts.Add(match.SectionTitle);
        parts.Add(match.HeadingPath);
        parts.Add(ExtractLeadingTitleishSegment(match.Text));
        parts.Add(ExtractTrailingTitleishSegment(match.Text));

        return string.Join("\n", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }


    internal static IReadOnlyList<string> BuildTitlePlacementTokens(
        IReadOnlyList<string> titleTokens,
        IReadOnlyList<string> documentHintTokens)
    {
        if (titleTokens.Count == 0 || documentHintTokens.Count == 0)
            return titleTokens;

        var subjectTokens = titleTokens
            .Where(token => !TokenMatchesAnyQueryToken(token, documentHintTokens))
            .ToArray();
        return subjectTokens.Length > 0 ? subjectTokens : titleTokens;
    }


    internal static int ComputeTitlePlacementSignal(
        string query,
        IReadOnlyList<string> titleTokens,
        RagMatch match)
    {
        var titleishSignal = BuildPreciseSelectionTitleishSignal(match);
        if (string.IsNullOrWhiteSpace(titleishSignal))
            return 0;

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count > 0)
            return ComputeQuotedTitleRolePlacementSignal(quotedPhrases, match);

        if (titleTokens.Count == 0)
            return 0;

        var normalizedTitleish = NormalizeForLexicalSignal(titleishSignal);
        if (titleTokens.Count >= 2
            && ContainsTitleLikeLexicalSequence(normalizedTitleish, titleTokens))
        {
            return 4;
        }

        if (titleTokens.Count >= 2
            && ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedTitleish, titleTokens, maxGapChars: 60))
        {
            return 3;
        }

        if (titleTokens.Count == 1
            && IsPrimarySpecificLexicalAnchorToken(titleTokens[0])
            && ContainsPrimarySpecificLexicalAnchor(titleTokens, titleishSignal))
        {
            return 2;
        }

        var required = titleTokens.Count <= 2
            ? titleTokens.Count
            : Math.Min(3, titleTokens.Count);
        return CountTitleAnchorTokens(titleTokens, titleishSignal) >= required ? 1 : 0;
    }


    private static int ComputeQuotedTitleRolePlacementSignal(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return 0;

        var titleSignal = string.Join(' ', new[] { match.SectionTitle, match.HeadingPath }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (ComputeQuotedLookupCandidateScore(quotedPhrases, titleSignal) > 0.0)
            return 5;

        var cardSignal = ComputeQuotedMatchedContentCardTitleSignal(quotedPhrases, match);
        if (cardSignal > 0)
            return cardSignal;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        var routeTitleHasReliableCarrier =
            IsResolvedTitleOrNavigationRoute(match)
            || IsDocumentProfileMatch(match)
            || (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && ComputeContentEvidencePriority(match) >= 0.65
                && !LooksLikeNavigationalChunk(match));
        if (!string.IsNullOrWhiteSpace(routeTitle)
            && routeTitleHasReliableCarrier
            && ComputeQuotedLookupCandidateScore(quotedPhrases, routeTitle) > 0.0
            && (!IsDirectTitleTokenRouteMatch(match) || DirectTitleTokenRouteHasStrongLeadEvidence(match)))
        {
            return 4;
        }

        var fuzzyTitleishSignal = ComputeQuotedTitleishTokenPlacementSignal(
            quotedPhrases,
            BuildPreciseSelectionTitleishSignal(
                match,
                includeSyntheticRouteTitle: routeTitleHasReliableCarrier));
        if (fuzzyTitleishSignal > 0)
            return fuzzyTitleishSignal;

        return HasStandaloneQuotedTitlePlacementEvidence(quotedPhrases, match.Text)
            || HasLocalTitleTokenLeadEvidence(match.Text, match.SectionTitle, match.HeadingPath, quotedPhrases)
                ? 3
                : 0;
    }


    private static int ComputeQuotedTitleishTokenPlacementSignal(
        IReadOnlyList<string> quotedPhrases,
        string titleishSignal)
    {
        var normalizedTitleish = NormalizeForLexicalSignal(titleishSignal);
        if (quotedPhrases.Count == 0 || string.IsNullOrWhiteSpace(normalizedTitleish))
            return 0;

        var best = 0;
        foreach (var phrase in quotedPhrases)
        {
            var titleTokens = ExtractTitleScoringTokens(phrase);
            if (titleTokens.Count < 2)
                continue;

            if (ContainsTitleLikeLexicalSequence(normalizedTitleish, titleTokens))
                return 4;

            if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedTitleish, titleTokens, maxGapChars: 60))
            {
                best = Math.Max(best, 3);
                continue;
            }
        }

        return best;
    }


    internal static int ComputeQuotedMatchedContentCardTitleSignal(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0 || match.MatchedContentCards is not { Count: > 0 })
            return 0;

        var best = 0;
        foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
        {
            if (string.IsNullOrWhiteSpace(card.Title) || !LooksLikeConcreteMatchedContentCardTitle(card.Title))
                continue;

            var score = ComputeQuotedLookupCandidateScore(quotedPhrases, card.Title);
            if (score <= 0.0)
                continue;

            best = Math.Max(best, HasExactQuotedPhraseInTitle(quotedPhrases, card.Title) ? 5 : 4);
            if (best >= 5)
                break;
        }

        return best;
    }


    private static bool HasExactQuotedPhraseInTitle(IReadOnlyList<string> quotedPhrases, string title)
    {
        var normalizedTitle = NormalizeForLexicalSignal(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var compactTitle = normalizedTitle.Replace(" ", string.Empty);
        foreach (var phrase in quotedPhrases)
        {
            var normalizedPhrase = NormalizeForLexicalSignal(phrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            if (normalizedTitle.Contains(normalizedPhrase, StringComparison.Ordinal))
                return true;

            var compactPhrase = normalizedPhrase.Replace(" ", string.Empty);
            if (compactPhrase.Length >= 8 && compactTitle.Contains(compactPhrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }


    internal static bool IsWeakStructuredQuotedNavigationMention(
        RagMatch match,
        bool hasReliableStructuredQuotedTitleEvidence,
        bool completeStructuredDirectTokenRoute)
    {
        if (completeStructuredDirectTokenRoute || IsDocumentProfileMatch(match))
            return false;
        if (hasReliableStructuredQuotedTitleEvidence)
        {
            return false;
        }

        if (IsResolvedTitleOrNavigationRoute(match) && ComputeContentEvidencePriority(match) >= 0.70)
            return false;

        var mixedOrNavigationRole =
            string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase)
            || string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase);
        var weakDensity = match.ContentDensityScore is null or < 0.70;
        var navigationish = mixedOrNavigationRole
            || LooksLikeNavigationalChunk(match)
            || (match.NavigationScore is >= 0.55 && weakDensity);

        return navigationish;
    }


    internal static bool IsWeakStructuredQuotedNavigationMentionForTesting(string query, RagMatch match)
    {
        var quotedPhrases = ExtractTitleLookupPhrases(query);
        var normalizedQuery = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        var hasStructuredQuotedTitleDetailIntent = quotedPhrases.Count > 0
            && (ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalizedQuery)
                || HasStructuredContinuationBackfillIntent(query));
        if (!hasStructuredQuotedTitleDetailIntent)
            return false;

        var directQuotedTitleTokenCoverage = ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match);
        var rawWeakDirectTitleTokenRoute = IsDirectTitleTokenRouteMatch(match)
            && !DirectTitleTokenRouteHasStrongLeadEvidence(match);
        var completeStructuredDirectTokenRoute =
            rawWeakDirectTitleTokenRoute
            && directQuotedTitleTokenCoverage >= 0.99
            && (LooksLikeStructuredAnswerChunk(match)
                || (ContainsStructuredInventoryCue(match) && ContainsStructuredProcedureCue(match)))
            && EstimateNormalizedTokenCount(match.Text) >= 60
            && (match.ContentDensityScore is null or >= 0.50);
        var weakDirectTitleTokenRoute = rawWeakDirectTitleTokenRoute
            && !completeStructuredDirectTokenRoute;
        var directChunkTitleSignal = weakDirectTitleTokenRoute
            ? 0
            : ComputeDirectChunkTitleSignal(query, match);
        var titleScoringQuery = BuildFocusedLexicalBackfillQuery(query);
        var titleTokens = ExtractTitleScoringTokens(
            string.IsNullOrWhiteSpace(titleScoringQuery) ? query : titleScoringQuery);
        var titlePlacementSignal = ComputeTitlePlacementSignal(query, titleTokens, match);
        var hasQuotedTitlePlacementEvidence = quotedPhrases.Count > 0
            && HasQuotedTitlePlacementEvidence(quotedPhrases, match);
        var quotedMatchedCardTitleSignal = ComputeQuotedMatchedContentCardTitleSignal(quotedPhrases, match);
        var hasReliableStructuredQuotedTitleEvidence =
            quotedMatchedCardTitleSignal > 0
            || titlePlacementSignal > 0
            || directChunkTitleSignal > 0
            || hasQuotedTitlePlacementEvidence
            || (IsResolvedTitleOrNavigationRoute(match) && !IsWeakResolvedRouteTarget(match));

        return IsWeakStructuredQuotedNavigationMention(
            match,
            hasReliableStructuredQuotedTitleEvidence,
            completeStructuredDirectTokenRoute);
    }


    private const string LeadingTitleishItemizedCuePattern = @"materials?|components?|requirements?|items?|elements?";


    private static readonly Regex LeadingTitleishStructuredCuePattern = new(
        @"\b(?:" + LeadingTitleishItemizedCuePattern + @"|preparation|materiel|equipment|steps?|etapes?|temps\s+total|total\s+time|duration|pour\s+\d+\s+(?:personnes?|pers|p)|for\s+\d+\s+(?:people|items?|units?))\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));


    private static string ExtractLeadingTitleishSegment(string? value)
    {
        var normalized = NormalizeForLexicalSignal(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var cut = Math.Min(normalized.Length, 180);
        var cue = SafeRegexMatch(LeadingTitleishStructuredCuePattern, normalized);
        if (cue.Success && cue.Index >= 4)
            cut = Math.Min(cut, cue.Index);

        return normalized[..cut];
    }


    private const int TrailingTitleishWindowChars = 360;

    private const int TrailingTitleishCandidateChars = 180;


    private static readonly char[] TrailingTitleishQuoteMarkers =
    [
        '\u00ab',
        '\u201c',
        '"'
    ];


    private static string ExtractTrailingTitleishSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var raw = value.Trim();
        if (raw.Length < 260)
            return string.Empty;

        var tailLength = Math.Min(TrailingTitleishWindowChars, raw.Length);
        var tail = raw[^tailLength..];
        var quoted = ExtractTrailingQuotedTitleishSegment(tail);
        if (!string.IsNullOrWhiteSpace(quoted))
            return quoted;

        var suffixLength = Math.Min(TrailingTitleishCandidateChars, tail.Length);
        var suffix = tail[^suffixLength..].Trim();
        return LooksLikeTrailingTitleishCandidate(suffix)
            ? NormalizeForLexicalSignal(suffix)
            : string.Empty;
    }


    private static string ExtractTrailingQuotedTitleishSegment(string tail)
    {
        var quoteIndex = -1;
        foreach (var marker in TrailingTitleishQuoteMarkers)
        {
            quoteIndex = Math.Max(quoteIndex, tail.LastIndexOf(marker));
        }

        if (quoteIndex < 0 || quoteIndex < Math.Max(0, tail.Length - 220))
            return string.Empty;

        var candidate = tail[quoteIndex..].Trim();
        if (candidate.Length > TrailingTitleishCandidateChars)
            candidate = candidate[..TrailingTitleishCandidateChars];

        return LooksLikeTrailingTitleishCandidate(candidate)
            ? NormalizeForLexicalSignal(candidate)
            : string.Empty;
    }


    private static bool LooksLikeTrailingTitleishCandidate(string value)
    {
        var normalized = NormalizeForLexicalSignal(value);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 220)
            return false;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .ToArray();
        return tokens.Length is >= 2 and <= 18
            && HasUppercaseTitleishDensity(value);
    }


    private static bool HasUppercaseTitleishDensity(string value)
    {
        var letters = 0;
        var uppercaseLetters = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetter(ch))
                continue;

            letters++;
            if (char.IsUpper(ch))
                uppercaseLetters++;
        }

        return letters >= 12
            && uppercaseLetters / (double)letters >= 0.55;
    }


    internal static bool IsWeakResolvedRouteTarget(RagMatch match)
    {
        if (!IsResolvedTitleOrNavigationRoute(match))
            return false;
        if (string.Equals(ResolveRetriever(match), "standard_reference_document_name", StringComparison.Ordinal))
            return false;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (!string.IsNullOrWhiteSpace(routeTitle)
            && LooksLikeProceduralFragmentContentCardTitle(routeTitle))
        {
            return true;
        }

        if (IsDirectTitleTokenRouteMatch(match)
            && !DirectTitleTokenRouteHasStrongLeadEvidence(match))
        {
            return true;
        }

        if (IsStrongLocalTitleTokenRouteTarget(match))
            return false;

        if (RouteTitleAppearsOnlyAsTrailingLead(match))
            return true;

        if (ResolvedRouteHasDirectTitlePlacementEvidence(match)
            && LooksLikeStructuredAnswerChunk(match)
            && ComputeContentEvidencePriority(match) >= 0.80)
        {
            return false;
        }

        if (LooksLikeStrongNavigationalChunk(match))
            return true;

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return true;

        if (match.NavigationScore is >= 0.72
            && (match.ContentDensityScore is null or < 0.55))
            return true;

        if (LooksLikeNavigationalChunk(match)
            && ComputeContentEvidencePriority(match) < 0.70)
        {
            return true;
        }

        var tokenCount = EstimateNormalizedTokenCount(match.Text);
        return tokenCount is > 0 and <= 22
            && !LooksLikeStructuredAnswerChunk(match)
            && !StructuredContentLexicon.ContainsStructuredAnswerCue(match.Text);
    }


    private static bool IsStrongLocalTitleTokenRouteTarget(RagMatch match)
    {
        if (!string.Equals(ResolveRetriever(match), "local_title_token_route", StringComparison.Ordinal))
            return false;

        var wasPromotedFromNavigation =
            string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase)
            || string.Equals(match.ChunkType, "section_window_v1", StringComparison.OrdinalIgnoreCase);
        if (!wasPromotedFromNavigation)
            return false;

        var text = match.Text ?? string.Empty;
        if (EstimateNormalizedTokenCount(text) < 36)
            return false;
        if (CountInlinePageNumberBoundaries(text) >= 5)
            return false;
        if (LooksLikeTitleListChunk(text) && !LooksLikeStructuredAnswerText(text))
            return false;

        return LooksLikeStructuredAnswerChunk(match)
            || LooksLikeStructuredAnswerText(text)
            || ComputeContentEvidencePriority(match) >= 0.70;
    }


    private static bool ResolvedRouteHasDirectTitlePlacementEvidence(RagMatch match)
    {
        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
            return false;

        var phrases = new[] { routeTitle };
        return HasStandaloneQuotedTitlePlacementEvidence(phrases, match.Text)
            || HasLocalTitleTokenLeadEvidence(match.Text, match.SectionTitle, match.HeadingPath, phrases);
    }


    private static bool RouteTitleAppearsOnlyAsTrailingLead(RagMatch match)
    {
        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
            return false;

        var normalizedTitle = NormalizeForLexicalSignal(routeTitle);
        var normalizedText = NormalizeForLexicalSignal(GetDirectChunkSignalText(match));
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedText)
            || normalizedText.Length < 80)
        {
            return false;
        }

        var position = normalizedText.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (position < 0)
        {
            var titleTokens = normalizedTitle
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static token => token.Length >= 3)
                .Where(static token => !TitleConnectorTokens.Contains(token))
                .Where(static token => !LexicalStopwords.Contains(token))
                .Where(static token => !PrimaryAnchorStopwords.Contains(token))
                .Take(6)
                .ToArray();
            if (titleTokens.Length < 2)
                return false;

            position = normalizedText.IndexOf(titleTokens[0], StringComparison.Ordinal);
            if (position < 0
                || !ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedText[position..], titleTokens, maxGapChars: 32))
            {
                return false;
            }
        }

        if (HasLocalTitleTokenFooterTitleEvidence(normalizedText, position, normalizedTitle.Length))
            return false;

        return position >= Math.Max(80, (int)Math.Round(normalizedText.Length * 0.65, MidpointRounding.AwayFromZero));
    }


    internal static int EstimateNormalizedTokenCount(string? value)
    {
        var normalized = NormalizeForLexicalSignal(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? 0
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }


    internal static bool IsContentSelectionCandidate(RagMatch match)
        => IsResolvedTitleOrNavigationRoute(match)
            ? !IsWeakResolvedRouteTarget(match)
            : !LooksLikeNavigationalChunk(match);


    internal static void PrioritizeQuotedTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return;

        var ranked = selected
            .Select(match => new
            {
                Match = match,
                QuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedPhrases,
                    GetQuotedLookupSelectionSignalText(match, includeSyntheticRouteTitle: true)),
                LocalQuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedPhrases,
                    GetQuotedLookupSelectionSignalText(match, includeSyntheticRouteTitle: false)),
                DirectQuotedTitleTokenCoverage = ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match),
                ContentEvidencePriority = ComputeContentEvidencePriority(match)
            })
            .Select(item => new
            {
                item.Match,
                QuotedScore = ShouldDiscountSyntheticRouteTitleForQuotedLookup(
                    quotedPhrases,
                    item.Match,
                    HasQuotedTitlePlacementEvidence(quotedPhrases, item.Match),
                    item.DirectQuotedTitleTokenCoverage)
                        ? 0.0
                        : item.QuotedScore,
                item.LocalQuotedScore,
                item.DirectQuotedTitleTokenCoverage,
                item.ContentEvidencePriority,
                ExactQuotedTitlePriority = ComputeExactQuotedTitlePlacementPriority(quotedPhrases, item.Match),
                IsWeakResolvedRoute = IsWeakResolvedRouteTarget(item.Match),
                HasTitlePlacementEvidence = HasQuotedTitlePlacementEvidence(quotedPhrases, item.Match),
                LocalQuotedRankingScore = ComputeQuotedLookupSelectionRankingScore(
                    item.Match,
                    item.LocalQuotedScore,
                    item.ContentEvidencePriority)
            })
            .ToList();

        if (!ranked.Any(static item => item.QuotedScore > 0.0 || item.DirectQuotedTitleTokenCoverage > 0.0))
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.HasTitlePlacementEvidence ? 1 : 0)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenByDescending(static item => item.DirectQuotedTitleTokenCoverage)
            .ThenByDescending(static item => item.IsWeakResolvedRoute ? 0 : 1)
            .ThenByDescending(static item => item.LocalQuotedScore > 0.0 ? 1 : 0)
            .ThenByDescending(static item => item.LocalQuotedRankingScore)
            .ThenByDescending(static item => item.LocalQuotedScore)
            .ThenByDescending(static item => item.ContentEvidencePriority)
            .ThenByDescending(static item => item.QuotedScore)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }


    private static bool ShouldDiscountSyntheticRouteTitleForQuotedLookup(
        IReadOnlyList<string> quotedPhrases,
        RagMatch match,
        bool hasTitlePlacementEvidence,
        double directQuotedTitleTokenCoverage)
    {
        if (quotedPhrases.Count == 0 || directQuotedTitleTokenCoverage >= 0.75)
            return false;
        if (string.IsNullOrWhiteSpace(ExtractMatchedRouteOrProfileTitle(match.EmbedText)))
            return false;

        var retriever = ResolveRetriever(match);
        var isSyntheticRoute =
            string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            || string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
            || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal);
        if (hasTitlePlacementEvidence && !isSyntheticRoute)
            return false;
        return isSyntheticRoute;
    }


    internal static double ComputeDirectQuotedTitleTokenCoverage(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return 0.0;
        if (RouteTitleAppearsOnlyAsTrailingLead(match))
            return 0.0;

        var normalizedSignal = NormalizeForLexicalSignal(GetDirectQuotedTitleSignalText(match));
        if (string.IsNullOrWhiteSpace(normalizedSignal))
            return 0.0;

        var bestCoverage = 0.0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var tokens = ExtractQuotedTitleCoverageTokens(quotedPhrase);
            if (tokens.Length == 0)
                continue;

            var matched = tokens.Count(token => ContainsDirectQuotedTitleToken(normalizedSignal, token));
            bestCoverage = Math.Max(bestCoverage, matched / (double)tokens.Length);
        }

        return bestCoverage;
    }


    private static string GetDirectQuotedTitleSignalText(RagMatch match)
        => string.Join("\n", new[]
        {
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));


    private static string[] ExtractQuotedTitleCoverageTokens(string quotedPhrase)
    {
        var normalizedPhrase = NormalizeForLexicalSignal(quotedPhrase);
        if (string.IsNullOrWhiteSpace(normalizedPhrase))
            return [];

        return normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsQuotedLookupSignalToken)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }


    private static bool ContainsDirectQuotedTitleToken(string normalizedCandidate, string token)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(token))
            return false;

        if (token.All(char.IsDigit))
            return ContainsExactNormalizedToken(normalizedCandidate, token);

        foreach (var variant in BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
        {
            var foldedVariant = FoldDiacritics(variant);
            if (foldedVariant.Length >= 3 && ContainsExactNormalizedToken(normalizedCandidate, foldedVariant))
                return true;
        }

        return false;
    }


    private static double ComputeQuotedLookupSelectionRankingScore(
        RagMatch match,
        double localQuotedScore,
        double contentEvidencePriority)
    {
        if (localQuotedScore <= 0.0)
            return 0.0;

        var score = localQuotedScore + (contentEvidencePriority * 3.0);
        if (IsWeakResolvedRouteTarget(match))
            score -= 18.0;
        if (LooksLikeNavigationalChunk(match))
            score -= 24.0;
        if (LooksLikeSourceListChunk(match) || LooksLikeGlossaryChunk(match))
            score -= 12.0;

        return score;
    }


    private static string GetQuotedLookupSelectionSignalText(RagMatch match, bool includeSyntheticRouteTitle)
    {
        if (IsDocumentProfileMatch(match))
            return BuildDocumentProfileCalibrationSignal(match, includeEmbedFallback: includeSyntheticRouteTitle);

        return string.Join("\n", new[]
        {
            includeSyntheticRouteTitle ? match.EmbedText : StripSyntheticMatchedTitleLine(match.EmbedText),
            match.MatchedContentCards is { Count: > 0 }
                ? string.Join("\n", match.MatchedContentCards.Take(MaxCalibrationCardCount).Select(static card => card.Title))
                : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }


    private static string? StripSyntheticMatchedTitleLine(string? embedText)
    {
        if (string.IsNullOrWhiteSpace(embedText))
            return null;

        if (ExtractMatchedRouteOrProfileTitle(embedText) is null)
            return embedText;

        var lineEnd = embedText.IndexOf('\n', StringComparison.Ordinal);
        if (lineEnd < 0 || lineEnd + 1 >= embedText.Length)
            return null;

        return embedText[(lineEnd + 1)..];
    }


    internal static void PrioritizeDocumentHintSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var documentHintTokens = ExtractDocumentHintTokens(query);
        if (documentHintTokens.Count == 0)
            return;

        var ranked = selected
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                MatchesDocumentHint = !IsDocumentProfileMatch(match)
                    && IsContentSelectionCandidate(match)
                    && DocumentMatchesHint(documentHintTokens, match)
            })
            .ToList();

        if (!ranked.Any(static item => item.MatchesDocumentHint)
            || ranked.All(static item => item.MatchesDocumentHint))
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.MatchesDocumentHint ? 1 : 0)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match));
    }

}
