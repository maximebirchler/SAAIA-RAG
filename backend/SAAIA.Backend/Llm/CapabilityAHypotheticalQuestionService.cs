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
        CancellationToken ct,
        string? documentLanguage = null)
    {
        var resolvedLanguage = ResolveDocumentLanguage(documentLanguage, docName, sectionTitles, excerpts);
        var fallback = RuntimeCapabilityAEnrichmentStore.BuildHypotheticalQuestions(docName, sectionTitles, excerpts, resolvedLanguage);
        if (!_llmClient.IsConfigured)
            return fallback;

        var systemPrompt = """
You generate concise hypothetical retrieval questions for document enrichment.
Return JSON only with the shape {"questions":["..."]}.
Constraints:
- 2 to 4 questions
- each question must be standalone and actionable
- no markdown
- no explanations
- keep each question under 140 characters
- write questions in the document language when it is known; otherwise use the dominant excerpt language
""";

        var userPrompt = BuildUserPrompt(docName, sectionTitles, excerpts, resolvedLanguage);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 220, temperature: 0.1, ct);
        var parsed = ParseQuestions(completion);
        var grounded = FilterGroundedQuestions(
            parsed,
            docName,
            sectionTitles,
            excerpts,
            resolvedLanguage);

        return grounded.Count == 0 ? fallback : grounded;
    }

    internal async Task<IReadOnlyList<string>> BuildTagsAsync(
        string docName,
        string? category,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        var fallback = BuildDeterministicTags(docName, category, sectionTitles);
        if (!_llmClient.IsConfigured)
            return fallback;

        var systemPrompt = """
You generate compact enterprise metadata tags for document enrichment.
Return JSON only with the shape {"tags":["..."]}.
Constraints:
- 3 to 6 tags
- lowercase slug-like tags only
- no markdown
- no explanations
- avoid duplicate or overly generic tags
""";

        var userPrompt = BuildTagsUserPrompt(docName, category, sectionTitles, excerpts);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 160, temperature: 0.1, ct);
        var parsed = ParseTags(completion);

        return parsed.Count == 0 ? fallback : parsed;
    }

    private static string BuildUserPrompt(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string documentLanguage)
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
Document language: {documentLanguage}
Section titles:
{string.Join(Environment.NewLine, normalizedSections.Select(static title => $"- {title}"))}
Excerpt highlights:
{string.Join(Environment.NewLine, normalizedExcerpts.Select(static excerpt => $"- {excerpt}"))}
Generate hypothetical user questions that would help retrieve this document. Preserve the document language.
""";
    }

    private static string ResolveDocumentLanguage(
        string? documentLanguage,
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => DocumentLanguageResolver.FirstKnownLanguage(documentLanguage)
            ?? DocumentLanguageResolver.DetectDominantLanguage(string.Join(' ', new[]
            {
                docName,
                string.Join(' ', sectionTitles),
                string.Join(' ', excerpts)
            }))
            ?? "und";

    private static string BuildTagsUserPrompt(
        string docName,
        string? category,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => $"""
Document name: {docName}
Category: {category ?? "unknown"}
Section titles:
{string.Join(Environment.NewLine, sectionTitles.Where(static title => !string.IsNullOrWhiteSpace(title)).Take(5).Select(static title => $"- {title.Trim()}"))}
Excerpt highlights:
{string.Join(Environment.NewLine, excerpts.Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt)).Select(NormalizeExcerpt).Take(2).Select(static excerpt => $"- {excerpt}"))}
Generate metadata tags for filtering, review, and retrieval diagnostics.
""";

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

    private static IReadOnlyList<string> ParseTags(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var cleaned = StripCodeFence(content.Trim());
        if (TryParseJsonStringArray(cleaned, "tags", out var jsonTags))
            return jsonTags
                .Select(NormalizeTag)
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

        return cleaned
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => LeadingBulletRegex().Replace(line, string.Empty).Trim())
            .Select(NormalizeTag)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
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

    private static bool TryParseJsonStringArray(string content, string propertyName, out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();

        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var arrayElement))
            {
                values = ReadJsonStringArray(arrayElement);
                return true;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                values = ReadJsonStringArray(root);
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

    private static IReadOnlyList<string> ReadJsonStringArray(JsonElement element)
        => element.ValueKind != JsonValueKind.Array
            ? Array.Empty<string>()
            : element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray();

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

    private static IReadOnlyList<string> FilterGroundedQuestions(
        IReadOnlyList<string> questions,
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string documentLanguage)
    {
        if (questions.Count == 0)
            return questions;

        var groundingCorpus = BuildGroundingCorpus(docName, sectionTitles, excerpts);
        if (string.IsNullOrWhiteSpace(groundingCorpus))
            return Array.Empty<string>();

        var primaryLanguage = DocumentLanguageResolver.PrimarySubtag(documentLanguage);
        return questions
            .Where(static question => question.Length <= 140)
            .Where(question => !LooksLikeWrongLanguageQuestion(question, primaryLanguage))
            .Where(question => IsGroundedQuestion(question, groundingCorpus))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static string BuildGroundingCorpus(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => NormalizeForGrounding(string.Join(' ', new[]
        {
            docName,
            string.Join(' ', sectionTitles),
            string.Join(' ', excerpts)
        }));

    private static bool LooksLikeWrongLanguageQuestion(string question, string primaryLanguage)
    {
        if (string.IsNullOrWhiteSpace(primaryLanguage) || primaryLanguage is "und" or "en")
            return false;

        var normalized = " " + NormalizeForGrounding(question) + " ";
        var englishHits = CountContains(normalized, EnglishQuestionSignals);
        if (englishHits < 3)
            return false;

        var targetHits = primaryLanguage switch
        {
            "fr" => CountContains(normalized, [" que ", " quels ", " quelles ", " comment ", " quand ", " document "]),
            "es" => CountContains(normalized, [" que ", " cuales ", " como ", " cuando ", " documento "]),
            "pt" => CountContains(normalized, [" que ", " quais ", " como ", " quando ", " documento "]),
            "de" => CountContains(normalized, [" was ", " welche ", " wie ", " wann ", " dokument "]),
            "it" => CountContains(normalized, [" che ", " quali ", " come ", " quando ", " documento "]),
            "nl" => CountContains(normalized, [" welke ", " wat ", " hoe ", " wanneer ", " handleiding "]),
            _ => 0
        };
        return targetHits == 0;
    }

    private static int CountContains(string normalized, IReadOnlyList<string> needles)
        => needles.Count(needle => normalized.Contains(needle, StringComparison.Ordinal));

    private static bool IsGroundedQuestion(string question, string groundingCorpus)
    {
        var tokens = BuildGroundingTokens(question);
        if (tokens.Length == 0)
            return false;

        var matched = tokens.Count(token => TokenOccursInCorpus(token, groundingCorpus));
        if (tokens.Length <= 2)
            return matched >= 1;

        return matched >= 2 && matched >= (int)Math.Ceiling(tokens.Length * 0.40d);
    }

    private static string[] BuildGroundingTokens(string value)
        => NormalizeForGrounding(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !GroundingStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();

    private static bool TokenOccursInCorpus(string token, string groundingCorpus)
        => groundingCorpus.Contains(token, StringComparison.Ordinal)
            || (token.Length > 5 && groundingCorpus.Contains(token.TrimEnd('s'), StringComparison.Ordinal))
            || (token.Length > 5 && groundingCorpus.Contains(token.TrimEnd('e', 's'), StringComparison.Ordinal));

    private static string NormalizeForGrounding(string? value)
        => StructuredContentLexicon.NormalizeForStructuredLookup(value);

    private static IReadOnlyList<string> BuildDeterministicTags(
        string docName,
        string? category,
        IReadOnlyList<string> sectionTitles)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(category))
            tags.Add(NormalizeTag(category));

        foreach (var token in ExtractTagTokens(Path.GetFileNameWithoutExtension(docName)))
            tags.Add(token);

        foreach (var title in sectionTitles)
        {
            foreach (var token in ExtractTagTokens(title))
                tags.Add(token);
        }

        return tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static IEnumerable<string> ExtractTagTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var token in value
            .Split([' ', '-', '_', '/', '\\', ',', ';', ':', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTag)
            .Where(static token => token.Length >= 3))
        {
            yield return token;
        }
    }

    private static string NormalizeTag(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Trim()
                .ToLowerInvariant()
                .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
                .ToArray())
                .Trim('-');

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

    private static readonly string[] EnglishQuestionSignals =
    [
        " what ",
        " which ",
        " how ",
        " when ",
        " does ",
        " should ",
        " document ",
        " describe ",
        " describes ",
        " requirements ",
        " apply ",
        " relevant "
    ];

    private static readonly HashSet<string> GroundingStopwords = new(StringComparer.Ordinal)
    {
        "about", "avec", "cette", "dans", "document", "documents", "from", "pour",
        "section", "sections", "that", "this", "with", "sobre", "para", "esta",
        "este", "questo", "questa", "dokument", "seite", "pages", "page",
        "what", "which", "does", "says", "discuss", "describe", "describes",
        "comment", "quels", "quelles", "quoi", "points", "cles", "principaux",
        "cuales", "quais", "welche", "wichtigsten", "quali", "punti",
        "user", "users", "question", "questions", "help", "helps", "retrieve",
        "retrieval", "would", "should", "when", "where", "waar", "welke", "wanneer"
    };
}
