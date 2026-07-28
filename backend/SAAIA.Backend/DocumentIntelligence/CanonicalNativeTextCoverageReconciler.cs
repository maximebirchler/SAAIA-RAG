using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record NativeTextCoverageReconciliationSummary(
    bool Enabled,
    int NativePageCount,
    int AuditedPageCount,
    int CandidateLineCount,
    int RecoveredLineCount,
    int RecoveredBlockCount,
    int RecoveredCharacterCount,
    int SkippedLowQualityPageCount,
    double MinimumLineCoverage,
    long DurationMs,
    string EngineVersion,
    int CandidateLayoutBlockCount = 0,
    int RecoveredLayoutBlockCount = 0,
    int RecoveredLayoutPageCount = 0,
    int RecoveredLayoutCharacterCount = 0);

internal static partial class CanonicalNativeTextCoverageReconciler
{
    internal const string StageId = "native_text_coverage_reconciliation";
    internal const string RecoveryBlockType = "native_text_recovery";
    internal const string RecoveryQualityFlag =
        "canonical_native_text_gap_recovered";
    internal const string PageOnlyAnchorQualityFlag =
        "spatial_anchor_page_only";

    private sealed record NativeLine(
        int SourceLineIndex,
        string Text,
        IReadOnlyList<string> Tokens,
        double OrderedCanonicalCoverage,
        double BagCanonicalCoverage,
        double CanonicalCoverage);

    public static NativeTextCoverageReconciliationSummary Apply(
        CanonicalDocument document,
        PdfExtractionResult? nativeExtraction,
        bool enabled = true,
        double minimumLineCoverage = 0.90)
    {
        ArgumentNullException.ThrowIfNull(document);
        minimumLineCoverage = Math.Clamp(
            minimumLineCoverage,
            0.50,
            1.0);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var engineVersion = ResolvePdfPigVersion();
        if (!enabled || nativeExtraction is null)
        {
            started.Stop();
            return new(
                enabled,
                nativeExtraction?.Pages.Count ?? 0,
                0,
                0,
                0,
                0,
                0,
                0,
                minimumLineCoverage,
                started.ElapsedMilliseconds,
                engineVersion,
                0,
                0,
                0,
                0);
        }

        var canonicalPages = document.Pages.ToDictionary(
            static page => page.PageNumber);
        var auditedPages = 0;
        var candidateLines = 0;
        var recoveredLines = 0;
        var recoveredBlocks = 0;
        var recoveredCharacters = 0;
        var skippedLowQualityPages = 0;
        var candidateLayoutBlocks = 0;
        var recoveredLayoutBlocks = 0;
        var recoveredLayoutPages = 0;
        var recoveredLayoutCharacters = 0;

        foreach (var nativePage in nativeExtraction.Pages
                     .OrderBy(static page => page.PageNumber))
        {
            if (!canonicalPages.TryGetValue(
                    nativePage.PageNumber,
                    out var canonicalPage))
            {
                continue;
            }

            if (!IsReliableNativeTextPage(nativePage))
            {
                skippedLowQualityPages++;
                continue;
            }

            auditedPages++;
            var navigationHint = PageContainsNavigationStructure(
                canonicalPage);
            var canonicalSegments = BuildCanonicalSegments(canonicalPage);
            var canonicalPageTokens =
                BuildCanonicalPageTokens(canonicalPage);
            var lines = BuildNativeLines(
                nativePage.Text,
                canonicalSegments,
                canonicalPageTokens);
            candidateLines += lines.Count;
            var recoverable = lines
                .Where(line =>
                    line.CanonicalCoverage < minimumLineCoverage
                    && IsUsefulRecoveryLine(
                        line.Text,
                        line.Tokens,
                        navigationHint))
                .ToArray();
            if (recoverable.Length > 0)
            {
                recoverable = ExpandWithPartialContext(
                    lines,
                    recoverable,
                    minimumLineCoverage);

                foreach (var group in GroupAdjacentLines(recoverable))
                {
                    var text = string.Join(
                        Environment.NewLine,
                        group.Select(static line => line.Text));
                    text = CollapseWhitespacePerLine(text);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var firstLine = group[0].SourceLineIndex;
                    var lastLine = group[^1].SourceLineIndex;
                    var blockId = CanonicalStableId.Create(
                        "block",
                        document.Source.Sha256,
                        RecoveryBlockType,
                        nativePage.PageNumber.ToString(
                            CultureInfo.InvariantCulture),
                        firstLine.ToString(CultureInfo.InvariantCulture),
                        lastLine.ToString(CultureInfo.InvariantCulture),
                        Sha256(text));
                    var ordinal = canonicalPage.Blocks.Count == 0
                        ? 0
                        : canonicalPage.Blocks.Max(
                            static block => block.Ordinal) + 1;
                    var readingOrder = canonicalPage.Blocks.Count == 0
                        ? 0
                        : canonicalPage.Blocks.Max(
                            static block => block.ReadingOrder) + 1;
                    canonicalPage.Blocks.Add(new()
                    {
                        BlockId = blockId,
                        BlockType = RecoveryBlockType,
                        Ordinal = ordinal,
                        ReadingOrder = readingOrder,
                        Text = BuildTextVariants(text),
                        QualityFlags =
                        [
                            RecoveryQualityFlag,
                            PageOnlyAnchorQualityFlag
                        ],
                        Provenance = new()
                        {
                            StageId = StageId,
                            Method = "native_text_coverage_reconciliation",
                            Engine = "PdfPig",
                            EngineVersion = engineVersion,
                            Attributes = new(StringComparer.Ordinal)
                            {
                                ["sourceType"] = "native_pdf_text_layer",
                                ["sourcePage"] = nativePage.PageNumber.ToString(
                                    CultureInfo.InvariantCulture),
                                ["sourceLineStart"] = firstLine.ToString(
                                    CultureInfo.InvariantCulture),
                                ["sourceLineEnd"] = lastLine.ToString(
                                    CultureInfo.InvariantCulture),
                                ["coverageAlgorithm"] = "line_lcs_v1",
                                ["maximumCanonicalCoverage"] = group
                                    .Max(static line =>
                                        line.CanonicalCoverage)
                                    .ToString(
                                        "0.######",
                                        CultureInfo.InvariantCulture),
                                ["minimumCanonicalCoverage"] = group
                                    .Min(static line =>
                                        line.CanonicalCoverage)
                                    .ToString(
                                        "0.######",
                                        CultureInfo.InvariantCulture),
                                ["minimumOrderedCanonicalCoverage"] = group
                                    .Min(static line =>
                                        line.OrderedCanonicalCoverage)
                                    .ToString(
                                        "0.######",
                                        CultureInfo.InvariantCulture),
                                ["contentRoleHint"] = navigationHint
                                    ? RetrievalContentClassifier.NavigationRole
                                    : RetrievalContentClassifier.ContentRole,
                                ["semanticDecisionOwner"] = "llm_client"
                            }
                        }
                    });
                    AddDistinct(
                        canonicalPage.QualityFlags,
                        RecoveryQualityFlag);
                    recoveredLines += group.Count;
                    recoveredBlocks++;
                    recoveredCharacters += text.Length;
                }
            }

            var layoutReconciliation =
                CanonicalNativeLayoutReconciler.Apply(
                    document,
                    canonicalPage,
                    nativePage,
                    navigationHint,
                    engineVersion);
            candidateLayoutBlocks +=
                layoutReconciliation.CandidateBlockCount;
            recoveredLayoutBlocks +=
                layoutReconciliation.RecoveredBlockCount;
            recoveredLayoutCharacters +=
                layoutReconciliation.RecoveredCharacterCount;
            if (layoutReconciliation.Applied)
                recoveredLayoutPages++;
        }

        CanonicalContractValidator.ValidateOrThrow(document);
        started.Stop();
        return new(
            true,
            nativeExtraction.Pages.Count,
            auditedPages,
            candidateLines,
            recoveredLines,
            recoveredBlocks,
            recoveredCharacters,
            skippedLowQualityPages,
            minimumLineCoverage,
            started.ElapsedMilliseconds,
            engineVersion,
            candidateLayoutBlocks,
            recoveredLayoutBlocks,
            recoveredLayoutPages,
            recoveredLayoutCharacters);
    }

    private static bool IsReliableNativeTextPage(
        ExtractedPdfPage page)
    {
        if (string.IsNullOrWhiteSpace(page.Text))
            return false;
        if (page.Quality is null)
            return page.WordCount >= 2 && page.CharCount >= 8;

        return !page.Quality.TextEmpty
               && page.Quality.SanitizedReplacementCharCount == 0
               && !page.Quality.Signals.Contains(
                   "replacement_chars_remaining",
                   StringComparer.Ordinal)
               && !page.Quality.Signals.Contains(
                   "invalid_control_chars_detected",
                   StringComparer.Ordinal);
    }

    private static IReadOnlyList<IReadOnlyList<string>>
        BuildCanonicalSegments(CanonicalPage page)
    {
        var segments = new List<IReadOnlyList<string>>();
        var orderedBlockTexts = page.Blocks
            .OrderBy(static block => block.ReadingOrder)
            .ThenBy(static block => block.Ordinal)
            .Select(static block => block.Text.Canonical)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        AddSegments(segments, orderedBlockTexts);
        AddAdjacentWindows(segments, orderedBlockTexts, 2);
        AddAdjacentWindows(segments, orderedBlockTexts, 3);

        foreach (var table in page.Tables
                     .OrderBy(static table => table.Ordinal))
        {
            for (var rowIndex = 0;
                 rowIndex < table.RowCount;
                 rowIndex++)
            {
                var values = table.Cells
                    .Where(cell => cell.RowIndex == rowIndex)
                    .OrderBy(static cell => cell.ColumnIndex)
                    .ThenBy(static cell => cell.CellId, StringComparer.Ordinal)
                    .Select(static cell => cell.Text.Canonical)
                    .Where(static text =>
                        !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                AddSegments(segments, values);
                if (values.Length > 1)
                    AddSegment(segments, string.Join(' ', values));
            }
        }

        return segments;
    }

    private static void AddAdjacentWindows(
        ICollection<IReadOnlyList<string>> segments,
        IReadOnlyList<string> values,
        int windowSize)
    {
        if (windowSize <= 1 || values.Count < windowSize)
            return;

        for (var index = 0;
             index <= values.Count - windowSize;
             index++)
        {
            AddSegment(
                segments,
                string.Join(
                    ' ',
                    values.Skip(index).Take(windowSize)));
        }
    }

    private static void AddSegments(
        ICollection<IReadOnlyList<string>> segments,
        IEnumerable<string> values)
    {
        foreach (var value in values)
            AddSegment(segments, value);
    }

    private static void AddSegment(
        ICollection<IReadOnlyList<string>> segments,
        string value)
    {
        var tokens = Tokenize(value);
        if (tokens.Count > 0)
            segments.Add(tokens);
    }

    private static IReadOnlyList<NativeLine> BuildNativeLines(
        string text,
        IReadOnlyList<IReadOnlyList<string>> canonicalSegments,
        IReadOnlyList<string> canonicalPageTokens)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var projected = new List<NativeLine>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = CollapseWhitespace(
                OcrNoiseFilter.RemoveSpacedLetterRunNoise(
                    lines[index]));
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;
            var orderedCoverage =
                ComputeMaximumOrderedCoverage(
                    tokens,
                    canonicalSegments);
            var bagCoverage = ComputeBagCoverage(
                tokens,
                canonicalPageTokens,
                EndsWithWordContinuationHyphen(line));
            projected.Add(new(
                index,
                line,
                tokens,
                orderedCoverage,
                bagCoverage,
                Math.Max(orderedCoverage, bagCoverage)));
        }

        return projected;
    }

    private static double ComputeMaximumOrderedCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<IReadOnlyList<string>> candidates)
    {
        if (source.Count == 0 || candidates.Count == 0)
            return 0.0;

        var maximum = 0.0;
        foreach (var candidate in candidates)
        {
            var coverage = ComputeOrderedCoverage(source, candidate);
            if (coverage > maximum)
                maximum = coverage;
            if (maximum >= 1.0)
                return 1.0;
        }

        return maximum;
    }

    private static IReadOnlyList<string> BuildCanonicalPageTokens(
        CanonicalPage page)
        => page.Blocks
            .SelectMany(static block =>
                Tokenize(block.Text.Canonical))
            .Concat(page.Tables
                .OrderBy(static table => table.Ordinal)
                .SelectMany(static table => table.Cells
                    .OrderBy(static cell => cell.RowIndex)
                    .ThenBy(static cell => cell.ColumnIndex))
                .SelectMany(static cell =>
                    Tokenize(cell.Text.Canonical)))
            .ToArray();

    private static double ComputeBagCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate,
        bool allowTerminalPrefixMatch = false)
    {
        if (source.Count == 0 || candidate.Count == 0)
            return 0.0;

        var remaining = candidate
            .GroupBy(static token => token, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var matched = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var token = source[index];
            if (TryConsume(remaining, token))
            {
                matched++;
                continue;
            }

            if (allowTerminalPrefixMatch
                && index == source.Count - 1
                && token.Length >= 3
                && TryConsumeByPrefix(remaining, token))
            {
                matched++;
                continue;
            }

            if (token.Length != 1
                || index + 1 >= source.Count)
            {
                continue;
            }

            var merged = token + source[index + 1];
            if (!TryConsume(remaining, merged))
                continue;

            matched += 2;
            index++;
        }

        return matched / (double)source.Count;
    }

    private static bool TryConsumeByPrefix(
        IDictionary<string, int> counts,
        string prefix)
    {
        var match = counts
            .Where(pair =>
                pair.Value > 0
                && pair.Key.Length > prefix.Length
                && pair.Key.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
            .OrderBy(static pair => pair.Key.Length)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key)
            .FirstOrDefault();
        if (match is null)
            return false;

        counts[match]--;
        return true;
    }

    private static bool TryConsume(
        IDictionary<string, int> counts,
        string token)
    {
        if (!counts.TryGetValue(token, out var count)
            || count <= 0)
        {
            return false;
        }

        counts[token] = count - 1;
        return true;
    }

    private static double ComputeOrderedCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate)
    {
        if (source.Count == 0 || candidate.Count == 0)
            return 0.0;

        var previous = new int[candidate.Count + 1];
        var current = new int[candidate.Count + 1];
        for (var sourceIndex = 1;
             sourceIndex <= source.Count;
             sourceIndex++)
        {
            for (var candidateIndex = 1;
                 candidateIndex <= candidate.Count;
                 candidateIndex++)
            {
                current[candidateIndex] = string.Equals(
                        source[sourceIndex - 1],
                        candidate[candidateIndex - 1],
                        StringComparison.Ordinal)
                    ? previous[candidateIndex - 1] + 1
                    : Math.Max(
                        previous[candidateIndex],
                        current[candidateIndex - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[candidate.Count]
               / (double)source.Count;
    }

    private static bool IsUsefulRecoveryLine(
        string text,
        IReadOnlyList<string> tokens,
        bool navigationHint)
    {
        if (text.Length < 6 || tokens.Count < 2)
            return false;
        if (tokens.All(static token =>
                token.All(char.IsDigit)))
        {
            return false;
        }

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(text)
            && !(navigationHint
                 && ExplicitNavigationLeaderBeforePageRegex().IsMatch(text)))
        {
            return false;
        }

        return tokens.Any(static token =>
            token.Length >= 3
            && token.Any(char.IsLetter));
    }

    private static bool EndsWithWordContinuationHyphen(string value)
    {
        var trimmed = value.TrimEnd();
        return trimmed.EndsWith(
                   "-",
                   StringComparison.Ordinal)
               || trimmed.EndsWith(
                   "\u2010",
                   StringComparison.Ordinal)
               || trimmed.EndsWith(
                   "\u2011",
                   StringComparison.Ordinal);
    }

    private static IReadOnlyList<IReadOnlyList<NativeLine>>
        GroupAdjacentLines(IReadOnlyList<NativeLine> lines)
    {
        var groups = new List<IReadOnlyList<NativeLine>>();
        var current = new List<NativeLine>();
        foreach (var line in lines.OrderBy(
                     static line => line.SourceLineIndex))
        {
            if (current.Count > 0
                && line.SourceLineIndex
                != current[^1].SourceLineIndex + 1)
            {
                groups.Add(current.ToArray());
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0)
            groups.Add(current.ToArray());
        return groups;
    }

    private static NativeLine[] ExpandWithPartialContext(
        IReadOnlyList<NativeLine> allLines,
        IReadOnlyList<NativeLine> recoverable,
        double minimumLineCoverage)
    {
        var bySourceLine = allLines.ToDictionary(
            static line => line.SourceLineIndex);
        var selected = recoverable.ToDictionary(
            static line => line.SourceLineIndex);
        foreach (var line in recoverable)
        {
            if (!bySourceLine.TryGetValue(
                    line.SourceLineIndex - 1,
                    out var previous))
            {
                continue;
            }

            if (previous.OrderedCanonicalCoverage
                    >= minimumLineCoverage
                || previous.OrderedCanonicalCoverage
                    < Math.Min(0.70, minimumLineCoverage)
                || previous.CanonicalCoverage
                    < minimumLineCoverage)
            {
                continue;
            }

            selected.TryAdd(
                previous.SourceLineIndex,
                previous);
        }

        return selected.Values
            .OrderBy(static line => line.SourceLineIndex)
            .ToArray();
    }

    private static bool PageContainsNavigationStructure(
        CanonicalPage page)
        => page.Tables.Any(table =>
            table.Provenance.Attributes.TryGetValue(
                "doclingLabel",
                out var label)
            && string.Equals(
                label,
                "document_index",
                StringComparison.OrdinalIgnoreCase));

    private static CanonicalTextVariants BuildTextVariants(
        string text)
        => new()
        {
            Raw = text,
            Canonical = text,
            Normalized = CollapseWhitespace(
                    text.Normalize(NormalizationForm.FormKC))
                .ToLowerInvariant(),
            Retrieval = text,
            Display = text,
            RawSha256 = Sha256(text)
        };

    private static IReadOnlyList<string> Tokenize(string value)
        => CoverageTokenRegex()
            .Matches(
                value.Normalize(NormalizationForm.FormKC)
                    .ToLowerInvariant())
            .Select(static match => match.Value)
            .ToArray();

    private static string CollapseWhitespace(string value)
        => WhitespaceRegex().Replace(value, " ").Trim();

    private static string CollapseWhitespacePerLine(string value)
        => string.Join(
            Environment.NewLine,
            value.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Select(CollapseWhitespace)
                .Where(static line =>
                    !string.IsNullOrWhiteSpace(line)));

    private static void AddDistinct(
        ICollection<string> values,
        string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
            values.Add(value);
    }

    private static string ResolvePdfPigVersion()
        => typeof(UglyToad.PdfPig.PdfDocument)
               .Assembly
               .GetName()
               .Version?
               .ToString()
           ?? "unknown";

    private static string Sha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex CoverageTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\.\u00b7\u2022]{2,}\s*\d{1,4}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNavigationLeaderBeforePageRegex();
}
