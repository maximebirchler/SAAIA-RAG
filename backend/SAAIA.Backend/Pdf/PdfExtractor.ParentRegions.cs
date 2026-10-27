using SAAIA.Backend;

static partial class PdfExtractor
{
    /// <summary>
    /// A landscape spread may contain two observed parents, each with several
    /// text columns. Flattening every X anchor first strands a parent's heading
    /// in its middle column. Only a continuous central gutter plus independent
    /// heading prefixes and parallel body columns justify this narrower order.
    /// </summary>
    private static bool TryBuildParentRegionText(IReadOnlyList<PdfLayoutSegment> segments,
        double paragraphGapThreshold, double medianWidth, double? pageWidth, double? pageHeight, out string text)
    {
        text = string.Empty;
        using var probe = BackendDiagnostics.Start("ingestion.native_parent_region_candidate");
        probe.Set("segments", segments.Count);
        bool Abstain(string reason) { probe.Complete("not_applied", reason); return false; }
        if (pageWidth is not { } width || pageHeight is not { } height || !IsFinite(width) || !IsFinite(height)
            || width <= 0 || height <= 0 || width / height is < 1.25 or > 2.0)
            return Abstain("landscape_spread_geometry_unavailable");
        probe.Set("page_width_points", width);
        probe.Set("page_height_points", height);
        if (segments.Count < 14 || segments.Any(segment => segment.Left < 0 || segment.Right > width
                || segment.Bottom < 0 || segment.Top > height)) return Abstain("source_regions_outside_spread_or_sparse");
        var center = width / 2d;
        // Use the physical center, never the largest gap between arbitrary
        // column anchors. An internal sidebar gap can be wider than the gutter.
        if (segments.Any(segment => segment.Left < center && segment.Right > center))
            return Abstain("source_segment_crosses_parent_gutter");
        var left = segments.Where(segment => segment.Right <= center).ToArray();
        var right = segments.Where(segment => segment.Left >= center).ToArray();
        if (left.Length + right.Length != segments.Count || left.Length < 7 || right.Length < 7)
            return Abstain("two_complete_parent_regions_unavailable");
        var gutterLeft = left.Max(segment => segment.Right);
        var gutterRight = right.Min(segment => segment.Left);
        var gutterWidth = gutterRight - gutterLeft;
        probe.Set("gutter_width_points", gutterWidth);
        if (gutterLeft < width * 0.35 || gutterRight > width * 0.65
            || gutterWidth < Math.Max(12d, medianWidth * 0.5d)) return Abstain("central_gutter_not_established");
        if (!HasObservedParentHeadingPrefix(left, medianWidth, out var leftColumns)
            || !HasObservedParentHeadingPrefix(right, medianWidth, out var rightColumns))
            return Abstain("parent_heading_prefix_or_parallel_columns_unavailable");
        probe.Set("left_parallel_columns", leftColumns);
        probe.Set("right_parallel_columns", rightColumns);
        if (!TryBuildNestedColumnarSegmentText(left, paragraphGapThreshold, medianWidth, out var leftText, parentRegionEstablished: true)
            || !TryBuildNestedColumnarSegmentText(right, paragraphGapThreshold, medianWidth, out var rightText, parentRegionEstablished: true))
            return Abstain("nested_parent_reading_order_unavailable");
        text = leftText + "\n\n\n" + rightText;
        probe.Set("parent_regions", 2);
        probe.Set("candidate", true);
        // The caller still enforces its unchanged complete-token retention
        // gate before admitting this candidate as the extracted primary text.
        probe.Complete("success", "two_observed_parent_regions_candidate");
        return true;
    }

    private static bool HasObservedParentHeadingPrefix(IReadOnlyList<PdfLayoutSegment> segments,
        double medianWidth, out int columnCount)
    {
        var tolerance = Math.Clamp(medianWidth * 2.1d, 28d, 48d);
        var stable = ClusterSegmentsByLeftAnchor(segments, tolerance).Where(column => column.Count >= 3).ToArray();
        columnCount = stable.Length;
        if (stable.Length < 2 || stable.Sum(column => column.Count) < Math.Ceiling(segments.Count * 0.60d)) return false;
        var parallel = segments.GroupBy(segment => segment.LineIndex).Where(group => group.Count() >= 2).ToArray();
        if (parallel.Length < 3) return false;
        var firstParallelLine = parallel.Min(group => group.Key);
        var firstBodyTop = parallel.Where(group => group.Key == firstParallelLine).SelectMany(group => group).Max(segment => segment.Top);
        return segments.Any(segment => segment.LineIndex < firstParallelLine && segment.Bottom > firstBodyTop
            && LooksLikeLayoutHeadingSegment(segment.Text));
    }
}
