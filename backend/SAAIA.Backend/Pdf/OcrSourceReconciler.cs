/// <summary>Conserves observations; similarity is not proof that two facts are equal.</summary>
internal static class OcrSourceReconciler
{
    internal static PdfExtractionResult FromProcessedPdf(PdfExtractionResult processed,
        PdfExtractionResult sidecar, string engine, string language)
    {
        var samePageNumbers = processed.Pages.Select(static page => page.PageNumber)
            .SequenceEqual(sidecar.Pages.Select(static page => page.PageNumber));
        var pages = processed.Pages.Select(page =>
        {
            var observations = new List<PdfOcrPageObservation>
                { new(page.RawText ?? page.Text, engine, language, []) };
            if (samePageNumbers)
                observations.Add(new(sidecar.Pages.Single(candidate => candidate.PageNumber == page.PageNumber).RawText ?? "",
                    engine + "_sidecar", language, []));
            var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
            return page with
            {
                OcrObservations = observations,
                Quality = quality with { Signals = quality.Signals.Concat(new[]
                    { "ocr_output_pdf_observation", samePageNumbers ? "ocr_sidecar_page_count_matches" : "ocr_sidecar_page_mapping_unverified" })
                    .Distinct(StringComparer.Ordinal).ToArray() }
            };
        }).ToList();
        return processed with { Pages = pages, Quality = PdfExtractionQualitySummary.FromPages(pages),
            Source = "ocr_output_pdf", OcrLanguages = language };
    }

    internal static IReadOnlyList<string> NovelLines(string nativeText, string ocrText)
    {
        var known = nativeText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(ComparisonKey).Where(static key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var rawLine in ocrText.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Replace('\f', '\n').Split('\n'))
        {
            var line = PdfTextSanitizer.ForStorage(rawLine).Trim();
            var key = ComparisonKey(line);
            if (key.Length > 0 && known.Add(key))
                result.Add(line);
        }
        return result;
    }

    internal static bool RetainsNativePages(PdfExtractionResult native, PdfExtractionResult candidate)
    {
        if (!native.Pages.Select(static page => page.PageNumber).Order()
                .SequenceEqual(candidate.Pages.Select(static page => page.PageNumber).Order()))
            return false;
        var candidates = candidate.Pages.ToDictionary(static page => page.PageNumber);
        return native.Pages.All(page => RetainsText(page.Text, candidates[page.PageNumber].Text));
    }

    private static bool RetainsText(string native, string candidate)
    {
        // A bag/subsequence permits a new negation inside an existing sentence.
        // Require the existing observation as a whole contiguous text, including unknown glyphs.
        var source = string.Join(' ', native.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (source.Length == 0)
            return true;
        var target = string.Join(' ', candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var start = 0;
        while ((start = target.IndexOf(source, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + source.Length;
            if ((start == 0 || char.IsWhiteSpace(target[start - 1]))
                && (end == target.Length || char.IsWhiteSpace(target[end])))
                return true;
            start++;
        }
        return false;
    }

    private static string ComparisonKey(string text)
        // Keep word boundaries: "1 5" is not "15", and "not able" is not "notable".
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
