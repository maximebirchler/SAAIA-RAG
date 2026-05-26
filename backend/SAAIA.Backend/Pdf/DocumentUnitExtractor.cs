using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class DocumentUnitExtractor
{
    private static readonly string UnitSeparator = Environment.NewLine + Environment.NewLine;
    private const int MaxImplicitBoundaryScanLength = 6000;
    private const int OversizedParagraphWindowLength = 3200;
    private const int OversizedParagraphMinimumWindowLength = 900;

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
        var skippedProbableOcrNoise = false;

        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
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

                if (OcrNoiseFilter.LooksLikeProbableNoiseText(normalized))
                {
                    skippedProbableOcrNoise = true;
                    continue;
                }

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
                    OffsetEnd: offsetCursor + normalized.Length,
                    ExtractionTextStatus: quality.TextStatus,
                    ExtractionTextSparse: quality.TextSparse,
                    ExtractionOcrCandidate: quality.OcrCandidate,
                    ExtractionQualitySignals: quality.Signals));

                offsetCursor += normalized.Length + UnitSeparator.Length;
            }
        }

        if (units.Count == 0)
        {
            if (skippedProbableOcrNoise)
                return Array.Empty<ExtractedDocumentUnit>();

            var fullText = string.Join(Environment.NewLine + Environment.NewLine,
                pages.OrderBy(p => p.PageNumber)
                    .Select(p => NormalizeLine(p.Text))
                    .Where(static t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(fullText))
                return Array.Empty<ExtractedDocumentUnit>();

            var fallbackQuality = ResolveFallbackExtractionQuality(pages);
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
                    OffsetEnd: fullText.Length,
                    ExtractionTextStatus: fallbackQuality.TextStatus,
                    ExtractionTextSparse: fallbackQuality.TextSparse,
                    ExtractionOcrCandidate: fallbackQuality.OcrCandidate,
                    ExtractionQualitySignals: fallbackQuality.Signals)
            };
        }

        return units;
    }

    private static PdfPageExtractionQuality ResolveFallbackExtractionQuality(IReadOnlyList<ExtractedPdfPage> pages)
    {
        var qualities = pages
            .Select(static page => page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount))
            .ToArray();
        if (qualities.Length == 0)
            return PdfPageExtractionQuality.FromCounts(0, 0);

        var worstStatus = "ok";
        foreach (var quality in qualities)
        {
            worstStatus = ResolveWorseTextStatus(worstStatus, quality.TextStatus);
        }

        return new PdfPageExtractionQuality(
            TextStatus: worstStatus,
            TextEmpty: qualities.All(static quality => quality.TextEmpty),
            TextSparse: qualities.Any(static quality => quality.TextSparse),
            OcrCandidate: qualities.Any(static quality => quality.OcrCandidate),
            AverageCharsPerWord: Math.Round(qualities.Average(static quality => quality.AverageCharsPerWord), 2),
            Signals: qualities
                .SelectMany(static quality => quality.Signals)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RawReplacementCharCount: qualities.Sum(static quality => quality.RawReplacementCharCount),
            SanitizedReplacementCharCount: qualities.Sum(static quality => quality.SanitizedReplacementCharCount),
            EncodingRepairApplied: qualities.Any(static quality => quality.EncodingRepairApplied));
    }

    private static string ResolveWorseTextStatus(string left, string right)
        => TextStatusScore(right) > TextStatusScore(left) ? right : left;

    private static int TextStatusScore(string? status)
        => status switch
        {
            "empty_text" => 4,
            "low_text" => 3,
            "ok" => 1,
            null or "" => 0,
            _ => 2
        };

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

        if (paragraph.Length > MaxImplicitBoundaryScanLength)
            return SplitOversizedDenseParagraph(paragraph);

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

    private static IReadOnlyList<string> SplitOversizedDenseParagraph(string paragraph)
    {
        var windows = new List<string>();
        var start = 0;
        while (start < paragraph.Length)
        {
            var end = ResolveOversizedParagraphWindowEnd(paragraph, start);
            var window = NormalizeLine(paragraph[start..end]);
            if (!string.IsNullOrWhiteSpace(window))
                windows.AddRange(SplitDenseStructuredParagraph(window));

            start = end;
            while (start < paragraph.Length && char.IsWhiteSpace(paragraph[start]))
                start++;
        }

        return windows.Count > 0 ? windows : [paragraph];
    }

    private static int ResolveOversizedParagraphWindowEnd(string paragraph, int start)
    {
        var hardEnd = Math.Min(paragraph.Length, start + OversizedParagraphWindowLength);
        if (hardEnd >= paragraph.Length)
            return paragraph.Length;

        var minEnd = Math.Min(hardEnd, start + OversizedParagraphMinimumWindowLength);
        for (var i = hardEnd; i > minEnd; i--)
        {
            var previous = paragraph[i - 1];
            if (".!?;:".Contains(previous) && i < paragraph.Length && char.IsWhiteSpace(paragraph[i]))
                return i;
        }

        for (var i = hardEnd; i > minEnd; i--)
        {
            if (char.IsWhiteSpace(paragraph[i - 1]))
                return i;
        }

        return hardEnd;
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

            if (candidate >= text.Length - 8)
            {
                index++;
                continue;
            }

            var looksLikeBoundaryLead = LooksLikeBoundaryLead(text, candidate);
            var previous = PreviousNonWhitespace(text, candidate - 1);
            if (previous < 0)
            {
                index++;
                continue;
            }

            var previousCanStartStructuredBody = ".!?;:".Contains(text[previous]);
            if (!looksLikeBoundaryLead && !previousCanStartStructuredBody)
            {
                index++;
                continue;
            }

            string? lookahead = null;
            string ResolveLookahead()
            {
                lookahead ??= text.Substring(candidate, Math.Min(180, text.Length - candidate));
                return lookahead;
            }

            var looksLikeStructuredBodyLead = false;
            if (!looksLikeBoundaryLead || previousCanStartStructuredBody)
                looksLikeStructuredBodyLead = LooksLikeStructuredBodyLead(ResolveLookahead());

            if (!looksLikeBoundaryLead && !looksLikeStructuredBodyLead)
            {
                index++;
                continue;
            }

            var postFooterStructuredBodyBoundary = looksLikeStructuredBodyLead
                && LooksLikePostFooterStructuredBodyBoundary(text, previous, candidate, ResolveLookahead());
            if (!looksLikeBoundaryLead && !postFooterStructuredBodyBoundary)
            {
                index++;
                continue;
            }

            var strongBoundary = ".!?".Contains(text[previous])
                || LooksLikeGluedPageTitleBoundary(text, previous, candidate)
                || LooksLikeLateStructuredTitleBoundary(text, previous, candidate)
                || postFooterStructuredBodyBoundary;
            if (!strongBoundary)
            {
                index++;
                continue;
            }

            var resolvedLookahead = ResolveLookahead();
            if (!StructuredContentLexicon.LooksLikeStructuredLeadMarker(resolvedLookahead)
                && !LooksLikeStructuredItemTitleLead(resolvedLookahead))
            {
                if (!postFooterStructuredBodyBoundary || !looksLikeStructuredBodyLead)
                {
                    index++;
                    continue;
                }
            }

            if (candidate >= 60)
                boundaries.Add(candidate);

            index = candidate + Math.Max(1, ResolveStructuredTitleLeadLength(resolvedLookahead));
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

    private static bool LooksLikePostFooterStructuredBodyBoundary(
        string text,
        int previous,
        int candidate,
        string lookahead)
    {
        if (candidate < 80 || candidate >= text.Length)
            return false;

        if (previous >= 0 && !".!?;:".Contains(text[previous]))
            return false;

        if (!LooksLikeStructuredBodyLead(lookahead))
            return false;

        var prefixStart = Math.Max(0, candidate - 320);
        var prefix = text.Substring(prefixStart, candidate - prefixStart);
        return ContainsTrailingStructuredFooterTitle(prefix);
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

    private static bool LooksLikeLateStructuredTitleBoundary(string text, int previous, int candidate)
    {
        if (candidate < 60 || candidate >= text.Length)
            return false;

        if (!char.IsLetter(text[candidate]) || !char.IsUpper(text[candidate]))
            return false;

        var compactMeasureBoundary = LooksLikeCompactMeasureToStructuredTitleBoundary(text, candidate);
        if (candidate > 0 && char.IsLetterOrDigit(text[candidate - 1]) && !compactMeasureBoundary)
            return false;

        if (previous >= 0 && char.IsDigit(text[previous]))
            return false;

        if (previous >= 0 && !char.IsLetterOrDigit(text[previous]) && !char.IsWhiteSpace(text[previous]))
            return false;

        var lookahead = text.Substring(candidate, Math.Min(220, text.Length - candidate));
        if (!LooksLikeStructuredItemTitleLead(lookahead))
            return false;

        var prefixStart = Math.Max(0, candidate - 280);
        var prefix = text.Substring(prefixStart, candidate - prefixStart);
        return compactMeasureBoundary || LooksLikeCompletedStructuredItemTail(prefix);
    }

    private static bool LooksLikeCompactMeasureToStructuredTitleBoundary(string text, int candidate)
    {
        if (candidate <= 0 || candidate >= text.Length)
            return false;

        var prefixStart = Math.Max(0, candidate - 80);
        var prefix = text.Substring(prefixStart, candidate - prefixStart);
        return CompactMeasureBeforeTitleBoundaryRegex().IsMatch(prefix);
    }

    private static bool LooksLikeStructuredItemTitleLead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = NormalizeLine(text);
        if (lead.Length < 18)
            return false;

        if (GenericStructuredCueLeadRegex().IsMatch(lead))
            return false;
        if (LooksLikeSingleWordFooterBeforeLongTitle(lead))
            return false;

        if (StructuredContentLexicon.TryExtractStructuredItemTitleLead(lead, out _))
            return true;

        return StructuredTitleLeadRegex().IsMatch(lead)
            && (StructuredContentLexicon.LooksLikeStructuredLeadMarker(lead)
                || StructuredItemEvidenceRegex().IsMatch(lead));
    }

    private static bool LooksLikeSingleWordFooterBeforeLongTitle(string lead)
    {
        var match = SingleWordFooterBeforeLongTitleRegex().Match(lead);
        if (!match.Success)
            return false;

        var followingTitle = match.Groups["title"].Value;
        var signalTokens = Regex.Matches(
                followingTitle,
                @"\b[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{3,}\b",
                RegexOptions.CultureInvariant)
            .Count;
        return signalTokens >= 3;
    }

    private static bool LooksLikeCompletedStructuredItemTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeStructuredBoundaryEvidenceText(text);
        return StructuredContentLexicon.LooksLikeStructuredLeadMarker(normalized)
            || StructuredItemEvidenceRegex().Matches(normalized).Count >= 2
            || CompletedStructuredTailRegex().IsMatch(normalized);
    }

    private static bool LooksLikeStructuredBodyLead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = NormalizeLine(text);
        if (lead.Length < 12)
            return false;

        return StructuredContentLexicon.LooksLikeStructuredLeadMarker(lead)
            || StructuredBodyLeadEvidenceRegex().IsMatch(lead)
            || StructuredItemEvidenceRegex().Matches(lead).Count >= 2;
    }

    private static bool ContainsTrailingStructuredFooterTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeStructuredBoundaryEvidenceText(text);
        if (!LooksLikeCompletedStructuredItemTail(normalized))
            return false;

        foreach (Match match in EmbeddedFooterTitleRegex().Matches(normalized))
        {
            var title = NormalizeLine(match.Groups["title"].Value);
            if (!LooksLikeUsefulFooterTitle(title))
                continue;

            var after = normalized[(match.Index + match.Length)..];
            if (after.Length <= 170)
                return true;
        }

        return false;
    }

    private static bool LooksLikeUsefulFooterTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length is < 4 or > 90)
            return false;

        var tokenCount = CountTokens(title);
        if (tokenCount is < 1 or > 10)
            return false;

        var letters = title.Where(char.IsLetter).ToArray();
        if (letters.Length < 4)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.72);
    }

    private static int ResolveStructuredTitleLeadLength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        var normalized = NormalizeLine(text);
        if (StructuredContentLexicon.TryExtractStructuredItemTitleLead(normalized, out var title))
            return Math.Max(1, title.Length);

        var match = StructuredTitleLeadRegex().Match(normalized);
        return match.Success ? match.Length : 1;
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

        if (segment.Length < 80
            && segments.Count > 0
            && !LooksLikeStructuredItemTitleLead(segment))
        {
            segments[^1] = NormalizeLine($"{segments[^1]} {segment}");
        }
        else
        {
            segments.Add(segment);
        }
    }

    private static string NormalizeLine(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static string InsertFooterTitleBoundarySpaces(string text)
        => UppercaseRunToTitleCaseBoundaryRegex().Replace(text, " ");

    private static string NormalizeStructuredBoundaryEvidenceText(string text)
    {
        var normalized = InsertFooterTitleBoundarySpaces(NormalizeLine(text));
        normalized = LetterBeforeStructuredQuantityRegex().Replace(normalized, " ");
        normalized = StructuredUnitBeforeNumberRegex().Replace(normalized, "${unit} ");
        normalized = StructuredUnitBeforeUppercaseRegex().Replace(normalized, "${unit} ");
        normalized = PunctuationBeforeStructuredQuantityRegex().Replace(normalized, " ");
        return NormalizeLine(normalized);
    }

    private static int CountTokens(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"[\.!?;:]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"^\s*(?:\d{1,4}\s*)?(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}|[\p{Lu}]{2,})(?:\s+(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}|[\p{Lu}]{2,}|a|au|aux|de|des|du|la|le|les|et|with|and|of|the|to|con|al|alla|mit|und)){1,9}", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredTitleLeadRegex();

    [GeneratedRegex(@"^\s*(?:preparation|pr[e\u00e9]paration|realisation|r[e\u00e9]alisation|technique|mat[e\u00e9]riel|materials?|components?|requirements?|items?|elements?|steps?|[e\u00e9]tapes?|temps(?:\s+total)?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericStructuredCueLeadRegex();

    [GeneratedRegex(@"^\s*(?<footer>[\p{Lu}]{4,})\s+(?<title>(?:[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,}\s+){2,}[\p{Lu}][\p{Lu}\p{Ll}'\u2019\-]{2,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex SingleWordFooterBeforeLongTitleRegex();

    [GeneratedRegex(@"\b(?:preparation|pr[e\u00e9]paration|realisation|r[e\u00e9]alisation|technique|temps\s+total|\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|elements?|parts?|pieces?|min|h))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredItemEvidenceRegex();

    [GeneratedRegex(@"\b(?:complete|completed|finish|finished|done|ready|minutes?|min|duration|duree|dur[e\u00e9]e|temps\s+total|total\s+time)\b.{0,90}\b(?:\d{1,3}\s*min|temps\s+total|total\s+time|personnes?|people|persons?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CompletedStructuredTailRegex();

    [GeneratedRegex(@"^\s*(?:\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h)\b|(?:materials?|components?|requirements?|items?|elements?|steps?|method|procedure|procedures?)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredBodyLeadEvidenceRegex();

    [GeneratedRegex(@"(?:\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h|s|sec|secs|secondes?|seconds?)|(?:pour|for|para|per|fur|fuer)\s+\d{1,3}\s+(?:personnes?|people|persons?|items?|units?))\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CompactMeasureBeforeTitleBoundaryRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<title>[\p{Lu}][\p{Lu}\p{Nd}'\u2019\-\s]{3,90}?)(?=(?:\s+[A-Z][\p{Ll}]{2,}|\s*$|[\.:\-\u2013\u2014]))", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedFooterTitleRegex();

    [GeneratedRegex(@"(?<=[\p{Lu}])(?=\p{Lu}\p{Ll}{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseRunToTitleCaseBoundaryRegex();

    [GeneratedRegex(@"(?<=[\p{L}])(?=\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LetterBeforeStructuredQuantityRegex();

    [GeneratedRegex(@"\b(?<unit>g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h|personnes?|people|persons?)(?=\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredUnitBeforeNumberRegex();

    [GeneratedRegex(@"\b(?<unit>g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h|personnes?|people|persons?)(?=\p{Lu})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredUnitBeforeUppercaseRegex();

    [GeneratedRegex(@"(?<=[\.!?:;\)\]\u00ae])(?=\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|units?|items?|pieces?|min|h)\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PunctuationBeforeStructuredQuantityRegex();

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
    int? OffsetEnd = null,
    string? ExtractionTextStatus = null,
    bool ExtractionTextSparse = false,
    bool ExtractionOcrCandidate = false,
    IReadOnlyList<string>? ExtractionQualitySignals = null);
