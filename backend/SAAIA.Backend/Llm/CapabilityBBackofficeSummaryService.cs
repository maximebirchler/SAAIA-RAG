using System.Text;
using System.Text.Json;
using System.Diagnostics;

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

        if (!_llmClient.IsConfigured)
        {
            var fallback = BuildDeterministicSummary(doc, sectionTitles, excerpts, "llm_not_configured", preferredLanguage);
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
You produce concise enterprise backoffice summaries for indexed documents.
Return plain text only.
Constraints:
- 2 to 5 short paragraphs or bullets
- no markdown heading
- no JSON
- mention concrete scope, sections, and operational takeaways when available
- keep the answer under 900 characters
- write in the requested output language; if it is unknown, use the dominant language of the provided titles and excerpts
""";

        var outputLanguage = NormalizePreferredLanguage(preferredLanguage);
        var userPrompt = BuildUserPrompt(doc, sectionTitles, excerpts, outputLanguage);
        var completion = await _llmClient.TryCompleteWithTelemetryAsync(systemPrompt, userPrompt, maxTokens: 320, temperature: 0.1, ct);
        var summaryText = NormalizeSummary(completion.Content);
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            var fallbackReason = string.Equals(completion.Error, "not_configured", StringComparison.Ordinal)
                ? "llm_not_configured"
                : "llm_empty_response";
            var fallback = BuildDeterministicSummary(doc, sectionTitles, excerpts, fallbackReason, outputLanguage);
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

        var quality = EvaluateQuality(summaryText, sectionTitles, excerpts);
        var meta = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "llm_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
            ["llmModel"] = _chatOptions.LlmModel,
            ["llmDurationMs"] = completion.DurationMs,
            ["llmResponseHeadersMs"] = completion.ResponseHeadersMs,
            ["llmFirstResponseMs"] = completion.FirstByteMs,
            ["llmBytesRead"] = completion.BytesRead,
            ["outputLanguage"] = outputLanguage ?? "auto",
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
        return new CapabilityBGeneratedSummaryPayload(summaryText, meta, quality);
    }

    internal static CapabilityBGeneratedSummaryPayload BuildDeterministicSummary(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? fallbackReason = null,
        string? preferredLanguage = null)
    {
        var outputLanguage = NormalizePreferredLanguage(preferredLanguage);
        var summaryText = ComposeSummaryText(doc, sectionTitles, excerpts, outputLanguage);
        var quality = EvaluateQuality(summaryText, sectionTitles, excerpts);
        var meta = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "deterministic_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
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
        });

        return new CapabilityBGeneratedSummaryPayload(summaryText, meta, quality);
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
            .Take(5)
            .ToArray();
        var normalizedExcerpts = excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .Select(TrimExcerpt)
            .Take(3)
            .ToArray();

        return $"""
Document name: {doc.DocName}
Document path: {doc.DocPath}
Category: {doc.Category ?? "unknown"}
Page count: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}
Indexed version: {doc.IndexedVersion}
Requested output language: {outputLanguage ?? "auto"}
Section titles:
{string.Join(Environment.NewLine, normalizedSections.Select(static title => $"- {title}"))}
Excerpt highlights:
{string.Join(Environment.NewLine, normalizedExcerpts.Select(static excerpt => $"- {excerpt}"))}
Write a short backoffice summary for operators who need to understand this document quickly.
""";
    }

    private static string NormalizeSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var normalized = string.Join(
            Environment.NewLine,
            content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim()));

        return normalized.Trim();
    }

    private static string ComposeSummaryText(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? outputLanguage)
    {
        var lines = new List<string>
        {
            BuildOverviewLine(doc, outputLanguage)
        };

        if (sectionTitles.Count > 0)
            lines.Add(BuildKeySectionsLabel(outputLanguage) + string.Join("; ", sectionTitles.Select(NormalizeInlineText)) + ".");

        if (excerpts.Count > 0)
        {
            lines.Add(BuildHighlightsLabel(outputLanguage));
            foreach (var excerpt in excerpts.Select(TrimExcerpt))
                lines.Add($"- {excerpt}");
        }
        else
        {
            lines.Add(BuildNoExcerptsLine(outputLanguage));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverviewLine(CapabilityBDocumentRow doc, string? outputLanguage)
    {
        if (string.Equals(outputLanguage, "fr", StringComparison.Ordinal))
        {
            var builderFr = new StringBuilder();
            builderFr.Append(doc.DocName);
            builderFr.Append(" est un document indexe");
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

            builderFr.Append(" : ");
            builderFr.Append(doc.DocPath);
            builderFr.Append('.');
            return builderFr.ToString();
        }

        var builder = new StringBuilder();
        builder.Append(doc.DocName);
        builder.Append(" is an indexed");
        if (!string.IsNullOrWhiteSpace(doc.Category))
        {
            builder.Append(' ');
            builder.Append(doc.Category!.Trim());
        }

        builder.Append(" document");
        if (doc.PageCount is > 0)
        {
            builder.Append(" with ");
            builder.Append(doc.PageCount.Value);
            builder.Append(doc.PageCount.Value == 1 ? " page" : " pages");
        }

        builder.Append(" at ");
        builder.Append(doc.DocPath);
        builder.Append('.');
        return builder.ToString();
    }

    private static string BuildKeySectionsLabel(string? outputLanguage)
        => string.Equals(outputLanguage, "fr", StringComparison.Ordinal)
            ? "Sections cles : "
            : "Key sections: ";

    private static string BuildHighlightsLabel(string? outputLanguage)
        => string.Equals(outputLanguage, "fr", StringComparison.Ordinal)
            ? "Extraits :"
            : "Highlights:";

    private static string BuildNoExcerptsLine(string? outputLanguage)
        => string.Equals(outputLanguage, "fr", StringComparison.Ordinal)
            ? "Aucun extrait d'unite disponible ; ce resume s'appuie sur les metadonnees indexees du document."
            : "No extracted unit excerpts were available, so this summary relies on the indexed document metadata.";

    private static string? NormalizePreferredLanguage(string? preferredLanguage)
    {
        if (string.IsNullOrWhiteSpace(preferredLanguage))
            return null;

        var language = preferredLanguage.Trim().ToLowerInvariant();
        return language is "fr" or "en" or "es" or "pt" or "de" or "it"
            ? language
            : null;
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
            .Take(5)
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
