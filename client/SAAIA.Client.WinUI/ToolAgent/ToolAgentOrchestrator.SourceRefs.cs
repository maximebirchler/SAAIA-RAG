using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static List<ToolMemory.SourceRef> DeriveSourcesFromRagHits(ToolResults toolResults)
    {
        try
        {
            return MergeSourceRefsByPage(EnumerateRagHitSummaries(toolResults)
                    .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
                    .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
                    .Where(static hit => !string.IsNullOrWhiteSpace(hit.DocPath))
                    .Select(BuildSourceRefFromRagHit))
                .Take(8)
                .ToList();
        }
        catch
        {
            return new List<ToolMemory.SourceRef>();
        }
    }

    private static bool HasRagHits(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array
            && hits.GetArrayLength() > 0;
    }

    private static bool HasDocumentContextItems(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && (TryGetBool(result, "found") ?? true)
            && result.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0;
    }

    private static int CountRagHits(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array
            ? hits.GetArrayLength()
            : 0;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromPlanningHits(ToolResults toolResults, string? query = null)
    {
        var sourceLimit = Math.Clamp(Math.Max(8, ResolveSourceBackedPlanningTargetItemCount(query)), 8, 24);
        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
        {
            var supportedDraft = BuildSourceBackedPlanningDraft(
                toolResults,
                language: "fr",
                minItems: ResolveSourceBackedPlanningTargetItemCount(query),
                query);
            return supportedDraft.Sources.Take(sourceLimit).ToList();
        }

        var draft = BuildSourceBackedPlanningDraft(toolResults, language: "fr", minItems: 1, query);
        if (draft.Sources.Count > 0)
            return draft.Sources.Take(sourceLimit).ToList();

        var planningSources = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                sourceLimit)
            .Select(candidate => candidate.Hit)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(sourceLimit)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        if (planningSources.Count > 0)
            return MergeSourceRefsByPagePreservingOrder(planningSources).Take(sourceLimit).ToList();

        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
            ? new List<ToolMemory.SourceRef>()
            : DeriveSourcesFromRagHits(toolResults).Take(sourceLimit).ToList();
    }

    private static string? TryInferDominantTopLevelCategoryScope(ToolResults toolResults, string? query = null)
    {
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return null;

        var grouped = hits
            .Select(hit => new
            {
                Hit = hit,
                TopLevel = ExtractTopLevelCategoryScope(hit)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.TopLevel))
            .GroupBy(static item => item.TopLevel!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Scope = group.Key,
                Count = group.Count(),
                MaxScore = group.Max(static item => item.Hit.Score),
                QueryRelevance = group.Max(item => ComputeRagHitLexicalRelevance(query ?? string.Empty, GetRagHitLookupText(item.Hit)))
            })
            .OrderByDescending(static group => group.Count)
            .ThenByDescending(static group => group.QueryRelevance)
            .ThenByDescending(static group => group.MaxScore)
            .ToList();

        var best = grouped.FirstOrDefault();
        if (best is null)
            return null;

        return best.Count >= 2 || grouped.Count == 1
            ? best.Scope
            : null;
    }

    private static string? ExtractTopLevelCategoryScope(RagHitSummary hit)
    {
        var path = CollapseWhitespace(hit.CategoryPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            path = CollapseWhitespace(hit.DocPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Replace('\\', '/').Trim('/');
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first) || first.Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        return first;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromOptionHits(ToolResults toolResults, string? query = null)
    {
        var optionHits = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems: 1).Items
            .Select(candidate => candidate.Hit)
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return optionHits.Count > 0
            ? optionHits
            : DeriveSourcesFromPlanningHits(toolResults, query);
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromCountdownPlanningHits(ToolResults toolResults, string? query = null)
    {
        var countdownHits = SelectSourceBackedCountdownPlanningCandidates(toolResults, query)
            .Where(candidate => candidate.VisibleMinutes.HasValue)
            .OrderByDescending(candidate => candidate.VisibleMinutes!.Value)
            .ThenByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Hit)
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(5)
            .Select(BuildSourceRefFromRagHit)
            .ToList();

        return countdownHits.Count > 0
            ? countdownHits
            : DeriveSourcesFromExtractiveHits(toolResults, query ?? string.Empty);
    }

    private static ToolMemory.SourceRef BuildSourceRefFromRagHit(RagHitSummary hit)
    {
        var label = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(hit.DocId),
            DocPath = hit.DocPath.Replace('\\', '/'),
            DocName = NullIfWhiteSpace(hit.DocName) ?? NullIfWhiteSpace(Path.GetFileName(hit.DocPath)),
            PageStart = hit.PageStart,
            PageEnd = hit.PageEnd,
            Label = label,
            SourceHash = NullIfWhiteSpace(hit.SourceHash),
            DocLanguage = NullIfWhiteSpace(hit.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(hit.ProfileLanguage),
            Category = NullIfWhiteSpace(hit.Category),
            CategoryRef = NullIfWhiteSpace(hit.CategoryRef),
            CategoryPath = NullIfWhiteSpace(hit.CategoryPath),
            ChunkId = NullIfWhiteSpace(hit.ChunkId),
            SectionTitle = NullIfWhiteSpace(hit.SectionTitle),
            HeadingPath = NullIfWhiteSpace(hit.HeadingPath),
            PrevChunkId = NullIfWhiteSpace(hit.PrevChunkId),
            NextChunkId = NullIfWhiteSpace(hit.NextChunkId),
            SameSectionChunkId = NullIfWhiteSpace(hit.SameSectionChunkId),
            OriginalChunkType = NullIfWhiteSpace(hit.OriginalChunkType),
            OffsetStart = hit.OffsetStart,
            OffsetEnd = hit.OffsetEnd,
            SourceUnitOrdinals = hit.SourceUnitOrdinals?
                .Distinct()
                .OrderBy(static ordinal => ordinal)
                .ToList() ?? new List<int>(),
            SourceUnitStartOrdinal = hit.SourceUnitStartOrdinal,
            SourceUnitEndOrdinal = hit.SourceUnitEndOrdinal,
            SourceUnitCount = hit.SourceUnitCount,
            ChunkComposition = NullIfWhiteSpace(hit.ChunkComposition),
            ExtractionSource = NullIfWhiteSpace(hit.ExtractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(hit.DocumentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(hit.PageQualityStatus),
            TextStatus = NullIfWhiteSpace(hit.TextStatus),
            ChunkTextStatus = NullIfWhiteSpace(hit.ChunkTextStatus),
            ChunkTextSparse = hit.ChunkTextSparse,
            ChunkOcrCandidate = hit.ChunkOcrCandidate,
            QualityStatus = NullIfWhiteSpace(hit.QualityStatus),
            ExtractionConfidence = hit.ExtractionConfidence,
            DocumentExtractionConfidence = hit.DocumentExtractionConfidence,
            PageExtractionConfidence = hit.PageExtractionConfidence,
            ManualReviewRecommended = hit.ManualReviewRecommended,
            DocumentManualReviewRecommended = hit.DocumentManualReviewRecommended,
            PageManualReviewRecommended = hit.PageManualReviewRecommended,
            OcrAttempted = hit.OcrAttempted,
            OcrApplied = hit.OcrApplied,
            OcrRecommended = hit.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(hit.ExtractionDiagnosticSummary),
            QualitySignals = hit.QualitySignals?
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? new List<string>(),
            ChunkQualitySignals = hit.ChunkQualitySignals?
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? new List<string>(),
            MatchedContentCards = hit.MatchedContentCards?
                .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                .Select(static card => new ToolMemory.SourceContentCardRef
                {
                    Title = card.Title,
                    ContentCardId = NullIfWhiteSpace(card.ContentCardId),
                    PageStart = card.PageStart,
                    PageEnd = card.PageEnd,
                    Kind = NullIfWhiteSpace(card.Kind),
                    Signals = card.Signals?
                        .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                        .Select(static signal => signal.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToList() ?? new List<string>(),
                    Evidence = GetSourceContentCardEvidenceElement(card)
                })
                .Take(5)
                .ToList() ?? new List<ToolMemory.SourceContentCardRef>(),
            ProfileSignals = CloneSourceProfileSignalsRef(hit.ProfileSignals),
            SelectionHintEvidenceRole = NullIfWhiteSpace(hit.SelectionHintRole),
            SelectionHintActionabilityScore = hit.SelectionHintActionabilityScore,
            SelectionHintSupportScore = hit.SelectionHintSupportScore,
            SelectionHintFragmentScore = hit.SelectionHintFragmentScore,
            SelectionHintNavigationScore = hit.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = hit.SelectionHintQualityPenalty,
            ContentRole = NullIfWhiteSpace(hit.ContentRole),
            NavigationReason = NullIfWhiteSpace(hit.NavigationReason),
            RetrievalNavigationScore = hit.NavigationScore,
            ContentDensityScore = hit.ContentDensityScore
        };
    }

    private static List<ToolMemory.SourceRef> MergeSourceRefsByPage(IEnumerable<ToolMemory.SourceRef> sources, int maxCardsPerSource = 5)
    {
        var candidates = sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.DocPath))
            .ToList();
        CanonicalizeFilenameOnlySourceRefAliases(candidates);

        return candidates
            .GroupBy(
                BuildSourceRefVisiblePageMergeKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeSourceRefGroup(group, maxCardsPerSource))
            .OrderByDescending(ComputeSourceRefRichness)
            .ToList();
    }

    private static List<ToolMemory.SourceRef> MergeSourceRefsByPagePreservingOrder(IEnumerable<ToolMemory.SourceRef> sources, int maxCardsPerSource = 5)
    {
        var candidates = sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.DocPath))
            .ToList();
        CanonicalizeFilenameOnlySourceRefAliases(candidates);

        var groups = new List<List<ToolMemory.SourceRef>>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in candidates)
        {
            var key = BuildSourceRefVisiblePageMergeKey(source);
            if (!indexByKey.TryGetValue(key, out var index))
            {
                index = groups.Count;
                indexByKey[key] = index;
                groups.Add(new List<ToolMemory.SourceRef>());
            }

            groups[index].Add(source);
        }

        return groups
            .Where(static group => group.Count > 0)
            .Select(group => MergeSourceRefGroup(group, maxCardsPerSource, preserveVisibleIdentity: true))
            .ToList();
    }

    private static List<ToolMemory.SourceRef> NormalizeVisibleSourceRefsForMemory(IEnumerable<ToolMemory.SourceRef>? sources)
        => sources is null
            ? new List<ToolMemory.SourceRef>()
            : MergeSourceRefsByPagePreservingOrder(sources);
}
