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
        var sourceTextByPage = new Dictionary<int, string>();
        var pageSizeByPage = new Dictionary<int, (double WidthPoints, double HeightPoints)>();
        var nativeLayoutByPage = new Dictionary<int, PdfNativeLayoutExtraction>();
        var nativeImagesByPage = new Dictionary<int, IReadOnlyList<ExtractedPdfImageRegion>>();

        using var doc = PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var sourceText = page.Text ?? string.Empty;
            var rawText = ExtractLayoutAwarePageText(page);
            var text = OcrNoiseFilter.RemoveSpacedLetterRunNoise(PdfTextSanitizer.ForStorage(rawText));
            var nativeLayout = ExtractNativeLayout(page);
            var nativeImages = ExtractPageImages(page);
            rawPages.Add((page.Number, text, nativeImages.Count));
            sourceTextByPage[page.Number] = sourceText;
            pageSizeByPage[page.Number] = (page.Width, page.Height);
            nativeLayoutByPage[page.Number] = nativeLayout;
            nativeImagesByPage[page.Number] = nativeImages;
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
            sourceTextByPage.TryGetValue(rawPage.PageNumber, out var sourceText);
            var hasPageSize = pageSizeByPage.TryGetValue(rawPage.PageNumber, out var pageSize);
            nativeLayoutByPage.TryGetValue(
                rawPage.PageNumber,
                out var nativeLayout);
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
                ImageCount: rawPage.ImageCount,
                RawText: sourceText,
                WidthPoints: hasPageSize ? pageSize.WidthPoints : null,
                HeightPoints: hasPageSize ? pageSize.HeightPoints : null,
                NativeLayoutText: nativeLayout?.Text,
                NativeLayoutBlocks: nativeLayout?.Blocks,
                NativeLayoutAlgorithm: nativeLayout?.Algorithm,
                NativeImageRegions: nativeImagesByPage.TryGetValue(
                    rawPage.PageNumber,
                    out var nativeImages)
                    ? nativeImages
                    : Array.Empty<ExtractedPdfImageRegion>()));
        }

        return new PdfExtractionResult(tokens, pages, PdfExtractionQualitySummary.FromPages(pages));
    }

    public static List<WordToken> ExtractWordTokens(string pdfPath)
        => ExtractWordTokens(pdfPath, CancellationToken.None);

    public static List<WordToken> ExtractWordTokens(string pdfPath, CancellationToken ct)
        => Extract(pdfPath, ct).Tokens;

    private static IEnumerable<string> SplitWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<ExtractedPdfImageRegion> ExtractPageImages(
        UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return page.GetImages()
                .Select((image, index) => new ExtractedPdfImageRegion(
                    SourceIndex: index,
                    Left: image.Bounds.Left,
                    Right: image.Bounds.Right,
                    Top: image.Bounds.Top,
                    Bottom: image.Bounds.Bottom,
                    WidthInSamples: image.WidthInSamples,
                    HeightInSamples: image.HeightInSamples))
                .Where(static image => image.HasUsableGeometry)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ExtractedPdfImageRegion>();
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

    private static PdfNativeLayoutExtraction ExtractNativeLayout(
        UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return PdfNativeLayoutExtractor.Extract(page);
        }
        catch
        {
            return new(
                Text: string.Empty,
                Blocks: Array.Empty<ExtractedPdfLayoutBlock>(),
                Algorithm: PdfNativeLayoutExtractor.Algorithm);
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

        var reconstructed = BuildPositionedLayoutText(
            usableWords,
            sameLineTolerance,
            paragraphGapThreshold,
            medianWidth,
            wrapBackThreshold).Trim();
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

    private static string BuildPositionedLayoutText(
        IReadOnlyList<PdfLayoutWord> words,
        double sameLineTolerance,
        double paragraphGapThreshold,
        double medianWidth,
        double wrapBackThreshold)
    {
        var lines = BuildLayoutLines(words, sameLineTolerance);
        if (lines.Count == 0)
            return string.Empty;

        var segments = lines
            .SelectMany((line, index) => SplitLineIntoSegments(line, index, medianWidth))
            .ToArray();

        return TryBuildColumnarText(segments, lines.Count, paragraphGapThreshold, medianWidth, out var columnar)
            ? columnar
            : BuildLineOrderedText(lines, paragraphGapThreshold, wrapBackThreshold);
    }

    private static List<PdfLayoutLine> BuildLayoutLines(IReadOnlyList<PdfLayoutWord> words, double sameLineTolerance)
    {
        var ordered = words
            .Select(static word => word with { Text = PdfTextSanitizer.ForStorage(word.Text).Trim() })
            .Where(static word => word.Text.Length > 0)
            .OrderByDescending(static word => word.CenterY)
            .ThenBy(static word => word.Left)
            .ToArray();

        var lines = new List<PdfLayoutLine>();
        foreach (var word in ordered)
        {
            var line = lines.LastOrDefault();
            if (line is null || Math.Abs(word.CenterY - line.CenterY) > sameLineTolerance)
            {
                lines.Add(new PdfLayoutLine([word]));
                continue;
            }

            line.Words.Add(word);
        }

        foreach (var line in lines)
            line.Words.Sort(static (left, right) => left.Left.CompareTo(right.Left));

        return lines;
    }

    private static IReadOnlyList<PdfLayoutSegment> SplitLineIntoSegments(
        PdfLayoutLine line,
        int lineIndex,
        double medianWidth)
    {
        if (line.Words.Count == 0)
            return Array.Empty<PdfLayoutSegment>();

        var gapThreshold = Math.Clamp(medianWidth * 1.50d, 20.0d, 58.0d);
        var segments = new List<PdfLayoutSegment>();
        var buffer = new List<PdfLayoutWord>();
        PdfLayoutWord? previous = null;

        void Flush()
        {
            if (buffer.Count == 0)
                return;

            segments.Add(new PdfLayoutSegment(
                lineIndex,
                string.Join(' ', buffer.Select(static word => word.Text)),
                buffer.Min(static word => word.Left),
                buffer.Max(static word => word.Right),
                buffer.Max(static word => word.Top),
                buffer.Min(static word => word.Bottom)));
            buffer.Clear();
        }

        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            var next = i + 1 < line.Words.Count ? line.Words[i + 1] : (PdfLayoutWord?)null;
            if (previous is not null
                && (word.Left - previous.Value.Right > gapThreshold
                    || ShouldStartInlineNumberedLayoutSegment(buffer, word, next)))
            {
                Flush();
            }

            buffer.Add(word);
            previous = word;
        }

        Flush();
        return segments;
    }

    private static bool ShouldStartInlineNumberedLayoutSegment(
        IReadOnlyList<PdfLayoutWord> buffer,
        PdfLayoutWord current,
        PdfLayoutWord? next)
    {
        if (buffer.Count < 3 || next is null)
            return false;

        var marker = current.Text.Trim(' ', '.', ')', ']', ':');
        if (!int.TryParse(marker, out var stepNumber) || stepNumber is < 1 or > 50)
            return false;

        var nextFirstLetter = next.Value.Text.FirstOrDefault(char.IsLetter);
        if (nextFirstLetter == default || !char.IsUpper(nextFirstLetter))
            return false;

        var previousText = buffer[^1].Text.Trim();
        if (previousText.EndsWith("(", StringComparison.Ordinal)
            || previousText.EndsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool TryBuildColumnarText(
        IReadOnlyList<PdfLayoutSegment> segments,
        int lineCount,
        double paragraphGapThreshold,
        double medianWidth,
        out string text)
    {
        text = string.Empty;
        if (segments.Count < 6 || lineCount < 3)
            return false;

        var multiSegmentLineCount = segments
            .GroupBy(static segment => segment.LineIndex)
            .Count(static group => group.Count() >= 2);
        if (multiSegmentLineCount < Math.Max(3, (int)Math.Ceiling(lineCount * 0.30d)))
            return false;

        var columns = ClusterSegmentsIntoColumns(segments, medianWidth)
            .Where(static column => column.Count >= 3)
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();
        if (columns.Length < 2)
            return false;

        var columnSegmentCount = columns.Sum(static column => column.Count);
        if (columnSegmentCount < Math.Ceiling(segments.Count * 0.60d))
            return false;

        var builder = new StringBuilder();
        for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
        {
            if (builder.Length > 0)
                builder.Append("\n\n\n");

            AppendSegmentsReadingOrder(builder, columns[columnIndex], paragraphGapThreshold, medianWidth);
        }

        text = builder.ToString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static List<List<PdfLayoutSegment>> ClusterSegmentsIntoColumns(
        IReadOnlyList<PdfLayoutSegment> segments,
        double medianWidth)
    {
        var tolerance = Math.Clamp(medianWidth * 3.5d, 45.0d, 110.0d);
        var columns = new List<List<PdfLayoutSegment>>();
        foreach (var segment in segments.OrderBy(static segment => segment.Left))
        {
            var column = columns
                .Where(column => Math.Abs(segment.Left - column.Average(static item => item.Left)) <= tolerance
                                 || segment.Left <= column.Max(static item => item.Right) + tolerance
                                    && segment.Right >= column.Min(static item => item.Left) - tolerance)
                .OrderBy(column => Math.Abs(segment.Left - column.Average(static item => item.Left)))
                .FirstOrDefault();

            if (column is null)
            {
                columns.Add([segment]);
            }
            else
            {
                column.Add(segment);
            }
        }

        return columns;
    }

    private static string BuildLineOrderedText(
        IReadOnlyList<PdfLayoutLine> lines,
        double paragraphGapThreshold,
        double wrapBackThreshold)
    {
        var builder = new StringBuilder();
        PdfLayoutLine? previous = null;
        foreach (var line in lines)
        {
            if (line.Words.Count == 0)
                continue;

            if (previous is not null)
            {
                var verticalGap = previous.Bottom - line.Top;
                var wrapsBack = line.Left + wrapBackThreshold < previous.Left;
                builder.Append(verticalGap > paragraphGapThreshold || wrapsBack ? "\n\n" : "\n");
            }

            builder.Append(string.Join(' ', line.Words.Select(static word => word.Text)));
            previous = line;
        }

        return builder.ToString();
    }

    private static void AppendSegmentsTopDown(
        StringBuilder builder,
        IReadOnlyList<PdfLayoutSegment> segments,
        double paragraphGapThreshold)
    {
        PdfLayoutSegment? previous = null;
        foreach (var segment in segments.OrderBy(static segment => segment.LineIndex))
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            if (previous is not null)
            {
                var verticalGap = previous.Value.Bottom - segment.Top;
                builder.Append(verticalGap > paragraphGapThreshold ? "\n\n" : "\n");
            }

            builder.Append(segment.Text);
            previous = segment;
        }
    }

    private static void AppendSegmentsReadingOrder(
        StringBuilder builder,
        IReadOnlyList<PdfLayoutSegment> segments,
        double paragraphGapThreshold,
        double medianWidth)
    {
        if (TryBuildNestedColumnarSegmentText(segments, paragraphGapThreshold, medianWidth, out var nestedText))
        {
            builder.Append(nestedText);
            return;
        }

        AppendSegmentsTopDown(builder, segments, paragraphGapThreshold);
    }

    private static bool TryBuildNestedColumnarSegmentText(
        IReadOnlyList<PdfLayoutSegment> segments,
        double paragraphGapThreshold,
        double medianWidth,
        out string text)
    {
        text = string.Empty;
        if (segments.Count < 7)
            return false;

        var distinctLineCount = segments.Select(static segment => segment.LineIndex).Distinct().Count();
        var multiSegmentLineCount = segments
            .GroupBy(static segment => segment.LineIndex)
            .Count(static group => group.Count() >= 2);
        if (multiSegmentLineCount < Math.Max(3, (int)Math.Ceiling(distinctLineCount * 0.15d)))
            return false;

        var tolerance = Math.Clamp(medianWidth * 2.1d, 28.0d, 48.0d);
        var nestedColumns = ClusterSegmentsByLeftAnchor(segments, tolerance)
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();
        var stableColumns = nestedColumns
            .Where(static column => column.Count >= 3)
            .ToArray();
        if (stableColumns.Length < 2)
            return false;

        var stableSegmentCount = stableColumns.Sum(static column => column.Count);
        if (stableSegmentCount < Math.Ceiling(segments.Count * 0.60d))
            return false;

        var stableLeft = stableColumns.Min(static column => column.Min(static segment => segment.Left));
        var stableRight = stableColumns.Max(static column => column.Max(static segment => segment.Right));
        if (stableRight - stableLeft < Math.Max(120.0d, medianWidth * 6.0d))
            return false;

        var firstStableLine = stableColumns
            .SelectMany(static column => column)
            .Min(static segment => segment.LineIndex);
        var prefixEndLine = ResolveNestedColumnPrefixEndLine(segments, firstStableLine);
        var prefix = segments
            .Where(segment => segment.LineIndex <= prefixEndLine)
            .OrderBy(static segment => segment.LineIndex)
            .ThenBy(static segment => segment.Left)
            .ToArray();
        var bodySegments = segments
            .Where(segment => segment.LineIndex > prefixEndLine)
            .ToArray();
        if (bodySegments.Length == 0)
            return false;

        var bodyColumns = ClusterSegmentsByLeftAnchor(bodySegments, tolerance)
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();

        var builder = new StringBuilder();
        if (prefix.Length > 0)
        {
            if (!TryAppendNestedPrefixByColumn(
                    builder,
                    prefix,
                    paragraphGapThreshold,
                    tolerance,
                    medianWidth))
            {
                AppendSegmentsTopDown(builder, prefix, paragraphGapThreshold);
            }
        }

        foreach (var column in bodyColumns)
        {
            if (column.Count == 0)
                continue;

            if (builder.Length > 0)
                builder.Append("\n\n");
            AppendSegmentsTopDown(builder, column, paragraphGapThreshold);
        }

        text = builder.ToString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryAppendNestedPrefixByColumn(
        StringBuilder builder,
        IReadOnlyList<PdfLayoutSegment> prefix,
        double paragraphGapThreshold,
        double tolerance,
        double medianWidth)
    {
        if (prefix.Count < 4)
            return false;

        var columns = ClusterSegmentsByLeftAnchor(prefix, tolerance)
            .Where(static column => column.Count >= 2)
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();
        if (columns.Length < 2)
            return false;

        var coveredSegmentCount = columns.Sum(static column => column.Count);
        if (coveredSegmentCount < Math.Ceiling(prefix.Count * 0.75d))
            return false;

        for (var index = 1; index < columns.Length; index++)
        {
            var previousRight = columns[index - 1].Max(static segment => segment.Right);
            var currentLeft = columns[index].Min(static segment => segment.Left);
            if (currentLeft - previousRight < Math.Max(10.0d, medianWidth * 0.75d))
                return false;
        }

        foreach (var column in columns)
        {
            if (builder.Length > 0)
                builder.Append("\n\n");
            AppendSegmentsTopDown(builder, column, paragraphGapThreshold);
        }

        return true;
    }

    private static int ResolveNestedColumnPrefixEndLine(
        IReadOnlyList<PdfLayoutSegment> segments,
        int firstStableLine)
    {
        var headingSearchEndLine = firstStableLine + 8;
        var headingLine = segments
            .Where(segment => segment.LineIndex <= headingSearchEndLine)
            .Where(static segment => LooksLikeLayoutHeadingSegment(segment.Text))
            .Select(static segment => segment.LineIndex)
            .DefaultIfEmpty(firstStableLine - 1)
            .Max();

        return Math.Max(firstStableLine - 1, headingLine);
    }

    private static bool LooksLikeLayoutHeadingSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeBoilerplateLine(text);
        if (normalized.Length is < 4 or > 140)
            return false;
        if (normalized.EndsWith(".", StringComparison.Ordinal)
            || normalized.EndsWith(",", StringComparison.Ordinal)
            || normalized.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        var letterCount = normalized.Count(char.IsLetter);
        if (letterCount < 3)
            return false;

        var digitCount = normalized.Count(char.IsDigit);
        if (digitCount > Math.Max(2, letterCount / 3))
            return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 16)
            return false;

        var uppercaseLetters = normalized.Count(char.IsUpper);
        if (uppercaseLetters >= Math.Ceiling(letterCount * 0.70d))
            return true;

        var titleCaseWords = words.Count(static word =>
        {
            var firstLetter = word.FirstOrDefault(char.IsLetter);
            return firstLetter != default && char.IsUpper(firstLetter);
        });

        return words.Length >= 2 && titleCaseWords >= Math.Max(2, words.Length - 1);
    }

    private static List<List<PdfLayoutSegment>> ClusterSegmentsByLeftAnchor(
        IReadOnlyList<PdfLayoutSegment> segments,
        double tolerance)
    {
        var columns = new List<List<PdfLayoutSegment>>();
        foreach (var segment in segments.OrderBy(static segment => segment.Left))
        {
            var column = columns
                .Where(column => Math.Abs(segment.Left - column.Average(static item => item.Left)) <= tolerance)
                .OrderBy(column => Math.Abs(segment.Left - column.Average(static item => item.Left)))
                .FirstOrDefault();

            if (column is null)
            {
                columns.Add([segment]);
            }
            else
            {
                column.Add(segment);
            }
        }

        return columns;
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
            if (normalized.Length is < 8 or > 260)
                continue;

            var tokenCount = SplitWords(normalized).Count();
            if (tokenCount is < 2 or > 24)
                continue;
            if (tokenCount > 12 && !LooksLikeExtendedBoilerplateLine(normalized))
                continue;

            yield return normalized;
        }
    }

    private static bool LooksLikeExtendedBoilerplateLine(string normalized)
    {
        var layoutMarkerCount = normalized.Count(static ch =>
            ch is '=' or '|' or '\u00a9' or '\u2022' or '+' or '*' or '\u00ae');
        if (layoutMarkerCount >= 2)
            return true;

        if (System.Text.RegularExpressions.Regex.IsMatch(
                normalized,
                @"\b(?:page|edition|revision|revisions|copyright|document|section|chapter)\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var letters = normalized.Where(char.IsLetter).ToArray();
            if (letters.Length >= 12)
            {
                var uppercase = letters.Count(char.IsUpper);
                return uppercase >= Math.Ceiling(letters.Length * 0.35);
            }
        }

        return false;
    }

    private static string RemoveRepeatedLines(
        string text,
        int pageNumber,
        int pageCount,
        ISet<string> repeated,
        ISet<string> repeatedPatterns)
    {
        var sourceText = text ?? string.Empty;
        var sourceLines = sourceText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var lines = new List<(string Text, int EmptyLinesBefore)>();
        var emptyLinesBeforeNextLine = 0;
        foreach (var sourceLine in sourceLines)
        {
            var line = sourceLine.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0)
                    emptyLinesBeforeNextLine++;
                continue;
            }

            lines.Add((line, emptyLinesBeforeNextLine));
            emptyLinesBeforeNextLine = 0;
        }

        if (lines.Count <= 1)
        {
            var normalized = NormalizeBoilerplateLine(sourceText);
            return IsRepeatedBoilerplateLine(normalized, repeated, repeatedPatterns) ? string.Empty : sourceText;
        }

        var lineTexts = lines.Select(static line => line.Text).ToArray();
        var builder = new StringBuilder();
        var pendingEmptyLines = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var normalized = NormalizeBoilerplateLine(line.Text);
            var remove = IsRepeatedBoilerplateLine(normalized, repeated, repeatedPatterns)
                         || IsLocalPageMarkerLine(normalized, pageNumber, pageCount, index, lines.Count)
                         || IsFloatingStandaloneNumericMarkerLine(normalized, index, lineTexts);
            if (remove)
            {
                pendingEmptyLines = Math.Max(pendingEmptyLines, line.EmptyLinesBefore);
                continue;
            }

            if (builder.Length > 0)
            {
                var emptyLines = Math.Max(line.EmptyLinesBefore, pendingEmptyLines);
                builder.Append(emptyLines >= 2 ? "\n\n\n" : emptyLines == 1 ? "\n\n" : "\n");
            }
            builder.Append(line.Text);
            pendingEmptyLines = 0;
        }

        return builder.ToString().Trim();
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

    private static bool IsFloatingStandaloneNumericMarkerLine(string line, int index, IReadOnlyList<string> lines)
    {
        if (lines.Count < 5 || index <= 0 || index >= lines.Count - 1)
            return false;

        var normalized = NormalizeBoilerplateLine(line)
            .Trim('-', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', ' ');
        if (normalized.Length is < 2 or > 4)
            return false;
        if (!normalized.All(char.IsDigit))
            return false;

        var previous = NormalizeBoilerplateLine(lines[index - 1]);
        var next = NormalizeBoilerplateLine(lines[index + 1]);
        if (!LooksLikeProseOrInstructionLine(previous) || !LooksLikeProseOrInstructionLine(next))
            return false;

        return true;
    }

    private static bool LooksLikeProseOrInstructionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 8)
            return false;

        var letterCount = line.Count(char.IsLetter);
        if (letterCount < 6)
            return false;

        var tokenCount = SplitWords(line).Count();
        return tokenCount >= 3;
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

internal sealed class PdfLayoutLine(List<PdfLayoutWord> words)
{
    public List<PdfLayoutWord> Words { get; } = words;
    public double Left => Words.Count == 0 ? 0 : Words.Min(static word => word.Left);
    public double Top => Words.Count == 0 ? 0 : Words.Max(static word => word.Top);
    public double Bottom => Words.Count == 0 ? 0 : Words.Min(static word => word.Bottom);
    public double CenterY => Words.Count == 0 ? 0 : Words.Average(static word => word.CenterY);
}

internal readonly record struct PdfLayoutSegment(
    int LineIndex,
    string Text,
    double Left,
    double Right,
    double Top,
    double Bottom);

internal sealed record ExtractedPdfImageRegion(
    int SourceIndex,
    double Left,
    double Right,
    double Top,
    double Bottom,
    int WidthInSamples,
    int HeightInSamples)
{
    public bool HasUsableGeometry =>
        SourceIndex >= 0
        && double.IsFinite(Left)
        && double.IsFinite(Right)
        && double.IsFinite(Top)
        && double.IsFinite(Bottom)
        && Math.Abs(Right - Left) > double.Epsilon
        && Math.Abs(Top - Bottom) > double.Epsilon;
}

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
    IReadOnlyList<ExtractedPdfImageRegion>? NativeImageRegions = null);

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
