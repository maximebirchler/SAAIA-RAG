using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static ToolMemory.SourceRef MergeSourceRefGroup(
        IEnumerable<ToolMemory.SourceRef> group,
        int maxCardsPerSource,
        bool preserveVisibleIdentity = false)
    {
        var originalSources = group.ToList();
        var sources = originalSources
            .OrderByDescending(ComputeSourceRefRichness)
            .ToList();
        var primary = preserveVisibleIdentity ? originalSources[0] : sources[0];

        return new ToolMemory.SourceRef
        {
            EvidenceId = preserveVisibleIdentity
                ? primary.EvidenceId
                : PickSourceString(sources, static source => source.EvidenceId),
            DocId = PickSourceString(sources, static source => source.DocId),
            DocPath = primary.DocPath,
            DocName = PickSourceString(sources, static source => source.DocName),
            PageStart = primary.PageStart,
            PageEnd = primary.PageEnd,
            Label = primary.Label,
            SourceHash = PickSourceString(sources, static source => source.SourceHash),
            RevisionId = PickSourceString(sources, static source => source.RevisionId),
            DocLanguage = PickSourceString(sources, static source => source.DocLanguage),
            ProfileLanguage = PickSourceString(sources, static source => source.ProfileLanguage),
            Category = PickSourceString(sources, static source => source.Category),
            CategoryRef = PickSourceString(sources, static source => source.CategoryRef),
            CategoryPath = PickSourceString(sources, static source => source.CategoryPath),
            ChunkId = PickSourceString(sources, static source => source.ChunkId),
            AnchorId = PickSourceString(sources, static source => source.AnchorId),
            ContentCardId = PickSourceString(sources, static source => source.ContentCardId),
            SectionTitle = PickSourceString(sources, static source => source.SectionTitle),
            HeadingPath = PickSourceString(sources, static source => source.HeadingPath),
            PrevChunkId = PickSourceString(sources, static source => source.PrevChunkId),
            NextChunkId = PickSourceString(sources, static source => source.NextChunkId),
            SameSectionChunkId = PickSourceString(sources, static source => source.SameSectionChunkId),
            OriginalChunkType = PickSourceString(sources, static source => source.OriginalChunkType),
            OffsetStart = PickSourceInt(sources, static source => source.OffsetStart),
            OffsetEnd = PickSourceInt(sources, static source => source.OffsetEnd),
            SourceUnitOrdinals = PickSourceIntList(sources, static source => source.SourceUnitOrdinals),
            SourceUnitStartOrdinal = PickSourceInt(sources, static source => source.SourceUnitStartOrdinal),
            SourceUnitEndOrdinal = PickSourceInt(sources, static source => source.SourceUnitEndOrdinal),
            SourceUnitCount = PickSourceInt(sources, static source => source.SourceUnitCount),
            ChunkComposition = PickSourceString(sources, static source => source.ChunkComposition),
            ExtractionSource = PickSourceString(sources, static source => source.ExtractionSource),
            DocumentQualityStatus = PickSourceString(sources, static source => source.DocumentQualityStatus),
            PageQualityStatus = PickSourceString(sources, static source => source.PageQualityStatus),
            TextStatus = PickSourceString(sources, static source => source.TextStatus),
            ChunkTextStatus = PickSourceString(sources, static source => source.ChunkTextStatus),
            ChunkTextSparse = PickSourceBool(sources, static source => source.ChunkTextSparse),
            ChunkOcrCandidate = PickSourceBool(sources, static source => source.ChunkOcrCandidate),
            QualityStatus = PickSourceString(sources, static source => source.QualityStatus),
            ExtractionConfidence = PickBestConfidence(sources, static source => source.ExtractionConfidence),
            DocumentExtractionConfidence = PickBestConfidence(sources, static source => source.DocumentExtractionConfidence),
            PageExtractionConfidence = PickBestConfidence(sources, static source => source.PageExtractionConfidence),
            ManualReviewRecommended = sources.Any(static source => source.ManualReviewRecommended),
            DocumentManualReviewRecommended = sources.Any(static source => source.DocumentManualReviewRecommended),
            PageManualReviewRecommended = sources.Any(static source => source.PageManualReviewRecommended),
            OcrAttempted = sources.Any(static source => source.OcrAttempted),
            OcrApplied = sources.Any(static source => source.OcrApplied),
            OcrRecommended = sources.Any(static source => source.OcrRecommended),
            ExtractionDiagnosticSummary = sources
                .Select(static source => CloneSourceExtractionDiagnostic(source.ExtractionDiagnosticSummary))
                .FirstOrDefault(static summary => summary is not null),
            QualitySignals = sources
                .SelectMany(static source => source.QualitySignals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            ChunkQualitySignals = sources
                .SelectMany(static source => source.ChunkQualitySignals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            MatchedContentCards = MergeSourceContentCards(sources, maxCardsPerSource),
            ProfileSignals = MergeSourceProfileSignals(sources),
            SelectionHintEvidenceRole = PickSourceString(sources, static source => source.SelectionHintEvidenceRole),
            SelectionHintActionabilityScore = PickBestScore(sources, static source => source.SelectionHintActionabilityScore),
            SelectionHintSupportScore = PickBestScore(sources, static source => source.SelectionHintSupportScore),
            SelectionHintFragmentScore = PickBestScore(sources, static source => source.SelectionHintFragmentScore),
            SelectionHintNavigationScore = PickBestScore(sources, static source => source.SelectionHintNavigationScore),
            SelectionHintQualityPenalty = PickBestScore(sources, static source => source.SelectionHintQualityPenalty),
            ContentRole = PickBestContentRole(sources),
            NavigationReason = PickSourceString(sources, static source => source.NavigationReason),
            RetrievalNavigationScore = PickLowestDouble(sources, static source => source.RetrievalNavigationScore),
            ContentDensityScore = PickBestConfidence(sources, static source => source.ContentDensityScore)
        };
    }

    private static List<ToolMemory.SourceContentCardRef> MergeSourceContentCards(
        IEnumerable<ToolMemory.SourceRef> sources,
        int maxCards)
        => sources
            .SelectMany(static source => source.MatchedContentCards)
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .GroupBy(BuildSourceContentCardMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => CloneSourceContentCardRef(
                group
                    .OrderByDescending(ComputeSourceContentCardRichness)
                    .First()))
            .OrderByDescending(ComputeSourceContentCardRichness)
            .Take(maxCards)
            .ToList();

    private static ToolMemory.SourceProfileSignalsRef? MergeSourceProfileSignals(IEnumerable<ToolMemory.SourceRef> sources)
    {
        var profiles = sources
            .Select(static source => source.ProfileSignals)
            .Where(static profile => profile is not null)
            .Select(static profile => profile!)
            .OrderByDescending(ComputeSourceProfileSignalsRichness)
            .ToArray();
        if (profiles.Length == 0)
            return null;

        var primary = profiles[0];
        var merged = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = NullIfWhiteSpace(primary.ProfileVersion),
            Language = NullIfWhiteSpace(primary.Language),
            Keywords = MergeProfileSignalList(profiles.Select(static profile => profile.Keywords), 8),
            Entities = MergeProfileSignalList(profiles.Select(static profile => profile.Entities), 8),
            Topics = MergeProfileSignalList(profiles.Select(static profile => profile.Topics), 8),
            HypotheticalQuestions = MergeProfileSignalList(profiles.Select(static profile => profile.HypotheticalQuestions), 4),
            Limits = MergeProfileSignalList(profiles.Select(static profile => profile.Limits), 4),
            MatchedTerms = MergeProfileSignalList(profiles.Select(static profile => profile.MatchedTerms), 12),
            MatchCount = profiles
                .Select(static profile => profile.MatchCount)
                .Where(static count => count.HasValue)
                .Select(static count => count!.Value)
                .DefaultIfEmpty()
                .Max()
        };

        return ComputeSourceProfileSignalsRichness(merged) == 0 ? null : merged;
    }

    private static List<string> MergeProfileSignalList(IEnumerable<IEnumerable<string>> lists, int maxItems)
        => lists
            .SelectMany(static list => list)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CompactClientProfileSignalValue)
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList();

    private static string CompactClientProfileSignalValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160].TrimEnd();
    }

    private static ToolMemory.SourceContentCardRef CloneSourceContentCardRef(ToolMemory.SourceContentCardRef card)
        => new()
        {
            Title = card.Title,
            ContentCardId = NullIfWhiteSpace(card.ContentCardId),
            PageStart = card.PageStart,
            PageEnd = card.PageEnd,
            Kind = NullIfWhiteSpace(card.Kind),
            Signals = card.Signals
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            Evidence = CloneNullableJsonElement(card.Evidence)
        };

    private static ToolMemory.SourceProfileSignalsRef? CloneSourceProfileSignalsRef(ToolMemory.SourceProfileSignalsRef? profile)
    {
        if (profile is null)
            return null;

        var clone = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = NullIfWhiteSpace(profile.ProfileVersion),
            Language = NullIfWhiteSpace(profile.Language),
            Keywords = CompactClientProfileSignalList(profile.Keywords, 8),
            Entities = CompactClientProfileSignalList(profile.Entities, 8),
            Topics = CompactClientProfileSignalList(profile.Topics, 8),
            HypotheticalQuestions = CompactClientProfileSignalList(profile.HypotheticalQuestions, 4),
            Limits = CompactClientProfileSignalList(profile.Limits, 4),
            MatchedTerms = CompactClientProfileSignalList(profile.MatchedTerms, 12),
            MatchCount = profile.MatchCount
        };

        return ComputeSourceProfileSignalsRichness(clone) == 0 ? null : clone;
    }

    private static List<string> CompactClientProfileSignalList(IEnumerable<string> values, int maxItems)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CompactClientProfileSignalValue)
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList();

    private static JsonElement? GetSourceContentCardEvidenceElement(RagHitContentCardSummary card)
    {
        if (card.RawEvidence is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } raw)
            return raw.Clone();

        return card.Evidence is null
            ? null
            : JsonSerializer.SerializeToElement(card.Evidence, ClientJson.CamelCase);
    }

    private static JsonElement? CloneNullableJsonElement(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
            ? element.Clone()
            : null;

    private static ToolMemory.SourceProfileSignalsRef? BuildSourceProfileSignalsRef(JsonElement source)
    {
        var profile = TryGetObject(source, "profileSignals")
                      ?? TryGetObject(source, "profile_signals")
                      ?? TryGetObject(source, "ProfileSignals");
        if (profile is null)
            return null;

        var value = profile.Value;
        var result = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = NullIfWhiteSpace(TryGetString(value, "profileVersion") ?? TryGetString(value, "profile_version") ?? TryGetString(value, "ProfileVersion")),
            Language = NullIfWhiteSpace(TryGetString(value, "language") ?? TryGetString(value, "Language") ?? TryGetString(value, "profileLanguage") ?? TryGetString(value, "ProfileLanguage")),
            Keywords = ExtractProfileSignalList(value, "keywords", "keywordMatches", "Keywords", "KeywordMatches", maxItems: 8),
            Entities = ExtractProfileSignalList(value, "entities", "entityMatches", "Entities", "EntityMatches", maxItems: 8),
            Topics = ExtractProfileSignalList(value, "topics", "topicMatches", "Topics", "TopicMatches", maxItems: 8),
            HypotheticalQuestions = ExtractProfileSignalList(value, "hypotheticalQuestions", "hypothetical_questions", "HypotheticalQuestions", maxItems: 4),
            Limits = ExtractProfileSignalList(value, "limits", "limitMatches", "Limits", "LimitMatches", maxItems: 4),
            MatchedTerms = ExtractProfileSignalList(value, "matchedTerms", "matched_terms", "MatchedTerms", maxItems: 12),
            MatchCount = TryGetInt(value, "matchCount") ?? TryGetInt(value, "match_count") ?? TryGetInt(value, "MatchCount")
        };

        return ComputeSourceProfileSignalsRichness(result) == 0 ? null : result;
    }

    private static List<string> ExtractProfileSignalList(JsonElement source, string primaryName, string secondaryName, string thirdName, int maxItems)
        => ExtractProfileSignalList(source, primaryName, secondaryName, thirdName, null, maxItems);

    private static List<string> ExtractProfileSignalList(
        JsonElement source,
        string primaryName,
        string secondaryName,
        string thirdName,
        string? fourthName,
        int maxItems)
    {
        var values = ExtractCompactSignals(source, primaryName)
            .Concat(ExtractCompactSignals(source, secondaryName))
            .Concat(ExtractCompactSignals(source, thirdName));
        if (!string.IsNullOrWhiteSpace(fourthName))
            values = values.Concat(ExtractCompactSignals(source, fourthName));

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CompactClientProfileSignalValue)
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList();
    }

    private static string BuildSourceContentCardMergeKey(ToolMemory.SourceContentCardRef card)
    {
        var id = NullIfWhiteSpace(card.ContentCardId);
        if (!string.IsNullOrWhiteSpace(id))
            return $"id:{id}";

        return string.Join(
            "|",
            "shape",
            CollapseWhitespace(card.Title).ToLowerInvariant(),
            CollapseWhitespace(card.Kind ?? string.Empty).ToLowerInvariant(),
            card.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            card.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static int ComputeSourceRefRichness(ToolMemory.SourceRef source)
        => (source.MatchedContentCards.Count * 12)
           + (source.MatchedContentCards.Count(static card => card.Evidence is not null) * 6)
           + (ComputeSourceProfileSignalsRichness(source.ProfileSignals) * 2)
           + (source.QualitySignals.Count * 2)
           + (source.ChunkQualitySignals.Count * 2)
           + (!string.IsNullOrWhiteSpace(source.SourceHash) ? 3 : 0)
           + (!string.IsNullOrWhiteSpace(source.DocLanguage) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.ProfileLanguage) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.Category) ? 1 : 0)
           + (!string.IsNullOrWhiteSpace(source.ChunkId) ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.SectionTitle) ? 1 : 0)
           + (!string.IsNullOrWhiteSpace(source.HeadingPath) ? 1 : 0)
           + (source.OffsetStart.HasValue ? 1 : 0)
           + (source.OffsetEnd.HasValue ? 1 : 0)
           + (!string.IsNullOrWhiteSpace(source.ContentRole) ? 2 : 0)
           + (source.ContentDensityScore.HasValue ? 1 : 0)
           + (source.SourceUnitOrdinals.Count > 0 ? 2 : 0)
           + (source.SourceUnitCount.HasValue ? 1 : 0)
           + (!string.IsNullOrWhiteSpace(source.ChunkComposition) ? 1 : 0)
           + (source.ExtractionDiagnosticSummary is not null ? 2 : 0)
           + (!string.IsNullOrWhiteSpace(source.ChunkTextStatus) ? 2 : 0)
           + (source.ChunkTextSparse.HasValue ? 1 : 0)
           + (source.ChunkOcrCandidate.HasValue ? 1 : 0)
           + (source.SelectionHintActionabilityScore ?? 0)
           + (source.SelectionHintSupportScore ?? 0)
           - (source.SelectionHintNavigationScore ?? 0);

    private static int ComputeSourceProfileSignalsRichness(ToolMemory.SourceProfileSignalsRef? profile)
        => profile is null
            ? 0
            : (string.IsNullOrWhiteSpace(profile.ProfileVersion) ? 0 : 1)
              + (string.IsNullOrWhiteSpace(profile.Language) ? 0 : 1)
              + profile.Keywords.Count
              + profile.Entities.Count
              + profile.Topics.Count
              + profile.HypotheticalQuestions.Count
              + profile.Limits.Count
              + profile.MatchedTerms.Count
              + (profile.MatchCount is > 0 ? 1 : 0);

    private static int ComputeSourceContentCardRichness(ToolMemory.SourceContentCardRef card)
        => (!string.IsNullOrWhiteSpace(card.ContentCardId) ? 8 : 0)
           + (card.Evidence is not null ? 8 : 0)
           + (card.Signals.Count * 2)
           + (card.PageStart.HasValue ? 1 : 0)
           + (card.PageEnd.HasValue ? 1 : 0);

    private static string? PickSourceString(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, string?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static double? PickBestConfidence(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, double?> selector)
        => sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty()
            .Max() is var value && value > 0.0
                ? value
                : null;

    private static int? PickBestScore(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, int?> selector)
        => sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty()
            .Max() is var value && value != 0
                ? value
                : null;

    private static int? PickSourceInt(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, int?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => value.HasValue);

    private static List<int> PickSourceIntList(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, List<int>> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static values => values.Count > 0)?
            .Distinct()
            .OrderBy(static value => value)
            .ToList() ?? new List<int>();

    private static bool? PickSourceBool(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, bool?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => value.HasValue);

    private static double? PickLowestDouble(
        IEnumerable<ToolMemory.SourceRef> sources,
        Func<ToolMemory.SourceRef, double?> selector)
    {
        var values = sources
            .Select(selector)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static string? PickBestContentRole(IEnumerable<ToolMemory.SourceRef> sources)
    {
        var roles = sources
            .Select(static source => NullIfWhiteSpace(source.ContentRole))
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .ToArray();
        if (roles.Length == 0)
            return null;

        if (roles.Any(static role => string.Equals(role, "content", StringComparison.OrdinalIgnoreCase)))
            return "content";
        if (roles.Any(static role => string.Equals(role, "mixed_navigation_content", StringComparison.OrdinalIgnoreCase)))
            return "mixed_navigation_content";
        return roles[0];
    }

    private static object BuildSourcesPayload(List<ToolMemory.SourceRef> sources)
    {
        var mergedSources = MergeSourceRefsByPagePreservingOrder(sources);
        return new
        {
            sources = mergedSources.Select(x => new
            {
                evidenceId = x.EvidenceId,
                docId = x.DocId,
                docPath = x.DocPath,
                docName = x.DocName,
                pageStart = x.PageStart,
                pageEnd = x.PageEnd,
                label = x.Label,
                sourceHash = x.SourceHash,
                revisionId = x.RevisionId,
                docLanguage = x.DocLanguage,
                profileLanguage = x.ProfileLanguage,
                category = x.Category,
                categoryRef = x.CategoryRef,
                categoryPath = x.CategoryPath,
                chunkId = x.ChunkId,
                anchorId = x.AnchorId,
                contentCardId = x.ContentCardId,
                sectionTitle = x.SectionTitle,
                headingPath = x.HeadingPath,
                prevChunkId = x.PrevChunkId,
                nextChunkId = x.NextChunkId,
                sameSectionChunkId = x.SameSectionChunkId,
                originalChunkType = x.OriginalChunkType,
                provenanceInfo = BuildSourceProvenancePayload(x),
                contentSignals = BuildSourceContentSignalsPayload(x),
                extractionQuality = BuildSourceExtractionQualityPayload(x),
                matchedContentCards = BuildSourceContentCardsPayload(x),
                profileSignals = BuildSourceProfileSignalsPayload(x),
                selectionHints = BuildSourceSelectionHintsPayload(x)
            }).ToList()
        };
    }

    private static object BuildSourcesPayload(string intent, List<ToolMemory.SourceRef> sources)
        => new
        {
            intent,
            sources = BuildSourcePayloadItems(sources)
        };

    private static object BuildSourcesPayload(
        string intent,
        List<ToolMemory.SourceRef> sources,
        SourceBackedConversationTurnMemory? conversationMemory)
        => new
        {
            intent,
            sources = BuildSourcePayloadItems(sources),
            conversationMemory
        };

    private static List<object> BuildSourcePayloadItems(List<ToolMemory.SourceRef> sources)
        => MergeSourceRefsByPagePreservingOrder(sources).Select(x => new
        {
            evidenceId = x.EvidenceId,
            docId = x.DocId,
            docPath = x.DocPath,
            docName = x.DocName,
            pageStart = x.PageStart,
            pageEnd = x.PageEnd,
            label = x.Label,
            sourceHash = x.SourceHash,
            revisionId = x.RevisionId,
            docLanguage = x.DocLanguage,
            profileLanguage = x.ProfileLanguage,
            category = x.Category,
            categoryRef = x.CategoryRef,
            categoryPath = x.CategoryPath,
            chunkId = x.ChunkId,
            anchorId = x.AnchorId,
            contentCardId = x.ContentCardId,
            sectionTitle = x.SectionTitle,
            headingPath = x.HeadingPath,
            prevChunkId = x.PrevChunkId,
            nextChunkId = x.NextChunkId,
            sameSectionChunkId = x.SameSectionChunkId,
            originalChunkType = x.OriginalChunkType,
            provenanceInfo = BuildSourceProvenancePayload(x),
            contentSignals = BuildSourceContentSignalsPayload(x),
            extractionQuality = BuildSourceExtractionQualityPayload(x),
            matchedContentCards = BuildSourceContentCardsPayload(x),
            profileSignals = BuildSourceProfileSignalsPayload(x),
            selectionHints = BuildSourceSelectionHintsPayload(x)
        }).Cast<object>().ToList();

    private static object? BuildSourceContentSignalsPayload(ToolMemory.SourceRef source)
    {
        var hasCompositionSignals = source.SourceUnitOrdinals.Count > 0
                                    || source.SourceUnitStartOrdinal.HasValue
                                    || source.SourceUnitEndOrdinal.HasValue
                                    || source.SourceUnitCount.HasValue
                                    || !string.IsNullOrWhiteSpace(source.ChunkComposition);
        if (string.IsNullOrWhiteSpace(source.ContentRole)
            && string.IsNullOrWhiteSpace(source.NavigationReason)
            && source.RetrievalNavigationScore is null
            && source.ContentDensityScore is null
            && !hasCompositionSignals)
        {
            return null;
        }

        return new
        {
            contentRole = source.ContentRole,
            navigationReason = source.NavigationReason,
            navigationScore = source.RetrievalNavigationScore,
            contentDensityScore = source.ContentDensityScore,
            sourceUnitOrdinals = source.SourceUnitOrdinals.Count == 0 ? null : source.SourceUnitOrdinals,
            sourceUnitStartOrdinal = source.SourceUnitStartOrdinal,
            sourceUnitEndOrdinal = source.SourceUnitEndOrdinal,
            sourceUnitCount = source.SourceUnitCount,
            chunkComposition = source.ChunkComposition
        };
    }

    private static object? BuildSourceProvenancePayload(ToolMemory.SourceRef source)
        => source.OffsetStart is null && source.OffsetEnd is null
            ? null
            : new
            {
                offsetStart = source.OffsetStart,
                offsetEnd = source.OffsetEnd
            };

    private static object? BuildSourceSelectionHintsPayload(ToolMemory.SourceRef source)
        => string.IsNullOrWhiteSpace(source.SelectionHintEvidenceRole)
           && source.SelectionHintActionabilityScore is null
           && source.SelectionHintSupportScore is null
           && source.SelectionHintFragmentScore is null
           && source.SelectionHintNavigationScore is null
           && source.SelectionHintQualityPenalty is null
            ? null
            : new
            {
                evidenceRole = source.SelectionHintEvidenceRole,
                actionabilityScore = source.SelectionHintActionabilityScore,
                supportScore = source.SelectionHintSupportScore,
                fragmentScore = source.SelectionHintFragmentScore,
                navigationScore = source.SelectionHintNavigationScore,
                qualityPenalty = source.SelectionHintQualityPenalty
            };

    private static object? BuildSourceProfileSignalsPayload(ToolMemory.SourceRef source)
    {
        var profile = source.ProfileSignals;
        if (profile is null || ComputeSourceProfileSignalsRichness(profile) == 0)
            return null;

        return new
        {
            profileVersion = profile.ProfileVersion,
            language = profile.Language,
            keywords = profile.Keywords.Count == 0 ? null : profile.Keywords,
            entities = profile.Entities.Count == 0 ? null : profile.Entities,
            topics = profile.Topics.Count == 0 ? null : profile.Topics,
            hypotheticalQuestions = profile.HypotheticalQuestions.Count == 0 ? null : profile.HypotheticalQuestions,
            limits = profile.Limits.Count == 0 ? null : profile.Limits,
            matchedTerms = profile.MatchedTerms.Count == 0 ? null : profile.MatchedTerms,
            matchCount = profile.MatchCount
        };
    }

    private static object? BuildSourceExtractionQualityPayload(ToolMemory.SourceRef source)
    {
        var diagnosticSummary = BuildSourceExtractionDiagnosticPayload(source.ExtractionDiagnosticSummary);
        if (string.IsNullOrWhiteSpace(source.QualityStatus)
            && string.IsNullOrWhiteSpace(source.ExtractionSource)
            && string.IsNullOrWhiteSpace(source.DocumentQualityStatus)
            && string.IsNullOrWhiteSpace(source.PageQualityStatus)
            && string.IsNullOrWhiteSpace(source.TextStatus)
            && string.IsNullOrWhiteSpace(source.ChunkTextStatus)
            && source.ChunkTextSparse is null
            && source.ChunkOcrCandidate is null
            && source.ExtractionConfidence is null
            && source.DocumentExtractionConfidence is null
            && source.PageExtractionConfidence is null
            && !source.ManualReviewRecommended
            && !source.DocumentManualReviewRecommended
            && !source.PageManualReviewRecommended
            && !source.OcrAttempted
            && !source.OcrApplied
            && !source.OcrRecommended
            && source.QualitySignals.Count == 0
            && source.ChunkQualitySignals.Count == 0
            && diagnosticSummary is null)
        {
            return null;
        }

        return new
        {
            extractionSource = source.ExtractionSource,
            documentQualityStatus = source.DocumentQualityStatus,
            pageQualityStatus = source.PageQualityStatus,
            textStatus = source.TextStatus,
            chunkTextStatus = source.ChunkTextStatus,
            chunkTextSparse = source.ChunkTextSparse,
            chunkOcrCandidate = source.ChunkOcrCandidate,
            qualityStatus = source.QualityStatus,
            extractionConfidence = source.ExtractionConfidence,
            documentExtractionConfidence = source.DocumentExtractionConfidence,
            pageExtractionConfidence = source.PageExtractionConfidence,
            manualReviewRecommended = source.ManualReviewRecommended,
            documentManualReviewRecommended = source.DocumentManualReviewRecommended,
            pageManualReviewRecommended = source.PageManualReviewRecommended,
            ocrAttempted = source.OcrAttempted,
            ocrApplied = source.OcrApplied,
            ocrRecommended = source.OcrRecommended,
            signals = source.QualitySignals.Count == 0 ? null : source.QualitySignals,
            chunkQualitySignals = source.ChunkQualitySignals.Count == 0 ? null : source.ChunkQualitySignals,
            diagnosticSummary
        };
    }

    private static object? BuildSourceExtractionDiagnosticPayload(ToolMemory.SourceExtractionDiagnosticRef? summary)
    {
        if (summary is null)
            return null;

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nativeTextStatus"] = NullIfWhiteSpace(summary.NativeTextStatus),
            ["nativeOcrRecommended"] = summary.NativeOcrRecommended,
            ["ocrMode"] = NullIfWhiteSpace(summary.OcrMode),
            ["ocrLanguages"] = NullIfWhiteSpace(summary.OcrLanguages),
            ["ocrDurationMs"] = summary.OcrDurationMs,
            ["ocrFailureReason"] = NullIfWhiteSpace(summary.OcrFailureReason),
            ["ocrAppliedReason"] = NullIfWhiteSpace(summary.OcrAppliedReason),
            ["ocrTimedOut"] = summary.OcrTimedOut,
            ["ocrAttemptedPageCount"] = summary.OcrAttemptedPageCount,
            ["ocrSkippedPageCount"] = summary.OcrSkippedPageCount,
            ["ocrPagesWithNovelTextCount"] = summary.OcrPagesWithNovelTextCount,
            ["pageCount"] = summary.PageCount,
            ["textPageCount"] = summary.TextPageCount,
            ["emptyPageCount"] = summary.EmptyPageCount,
            ["sparsePageCount"] = summary.SparsePageCount,
            ["imagePageCount"] = summary.ImagePageCount,
            ["pageWarningCount"] = summary.PageWarningCount,
            ["pageReviewRecommendedCount"] = summary.PageReviewRecommendedCount,
            ["retrievalChunkQuality"] = BuildSourceRetrievalChunkQualityPayload(summary.RetrievalChunkQuality)
        };

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? BuildSourceRetrievalChunkQualityPayload(ToolMemory.SourceRetrievalChunkQualityRef? summary)
    {
        if (summary is null)
            return null;

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["totalChunkCount"] = summary.TotalChunkCount,
            ["searchableChunkCount"] = summary.SearchableChunkCount,
            ["rejectedChunkCount"] = summary.RejectedChunkCount,
            ["manualReviewRecommended"] = summary.ManualReviewRecommended,
            ["rejectionReasons"] = summary.RejectionReasons.Count == 0 ? null : summary.RejectionReasons
        };

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? BuildSourceContentCardsPayload(ToolMemory.SourceRef source)
        => source.MatchedContentCards.Count == 0
            ? null
            : source.MatchedContentCards.Select(static card => new
            {
                title = card.Title,
                contentCardId = card.ContentCardId,
                pageStart = card.PageStart,
                pageEnd = card.PageEnd,
                kind = card.Kind,
                signals = card.Signals,
                evidence = card.Evidence
            }).ToList();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
