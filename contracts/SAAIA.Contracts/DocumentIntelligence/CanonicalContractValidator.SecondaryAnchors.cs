namespace SAAIA.Contracts.DocumentIntelligence;

internal sealed class CanonicalAnchorValidationIndex
{
    internal sealed record SecondaryLine(CanonicalPage Page, CanonicalTableRegionObservation Observation,
        CanonicalTableRegionObservationLine Line);
    internal Dictionary<int, CanonicalPage> Pages { get; } = [];
    internal Dictionary<string, SecondaryLine> SecondaryLines { get; } = new(StringComparer.Ordinal);

    internal CanonicalAnchorValidationIndex(CanonicalDocument document)
    {
        foreach (var page in document.Pages)
        {
            Pages.TryAdd(page.PageNumber, page);
            foreach (var observation in page.TableRegionObservations?.Observations ?? [])
            {
                if (observation?.Lines is null) continue;
                foreach (var line in observation.Lines)
                    if (line is not null && !string.IsNullOrWhiteSpace(line.LineId))
                        SecondaryLines.TryAdd(line.LineId, new(page, observation, line));
            }
        }
    }
}

public static partial class CanonicalContractValidator
{
    private static void ValidateSecondaryAnchorRegion(SourceAnchor anchor, SourceAnchorRegion region,
        CanonicalDocument document, CanonicalAnchorValidationIndex index, string path,
        List<CanonicalValidationIssue> issues)
    {
        Require(anchor.ProjectionType == CanonicalTableObservationAnchors.ProjectionType
            && anchor.Precision == CanonicalTableObservationAnchors.Precision && anchor.Regions.Count == 1,
            path, "secondary_anchor_type", "Secondary anchors must identify one literal observation line.", issues);
        Require(region.BlockIds.Count == 0 && region.SpanIds.Count == 0 && region.TableCellIds.Count == 0,
            path, "secondary_primary_mixed", "Secondary anchors cannot borrow primary references.", issues);
        if (region.TableRegionObservationLineIds is not { Count: 1 } ids || string.IsNullOrWhiteSpace(ids[0])
            || !index.SecondaryLines.TryGetValue(ids[0], out var source))
        {
            Require(false, path, "broken_reference", "Secondary line does not exist in this document.", issues);
            return;
        }
        Require(source.Page.PageNumber == region.PageNumber
            && source.Observation.ObservationId == region.TableRegionObservationId
            && source.Observation.PhysicalPage == region.PageNumber && anchor.ProjectionId == source.Line.LineId,
            path, "secondary_scope", "Secondary observation, page and line identities must match.", issues);
        Require(string.Equals(region.RawTextSha256, source.Line.TextSha256, StringComparison.OrdinalIgnoreCase),
            path, "hash_mismatch", "Secondary line literal hash must match.", issues);
        Require(CanonicalTableRegionObservationValidator.EqualPolygon(region.Polygon, source.Line.Polygon),
            path, "secondary_polygon", "Secondary line polygon must match the observation.", issues);
        Require(anchor.AnchorId == CanonicalTableObservationAnchors.BuildAnchorId(source.Observation,
            source.Line, document.ManifestSha256), path, "secondary_anchor_identity", "Secondary anchor identity must match.", issues);
    }
}
