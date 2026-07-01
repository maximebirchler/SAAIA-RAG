using System.Threading;
using System.Security.Cryptography;
using System.Text;
using PdfPig = UglyToad.PdfPig;

static class PdfExtractor
{
    public static PdfExtractionResult Extract(string pdfPath)
        => Extract(pdfPath, CancellationToken.None);

    public static PdfExtractionResult Extract(string pdfPath, CancellationToken ct)
    {
        var tokens = new List<WordToken>();
        var pages = new List<ExtractedPdfPage>();
        var rawPages = new List<(int PageNumber, string Text, int ImageCount)>();
        var replacementStatsByPage = new Dictionary<int, (int RawReplacementCharCount, int SanitizedReplacementCharCount)>();

        using var doc = PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var rawText = ExtractLayoutAwarePageText(page);
            var text = PdfTextSanitizer.ForStorage(rawText);
            rawPages.Add((page.Number, text, CountPageImages(page)));
            replacementStatsByPage[page.Number] = (
                CountReplacementCharacters(rawText),
                CountReplacementCharacters(text));
        }

        foreach (var rawPage in RemoveRepeatedPageBoilerplate(rawPages))
        {
            ct.ThrowIfCancellationRequested();

            var text = rawPage.Text;
            var words = SplitWords(text).ToArray();
            foreach (var w in words)
            {
                ct.ThrowIfCancellationRequested();
                if (w.Length == 0) continue;
                tokens.Add(new WordToken(w, rawPage.PageNumber)); // PageNumber is 1-based
            }

            var replacementStats = replacementStatsByPage.TryGetValue(rawPage.PageNumber, out var stats)
                ? stats
                : (RawReplacementCharCount: 0, SanitizedReplacementCharCount: CountReplacementCharacters(text));
            pages.Add(new ExtractedPdfPage(
                PageNumber: rawPage.PageNumber,
                Text: text,
                WordCount: words.Length,
                CharCount: text.Length,
                Checksum: SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)),
                Quality: PdfPageExtractionQuality.FromSanitizedText(
                    text,
                    words.Length,
                    text.Length,
                    replacementStats.RawReplacementCharCount,
                    replacementStats.SanitizedReplacementCharCount),
                ImageCount: rawPage.ImageCount));
        }

        return new PdfExtractionResult(tokens, pages, PdfExtractionQualitySummary.FromPages(pages));
    }

    public static List<WordToken> ExtractWordTokens(string pdfPath)
        => ExtractWordTokens(pdfPath, CancellationToken.None);

    public static List<WordToken> ExtractWordTokens(string pdfPath, CancellationToken ct)
        => Extract(pdfPath, ct).Tokens;

    private static IEnumerable<string> SplitWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int CountPageImages(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return page.GetImages().Count();
        }
        catch
        {
            return 0;
        }
    }

    private static string ExtractLayoutAwarePageText(UglyToad.PdfPig.Content.Page page)
    {
        var fallback = page.Text ?? string.Empty;
        try
        {
            var words = page.GetWords()
                .Select(static word => new PdfLayoutWord(
                    word.Text,
                    word.BoundingBox.Left,
                    word.BoundingBox.Right,
                    word.BoundingBox.Top,
                    word.BoundingBox.Bottom))
                .ToArray();
            return BuildLayoutAwareText(words, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    internal static string BuildLayoutAwareText(IReadOnlyList<PdfLayoutWord> words, string? fallback)
    {
        fallback ??= string.Empty;
        var usableWords = words
            .Where(static word =>
                !string.IsNullOrWhiteSpace(word.Text)
                && IsFinite(word.Left)
                && IsFinite(word.Right)
                && IsFinite(word.Top)
                && IsFinite(word.Bottom)
                && word.Right >= word.Left
                && word.Top >= word.Bottom)
            .ToArray();
        if (usableWords.Length == 0)
            return fallback;

        var fallbackTokenCount = SplitWords(fallback).Count();
        if (fallbackTokenCount > 0 && usableWords.Length < Math.Max(4, fallbackTokenCount / 2))
            return fallback;

        var medianHeight = ResolveMedianPositive(usableWords.Select(static word => word.Height));
        var medianWidth = ResolveMedianPositive(usableWords.Select(static word => word.Width));
        if (medianHeight <= 0 || medianWidth <= 0)
            return fallback;

        var sameLineTolerance = Math.Clamp(medianHeight * 0.45d, 2.0d, 8.0d);
        var paragraphGapThreshold = Math.Clamp(medianHeight * 1.15d, 7.0d, 28.0d);
        var wrapBackThreshold = Math.Clamp(medianWidth * 2.5d, 18.0d, 72.0d);

        var builder = new StringBuilder();
        PdfLayoutWord? previous = null;
        foreach (var word in usableWords)
        {
            var text = PdfTextSanitizer.ForStorage(word.Text).Trim();
            if (text.Length == 0)
                continue;

            if (previous is null)
            {
                builder.Append(text);
                previous = word;
                continue;
            }

            var verticalDelta = Math.Abs(word.CenterY - previous.Value.CenterY);
            var wrapsBack = word.Left + wrapBackThreshold < previous.Value.Left;
            if (verticalDelta > sameLineTolerance || wrapsBack)
            {
                var verticalGap = previous.Value.Bottom - word.Top;
                builder.Append(verticalGap > paragraphGapThreshold ? "\n\n" : "\n");
            }
            else if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }

            builder.Append(text);
            previous = word;
        }

        var reconstructed = builder.ToString().Trim();
        if (reconstructed.Length == 0)
            return fallback;

        if (!fallback.Contains('\n', StringComparison.Ordinal)
            && reconstructed.Contains('\n', StringComparison.Ordinal))
        {
            return reconstructed;
        }

        return reconstructed.Length >= Math.Max(20, fallback.Length / 2)
            ? reconstructed
            : fallback;
    }

    private static double ResolveMedianPositive(IEnumerable<double> values)
    {
        var ordered = values
            .Where(static value => IsFinite(value) && value > 0)
            .OrderBy(static value => value)
            .ToArray();
        if (ordered.Length == 0)
            return 0;

        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2d;
    }

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private static int CountReplacementCharacters(string? text)
        => string.IsNullOrEmpty(text)
            ? 0
            : text.Count(static ch => ch == '\uFFFD');

    internal static IReadOnlyList<(int PageNumber, string Text, int ImageCount)> RemoveRepeatedPageBoilerplate(
        IReadOnlyList<(int PageNumber, string Text, int ImageCount)> pages)
    {
        if (pages.Count == 0)
            return pages;

        HashSet<string> repeated;
        HashSet<string> repeatedPatterns;
        if (pages.Count >= 3)
        {
            var pageLineSets = pages
                .Select(static page => ExtractCandidateBoilerplateLines(page.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                .ToArray();
            repeated = pageLineSets
                .SelectMany(static lines => lines)
                .GroupBy(static line => line, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() >= Math.Max(3, (int)Math.Ceiling(pages.Count * 0.35)))
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pagePatternSets = pageLineSets
                .Select(static lines => lines
                    .Where(HasVariableBoilerplateMarker)
                    .Select(NormalizeVariableBoilerplateLine)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
                .ToArray();
            repeatedPatterns = pagePatternSets
                .SelectMany(static lines => lines)
                .GroupBy(static line => line, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() >= Math.Max(3, (int)Math.Ceiling(pages.Count * 0.35)))
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            repeatedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var pageCount = pages.Max(static page => page.PageNumber);
        return pages
            .Select(page => (page.PageNumber, Text: RemoveRepeatedLines(page.Text, page.PageNumber, pageCount, repeated, repeatedPatterns), page.ImageCount))
            .ToArray();
    }

    private static IEnumerable<string> ExtractCandidateBoilerplateLines(string text)
    {
        foreach (var line in SplitLikelyLines(text))
        {
            var normalized = NormalizeBoilerplateLine(line);
            if (normalized.Length is < 8 or > 120)
                continue;

            var tokenCount = SplitWords(normalized).Count();
            if (tokenCount is < 2 or > 12)
                continue;

            yield return normalized;
        }
    }

    private static string RemoveRepeatedLines(
        string text,
        int pageNumber,
        int pageCount,
        ISet<string> repeated,
        ISet<string> repeatedPatterns)
    {
        var lines = SplitLikelyLines(text).ToArray();
        if (lines.Length <= 1)
        {
            var normalized = NormalizeBoilerplateLine(text);
            return IsRepeatedBoilerplateLine(normalized, repeated, repeatedPatterns) ? string.Empty : text;
        }

        return string.Join('\n', lines.Where((line, index) =>
        {
            var normalized = NormalizeBoilerplateLine(line);
            return !IsRepeatedBoilerplateLine(normalized, repeated, repeatedPatterns)
                   && !IsLocalPageMarkerLine(normalized, pageNumber, pageCount, index, lines.Length);
        })).Trim();
    }

    private static IEnumerable<string> SplitLikelyLines(string text)
        => (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeBoilerplateLine(string line)
        => System.Text.RegularExpressions.Regex.Replace(line ?? string.Empty, @"\s+", " ").Trim();

    private static bool IsRepeatedBoilerplateLine(string normalized, ISet<string> repeated, ISet<string> repeatedPatterns)
        => repeated.Contains(normalized)
           || (HasVariableBoilerplateMarker(normalized)
               && repeatedPatterns.Contains(NormalizeVariableBoilerplateLine(normalized)));

    private static bool HasVariableBoilerplateMarker(string line)
        => !string.IsNullOrWhiteSpace(line)
           && line.Any(char.IsDigit);

    private static string NormalizeVariableBoilerplateLine(string line)
        => System.Text.RegularExpressions.Regex.Replace(NormalizeBoilerplateLine(line), @"\d+", "#");

    private static bool IsLocalPageMarkerLine(string line, int pageNumber, int pageCount, int index, int lineCount)
    {
        if (lineCount <= 1 || index > 1 && index < lineCount - 2)
            return false;

        var normalized = NormalizeBoilerplateLine(line)
            .Trim('-', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', ' ')
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (int.TryParse(normalized, out var simpleNumber))
            return simpleNumber == pageNumber;

        var pagePattern = pageCount > 0
            ? $@"^(?:page|p\.?)?\s*0*{pageNumber}\s*(?:/|of|sur|de)\s*0*{pageCount}$"
            : $@"^(?:page|p\.?)?\s*0*{pageNumber}$";
        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, pagePattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return true;

        var singlePagePattern = $@"^(?:page|p\.?)\s*0*{pageNumber}$";
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, singlePagePattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

sealed record WordToken(string Word, int Page);
internal readonly record struct PdfLayoutWord(
    string Text,
    double Left,
    double Right,
    double Top,
    double Bottom)
{
    public double Width => Math.Max(0d, Right - Left);
    public double Height => Math.Max(0d, Top - Bottom);
    public double CenterY => (Top + Bottom) / 2d;
}

sealed record ExtractedPdfPage(
    int PageNumber,
    string Text,
    int WordCount,
    int CharCount,
    byte[] Checksum,
    PdfPageExtractionQuality? Quality = null,
    int ImageCount = 0);

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
        if (rawReplacementCharCount == 0 && sanitizedReplacementCharCount == 0)
            return quality;

        var hasRemainingReplacementCharacters = sanitizedReplacementCharCount > 0
            || (!string.IsNullOrEmpty(text) && text.Contains('\uFFFD', StringComparison.Ordinal));

        return quality with
        {
            TextStatus = hasRemainingReplacementCharacters ? "low_text" : quality.TextStatus,
            TextSparse = hasRemainingReplacementCharacters || quality.TextSparse,
            OcrCandidate = hasRemainingReplacementCharacters || quality.OcrCandidate,
            Signals = quality.Signals
                .Where(static signal => !string.Equals(signal, "text_extraction_ok", StringComparison.Ordinal))
                .Concat(["replacement_chars_detected"])
                .Concat(rawReplacementCharCount > sanitizedReplacementCharCount
                    ? ["replacement_chars_repaired"]
                    : Array.Empty<string>())
                .Concat(hasRemainingReplacementCharacters
                    ? ["replacement_chars_remaining"]
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
    IReadOnlyList<PdfImagePageOcrDiagnostic>? ImagePageDiagnostics = null);

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
