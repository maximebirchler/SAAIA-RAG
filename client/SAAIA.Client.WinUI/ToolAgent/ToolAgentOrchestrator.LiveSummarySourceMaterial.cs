using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<ToolMemory.SourceRef?> ResolveLiveSummarySourceMetadataAsync(ResolvedDocRef resolved, CancellationToken ct)
    {
        try
        {
            var raw = await _api.SourceResolveAsync(
                string.IsNullOrWhiteSpace(resolved.DocId) ? resolved.DocPath : resolved.DocId,
                null,
                ct).ConfigureAwait(false);
            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.Object)
            {
                var backendSource = TryBuildSourceRefFromJsonElement(source);
                if (backendSource is not null)
                    return MergeSourceResolveMetadata(backendSource, ResolveLiveSummaryFallbackSourceMetadata(resolved));
            }
        }
        catch
        {
        }

        return null;
    }

    private static ToolMemory.SourceRef BuildLiveSummaryFallbackSourceMetadata(ResolvedDocRef resolved)
        => new()
        {
            DocId = NullIfWhiteSpace(resolved.DocId),
            DocPath = (resolved.DocPath ?? string.Empty).Replace('\\', '/'),
            DocName = NullIfWhiteSpace(resolved.DocName),
            PageStart = 1,
            PageEnd = Math.Max(1, resolved.Pages ?? 1),
            Label = string.IsNullOrWhiteSpace(resolved.DocName) ? resolved.DocPath ?? string.Empty : resolved.DocName,
            CategoryRef = NullIfWhiteSpace(resolved.CategoryRef),
            CategoryPath = NullIfWhiteSpace(resolved.CategoryPath) ?? NullIfWhiteSpace(resolved.Category),
            Category = NullIfWhiteSpace(resolved.Category),
            SourceHash = NullIfWhiteSpace(resolved.SourceHash),
            DocLanguage = NullIfWhiteSpace(resolved.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(resolved.ProfileLanguage)
        };

    private ToolMemory.SourceRef? ResolveLiveSummaryFallbackSourceMetadata(ResolvedDocRef resolved)
        => ResolveSourceRef(resolved.DocId)
           ?? ResolveSourceRef(resolved.DocPath)
           ?? ResolveSourceRef(resolved.DocName)
           ?? BuildLiveSummaryFallbackSourceMetadata(resolved);

    private static string ResolveLiveSummaryDocumentLanguage(string requestedDocLanguage, ToolMemory.SourceRef? source)
    {
        var sourceDocLanguage = NormalizeDocumentLanguageTag(source?.DocLanguage);
        if (!string.Equals(sourceDocLanguage, "und", StringComparison.Ordinal))
            return sourceDocLanguage;

        var profileLanguage = NormalizeDocumentLanguageTag(source?.ProfileLanguage);
        if (!string.Equals(profileLanguage, "und", StringComparison.Ordinal))
            return profileLanguage;

        var requested = NormalizeDocumentLanguageTag(requestedDocLanguage);
        return string.Equals(requested, "und", StringComparison.Ordinal) ? "und" : requested;
    }

    private static IEnumerable<SummaryChunk> ExtractSummaryChunksFromDebugScroll(JsonElement raw, string fallbackDocPath, string fallbackDocName, ToolMemory.SourceRef? sourceMetadata)
    {
        if (!raw.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            yield break;
        if (!result.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in points.EnumerateArray())
        {
            if (!item.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                continue;

            var chunkText = (textEl.GetString() ?? string.Empty).Trim();
            if (chunkText.Length == 0)
                continue;

            var docPath = TryGetString(payload, "doc_path") ?? fallbackDocPath;
            var docName = TryGetString(payload, "doc_name") ?? fallbackDocName;
            var pageStart = TryGetInt(payload, "page_start") ?? 1;
            var pageEnd = TryGetInt(payload, "page_end") ?? pageStart;
            var chunkIndex = TryGetInt(payload, "chunk_index") ?? int.MaxValue;
            var chunk = new SummaryChunk(chunkText, Math.Max(1, pageStart), Math.Max(pageStart, pageEnd), chunkIndex, docPath, docName);
            chunk = ApplySourceMetadataToSummaryChunk(chunk, sourceMetadata);
            chunk = ApplySourceMetadataToSummaryChunk(chunk, TryBuildSourceRefFromJsonElement(payload));
            yield return chunk;
        }
    }

    private static SummaryChunk BuildSummaryChunkFromRagItem(SAAIA.Contracts.RagItem item, ResolvedDocRef resolved, ToolMemory.SourceRef? fallbackSource)
    {
        var text = (item.Text ?? string.Empty).Trim();
        var pageStart = Math.Max(1, item.PageStart ?? 1);
        var pageEnd = Math.Max(pageStart, item.PageEnd ?? pageStart);
        var qualityStatus = item.ExtractionQuality?.PageQualityStatus
            ?? item.ExtractionQuality?.DocumentQualityStatus
            ?? fallbackSource?.QualityStatus;
        var confidence = item.ExtractionQuality?.PageExtractionConfidence
            ?? item.ExtractionQuality?.DocumentExtractionConfidence
            ?? fallbackSource?.ExtractionConfidence;
        var manualReview = item.ExtractionQuality?.PageManualReviewRecommended
            ?? item.ExtractionQuality?.DocumentManualReviewRecommended
            ?? fallbackSource?.ManualReviewRecommended
            ?? false;
        var signals = item.ExtractionQuality?.Signals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var cards = item.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new ToolMemory.SourceContentCardRef
            {
                Title = card.Title.Trim(),
                ContentCardId = card.ContentCardId,
                PageStart = card.PageStart,
                PageEnd = card.PageEnd,
                Kind = NullIfWhiteSpace(card.Kind),
                Signals = card.Signals?
                    .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                    .Select(static signal => signal.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList() ?? new List<string>(),
                Evidence = card.Evidence
            })
            .Take(5)
            .ToList();

        return new SummaryChunk(
            text,
            pageStart,
            pageEnd,
            item.ChunkIndex ?? int.MaxValue,
            string.IsNullOrWhiteSpace(item.DocPath) ? resolved.DocPath : item.DocPath!,
            string.IsNullOrWhiteSpace(item.DocName) ? resolved.DocName : item.DocName,
            DocId: item.DocId ?? fallbackSource?.DocId,
            SourceHash: item.SourceHash ?? fallbackSource?.SourceHash,
            DocLanguage: item.DocLanguage ?? fallbackSource?.DocLanguage,
            ProfileLanguage: item.ProfileLanguage ?? fallbackSource?.ProfileLanguage,
            Category: item.Category ?? fallbackSource?.Category,
            CategoryRef: item.CategoryRef ?? fallbackSource?.CategoryRef,
            CategoryPath: item.CategoryPath ?? fallbackSource?.CategoryPath,
            ChunkId: item.ChunkId ?? fallbackSource?.ChunkId,
            SectionTitle: item.Context?.SectionTitle ?? fallbackSource?.SectionTitle,
            HeadingPath: item.Context?.HeadingPath ?? fallbackSource?.HeadingPath,
            PrevChunkId: item.Context?.PrevChunkId ?? fallbackSource?.PrevChunkId,
            NextChunkId: item.Context?.NextChunkId ?? fallbackSource?.NextChunkId,
            SameSectionChunkId: item.Context?.SameSectionChunkId ?? fallbackSource?.SameSectionChunkId,
            OriginalChunkType: item.Context?.OriginalChunkType ?? fallbackSource?.OriginalChunkType,
            OffsetStart: item.ProvenanceInfo?.OffsetStart ?? fallbackSource?.OffsetStart,
            OffsetEnd: item.ProvenanceInfo?.OffsetEnd ?? fallbackSource?.OffsetEnd,
            ExtractionSource: item.ExtractionQuality?.ExtractionSource ?? fallbackSource?.ExtractionSource,
            DocumentQualityStatus: item.ExtractionQuality?.DocumentQualityStatus ?? fallbackSource?.DocumentQualityStatus,
            PageQualityStatus: item.ExtractionQuality?.PageQualityStatus ?? fallbackSource?.PageQualityStatus,
            TextStatus: item.ExtractionQuality?.TextStatus ?? fallbackSource?.TextStatus,
            ChunkTextStatus: item.ExtractionQuality?.ChunkTextStatus ?? fallbackSource?.ChunkTextStatus,
            ChunkTextSparse: item.ExtractionQuality?.ChunkTextSparse ?? fallbackSource?.ChunkTextSparse,
            ChunkOcrCandidate: item.ExtractionQuality?.ChunkOcrCandidate ?? fallbackSource?.ChunkOcrCandidate,
            QualityStatus: qualityStatus,
            ExtractionConfidence: confidence,
            DocumentExtractionConfidence: item.ExtractionQuality?.DocumentExtractionConfidence ?? fallbackSource?.DocumentExtractionConfidence,
            PageExtractionConfidence: item.ExtractionQuality?.PageExtractionConfidence ?? fallbackSource?.PageExtractionConfidence,
            ManualReviewRecommended: manualReview,
            DocumentManualReviewRecommended: item.ExtractionQuality?.DocumentManualReviewRecommended ?? fallbackSource?.DocumentManualReviewRecommended ?? false,
            PageManualReviewRecommended: item.ExtractionQuality?.PageManualReviewRecommended ?? fallbackSource?.PageManualReviewRecommended ?? false,
            OcrAttempted: item.ExtractionQuality?.OcrAttempted ?? fallbackSource?.OcrAttempted ?? false,
            OcrApplied: item.ExtractionQuality?.OcrApplied ?? fallbackSource?.OcrApplied ?? false,
            OcrRecommended: item.ExtractionQuality?.OcrRecommended ?? fallbackSource?.OcrRecommended ?? false,
            ExtractionDiagnosticSummary: BuildSourceExtractionDiagnosticRef(item.ExtractionQuality?.DiagnosticSummary)
                ?? CloneSourceExtractionDiagnostic(fallbackSource?.ExtractionDiagnosticSummary),
            QualitySignals: signals is { Count: > 0 } ? signals : fallbackSource?.QualitySignals,
            ChunkQualitySignals: item.ExtractionQuality?.ChunkQualitySignals is { Count: > 0 } chunkSignals
                ? chunkSignals
                : fallbackSource?.ChunkQualitySignals,
            MatchedContentCards: cards is { Count: > 0 } ? cards : fallbackSource?.MatchedContentCards,
            ProfileSignals: MergeSourceProfileSignals(
            [
                new ToolMemory.SourceRef { ProfileSignals = ConvertProfileSignals(item.ProfileSignals) },
                new ToolMemory.SourceRef { ProfileSignals = fallbackSource?.ProfileSignals }
            ]),
            SelectionHintEvidenceRole: NullIfWhiteSpace(item.SelectionHints?.EvidenceRole) ?? fallbackSource?.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore: item.SelectionHints is not null ? item.SelectionHints.ActionabilityScore : fallbackSource?.SelectionHintActionabilityScore,
            SelectionHintSupportScore: item.SelectionHints is not null ? item.SelectionHints.SupportScore : fallbackSource?.SelectionHintSupportScore,
            SelectionHintFragmentScore: item.SelectionHints is not null ? item.SelectionHints.FragmentScore : fallbackSource?.SelectionHintFragmentScore,
            SelectionHintNavigationScore: item.SelectionHints is not null ? item.SelectionHints.NavigationScore : fallbackSource?.SelectionHintNavigationScore,
            SelectionHintQualityPenalty: item.SelectionHints is not null ? item.SelectionHints.QualityPenalty : fallbackSource?.SelectionHintQualityPenalty,
            ContentRole: NullIfWhiteSpace(item.Context?.ContentRole) ?? fallbackSource?.ContentRole,
            NavigationReason: NullIfWhiteSpace(item.Context?.NavigationReason) ?? fallbackSource?.NavigationReason,
            RetrievalNavigationScore: item.Context?.NavigationScore ?? fallbackSource?.RetrievalNavigationScore,
            ContentDensityScore: item.Context?.ContentDensityScore ?? fallbackSource?.ContentDensityScore);
    }

    private static SummaryChunk ApplySourceMetadataToSummaryChunk(SummaryChunk chunk, ToolMemory.SourceRef? source)
    {
        if (source is null)
            return chunk;

        return chunk with
        {
            DocId = NullIfWhiteSpace(source.DocId) ?? chunk.DocId,
            SourceHash = NullIfWhiteSpace(source.SourceHash) ?? chunk.SourceHash,
            DocLanguage = NullIfWhiteSpace(source.DocLanguage) ?? chunk.DocLanguage,
            ProfileLanguage = NullIfWhiteSpace(source.ProfileLanguage) ?? chunk.ProfileLanguage,
            Category = NullIfWhiteSpace(source.Category) ?? chunk.Category,
            CategoryRef = NullIfWhiteSpace(source.CategoryRef) ?? chunk.CategoryRef,
            CategoryPath = NullIfWhiteSpace(source.CategoryPath) ?? chunk.CategoryPath,
            ChunkId = NullIfWhiteSpace(source.ChunkId) ?? chunk.ChunkId,
            SectionTitle = NullIfWhiteSpace(source.SectionTitle) ?? chunk.SectionTitle,
            HeadingPath = NullIfWhiteSpace(source.HeadingPath) ?? chunk.HeadingPath,
            PrevChunkId = NullIfWhiteSpace(source.PrevChunkId) ?? chunk.PrevChunkId,
            NextChunkId = NullIfWhiteSpace(source.NextChunkId) ?? chunk.NextChunkId,
            SameSectionChunkId = NullIfWhiteSpace(source.SameSectionChunkId) ?? chunk.SameSectionChunkId,
            OriginalChunkType = NullIfWhiteSpace(source.OriginalChunkType) ?? chunk.OriginalChunkType,
            OffsetStart = source.OffsetStart ?? chunk.OffsetStart,
            OffsetEnd = source.OffsetEnd ?? chunk.OffsetEnd,
            ExtractionSource = NullIfWhiteSpace(source.ExtractionSource) ?? chunk.ExtractionSource,
            DocumentQualityStatus = NullIfWhiteSpace(source.DocumentQualityStatus) ?? chunk.DocumentQualityStatus,
            PageQualityStatus = NullIfWhiteSpace(source.PageQualityStatus) ?? chunk.PageQualityStatus,
            TextStatus = NullIfWhiteSpace(source.TextStatus) ?? chunk.TextStatus,
            ChunkTextStatus = NullIfWhiteSpace(source.ChunkTextStatus) ?? chunk.ChunkTextStatus,
            ChunkTextSparse = source.ChunkTextSparse ?? chunk.ChunkTextSparse,
            ChunkOcrCandidate = source.ChunkOcrCandidate ?? chunk.ChunkOcrCandidate,
            QualityStatus = NullIfWhiteSpace(source.QualityStatus) ?? chunk.QualityStatus,
            ExtractionConfidence = source.ExtractionConfidence ?? chunk.ExtractionConfidence,
            DocumentExtractionConfidence = source.DocumentExtractionConfidence ?? chunk.DocumentExtractionConfidence,
            PageExtractionConfidence = source.PageExtractionConfidence ?? chunk.PageExtractionConfidence,
            ManualReviewRecommended = source.ManualReviewRecommended || chunk.ManualReviewRecommended,
            DocumentManualReviewRecommended = source.DocumentManualReviewRecommended || chunk.DocumentManualReviewRecommended,
            PageManualReviewRecommended = source.PageManualReviewRecommended || chunk.PageManualReviewRecommended,
            OcrAttempted = source.OcrAttempted || chunk.OcrAttempted,
            OcrApplied = source.OcrApplied || chunk.OcrApplied,
            OcrRecommended = source.OcrRecommended || chunk.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(source.ExtractionDiagnosticSummary ?? chunk.ExtractionDiagnosticSummary),
            QualitySignals = source.QualitySignals.Count > 0 ? source.QualitySignals : chunk.QualitySignals,
            ChunkQualitySignals = source.ChunkQualitySignals.Count > 0 ? source.ChunkQualitySignals : chunk.ChunkQualitySignals,
            MatchedContentCards = source.MatchedContentCards.Count > 0 ? source.MatchedContentCards : chunk.MatchedContentCards,
            ProfileSignals = MergeSourceProfileSignals(
            [
                new ToolMemory.SourceRef { ProfileSignals = source.ProfileSignals },
                new ToolMemory.SourceRef { ProfileSignals = chunk.ProfileSignals }
            ]),
            SelectionHintEvidenceRole = NullIfWhiteSpace(source.SelectionHintEvidenceRole) ?? chunk.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = source.SelectionHintActionabilityScore ?? chunk.SelectionHintActionabilityScore,
            SelectionHintSupportScore = source.SelectionHintSupportScore ?? chunk.SelectionHintSupportScore,
            SelectionHintFragmentScore = source.SelectionHintFragmentScore ?? chunk.SelectionHintFragmentScore,
            SelectionHintNavigationScore = source.SelectionHintNavigationScore ?? chunk.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = source.SelectionHintQualityPenalty ?? chunk.SelectionHintQualityPenalty,
            ContentRole = NullIfWhiteSpace(source.ContentRole) ?? chunk.ContentRole,
            NavigationReason = NullIfWhiteSpace(source.NavigationReason) ?? chunk.NavigationReason,
            RetrievalNavigationScore = source.RetrievalNavigationScore ?? chunk.RetrievalNavigationScore,
            ContentDensityScore = source.ContentDensityScore ?? chunk.ContentDensityScore
        };
    }

    private static ToolMemory.SourceProfileSignalsRef? ConvertProfileSignals(SAAIA.Contracts.RagItemProfileSignals? profile)
    {
        if (profile is null)
            return null;

        var converted = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = NullIfWhiteSpace(profile.ProfileVersion),
            Language = NullIfWhiteSpace(profile.Language),
            Keywords = NormalizeProfileSignalList(profile.Keywords, 8),
            Entities = NormalizeProfileSignalList(profile.Entities, 8),
            Topics = NormalizeProfileSignalList(profile.Topics, 8),
            HypotheticalQuestions = NormalizeProfileSignalList(profile.HypotheticalQuestions, 4),
            Limits = NormalizeProfileSignalList(profile.Limits, 4),
            MatchedTerms = NormalizeProfileSignalList(profile.MatchedTerms, 12),
            MatchCount = profile.MatchCount
        };

        return ComputeSourceProfileSignalsRichness(converted) == 0 ? null : converted;
    }

    private static List<string> NormalizeProfileSignalList(IEnumerable<string>? values, int maxItems)
        => values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList() ?? new List<string>();
}
