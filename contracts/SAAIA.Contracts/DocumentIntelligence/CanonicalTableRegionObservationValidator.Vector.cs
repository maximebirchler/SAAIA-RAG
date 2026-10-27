namespace SAAIA.Contracts.DocumentIntelligence;

public static partial class CanonicalTableRegionObservationValidator
{
    private static void ValidateVectorEvidence(CanonicalTableRegionObservation observation,
        Dictionary<string, CanonicalTableRegionObservationLine> lines, List<CanonicalValidationIssue> issues)
    {
        var reading=observation.PhysicalReading;
        if (reading is null) return;
        var candidate=reading.Availability=="candidate_only";
        var hasProof=reading.NativeFrameAvailability is not null || reading.VectorObservationAvailability is not null
            || reading.VectorRules is not null || reading.VectorEvidenceSha256 is not null;
        if (!candidate && !hasProof) return; // Uninspected legacy/default-off is unknown, never observed zero.
        Check(reading.NativeFrameAvailability is "available" or "unavailable", "physicalReading.nativeFrameAvailability", "native_frame", issues);
        Check(reading.VectorObservationAvailability is "observed" or "partial" or "unavailable" or "refused",
            "physicalReading.vectorObservationAvailability", "vector_availability", issues);
        if (!candidate)
        {
            Check(reading.VectorRules is null && reading.VectorEvidenceSha256 is null,
                "physicalReading.vectorRules", "unused_vector_proof", issues);
            return;
        }
        var frame=observation.CoordinateFrame;
        var validFrame=frame is not null && reading.NativeFrameAvailability=="available"
            && reading.NativeRotationDegrees==0 && reading.NativeCropBoxLeft==0 && reading.NativeCropBoxBottom==0
            && reading.NativeMediaBoxLeft==0 && reading.NativeMediaBoxBottom==0;
        Check(validFrame, "physicalReading.nativeFrameAvailability", "native_frame", issues);
        var validRules=reading.VectorObservationAvailability=="observed"
            && reading.VectorRules is {Count:>0 and <=MaximumBands} && frame is not null;
        Check(validRules,"physicalReading.vectorRules","vector_proof_required",issues);
        if (!validRules || !validFrame || observation.Region?.Points is not {Count:4}) return;
        Check(CanonicalTableVectorEvidence.IsRectangularRegion(observation.Region),
            "physicalReading.bands","rectangular_region_required",issues);
        var rules=reading.VectorRules!;
        var ids=new HashSet<string>(StringComparer.Ordinal);
        double? bottom=null;
        var region=observation.Region.Points;
        var left=region.Min(p=>p.X)*frame!.PageWidthPoints;
        var right=region.Max(p=>p.X)*frame.PageWidthPoints;
        var top=region.Min(p=>p.Y)*frame.PageHeightPoints;
        var lower=region.Max(p=>p.Y)*frame.PageHeightPoints;
        foreach(var rule in rules.OrderBy(r=>r?.Start))
        {
            if(rule is null) { Check(false,"physicalReading.vectorRules","required",issues); continue; }
            var valid=Id(rule.RuleId) && ids.Add(rule.RuleId) && rule.Axis=="h"
                && double.IsFinite(rule.Coordinate) && double.IsFinite(rule.Start) && double.IsFinite(rule.End)
                && double.IsFinite(rule.Thickness) && rule.Thickness is >0 and <=2
                && rule.Start>=0 && rule.End<=frame.PageWidthPoints && rule.Start<rule.End
                && Math.Max(rule.Start,left)<Math.Min(rule.End,right)
                && rule.End-rule.Start>=10*rule.Thickness && rule.Coordinate>top && rule.Coordinate<=lower
                && (bottom is null || bottom==rule.Coordinate)
                && rule.PrimitiveIds is {Count:>0 and <=32} && rule.PrimitiveIds.All(Id)
                && rule.PrimitiveIds.Distinct(StringComparer.Ordinal).Count()==rule.PrimitiveIds.Count;
            Check(valid,"physicalReading.vectorRules","literal_vector_geometry",issues);
            bottom=rule.Coordinate;
        }
        Check(Sha(reading.VectorEvidenceSha256) && reading.VectorEvidenceSha256==CanonicalTableVectorEvidence.ComputeSha256(observation),
            "physicalReading.vectorEvidenceSha256","vector_evidence_hash",issues);
        if (issues.Count>0 || lines.Count!=observation.Lines.Count) return;
        var expected=CanonicalTableVectorEvidence.BuildBands(observation);
        Check(reading.Bands is not null && CanonicalContractJson.SerializeCanonical(reading.Bands)==CanonicalContractJson.SerializeCanonical(expected),
            "physicalReading.bands","source_supported_band_assignment",issues);
        var assigned=expected.SelectMany(b=>b.OrderedLineIds).ToHashSet(StringComparer.Ordinal);
        Check(reading.UnassignedLineIds.SequenceEqual(observation.Lines.Where(l=>!assigned.Contains(l.LineId)).Select(l=>l.LineId)),
            "physicalReading.unassignedLineIds","source_supported_band_assignment",issues);
    }
}
