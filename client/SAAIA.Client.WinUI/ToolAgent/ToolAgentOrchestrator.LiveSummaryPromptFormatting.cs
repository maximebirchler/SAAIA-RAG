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
    private static string BuildLiveSummaryPrompt(
        ResolvedDocRef doc,
        List<SummaryChunk> chunks,
        string language,
        string level,
        int maxWords,
        string strategy,
        string docLanguage,
        IReadOnlyList<ToolMemory.SourceRef> sourceMetadata)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.DocName}");
        sb.AppendLine($"Path: {doc.DocPath}");
        sb.AppendLine($"DocumentLanguage: {NormalizeDocumentLanguageTag(docLanguage)}");
        sb.AppendLine($"TargetLanguage: {language}");
        sb.AppendLine($"Level: {level}");
        sb.AppendLine($"Strategy: {strategy}");
        sb.AppendLine($"MaxWords: {maxWords}");
        AppendLiveSummarySourceMetadataPrompt(sb, sourceMetadata);
        sb.AppendLine();
        sb.AppendLine("Summarize only this document. Give concrete information from the document itself, not just metadata such as path, category or dates.");

        if (strategy == "about")
        {
            sb.AppendLine("Goal: answer the question 'what is this document about?' in 2 to 4 short sentences maximum.");
            sb.AppendLine("Keep only the main purpose, the main topics and the most useful concrete elements found in the chunks. No long explanation, no bullets, no metadata.");
        }
        else if (strategy == "store")
        {
            sb.AppendLine("Goal: produce a clean reusable summary that can be stored and shown later to standard users.");
            sb.AppendLine("Cover purpose, main sections or topics, important tables, constraints, decisions, examples, values, checks, or steps only when they are present.");
            sb.AppendLine("Prefer 2 to 4 compact paragraphs in the target language.");
        }
        else
        {
            sb.AppendLine("Explain the purpose of the document, the main sections or topics, and the most important concrete facts when they appear in the text: tables, conditions, examples, values, checks, warnings, constraints, decisions, or steps.");
            sb.AppendLine("Write a genuinely useful summary, not a one-line description. Prefer 2 to 4 compact paragraphs in the target language.");
        }

        sb.AppendLine("Do not mention context-window limits. Do not invent content. If some sections are unclear, say so briefly but still summarize what is actually present.");
        sb.AppendLine("Do not output partial URLs, incomplete hostnames, truncated identifiers or half-finished values. If such data appears incomplete in the chunks, omit it instead of guessing.");
        sb.AppendLine();
        sb.AppendLine("CHUNKS:");
        for (var i = 0; i < chunks.Count; i++)
            sb.AppendLine($"[{i + 1}] {chunks[i].Text}");
        return sb.ToString();
    }

    private static List<ToolMemory.SourceRef> BuildLiveSummarySourceRefs(
        IReadOnlyList<SummaryChunk> chunks,
        ToolMemory.SourceRef? fallbackSource,
        string fallbackDocPath,
        string fallbackDocName,
        string? uiLanguage)
        => chunks
            .Select(chunk => BuildSummarySourceRef(chunk, fallbackSource, fallbackDocPath, fallbackDocName, uiLanguage))
            .ToList();

    private static void AppendLiveSummarySourceMetadataPrompt(StringBuilder sb, IReadOnlyList<ToolMemory.SourceRef>? sourceMetadata)
    {
        var sources = sourceMetadata?
            .Where(static source => source is not null)
            .Take(16)
            .ToList() ?? [];
        if (sources.Count == 0)
            return;

        if (sources.Count == 1)
        {
            AppendLiveSummarySourceMetadataPrompt(sb, sources[0]);
            return;
        }

        var lines = sources
            .Select((source, index) => FormatLiveSummarySourceMetadataPrompt(source, index + 1))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
            return;

        sb.AppendLine("IngestionMetadata:");
        foreach (var line in lines)
            sb.AppendLine(line);
        sb.AppendLine("- diagnosticFields: sourceHash/categoryRef/chunkId help trace retrieval; do not include these identifiers in the summary unless explicitly asked.");
    }

    private static string? FormatLiveSummarySourceMetadataPrompt(ToolMemory.SourceRef sourceMetadata, int index)
    {
        var parts = new List<string>();
        var pageStart = Math.Max(1, sourceMetadata.PageStart);
        var pageEnd = Math.Max(pageStart, sourceMetadata.PageEnd);
        parts.Add(pageStart == pageEnd ? $"page=p.{pageStart}" : $"page=p.{pageStart}-{pageEnd}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SourceHash))
            parts.Add($"sourceHash={sourceMetadata.SourceHash}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage))
            parts.Add($"docLanguage={NormalizeDocumentLanguageTag(sourceMetadata.DocLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage))
            parts.Add($"profileLanguage={NormalizeDocumentLanguageTag(sourceMetadata.ProfileLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef))
            parts.Add($"categoryRef={sourceMetadata.CategoryRef}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath))
            parts.Add($"categoryPath={sourceMetadata.CategoryPath}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ChunkId))
            parts.Add($"chunkId={sourceMetadata.ChunkId}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ExtractionSource))
            parts.Add($"extractionSource={sourceMetadata.ExtractionSource}");

        var quality = sourceMetadata.QualityStatus
            ?? sourceMetadata.DocumentQualityStatus
            ?? sourceMetadata.PageQualityStatus;
        if (!string.IsNullOrWhiteSpace(quality))
            parts.Add($"extractionQuality={quality}");
        if (sourceMetadata.DocumentExtractionConfidence.HasValue)
            parts.Add($"documentExtractionConfidence={sourceMetadata.DocumentExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.PageExtractionConfidence.HasValue)
            parts.Add($"pageExtractionConfidence={sourceMetadata.PageExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        var qualitySignals = sourceMetadata.QualitySignals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray() ?? [];
        if (qualitySignals.Length > 0)
            parts.Add("qualitySignals=" + string.Join(",", qualitySignals));

        var profileSignals = FormatLiveSummaryProfileSignalsPrompt(sourceMetadata.ProfileSignals);
        if (!string.IsNullOrWhiteSpace(profileSignals))
            parts.Add(profileSignals);

        if (sourceMetadata.OcrApplied)
            parts.Add("ocr=applied");
        else if (sourceMetadata.OcrRecommended)
            parts.Add("ocr=recommended");
        else if (sourceMetadata.OcrAttempted)
            parts.Add("ocr=attempted");
        if (sourceMetadata.ManualReviewRecommended)
            parts.Add("manualReview=recommended");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SelectionHintEvidenceRole))
            parts.Add($"evidenceRole={sourceMetadata.SelectionHintEvidenceRole}");
        if (sourceMetadata.SelectionHintActionabilityScore.HasValue
            || sourceMetadata.SelectionHintSupportScore.HasValue
            || sourceMetadata.SelectionHintFragmentScore.HasValue
            || sourceMetadata.SelectionHintNavigationScore.HasValue
            || sourceMetadata.SelectionHintQualityPenalty.HasValue)
        {
            parts.Add(
                "selectionScores="
                + $"actionability:{sourceMetadata.SelectionHintActionabilityScore?.ToString() ?? "n/a"},"
                + $"support:{sourceMetadata.SelectionHintSupportScore?.ToString() ?? "n/a"},"
                + $"fragment:{sourceMetadata.SelectionHintFragmentScore?.ToString() ?? "n/a"},"
                + $"navigation:{sourceMetadata.SelectionHintNavigationScore?.ToString() ?? "n/a"},"
                + $"qualityPenalty:{sourceMetadata.SelectionHintQualityPenalty?.ToString() ?? "n/a"}");
        }

        var cards = sourceMetadata.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(FormatLiveSummaryContentCardPrompt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray() ?? [];
        if (cards.Length > 0)
            parts.Add("contentCards=" + string.Join(" | ", cards));

        return parts.Count == 0 ? null : $"- source[{index}]: " + string.Join("; ", parts);
    }

    private static void AppendLiveSummarySourceMetadataPrompt(StringBuilder sb, ToolMemory.SourceRef? sourceMetadata)
    {
        if (sourceMetadata is null)
            return;

        var cards = sourceMetadata.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(FormatLiveSummaryContentCardPrompt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray() ?? [];

        var quality = sourceMetadata.QualityStatus
            ?? sourceMetadata.DocumentQualityStatus
            ?? sourceMetadata.PageQualityStatus;

        var qualitySignals = sourceMetadata.QualitySignals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray() ?? [];

        if (cards.Length == 0
            && qualitySignals.Length == 0
            && string.IsNullOrWhiteSpace(quality)
            && string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath)
            && string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef)
            && string.IsNullOrWhiteSpace(sourceMetadata.SourceHash)
            && string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage)
            && string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage)
            && ComputeSourceProfileSignalsRichness(sourceMetadata.ProfileSignals) == 0)
        {
            return;
        }

        sb.AppendLine("IngestionMetadata:");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SourceHash))
            sb.AppendLine($"- sourceHash: {sourceMetadata.SourceHash}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage))
            sb.AppendLine($"- docLanguage: {NormalizeDocumentLanguageTag(sourceMetadata.DocLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage))
            sb.AppendLine($"- profileLanguage: {NormalizeDocumentLanguageTag(sourceMetadata.ProfileLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef))
            sb.AppendLine($"- categoryRef: {sourceMetadata.CategoryRef}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath))
            sb.AppendLine($"- categoryPath: {sourceMetadata.CategoryPath}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ChunkId))
            sb.AppendLine($"- chunkId: {sourceMetadata.ChunkId}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ExtractionSource))
            sb.AppendLine($"- extractionSource: {sourceMetadata.ExtractionSource}");
        if (!string.IsNullOrWhiteSpace(quality))
            sb.AppendLine($"- extractionQuality: {quality}");
        if (sourceMetadata.ExtractionConfidence.HasValue)
            sb.AppendLine($"- extractionConfidence: {sourceMetadata.ExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.DocumentExtractionConfidence.HasValue)
            sb.AppendLine($"- documentExtractionConfidence: {sourceMetadata.DocumentExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.PageExtractionConfidence.HasValue)
            sb.AppendLine($"- pageExtractionConfidence: {sourceMetadata.PageExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (qualitySignals.Length > 0)
            sb.AppendLine("- qualitySignals: " + string.Join(" | ", qualitySignals));
        var profileSignals = FormatLiveSummaryProfileSignalsPrompt(sourceMetadata.ProfileSignals);
        if (!string.IsNullOrWhiteSpace(profileSignals))
            sb.AppendLine("- " + profileSignals);
        if (sourceMetadata.OcrApplied)
            sb.AppendLine("- ocr: applied");
        else if (sourceMetadata.OcrRecommended)
            sb.AppendLine("- ocr: recommended");
        else if (sourceMetadata.OcrAttempted)
            sb.AppendLine("- ocr: attempted");
        if (sourceMetadata.ManualReviewRecommended)
            sb.AppendLine("- manualReview: recommended");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SelectionHintEvidenceRole))
            sb.AppendLine($"- evidenceRole: {sourceMetadata.SelectionHintEvidenceRole}");
        if (sourceMetadata.SelectionHintActionabilityScore.HasValue || sourceMetadata.SelectionHintSupportScore.HasValue)
            sb.AppendLine($"- selectionScores: actionability={sourceMetadata.SelectionHintActionabilityScore?.ToString() ?? "n/a"} support={sourceMetadata.SelectionHintSupportScore?.ToString() ?? "n/a"}");
        if (cards.Length > 0)
            sb.AppendLine("- contentCards: " + string.Join(" | ", cards));
        sb.AppendLine("- diagnosticFields: sourceHash/categoryRef/chunkId help trace retrieval; do not include these identifiers in the summary unless explicitly asked.");
    }

    private static string? FormatLiveSummaryProfileSignalsPrompt(ToolMemory.SourceProfileSignalsRef? profile)
    {
        if (profile is null || ComputeSourceProfileSignalsRichness(profile) == 0)
            return null;

        var groups = new List<string>();
        AddProfileSignalGroup(groups, "profileKeywords", profile.Keywords, 4);
        AddProfileSignalGroup(groups, "profileEntities", profile.Entities, 4);
        AddProfileSignalGroup(groups, "profileTopics", profile.Topics, 4);
        AddProfileSignalGroup(groups, "profileQuestions", profile.HypotheticalQuestions, 2);
        AddProfileSignalGroup(groups, "profileLimits", profile.Limits, 2);
        AddProfileSignalGroup(groups, "profileMatchedTerms", profile.MatchedTerms, 4);

        return groups.Count == 0
            ? null
            : "profileSignals=" + string.Join(" | ", groups);
    }

    private static void AddProfileSignalGroup(
        List<string> groups,
        string label,
        IEnumerable<string>? values,
        int maxItems)
    {
        var compact = values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 8))
            .ToArray() ?? [];
        if (compact.Length == 0)
            return;

        groups.Add($"{label}:{string.Join(", ", compact)}");
    }

    private static string FormatLiveSummaryContentCardPrompt(ToolMemory.SourceContentCardRef card)
    {
        var parts = new List<string> { card.Title.Trim() };
        if (!string.IsNullOrWhiteSpace(card.ContentCardId))
            parts.Add($"id={card.ContentCardId.Trim()}");
        if (!string.IsNullOrWhiteSpace(card.Kind))
            parts.Add($"kind={card.Kind.Trim()}");
        if (card.PageStart.HasValue)
        {
            var start = Math.Max(1, card.PageStart.Value);
            var end = Math.Max(start, card.PageEnd ?? start);
            parts.Add(start == end ? $"p.{start}" : $"p.{start}-{end}");
        }

        var signals = card.Signals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray() ?? [];
        if (signals.Length > 0)
            parts.Add("signals=" + string.Join(",", signals));

        var evidence = FormatLiveSummaryContentCardEvidencePrompt(card.Evidence);
        if (!string.IsNullOrWhiteSpace(evidence))
            parts.Add($"evidence={evidence}");

        return string.Join(" ", parts);
    }

    private static string? FormatLiveSummaryContentCardEvidencePrompt(JsonElement? evidence)
    {
        if (!evidence.HasValue || evidence.Value.ValueKind != JsonValueKind.Object)
            return null;

        var root = evidence.Value;
        var parts = new List<string>();
        var schemaVersion = TryGetString(root, "schemaVersion") ?? TryGetString(root, "schema_version") ?? TryGetString(root, "SchemaVersion");
        if (!string.IsNullOrWhiteSpace(schemaVersion))
            parts.Add($"schema={schemaVersion.Trim()}");

        var scaleBasis = TryGetObject(root, "scaleBasis") ?? TryGetObject(root, "scale_basis") ?? TryGetObject(root, "ScaleBasis");
        if (scaleBasis.HasValue)
        {
            var label = TryGetString(scaleBasis.Value, "label") ?? TryGetString(scaleBasis.Value, "Label");
            var count = TryGetDouble(scaleBasis.Value, "count") ?? TryGetDouble(scaleBasis.Value, "value") ?? TryGetDouble(scaleBasis.Value, "Count") ?? TryGetDouble(scaleBasis.Value, "Value");
            if (!string.IsNullOrWhiteSpace(label) || count.HasValue)
            {
                var basis = string.Join(" ", new[]
                {
                    count?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    label?.Trim()
                }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(basis))
                    parts.Add($"basis={basis}");
            }
        }

        var quantityFacts = TryGetArray(root, "quantityFacts") ?? TryGetArray(root, "quantity_facts") ?? TryGetArray(root, "QuantityFacts");
        if (quantityFacts.HasValue)
        {
            var facts = new List<string>();
            foreach (var fact in quantityFacts.Value.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
                var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
                var factText = string.Join(" ", new[]
                {
                    value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    unit?.Trim(),
                    label?.Trim()
                }.Where(static item => !string.IsNullOrWhiteSpace(item)));
                if (!string.IsNullOrWhiteSpace(factText))
                    facts.Add(factText);
                if (facts.Count >= 4)
                    break;
            }

            if (facts.Count > 0)
                parts.Add("facts=" + string.Join(",", facts));
        }

        var confidence = TryGetDouble(root, "confidence") ?? TryGetDouble(root, "Confidence");
        if (confidence.HasValue)
            parts.Add($"confidence={confidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        foreach (var property in root.EnumerateObject())
        {
            if (parts.Count >= 8)
                break;

            if (property.NameEquals("schemaVersion")
                || property.NameEquals("schema_version")
                || property.NameEquals("SchemaVersion")
                || property.NameEquals("scaleBasis")
                || property.NameEquals("scale_basis")
                || property.NameEquals("ScaleBasis")
                || property.NameEquals("quantityFacts")
                || property.NameEquals("quantity_facts")
                || property.NameEquals("QuantityFacts")
                || property.NameEquals("confidence")
                || property.NameEquals("Confidence"))
            {
                continue;
            }

            var compactValue = CompactLiveSummaryEvidenceValue(property.Value);
            if (!string.IsNullOrWhiteSpace(compactValue))
                parts.Add($"{property.Name}={compactValue}");
        }

        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static string? CompactLiveSummaryEvidenceValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ShortenLiveSummaryEvidenceValue(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => CompactLiveSummaryEvidenceArray(value),
            JsonValueKind.Object => CompactLiveSummaryEvidenceObject(value),
            _ => null
        };
    }

    private static string? CompactLiveSummaryEvidenceArray(JsonElement value)
    {
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var compact = CompactLiveSummaryEvidenceValue(item);
            if (!string.IsNullOrWhiteSpace(compact))
                items.Add(compact);
            if (items.Count >= 3)
                break;
        }

        return items.Count == 0 ? null : "[" + string.Join(",", items) + "]";
    }

    private static string? CompactLiveSummaryEvidenceObject(JsonElement value)
    {
        var items = new List<string>();
        foreach (var property in value.EnumerateObject())
        {
            var compact = CompactLiveSummaryEvidenceValue(property.Value);
            if (!string.IsNullOrWhiteSpace(compact))
                items.Add($"{property.Name}:{compact}");
            if (items.Count >= 3)
                break;
        }

        return items.Count == 0 ? null : "{" + string.Join(",", items) + "}";
    }

    private static string? ShortenLiveSummaryEvidenceValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return trimmed.Length <= 80 ? trimmed : trimmed[..77] + "...";
    }
}
