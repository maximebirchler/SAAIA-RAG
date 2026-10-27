namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>Hashes the persisted geometry proof, with its source and coordinate identity.</summary>
public static class CanonicalTableVectorEvidence
{
    public static string ComputeSha256(CanonicalTableRegionObservation observation)
    {
        var reading = observation.PhysicalReading;
        return CanonicalContractJson.ComputeSha256(new
        {
            observation.DocumentId, observation.RevisionId, observation.ObservationId,
            observation.SourceSha256, observation.RawObservationSha256, observation.PhysicalPage, observation.TableId,
            observation.SourceTableRef, observation.Region, observation.CoordinateFrame,
            reading.NativeFrameAvailability, reading.NativeFrameReason,
            reading.NativeRotationDegrees, reading.NativeCropBoxLeft, reading.NativeCropBoxBottom,
            reading.NativeMediaBoxLeft, reading.NativeMediaBoxBottom,
            reading.VectorObservationAvailability, reading.VectorObservationReason, reading.VectorRules,
            bandGeometryRule = "exact_source_rule_region_intersection_v1", reading.Bands
        });
    }

    public static List<CanonicalTablePhysicalBand> BuildBands(CanonicalTableRegionObservation observation)
    {
        var reading = observation.PhysicalReading;
        var frame = observation.CoordinateFrame;
        var top = observation.Region.Points.Min(p => p.Y);
        var left = observation.Region.Points.Min(p => p.X) * frame.PageWidthPoints;
        var right = observation.Region.Points.Max(p => p.X) * frame.PageWidthPoints;
        var bottom = observation.Region.Points.Max(p => p.Y);
        var bands = reading.VectorRules!.OrderBy(r => r.Start).Select((rule, index) => new CanonicalTablePhysicalBand
        {
            BandOrdinal = index, SupportingRuleIds = [rule.RuleId],
            Polygon = new() { Points = [new() { X=Math.Max(rule.Start,left)/frame.PageWidthPoints,Y=top },
                new() { X=Math.Min(rule.End,right)/frame.PageWidthPoints,Y=top },
                new() { X=Math.Min(rule.End,right)/frame.PageWidthPoints,Y=bottom },
                new() { X=Math.Max(rule.Start,left)/frame.PageWidthPoints,Y=bottom }] }
        }).ToList();
        foreach (var line in observation.Lines)
        {
            var matches = bands.Where(b => Contains(b.Polygon, line.Polygon)).Take(2).ToArray();
            if (matches.Length == 1) matches[0].OrderedLineIds.Add(line.LineId);
        }
        var lookup = observation.Lines.ToDictionary(l => l.LineId, StringComparer.Ordinal);
        foreach (var band in bands)
            band.OrderedLineIds = band.OrderedLineIds.OrderBy(id => lookup[id].Polygon.Points.Min(p=>p.Y))
                .ThenBy(id => lookup[id].Polygon.Points.Min(p=>p.X)).ThenBy(id=>lookup[id].RawOrdinal).ToList();
        return bands;
    }

    public static bool IsRectangularRegion(CanonicalPolygon? region)
    {
        if(region?.Points is not {Count:4} points || points.Any(p=>p is null || !double.IsFinite(p.X)
            || !double.IsFinite(p.Y) || p.X is <0 or >1 || p.Y is <0 or >1)) return false;
        if(points.Select(p=>p.X).Distinct().Count()!=2 || points.Select(p=>p.Y).Distinct().Count()!=2
            || points.Select(p=>(p.X,p.Y)).Distinct().Count()!=4) return false;
        return points.Select((point,index)=>(point,next:points[(index+1)%4]))
            .All(pair=>(pair.point.X==pair.next.X) != (pair.point.Y==pair.next.Y));
    }

    private static bool Contains(CanonicalPolygon outer, CanonicalPolygon inner)
        => inner.Points.All(p => p.X >= outer.Points.Min(q=>q.X) && p.X <= outer.Points.Max(q=>q.X)
            && p.Y >= outer.Points.Min(q=>q.Y) && p.Y <= outer.Points.Max(q=>q.Y));
}
