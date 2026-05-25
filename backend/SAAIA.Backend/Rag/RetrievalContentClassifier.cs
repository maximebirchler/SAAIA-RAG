using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class RetrievalContentClassifier
{
    internal const string ContentRole = "content";
    internal const string NavigationRole = "navigation";
    internal const string MixedNavigationContentRole = "mixed_navigation_content";
    internal const string NavigationChunkType = "navigation_index_v1";

    public static RetrievalChunkClassification ClassifyChunk(string text, string chunkType)
    {
        var signal = AnalyzeChunk(text);
        if (!string.Equals(signal.ContentRole, NavigationRole, StringComparison.Ordinal))
        {
            return new RetrievalChunkClassification(
                signal.ContentRole,
                chunkType,
                signal.NavigationReason,
                OriginalChunkType: null,
                signal.NavigationScore,
                signal.ContentDensityScore);
        }

        return new RetrievalChunkClassification(
            NavigationRole,
            NavigationChunkType,
            signal.NavigationReason,
            chunkType,
            signal.NavigationScore,
            signal.ContentDensityScore);
    }

    public static bool IsNavigationChunkType(string? chunkType)
        => string.Equals(chunkType, NavigationChunkType, StringComparison.Ordinal);

    public static bool IsPredominantlyNavigationContent(
        string? contentRole,
        string? chunkType,
        double navigationScore,
        double contentDensityScore)
    {
        if (string.Equals(contentRole, NavigationRole, StringComparison.Ordinal)
            || IsNavigationChunkType(chunkType))
        {
            return true;
        }

        return string.Equals(contentRole, MixedNavigationContentRole, StringComparison.Ordinal)
               && navigationScore >= 0.70
               && contentDensityScore < 0.55;
    }

    internal static string? DetectNavigationReason(string? text)
    {
        var signal = AnalyzeChunk(text);
        return string.Equals(signal.ContentRole, ContentRole, StringComparison.Ordinal)
            ? null
            : signal.NavigationReason;
    }

    internal static RetrievalNavigationSignal AnalyzeChunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, 0.0);

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeForNavigationLookup(folded)} ";
        var hasStrongMarker = HasStrongNavigationMarker(folded, padded);
        var inlinePageNumberBoundaries = CountInlinePageNumberBoundaries(text);
        var shape = AnalyzeShape(text);
        var hasListShape = CountBulletMarkers(text) >= 8
            || inlinePageNumberBoundaries >= 5
            || CountShortNumberTokens(text) >= 8
            || shape.DotLeaderLineCount >= 3
            || shape.PageReferenceLineCount >= 5
            || LooksLikeCompactIndexCatalog(text, padded)
            || LooksLikeTitleListChunk(text);
        var contentDensityScore = ComputeContentDensityScore(text, folded, shape);
        var hasLayoutIndexArtifact = ContainsLayoutIndexArtifact(folded);
        var hasDenseMeasuredContent = LooksLikeDenseMeasuredContent(text, folded, shape);
        var hasMeasuredSequentialContent = LooksLikeMeasuredSequentialContent(text, shape);

        string? reason = null;
        var navigationScore = 0.0;

        var hasExplicitTocMarker = ContainsExplicitTableOfContentsMarker(folded, padded);
        var hasShortTocMarker = ContainsShortTableOfContentsMarker(padded);
        if (hasExplicitTocMarker
            || (hasShortTocMarker
                && (inlinePageNumberBoundaries >= 3
                    || CountShortNumberTokens(text) >= 4
                    || shape.PageReferenceLineCount >= 2
                    || shape.DotLeaderLineCount >= 1
                    || hasListShape)))
        {
            reason = "table_of_contents";
            navigationScore = 0.95;
        }

        var looksStructured = LooksLikeStructuredContent(folded);
        if (hasDenseMeasuredContent
            && !hasExplicitTocMarker
            && shape.DotLeaderLineCount == 0
            && shape.PageReferenceLineCount < 2
            && contentDensityScore >= 0.65)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.72));
        }

        if (hasMeasuredSequentialContent
            && !hasExplicitTocMarker
            && !hasShortTocMarker
            && !hasStrongMarker
            && !hasLayoutIndexArtifact)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.70));
        }

        if (reason is null
            && looksStructured
            && !hasListShape
            && !folded.Contains("fiche-index", StringComparison.Ordinal)
            && !folded.Contains("fiche index", StringComparison.Ordinal))
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null
            && hasLayoutIndexArtifact
            && looksStructured
            && inlinePageNumberBoundaries < 5
            && shape.PageReferenceLineCount < 5)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null && hasLayoutIndexArtifact && !hasListShape)
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);

        if (reason is null
            && (hasStrongMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal)))
        {
            reason = "explicit_index_marker";
            navigationScore = Math.Max(navigationScore, 0.88);
        }

        if (reason is null && padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (hasListShape)
            {
                reason = "weak_index_marker_with_list_shape";
                navigationScore = Math.Max(navigationScore, 0.76);
            }

            if (reason is null)
                return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);
        }

        if (reason is null && hasListShape && inlinePageNumberBoundaries >= 5)
        {
            reason = "inline_page_number_list";
            navigationScore = Math.Max(navigationScore, 0.82);
        }

        if (reason is null && hasListShape && CountShortNumberTokens(text) >= 8)
        {
            reason = "numeric_title_catalog";
            navigationScore = Math.Max(navigationScore, 0.76);
        }

        if (reason is null && shape.DotLeaderLineCount >= 3)
        {
            reason = "title_list_with_page_refs";
            navigationScore = Math.Max(navigationScore, 0.80);
        }

        if (reason is null && LooksLikeTitleListChunk(text))
        {
            reason = inlinePageNumberBoundaries >= 3
                ? "compact_title_catalog_with_page_refs"
                : "dense_title_catalog";
            navigationScore = Math.Max(navigationScore, inlinePageNumberBoundaries >= 3 ? 0.78 : 0.76);
        }

        if (reason is null && hasListShape)
        {
            reason = "title_list_shape";
            navigationScore = Math.Max(navigationScore, 0.64);
        }

        if (reason is null)
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, contentDensityScore);

        if ((hasDenseMeasuredContent || contentDensityScore >= 0.70)
            && navigationScore < 0.90
            && !hasExplicitTocMarker
            && !hasStrongMarker
            && shape.DotLeaderLineCount == 0
            && shape.PageReferenceLineCount < 2
            && inlinePageNumberBoundaries < 3)
        {
            return new RetrievalNavigationSignal(ContentRole, null, 0.0, Math.Max(contentDensityScore, 0.72));
        }

        if (shape.ShortLineRatio >= 0.65 && shape.PageReferenceLineCount >= 3)
            navigationScore = Math.Max(navigationScore, 0.82);
        if (shape.LongLineRatio >= 0.35 || looksStructured)
            contentDensityScore = Math.Max(contentDensityScore, looksStructured ? 0.70 : 0.50);
        if (!looksStructured
            && (inlinePageNumberBoundaries >= 5
                || (shape.LongLineRatio < 0.30
                    && (shape.DotLeaderLineCount >= 3 || shape.PageReferenceLineCount >= 5))))
        {
            contentDensityScore = Math.Min(contentDensityScore, 0.35);
        }
        if (contentDensityScore >= 0.55 && navigationScore < 0.90)
            navigationScore = Math.Min(navigationScore, 0.69);

        var role = navigationScore >= 0.90 || (navigationScore >= 0.72 && contentDensityScore < 0.50)
            ? NavigationRole
            : navigationScore >= 0.55
                ? MixedNavigationContentRole
                : ContentRole;

        return new RetrievalNavigationSignal(
            role,
            string.Equals(role, ContentRole, StringComparison.Ordinal) ? null : reason,
            Math.Clamp(navigationScore, 0.0, 1.0),
            Math.Clamp(contentDensityScore, 0.0, 1.0));
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

    internal static bool LooksLikeMeasuredSequentialContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return LooksLikeMeasuredSequentialContent(text, AnalyzeShape(text));
    }

    private static bool LooksLikeMeasuredSequentialContent(
        string text,
        RetrievalNavigationShape shape)
    {
        if (shape.DotLeaderLineCount > 0 || shape.PageReferenceLineCount >= 2)
            return false;

        if (CountWords(text) < 50)
            return false;

        if (CountMeasurementOrSpecificationTokens(text) < 4)
            return false;

        return CountInlineOrdinalBodyMarkers(text) >= 3;
    }

    private static bool LooksLikeDenseMeasuredContent(
        string text,
        string foldedText,
        RetrievalNavigationShape shape)
    {
        var words = CountWords(text);
        if (words < 28)
            return false;

        var measurementCount = CountMeasurementOrSpecificationTokens(text);
        if (measurementCount < 4)
            return false;

        var hasContentCue = DenseMeasuredContentCueRegex().IsMatch(foldedText);
        var hasStructuredBodyShape = CountBulletMarkers(text) >= 4
            || shape.LongLineRatio >= 0.25
            || words >= 60;

        return hasContentCue || hasStructuredBodyShape;
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

    private static bool ContainsExplicitTableOfContentsMarker(string foldedText, string paddedNormalizedText)
        => foldedText.Contains("table des matieres", StringComparison.Ordinal)
            || foldedText.Contains("table of contents", StringComparison.Ordinal)
            || foldedText.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice general ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenido ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de contenidos ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice de materias ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice analitico ", StringComparison.Ordinal);

    private static bool ContainsShortTableOfContentsMarker(string paddedNormalizedText)
        => paddedNormalizedText.Contains(" sommaire ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" contents ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" sommario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" sumario ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" indice ", StringComparison.Ordinal)
            || paddedNormalizedText.Contains(" toc ", StringComparison.Ordinal);

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i]))
                continue;

            var start = i;
            while (i < text.Length && char.IsDigit(text[i]))
                i++;

            var digitRun = i - start;
            if (digitRun is <= 0 or > 4)
                continue;

            var j = i;
            while (j < text.Length && (char.IsWhiteSpace(text[j]) || text[j] is '-' or '\u2013' or '\u2014' or '.' or ')'))
                j++;

            if (j < text.Length && char.IsLetter(text[j]) && char.IsUpper(text[j]))
                count++;

            i--;
        }

        return count;
    }

    private static bool LooksLikeCompactIndexCatalog(string text, string paddedNormalizedText)
        => (paddedNormalizedText.Contains(" index ", StringComparison.Ordinal)
            || paddedNormalizedText.TrimStart().StartsWith("index", StringComparison.Ordinal))
            && CountWords(text) >= 20
            && (CountInlinePageNumberBoundaries(text) >= 3 || CountShortNumberTokens(text) >= 5);

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

    private static int CountShortNumberTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in ShortNumberTokenRegex().Matches(text))
            count++;

        return count;
    }

    private static int CountMeasurementOrSpecificationTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in MeasurementOrSpecificationRegex().Matches(text))
            count++;

        return count;
    }

    private static int CountInlineOrdinalBodyMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        foreach (Match _ in InlineOrdinalBodyMarkerRegex().Matches(text))
            count++;

        return count;
    }

    private static RetrievalNavigationShape AnalyzeShape(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            lines = [text.Trim()];

        var shortLines = 0;
        var longLines = 0;
        var pageReferenceLines = 0;
        var dotLeaderLines = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var words = CountWords(line);
            if (words is > 0 and <= 7)
                shortLines++;
            if (words >= 14 || line.Length >= 120)
                longLines++;
            if (LooksLikePageReferenceLine(trimmed))
                pageReferenceLines++;
            if (LooksLikeDotLeaderLine(trimmed))
                dotLeaderLines++;
        }

        return new RetrievalNavigationShape(
            LineCount: lines.Length,
            ShortLineRatio: lines.Length == 0 ? 0.0 : shortLines / (double)lines.Length,
            LongLineRatio: lines.Length == 0 ? 0.0 : longLines / (double)lines.Length,
            PageReferenceLineCount: pageReferenceLines,
            DotLeaderLineCount: dotLeaderLines);
    }

    private static bool LooksLikeDotLeaderLine(string line)
        => !string.IsNullOrWhiteSpace(line)
            && line.Contains("..", StringComparison.Ordinal)
            && PageNumberAtLineEndRegex().IsMatch(line);

    private static bool LooksLikePageReferenceLine(string line)
        => !string.IsNullOrWhiteSpace(line)
            && CountWords(line) <= 12
            && PageNumberAtLineEndRegex().IsMatch(line);

    private static double ComputeContentDensityScore(string text, string foldedText, RetrievalNavigationShape shape)
    {
        var words = CountWords(text);
        if (words == 0)
            return 0.0;

        var textWithoutDotLeaders = DotLeaderSequenceRegex().Replace(text, " ");
        var sentenceMarkers = textWithoutDotLeaders.Count(static ch => ch is '.' or '!' or '?' or ';');
        var sentenceDensity = Math.Clamp(sentenceMarkers / Math.Max(1.0, words / 20.0), 0.0, 1.0);
        var longLineSignal = Math.Clamp(shape.LongLineRatio * 1.4, 0.0, 1.0);
        var structuredSignal = LooksLikeStructuredContent(foldedText) ? 1.0 : 0.0;

        return Math.Clamp(
            (sentenceDensity * 0.35)
            + (longLineSignal * 0.35)
            + (structuredSignal * 0.30),
            0.0,
            1.0);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                    count++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    private static bool ContainsLayoutIndexArtifact(string foldedText)
        => foldedText.Contains("[index", StringComparison.Ordinal)
            || foldedText.Contains(" index: ", StringComparison.Ordinal)
            || foldedText.EndsWith(" index:", StringComparison.Ordinal);

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

    [GeneratedRegex(@"\b(?:index|liste|list|catalogue|catalog|inventaire|inventory|indice\s+(?:general|de\s+contenidos?|de\s+materias?|analitico)|sumario|sommario|inhaltsverzeichnis)\s+(?:des?|de|du|d['\u2019]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:index|liste|list|catalogue|catalog|inventory|sumario|sommario)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StrongNavigationMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CountOrStepMarkerRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d+\s*[\.)]\s+\p{L}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{1,4}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNumberTokenRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d+(?:[,.]\d+)?\s*(?:%|°\s*[cfk]?|kg|g|mg|l|ml|cl|dl|m|cm|mm|km|h|min|mn|s|sec|w|kw|v|kv|a|ma|hz|khz|mhz|pa|kpa|bar|psi|nm|rpm|tr/min|chf|eur|usd|gb|mb|kb|tb)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MeasurementOrSpecificationRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])\d{1,2}\s+(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex InlineOrdinalBodyMarkerRegex();

    [GeneratedRegex(@"\b(?:preparation|preparacion|preparacao|preparazione|procedure|procedures|procedimiento|procedimento|procedura|instructions?|instruction|etapes?|steps?|passos?|schritte?|material|materiel|materials|materiaux|component|components|composant|composants|assembly|assemblage|montage|configuration|installation|maintenance|controle|control|verification|pruefung|prufung|pruefung|verificacion|verifica)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DenseMeasuredContentCueRegex();

    [GeneratedRegex(@"\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberAtLineEndRegex();

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DotLeaderSequenceRegex();
}

internal sealed record RetrievalChunkClassification(
    string ContentRole,
    string ChunkType,
    string? NavigationReason,
    string? OriginalChunkType,
    double NavigationScore,
    double ContentDensityScore);

internal sealed record RetrievalNavigationSignal(
    string ContentRole,
    string? NavigationReason,
    double NavigationScore,
    double ContentDensityScore);

internal sealed record RetrievalNavigationShape(
    int LineCount,
    double ShortLineRatio,
    double LongLineRatio,
    int PageReferenceLineCount,
    int DotLeaderLineCount);
