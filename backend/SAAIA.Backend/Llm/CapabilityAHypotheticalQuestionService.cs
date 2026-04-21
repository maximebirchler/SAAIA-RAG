using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Backend;

internal sealed partial class CapabilityAHypotheticalQuestionService
{
    private readonly LocalLlmChatClient _llmClient;

    public CapabilityAHypotheticalQuestionService(LocalLlmChatClient llmClient)
    {
        _llmClient = llmClient;
    }

    internal async Task<IReadOnlyList<string>> BuildQuestionsAsync(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        var fallback = RuntimeCapabilityAEnrichmentStore.BuildHypotheticalQuestions(docName, sectionTitles, excerpts);
        if (!_llmClient.IsConfigured)
            return fallback;

        var systemPrompt = """
You generate concise hypothetical retrieval questions for enterprise document enrichment.
Return JSON only with the shape {"questions":["..."]}.
Constraints:
- 2 to 4 questions
- each question must be standalone and actionable
- no markdown
- no explanations
- keep each question under 140 characters
""";

        var userPrompt = BuildUserPrompt(docName, sectionTitles, excerpts);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 220, temperature: 0.1, ct);
        var parsed = ParseQuestions(completion);

        return parsed.Count == 0 ? fallback : parsed;
    }

    private static string BuildUserPrompt(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var normalizedSections = sectionTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title.Trim())
            .Take(3)
            .ToArray();
        var normalizedExcerpts = excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .Select(NormalizeExcerpt)
            .Take(2)
            .ToArray();

        return $"""
Document name: {docName}
Section titles:
{string.Join(Environment.NewLine, normalizedSections.Select(static title => $"- {title}"))}
Excerpt highlights:
{string.Join(Environment.NewLine, normalizedExcerpts.Select(static excerpt => $"- {excerpt}"))}
Generate hypothetical user questions that would help retrieve this document.
""";
    }

    private static IReadOnlyList<string> ParseQuestions(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var cleaned = content.Trim();
        cleaned = StripCodeFence(cleaned);

        if (TryParseJsonQuestions(cleaned, out var jsonQuestions))
            return jsonQuestions;

        return cleaned
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => LeadingBulletRegex().Replace(line, string.Empty).Trim())
            .Select(NormalizeQuestion)
            .Where(static question => !string.IsNullOrWhiteSpace(question))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static bool TryParseJsonQuestions(string content, out IReadOnlyList<string> questions)
    {
        questions = Array.Empty<string>();

        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var questionsElement))
            {
                questions = NormalizeQuestions(questionsElement);
                return true;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                questions = NormalizeQuestions(root);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizeQuestions(JsonElement element)
        => element.ValueKind != JsonValueKind.Array
            ? Array.Empty<string>()
            : element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Select(NormalizeQuestion)
                .Where(static question => !string.IsNullOrWhiteSpace(question))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray()!;

    private static string NormalizeExcerpt(string excerpt)
    {
        var normalized = string.Join(" ", excerpt
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

        return normalized.Length <= 220
            ? normalized
            : normalized[..217].TrimEnd() + "...";
    }

    private static string NormalizeQuestion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Trim('"', '\'');
        normalized = Regex.Replace(normalized, @"\s+", " ");
        if (normalized.Length == 0)
            return string.Empty;

        if (!normalized.EndsWith("?", StringComparison.Ordinal))
            normalized += "?";

        return normalized;
    }

    private static string StripCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
            return value;

        var normalized = value.Trim();
        normalized = CodeFencePrefixRegex().Replace(normalized, string.Empty);
        normalized = CodeFenceSuffixRegex().Replace(normalized, string.Empty);
        return normalized.Trim();
    }

    [GeneratedRegex(@"^\s*```(?:json)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFencePrefixRegex();

    [GeneratedRegex(@"\s*```\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFenceSuffixRegex();

    [GeneratedRegex(@"^\s*(?:[-*]|\d+[.)])\s*")]
    private static partial Regex LeadingBulletRegex();
}
