using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class RetrievalContentClassifier
{
    internal const string ContentRole = "content";
    internal const string NavigationRole = "navigation";
    internal const string NavigationChunkType = "navigation_index_v1";

    public static RetrievalChunkClassification ClassifyChunk(string text, string chunkType)
    {
        var navigationReason = DetectNavigationReason(text);
        if (navigationReason is null)
        {
            return new RetrievalChunkClassification(
                ContentRole,
                chunkType,
                NavigationReason: null,
                OriginalChunkType: null);
        }

        return new RetrievalChunkClassification(
            NavigationRole,
            NavigationChunkType,
            navigationReason,
            chunkType);
    }

    public static bool IsNavigationChunkType(string? chunkType)
        => string.Equals(chunkType, NavigationChunkType, StringComparison.Ordinal);

    internal static string? DetectNavigationReason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeForNavigationLookup(folded)} ";
        var hasStrongMarker = HasStrongNavigationMarker(folded, padded);
        var hasListShape = CountBulletMarkers(text) >= 8
            || CountInlinePageNumberBoundaries(text) >= 5
            || LooksLikeTitleListChunk(text);

        if (folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || padded.Contains(" contents ", StringComparison.Ordinal))
        {
            return "table_of_contents";
        }

        if (LooksLikeStructuredContent(folded)
            && !hasListShape
            && !folded.Contains("fiche-index", StringComparison.Ordinal)
            && !folded.Contains("fiche index", StringComparison.Ordinal))
        {
            return null;
        }

        if (hasStrongMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal))
        {
            return "explicit_index_marker";
        }

        if (padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (hasListShape)
                return "weak_index_marker_with_list_shape";

            return null;
        }

        if (hasListShape && CountInlinePageNumberBoundaries(text) >= 5)
            return "inline_page_number_list";

        return hasListShape
            ? "title_list_shape"
            : null;
    }

    private static bool LooksLikeStructuredContent(string foldedText)
    {
        var hasItemizedSection = StructuredContentLexicon.ContainsItemizedCue(foldedText)
            || ContainsAny(foldedText, "resources", "ressources");
        var hasProcedureSection = StructuredContentLexicon.ContainsRetrievalProcedureCue(foldedText);
        var hasGovernanceSection = StructuredContentLexicon.ContainsGovernanceCue(foldedText);
        var hasCountOrSteps = CountOrStepMarkerRegex().IsMatch(foldedText)
            || CountNumberedSteps(foldedText) >= 2;

        return (hasItemizedSection && (hasProcedureSection || hasCountOrSteps))
            || (hasProcedureSection && hasCountOrSteps)
            || (hasGovernanceSection && hasCountOrSteps);
    }

    private static bool HasStrongNavigationMarker(string foldedText, string paddedNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(foldedText) || string.IsNullOrWhiteSpace(paddedNormalizedText))
            return false;

        if (foldedText.Contains("fiche-index", StringComparison.Ordinal)
            || foldedText.Contains("fiche index", StringComparison.Ordinal))
        {
            return true;
        }

        return StrongNavigationMarkerRegex().IsMatch(paddedNormalizedText);
    }

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var digitRun = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digitRun++;
                continue;
            }

            if (digitRun is > 0 and <= 4 && char.IsLetter(ch) && char.IsUpper(ch))
                count++;

            digitRun = 0;
        }

        return count;
    }

    private static bool LooksLikeTitleListChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;

        var words = 0;
        var capitalizedStarts = 0;
        var sentenceMarkers = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is '.' or '!' or '?' or ';' or ':' or '\u2022')
                sentenceMarkers++;

            if (char.IsLetter(ch))
            {
                if (!inWord)
                {
                    words++;
                    if (char.IsUpper(ch))
                        capitalizedStarts++;
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return (words >= 24
                && capitalizedStarts >= Math.Max(12, words / 3)
                && sentenceMarkers <= 2)
            || (words >= 40
                && capitalizedStarts >= 20
                && sentenceMarkers <= 2)
            || (CountLowerToUpperTransitions(text) >= 8 && sentenceMarkers <= 3);
    }

    private static int CountLowerToUpperTransitions(string text)
    {
        var count = 0;
        var previousWasLower = false;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch) && previousWasLower)
                count++;

            previousWasLower = char.IsLower(ch);
        }

        return count;
    }

    private static int CountNumberedSteps(string text)
    {
        var count = 0;
        foreach (Match _ in NumberedStepRegex().Matches(text))
            count++;

        return count;
    }

    private static string NormalizeForNavigationLookup(string text)
        => NavigationLookupRegex().Replace(text, " ").Trim();

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NavigationLookupRegex();

    [GeneratedRegex(@"\b(?:index|liste|list|catalogue|catalog|inventaire|inventory)\s+(?:des?|de|du|d['\u2019]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:index|liste|list|catalogue|catalog|inventory)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StrongNavigationMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountOrStepMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();
}

internal sealed record RetrievalChunkClassification(
    string ContentRole,
    string ChunkType,
    string? NavigationReason,
    string? OriginalChunkType);
