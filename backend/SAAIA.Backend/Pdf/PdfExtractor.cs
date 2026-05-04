using System.Threading;
using System.Security.Cryptography;
using PdfPig = UglyToad.PdfPig;

static class PdfExtractor
{
    public static PdfExtractionResult Extract(string pdfPath)
        => Extract(pdfPath, CancellationToken.None);

    public static PdfExtractionResult Extract(string pdfPath, CancellationToken ct)
    {
        var tokens = new List<WordToken>();
        var pages = new List<ExtractedPdfPage>();

        using var doc = PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var text = PdfTextSanitizer.ForStorage(page.Text);
            var words = SplitWords(text).ToArray();
            foreach (var w in words)
            {
                ct.ThrowIfCancellationRequested();
                if (w.Length == 0) continue;
                tokens.Add(new WordToken(w, page.Number)); // page.Number is 1-based
            }

            pages.Add(new ExtractedPdfPage(
                PageNumber: page.Number,
                Text: text,
                WordCount: words.Length,
                CharCount: text.Length,
                Checksum: SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)),
                Quality: PdfPageExtractionQuality.FromCounts(words.Length, text.Length)));
        }

        return new PdfExtractionResult(tokens, pages, PdfExtractionQualitySummary.FromPages(pages));
    }

    public static List<WordToken> ExtractWordTokens(string pdfPath)
        => ExtractWordTokens(pdfPath, CancellationToken.None);

    public static List<WordToken> ExtractWordTokens(string pdfPath, CancellationToken ct)
        => Extract(pdfPath, ct).Tokens;

    private static IEnumerable<string> SplitWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

sealed record WordToken(string Word, int Page);
sealed record ExtractedPdfPage(
    int PageNumber,
    string Text,
    int WordCount,
    int CharCount,
    byte[] Checksum,
    PdfPageExtractionQuality? Quality = null);

sealed record PdfPageExtractionQuality(
    string TextStatus,
    bool TextEmpty,
    bool TextSparse,
    bool OcrCandidate,
    double AverageCharsPerWord,
    string[] Signals)
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
            .Select(static page => page.Quality ?? PdfPageExtractionQuality.FromCounts(page.WordCount, page.CharCount))
            .ToArray();
        var totalWords = pages.Sum(static page => page.WordCount);
        var totalChars = pages.Sum(static page => page.CharCount);
        var emptyPages = qualities.Count(static quality => quality.TextEmpty);
        var sparsePages = qualities.Count(static quality => quality.TextSparse);
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
    PdfExtractionQualitySummary Quality);

sealed record Chunk(int ChunkIndex, int PageStart, int PageEnd, string Text);
