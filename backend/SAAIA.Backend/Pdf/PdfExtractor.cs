using System.Threading;
using System.Security.Cryptography;
using System.Text;
using PdfPig = UglyToad.PdfPig;

static partial class PdfExtractor
{
    public static PdfExtractionResult Extract(string pdfPath)
        => Extract(pdfPath, CancellationToken.None);

    public static PdfExtractionResult Extract(string pdfPath, CancellationToken ct)
        => Extract(pdfPath, ct, observeNativeVectors: false);

    public static PdfExtractionResult Extract(string pdfPath, CancellationToken ct, bool observeNativeVectors)
        => Extract(pdfPath, ct, observeNativeVectors, observeNativeTypography: false);

    public static PdfExtractionResult Extract(string pdfPath, CancellationToken ct, bool observeNativeVectors, bool observeNativeTypography)
    {
        using var source = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Extract(source, ct, observeNativeVectors, observeNativeTypography);
    }

    internal static PdfExtractionResult Extract(Stream source, CancellationToken ct)
        => Extract(source, ct, observeNativeVectors: false);

    internal static PdfExtractionResult Extract(Stream source, CancellationToken ct, bool observeNativeVectors)
        => Extract(source, ct, observeNativeVectors, observeNativeTypography: false);

    internal static PdfExtractionResult Extract(Stream source, CancellationToken ct, bool observeNativeVectors, bool observeNativeTypography)
    {
        ct.ThrowIfCancellationRequested();
        var sourceSha = PdfNativeWordExtractor.HashOpenedSource(source, ct);
        source.Position = 0;
        var tokens = new List<WordToken>();
        var pages = new List<ExtractedPdfPage>();
        var rawPages = new List<(int PageNumber, string Text, int ImageCount)>();
        var replacementStatsByPage = new Dictionary<int, (int RawReplacementCharCount, int SanitizedReplacementCharCount)>();
        var sourceTextByPage = new Dictionary<int, string>();
        var pageSizeByPage = new Dictionary<int, (double WidthPoints, double HeightPoints)>();
        var nativeLayoutByPage = new Dictionary<int, PdfNativeLayoutExtraction>();
        var nativeImagesByPage = new Dictionary<int, IReadOnlyList<ExtractedPdfImageRegion>>();
        var nativeWordsByPage = new Dictionary<int, PdfNativeWordObservation>();
        var nativeVectorsByPage = new Dictionary<int, PdfNativeVectorObservation>();
        var nativeTypographyByPage = new Dictionary<int, PdfNativeTypographyObservation>();
        var typographyBudget = new PdfNativeTypographyBudget();
        var remainingNativeVectorCommands = PdfNativeVectorGeometryObserver.MaximumCommandsPerDocument;
        var remainingNativeWords = PdfNativeWordExtractor.MaximumWordsPerDocument;

        using var doc = PdfPig.PdfDocument.Open(source);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var sourceText = page.Text ?? string.Empty;
            var rawText = ExtractLayoutAwarePageText(page);
            var text = OcrNoiseFilter.RemoveSpacedLetterRunNoise(PdfTextSanitizer.ForStorage(rawText));
            var nativeLayout = ExtractNativeLayout(page);
            var nativeImages = ExtractPageImages(page);
            var nativeWords = PdfNativeWordExtractor.Extract(page, sourceSha, remainingNativeWords, ct,
                sourceStructure: doc.Structure);
            remainingNativeWords -= Math.Min(remainingNativeWords, nativeWords.InspectedWordCount);
            nativeWordsByPage[page.Number] = nativeWords;
            if (observeNativeTypography)
                nativeTypographyByPage[page.Number] = PdfNativeTypographyObserver.Observe(page, doc.Structure, sourceSha, nativeWords, typographyBudget, ct);
            if (observeNativeVectors)
            {
                var vector = PdfNativeVectorGeometryObserver.Observe(page, doc.Structure, sourceSha, remainingNativeVectorCommands, ct);
                remainingNativeVectorCommands -= Math.Min(remainingNativeVectorCommands, vector.InspectedCommandCount);
                nativeVectorsByPage[page.Number] = vector;
            }
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
                    : Array.Empty<ExtractedPdfImageRegion>(),
                NativeWordObservation: nativeWordsByPage[rawPage.PageNumber],
                NativeVectorObservation: nativeVectorsByPage.GetValueOrDefault(rawPage.PageNumber),
                NativeTypographyObservation: nativeTypographyByPage.GetValueOrDefault(rawPage.PageNumber)));
        }

        var afterSha = PdfNativeWordExtractor.HashOpenedSource(source, ct);
        if (!string.Equals(sourceSha, afterSha, StringComparison.Ordinal))
            throw new InvalidDataException("native_pdf_source_changed_on_open_handle");
        foreach (var observation in nativeTypographyByPage.Values)
            observation.Observation.SourceHandleSha256After = afterSha;
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
                .Select(ToLayoutWord)
                .ToArray();
            try
            {
                var completeWords = page.GetWords(
                        UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance)
                    .Select(ToLayoutWord)
                    .ToArray();
                words = SelectCompleteLayoutWords(words, completeWords, fallback);
            }
            catch
            {
                // The original geometry and source text remain available when
                // the optional word segmenter cannot establish a complete page.
            }
            return BuildLayoutAwareText(words, fallback, page.Width, page.Height);
        }
        catch
        {
            return fallback;
        }
    }

    private static PdfLayoutWord ToLayoutWord(UglyToad.PdfPig.Content.Word word)
    {
        var first = word.Letters.FirstOrDefault(static letter => !string.IsNullOrWhiteSpace(letter.Value));
        return new(word.Text, word.BoundingBox.Left, word.BoundingBox.Right,
            word.BoundingBox.Top, word.BoundingBox.Bottom,
            first?.StartBaseLine.X, first?.StartBaseLine.Y,
            first?.EndBaseLine.X, first?.EndBaseLine.Y);
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

    internal static PdfLayoutWord[] SelectCompleteLayoutWords(
        IReadOnlyList<PdfLayoutWord> original, IReadOnlyList<PdfLayoutWord> alternative, string sourceText)
    {
        var source = original.Where(static word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        var candidate = alternative.Where(static word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        if (candidate.Length == 0 || candidate.Any(static word => !HasUsableLayoutGeometry(word))
            || !NonWhitespaceCharacters(source).SequenceEqual(NonWhitespaceCharacters(candidate))
            || !PreservesRawLayoutCharacters(candidate, sourceText))
            return source;

        // Inclined native text can be split into individual letters by the
        // default extractor. Prefer assembled words only after proving that
        // every original character, including case, digits and punctuation,
        // occurs exactly as often in the alternative segmentation.
        return candidate.Count(IsSingleLetterLayoutWord) < source.Count(IsSingleLetterLayoutWord)
            ? candidate : source;
    }

    private static bool IsSingleLetterLayoutWord(PdfLayoutWord word)
        => word.Text.Trim() is { Length: 1 } text && char.IsLetter(text[0]);

    private static IEnumerable<char> NonWhitespaceCharacters(IEnumerable<PdfLayoutWord> words)
        => words.SelectMany(static word => word.Text).Where(static ch => !char.IsWhiteSpace(ch)).Order();

    private static bool PreservesRawLayoutCharacters(IEnumerable<PdfLayoutWord> words, string sourceText)
        => NonWhitespaceCharacters(words)
            .SequenceEqual(sourceText.Where(static ch => !char.IsWhiteSpace(ch)).Order());

    private static bool CoversRawLayoutCharacters(IEnumerable<PdfLayoutWord> words, string sourceText)
    {
        var remaining = words.SelectMany(static word => word.Text).Where(static ch => !char.IsWhiteSpace(ch))
            .GroupBy(static ch => ch).ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var ch in sourceText.Where(static ch => !char.IsWhiteSpace(ch)))
        {
            if (!remaining.TryGetValue(ch, out var count) || count == 0)
                return false;
            remaining[ch] = count - 1;
        }
        return true;
    }

    private static bool HasUsableLayoutGeometry(PdfLayoutWord word)
        => IsFinite(word.Left) && IsFinite(word.Right) && IsFinite(word.Top) && IsFinite(word.Bottom)
            && word.Right >= word.Left && word.Top >= word.Bottom;

    private static bool PreservesLayoutTokens(IEnumerable<PdfLayoutWord> words, string text)
        => words.SelectMany(static word => SplitWords(PdfTextSanitizer.ForStorage(word.Text)))
            .Order(StringComparer.Ordinal).SequenceEqual(SplitWords(text).Order(StringComparer.Ordinal));

    internal static string BuildLayoutAwareText(IReadOnlyList<PdfLayoutWord> words, string? fallback)
        => BuildLayoutAwareText(words, fallback, null, null);

    internal static string BuildLayoutAwareText(IReadOnlyList<PdfLayoutWord> words, string? fallback,
        double? pageWidth, double? pageHeight)
    {
        fallback ??= string.Empty;
        var usableWords = words
            .Where(static word =>
                !string.IsNullOrWhiteSpace(word.Text)
                && HasUsableLayoutGeometry(word))
            .ToArray();
        if (usableWords.Length == 0)
            return fallback;

        if (usableWords.Length != words.Count(static word => !string.IsNullOrWhiteSpace(word.Text)))
            return fallback;

        // Raw aggregation may itself expose fewer characters than the word
        // objects. Preserve all observed words, but never omit a raw source fact.
        if (!string.IsNullOrWhiteSpace(fallback) && !CoversRawLayoutCharacters(usableWords, fallback))
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
            wrapBackThreshold, pageWidth, pageHeight).Trim();
        if (reconstructed.Length == 0)
            return fallback;

        if (!PreservesLayoutTokens(usableWords, reconstructed))
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
        double wrapBackThreshold,
        double? pageWidth,
        double? pageHeight)
    {
        var lines = BuildLayoutLines(words, sameLineTolerance);
        if (lines.Count == 0)
            return string.Empty;

        var segments = lines
            .SelectMany((line, index) => SplitLineIntoSegments(line, index, medianWidth))
            .ToArray();

        if (TryBuildParentRegionText(segments, paragraphGapThreshold, medianWidth,
                pageWidth, pageHeight, out var parentRegions)) return parentRegions;

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
            var line = lines.LastOrDefault(candidate => BelongsToLayoutLine(candidate, word, sameLineTolerance));
            if (line is null)
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

    private static bool BelongsToLayoutLine(PdfLayoutLine line, PdfLayoutWord word, double tolerance)
    {
        if (TryGetLayoutBaseline(word, out var x, out var y, out var dx, out var dy))
        {
            foreach (var reference in line.Words)
            {
                if (!TryGetLayoutBaseline(reference, out var rx, out var ry, out var rdx, out var rdy))
                    continue;
                // Equal baselines remain a line when glyph bounding boxes rise
                // along an inclined sentence. Different orientations do not mix.
                return Math.Abs(dx * rdy - dy * rdx) <= 0.001d
                    && dx * rdx + dy * rdy > 0d
                    && Math.Abs((x - rx) * rdy - (y - ry) * rdx) <= tolerance;
            }
        }
        return Math.Abs(word.CenterY - line.CenterY) <= tolerance;
    }

    private static bool TryGetLayoutBaseline(PdfLayoutWord word,
        out double x, out double y, out double dx, out double dy)
    {
        x = y = dx = dy = 0d;
        if (word.BaselineStartX is not { } sx || word.BaselineStartY is not { } sy
            || word.BaselineEndX is not { } ex || word.BaselineEndY is not { } ey
            || !IsFinite(sx) || !IsFinite(sy) || !IsFinite(ex) || !IsFinite(ey))
            return false;
        var length = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
        if (!IsFinite(length) || length <= double.Epsilon)
            return false;
        x = sx;
        y = sy;
        dx = (ex - sx) / length;
        dy = (ey - sy) / length;
        return true;
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
        // Three observed shared rows establish concurrent columns. A long
        // neighboring paragraph must not dilute that local geometric evidence.
        if (multiSegmentLineCount < 3)
            return false;

        // A spanning title must not widen a column's envelope until it
        // absorbs the adjacent body. Stable left anchors establish columns;
        // small regions still participate in the emitted reading order.
        var columns = ClusterSegmentsByLeftAnchor(segments, Math.Clamp(medianWidth * 3.5d, 45.0d, 110.0d))
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();
        var stableColumns = columns.Where(static column => column.Count >= 3).ToArray();
        if (stableColumns.Length < 2)
            return false;

        var columnSegmentCount = stableColumns.Sum(static column => column.Count);
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
        out string text,
        bool parentRegionEstablished = false)
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
        // A separately established physical parent already proves its region;
        // long technical identifiers must not require six word-widths inside
        // it. The original unparented heuristic retains its stronger gate.
        if (stableRight - stableLeft < Math.Max(120.0d, parentRegionEstablished ? 0d : medianWidth * 6.0d))
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
            .OrderBy(static column => column.Min(static segment => segment.Left))
            .ToArray();
        var stableColumns = columns.Where(static column => column.Count >= 2).ToArray();
        if (stableColumns.Length < 2)
            return false;

        var coveredSegmentCount = stableColumns.Sum(static column => column.Count);
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

        var normalized = NormalizeLayoutLine(text);
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

        // Text and physical page identity cannot establish that a repeated
        // fact, warning, heading or caption is useless. Keep all such content,
        // regardless of its numeric form. Only whole-line explicit counters
        // matching the physical page can be removed with this information.
        var pageCount = pages.Max(static page => page.PageNumber);
        return pages
            .Select(page => (page.PageNumber, Text: RemoveEstablishedPageCounterLines(page.Text, page.PageNumber, pageCount), page.ImageCount))
            .ToArray();
    }

    private static string RemoveEstablishedPageCounterLines(
        string text,
        int pageNumber,
        int pageCount)
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
            return sourceText;

        var builder = new StringBuilder();
        var pendingEmptyLines = 0;
        var removedCounter = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var normalized = NormalizeLayoutLine(line.Text);
            var remove = IsEstablishedPageCounterLine(normalized, pageNumber, pageCount, index, lines.Count);
            if (remove)
            {
                removedCounter = true;
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

        return removedCounter ? builder.ToString().Trim() : sourceText;
    }

    private static string NormalizeLayoutLine(string line)
        => System.Text.RegularExpressions.Regex.Replace(line ?? string.Empty, @"\s+", " ").Trim();

    private static bool IsEstablishedPageCounterLine(string line, int pageNumber, int pageCount, int index, int lineCount)
    {
        if (lineCount <= 1 || index > 1 && index < lineCount - 2)
            return false;

        var normalized = NormalizeLayoutLine(line)
            .Trim('-', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014', ' ')
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var pagePattern = pageCount > 0
            ? $@"^(?:page|p\.)\s*0*{pageNumber}\s*(?:/|of|sur|de)\s*0*{pageCount}$"
            : $@"^(?:page|p\.)\s*0*{pageNumber}$";
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
    double Bottom,
    double? BaselineStartX = null,
    double? BaselineStartY = null,
    double? BaselineEndX = null,
    double? BaselineEndY = null)
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
