internal static class ExtractionQualityDiagnostics
{
    internal static ExtractionPageReview AssessPage(
        int wordCount,
        int charCount,
        int imageCount,
        int unitCount,
        int suspiciousUnitCount,
        int chunkCount,
        IReadOnlyCollection<string>? extractionSignals = null)
    {
        var pageQuality = PdfPageExtractionQuality.FromCounts(wordCount, charCount);
        var signals = new List<string>(pageQuality.Signals);
        if (extractionSignals is not null)
        {
            foreach (var signal in extractionSignals)
            {
                if (!string.IsNullOrWhiteSpace(signal))
                    signals.Add(signal.Trim());
            }
        }

        if (imageCount > 0)
            signals.Add("page_contains_images");
        if (unitCount == 0)
            signals.Add("no_units_on_page");
        if (chunkCount == 0)
            signals.Add("no_chunks_on_page");
        if (suspiciousUnitCount > 0)
            signals.Add("probable_ocr_noise_units");

        var status = ResolveStatus(pageQuality, wordCount, charCount, imageCount, unitCount, suspiciousUnitCount, chunkCount);
        var manualReviewRecommended = status.StartsWith("manual_review_", StringComparison.Ordinal);

        return new ExtractionPageReview(
            Status: status,
            ExtractionConfidence: ResolveConfidence(status),
            ManualReviewRecommended: manualReviewRecommended,
            TextStatus: pageQuality.TextStatus,
            TextEmpty: pageQuality.TextEmpty,
            TextSparse: pageQuality.TextSparse,
            OcrCandidate: pageQuality.OcrCandidate,
            AverageCharsPerWord: pageQuality.AverageCharsPerWord,
            Signals: signals.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string ResolveStatus(
        PdfPageExtractionQuality quality,
        int wordCount,
        int charCount,
        int imageCount,
        int unitCount,
        int suspiciousUnitCount,
        int chunkCount)
    {
        if (quality.TextEmpty)
        {
            if (chunkCount > 0 && imageCount <= 0)
                return "page_ok_indexed_by_context";

            return imageCount > 0
                ? "manual_review_empty_text"
                : "page_ok_empty_text";
        }
        if (suspiciousUnitCount > 0)
            return "manual_review_probable_ocr_noise";
        if (quality.TextSparse)
            return imageCount > 0 && unitCount == 0
                ? "manual_review_low_text"
                : "page_ok_low_value_text";
        if (unitCount == 0)
        {
            if (chunkCount > 0)
                return "page_ok_indexed_by_context";

            return wordCount >= 30 || charCount >= 200
                ? "manual_review_text_not_indexed"
                : "page_ok_low_value_text";
        }
        if (imageCount > 0)
            return "page_ok_with_images";

        return "page_ok";
    }

    private static double ResolveConfidence(string status)
        => status switch
        {
            "page_ok" => 1.0,
            "page_ok_with_images" => 0.86,
            "page_ok_indexed_by_context" => 0.84,
            "page_ok_empty_text" => 0.80,
            "page_ok_low_value_text" => 0.78,
            "manual_review_probable_ocr_noise" => 0.45,
            "manual_review_text_not_indexed" => 0.40,
            "manual_review_low_text" => 0.35,
            "manual_review_empty_text" => 0.15,
            _ => 0.50
        };
}

internal sealed record ExtractionPageReview(
    string Status,
    double ExtractionConfidence,
    bool ManualReviewRecommended,
    string TextStatus,
    bool TextEmpty,
    bool TextSparse,
    bool OcrCandidate,
    double AverageCharsPerWord,
    string[] Signals);
