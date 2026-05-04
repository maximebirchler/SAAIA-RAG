using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Backend;

internal sealed partial class DocumentProfileEnrichmentService
{
    private readonly LocalLlmChatClient _llmClient;

    public DocumentProfileEnrichmentService(LocalLlmChatClient llmClient)
    {
        _llmClient = llmClient;
    }

    internal async Task<ProjectedDocumentProfile?> BuildEnrichedProfileAsync(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        if (!_llmClient.IsConfigured)
            return null;

        var systemPrompt = """
Create a compact retrieval profile. Return JSON only:
{"language":"fr|en|es|pt|de|it|und","summary":"...","keywords":["..."],"entities":["..."],"topics":["..."],"questions":["..."],"limits":["..."],"cards":[{"title":"...","pageStart":1,"pageEnd":1,"kind":"content_item","signals":["..."]}]}
Use only provided data. No invented facts, page refs, quantities, or certifications. Summary max 80 words. Arrays must be short and deduplicated. Cards must describe concrete reusable content found in the provided section titles, excerpts, or baseline cards.
Write summary, questions, and limits in the requested output language when it is provided.
""";

        var userPrompt = BuildUserPrompt(doc, baseline, sectionTitles, excerpts);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 420, temperature: 0.1, ct);
        if (!TryParseProfile(completion, out var parsed))
            return null;

        var summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? baseline.SummaryText
            : parsed.Summary!;
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        return DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: NormalizeLanguage(parsed.Language, baseline.Language),
            summaryText: summary,
            keywords: Merge(parsed.Keywords, baseline.Keywords, 32),
            entities: Merge(parsed.Entities, baseline.Entities, 32),
            topics: Merge(parsed.Topics, baseline.Topics, 20),
            hypotheticalQuestions: Merge(parsed.Questions, baseline.HypotheticalQuestions, 10),
            limits: Merge(parsed.Limits, baseline.Limits, 8),
            docPath: doc.DocPath,
            docName: doc.DocName,
            contentCards: MergeCards(parsed.Cards, baseline.ContentCards ?? []));
    }

    private static string BuildUserPrompt(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => $"""
Doc: {doc.DocName}
Path: {doc.DocPath}
Category: {doc.Category ?? "unknown"}; pages: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}; indexedVersion: {doc.IndexedVersion}
Baseline language: {baseline.Language}
Requested output language: {NormalizeLanguage(null, baseline.Language)}
Baseline summary: {TrimText(baseline.SummaryText, 420)}
Keywords: {string.Join(", ", baseline.Keywords.Take(16))}
Entities: {string.Join(", ", baseline.Entities.Take(12))}
Topics: {string.Join(", ", baseline.Topics.Take(8))}
Questions: {string.Join(" | ", baseline.HypotheticalQuestions.Take(4))}
Baseline cards: {string.Join(" | ", (baseline.ContentCards ?? []).Take(12).Select(FormatPromptCard))}
Sections: {string.Join(" | ", sectionTitles.Where(static title => !string.IsNullOrWhiteSpace(title)).Select(static title => title.Trim()).Take(5))}
Excerpts: {string.Join(" | ", excerpts.Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt)).Select(TrimExcerpt).Take(2))}
""";

    private static bool TryParseProfile(string? content, out ParsedProfile profile)
    {
        profile = default;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var cleaned = ExtractJsonObject(StripCodeFence(content.Trim()));
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        try
        {
            using var json = JsonDocument.Parse(cleaned);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = json.RootElement;
            profile = new ParsedProfile(
                Language: ReadString(root, "language"),
                Summary: ReadString(root, "summary"),
                Keywords: ReadArray(root, "keywords"),
                Entities: ReadArray(root, "entities"),
                Topics: ReadArray(root, "topics"),
                Questions: ReadArray(root, "questions"),
                Limits: ReadArray(root, "limits"),
                Cards: ReadCards(root, "cards"));
            return !string.IsNullOrWhiteSpace(profile.Summary)
                || profile.Keywords.Count > 0
                || profile.Topics.Count > 0
                || profile.Questions.Count > 0
                || profile.Cards.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeText(value.GetString())
            : null;

    private static IReadOnlyList<string> ReadArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => NormalizeText(item.GetString()))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> Merge(
        IReadOnlyList<string> preferred,
        IReadOnlyList<string> fallback,
        int maxItems)
        => preferred
            .Concat(fallback)
            .Select(NormalizeText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();

    private static IReadOnlyList<DocumentProfileContentCard> MergeCards(
        IReadOnlyList<DocumentProfileContentCard> preferred,
        IReadOnlyList<DocumentProfileContentCard> fallback)
        => preferred
            .Concat(fallback)
            .GroupBy(card => NormalizeText(card.Title)?.ToLowerInvariant() ?? string.Empty, StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => group.First())
            .Take(240)
            .ToArray();

    private static IReadOnlyList<DocumentProfileContentCard> ReadCards(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<DocumentProfileContentCard>();

        var cards = new List<DocumentProfileContentCard>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            cards.Add(new DocumentProfileContentCard(
                title,
                ReadInt(item, "pageStart"),
                ReadInt(item, "pageEnd"),
                ReadString(item, "kind") ?? "llm_content_card",
                ReadArray(item, "signals")));
        }

        return cards;
    }

    private static int? ReadInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) && parsed > 0
            ? parsed
            : null;

    private static string FormatPromptCard(DocumentProfileContentCard card)
    {
        var pages = card.PageStart is > 0
            ? $" p.{card.PageStart}{(card.PageEnd is > 0 && card.PageEnd != card.PageStart ? "-" + card.PageEnd : string.Empty)}"
            : string.Empty;
        return $"{card.Title}{pages} [{string.Join(", ", card.Signals.Take(4))}]";
    }

    private static string TrimExcerpt(string value)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        return TrimText(normalized, 260);
    }

    private static string TrimText(string value, int maxLength)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        if (normalized.Length <= maxLength)
            return normalized;

        var cut = normalized.LastIndexOf(' ', Math.Max(0, maxLength - 1));
        if (cut < maxLength / 3)
            cut = maxLength;
        return normalized[..cut].TrimEnd();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        var match = CodeFenceRegex().Match(trimmed);
        return match.Success ? match.Groups["json"].Value.Trim() : trimmed;
    }

    private static string? ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start
            ? trimmed[start..(end + 1)].Trim()
            : null;
    }

    private static string NormalizeLanguage(string? preferred, string fallback)
    {
        var language = string.IsNullOrWhiteSpace(preferred)
            ? fallback
            : preferred.Trim().ToLowerInvariant();

        return language is "fr" or "en" or "es" or "pt" or "de" or "it" or "und"
            ? language
            : string.IsNullOrWhiteSpace(fallback) ? "und" : fallback.Trim().ToLowerInvariant();
    }

    private readonly record struct ParsedProfile(
        string? Language,
        string? Summary,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> Entities,
        IReadOnlyList<string> Topics,
        IReadOnlyList<string> Questions,
        IReadOnlyList<string> Limits,
        IReadOnlyList<DocumentProfileContentCard> Cards);

    [GeneratedRegex(@"^```(?:json)?\s*(?<json>.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();
}
