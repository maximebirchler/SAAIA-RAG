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
You enrich enterprise document retrieval profiles.
Return JSON only with this shape:
{"language":"fr|en|es|pt|de|it|und","summary":"...","keywords":["..."],"entities":["..."],"topics":["..."],"questions":["..."],"limits":["..."]}
Constraints:
- Use only the provided document metadata, baseline profile, section titles, and excerpts.
- Do not invent exact facts, quantities, steps, page references, or certifications.
- Summary: max 120 words.
- keywords/entities/topics: compact, retrieval-friendly, no duplicates.
- questions: likely user questions that should retrieve this document, max 8.
- limits: short warnings about what the profile cannot prove without page chunks.
- No markdown, no prose outside JSON.
""";

        var userPrompt = BuildUserPrompt(doc, baseline, sectionTitles, excerpts);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 720, temperature: 0.1, ct);
        if (!TryParseProfile(completion, out var parsed))
            return null;

        var summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? baseline.SummaryText
            : parsed.Summary!;
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        return DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: string.IsNullOrWhiteSpace(parsed.Language) ? baseline.Language : parsed.Language,
            summaryText: summary,
            keywords: Merge(parsed.Keywords, baseline.Keywords, 32),
            entities: Merge(parsed.Entities, baseline.Entities, 32),
            topics: Merge(parsed.Topics, baseline.Topics, 20),
            hypotheticalQuestions: Merge(parsed.Questions, baseline.HypotheticalQuestions, 10),
            limits: Merge(parsed.Limits, baseline.Limits, 8),
            docPath: doc.DocPath,
            docName: doc.DocName);
    }

    private static string BuildUserPrompt(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => $"""
Document:
- name: {doc.DocName}
- path: {doc.DocPath}
- category: {doc.Category ?? "unknown"}
- pageCount: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}
- indexedVersion: {doc.IndexedVersion}

Baseline deterministic profile:
- language: {baseline.Language}
- summary: {baseline.SummaryText}
- keywords: {string.Join(", ", baseline.Keywords.Take(24))}
- entities: {string.Join(", ", baseline.Entities.Take(24))}
- topics: {string.Join(", ", baseline.Topics.Take(16))}
- questions: {string.Join(" | ", baseline.HypotheticalQuestions.Take(8))}

Section titles:
{string.Join(Environment.NewLine, sectionTitles.Where(static title => !string.IsNullOrWhiteSpace(title)).Take(8).Select(static title => $"- {title.Trim()}"))}

Excerpt highlights:
{string.Join(Environment.NewLine, excerpts.Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt)).Select(TrimExcerpt).Take(5).Select(static excerpt => $"- {excerpt}"))}
""";

    private static bool TryParseProfile(string? content, out ParsedProfile profile)
    {
        profile = default;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var cleaned = StripCodeFence(content.Trim());
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
                Limits: ReadArray(root, "limits"));
            return !string.IsNullOrWhiteSpace(profile.Summary)
                || profile.Keywords.Count > 0
                || profile.Topics.Count > 0
                || profile.Questions.Count > 0;
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

    private static string TrimExcerpt(string value)
    {
        var normalized = NormalizeText(value) ?? string.Empty;
        if (normalized.Length <= 520)
            return normalized;

        var cut = normalized.LastIndexOf(' ', 519);
        if (cut < 180)
            cut = 520;
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

    private readonly record struct ParsedProfile(
        string? Language,
        string? Summary,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> Entities,
        IReadOnlyList<string> Topics,
        IReadOnlyList<string> Questions,
        IReadOnlyList<string> Limits);

    [GeneratedRegex(@"^```(?:json)?\s*(?<json>.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();
}
