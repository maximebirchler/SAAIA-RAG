internal static class ExtractionQualityPolicy
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

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(unit.Text))
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
}
