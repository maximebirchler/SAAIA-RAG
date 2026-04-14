using System.Text.RegularExpressions;

internal static partial class DocumentSectionExtractor
{
    public static IReadOnlyList<ExtractedDocumentSection> Extract(IReadOnlyList<ExtractedPdfPage> pages)
    {
        if (pages.Count == 0)
            return Array.Empty<ExtractedDocumentSection>();

        var pageLines = pages
            .Select(p => new PageLines(
                p.PageNumber,
                SplitLines(p.Text)))
            .ToList();

        var duplicateFrequency = pageLines
            .SelectMany(p => p.Lines.Distinct(StringComparer.Ordinal))
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var candidates = new List<SectionCandidate>();
        foreach (var page in pageLines)
        {
            for (var i = 0; i < page.Lines.Count; i++)
            {
                var line = page.Lines[i];
                if (duplicateFrequency.TryGetValue(line, out var repeats) && repeats >= 3)
                    continue;

                if (!TryClassifyHeading(line, out var level))
                    continue;

                candidates.Add(new SectionCandidate(page.PageNumber, i + 1, line, level));
            }
        }

        if (candidates.Count == 0)
        {
            return new[]
            {
                new ExtractedDocumentSection(
                    Ordinal: 0,
                    Title: "Document",
                    Level: 1,
                    PageStart: pages.Min(p => p.PageNumber),
                    PageEnd: pages.Max(p => p.PageNumber),
                    StartLine: null,
                    EndLine: null)
            };
        }

        var sections = new List<ExtractedDocumentSection>(candidates.Count);
        for (var idx = 0; idx < candidates.Count; idx++)
        {
            var current = candidates[idx];
            var next = idx + 1 < candidates.Count ? candidates[idx + 1] : null;
            var pageEnd = next is null
                ? pages.Max(p => p.PageNumber)
                : next.PageNumber > current.PageNumber
                    ? next.PageNumber - 1
                    : current.PageNumber;
            int? endLine = next is null || next.PageNumber != current.PageNumber
                ? null
                : Math.Max(current.LineNumber, next.LineNumber - 1);

            sections.Add(new ExtractedDocumentSection(
                Ordinal: idx,
                Title: current.Title,
                Level: current.Level,
                PageStart: current.PageNumber,
                PageEnd: Math.Max(current.PageNumber, pageEnd),
                StartLine: current.LineNumber,
                EndLine: endLine));
        }

        return sections;
    }

    private static List<string> SplitLines(string text)
        => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static bool TryClassifyHeading(string line, out int level)
    {
        level = 1;

        if (line.Length < 3 || line.Length > 140)
            return false;

        if (line.EndsWith(".", StringComparison.Ordinal))
            return false;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 16)
            return false;

        if (NumberedHeadingRegex().IsMatch(line))
        {
            var prefix = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            level = Math.Clamp(prefix.Count(c => c == '.') + 1, 1, 6);
            return true;
        }

        if (RomanHeadingRegex().IsMatch(line))
        {
            level = 1;
            return true;
        }

        var letterCount = line.Count(char.IsLetter);
        if (letterCount < 3)
            return false;

        var uppercaseLetters = line.Count(char.IsUpper);
        var titleCaseWords = words.Count(static w => w.Length > 0 && char.IsUpper(w[0]));

        if (uppercaseLetters >= Math.Max(3, letterCount / 2))
            return true;

        if (titleCaseWords >= Math.Max(2, words.Length - 1))
            return true;

        return false;
    }

    [GeneratedRegex(@"^\d+(?:\.\d+){0,5}\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeadingRegex();

    [GeneratedRegex(@"^(?:[IVXLCM]+)[\.\)]?\s+\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RomanHeadingRegex();

    private sealed record PageLines(int PageNumber, List<string> Lines);
    private sealed record SectionCandidate(int PageNumber, int LineNumber, string Title, int Level);
}

internal sealed record ExtractedDocumentSection(
    int Ordinal,
    string Title,
    int Level,
    int PageStart,
    int PageEnd,
    int? StartLine,
    int? EndLine);
