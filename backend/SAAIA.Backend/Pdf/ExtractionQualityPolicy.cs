using System.Text.RegularExpressions;

internal static partial class ExtractionQualityPolicy
{
    public static bool ShouldRestrictUnitToTargetedReferences(ExtractedDocumentUnit unit)
    {
        if (unit.ExtractionQualitySignals is { Count: > 0 }
            && unit.ExtractionQualitySignals.Any(static signal =>
                string.Equals(signal, "replacement_chars_remaining", StringComparison.Ordinal)))
        {
            return true;
        }

        if (string.Equals(unit.ExtractionTextStatus, "empty_text", StringComparison.Ordinal))
            return true;

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(unit.Text)
            && !LooksLikeClassifierConfirmedRetrievalContent(unit.Text, unit.TokenCount))
        {
            return true;
        }

        if (LooksLikeShortIndexOrClassificationMetadata(unit.Text, unit.TokenCount))
            return true;

        if (LooksLikeShortStandaloneMetadataScheduleCard(unit.Text, unit.TokenCount))
            return true;

        return unit.ExtractionTextSparse && unit.TokenCount < 20;
    }

    public static bool ShouldUseUnitForProfileCards(ExtractedDocumentUnit unit)
    {
        if (!ShouldRestrictUnitToTargetedReferences(unit))
            return true;

        return LooksLikeTargetedReferenceCarrier(unit.Text)
            || LooksLikeStrongStandaloneHeading(unit.Text);
    }

    public static bool ShouldUseUnitForRetrievalWindow(ExtractedDocumentUnit unit)
        => !ShouldRestrictUnitToTargetedReferences(unit);

    public static bool IsPageUnreliableForEmbeddedCards(ExtractedPdfPage page)
    {
        var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
        if (quality.TextEmpty)
            return true;

        if (quality.TextSparse && page.WordCount < 20)
            return true;

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(page.Text))
            return true;

        return quality.Signals.Any(static signal =>
            string.Equals(signal, "replacement_chars_remaining", StringComparison.Ordinal));
    }

    private static bool LooksLikeTargetedReferenceCarrier(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && ExactMatchEntryExtractor.ExtractTargetedReferences(text).Any();

    private static bool LooksLikeClassifierConfirmedRetrievalContent(string? text, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(text) || tokenCount < 20)
            return false;

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        return string.Equals(signal.ContentRole, RetrievalContentClassifier.ContentRole, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(signal.NavigationReason)
            && signal.ContentDensityScore >= 0.35;
    }

    private static bool LooksLikeStrongStandaloneHeading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        if (normalized.Length is < 4 or > 120)
            return false;

        var letters = normalized.Where(char.IsLetter).ToArray();
        if (letters.Length < 4)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.70)
            && normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 10;
    }

    private static bool LooksLikeShortIndexOrClassificationMetadata(string? text, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(text) || tokenCount is < 4 or > 24)
            return false;

        var normalized = NormalizeShortLayoutMetadataText(text);
        if (normalized.Length is < 20 or > 260)
            return false;
        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022'))
            return false;

        var compact = Regex.Replace(normalized, @"\s+", string.Empty);
        var internalCodeCount = InternalReferenceCodeRegex().Matches(compact).Count;
        if (ContainsIndexMarkerRegex().IsMatch(normalized) && internalCodeCount > 0)
            return true;
        if (internalCodeCount >= 2)
            return true;

        var containsCategoryCue = ContainsCategoryOrClassificationCue(normalized);
        if (!containsCategoryCue)
            return false;

        var hasLayoutNumber = Regex.IsMatch(normalized, @"^\s*\d{1,4}\p{L}", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b\d{1,4}\s*$", RegexOptions.CultureInvariant)
            || ShortLayoutQuantityRegex().IsMatch(normalized);
        if (!hasLayoutNumber)
            return false;

        if (ShortSentenceVerbRegex().IsMatch(normalized))
            return false;

        var letters = normalized.Where(char.IsLetter).ToArray();
        if (letters.Length < 12)
            return false;

        var lowerOrUpperRatio = letters.Count(ch => char.IsUpper(ch) || char.IsLower(ch)) / (double)letters.Length;
        return lowerOrUpperRatio >= 0.80;
    }

    private static bool LooksLikeShortStandaloneMetadataScheduleCard(string? text, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(text) || tokenCount is < 5 or > 24)
            return false;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('.');
        if (normalized.Length is < 24 or > 220)
            return false;

        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022'))
            return false;

        if (ShortSentenceVerbRegex().IsMatch(normalized))
            return false;

        var labelCount = ShortMetadataLabelRegex().Matches(normalized).Count;
        if (labelCount < 2)
            return false;

        var scheduleValueCount = MetadataScheduleValueRegex().Matches(normalized).Count;
        if (scheduleValueCount == 0)
            return false;

        return scheduleValueCount >= 2 || tokenCount <= 16;
    }

    private static string NormalizeShortLayoutMetadataText(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        normalized = DigitBeforeUppercaseRegex().Replace(normalized, " ");
        normalized = LowercaseBeforeUppercaseRegex().Replace(normalized, " ");
        normalized = LowercaseBeforeLayoutQuantityRegex().Replace(normalized, " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool ContainsCategoryOrClassificationCue(string normalized)
        => ContainsCategoryCueRegex().IsMatch(normalized)
            || normalized.Contains("category", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("categories", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("categorie", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cat\u00e9gorie", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("classification", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:index|indice)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContainsIndexMarkerRegex();

    [GeneratedRegex(@"(?:^|\s)[\p{L}][\p{L}\p{N}'\u2019\u2018\-]{0,18}\s*[:;]", RegexOptions.CultureInvariant)]
    private static partial Regex ShortMetadataLabelRegex();

    [GeneratedRegex(@"\b\d+(?:[,.]\d+)?\s*(?:s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|heures?|hours?|j|jr|jours?|d|days?|dias?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataScheduleValueRegex();

    [GeneratedRegex(@"\b(?:category|categories|cat[e\u00e9]gories?|categoria|categor[i\u00ed]as?|kategorie(?:n)?|classification(?:s)?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContainsCategoryCueRegex();

    [GeneratedRegex(@"[\p{L}]{2,}\d[\p{L}\p{N}_\-]{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InternalReferenceCodeRegex();

    [GeneratedRegex(@"(?<=\d)(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex DigitBeforeUppercaseRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex LowercaseBeforeUppercaseRegex();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=\d{1,4}\s+(?:items?|elements?|entries?|parts?|pieces?|units?|unites?|pages?)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowercaseBeforeLayoutQuantityRegex();

    [GeneratedRegex(@"\b(?:for|pour|para|per|fur|fuer)?\s*\d{1,4}\s+(?:items?|elements?|entries?|parts?|pieces?|units?|unites?|pages?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShortLayoutQuantityRegex();

    [GeneratedRegex(@"\b(?:is|are|was|were|be|been|est|sont|sera|seront|will|must|should|doit|doivent|peut|peuvent)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ShortSentenceVerbRegex();

}
