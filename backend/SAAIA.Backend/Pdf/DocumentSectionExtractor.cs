using System.Text;
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

                if (LooksLikeNavigationListLine(line, page.Lines, i))
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

        if (LooksLikeNavigationHeadingTitle(line))
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

    private static bool LooksLikeNavigationHeadingTitle(string line)
    {
        var normalized = FoldDiacritics(NormalizeWhitespace(line)).ToLowerInvariant();
        return normalized.Contains("table des matieres", StringComparison.Ordinal)
            || normalized.Contains("table of contents", StringComparison.Ordinal)
            || normalized.Contains("inhaltsverzeichnis", StringComparison.Ordinal)
            || normalized.Contains("indice general", StringComparison.Ordinal)
            || normalized.Contains("indice de contenido", StringComparison.Ordinal)
            || normalized.Contains("indice de contenidos", StringComparison.Ordinal)
            || normalized.Contains("indice de materias", StringComparison.Ordinal)
            || normalized.Contains("indice analitico", StringComparison.Ordinal)
            || normalized is "sommaire" or "contents" or "sommario" or "sumario" or "indice" or "index" or "toc";
    }

    private static bool LooksLikeNavigationListLine(string line, IReadOnlyList<string> lines, int index)
    {
        if (!LooksLikePageReferenceLine(line))
            return false;

        var similarNeighbors = 0;
        var first = Math.Max(0, index - 3);
        var last = Math.Min(lines.Count - 1, index + 3);
        for (var i = first; i <= last; i++)
        {
            if (i == index)
                continue;
            if (LooksLikePageReferenceLine(lines[i]))
                similarNeighbors++;
        }

        return similarNeighbors >= 2
            || (DotLeaderPageReferenceRegex().IsMatch(line) && similarNeighbors >= 1);
    }

    private static bool LooksLikePageReferenceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 160)
            return false;

        if (DotLeaderPageReferenceRegex().IsMatch(line))
            return true;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 14
            && TrailingPageReferenceRegex().IsMatch(line)
            && !NumberedHeadingRegex().IsMatch(line)
            && !line.Contains(':', StringComparison.Ordinal);
    }

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"^\d+(?:\.\d+){0,5}\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeadingRegex();

    [GeneratedRegex(@"^(?:[IVXLCM]+)[\.\)]?\s+\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RomanHeadingRegex();

    [GeneratedRegex(@"\.{2,}\s*\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotLeaderPageReferenceRegex();

    [GeneratedRegex(@"\s\d{1,5}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPageReferenceRegex();

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
