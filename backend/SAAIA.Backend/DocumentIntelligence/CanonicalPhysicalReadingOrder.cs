using SAAIA.Contracts.DocumentIntelligence;

/// <summary>
/// A table spanning the physical text extent is a horizontal flow barrier. It
/// can disambiguate before/table/after without assuming a universal column order.
/// Within a band, the observed primary order is retained. Ambiguous geometry is
/// left untouched; no sentence or section association is invented.
/// </summary>
internal static class CanonicalPhysicalReadingOrder
{
    private sealed record Positioned(string Id, Bounds Bounds, int ObservedOrder);
    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom);

    internal static IReadOnlyDictionary<string, int> Resolve(CanonicalPage page,
        IReadOnlyList<CanonicalBlock> selected)
    {
        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        if (page.Geometry.RotationDegrees != 0 || page.Tables.Count == 0)
            return empty;
        var body = selected.Where(block => !CanonicalObservationSelector.IsNativeObservation(block)
                && !block.IsRepeatedFurniture && block.BlockType is not "page_header" and not "page_footer"
                && !string.IsNullOrWhiteSpace(block.Text.Retrieval)).ToArray();
        if (body.Length == 0 || body.Any(block => Box(block.Polygon) is null))
            return empty;
        var positioned = body.Select(block => new Positioned(block.BlockId, Box(block.Polygon)!.Value,
            block.ReadingOrder)).ToArray();
        var leftCenter = positioned.Min(item => (item.Bounds.Left + item.Bounds.Right) / 2);
        var rightCenter = positioned.Max(item => (item.Bounds.Left + item.Bounds.Right) / 2);
        var barriers = page.Tables.Select(table => (Table: table, Box: Box(table.Polygon)))
            .Where(item => item.Box is { } box && box.Right - box.Left >= .60
                && box.Left <= leftCenter && box.Right >= rightCenter)
            .OrderBy(item => item.Box!.Value.Top).ToArray();
        if (barriers.Length == 0)
            return empty;
        for (var index = 1; index < barriers.Length; index++)
            if (barriers[index - 1].Box!.Value.Bottom > barriers[index].Box!.Value.Top)
                return empty;
        // A paragraph overlapping a barrier may be a floating or merged region.
        // Its reading order cannot be established from this separation rule.
        if (positioned.Any(item => barriers.Any(barrier =>
                item.Bounds.Top < barrier.Box!.Value.Bottom
                && item.Bounds.Bottom > barrier.Box.Value.Top)))
            return empty;

        var ordered = selected.Where(block => !CanonicalObservationSelector.IsNativeObservation(block)
                && block.BlockType == "page_header").OrderBy(block => block.ReadingOrder)
            .Select(block => block.BlockId).ToList();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var blockTypes = body.ToDictionary(block => block.BlockId, block => block.BlockType, StringComparer.Ordinal);
        foreach (var barrier in barriers)
        {
            var band = positioned.Where(item => item.Bounds.Bottom <= barrier.Box!.Value.Top
                && !emitted.Contains(item.Id)).ToArray();
            foreach (var block in OrderBand(band, blockTypes))
            {
                ordered.Add(block.Id);
                emitted.Add(block.Id);
            }
            ordered.Add(barrier.Table.TableId);
            emitted.Add(barrier.Table.TableId);
        }
        foreach (var block in positioned.Where(item => !emitted.Contains(item.Id)).OrderBy(item => item.ObservedOrder))
        {
            ordered.Add(block.Id);
            emitted.Add(block.Id);
        }
        // Tables which are not full-width retain their observed fallback order.
        // Do not mix an established barrier order with another unplaced table.
        if (page.Tables.Any(table => !emitted.Contains(table.TableId)))
            return empty;
        ordered.AddRange(selected.Where(block => !CanonicalObservationSelector.IsNativeObservation(block)
                && block.BlockType == "page_footer").OrderBy(block => block.ReadingOrder).Select(block => block.BlockId));
        return ordered.Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => checked(item.index * 100), StringComparer.Ordinal);
    }

    private static IEnumerable<Positioned> OrderBand(Positioned[] band, IReadOnlyDictionary<string, string> types)
    {
        // A terminal heading below the complete extent of two side-by-side
        // narrative columns cannot be read between those columns. This only
        // orders the observed block; it does not attach it to the table or
        // assert a section/caption relationship. A heading within a column
        // retains the observed order, including all of its continuation.
        var narrative = band.Where(item => types[item.Id] is not "section_header" and not "caption").ToArray();
        if (!narrative.Any(left => narrative.Any(right => left.Bounds.Right < right.Bounds.Left)))
            return band.OrderBy(item => item.ObservedOrder);
        var terminal = band.Where(item => types[item.Id] is "section_header" or "caption"
            && narrative.All(other => other.Bounds.Bottom <= item.Bounds.Top)).ToArray();
        var terminalIds = terminal.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return band.Where(item => !terminalIds.Contains(item.Id)).OrderBy(item => item.ObservedOrder)
            .Concat(terminal.OrderBy(item => item.Bounds.Top).ThenBy(item => item.ObservedOrder));
    }

    private static Bounds? Box(CanonicalPolygon? polygon)
    {
        if (polygon?.Points is not { Count: >= 3 })
            return null;
        var box = new Bounds(polygon.Points.Min(point => point.X), polygon.Points.Min(point => point.Y),
            polygon.Points.Max(point => point.X), polygon.Points.Max(point => point.Y));
        return double.IsFinite(box.Left) && double.IsFinite(box.Top) && double.IsFinite(box.Right)
            && double.IsFinite(box.Bottom) && box.Left >= 0 && box.Top >= 0 && box.Right <= 1
            && box.Bottom <= 1 && box.Right > box.Left && box.Bottom > box.Top ? box : null;
    }
}
