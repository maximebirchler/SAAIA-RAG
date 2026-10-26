sealed record ExtractedPdfPage(
    int PageNumber,
    string Text,
    int WordCount,
    int CharCount,
    byte[] Checksum,
    PdfPageExtractionQuality? Quality = null,
    int ImageCount = 0,
    string? RawText = null,
    double? WidthPoints = null,
    double? HeightPoints = null,
    string? NativeLayoutText = null,
    IReadOnlyList<ExtractedPdfLayoutBlock>? NativeLayoutBlocks = null,
    string? NativeLayoutAlgorithm = null,
    IReadOnlyList<ExtractedPdfImageRegion>? NativeImageRegions = null,
    IReadOnlyList<PdfOcrPageObservation>? OcrObservations = null,
    PdfNativeWordObservation? NativeWordObservation = null);

sealed record PdfPageExtractionQuality(
    string TextStatus,
    bool TextEmpty,
    bool TextSparse,
    bool OcrCandidate,
    double AverageCharsPerWord,
    string[] Signals,
    int RawReplacementCharCount = 0,
    int SanitizedReplacementCharCount = 0,
    bool EncodingRepairApplied = false)
{
    private const int SparseWordThreshold = 12;
    private const int SparseCharThreshold = 80;

    public static PdfPageExtractionQuality FromCounts(int wordCount, int charCount)
    {
        var textEmpty = wordCount <= 0 || charCount <= 0;
        var textSparse = !textEmpty && (wordCount < SparseWordThreshold || charCount < SparseCharThreshold);
        var signals = new List<string>();
        if (textEmpty)
            signals.Add("no_text_on_page");
        else if (textSparse)
            signals.Add("sparse_text_on_page");
        else
            signals.Add("text_extraction_ok");

        return new PdfPageExtractionQuality(
            TextStatus: textEmpty ? "empty_text" : textSparse ? "low_text" : "ok",
            TextEmpty: textEmpty,
            TextSparse: textSparse,
            OcrCandidate: textEmpty || textSparse,
            AverageCharsPerWord: wordCount <= 0 ? 0 : Math.Round((double)charCount / wordCount, 2),
            Signals: signals.ToArray());
    }

    public static PdfPageExtractionQuality FromText(string? text, int wordCount, int charCount)
    {
        var replacementCharCount = CountReplacementCharacters(text);
        return FromSanitizedText(
            text,
            wordCount,
            charCount,
            replacementCharCount,
            replacementCharCount);
    }

    public static PdfPageExtractionQuality FromSanitizedText(
        string? text,
        int wordCount,
        int charCount,
        int rawReplacementCharCount,
        int sanitizedReplacementCharCount)
    {
        var quality = FromCounts(wordCount, charCount);
        rawReplacementCharCount = Math.Max(0, rawReplacementCharCount);
        sanitizedReplacementCharCount = Math.Max(0, sanitizedReplacementCharCount);
        var invalidControlCharCount = CountInvalidControlCharacters(text);
        if (rawReplacementCharCount == 0
            && sanitizedReplacementCharCount == 0
            && invalidControlCharCount == 0)
        {
            return quality;
        }

        var hasRemainingReplacementCharacters = sanitizedReplacementCharCount > 0
            || (!string.IsNullOrEmpty(text) && text.Contains('\uFFFD', StringComparison.Ordinal));
        var hasInvalidControlCharacters = invalidControlCharCount > 0;
        var hasRemainingEncodingCorruption =
            hasRemainingReplacementCharacters || hasInvalidControlCharacters;

        return quality with
        {
            TextStatus = hasRemainingEncodingCorruption ? "low_text" : quality.TextStatus,
            TextSparse = hasRemainingEncodingCorruption || quality.TextSparse,
            OcrCandidate = hasRemainingEncodingCorruption || quality.OcrCandidate,
            Signals = quality.Signals
                .Where(static signal => !string.Equals(signal, "text_extraction_ok", StringComparison.Ordinal))
                .Concat(rawReplacementCharCount > 0 || sanitizedReplacementCharCount > 0
                    ? ["replacement_chars_detected"]
                    : Array.Empty<string>())
                .Concat(rawReplacementCharCount > sanitizedReplacementCharCount
                    ? ["replacement_chars_repaired"]
                    : Array.Empty<string>())
                .Concat(hasRemainingReplacementCharacters
                    ? ["replacement_chars_remaining"]
                    : Array.Empty<string>())
                .Concat(hasInvalidControlCharacters
                    ? ["invalid_control_chars_detected"]
                    : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RawReplacementCharCount = rawReplacementCharCount,
            SanitizedReplacementCharCount = sanitizedReplacementCharCount,
            EncodingRepairApplied = rawReplacementCharCount > sanitizedReplacementCharCount
        };
    }

    private static int CountReplacementCharacters(string? text)
        => string.IsNullOrEmpty(text)
            ? 0
            : text.Count(static ch => ch == '\uFFFD');

    private static int CountInvalidControlCharacters(string? text)
        => string.IsNullOrEmpty(text)
            ? 0
            : text.Count(static ch => ch is >= '\u0080' and <= '\u009F');
}

sealed record PdfExtractionQualitySummary(
    int PageCount,
    int TextPageCount,
    int EmptyPageCount,
    int SparsePageCount,
    int TotalWordCount,
    int TotalCharCount,
    double AverageWordsPerPage,
    double AverageCharsPerPage,
    double TextPageRatio,
    double EmptyPageRatio,
    double SparsePageRatio,
    string TextStatus,
    bool OcrRecommended,
    string[] Signals)
{
    public static PdfExtractionQualitySummary FromPages(IReadOnlyList<ExtractedPdfPage> pages)
    {
        if (pages.Count == 0)
        {
            return new PdfExtractionQualitySummary(
                PageCount: 0,
                TextPageCount: 0,
                EmptyPageCount: 0,
                SparsePageCount: 0,
                TotalWordCount: 0,
                TotalCharCount: 0,
                AverageWordsPerPage: 0,
                AverageCharsPerPage: 0,
                TextPageRatio: 0,
                EmptyPageRatio: 0,
                SparsePageRatio: 0,
                TextStatus: "empty_text",
                OcrRecommended: true,
                Signals: ["no_pages_extracted"]);
        }

        var qualities = pages
            .Select(static page => page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount))
            .ToArray();
        var totalWords = pages.Sum(static page => page.WordCount);
        var totalChars = pages.Sum(static page => page.CharCount);
        var emptyPages = qualities.Count(static quality => quality.TextEmpty);
        var sparsePages = qualities.Count(static quality => quality.TextSparse);
        var replacementCharPages = qualities.Count(static quality => quality.Signals.Contains("replacement_chars_detected"));
        var invalidControlCharPages = qualities.Count(static quality =>
            quality.Signals.Contains("invalid_control_chars_detected"));
        var textPages = pages.Count - emptyPages;
        var averageWords = Math.Round((double)totalWords / pages.Count, 2);
        var averageChars = Math.Round((double)totalChars / pages.Count, 2);
        var textPageRatio = Math.Round((double)textPages / pages.Count, 4);
        var emptyPageRatio = Math.Round((double)emptyPages / pages.Count, 4);
        var sparsePageRatio = Math.Round((double)sparsePages / pages.Count, 4);

        var signals = new List<string>();
        string textStatus;
        if (totalWords == 0)
        {
            textStatus = "empty_text";
            signals.Add("no_text_extracted");
        }
        else if (invalidControlCharPages > 0)
        {
            textStatus = "low_text";
            signals.Add("invalid_control_chars_detected");
        }
        else if (emptyPageRatio >= 0.6)
        {
            textStatus = "low_text";
            signals.Add("many_empty_pages");
        }
        else if (sparsePageRatio >= 0.6 && averageWords < 30)
        {
            textStatus = "low_text";
            signals.Add("many_sparse_pages");
        }
        else if (averageWords < 10)
        {
            textStatus = "low_text";
            signals.Add("low_average_words_per_page");
        }
        else
        {
            textStatus = "ok";
            signals.Add("text_extraction_ok");
        }

        var ocrRecommended = !string.Equals(textStatus, "ok", StringComparison.Ordinal);
        if (ocrRecommended)
            signals.Add("ocr_recommended");
        if (replacementCharPages > 0)
            signals.Add("replacement_chars_detected");

        return new PdfExtractionQualitySummary(
            PageCount: pages.Count,
            TextPageCount: textPages,
            EmptyPageCount: emptyPages,
            SparsePageCount: sparsePages,
            TotalWordCount: totalWords,
            TotalCharCount: totalChars,
            AverageWordsPerPage: averageWords,
            AverageCharsPerPage: averageChars,
            TextPageRatio: textPageRatio,
            EmptyPageRatio: emptyPageRatio,
            SparsePageRatio: sparsePageRatio,
            TextStatus: textStatus,
            OcrRecommended: ocrRecommended,
            Signals: signals.Distinct(StringComparer.Ordinal).ToArray());
    }
}

sealed record PdfExtractionResult(
    List<WordToken> Tokens,
    List<ExtractedPdfPage> Pages,
    PdfExtractionQualitySummary Quality,
    string Source = "pdf_text",
    string? OcrLanguages = null,
    PdfOcrDiagnostics? OcrDiagnostics = null);

sealed record PdfImagePageOcrPlan(
    int CandidatePageCount,
    int AttemptedPageCount,
    int SkippedPageCount,
    int MaxPages,
    int[] CandidatePages,
    int[] AttemptedPages,
    int[] SkippedPages);

sealed record PdfImagePageOcrDiagnostic(
    int PageNumber,
    string Status,
    string? Reason = null,
    int? OcrWordCount = null,
    int? OcrCharCount = null,
    int? ExitCode = null,
    bool TimedOut = false);

sealed record PdfOcrDiagnostics(
    string Mode,
    int CandidatePageCount,
    int AttemptedPageCount,
    int SkippedPageCount,
    int MaxPages,
    int[] CandidatePages,
    int[] AttemptedPages,
    int[] SkippedPages,
    int[] PagesWithOcrText,
    int[] PagesWithNovelText,
    int? ExitCode = null,
    bool TimedOut = false,
    int? TimeoutSeconds = null,
    string? Stderr = null,
    string? FailureReason = null,
    string? AppliedReason = null,
    string? CoverageStatus = null,
    IReadOnlyList<PdfImagePageOcrDiagnostic>? ImagePageDiagnostics = null)
{
    public bool PageCountsAvailable { get; init; } = true;
    public int? EngineTimingCount { get; init; }
    public string? EngineTimingScope { get; init; }
}

sealed record PdfImagePageOcrResult(
    PdfExtractionResult? Extraction,
    PdfOcrDiagnostics Diagnostics);

sealed record PdfImagePageOcrCallbacks(
    Func<int, int, CancellationToken, Task>? ReportProgressAsync = null,
    Func<CancellationToken, Task<bool>>? IsCancellationRequestedAsync = null)
{
    public static readonly PdfImagePageOcrCallbacks None = new();

    public Task ReportAsync(int current, int total, CancellationToken ct)
        => ReportProgressAsync?.Invoke(current, total, ct) ?? Task.CompletedTask;

    public async Task ThrowIfCancellationRequestedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsCancellationRequestedAsync is not null
            && await IsCancellationRequestedAsync(ct).ConfigureAwait(false))
            throw new OperationCanceledException("Image-page OCR cancellation requested.", ct);
    }
}

sealed record Chunk(int ChunkIndex, int PageStart, int PageEnd, string Text);

internal sealed record PdfOcrObservedWord(string Text, double? Confidence,
    int Left, int Top, int Width, int Height, string Block, string Paragraph, string Line);

internal sealed record PdfOcrPageObservation(string RawText, string Engine, string? Language,
    IReadOnlyList<PdfOcrObservedWord> Words, int? ImageWidth = null, int? ImageHeight = null,
    string? RawTsv = null)
{
    public bool HasLowConfidenceWords => Words.Any(static word => word.Confidence is null or < 0.58);
}
