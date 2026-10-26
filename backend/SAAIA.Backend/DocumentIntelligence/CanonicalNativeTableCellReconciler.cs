using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Selects literal native typography only for uniquely associated table-cell observations.</summary>
internal static class CanonicalNativeTableCellReconciler
{
    internal const string ObservationBlockType = "native_table_cell_observation";
    private const string SelectionFlag = "canonical_table_native_literal_selected";
    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal double Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
    }

    internal static int Apply(CanonicalDocument document, CanonicalPage page,
        ExtractedPdfPage nativePage, string engineVersion)
    {
        if (nativePage.NativeLayoutBlocks is not { Count: > 0 }
            || nativePage.WidthPoints is not > 0 || nativePage.HeightPoints is not > 0
            || page.Geometry.RotationDegrees != 0)
            return 0;

        var candidates = nativePage.NativeLayoutBlocks.Select(block => (Block: block, Bounds: new Bounds(
            block.Left / nativePage.WidthPoints.Value, 1 - block.Top / nativePage.HeightPoints.Value,
            block.Right / nativePage.WidthPoints.Value, 1 - block.Bottom / nativePage.HeightPoints.Value)))
            .Where(item => IsFinite(item.Bounds) && item.Bounds.Area > 0 && !string.IsNullOrWhiteSpace(item.Block.Text)).ToArray();
        var cells = page.Tables.SelectMany(table => table.Cells).ToArray();
        var selected = 0;
        foreach (var table in page.Tables)
        foreach (var cell in table.Cells)
        {
            if (ToBounds(cell.Polygon) is not { } cellBounds || cellBounds.Area <= 0)
                continue;
            // Both directions must overlap: a whole-page/table block cannot stand in for a cell.
            var overlapping = candidates.Where(candidate => Corresponds(candidate.Bounds, cellBounds)).ToArray();
            if (overlapping.Length != 1)
                continue;
            var candidate = overlapping[0];
            if (cells.Count(other => ToBounds(other.Polygon) is { } otherBounds
                    && Corresponds(candidate.Bounds, otherBounds)) != 1)
                continue;
            var literal = candidate.Block.Text.Trim();
            if (string.Equals(cell.Text.Canonical, literal, StringComparison.Ordinal)
                || !OnlyAlphabeticSeparatorTypographyDiffers(cell.Text.Canonical, literal))
                continue;

            var blockId = CanonicalStableId.Create("block", document.Source.Sha256,
                ObservationBlockType, cell.CellId, Sha256(literal));
            if (!page.Blocks.Any(block => block.BlockId == blockId))
            {
                page.Blocks.Add(new()
                {
                    BlockId = blockId, BlockType = ObservationBlockType,
                    Ordinal = page.Blocks.Count == 0 ? 0 : page.Blocks.Max(block => block.Ordinal) + 1,
                    ReadingOrder = page.Blocks.Count == 0 ? 0 : page.Blocks.Max(block => block.ReadingOrder) + 1,
                    Polygon = ToPolygon(candidate.Bounds),
                    Text = new() { Raw = literal, Canonical = literal, Display = literal,
                        Normalized = literal.Normalize(NormalizationForm.FormC).ToLowerInvariant(),
                        Retrieval = "", RawSha256 = Sha256(literal) },
                    QualityFlags = [SelectionFlag],
                    Provenance = new()
                    {
                        StageId = CanonicalNativeTextCoverageReconciler.StageId,
                        Method = "unique_spatial_native_table_cell_literal", Engine = "PdfPig", EngineVersion = engineVersion,
                        Attributes = new(StringComparer.Ordinal)
                        {
                            ["sourceType"] = "native_pdf_text_geometry", ["sourcePage"] = page.PageNumber.ToString(CultureInfo.InvariantCulture),
                            ["sourceReadingOrder"] = candidate.Block.ReadingOrder.ToString(CultureInfo.InvariantCulture),
                            ["layoutAlgorithm"] = candidate.Block.Algorithm, ["canonicalTableId"] = table.TableId,
                            ["canonicalTableCellId"] = cell.CellId, ["sectionAssociation"] = "not_asserted",
                            ["selectionPolicy"] = "unique_spatial_literal_typography_only"
                        }
                    }
                });
            }
            if (!cell.BlockIds.Contains(blockId, StringComparer.Ordinal)) cell.BlockIds.Add(blockId);
            // Preserve the original engine observation and its hash; only the selected view changes.
            cell.Text.Canonical = literal;
            cell.Text.Retrieval = literal;
            cell.Text.Display = literal;
            cell.Text.Normalized = literal.Normalize(NormalizationForm.FormC).ToLowerInvariant();
            if (!page.QualityFlags.Contains(SelectionFlag, StringComparer.Ordinal)) page.QualityFlags.Add(SelectionFlag);
            selected++;
        }
        return selected;
    }

    private static bool OnlyAlphabeticSeparatorTypographyDiffers(string converted, string literal)
    {
        var left = CollapseSpaces(converted);
        var right = CollapseSpaces(literal);
        if (left.Length != right.Length) return false;
        var changed = false;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] == right[index]) continue;
            if (!IsSeparatorDash(left[index]) || !IsSeparatorDash(right[index])
                || index < 2 || index + 2 >= left.Length || left[index - 1] != ' ' || left[index + 1] != ' '
                || !char.IsLetter(left[index - 2]) || !char.IsLetter(left[index + 2])) return false;
            changed = true;
        }
        return changed;
    }

    private static bool IsSeparatorDash(char value) => value is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014';
    private static string CollapseSpaces(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsFinite(Bounds value) => double.IsFinite(value.Left) && double.IsFinite(value.Top)
        && double.IsFinite(value.Right) && double.IsFinite(value.Bottom)
        && value.Left >= 0 && value.Top >= 0 && value.Right <= 1 && value.Bottom <= 1;
    private static bool Corresponds(Bounds first, Bounds second)
    {
        var intersection = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left))
            * Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return first.Area > 0 && second.Area > 0 && intersection / first.Area >= 0.70 && intersection / second.Area >= 0.70;
    }
    private static Bounds? ToBounds(CanonicalPolygon? polygon) => polygon?.Points is not { Count: >= 3 } ? null
        : new(polygon.Points.Min(point => point.X), polygon.Points.Min(point => point.Y),
            polygon.Points.Max(point => point.X), polygon.Points.Max(point => point.Y));
    private static CanonicalPolygon ToPolygon(Bounds bounds) => new()
    {
        Points = [new() { X = bounds.Left, Y = bounds.Top }, new() { X = bounds.Right, Y = bounds.Top },
            new() { X = bounds.Right, Y = bounds.Bottom }, new() { X = bounds.Left, Y = bounds.Bottom }]
    };
}
