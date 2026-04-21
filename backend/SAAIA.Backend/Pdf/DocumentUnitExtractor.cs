using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class DocumentUnitExtractor
{
    private static readonly string UnitSeparator = Environment.NewLine + Environment.NewLine;

    public static IReadOnlyList<ExtractedDocumentUnit> Extract(
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections)
    {
        if (pages.Count == 0)
            return Array.Empty<ExtractedDocumentUnit>();

        var normalizedSectionTitles = sections
            .Select(s => NormalizeLine(s.Title))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.Ordinal);

        var units = new List<ExtractedDocumentUnit>();
        var ordinal = 0;
        var offsetCursor = 0;

        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            var paragraphs = SplitParagraphs(page.Text);
            if (paragraphs.Count == 0)
                continue;

            var currentSection = sections
                .Where(s => s.PageStart <= page.PageNumber && s.PageEnd >= page.PageNumber)
                .OrderByDescending(s => s.PageStart)
                .ThenByDescending(s => s.Ordinal)
                .FirstOrDefault();

            foreach (var paragraph in paragraphs)
            {
                var normalized = NormalizeLine(paragraph);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (normalizedSectionTitles.Contains(normalized))
                    continue;

                var tokenCount = CountTokens(normalized);
                if (tokenCount <= 0)
                    continue;

                units.Add(new ExtractedDocumentUnit(
                    Ordinal: ordinal++,
                    SectionOrdinal: currentSection?.Ordinal,
                    PageStart: page.PageNumber,
                    PageEnd: page.PageNumber,
                    Text: normalized,
                    CharCount: normalized.Length,
                    TokenCount: tokenCount,
                    Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(normalized)),
                    OffsetStart: offsetCursor,
                    OffsetEnd: offsetCursor + normalized.Length));

                offsetCursor += normalized.Length + UnitSeparator.Length;
            }
        }

        if (units.Count == 0)
        {
            var fullText = string.Join(Environment.NewLine + Environment.NewLine,
                pages.OrderBy(p => p.PageNumber)
                    .Select(p => NormalizeLine(p.Text))
                    .Where(static t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(fullText))
                return Array.Empty<ExtractedDocumentUnit>();

            return new[]
            {
                new ExtractedDocumentUnit(
                    Ordinal: 0,
                    SectionOrdinal: sections.Count > 0 ? sections[0].Ordinal : null,
                    PageStart: pages.Min(p => p.PageNumber),
                    PageEnd: pages.Max(p => p.PageNumber),
                    Text: fullText,
                    CharCount: fullText.Length,
                    TokenCount: CountTokens(fullText),
                    Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(fullText)),
                    OffsetStart: 0,
                    OffsetEnd: fullText.Length)
            };
        }

        return units;
    }

    private static List<string> SplitParagraphs(string text)
    {
        var normalizedNewlines = text.Replace("\r\n", "\n");
        var explicitParagraphs = normalizedNewlines
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLine)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (explicitParagraphs.Count > 0)
            return explicitParagraphs;

        var lines = normalizedNewlines
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLine)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (lines.Count == 0)
            return [];

        var grouped = new List<string>();
        var buffer = new List<string>();
        var currentLength = 0;

        foreach (var line in lines)
        {
            buffer.Add(line);
            currentLength += line.Length;

            if (currentLength >= 500 || SentenceEndRegex().IsMatch(line))
            {
                grouped.Add(string.Join(' ', buffer));
                buffer.Clear();
                currentLength = 0;
            }
        }

        if (buffer.Count > 0)
            grouped.Add(string.Join(' ', buffer));

        return grouped;
    }

    private static string NormalizeLine(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"[\.!?;:]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();
}

internal sealed record ExtractedDocumentUnit(
    int Ordinal,
    int? SectionOrdinal,
    int PageStart,
    int PageEnd,
    string Text,
    int CharCount,
    int TokenCount,
    byte[] Checksum,
    int? OffsetStart = null,
    int? OffsetEnd = null);
