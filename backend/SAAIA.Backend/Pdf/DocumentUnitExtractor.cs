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
            return SplitDenseStructuredParagraphs(explicitParagraphs);

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

        return SplitDenseStructuredParagraphs(grouped);
    }

    private static List<string> SplitDenseStructuredParagraphs(IEnumerable<string> paragraphs)
    {
        var result = new List<string>();
        foreach (var paragraph in paragraphs)
            result.AddRange(SplitDenseStructuredParagraph(paragraph));

        return result;
    }

    private static IReadOnlyList<string> SplitDenseStructuredParagraph(string paragraph)
    {
        paragraph = NormalizeLine(paragraph);
        if (paragraph.Length < 120)
            return [paragraph];

        var boundaries = FindImplicitStructuredBoundaries(paragraph);
        if (boundaries.Count == 0)
            return [paragraph];

        var segments = new List<string>();
        var start = 0;
        foreach (var boundary in boundaries)
        {
            if (boundary <= start)
                continue;

            AddSegment(segments, paragraph[start..boundary]);
            start = boundary;
        }

        AddSegment(segments, paragraph[start..]);
        return segments.Count > 1 ? segments : [paragraph];
    }

    private static List<int> FindImplicitStructuredBoundaries(string text)
    {
        var boundaries = new List<int>();
        var index = 1;

        while (index < text.Length - 8)
        {
            var candidate = index;
            if (char.IsWhiteSpace(text[candidate]))
            {
                while (candidate < text.Length && char.IsWhiteSpace(text[candidate]))
                    candidate++;
            }

            if (candidate >= text.Length - 8 || !LooksLikeBoundaryLead(text, candidate))
            {
                index++;
                continue;
            }

            var previous = PreviousNonWhitespace(text, candidate - 1);
            if (previous < 0)
            {
                index++;
                continue;
            }

            var strongBoundary = ".!?".Contains(text[previous])
                || LooksLikeGluedPageTitleBoundary(text, previous, candidate);
            if (!strongBoundary)
            {
                index++;
                continue;
            }

            var lookaheadLength = Math.Min(180, text.Length - candidate);
            var lookahead = text.Substring(candidate, lookaheadLength);
            if (!StructuredLeadMarkerRegex().IsMatch(lookahead))
            {
                index++;
                continue;
            }

            if (candidate >= 60)
                boundaries.Add(candidate);

            index = candidate + 1;
        }

        return boundaries;
    }

    private static bool LooksLikeBoundaryLead(string text, int index)
    {
        var ch = text[index];
        if (char.IsUpper(ch))
            return true;

        if (!char.IsDigit(ch))
            return false;

        var digitEnd = index;
        while (digitEnd < text.Length && char.IsDigit(text[digitEnd]))
            digitEnd++;

        return digitEnd < text.Length
            && char.IsLetter(text[digitEnd])
            && char.IsUpper(text[digitEnd]);
    }

    private static bool LooksLikeGluedPageTitleBoundary(string text, int previous, int candidate)
    {
        if (candidate <= 0 || candidate >= text.Length)
            return false;

        if (!char.IsDigit(text[candidate]))
            return false;

        if (previous >= 0 && char.IsDigit(text[previous]))
            return false;

        var digitEnd = candidate;
        while (digitEnd < text.Length && char.IsDigit(text[digitEnd]))
            digitEnd++;

        return digitEnd < text.Length
            && char.IsUpper(text[digitEnd]);
    }

    private static int PreviousNonWhitespace(string text, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static void AddSegment(List<string> segments, string value)
    {
        var segment = NormalizeLine(value);
        if (string.IsNullOrWhiteSpace(segment))
            return;

        if (segment.Length < 80 && segments.Count > 0)
            segments[^1] = NormalizeLine($"{segments[^1]} {segment}");
        else
            segments.Add(segment);
    }

    private static string NormalizeLine(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"[\.!?;:]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}]|(?<=[\p{Ll}])(?=[\p{Lu}]))(?:pour|for|para|per)\s+\d+|(?:^|[^\p{L}\p{N}]|(?<=[\p{Ll}])(?=[\p{Lu}]))(?:ingredients?|ingr[eé]dients?|ingredienti|zutaten|preparation|pr[eé]paration|method|steps?|[eé]tapes?|procedure|requirements?|materials?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredLeadMarkerRegex();
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
