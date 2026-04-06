using System.Threading;
using PdfPig = UglyToad.PdfPig;

static class PdfExtractor
{
    public static List<WordToken> ExtractWordTokens(string pdfPath)
        => ExtractWordTokens(pdfPath, CancellationToken.None);

    public static List<WordToken> ExtractWordTokens(string pdfPath, CancellationToken ct)
    {
        var tokens = new List<WordToken>();

        using var doc = PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var text = page.Text ?? "";
            var words = SplitWords(text);
            foreach (var w in words)
            {
                ct.ThrowIfCancellationRequested();
                if (w.Length == 0) continue;
                tokens.Add(new WordToken(w, page.Number)); // page.Number is 1-based
            }
        }

        return tokens;
    }

    private static IEnumerable<string> SplitWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

sealed record WordToken(string Word, int Page);

sealed record Chunk(int ChunkIndex, int PageStart, int PageEnd, string Text);
