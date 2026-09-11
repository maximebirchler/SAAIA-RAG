using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class DocumentUnitExtractor
{
    private static readonly string UnitSeparator = Environment.NewLine + Environment.NewLine;
    private const int MaxImplicitBoundaryScanLength = 6000;
    private const int OversizedParagraphWindowLength = 3200;
    private const int OversizedParagraphMinimumWindowLength = 900;
    private const int LongStructuredListSoftMaxTokens = 140;
    private const int LongStructuredListMinimumTokens = 55;
    private const int LongNarrativeSoftMaxTokens = 180;
    private static readonly HashSet<string> SoftHyphenContinuationStopwords = new(StringComparer.Ordinal)
    {
        "and",
        "e",
        "et",
        "nor",
        "o",
        "oder",
        "ou",
        "or",
        "und",
        "y"
    };

    public static IReadOnlyList<ExtractedDocumentUnit> Extract(
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections)
    {
        if (pages.Count == 0)
            return Array.Empty<ExtractedDocumentUnit>();

        var normalizedSectionTitles = sections
            .SelectMany(static s => ExpandNormalizedSectionTitles(s.Title))
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = new List<CandidateDocumentUnit>();
        var skippedProbableOcrNoise = false;

        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
            var paragraphs = SplitParagraphs(page.Text, normalizedSectionTitles);
            if (paragraphs.Count == 0)
                continue;

            var pageCandidateCount = 0;
            foreach (var paragraph in paragraphs)
            {
                var normalized = NormalizeLine(paragraph.Text);
                normalized = OcrNoiseFilter.RemoveTrailingNoisySupplement(normalized);
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

                if (LooksLikeStandaloneStructuredMetadataUnit(normalized))
                    continue;

                candidates.Add(new CandidateDocumentUnit(
                    SectionOrdinal: ResolveSectionForParagraph(sections, page.PageNumber, paragraph.StartLine)?.Ordinal,
                    PageStart: page.PageNumber,
                    PageEnd: page.PageNumber,
                    Text: normalized,
                    TokenCount: tokenCount,
                    StartLine: paragraph.StartLine,
                    EndLine: paragraph.EndLine,
                    ExtractionTextStatus: quality.TextStatus,
                    ExtractionTextSparse: quality.TextSparse,
                    ExtractionOcrCandidate: quality.OcrCandidate,
                    ExtractionQualitySignals: quality.Signals));
                pageCandidateCount++;
            }

            if (pageCandidateCount == 0
                && TryBuildWholePageFallbackCandidate(
                    page,
                    quality,
                    sections,
                    normalizedSectionTitles,
                    paragraphs,
                    out var fallbackCandidate))
            {
                candidates.Add(fallbackCandidate);
            }
        }

        if (candidates.Count == 0)
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

        var filteredCandidates = RemoveRepeatedShortLayoutUnits(candidates, pages.Count);
        filteredCandidates = MergeBoundarySoftHyphenatedCandidates(filteredCandidates);
        filteredCandidates = AddWholePageFallbackCandidatesForUncoveredPages(
            filteredCandidates,
            pages,
            sections,
            normalizedSectionTitles);
        if (filteredCandidates.Count == 0)
            return Array.Empty<ExtractedDocumentUnit>();

        var units = MaterializeUnits(filteredCandidates);
        var repairedCandidates = AddWholePageFallbackCandidatesForUncoveredPages(
            filteredCandidates,
            pages,
            sections,
            normalizedSectionTitles,
            units);
        return repairedCandidates.Count == filteredCandidates.Count
            ? units
            : MaterializeUnits(repairedCandidates);
    }

    private static IReadOnlyList<ExtractedDocumentUnit> MaterializeUnits(IReadOnlyList<CandidateDocumentUnit> candidates)
    {
        var units = new List<ExtractedDocumentUnit>(candidates.Count);
        var offsetCursor = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var text = OcrNoiseFilter.RemoveTrailingNoisySupplement(candidate.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var tokenCount = CountTokens(text);
            if (tokenCount <= 0)
                continue;
            if (OcrNoiseFilter.LooksLikeProbableNoiseText(text)
                && !ShouldAllowSubstantiveCandidateFallback(candidate, text, tokenCount))
            {
                continue;
            }

            if (LooksLikeStandaloneStructuredMetadataUnit(text))
                continue;

            units.Add(new ExtractedDocumentUnit(
                Ordinal: units.Count,
                SectionOrdinal: candidate.SectionOrdinal,
                PageStart: candidate.PageStart,
                PageEnd: candidate.PageEnd,
                Text: text,
                CharCount: text.Length,
                TokenCount: tokenCount,
                Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                OffsetStart: offsetCursor,
                OffsetEnd: offsetCursor + text.Length,
                ExtractionTextStatus: candidate.ExtractionTextStatus,
                ExtractionTextSparse: candidate.ExtractionTextSparse,
                ExtractionOcrCandidate: candidate.ExtractionOcrCandidate,
                ExtractionQualitySignals: candidate.ExtractionQualitySignals));

            offsetCursor += text.Length + UnitSeparator.Length;
        }

        return units;
    }

    private static bool TryBuildWholePageFallbackCandidate(
        ExtractedPdfPage page,
        PdfPageExtractionQuality quality,
        IReadOnlyList<ExtractedDocumentSection> sections,
        ISet<string> normalizedSectionTitles,
        IReadOnlyList<DocumentParagraph> paragraphs,
        out CandidateDocumentUnit candidate)
    {
        candidate = default!;

        var text = NormalizeLine(page.Text);
        text = OcrNoiseFilter.RemoveTrailingNoisySupplement(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (normalizedSectionTitles.Contains(text))
            return false;

        var tokenCount = CountTokens(text);
        if (tokenCount < 12)
            return false;
        if (OcrNoiseFilter.LooksLikeProbableNoiseText(text)
            && !ShouldAllowSubstantiveWholePageFallback(page, quality, text, tokenCount))
        {
            return false;
        }

        if (LooksLikeStandaloneStructuredMetadataUnit(text))
            return false;

        candidate = new CandidateDocumentUnit(
            SectionOrdinal: ResolveSectionForParagraph(
                sections,
                page.PageNumber,
                paragraphs.Count > 0 ? paragraphs[0].StartLine : 0)?.Ordinal,
            PageStart: page.PageNumber,
            PageEnd: page.PageNumber,
            Text: text,
            TokenCount: tokenCount,
            StartLine: paragraphs.Count > 0 ? paragraphs[0].StartLine : 0,
            EndLine: paragraphs.Count > 0 ? paragraphs[^1].EndLine : 0,
            ExtractionTextStatus: quality.TextStatus,
            ExtractionTextSparse: quality.TextSparse,
            ExtractionOcrCandidate: quality.OcrCandidate,
            ExtractionQualitySignals: quality.Signals);
        return true;
    }

    private static bool ShouldAllowSubstantiveCandidateFallback(
        CandidateDocumentUnit candidate,
        string text,
        int tokenCount)
    {
        if (tokenCount < 40 || text.Length < 300)
            return false;
        if (candidate.ExtractionTextSparse || string.Equals(candidate.ExtractionTextStatus, "empty_text", StringComparison.Ordinal))
            return false;

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        return !RetrievalContentClassifier.IsPredominantlyNavigationContent(
                signal.ContentRole,
                chunkType: null,
                signal.NavigationScore,
                signal.ContentDensityScore)
            && signal.ContentDensityScore >= 0.35;
    }

    private static bool ShouldAllowSubstantiveWholePageFallback(
        ExtractedPdfPage page,
        PdfPageExtractionQuality quality,
        string text,
        int tokenCount)
    {
        if (tokenCount < 40 || page.CharCount < 300 || page.WordCount < 30)
            return false;
        if (quality.TextSparse || string.Equals(quality.TextStatus, "empty_text", StringComparison.Ordinal))
            return false;

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);
        return !RetrievalContentClassifier.IsPredominantlyNavigationContent(
                signal.ContentRole,
                chunkType: null,
                signal.NavigationScore,
                signal.ContentDensityScore)
            && signal.ContentDensityScore >= 0.35;
    }

    private static IReadOnlyList<CandidateDocumentUnit> AddWholePageFallbackCandidatesForUncoveredPages(
        IReadOnlyList<CandidateDocumentUnit> candidates,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections,
        ISet<string> normalizedSectionTitles,
        IReadOnlyList<ExtractedDocumentUnit>? units = null)
    {
        var additions = new List<CandidateDocumentUnit>();
        foreach (var page in pages.OrderBy(static p => p.PageNumber))
        {
            var covered = units is null
                ? candidates.Any(candidate => candidate.PageStart <= page.PageNumber && candidate.PageEnd >= page.PageNumber)
                : units.Any(unit => unit.PageStart <= page.PageNumber && unit.PageEnd >= page.PageNumber);
            if (covered)
                continue;

            var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
            var paragraphs = SplitParagraphs(page.Text, normalizedSectionTitles);
            if (TryBuildWholePageFallbackCandidate(
                    page,
                    quality,
                    sections,
                    normalizedSectionTitles,
                    paragraphs,
                    out var fallbackCandidate))
            {
                additions.Add(fallbackCandidate);
            }
        }

        if (additions.Count == 0)
            return candidates;

        return candidates
            .Concat(additions)
            .OrderBy(static candidate => candidate.PageStart)
            .ThenBy(static candidate => candidate.StartLine)
            .ThenBy(static candidate => candidate.EndLine)
            .ToArray();
    }

    private static IReadOnlyList<CandidateDocumentUnit> RemoveRepeatedShortLayoutUnits(
        IReadOnlyList<CandidateDocumentUnit> candidates,
        int pageCount)
    {
        if (candidates.Count < 4 || pageCount < 2)
            return candidates;

        var repeatedLayoutKeys = candidates
            .Where(IsRepeatedShortLayoutUnitCandidate)
            .GroupBy(static unit => NormalizeRepeatedLayoutKey(unit.Text), StringComparer.Ordinal)
            .Where(group => ShouldDropRepeatedShortLayoutGroup(group, pageCount))
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (repeatedLayoutKeys.Count == 0)
            return candidates;

        return candidates
            .Where(unit => !repeatedLayoutKeys.Contains(NormalizeRepeatedLayoutKey(unit.Text)))
            .ToArray();
    }

    private static IReadOnlyList<CandidateDocumentUnit> MergeBoundarySoftHyphenatedCandidates(
        IReadOnlyList<CandidateDocumentUnit> candidates)
    {
        if (candidates.Count < 2)
            return candidates;

        var merged = new List<CandidateDocumentUnit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (merged.Count == 0)
            {
                merged.Add(candidate);
                continue;
            }

            var previous = merged[^1];
            if (!CanMergeBoundarySoftHyphen(previous, candidate))
            {
                merged.Add(candidate);
                continue;
            }

            var mergedText = BoundarySoftHyphenatedCandidateRegex().Replace(
                $"{previous.Text} {candidate.Text}",
                "${left}${right}");
            mergedText = NormalizeLine(mergedText);
            merged[^1] = previous with
            {
                PageEnd = candidate.PageEnd,
                Text = mergedText,
                TokenCount = CountTokens(mergedText),
                EndLine = candidate.EndLine,
                ExtractionTextSparse = previous.ExtractionTextSparse || candidate.ExtractionTextSparse,
                ExtractionOcrCandidate = previous.ExtractionOcrCandidate || candidate.ExtractionOcrCandidate,
                ExtractionQualitySignals = MergeQualitySignals(previous.ExtractionQualitySignals, candidate.ExtractionQualitySignals),
                ExtractionTextStatus = ResolveMergedTextStatus(previous.ExtractionTextStatus, candidate.ExtractionTextStatus)
            };
        }

        return merged;
    }

    private static bool CanMergeBoundarySoftHyphen(CandidateDocumentUnit previous, CandidateDocumentUnit candidate)
    {
        if (candidate.PageStart > previous.PageEnd + 1)
            return false;
        if (StartsWithSoftHyphenContinuationStopword(candidate.Text))
            return false;
        if (!BoundarySoftHyphenatedCandidateRegex().IsMatch($"{previous.Text} {candidate.Text}"))
            return false;
        if (previous.SectionOrdinal != candidate.SectionOrdinal && candidate.PageStart != previous.PageEnd)
            return false;

        return StartsWithLowercaseWord(candidate.Text);
    }

    private static bool StartsWithLowercaseWord(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                return false;

            return char.IsLower(ch);
        }

        return false;
    }

    private static bool StartsWithSoftHyphenContinuationStopword(string text)
    {
        var match = LeadingLowercaseWordRegex().Match(text);
        return match.Success && SoftHyphenContinuationStopwords.Contains(match.Value.ToLowerInvariant());
    }

    private static string? ResolveMergedTextStatus(string? left, string? right)
    {
        var resolved = ResolveWorseTextStatus(left ?? string.Empty, right ?? string.Empty);
        return string.IsNullOrEmpty(resolved) ? null : resolved;
    }

    private static IReadOnlyList<string>? MergeQualitySignals(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        if ((left is null || left.Count == 0) && (right is null || right.Count == 0))
            return null;

        return (left ?? [])
            .Concat(right ?? [])
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRepeatedShortLayoutUnitCandidate(CandidateDocumentUnit unit)
    {
        if (unit.TokenCount is <= 0 or > 5)
            return false;

        var text = unit.Text.Trim();
        if (text.Length is < 3 or > 90)
            return false;

        if (SentenceEndRegex().IsMatch(text))
            return false;

        var letterOrDigitCount = text.Count(static ch => char.IsLetterOrDigit(ch));
        if (letterOrDigitCount < 3)
            return false;

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        if (tokens.Any(static token => token.Length >= 14 && token.Any(char.IsDigit) && token.Any(char.IsLetter)))
            return false;

        return true;
    }

    private static bool LooksLikeStandaloneStructuredMetadataUnit(string text)
    {
        var normalized = NormalizeLine(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokenCount = CountTokens(normalized);
        if (tokenCount is < 3 or > 24)
            return false;

        if (normalized.Contains('\u2022', StringComparison.Ordinal)
            || normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026'))
        {
            return false;
        }

        return StructuredMetadataSignalRegex().Matches(normalized).Count >= 2;
    }

    private static bool ShouldDropRepeatedShortLayoutGroup(
        IGrouping<string, CandidateDocumentUnit> group,
        int pageCount)
    {
        if (string.IsNullOrWhiteSpace(group.Key))
            return false;

        var units = group.ToArray();
        var distinctPages = units.Select(static unit => unit.PageStart).Distinct().Count();
        var minimumOccurrences = pageCount >= 10 ? 3 : 2;
        if (units.Length < minimumOccurrences || distinctPages < minimumOccurrences)
            return false;

        var lineNumbers = units.Select(static unit => unit.StartLine).Distinct().Order().ToArray();
        var stableLine = lineNumbers.Length <= 2 || lineNumbers[^1] - lineNumbers[0] <= 2;
        if (stableLine)
            return true;

        return units.Length >= Math.Max(5, pageCount / 3);
    }

    private static string NormalizeRepeatedLayoutKey(string text)
        => Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();

    private static ExtractedDocumentSection? ResolveSectionForParagraph(
        IReadOnlyList<ExtractedDocumentSection> sections,
        int pageNumber,
        int lineNumber)
    {
        var pageSections = sections
            .Where(s => s.PageStart <= pageNumber && s.PageEnd >= pageNumber)
            .ToList();
        if (pageSections.Count == 0)
            return null;

        var lineAware = pageSections
            .Where(section => SectionContainsLine(section, pageNumber, lineNumber))
            .OrderByDescending(static section => section.PageStart)
            .ThenByDescending(static section => section.StartLine ?? 0)
            .ThenByDescending(static section => section.Ordinal)
            .FirstOrDefault();
        if (lineAware is not null)
            return lineAware;

        return pageSections
            .OrderByDescending(static section => section.PageStart)
            .ThenByDescending(static section => section.Ordinal)
            .FirstOrDefault();
    }

    private static bool SectionContainsLine(ExtractedDocumentSection section, int pageNumber, int lineNumber)
    {
        if (pageNumber < section.PageStart || pageNumber > section.PageEnd)
            return false;

        if (pageNumber == section.PageStart && section.StartLine.HasValue && lineNumber < section.StartLine.Value)
            return false;

        if (pageNumber == section.PageEnd && section.EndLine.HasValue && lineNumber > section.EndLine.Value)
            return false;

        return true;
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

    private static List<DocumentParagraph> SplitParagraphs(string text, ISet<string> normalizedSectionTitles)
    {
        var blocks = BuildParagraphBlocks(text, normalizedSectionTitles);
        if (blocks.Count == 0)
            return [];

        return SplitDenseStructuredParagraphs(blocks, normalizedSectionTitles);
    }

    private static List<ParagraphBlock> BuildParagraphBlocks(string text, ISet<string> normalizedSectionTitles)
    {
        var normalizedNewlines = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalizedNewlines.Split('\n');
        var blocks = new List<ParagraphBlock>();
        var buffer = new List<string>();
        var startLine = 0;
        var endLine = 0;
        var contentLineNumber = 0;

        void Flush()
        {
            if (buffer.Count == 0)
                return;

            blocks.Add(new ParagraphBlock(buffer.ToArray(), startLine, endLine));
            buffer.Clear();
            startLine = 0;
            endLine = 0;
        }

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = NormalizeLine(rawLines[i]);
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var lineNumber = ++contentLineNumber;
            if (normalizedSectionTitles.Contains(line))
                Flush();

            if (buffer.Count == 0)
                startLine = lineNumber;

            buffer.Add(line);
            endLine = lineNumber;

            if (normalizedSectionTitles.Contains(line))
                Flush();
        }

        Flush();
        return blocks;
    }

    private static List<DocumentParagraph> SplitDenseStructuredParagraphs(
        IEnumerable<ParagraphBlock> paragraphs,
        ISet<string> normalizedSectionTitles)
    {
        var result = new List<DocumentParagraph>();
        foreach (var paragraph in paragraphs)
        {
            var text = BuildParagraphTextWithoutLeadingTitles(paragraph, normalizedSectionTitles, out var startLine);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            foreach (var segment in SplitDenseStructuredParagraph(text))
                result.Add(new DocumentParagraph(segment, startLine, paragraph.EndLine));
        }

        return result;
    }

    private static string BuildParagraphTextWithoutLeadingTitles(
        ParagraphBlock paragraph,
        ISet<string> normalizedSectionTitles,
        out int startLine)
    {
        var lines = paragraph.Lines;
        var index = 0;
        while (index < lines.Length && normalizedSectionTitles.Contains(NormalizeLine(lines[index])))
            index++;

        startLine = paragraph.StartLine + index;
        if (index >= lines.Length)
            return string.Empty;

        return JoinParagraphLines(lines.Skip(index));
    }

    private static string JoinParagraphLines(IEnumerable<string> lines)
    {
        var joined = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = NormalizeLine(rawLine);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (joined.Length == 0)
            {
                joined.Append(line);
                continue;
            }

            if (ShouldJoinSoftHyphenatedLineBreak(joined, line))
            {
                RemoveTrailingSoftHyphen(joined);
                joined.Append(line.TrimStart());
                continue;
            }

            joined.Append(' ');
            joined.Append(line);
        }

        return NormalizeLine(joined.ToString());
    }

    private static bool ShouldJoinSoftHyphenatedLineBreak(StringBuilder previous, string nextLine)
    {
        var previousIndex = previous.Length - 1;
        while (previousIndex >= 0 && char.IsWhiteSpace(previous[previousIndex]))
            previousIndex--;
        if (previousIndex <= 0 || !IsSoftLineHyphen(previous[previousIndex]))
            return false;

        var beforeHyphenIndex = previousIndex - 1;
        while (beforeHyphenIndex >= 0 && char.IsWhiteSpace(previous[beforeHyphenIndex]))
            beforeHyphenIndex--;
        if (beforeHyphenIndex < 0 || !char.IsLetter(previous[beforeHyphenIndex]))
            return false;

        var nextIndex = 0;
        while (nextIndex < nextLine.Length && char.IsWhiteSpace(nextLine[nextIndex]))
            nextIndex++;

        return nextIndex < nextLine.Length
            && char.IsLower(nextLine[nextIndex])
            && !StartsWithSoftHyphenContinuationStopword(nextLine[nextIndex..]);
    }

    private static void RemoveTrailingSoftHyphen(StringBuilder text)
    {
        while (text.Length > 0 && char.IsWhiteSpace(text[^1]))
            text.Length--;
        if (text.Length > 0 && IsSoftLineHyphen(text[^1]))
            text.Length--;
        while (text.Length > 0 && char.IsWhiteSpace(text[^1]))
            text.Length--;
    }

    private static bool IsSoftLineHyphen(char ch)
        => ch is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014';

    private static IReadOnlyList<string> SplitDenseStructuredParagraph(string paragraph)
    {
        paragraph = NormalizeLine(paragraph);
        if (paragraph.Length < 120)
            return [paragraph];

        if (paragraph.Length > MaxImplicitBoundaryScanLength)
            return SplitOversizedDenseParagraph(paragraph);

        var boundaries = FindImplicitStructuredBoundaries(paragraph);
        if (boundaries.Count == 0)
        {
            var structuredListSegments = SplitLongStructuredListParagraph(paragraph);
            if (structuredListSegments.Count > 1)
                return structuredListSegments;

            return SplitLongNarrativeParagraph(paragraph);
        }

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
        if (segments.Count <= 1)
            return [paragraph];

        var refinedSegments = new List<string>();
        foreach (var segment in segments)
        {
            var splitSegments = SplitLongStructuredListParagraph(segment);
            if (splitSegments.Count <= 1)
                splitSegments = SplitLongNarrativeParagraph(segment);
            refinedSegments.AddRange(splitSegments.Count > 1 ? splitSegments : [segment]);
        }

        return refinedSegments.Count > 1 ? refinedSegments : [paragraph];
    }

    private static IReadOnlyList<string> SplitLongStructuredListParagraph(string paragraph)
    {
        if (CountTokens(paragraph) <= LongStructuredListSoftMaxTokens)
            return [paragraph];

        var markerMatches = StructuralListMarkerRegex().Matches(paragraph);
        if (markerMatches.Count < 4)
            return [paragraph];

        var pieces = new List<string>();
        void AddPiece(string value)
        {
            var piece = NormalizeLine(value);
            if (!string.IsNullOrWhiteSpace(piece))
                pieces.Add(piece);
        }

        var previous = 0;
        foreach (Match match in markerMatches)
        {
            var markerIndex = match.Index + match.Value.TakeWhile(char.IsWhiteSpace).Count();
            if (markerIndex > previous)
                AddPiece(paragraph[previous..markerIndex]);
            previous = markerIndex;
        }

        if (previous < paragraph.Length)
            AddPiece(paragraph[previous..]);

        if (pieces.Count < 4)
            return [paragraph];

        var result = new List<string>();
        var buffer = new List<string>();
        var tokenTotal = 0;

        void Flush()
        {
            if (buffer.Count == 0)
                return;

            var text = NormalizeLine(string.Join(' ', buffer));
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
            buffer.Clear();
            tokenTotal = 0;
        }

        foreach (var piece in pieces)
        {
            var pieceTokens = CountTokens(piece);
            if (pieceTokens <= 0)
                continue;

            if (buffer.Count > 0
                && tokenTotal >= LongStructuredListMinimumTokens
                && tokenTotal + pieceTokens > LongStructuredListSoftMaxTokens)
            {
                Flush();
            }

            if (pieceTokens > LongStructuredListSoftMaxTokens)
            {
                Flush();
                foreach (var splitPiece in SplitLongTextAtSentenceBoundaries(piece, LongStructuredListSoftMaxTokens))
                    result.Add(splitPiece);
                continue;
            }

            buffer.Add(piece);
            tokenTotal += pieceTokens;
        }

        Flush();
        return result.Count > 1 ? result : [paragraph];
    }

    private static IReadOnlyList<string> SplitLongNarrativeParagraph(string paragraph)
    {
        if (CountTokens(paragraph) <= LongNarrativeSoftMaxTokens)
            return [paragraph];

        var segments = SplitLongTextAtSentenceBoundaries(paragraph, LongNarrativeSoftMaxTokens);
        return segments.Count > 1 ? segments : [paragraph];
    }

    private static IReadOnlyList<string> SplitLongTextAtSentenceBoundaries(string text, int softMaxTokens)
    {
        var normalized = NormalizeLine(text);
        if (CountTokens(normalized) <= softMaxTokens)
            return [normalized];

        var result = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var end = ResolveTokenWindowEnd(normalized, start, softMaxTokens);
            var segment = NormalizeLine(normalized[start..end]);
            if (!string.IsNullOrWhiteSpace(segment))
                result.Add(segment);
            start = end;
            while (start < normalized.Length && char.IsWhiteSpace(normalized[start]))
                start++;
        }

        return result.Count > 0 ? result : [normalized];
    }

    private static int ResolveTokenWindowEnd(string text, int start, int softMaxTokens)
    {
        var tokenCount = 0;
        var lastSentenceEnd = -1;
        var lastWhitespace = -1;
        for (var i = start; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                lastWhitespace = i;
                if (i > start && !char.IsWhiteSpace(text[i - 1]))
                    tokenCount++;
            }

            if (".!?;:".Contains(text[i]))
                lastSentenceEnd = i + 1;

            if (tokenCount < softMaxTokens)
                continue;

            if (lastSentenceEnd > start + 40)
                return lastSentenceEnd;
            if (lastWhitespace > start + 40)
                return lastWhitespace;
            return i + 1;
        }

        return text.Length;
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
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length == 0)
            return normalized;

        normalized = RemoveInlineRevisionLegendFragments(normalized, out var removedRevisionLegend);
        if (removedRevisionLegend)
            normalized = RepairInlineSoftHyphenatedWordBreaks(normalized);
        normalized = RepairInlineHyphenatedCompoundSpacing(normalized);
        normalized = RepairInlineShortPrefixSoftHyphenatedWordBreaks(normalized);
        normalized = LeadingOcrSectionColonRegex().Replace(normalized, "${left}.${right}");
        normalized = BooleanChoiceLabelBeforeDigitRegex().Replace(normalized, "${label} ");
        return normalized;
    }

    private static string RemoveInlineRevisionLegendFragments(string text, out bool removed)
    {
        removed = false;
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var matches = InlineRevisionLegendRegex().Matches(text);
        removed = matches.Count > 0;
        if (!removed)
            return text;

        var cleaned = InlineRevisionLegendRegex().Replace(text, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static string RepairInlineSoftHyphenatedWordBreaks(string text)
        => InlineSoftHyphenatedWordBreakRegex().Replace(text, "${left}${right}");

    private static string RepairInlineHyphenatedCompoundSpacing(string text)
        => InlineHyphenatedCompoundSpacingRegex().Replace(text, "${left}-${right}");

    private static string RepairInlineShortPrefixSoftHyphenatedWordBreaks(string text)
        => InlineShortPrefixSoftHyphenatedWordBreakRegex().Replace(text, "${left}${right}");

    private static IEnumerable<string> ExpandNormalizedSectionTitles(string title)
    {
        var normalized = NormalizeLine(title);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var splitIndex = -1;
        for (var i = 1; i < words.Length - 1; i++)
        {
            if (IsContinuationConnector(words[i]))
                splitIndex = i;
        }

        if (splitIndex > 0)
        {
            var prefix = NormalizeLine(string.Join(' ', words.Take(splitIndex)));
            var suffix = NormalizeLine(string.Join(' ', words.Skip(splitIndex)));
            if (CountTokens(prefix) >= 2)
                yield return prefix;
            if (CountTokens(suffix) >= 2)
                yield return suffix;
        }

        if (words.Length >= 3 && LooksLikeSectionTitleWithPotentialLineBreak(normalized))
        {
            for (var i = 1; i < words.Length; i++)
            {
                var prefix = NormalizeLine(string.Join(' ', words.Take(i)));
                var suffix = NormalizeLine(string.Join(' ', words.Skip(i)));
                if (LooksLikeTitleLineComponent(prefix))
                    yield return prefix;
                if (LooksLikeTitleLineComponent(suffix))
                    yield return suffix;
            }
        }

        if (LooksLikeMostlyUppercaseSectionTitle(normalized) && words.Length >= 4)
        {
            var firstSplit = Math.Max(2, words.Length - 4);
            var lastSplit = words.Length - 2;
            for (var i = firstSplit; i <= lastSplit; i++)
            {
                var prefix = NormalizeLine(string.Join(' ', words.Take(i)));
                var suffix = NormalizeLine(string.Join(' ', words.Skip(i)));
                if (CountTokens(prefix) >= 2)
                    yield return prefix;
                if (CountTokens(suffix) >= 2)
                    yield return suffix;
            }
        }
    }

    private static bool LooksLikeSectionTitleWithPotentialLineBreak(string title)
    {
        if (title.Length is < 8 or > 140)
            return false;
        if (title.Any(static ch => char.IsDigit(ch) || ch is ':' or ';' or '|' or '\u2022'))
            return false;

        var first = title.FirstOrDefault(char.IsLetter);
        return first != default && char.IsUpper(first);
    }

    private static bool LooksLikeTitleLineComponent(string text)
    {
        var normalized = NormalizeLine(text);
        if (normalized.Length is < 4 or > 100)
            return false;
        if (normalized.Any(static ch => char.IsDigit(ch) || ch is ':' or ';' or '|' or '\u2022'))
            return false;

        var first = normalized.FirstOrDefault(char.IsLetter);
        if (first == default || !char.IsUpper(first))
            return false;

        return normalized.Count(char.IsLetter) >= 4;
    }

    private static bool LooksLikeMostlyUppercaseSectionTitle(string title)
    {
        var letters = title.Where(char.IsLetter).ToArray();
        if (letters.Length < 8)
            return false;

        var uppercase = letters.Count(char.IsUpper);
        return uppercase >= Math.Ceiling(letters.Length * 0.72d);
    }

    private static bool IsContinuationConnector(string value)
    {
        var normalized = TrimToken(value).ToUpperInvariant();
        return normalized is "A" or "\u00c0" or "AL" or "ALLA" or "AND" or "AU" or "AUX" or "CON" or "DE" or "DEL" or "DELLA" or "DES" or "DU" or "ET" or "MIT" or "OF" or "THE" or "UND" or "WITH";
    }

    private static string TrimToken(string value)
        => value.Trim(' ', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '\u2018', '\u2019', '\u201c', '\u201d');

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

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\d{4}\s+Edition\s+)?(?:(?:(?:Shaded|Deleted|Deletec|Deleti\w*|Jeleti)\s+text\s*=\s*Revisi\w*|(?:A|\u00c0|4)\s*=\s*Text\s+deleti\w*(?:\s+and\s+figure\s*/\s*table\s+revisions?)?|(?:A|\u00c0|4)\s*=\s*Text\s+deletion:?\s*s\s+and\s+figure\s*/\s*table\s+revisions?|:?\s*s\s+and\s+figure\s*/\s*table\s+revisions?|(?:\*|\+|e|\u00b0|\u00ae)?\s*=\s*Section\s+deletions?|N\s*=\s*New\s+material)[\.,;]?\s*)+(?:\d{4}\s+Edition)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InlineRevisionLegendRegex();

    [GeneratedRegex(@"(?<left>\p{Ll}[\p{Ll}\p{M}]{2,})[-\u2010\u2011\u2012\u2013\u2014]\s+(?<right>(?!and\b|nor\b|oder\b|or\b|und\b)\p{Ll}[\p{Ll}\p{M}]{2,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InlineSoftHyphenatedWordBreakRegex();

    [GeneratedRegex(@"(?<left>\p{L}[\p{L}\p{M}]{2,})[-\u2010\u2011\u2012\u2013\u2014]\s+(?<right>based|circuit|controlled|current|load|mounted|operated|phase|pole|proof|protected|rated|related|resistant|section|tight|time|type|types|voltage|wire)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InlineHyphenatedCompoundSpacingRegex();

    [GeneratedRegex(@"(?<left>\p{Ll}[\p{Ll}\p{M}]{1,2})[-\u2010\u2011\u2012\u2013\u2014]\s+(?<right>(?!and\b|e\b|et\b|nor\b|o\b|oder\b|ou\b|or\b|und\b|y\b)\p{Ll}[\p{Ll}\p{M}]{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex InlineShortPrefixSoftHyphenatedWordBreakRegex();

    [GeneratedRegex(@"(?<left>\p{Ll}[\p{Ll}\p{M}]{2,})[-\u2010\u2011\u2012\u2013\u2014]\s+(?<right>(?!and\b|e\b|et\b|nor\b|o\b|oder\b|ou\b|or\b|und\b|y\b)\p{Ll}[\p{Ll}\p{M}]{2,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BoundarySoftHyphenatedCandidateRegex();

    [GeneratedRegex(@"^\p{Ll}[\p{Ll}\p{M}]*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingLowercaseWordRegex();

    [GeneratedRegex(@"^(?<left>\d{1,2}):(?<right>\d(?:\.\d){1,5})(?=\b|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingOcrSectionColonRegex();

    [GeneratedRegex(@"\b(?<label>(?:no|yes|oui|non|ja|nein|si|s\u00ed)/(?:no|yes|oui|non|ja|nein|si|s\u00ed))(?=\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BooleanChoiceLabelBeforeDigitRegex();

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

    [GeneratedRegex(@"(?:^|\s)(?:[\u2022*]\s*|\-\s+|\u2013\s+|\u2014\s+)(?=\S)", RegexOptions.CultureInvariant)]
    private static partial Regex StructuralListMarkerRegex();

    [GeneratedRegex(@"\b(?:preparation|pr[e\u00e9]paration|duration|dur[e\u00e9]e|time|temps|difficulty|difficult[e\u00e9]|level|niveau|facile|easy|interm[e\u00e9]diaire|medium|difficile|hard|assez\s+cher|pas\s+cher|bon\s+march[e\u00e9]|cheap|expensive|\d{1,3}\s*(?:min|minutes?|h|hr|hrs?|hours?|heures?))\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StructuredMetadataSignalRegex();

    private sealed record ParagraphBlock(string[] Lines, int StartLine, int EndLine);
    private sealed record DocumentParagraph(string Text, int StartLine, int EndLine);

    private sealed record CandidateDocumentUnit(
        int? SectionOrdinal,
        int PageStart,
        int PageEnd,
        string Text,
        int TokenCount,
        int StartLine,
        int EndLine,
        string? ExtractionTextStatus,
        bool ExtractionTextSparse,
        bool ExtractionOcrCandidate,
        IReadOnlyList<string>? ExtractionQualitySignals);
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
    IReadOnlyList<string>? ExtractionQualitySignals = null,
    string? SectionTitle = null,
    string? HeadingPath = null,
    int? HeadingLevel = null);
