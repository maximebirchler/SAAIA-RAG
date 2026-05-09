using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class TitleAnchorNormalizer
{
    private const int MinUsefulTokenLength = 3;

    internal static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(value);
        normalized = FoldDiacritics(normalized);
        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    internal static string[] BuildTitleTokens(string? value, int maxTokens = 16)
    {
        var normalized = NormalizeTitle(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return TokenRegex().Matches(normalized)
            .Select(static match => match.Value)
            .Where(IsUsefulToken)
            .Distinct(StringComparer.Ordinal)
            .Take(maxTokens)
            .ToArray();
    }

    internal static bool IsUsefulTitleCandidate(string? title)
    {
        var normalized = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Length < 4 || normalized.Length > 180)
            return false;

        if (OnlyPageOrNumberRegex().IsMatch(normalized))
            return false;

        if (GenericNavigationLabelRegex().IsMatch(normalized))
            return false;

        var tokens = TokenRegex().Matches(normalized).Select(static match => match.Value).ToArray();
        if (tokens.Length == 0)
            return false;

        var usefulTokens = tokens.Count(IsUsefulToken);
        if (usefulTokens == 0)
            return HasTechnicalSignal(normalized);

        if (tokens.Length <= 2)
            return usefulTokens > 0 && (normalized.Length >= 8 || HasTechnicalSignal(normalized));

        return usefulTokens >= Math.Min(2, tokens.Length);
    }

    internal static double ComputeTokenOverlapScore(IReadOnlyCollection<string> queryTokens, IReadOnlyCollection<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            return 0.0;

        var candidate = candidateTokens as HashSet<string> ?? new HashSet<string>(candidateTokens, StringComparer.Ordinal);
        var overlap = queryTokens.Count(candidate.Contains);
        return overlap <= 0 ? 0.0 : overlap / (double)Math.Max(1, queryTokens.Count);
    }

    internal static bool ContainsAllUsefulTokens(string? candidate, IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0)
            return false;

        var normalized = NormalizeTitle(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return tokens.All(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static bool IsUsefulToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.Length < MinUsefulTokenLength && !token.Any(char.IsDigit))
            return false;

        return !GenericStopwords.Contains(token);
    }

    private static bool HasTechnicalSignal(string value)
        => value.Any(char.IsDigit)
            || value.Any(static ch => ch is '/' or '-' or '_')
            || value.Any(static ch => char.GetUnicodeCategory(ch) == UnicodeCategory.OtherLetter);

    private static string FoldDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> GenericStopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "as", "at", "be", "by", "for", "from", "in", "into", "is", "it", "of", "on", "or", "the", "to", "with",
        "au", "aux", "avec", "ce", "ces", "dans", "de", "des", "du", "en", "et", "la", "le", "les", "ou", "par", "pour", "sur",
        "al", "con", "del", "el", "en", "la", "las", "los", "para", "por", "un", "una",
        "com", "das", "dos", "para", "por", "uma",
        "der", "die", "das", "ein", "eine", "fur", "fuer", "im", "mit", "und", "von", "zu",
        "con", "dei", "del", "della", "di", "il", "gli", "per", "una"
    };

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\-/._]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(?:p|pp|page|pages?|pagina|pagine|seite|seiten|pag|pags?)?\s*\d{1,4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OnlyPageOrNumberRegex();

    [GeneratedRegex(@"^(?:contents?|table of contents|sommaire|index|indice|inhaltsverzeichnis|toc|references?|bibliography|glossary|lexique|appendix|annex(?:e)?s?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericNavigationLabelRegex();
}
