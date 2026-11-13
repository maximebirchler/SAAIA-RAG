using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Keep text observed inside a picture without treating it as prose under a nearby heading.</summary>
internal static class CanonicalFigureTextAssociationPolicy
{
    internal const string QualityFlag = "canonical_picture_text_section_association_not_asserted";

    internal static void Apply(CanonicalDocument document, CancellationToken cancellationToken = default)
    {
        using var probe = BackendDiagnostics.Start("ingestion.canonical_picture_text_association");
        var observedPictures = 0;
        var markedBlocks = 0;
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pictures = page.Figures.Where(figure => figure.FigureType == "picture")
                .Select(figure => (Figure: figure, Bounds: Bounds(figure.Polygon)))
                // A page-sized raster can be a scanned text page. Its presence
                // alone does not distinguish photographed text from document prose.
                .Where(item => item.Bounds is { Area: > 0 and < 0.9 }).ToArray();
            observedPictures += pictures.Length;
            foreach (var block in page.Blocks)
            {
                if (!block.Provenance.Attributes.ContainsKey("doclingSelfRef")
                    || block.BlockType == "caption"
                    || page.Figures.Any(figure => figure.CaptionBlockIds.Contains(block.BlockId, StringComparer.Ordinal))
                    || CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(block)
                    || Bounds(block.Polygon) is not { Area: > 0 } bounds)
                    continue;
                var enclosing = pictures.FirstOrDefault(item =>
                    !item.Figure.CaptionBlockIds.Contains(block.BlockId, StringComparer.Ordinal)
                    && item.Bounds!.Value.CoverageOf(bounds) >= 0.95);
                if (enclosing.Figure is null)
                    continue;
                block.Provenance.Attributes["sectionAssociation"] = "not_asserted";
                block.Provenance.Attributes["contextScope"] = "physical_page";
                block.Provenance.Attributes["sectionAssociationReason"] = "text_within_detected_picture";
                block.Provenance.Attributes["containingFigureId"] = enclosing.Figure.FigureId;
                if (!block.QualityFlags.Contains(QualityFlag, StringComparer.Ordinal))
                    block.QualityFlags.Add(QualityFlag);
                affected.Add(block.BlockId);
                markedBlocks++;
            }
        }
        foreach (var section in document.SectionTree)
        {
            section.ContentBlockIds.RemoveAll(affected.Contains);
            if (section.TitleBlockIds.Any(affected.Contains))
                section.ParentSectionId = null;
        }
        probe.Set("bounded_detected_picture_count", observedPictures);
        probe.Set("unassigned_picture_text_block_count", markedBlocks);
        probe.Complete();
    }

    private readonly record struct Rectangle(double Left, double Top, double Right, double Bottom)
    {
        internal double Area => (Right - Left) * (Bottom - Top);
        internal double CoverageOf(Rectangle other) =>
            Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left))
            * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top)) / other.Area;
    }

    private static Rectangle? Bounds(CanonicalPolygon? polygon)
    {
        if (polygon?.Points is not { Count: 4 } points
            || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            return null;
        var bounds = new Rectangle(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
        // Bounding-box overlap would overstate containment for a rotated or
        // nonrectangular polygon. Leave such observations untouched.
        return points.All(point => (point.X == bounds.Left || point.X == bounds.Right)
            && (point.Y == bounds.Top || point.Y == bounds.Bottom)) ? bounds : null;
    }
}
