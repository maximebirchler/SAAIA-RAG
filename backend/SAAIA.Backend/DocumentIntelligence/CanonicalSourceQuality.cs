using SAAIA.Contracts.DocumentIntelligence;

/// <summary>
/// Source warnings describe the observations used by a projection. A warning is
/// not a proof that its readable text failed OCR, nor a semantic accuracy score.
/// </summary>
internal static class CanonicalSourceQuality
{
    internal static IReadOnlyList<string> Combine(CanonicalPage page, IEnumerable<string> signals)
        => signals.Concat(page.QualityFlags).Distinct(StringComparer.Ordinal).ToArray();

    internal static PdfPageExtractionQuality ForRetrievalText(
        string text, int wordCount, IEnumerable<string> sourceSignals)
    {
        var observed = PdfPageExtractionQuality.FromText(text, wordCount, text.Length);
        var encodingSignals = observed.Signals.Where(static signal => signal is
            "replacement_chars_detected" or "replacement_chars_remaining" or "invalid_control_chars_detected");
        var signals = sourceSignals.Concat(encodingSignals).Distinct(StringComparer.Ordinal).ToArray();
        var corruptionObserved = signals.Any(static signal => signal is
            "replacement_chars_remaining" or "invalid_control_chars_detected");
        // A short heading or a structured table fragment is valid evidence.
        // Only empty text or observed encoding corruption changes this status;
        // geometric uncertainty, image semantics and inconclusive repair remain
        // explicit signals without inventing a universal OCR failure.
        return observed with
        {
            TextStatus = observed.TextEmpty ? "empty_text" : corruptionObserved ? "low_text" : "ok",
            TextSparse = observed.TextEmpty || corruptionObserved,
            OcrCandidate = observed.TextEmpty || corruptionObserved,
            Signals = signals
        };
    }
}
