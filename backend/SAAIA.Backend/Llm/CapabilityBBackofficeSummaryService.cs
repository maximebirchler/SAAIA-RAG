using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;

namespace SAAIA.Backend;

internal sealed class CapabilityBBackofficeSummaryService
{
    private static readonly string[] IgnoredKeywordTokens =
    [
        "avec",
        "cette",
        "dans",
        "des",
        "document",
        "from",
        "into",
        "page",
        "pages",
        "pour",
        "que",
        "section",
        "sections",
        "should",
        "sur",
        "that",
        "the",
        "this",
        "through",
        "under",
        "what",
        "when",
        "where",
        "which",
        "with"
    ];

    private readonly LocalLlmChatClient _llmClient;
    private readonly ChatOptions _chatOptions;

    public CapabilityBBackofficeSummaryService(
        LocalLlmChatClient llmClient,
        ChatOptions chatOptions)
    {
        _llmClient = llmClient;
        _chatOptions = chatOptions;
    }

    internal async Task<CapabilityBGeneratedSummaryPayload> BuildSummaryAsync(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct,
        string? preferredLanguage = null)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBSummaryGenerationActivity();
        var sw = Stopwatch.StartNew();

        var outputLanguage = ResolveOutputLanguage(preferredLanguage, doc, sectionTitles, excerpts);

        if (!_llmClient.IsConfigured)
        {
            var fallback = BuildDeterministicSummary(
                doc,
                sectionTitles,
                excerpts,
                "llm_not_configured",
                outputLanguage,
                BuildLlmFallbackMeta(LocalLlmChatCompletionResult.NotConfigured, "llm_not_configured"));
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBSummaryGeneration(
                activity,
                strategy: "deterministic_document_foundation",
                fallbackUsed: true,
                durationMs: sw.ElapsedMilliseconds,
                firstResponseMs: null,
                qualityScore: fallback.Quality.Score,
                outputLength: fallback.SummaryText.Length,
                sectionCount: sectionTitles.Count,
                excerptCount: excerpts.Count,
                fallbackReason: "llm_not_configured");
            return fallback;
        }

        var systemPrompt = """
You produce concise document summaries for an indexed knowledge base.
Return plain text only.
Constraints:
- 2 to 5 short paragraphs or bullets
- no markdown heading
- no JSON
- mention concrete scope, topics, sections, procedures, constraints, examples, or takeaways when available
- cite at most 3 clear examples; prefer broad topics over enumerations
- skip names or phrases that look garbled, OCR-like, or semantically uncertain
- if extraction/OCR quality says manual review, low text, OCR failure, or low confidence, include one brief caveat in the requested output language that the available extracted text may be incomplete or uncertain
- never treat low-confidence OCR/extraction samples as a normal complete source
- do not group examples into inferred categories, audiences, tools, or item families unless the excerpt explicitly says so
- if examples are mixed or uncertain, write "includes examples such as ..." instead of classifying them
- do not use parenthetical examples such as "(like ...)" or "(comme ...)"
- do not state numeric content counts unless the requested document name itself contains that count
- do not mention internal indexing metadata, indexed version, source hash, storage path, runtime, pipeline, or database state
- do not invent a target audience such as "operators" unless the excerpts explicitly talk about operators
- treat excerpt bullets as independent source samples; do not merge two separate bullets into one invented procedure, item, fact, or causal claim
- when listing examples from separate excerpts, use neutral wording such as "includes examples of" or "covers topics such as"
- avoid redundant wording and repeated noun phrases
- stay neutral and documentary: summarize what the document contains, not how SAAIA indexed it
- keep the answer under 750 characters
- write in the requested output language; if it is unknown, use the dominant language of the provided titles and excerpts
""";

        var userPrompt = BuildUserPrompt(doc, sectionTitles, excerpts, outputLanguage);
        var completion = await _llmClient.TryCompleteWithTelemetryAsync(systemPrompt, userPrompt, maxTokens: 280, temperature: 0.05, ct);
        var summaryText = NormalizeSummary(completion.Content);
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            var fallbackReason = ResolveLlmFallbackReason(completion);
            var fallback = BuildDeterministicSummary(
                doc,
                sectionTitles,
                excerpts,
                fallbackReason,
                outputLanguage,
                BuildLlmFallbackMeta(completion, fallbackReason));
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBSummaryGeneration(
                activity,
                strategy: "deterministic_document_foundation",
                fallbackUsed: true,
                durationMs: sw.ElapsedMilliseconds,
                firstResponseMs: completion.FirstByteMs,
                qualityScore: fallback.Quality.Score,
                outputLength: fallback.SummaryText.Length,
                sectionCount: sectionTitles.Count,
                excerptCount: excerpts.Count,
                fallbackReason: fallbackReason);
            return fallback;
        }

        if (LooksLikeLanguageMismatch(summaryText, outputLanguage))
        {
            var fallback = BuildDeterministicSummary(
                doc,
                sectionTitles,
                excerpts,
                "llm_language_mismatch",
                outputLanguage,
                BuildLlmFallbackMeta(completion, "llm_language_mismatch"));
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBSummaryGeneration(
                activity,
                strategy: "deterministic_document_foundation",
                fallbackUsed: true,
                durationMs: sw.ElapsedMilliseconds,
                firstResponseMs: completion.FirstByteMs,
                qualityScore: fallback.Quality.Score,
                outputLength: fallback.SummaryText.Length,
                sectionCount: sectionTitles.Count,
                excerptCount: excerpts.Count,
                fallbackReason: "llm_language_mismatch");
            return fallback;
        }

        summaryText = CapabilityBExtractionQualityPrompting.ApplySummaryCaveat(summaryText, doc.ExtractionQuality, outputLanguage);
        var quality = EvaluateQuality(summaryText, sectionTitles, excerpts);
        var meta = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "llm_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
            ["sourceSampleStrategy"] = "representative_document_units_v1",
            ["llmModel"] = _chatOptions.LlmModel,
            ["llmDurationMs"] = completion.DurationMs,
            ["llmResponseHeadersMs"] = completion.ResponseHeadersMs,
            ["llmFirstResponseMs"] = completion.FirstByteMs,
            ["llmStatusCode"] = completion.StatusCode,
            ["llmBytesRead"] = completion.BytesRead,
            ["outputLanguage"] = outputLanguage ?? "auto",
            ["extractionQuality"] = CapabilityBExtractionQualityPrompting.BuildMeta(doc.ExtractionQuality, outputLanguage),
            ["qualityScore"] = quality.Score,
            ["qualitySignals"] = new Dictionary<string, object?>
            {
                ["lineCount"] = quality.LineCount,
                ["lengthScore"] = quality.LengthScore,
                ["structureScore"] = quality.StructureScore,
                ["sectionCoverageScore"] = quality.SectionCoverageScore,
                ["matchedSectionCount"] = quality.MatchedSectionCount,
                ["expectedSectionCount"] = quality.ExpectedSectionCount,
                ["keywordCoverageScore"] = quality.KeywordCoverageScore,
                ["matchedKeywordCount"] = quality.MatchedKeywordCount,
                ["expectedKeywordCount"] = quality.ExpectedKeywordCount
            },
            ["fallbackUsed"] = false,
            ["generatedAt"] = DateTimeOffset.UtcNow
        });

        sw.Stop();
        RuntimeGovernanceTelemetry.CompleteCapabilityBSummaryGeneration(
            activity,
            strategy: "llm_document_foundation",
            fallbackUsed: false,
            durationMs: sw.ElapsedMilliseconds,
            firstResponseMs: completion.FirstByteMs,
            qualityScore: quality.Score,
            outputLength: summaryText.Length,
            sectionCount: sectionTitles.Count,
            excerptCount: excerpts.Count);
        return new CapabilityBGeneratedSummaryPayload(summaryText, meta, quality, outputLanguage ?? "und");
    }

    internal static CapabilityBGeneratedSummaryPayload BuildDeterministicSummary(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? fallbackReason = null,
        string? preferredLanguage = null,
        IReadOnlyDictionary<string, object?>? additionalMeta = null)
    {
        var outputLanguage = ResolveOutputLanguage(preferredLanguage, doc, sectionTitles, excerpts);
        var summaryText = CapabilityBExtractionQualityPrompting.ApplySummaryCaveat(
            ComposeSummaryText(doc, sectionTitles, excerpts, outputLanguage),
            doc.ExtractionQuality,
            outputLanguage);
        var quality = EvaluateQuality(summaryText, sectionTitles, excerpts);
        var metaValues = new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "deterministic_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
            ["sourceSampleStrategy"] = "representative_document_units_v1",
            ["extractionQuality"] = CapabilityBExtractionQualityPrompting.BuildMeta(doc.ExtractionQuality, outputLanguage),
            ["qualityScore"] = quality.Score,
            ["qualitySignals"] = new Dictionary<string, object?>
            {
                ["lineCount"] = quality.LineCount,
                ["lengthScore"] = quality.LengthScore,
                ["structureScore"] = quality.StructureScore,
                ["sectionCoverageScore"] = quality.SectionCoverageScore,
                ["matchedSectionCount"] = quality.MatchedSectionCount,
                ["expectedSectionCount"] = quality.ExpectedSectionCount,
                ["keywordCoverageScore"] = quality.KeywordCoverageScore,
                ["matchedKeywordCount"] = quality.MatchedKeywordCount,
                ["expectedKeywordCount"] = quality.ExpectedKeywordCount
            },
            ["fallbackUsed"] = true,
            ["fallbackReason"] = fallbackReason,
            ["outputLanguage"] = outputLanguage ?? "auto",
            ["generatedAt"] = DateTimeOffset.UtcNow
        };

        if (additionalMeta is not null)
        {
            foreach (var item in additionalMeta)
                metaValues[item.Key] = item.Value;
        }

        var meta = JsonSerializer.SerializeToElement(metaValues);

        return new CapabilityBGeneratedSummaryPayload(summaryText, meta, quality, outputLanguage ?? "und");
    }

    private static string ResolveLlmFallbackReason(LocalLlmChatCompletionResult completion)
    {
        var error = completion.Error?.Trim();
        if (string.IsNullOrWhiteSpace(error))
            return "llm_empty_response";

        if (string.Equals(error, "not_configured", StringComparison.Ordinal))
            return "llm_not_configured";

        if (string.Equals(error, "llm_queue_full", StringComparison.Ordinal)
            || string.Equals(error, "llm_timeout", StringComparison.Ordinal)
            || string.Equals(error, "llm_transport_error", StringComparison.Ordinal))
        {
            return error;
        }

        if (string.Equals(error, "empty_body", StringComparison.Ordinal)
            || string.Equals(error, "choices_missing", StringComparison.Ordinal)
            || string.Equals(error, "content_missing", StringComparison.Ordinal))
        {
            return "llm_empty_response";
        }

        if (error.StartsWith("http_", StringComparison.Ordinal))
        {
            return completion.StatusCode is >= 500 and <= 599
                ? "llm_runtime_unavailable"
                : "llm_http_error";
        }

        return "llm_exception";
    }

    private static IReadOnlyDictionary<string, object?> BuildLlmFallbackMeta(
        LocalLlmChatCompletionResult completion,
        string fallbackReason)
    {
        return new Dictionary<string, object?>
        {
            ["llmError"] = completion.Error,
            ["llmFailureKind"] = fallbackReason,
            ["llmFailureCategory"] = ResolveLlmFailureCategory(completion.Error, fallbackReason),
            ["llmStatusCode"] = completion.StatusCode,
            ["llmDurationMs"] = completion.DurationMs,
            ["llmResponseHeadersMs"] = completion.ResponseHeadersMs,
            ["llmFirstResponseMs"] = completion.FirstByteMs,
            ["llmBytesRead"] = completion.BytesRead
        };
    }

    private static string ResolveLlmFailureCategory(string? llmError, string fallbackReason)
    {
        if (string.Equals(fallbackReason, "llm_not_configured", StringComparison.Ordinal))
            return "configuration";
        if (string.Equals(fallbackReason, "llm_queue_full", StringComparison.Ordinal))
            return "queue";
        if (string.Equals(fallbackReason, "llm_timeout", StringComparison.Ordinal))
            return "timeout";
        if (string.Equals(fallbackReason, "llm_transport_error", StringComparison.Ordinal))
            return "transport";
        if (string.Equals(fallbackReason, "llm_empty_response", StringComparison.Ordinal))
            return "empty";
        if (string.Equals(fallbackReason, "llm_runtime_unavailable", StringComparison.Ordinal)
            || string.Equals(fallbackReason, "llm_http_error", StringComparison.Ordinal)
            || (llmError?.StartsWith("http_", StringComparison.Ordinal) ?? false))
        {
            return "http";
        }
        if (string.Equals(fallbackReason, "llm_language_mismatch", StringComparison.Ordinal))
            return "quality";

        return "exception";
    }

    private static string BuildUserPrompt(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? outputLanguage)
    {
        var normalizedSections = sectionTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title.Trim())
            .Take(8)
            .ToArray();
        var normalizedExcerpts = excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .Select(TrimExcerpt)
            .Take(6)
            .ToArray();

        return $"""
Document name: {doc.DocName}
Catalog category: {doc.Category ?? "unknown"}
Page count: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}
Requested output language: {outputLanguage ?? "auto"}
{CapabilityBExtractionQualityPrompting.BuildPromptBlock(doc.ExtractionQuality)}
Section titles:
{string.Join(Environment.NewLine, normalizedSections.Select(static title => $"- {title}"))}
Independent source excerpts:
{string.Join(Environment.NewLine, normalizedExcerpts.Select(static excerpt => $"- {excerpt}"))}
{BuildOutputLanguageInstruction(outputLanguage)}
Write a short documentary summary of what this document contains. Keep separate examples separate unless one excerpt explicitly links them. Do not classify examples, do not use parenthetical examples, and do not describe indexing, storage, file processing, or internal metadata.
""";
    }

    private static string NormalizeSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var repaired = TextEncodingSanitizer.RepairCommonMojibake(content);
        var normalized = string.Join(
            Environment.NewLine,
            repaired
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim()));

        return CleanupRepeatedTerms(DeduplicateSentences(normalized.Trim()));
    }

    private static string DeduplicateSentences(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var lines = normalized.Split(["\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(lines.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var dedupedLine = DeduplicateInlineSentences(line, seen);
            if (!string.IsNullOrWhiteSpace(dedupedLine))
                result.Add(dedupedLine);
        }

        return string.Join(Environment.NewLine, result);
    }

    private static string DeduplicateInlineSentences(string line, ISet<string> seen)
    {
        var parts = line.Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            var key = BuildSentenceDedupKey(line);
            return seen.Add(key) ? line : string.Empty;
        }

        var kept = new List<string>(parts.Length);
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            var terminal = part.EndsWith(".", StringComparison.Ordinal) ? string.Empty : ".";
            var sentence = part + terminal;
            var key = BuildSentenceDedupKey(sentence);
            if (seen.Add(key))
                kept.Add(sentence);
        }

        return string.Join(" ", kept).Trim();
    }

    private static string BuildSentenceDedupKey(string sentence)
        => FoldDiacritics(NormalizeInlineText(sentence))
            .Trim()
            .TrimEnd('.', '!', '?', ':', ';')
            .ToLowerInvariant();

    private static string BuildOutputLanguageInstruction(string? outputLanguage)
        => outputLanguage switch
        {
            "fr" => "Important: redige uniquement en francais.",
            "es" => "Importante: escribe solo en espanol.",
            "pt" => "Importante: escreve apenas em portugues.",
            "de" => "Wichtig: schreibe nur auf Deutsch.",
            "it" => "Importante: scrivi solo in italiano.",
            "en" => "Important: write only in English.",
            "und" or null or "" => "Important: write in the dominant language of the document excerpts. Do not translate the document into another language.",
            _ => $"Important: write only in the document language identified by this BCP-47 language tag: {outputLanguage}."
        };

    private static bool LooksLikeLanguageMismatch(string summaryText, string? outputLanguage)
    {
        if (string.IsNullOrWhiteSpace(summaryText) || string.IsNullOrWhiteSpace(outputLanguage))
            return false;

        var normalized = " " + FoldDiacritics(NormalizeInlineText(summaryText)).ToLowerInvariant() + " ";
        var target = NormalizeLanguagePrimarySubtag(outputLanguage);
        if (string.IsNullOrWhiteSpace(target) || target == "und")
            return false;

        if (target != "en")
        {
            var targetSignals = GetLanguageMismatchGuardSignals(target);
            if (targetSignals.Count == 0)
                return false;

            var englishHits = CountLanguageHits(normalized, [" this ", " is ", " document ", " contains ", " containing ", " includes ", " covers ", " provides ", " describes ", " requirements ", " examples ", " should ", " available "]);
            var targetHits = CountLanguageHits(normalized, targetSignals);
            return englishHits >= 3 && targetHits <= 1;
        }

        return false;
    }

    private static string NormalizeLanguagePrimarySubtag(string? language)
        => DocumentLanguageResolver.PrimarySubtag(language);

    private static IReadOnlyList<string> GetLanguageMismatchGuardSignals(string targetLanguage)
        => targetLanguage switch
        {
            "fr" => [" ce ", " cette ", " contient ", " couvre ", " exemples ", " propose ", " decrit ", " exigences "],
            "es" => [" este ", " esta ", " contiene ", " cubre ", " ejemplos ", " describe ", " requisitos "],
            "pt" => [" este ", " esta ", " contem ", " cobre ", " exemplos ", " descreve ", " requisitos "],
            "de" => [" dieses ", " dokument ", " enthaelt ", " beschreibt ", " behandelt ", " beispiele ", " anforderungen "],
            "it" => [" questo ", " questa ", " contiene ", " copre ", " esempi ", " descrive ", " requisiti "],
            "nl" => [" dit ", " deze ", " bevat ", " beschrijft ", " behandelt ", " voorbeelden ", " vereisten "],
            _ => []
        };

    private static string CleanupRepeatedTerms(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        var words = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 3)
            return summary;

        var result = new List<string>(words.Length);
        for (var i = 0; i < words.Length; i++)
        {
            if (i + 2 < words.Length
                && IsConjunction(words[i + 1])
                && SameWordIgnoringPunctuation(words[i], words[i + 2]))
            {
                result.Add(PreserveTrailingPunctuation(words[i], words[i + 2]));
                i += 2;
                continue;
            }

            result.Add(words[i]);
        }

        return string.Join(' ', result);
    }

    private static bool IsConjunction(string value)
    {
        var normalized = FoldDiacritics(value.Trim().Trim(',', ';', ':')).ToLowerInvariant();
        return normalized is "and" or "et" or "or" or "ou" or "y" or "o" or "und" or "oder" or "e" or "oppure";
    }

    private static bool SameWordIgnoringPunctuation(string left, string right)
        => string.Equals(
            FoldDiacritics(left.Trim().Trim(',', ';', ':', '.', '!', '?')).ToLowerInvariant(),
            FoldDiacritics(right.Trim().Trim(',', ';', ':', '.', '!', '?')).ToLowerInvariant(),
            StringComparison.Ordinal);

    private static string PreserveTrailingPunctuation(string kept, string removed)
    {
        if (string.IsNullOrWhiteSpace(kept) || EndsWithSentencePunctuation(kept))
            return kept;

        var trimmedRemoved = removed.Trim();
        if (trimmedRemoved.Length == 0)
            return kept;

        var last = trimmedRemoved[^1];
        return last is '.' or '!' or '?' or ';' or ':'
            ? kept + last
            : kept;
    }

    private static bool EndsWithSentencePunctuation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var last = value.Trim()[^1];
        return last is '.' or '!' or '?' or ';' or ':';
    }

    private static string ComposeSummaryText(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? outputLanguage)
    {
        var templateLanguage = NormalizeLanguagePrimarySubtag(outputLanguage);
        if (!HasLocalizedDeterministicSummaryTemplate(templateLanguage))
            return ComposeNeutralExtractiveSummary(doc, sectionTitles, excerpts);

        var lines = new List<string>
        {
            BuildOverviewLine(doc, templateLanguage)
        };

        var cappedSectionTitles = sectionTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(NormalizeInlineText)
            .Take(8)
            .ToArray();
        if (cappedSectionTitles.Length > 0)
            lines.Add(BuildKeySectionsLabel(templateLanguage) + string.Join("; ", cappedSectionTitles) + ".");

        var cappedExcerpts = excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .Select(TrimExcerpt)
            .Take(6)
            .ToArray();
        if (cappedExcerpts.Length > 0)
        {
            lines.Add(BuildHighlightsLabel(templateLanguage));
            foreach (var excerpt in cappedExcerpts)
                lines.Add($"- {excerpt}");
        }
        else
        {
            lines.Add(BuildNoExcerptsLine(templateLanguage));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool HasLocalizedDeterministicSummaryTemplate(string? outputLanguage)
        => NormalizeLanguagePrimarySubtag(outputLanguage) is "fr" or "en" or "es" or "pt" or "de" or "it";

    private static string ComposeNeutralExtractiveSummary(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.DocName))
            lines.Add(NormalizeInlineText(doc.DocName));

        foreach (var title in sectionTitles
                     .Where(static title => !string.IsNullOrWhiteSpace(title))
                     .Select(NormalizeInlineText)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            lines.Add(title);
        }

        foreach (var excerpt in excerpts
                     .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
                     .Select(TrimExcerpt)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            lines.Add("- " + excerpt);
        }

        return lines.Count == 0
            ? NormalizeInlineText(doc.DocPath)
            : string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverviewLine(CapabilityBDocumentRow doc, string? outputLanguage)
    {
        if (string.Equals(outputLanguage, "fr", StringComparison.Ordinal))
        {
            var builderFr = new StringBuilder();
            builderFr.Append(doc.DocName);
            builderFr.Append(" est un document");
            if (!string.IsNullOrWhiteSpace(doc.Category))
            {
                builderFr.Append(' ');
                builderFr.Append(doc.Category!.Trim());
            }

            if (doc.PageCount is > 0)
            {
                builderFr.Append(" de ");
                builderFr.Append(doc.PageCount.Value);
                builderFr.Append(doc.PageCount.Value == 1 ? " page" : " pages");
            }

            builderFr.Append('.');
            return builderFr.ToString();
        }

        if (string.Equals(outputLanguage, "es", StringComparison.Ordinal))
        {
            var builderEs = new StringBuilder();
            builderEs.Append(doc.DocName);
            builderEs.Append(" es un documento");
            AppendOptionalCategory(builderEs, doc.Category);
            AppendPageCount(builderEs, doc.PageCount, " de ", " pagina", " paginas");
            builderEs.Append('.');
            return builderEs.ToString();
        }

        if (string.Equals(outputLanguage, "pt", StringComparison.Ordinal))
        {
            var builderPt = new StringBuilder();
            builderPt.Append(doc.DocName);
            builderPt.Append(" e um documento");
            AppendOptionalCategory(builderPt, doc.Category);
            AppendPageCount(builderPt, doc.PageCount, " de ", " pagina", " paginas");
            builderPt.Append('.');
            return builderPt.ToString();
        }

        if (string.Equals(outputLanguage, "de", StringComparison.Ordinal))
        {
            var builderDe = new StringBuilder();
            builderDe.Append(doc.DocName);
            builderDe.Append(" ist ein Dokument");
            AppendOptionalCategory(builderDe, doc.Category);
            AppendPageCount(builderDe, doc.PageCount, " mit ", " Seite", " Seiten");
            builderDe.Append('.');
            return builderDe.ToString();
        }

        if (string.Equals(outputLanguage, "it", StringComparison.Ordinal))
        {
            var builderIt = new StringBuilder();
            builderIt.Append(doc.DocName);
            builderIt.Append(" e un documento");
            AppendOptionalCategory(builderIt, doc.Category);
            AppendPageCount(builderIt, doc.PageCount, " di ", " pagina", " pagine");
            builderIt.Append('.');
            return builderIt.ToString();
        }

        var builder = new StringBuilder();
        builder.Append(doc.DocName);
        builder.Append(" is a document");
        if (!string.IsNullOrWhiteSpace(doc.Category))
        {
            builder.Append(" in ");
            builder.Append(doc.Category!.Trim());
            builder.Append(" category");
        }
        if (doc.PageCount is > 0)
        {
            builder.Append(" with ");
            builder.Append(doc.PageCount.Value);
            builder.Append(doc.PageCount.Value == 1 ? " page" : " pages");
        }

        builder.Append('.');
        return builder.ToString();
    }

    private static string BuildKeySectionsLabel(string? outputLanguage)
        => outputLanguage switch
        {
            "fr" => "Sections cles : ",
            "es" => "Secciones clave: ",
            "pt" => "Secoes principais: ",
            "de" => "Wichtige Abschnitte: ",
            "it" => "Sezioni chiave: ",
            _ => "Key sections: "
        };

    private static string BuildHighlightsLabel(string? outputLanguage)
        => outputLanguage switch
        {
            "fr" => "Extraits :",
            "es" => "Extractos:",
            "pt" => "Excertos:",
            "de" => "Auszuege:",
            "it" => "Estratti:",
            _ => "Highlights:"
        };

    private static string BuildNoExcerptsLine(string? outputLanguage)
        => outputLanguage switch
        {
            "fr" => "Aucun extrait d'unite disponible ; ce resume s'appuie sur les metadonnees indexees du document.",
            "es" => "No hay extractos de unidad disponibles; este resumen se basa en los metadatos indexados del documento.",
            "pt" => "Nao ha excertos de unidade disponiveis; este resumo usa os metadados indexados do documento.",
            "de" => "Keine Einheitenauszuege verfuegbar; diese Zusammenfassung nutzt die indexierten Dokumentmetadaten.",
            "it" => "Nessun estratto di unita disponibile; questo riassunto usa i metadati indicizzati del documento.",
            _ => "No extracted unit excerpts were available, so this summary relies on the indexed document metadata."
        };

    private static void AppendOptionalCategory(StringBuilder builder, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return;

        builder.Append(' ');
        builder.Append(category.Trim());
    }

    private static void AppendPageCount(StringBuilder builder, int? pageCount, string prefix, string singular, string plural)
    {
        if (pageCount is not > 0)
            return;

        builder.Append(prefix);
        builder.Append(pageCount.Value);
        builder.Append(pageCount.Value == 1 ? singular : plural);
    }

    private static string? NormalizePreferredLanguage(string? preferredLanguage)
        => DocumentLanguageResolver.FirstKnownLanguage(preferredLanguage);

    private static string? ResolveOutputLanguage(
        string? preferredLanguage,
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        return DocumentLanguageResolver.FirstKnownLanguage(preferredLanguage, doc.ProfileLanguage)
            ?? DetectDominantLanguage(doc, sectionTitles, excerpts);
    }

    private static string? DetectDominantLanguage(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var corpus = string.Join(' ', new[]
        {
            doc.DocName,
            doc.DocPath,
            doc.Category ?? string.Empty,
            string.Join(' ', sectionTitles),
            string.Join(' ', excerpts)
        });

        return DocumentLanguageResolver.DetectDominantLanguage(corpus);
    }

    private static int CountLanguageHits(string normalized, IReadOnlyList<string> needles)
        => needles.Count(needle => normalized.Contains(needle, StringComparison.Ordinal));

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string TrimExcerpt(string text)
    {
        var normalized = NormalizeInlineText(text);
        return normalized.Length <= 220
            ? normalized
            : normalized[..217] + "...";
    }

    private static string NormalizeInlineText(string text)
        => string.Join(" ", text
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

    private static CapabilityBSummaryQualityEvaluation EvaluateQuality(
        string summaryText,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var normalizedSummary = NormalizeInlineText(summaryText).ToLowerInvariant();
        var lineCount = summaryText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        var expectedSections = sectionTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => NormalizeInlineText(title).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var matchedSectionCount = expectedSections.Count(section => normalizedSummary.Contains(section, StringComparison.Ordinal));
        var sectionCoverageScore = expectedSections.Length == 0
            ? 1d
            : Math.Round((double)matchedSectionCount / expectedSections.Length, 3, MidpointRounding.AwayFromZero);

        var expectedKeywords = BuildExpectedKeywords(excerpts);
        var matchedKeywordCount = expectedKeywords.Count(keyword => normalizedSummary.Contains(keyword, StringComparison.Ordinal));
        var keywordCoverageScore = expectedKeywords.Count == 0
            ? 1d
            : Math.Round((double)matchedKeywordCount / expectedKeywords.Count, 3, MidpointRounding.AwayFromZero);

        var lengthScore = EvaluateLengthScore(summaryText.Length);
        var structureScore = EvaluateStructureScore(lineCount);
        var score = Math.Round(
            (sectionCoverageScore * 0.35d)
            + (keywordCoverageScore * 0.25d)
            + (lengthScore * 0.20d)
            + (structureScore * 0.20d),
            3,
            MidpointRounding.AwayFromZero);

        return new CapabilityBSummaryQualityEvaluation(
            Score: Math.Clamp(score, 0d, 1d),
            LineCount: lineCount,
            LengthScore: lengthScore,
            StructureScore: structureScore,
            SectionCoverageScore: sectionCoverageScore,
            MatchedSectionCount: matchedSectionCount,
            ExpectedSectionCount: expectedSections.Length,
            KeywordCoverageScore: keywordCoverageScore,
            MatchedKeywordCount: matchedKeywordCount,
            ExpectedKeywordCount: expectedKeywords.Count);
    }

    private static double EvaluateLengthScore(int length)
    {
        if (length is >= 180 and <= 900)
            return 1d;
        if (length is >= 140 and <= 980)
            return 0.8d;
        if (length is >= 100 and <= 1100)
            return 0.6d;
        return 0.35d;
    }

    private static double EvaluateStructureScore(int lineCount)
    {
        if (lineCount is >= 2 and <= 6)
            return 1d;
        if (lineCount == 1 || lineCount == 7)
            return 0.7d;
        return 0.4d;
    }

    private static List<string> BuildExpectedKeywords(IReadOnlyList<string> excerpts)
    {
        return excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .SelectMany(excerpt => NormalizeInlineText(excerpt)
                .ToLowerInvariant()
                .Split([' ', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static token => token.Length >= 5)
            .Where(static token => !IgnoredKeywordTokens.Contains(token, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
    }
}

internal sealed record CapabilityBGeneratedSummaryPayload(
    string SummaryText,
    JsonElement Meta,
    CapabilityBSummaryQualityEvaluation Quality,
    string? DocLanguage = null);

internal sealed record CapabilityBSummaryQualityEvaluation(
    double Score,
    int LineCount,
    double LengthScore,
    double StructureScore,
    double SectionCoverageScore,
    int MatchedSectionCount,
    int ExpectedSectionCount,
    double KeywordCoverageScore,
    int MatchedKeywordCount,
    int ExpectedKeywordCount);
