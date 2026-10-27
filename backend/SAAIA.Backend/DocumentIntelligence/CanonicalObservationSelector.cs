using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record CanonicalObservationDecision(
    string BlockId, string Disposition, string Reason, int? SourceLineStart = null, int? SourceLineEnd = null);

internal sealed record CanonicalPageObservationSelection(
    IReadOnlyList<CanonicalBlock> Blocks,
    IReadOnlyList<CanonicalTable> Tables,
    IReadOnlyList<CanonicalObservationDecision> Decisions,
    IReadOnlyDictionary<string, int> OrderOverrides)
{
    internal int ResolveOrder(string sourceId, int fallback)
        => OrderOverrides.TryGetValue(sourceId, out var order) ? order : fallback;

    internal bool HasPhysicalOrderOverride => OrderOverrides.Count > 0;
}

/// <summary>
/// The canonical document keeps every observation for audit. Publication selects
/// its primary observations and source-bound additions, never concurrent full views.
/// Primary repetitions are not deduplicated. Each native channel gets an independent
/// occurrence inventory so another reading cannot consume a real extra occurrence.
/// </summary>
internal static class CanonicalObservationSelector
{
    internal const string SelectedSupplementSignal = "canonical_selected_native_supplement";
    internal const string AuditOnlySignal = "canonical_observation_audit_only";
    // Read compatibility for previously published canonical bundles. New
    // reconciliation describes literal uncertainty, not semantic novelty.
    internal const string VerifiedSourceGapRole = "verified_native_source_gap";
    internal const string LiteralCoverageUnresolvedRole = "literal_native_coverage_unresolved";
    internal const string LiteralCoverageUnresolvedSignal = "canonical_native_literal_coverage_unresolved";

    internal static CanonicalPageObservationSelection SelectPage(CanonicalPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var aliases = page.Blocks.Where(block => !IsNativeObservation(block)
            && IsRedundantTableCellObservation(page, block)).ToArray();
        var aliasIds = aliases.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        var primary = page.Blocks.Where(block => !IsNativeObservation(block) && !aliasIds.Contains(block.BlockId)).ToArray();
        var selected = primary.ToList();
        var decisions = primary.Select(block => new CanonicalObservationDecision(
            block.BlockId, "primary", "original_canonical_observation")).ToList();
        decisions.AddRange(aliases.Select(block => new CanonicalObservationDecision(
            block.BlockId, "audit_only", "same_literal_and_unique_physical_table_cell_anchor")));
        var representedTexts = CanonicalObservationCoverage.PrimaryTexts(page).ToList();

        foreach (var channel in page.Blocks.Where(IsNativeObservation)
                     .GroupBy(block => block.BlockType, StringComparer.Ordinal)
                     .OrderBy(group => group.Key == CanonicalNativeTextCoverageReconciler.RecoveryBlockType ? 0 : 1)
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var inventory = new CanonicalObservationCoverage.Inventory(representedTexts);
            var channelAdditions = new List<string>();
            var orderedBlocks = channel.OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal).ToArray();
            var sourceLines = orderedBlocks.Where(block => !RequiresLiteralRetention(block)
                    && !string.IsNullOrWhiteSpace(block.Text.Retrieval))
                .SelectMany(block => CanonicalObservationCoverage.Lines(block.Text.Retrieval)
                    .Select(line => (BlockId: block.BlockId, Line: line with { FlowGroup = block.BlockId }))).ToArray();
            var representedLines = inventory.RepresentedLines(sourceLines.Select(item => item.Line).ToArray());
            var representedKeys = representedLines.Select(index => (sourceLines[index].BlockId, sourceLines[index].Line.Start)).ToHashSet();
            foreach (var block in orderedBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text.Retrieval))
                {
                    decisions.Add(new(block.BlockId, "audit_only", "observation_already_represented_by_structured_cell"));
                    continue;
                }
                if (RequiresLiteralRetention(block))
                {
                    selected.Add(SelectLines(block, CanonicalObservationCoverage.Lines(block.Text.Retrieval)));
                    channelAdditions.Add(block.Text.Retrieval);
                    decisions.Add(new(block.BlockId, "supplement", "unresolved_native_literal_retained_without_semantic_novelty_assertion"));
                    continue;
                }

                var retained = CanonicalObservationCoverage.Lines(block.Text.Retrieval)
                    .Where(line => !representedKeys.Contains((block.BlockId, line.Start))).ToArray();
                if (retained.Length == 0)
                {
                    decisions.Add(new(block.BlockId, "audit_only", "content_occurrences_already_represented_by_selected_view"));
                    continue;
                }
                selected.Add(SelectLines(block, retained));
                var text = string.Join(Environment.NewLine, retained.Select(line => line.Text));
                channelAdditions.Add(text);
                decisions.Add(new(block.BlockId, "supplement", "unrepresented_native_source_lines",
                    retained.Min(line => line.Start), retained.Max(line => line.End)));
            }
            representedTexts.AddRange(channelAdditions);
        }

        return new(selected, page.Tables.ToArray(), decisions,
            CanonicalPhysicalReadingOrder.Resolve(page, selected));
    }

    internal static bool IsNativeObservation(CanonicalBlock block)
        => IsNativeObservation(block.BlockType);

    internal static bool IsNativeObservation(string blockType)
        => blockType is CanonicalNativeTextCoverageReconciler.RecoveryBlockType
            or CanonicalNativeLayoutReconciler.RecoveryBlockType
            or CanonicalNativeTableCellReconciler.ObservationBlockType;

    private static bool RequiresLiteralRetention(CanonicalBlock block)
        => block.Provenance.Attributes.TryGetValue("selectionRole", out var role)
            && role is LiteralCoverageUnresolvedRole or VerifiedSourceGapRole;

    internal static bool IsRedundantTableCellObservation(CanonicalPage page, CanonicalBlock block)
    {
        // A heading/caption can carry a separate structural role. Only parallel
        // ordinary text at a unique cell's own physical anchor is an alias.
        if (block.BlockType is not "text" and not "list_item"
            || block.Polygon?.Points is not { Count: >= 3 }
            || string.IsNullOrWhiteSpace(block.Text.Canonical))
            return false;
        var text = string.Join(" ", block.Text.Canonical.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return page.Tables.SelectMany(table => table.Cells).Count(cell =>
            cell.Polygon?.Points is { Count: >= 3 }
            && string.Join(" ", cell.Text.Canonical.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) == text
            && SameAnchor(block.Polygon, cell.Polygon)) == 1;
    }

    private static bool SameAnchor(CanonicalPolygon first, CanonicalPolygon second)
    {
        var a = (Left: first.Points.Min(point => point.X), Top: first.Points.Min(point => point.Y),
            Right: first.Points.Max(point => point.X), Bottom: first.Points.Max(point => point.Y));
        var b = (Left: second.Points.Min(point => point.X), Top: second.Points.Min(point => point.Y),
            Right: second.Points.Max(point => point.X), Bottom: second.Points.Max(point => point.Y));
        if (!new[] { a.Left, a.Top, a.Right, a.Bottom, b.Left, b.Top, b.Right, b.Bottom }.All(double.IsFinite)
            || a.Right <= a.Left || b.Right <= b.Left || a.Bottom <= a.Top || b.Bottom <= b.Top)
            return false;
        const double epsilon = .000001;
        if (Math.Abs(a.Left - b.Left) > epsilon || Math.Abs(a.Top - b.Top) > epsilon
            || Math.Abs(a.Bottom - b.Bottom) > epsilon)
            return false;
        // OCR boxes can include a different trailing blank width. Their common
        // left edge and identical baseline/height must still be established.
        var widthRatio = Math.Min(a.Right - a.Left, b.Right - b.Left) / Math.Max(a.Right - a.Left, b.Right - b.Left);
        return widthRatio >= .50;
    }

    private static CanonicalBlock SelectLines(CanonicalBlock source,
        IReadOnlyList<CanonicalObservationCoverage.SourceLine> lines)
    {
        var text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        var attributes = new Dictionary<string, string>(source.Provenance.Attributes, StringComparer.Ordinal)
        {
            ["selectionPolicy"] = "primary_plus_occurrence_bounded_native_gaps_v1",
            ["selectedSourceLineRanges"] = string.Join(",", lines.Select(line =>
                line.Start.ToString(CultureInfo.InvariantCulture) + "-" + line.End.ToString(CultureInfo.InvariantCulture))),
            ["selectedObservationOf"] = source.BlockId,
            ["sourceObservationRawSha256"] = source.Text.RawSha256,
            ["selectionRole"] = "selected_native_supplement"
        };
        if (source.Provenance.Attributes.TryGetValue("selectionRole", out var originalRole))
            attributes["sourceObservationSelectionRole"] = originalRole;
        // This view keeps the original block ID as its source anchor. The raw
        // source block remains unchanged in page.Blocks; selected ranges are explicit.
        return new()
        {
            BlockId = source.BlockId, BlockType = source.BlockType, Ordinal = source.Ordinal,
            ReadingOrder = source.ReadingOrder, Polygon = source.Polygon, Spans = source.Spans.ToList(),
            ChildBlockIds = source.ChildBlockIds.ToList(), IsRepeatedFurniture = source.IsRepeatedFurniture,
            Text = new()
            {
                Raw = text, RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                Canonical = text, Display = text, Retrieval = text,
                Normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()
            },
            QualityFlags = source.QualityFlags.Append(SelectedSupplementSignal).Distinct(StringComparer.Ordinal).ToList(),
            Provenance = new()
            {
                StageId = source.Provenance.StageId, Method = source.Provenance.Method,
                Engine = source.Provenance.Engine, EngineVersion = source.Provenance.EngineVersion,
                Confidence = source.Provenance.Confidence, Attributes = attributes
            }
        };
    }
}
