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
    private const int MaxSourceBackedEvidenceExplorationPasses = 6;
    private const int MaxSourceBackedLlmEvidenceExplorationPasses = 4;
    private const int MaxSourceBackedLlmEvidenceExplorationRounds = 2;
    private const int MaxSourceBackedLlmEvidenceExplorationQueries = 10;
    private const int MaxSourceBackedAnchorFollowupRounds = 5;
    private const int DefaultSourceBackedDocumentScopedAnchorFollowupLimit = 4;
    private const int BroadSourceBackedDocumentScopedAnchorFollowupLimit = 32;
    private const int MaxInferredSourceBackedNavigationPageSpan = 6;

    private sealed record SourceBackedEvidenceExplorationPass(
        string Label,
        string Purpose,
        string[] Queries,
        string? CategoryScope = null,
        string? DocId = null,
        string? DocPath = null,
        int? PageStart = null,
        int? PageEnd = null,
        string? Origin = null,
        string? DocRef = null,
        string? ChunkId = null,
        string? ToolName = null);

    private sealed record SourceBackedLlmCategoryScopeDecision(
        string? CategoryScope,
        string? Decision,
        string? Confidence,
        string? Reason);

    private sealed record SourceBackedDocumentNavigationFollowupLabel(
        string Label,
        string RawLabel,
        int ScoreHint,
        string? DocId,
        string? DocPath,
        string? CategoryPath,
        int? TargetPageStart = null,
        int? TargetPageEnd = null);

    private sealed record SourceBackedDocumentNavigationSeed(
        string? DocId,
        string? DocPath,
        string? CategoryPath,
        string DisplayName,
        int Score);

    private static IReadOnlyList<SourceBackedDocumentNavigationSeed> SelectSourceBackedDocumentNavigationSeeds(
        ToolResults toolResults,
        int maxDocuments)
    {
        if (maxDocuments <= 0)
            return Array.Empty<SourceBackedDocumentNavigationSeed>();

        var selected = new Dictionary<string, (SourceBackedDocumentNavigationSeed Seed, int Index)>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        void AddSeed(SourceBackedDocumentNavigationSeed seed)
        {
            var key = BuildSourceBackedDocumentNavigationSeedScopeKey(seed);
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (!selected.TryGetValue(key, out var existing)
                || seed.Score > existing.Seed.Score)
            {
                selected[key] = (seed, index);
            }

            index++;
        }

        foreach (var hit in EnumerateRagHitSummaries(toolResults))
        {
            var docId = NullIfWhiteSpace(hit.DocId);
            var docPath = NullIfWhiteSpace(hit.DocPath);
            if (docId is null && docPath is null)
                continue;

            var key = BuildSourceBackedDocumentNavigationSeedScopeKey(docId, docPath);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var displayName = NullIfWhiteSpace(hit.DocName)
                              ?? NullIfWhiteSpace(Path.GetFileName(docPath ?? string.Empty))
                              ?? docId
                              ?? docPath
                              ?? "document";
            var seed = new SourceBackedDocumentNavigationSeed(
                docId,
                docPath,
                NullIfWhiteSpace(hit.CategoryPath) ?? NullIfWhiteSpace(hit.Category) ?? NullIfWhiteSpace(hit.CategoryRef),
                displayName,
                ComputeSourceBackedDocumentNavigationSeedScore(hit));

            AddSeed(seed);
        }

        foreach (var seed in SelectSourceBackedSummaryDocumentNavigationSeeds(toolResults))
        {
            AddSeed(seed);
        }

        return selected.Values
            .OrderByDescending(static item => item.Seed.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Seed)
            .Take(maxDocuments)
            .ToArray();
    }

    private static bool ShouldAttemptSourceBackedContextReadFollowup(
        SourceBackedEvidenceSufficiency analysis,
        string? query,
        ToolResults toolResults)
    {
        if (!UsesSourceBackedPlanningCoverage(query)
            && !LooksLikeGenericCollectionOrListRequest(query)
            && !LooksLikeBroadSourceBackedCompositionRequest(query)
            && !LooksLikeMultipleCandidateSynthesisRequest(query)
            && !LooksLikeUserNeedsSynthesizedDecisionOrPlan(query))
        {
            return false;
        }

        if (analysis.CandidateCount >= Math.Max(1, analysis.MinimumCandidateCount)
            && analysis.UsableHitCount > 0)
        {
            return false;
        }

        return BuildSourceBackedContextReadExplorationPasses(toolResults, query ?? string.Empty, "fr", maxPasses: 1).Count > 0;
    }

    private static IReadOnlyList<SourceBackedEvidenceExplorationPass> BuildSourceBackedContextReadExplorationPasses(
        ToolResults toolResults,
        string? query,
        string language,
        int maxPasses)
    {
        if (maxPasses <= 0)
            return Array.Empty<SourceBackedEvidenceExplorationPass>();

        var ranked = new List<(SourceBackedEvidenceExplorationPass Pass, int Score, int Index)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        var queryTerms = BuildSourceBackedTreeFollowupQueryTerms(query ?? string.Empty).ToArray();
        var requiresStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var dominantTopLevelScope = requiresStructuredPlanning
            ? TryInferDominantTopLevelCategoryScope(toolResults, query)
            : null;
        var alreadyReadPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in EnumerateDocumentContextHitSummaries(toolResults))
        {
            var exactSignature = BuildSourceBackedContextReadPageSignature(hit.DocId, hit.DocPath, hit.PageStart);
            if (!string.IsNullOrWhiteSpace(exactSignature))
                alreadyReadPages.Add(exactSignature);

            var pathOnlySignature = BuildSourceBackedContextReadPageSignature(null, hit.DocPath, hit.PageStart);
            if (!string.IsNullOrWhiteSpace(pathOnlySignature))
                alreadyReadPages.Add(pathOnlySignature);
        }

        void AddPass(
            string label,
            string purpose,
            string? docId,
            string? docPath,
            string? categoryPath,
            string? chunkId,
            int? pageStart,
            int? pageEnd,
            int score)
        {
            docId = NullIfWhiteSpace(docId);
            docPath = NullIfWhiteSpace(docPath);
            chunkId = NullIfWhiteSpace(chunkId);
            pageStart = NormalizeSourceBackedNavigationTargetPageStart(pageStart);
            pageEnd = NormalizeSourceBackedNavigationTargetPageEnd(pageEnd, pageStart);
            if (pageStart.HasValue
                && pageEnd.HasValue
                && pageEnd.Value - pageStart.Value > MaxInferredSourceBackedNavigationPageSpan)
            {
                pageEnd = pageStart.Value + MaxInferredSourceBackedNavigationPageSpan;
            }

            if (string.IsNullOrWhiteSpace(docId)
                && string.IsNullOrWhiteSpace(docPath)
                && string.IsNullOrWhiteSpace(chunkId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(chunkId) && !pageStart.HasValue)
                return;

            if (!SourceBackedExplorationPassMatchesDominantTopLevel(categoryPath, docPath, dominantTopLevelScope))
                return;

            var pass = new SourceBackedEvidenceExplorationPass(
                label,
                purpose,
                Array.Empty<string>(),
                CategoryScope: NullIfWhiteSpace(categoryPath),
                DocId: docId,
                DocPath: docPath,
                PageStart: pageStart,
                PageEnd: pageEnd,
                Origin: "context_followup",
                ChunkId: chunkId,
                ToolName: "documents.context");
            var signature = BuildSourceBackedContextReadPassSignature(pass);
            if (string.IsNullOrWhiteSpace(signature) || !seen.Add(signature))
                return;

            ranked.Add((pass, score, index++));
        }

        foreach (var hit in EnumerateRagHitSummaries(toolResults)
                     .Where(static hit => !string.Equals(hit.Retriever, "documents.context", StringComparison.OrdinalIgnoreCase))
                     .Take(160))
        {
            var score = ComputeSourceBackedContextReadSeedScore(hit, queryTerms);
            if (score <= 0)
                continue;

            AddPass(
                "context_from_rag_anchor",
                "Read indexed chunk context around a retrieved document/page/chunk anchor before judging source support.",
                hit.DocId,
                hit.DocPath,
                NullIfWhiteSpace(hit.CategoryPath) ?? NullIfWhiteSpace(hit.Category) ?? NullIfWhiteSpace(hit.CategoryRef),
                hit.ChunkId,
                hit.PageStart,
                hit.PageEnd,
                score);
        }

        foreach (var item in EnumerateRecentSourceBackedNavigationItems(toolResults, query ?? string.Empty))
        {
            foreach (var label in ExtractDocumentNavigationFollowupLabels(item.Result))
            {
                var pageStart = NormalizeSourceBackedNavigationTargetPageStart(label.TargetPageStart);
                if (!pageStart.HasValue)
                    continue;

                var usableNavigationTitleScores = ExpandTreeNavigationAnchorLabel(label.Label)
                    .Select(CleanNavigationRouteAnchorTitle)
                    .Where(IsUsableSourceBackedOptionTitle)
                    .Where(static title => !LooksLikeNavigationIndexHeadingTitle(title))
                    .Where(static title => !LooksLikeNoisyStructuredPlanningCandidateTitle(title))
                    .Where(static title => !LooksLikeWeakSourceBackedOptionTitle(title))
                    .Where(title => !LooksLikeSubjectlessReferenceNavigationFollowupLabel(label, title, queryTerms))
                    .Select(title => ComputeTreeNavigationAnchorFollowupScore(title, label.RawLabel, queryTerms))
                    .Where(static score => score > 0)
                    .ToArray();
                if (requiresStructuredPlanning && usableNavigationTitleScores.Length == 0)
                    continue;

                AddPass(
                    "context_from_navigation_anchor",
                    "Read indexed chunk context around a resolved navigation/title anchor before using it as evidence.",
                    label.DocId,
                    label.DocPath,
                    label.CategoryPath,
                    null,
                    pageStart,
                    NormalizeSourceBackedNavigationTargetPageEnd(label.TargetPageEnd, pageStart),
                    label.ScoreHint
                    + ComputeSourceBackedNavigationContextSeedScore(label, queryTerms)
                    + Math.Clamp(usableNavigationTitleScores.DefaultIfEmpty(0).Max(), 0, 12));
            }
        }

        var selected = new List<SourceBackedEvidenceExplorationPass>();
        var selectedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ranked
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index))
        {
            var pageSignature = BuildSourceBackedContextReadPageSignature(item.Pass.DocId, item.Pass.DocPath, item.Pass.PageStart);
            var pathOnlyPageSignature = BuildSourceBackedContextReadPageSignature(null, item.Pass.DocPath, item.Pass.PageStart);
            if ((!string.IsNullOrWhiteSpace(pageSignature) && alreadyReadPages.Contains(pageSignature))
                || (!string.IsNullOrWhiteSpace(pathOnlyPageSignature) && alreadyReadPages.Contains(pathOnlyPageSignature))
                || (!string.IsNullOrWhiteSpace(pageSignature) && !selectedPages.Add(pageSignature)))
            {
                continue;
            }

            selected.Add(item.Pass);
            if (selected.Count >= maxPasses)
                break;
        }

        return selected.ToArray();
    }

    private static bool SourceBackedExplorationPassMatchesDominantTopLevel(
        string? categoryPath,
        string? docPath,
        string? dominantTopLevelScope)
    {
        if (string.IsNullOrWhiteSpace(dominantTopLevelScope))
            return true;

        var candidateTopLevel = ExtractTopLevelScopeSegment(categoryPath)
                                ?? ExtractTopLevelScopeSegment(docPath);
        if (string.IsNullOrWhiteSpace(candidateTopLevel))
            return true;

        return string.Equals(candidateTopLevel, dominantTopLevelScope, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTopLevelScopeSegment(string? path)
    {
        var normalized = CollapseWhitespace(path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var first = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first) || first.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        return first;
    }

    private static string BuildSourceBackedContextReadPassSignature(SourceBackedEvidenceExplorationPass pass)
    {
        var doc = NormalizeLooseLookup(pass.DocId);
        if (string.IsNullOrWhiteSpace(doc))
            doc = NormalizeLooseLookup(pass.DocPath);
        var chunk = NormalizeLooseLookup(pass.ChunkId);
        var pageStart = pass.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var pageEnd = pass.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return CollapseWhitespace($"{doc}|chunk:{chunk}|p:{pageStart}-{pageEnd}");
    }

    private static string BuildSourceBackedContextReadPageSignature(string? docId, string? docPath, int? pageStart)
    {
        if (!pageStart.HasValue)
            return string.Empty;

        var doc = NormalizeLooseLookup(docId);
        if (string.IsNullOrWhiteSpace(doc))
            doc = NormalizeLooseLookup(docPath);
        if (string.IsNullOrWhiteSpace(doc))
            return string.Empty;

        return CollapseWhitespace($"{doc}|p:{pageStart.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static int ComputeSourceBackedContextReadSeedScore(
        RagHitSummary hit,
        IReadOnlyList<string> queryTerms)
    {
        if (string.IsNullOrWhiteSpace(hit.DocId)
            && string.IsNullOrWhiteSpace(hit.DocPath)
            && string.IsNullOrWhiteSpace(hit.ChunkId))
        {
            return 0;
        }

        var hasResolvableAnchor = !string.IsNullOrWhiteSpace(hit.ChunkId) || hit.PageStart > 0;
        var score = 1;
        if (!string.IsNullOrWhiteSpace(hit.ChunkId))
            score += 5;
        if (hit.PageStart > 0)
            score += 3;
        if (hit.MatchedContentCards is { Count: > 0 } cards)
            score += Math.Min(10, cards.Count * 2);
        if (hit.SelectionHintActionabilityScore is { } actionability)
            score += Math.Clamp(actionability, 0, 10);
        if (hit.SelectionHintSupportScore is { } support)
            score += Math.Clamp(support, 0, 10) / 2;
        if (hit.SelectionHintFragmentScore is { } fragment)
            score -= Math.Clamp(fragment, 0, 10);
        if (LooksLikeLowSignalContentCandidateHit(hit))
            score -= 4;
        if (LooksLikeNavigationOnlyHit(hit))
            score -= 1;
        if (hit.NavigationScore is >= 0.65)
            score += 2;
        if (hit.ContentDensityScore is >= 0.55)
            score += 2;

        var haystack = NormalizeLexicalLookup(string.Join(
            ' ',
            hit.DocPath,
            hit.DocName,
            hit.SectionTitle ?? string.Empty,
            hit.HeadingPath ?? string.Empty,
            hit.Excerpt));
        if (!string.IsNullOrWhiteSpace(haystack)
            && queryTerms.Any(term => haystack.Contains(NormalizeLexicalLookup(term), StringComparison.Ordinal)))
        {
            score += 4;
        }

        return hasResolvableAnchor && score <= 0 ? 1 : score;
    }

    private static int ComputeSourceBackedNavigationContextSeedScore(
        SourceBackedDocumentNavigationFollowupLabel label,
        IReadOnlyList<string> queryTerms)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(label.DocId) || !string.IsNullOrWhiteSpace(label.DocPath))
            score += 4;
        if (label.TargetPageStart.HasValue)
            score += 4;

        var haystack = NormalizeLexicalLookup(string.Join(
            ' ',
            label.Label,
            label.RawLabel,
            label.DocPath ?? string.Empty,
            label.CategoryPath ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(haystack)
            && queryTerms.Any(term => haystack.Contains(NormalizeLexicalLookup(term), StringComparison.Ordinal)))
        {
            score += 4;
        }

        return score;
    }

    private static IEnumerable<SourceBackedDocumentNavigationSeed> SelectSourceBackedSummaryDocumentNavigationSeeds(
        ToolResults toolResults)
    {
        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "summary.search" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(3))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var sourceElement = TryGetObject(entry, "source") ?? TryGetObject(entry, "Source");
                var docId = NullIfWhiteSpace(TryGetString(entry, "docId") ?? TryGetString(entry, "DocId"))
                            ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docId") ?? TryGetString(sourceElement.Value, "DocId")) : null);
                var docPath = NullIfWhiteSpace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath"))
                              ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docPath") ?? TryGetString(sourceElement.Value, "DocPath")) : null);
                if (docId is null && docPath is null)
                    continue;

                var docName = NullIfWhiteSpace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName"))
                              ?? (sourceElement.HasValue ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "docName") ?? TryGetString(sourceElement.Value, "DocName")) : null)
                              ?? NullIfWhiteSpace(Path.GetFileName(docPath ?? string.Empty))
                              ?? docId
                              ?? docPath
                              ?? "document";
                var categoryPath = NullIfWhiteSpace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath"))
                                   ?? NullIfWhiteSpace(TryGetString(entry, "category") ?? TryGetString(entry, "Category"))
                                   ?? (sourceElement.HasValue
                                       ? NullIfWhiteSpace(TryGetString(sourceElement.Value, "categoryPath") ?? TryGetString(sourceElement.Value, "CategoryPath"))
                                         ?? NullIfWhiteSpace(TryGetString(sourceElement.Value, "category") ?? TryGetString(sourceElement.Value, "Category"))
                                       : null);
                var cards = ExtractRagHitMatchedContentCards(entry)?.Count ?? 0;
                var profileRichness = ComputeSourceProfileSignalsRichness(BuildSourceProfileSignalsRef(entry));
                yield return new SourceBackedDocumentNavigationSeed(
                    docId,
                    docPath,
                    categoryPath,
                    docName,
                    5 + Math.Min(6, cards * 2) + Math.Min(6, profileRichness));
            }
        }
    }

    private static int ComputeSourceBackedDocumentNavigationSeedScore(RagHitSummary hit)
    {
        var score = 0;
        if (hit.ExactMatchHit)
            score += 8;
        if (!LooksLikeLowSignalContentCandidateHit(hit))
            score += 4;
        if (!LooksLikeNavigationOnlyHit(hit))
            score += 2;
        else
            score += 1;
        if (LooksLikeResolvedRouteTargetHit(hit))
            score += 3;
        if (IsRouteDiscoveryAnchorHit(hit) || HasRouteContentCardCue(hit))
            score += 3;
        if (hit.MatchedContentCards is { Count: > 0 } cards)
            score += Math.Min(5, cards.Count);
        if (hit.SelectionHintActionabilityScore is { } actionability)
            score += Math.Clamp(actionability, 0, 10);
        if (hit.SelectionHintSupportScore is { } support)
            score += Math.Clamp(support, 0, 10);
        if (hit.SelectionHintNavigationScore is { } navigation)
            score += Math.Clamp(navigation, 0, 10) / 2;
        if (hit.SelectionHintFragmentScore is { } fragment)
            score -= Math.Clamp(fragment, 0, 10);
        if (hit.NavigationScore is >= 0.65)
            score += 2;
        if (hit.NavigationScore is >= 0.9)
            score += 1;
        if (hit.ManualReviewRecommended || hit.PageManualReviewRecommended || hit.DocumentManualReviewRecommended)
            score -= 2;
        if (hit.OcrRecommended)
            score -= 1;

        return score;
    }

    private static string BuildSourceBackedDocumentNavigationSeedScopeKey(SourceBackedDocumentNavigationSeed seed)
        => BuildSourceBackedDocumentNavigationSeedScopeKey(seed.DocId, seed.DocPath);

    private static string BuildSourceBackedDocumentNavigationSeedScopeKey(string? docId, string? docPath)
    {
        var normalizedDocId = NormalizeLooseLookup(docId);
        if (!string.IsNullOrWhiteSpace(normalizedDocId))
            return $"doc:{normalizedDocId}";

        var normalizedDocPath = NormalizeLooseLookup(docPath);
        return string.IsNullOrWhiteSpace(normalizedDocPath)
            ? string.Empty
            : $"docpath:{normalizedDocPath}";
    }

}
