using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record NativeLayoutPageReconciliation(
    int CandidateBlockCount,
    int RecoveredBlockCount,
    int RecoveredCharacterCount,
    double OrderedAgreement,
    double BagAgreement,
    bool Applied,
    string DecisionReason);

internal static partial class CanonicalNativeLayoutReconciler
{
    internal const string RecoveryBlockType =
        "native_layout_recovery";
    internal const string RecoveryQualityFlag =
        "canonical_native_layout_alternative_recovered";
    internal const string PageQualityFlag =
        "canonical_native_layout_disagreement";
    internal const string UndersegmentedDecisionReason =
        "undersegmented_dominant_block";
    internal const double MaximumOrderedAgreement = 0.97;
    private const double MinimumBagAgreement = 0.85;
    private const double MinimumOrderDisagreement = 0.03;
    private const double MinimumTableOverlapToExclude = 0.35;
    private const double MinimumAverageTokensPerBlock = 2.3;
    private const double MaximumSingleTokenBlockRatio = 0.40;
    private const int MaximumUnfragmentedBlockCount = 160;
    private const int MinimumDominantBlockTokens = 320;
    private const double MinimumDominantBlockTokenRatio = 0.65;

    public static NativeLayoutPageReconciliation Apply(
        CanonicalDocument document,
        CanonicalPage page,
        ExtractedPdfPage nativePage,
        bool navigationHint,
        string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nativePage);

        if (nativePage.NativeLayoutBlocks is not { Count: > 0 }
            || nativePage.WidthPoints is not > 0
            || nativePage.HeightPoints is not > 0)
        {
            return Empty("native_layout_unavailable");
        }

        var tableBounds = page.Tables
            .Select(static table => ToBounds(table.Polygon))
            .Where(static bounds => bounds.HasValue)
            .Select(static bounds => bounds!.Value)
            .ToArray();
        var projected = nativePage.NativeLayoutBlocks
            .OrderBy(static block => block.ReadingOrder)
            .Select(block => new LayoutCandidate(
                block,
                Clean(block.Text),
                Tokenize(block.Text),
                ToNormalizedBounds(
                    block,
                    nativePage.WidthPoints.Value,
                    nativePage.HeightPoints.Value)))
            .Where(static candidate =>
                IsUseful(candidate.Text, candidate.Tokens))
            .ToArray();
        var outsideTable = projected
            .Where(candidate =>
                !tableBounds.Any(table =>
                    ComputeOverlapRatio(
                        candidate.Bounds,
                        table)
                    >= MinimumTableOverlapToExclude))
            .ToArray();
        var rawTokenCounts = nativePage.NativeLayoutBlocks
            .Select(static block => Tokenize(block.Text).Count)
            .Where(static count => count > 0)
            .ToArray();
        var fragmented = rawTokenCounts.Length
                             > MaximumUnfragmentedBlockCount
                         || rawTokenCounts.Length > 0
                         && rawTokenCounts.Average()
                         < MinimumAverageTokensPerBlock
                         || rawTokenCounts.Length > 0
                         && rawTokenCounts.Count(static count => count == 1)
                         / (double)rawTokenCounts.Length
                         > MaximumSingleTokenBlockRatio;
        if (fragmented && tableBounds.Length == 0)
            return Empty("fragmented_without_table");
        var projectedTokenCount = projected.Sum(
            static candidate => candidate.Tokens.Count);
        var outsideTableTokenRatio = outsideTable.Sum(
                static candidate => candidate.Tokens.Count)
            / (double)Math.Max(1, projectedTokenCount);
        var useOutsideTableOnly =
            tableBounds.Length > 0
            && outsideTable.Length >= 2
            && outsideTableTokenRatio >= 0.25
            || fragmented;
        var candidates = useOutsideTableOnly
            ? outsideTable
            : projected;
        if (candidates.Length < 2)
            return Empty(
                "insufficient_layout_candidates",
                candidates.Length);
        var candidateTokenCounts = candidates
            .Select(static candidate => candidate.Tokens.Count)
            .ToArray();
        var candidateTokenTotal = candidateTokenCounts.Sum();
        var dominantBlockTokenCount = candidateTokenCounts.Max();
        var dominantBlockTokenRatio = dominantBlockTokenCount
            / (double)Math.Max(1, candidateTokenTotal);
        if (dominantBlockTokenCount
                >= MinimumDominantBlockTokens
            && dominantBlockTokenRatio
                >= MinimumDominantBlockTokenRatio)
        {
            return Empty(
                UndersegmentedDecisionReason,
                candidates.Length);
        }

        var nativeTokens = candidates
            .SelectMany(static candidate => candidate.Tokens)
            .ToArray();
        var canonicalBlockTokens = page.Blocks
            .Where(static block =>
                !string.Equals(
                    block.BlockType,
                    CanonicalNativeTextCoverageReconciler
                        .RecoveryBlockType,
                    StringComparison.Ordinal)
                && !string.Equals(
                    block.BlockType,
                    RecoveryBlockType,
                    StringComparison.Ordinal))
            .Where(block =>
                !useOutsideTableOnly
                || !OverlapsAnyTable(
                    block.Polygon,
                    tableBounds))
            .OrderBy(static block => block.ReadingOrder)
            .ThenBy(static block => block.Ordinal)
            .SelectMany(static block =>
                Tokenize(block.Text.Canonical))
            .ToArray();
        var canonicalTokens = useOutsideTableOnly
            ? canonicalBlockTokens
            : canonicalBlockTokens
                .Concat(page.Tables
                    .OrderBy(static table => table.Ordinal)
                    .SelectMany(static table => table.Cells
                        .OrderBy(static cell => cell.RowIndex)
                        .ThenBy(static cell => cell.ColumnIndex)
                        .ThenBy(
                            static cell => cell.CellId,
                            StringComparer.Ordinal))
                    .SelectMany(static cell =>
                        Tokenize(cell.Text.Canonical)))
                .ToArray();
        if (canonicalTokens.Length == 0)
            return Empty(
                "canonical_tokens_unavailable",
                candidates.Length);

        var nativeFlatTokens = Tokenize(nativePage.Text);
        if (nativeFlatTokens.Count == 0)
            return Empty(
                "native_flat_tokens_unavailable",
                candidates.Length);
        var orderedAgreement = ComputeOrderedCoverage(
            nativeTokens,
            nativeFlatTokens);
        var bagAgreement = ComputeBagCoverage(
            nativeTokens,
            nativeFlatTokens);
        var canonicalOrderedAgreement =
            ComputeOrderedCoverage(
                nativeTokens,
                canonicalTokens);
        var canonicalBagAgreement = ComputeBagCoverage(
            nativeTokens,
            canonicalTokens);
        if (orderedAgreement >= MaximumOrderedAgreement
            || bagAgreement < MinimumBagAgreement
            || bagAgreement - orderedAgreement
            < MinimumOrderDisagreement
            || canonicalBagAgreement >= MinimumBagAgreement
            && canonicalOrderedAgreement
            >= MaximumOrderedAgreement)
        {
            return new(
                candidates.Length,
                0,
                0,
                orderedAgreement,
                bagAgreement,
                false,
                "no_material_order_advantage");
        }

        var nextOrdinal = page.Blocks.Count == 0
            ? 0
            : page.Blocks.Max(static block => block.Ordinal) + 1;
        var nextReadingOrder = page.Blocks.Count == 0
            ? 0
            : page.Blocks.Max(static block => block.ReadingOrder) + 1;
        var recoveredCharacters = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var blockId = CanonicalStableId.Create(
                "block",
                document.Source.Sha256,
                RecoveryBlockType,
                nativePage.PageNumber.ToString(
                    CultureInfo.InvariantCulture),
                candidate.Source.ReadingOrder.ToString(
                    CultureInfo.InvariantCulture),
                Sha256(candidate.Text));
            page.Blocks.Add(new()
            {
                BlockId = blockId,
                BlockType = RecoveryBlockType,
                Ordinal = nextOrdinal + index,
                ReadingOrder = nextReadingOrder + index,
                Polygon = ToCanonicalPolygon(candidate.Bounds),
                Text = BuildTextVariants(candidate.Text),
                QualityFlags =
                [
                    RecoveryQualityFlag,
                    "alternate_mechanical_reading_order"
                ],
                Provenance = new()
                {
                    StageId =
                        CanonicalNativeTextCoverageReconciler.StageId,
                    Method =
                        "native_layout_reading_order_reconciliation",
                    Engine = "PdfPig",
                    EngineVersion = engineVersion,
                    Attributes = new(StringComparer.Ordinal)
                    {
                        ["sourceType"] =
                            "native_pdf_text_geometry",
                        ["sourcePage"] = nativePage.PageNumber
                            .ToString(CultureInfo.InvariantCulture),
                        ["sourceReadingOrder"] = candidate.Source
                            .ReadingOrder.ToString(
                                CultureInfo.InvariantCulture),
                        ["layoutAlgorithm"] =
                            nativePage.NativeLayoutAlgorithm
                            ?? candidate.Source.Algorithm,
                        ["orderedAgreement"] =
                            orderedAgreement.ToString(
                                "0.######",
                                CultureInfo.InvariantCulture),
                        ["bagAgreement"] =
                            bagAgreement.ToString(
                                "0.######",
                                CultureInfo.InvariantCulture),
                        ["canonicalOrderedAgreement"] =
                            canonicalOrderedAgreement.ToString(
                                "0.######",
                                CultureInfo.InvariantCulture),
                        ["canonicalBagAgreement"] =
                            canonicalBagAgreement.ToString(
                                "0.######",
                                CultureInfo.InvariantCulture),
                        ["tableRegionPolicy"] = useOutsideTableOnly
                            ? "exclude_canonical_table_regions"
                            : "retain_table_region_as_layout_challenger",
                        ["fragmentationGuard"] = fragmented
                            ? "fragmented"
                            : "coherent",
                        ["contentRoleHint"] = navigationHint
                            ? RetrievalContentClassifier.NavigationRole
                            : RetrievalContentClassifier.ContentRole,
                        ["semanticDecisionOwner"] = "llm_client"
                    }
                }
            });
            recoveredCharacters += candidate.Text.Length;
        }

        AddDistinct(page.QualityFlags, PageQualityFlag);
        AddDistinct(page.QualityFlags, RecoveryQualityFlag);
        return new(
            candidates.Length,
            candidates.Length,
            recoveredCharacters,
            orderedAgreement,
            bagAgreement,
            true,
            "applied");
    }

    private static bool OverlapsAnyTable(
        CanonicalPolygon? polygon,
        IReadOnlyList<Bounds> tableBounds)
    {
        var bounds = ToBounds(polygon);
        return bounds.HasValue
               && tableBounds.Any(table =>
                   ComputeOverlapRatio(bounds.Value, table)
                   >= MinimumTableOverlapToExclude);
    }

    private static bool IsUseful(
        string text,
        IReadOnlyList<string> tokens)
        => text.Length >= 6
           && tokens.Count >= 2
           && tokens.Any(static token =>
               token.Length >= 3
               && token.Any(char.IsLetter));

    private static string Clean(string text)
        => CollapseWhitespacePerLine(
            OcrNoiseFilter.RemoveSpacedLetterRunNoise(
                PdfTextSanitizer.ForStorage(text)));

    private static Bounds ToNormalizedBounds(
        ExtractedPdfLayoutBlock block,
        double pageWidth,
        double pageHeight)
        => new(
            Left: Math.Clamp(block.Left / pageWidth, 0d, 1d),
            Top: Math.Clamp(1d - block.Top / pageHeight, 0d, 1d),
            Right: Math.Clamp(block.Right / pageWidth, 0d, 1d),
            Bottom: Math.Clamp(
                1d - block.Bottom / pageHeight,
                0d,
                1d));

    private static Bounds? ToBounds(CanonicalPolygon? polygon)
    {
        if (polygon?.Points is not { Count: >= 3 })
            return null;
        return new(
            polygon.Points.Min(static point => point.X),
            polygon.Points.Min(static point => point.Y),
            polygon.Points.Max(static point => point.X),
            polygon.Points.Max(static point => point.Y));
    }

    private static CanonicalPolygon ToCanonicalPolygon(
        Bounds bounds)
        => new()
        {
            Points =
            [
                new() { X = bounds.Left, Y = bounds.Top },
                new() { X = bounds.Right, Y = bounds.Top },
                new() { X = bounds.Right, Y = bounds.Bottom },
                new() { X = bounds.Left, Y = bounds.Bottom }
            ]
        };

    private static double ComputeOverlapRatio(
        Bounds source,
        Bounds target)
    {
        var sourceArea = Math.Max(
            0d,
            source.Right - source.Left)
            * Math.Max(0d, source.Bottom - source.Top);
        if (sourceArea <= 0d)
            return 0d;

        var intersectionWidth = Math.Max(
            0d,
            Math.Min(source.Right, target.Right)
            - Math.Max(source.Left, target.Left));
        var intersectionHeight = Math.Max(
            0d,
            Math.Min(source.Bottom, target.Bottom)
            - Math.Max(source.Top, target.Top));
        return intersectionWidth * intersectionHeight / sourceArea;
    }

    private static double ComputeOrderedCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate)
    {
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
               / (double)Math.Max(1, source.Count);
    }

    private static double ComputeBagCoverage(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate)
    {
        var remaining = candidate
            .GroupBy(static token => token, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var matched = 0;
        foreach (var token in source)
        {
            if (!remaining.TryGetValue(token, out var count)
                || count <= 0)
            {
                continue;
            }

            remaining[token] = count - 1;
            matched++;
        }

        return matched / (double)Math.Max(1, source.Count);
    }

    private static IReadOnlyList<string> Tokenize(string value)
        => CoverageTokenRegex()
            .Matches(
                value.Normalize(NormalizationForm.FormKC)
                    .ToLowerInvariant())
            .Select(static match => match.Value)
            .ToArray();

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

    private static NativeLayoutPageReconciliation Empty(
        string decisionReason,
        int candidateBlockCount = 0)
        => new(
            candidateBlockCount,
            0,
            0,
            1.0,
            0.0,
            false,
            decisionReason);

    private static string Sha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record LayoutCandidate(
        ExtractedPdfLayoutBlock Source,
        string Text,
        IReadOnlyList<string> Tokens,
        Bounds Bounds);

    private readonly record struct Bounds(
        double Left,
        double Top,
        double Right,
        double Bottom);

    [GeneratedRegex(
        @"[\p{L}\p{N}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex CoverageTokenRegex();

    [GeneratedRegex(
        @"\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
