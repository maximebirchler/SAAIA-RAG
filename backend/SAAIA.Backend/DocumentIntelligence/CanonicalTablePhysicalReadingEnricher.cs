using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record TablePhysicalReadingSummary(int ObservationCount,int CandidateCount,int UnavailableCount,int RefusedCount);

/// <summary>Alternative physical bands supported by observed source rules. Never a source grid.</summary>
internal static class CanonicalTablePhysicalReadingEnricher
{
    internal static TablePhysicalReadingSummary Apply(CanonicalDocument document,PdfExtractionResult? extraction,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        var pages=extraction?.Pages.GroupBy(p=>p.PageNumber).ToDictionary(g=>g.Key,g=>g.ToArray());
        var observations=document.Pages.SelectMany(p=>p.TableRegionObservations?.Observations??[]).ToArray();
        var documentBytes=observations.Sum(o=>Bytes(o)+(long)(o.PublicationRawBytes?.Length??0));
        var candidates=0;var unavailable=0;var refused=0;
        foreach(var page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var observation in page.TableRegionObservations?.Observations??[])
            {
                ct.ThrowIfCancellationRequested();
                var before=Bytes(observation);var original=observation.PhysicalReading;
                var native=pages is not null && pages.TryGetValue(page.PageNumber,out var rows) && rows.Length==1
                    ? rows[0].NativeVectorObservation:null;
                var reading=Unknown(observation,"native_vector_observation_unavailable");
                observation.PhysicalReading=reading;
                if(native is not null)
                {
                    var reason=Bind(document,page,observation,native);
                    if(native.Frame is not null && reason is not ("native_vector_source_identity_mismatch" or "native_vector_page_geometry_mismatch"))
                        CopyFrame(reading,native);
                    if(reason=="native_vector_rule_geometry_or_identity_unavailable")
                    {reading.VectorObservationAvailability="unavailable";reading.VectorObservationReason=reason;}
                    if(reason is not null) reading.Reason=reason;
                    else if(native.Availability!="observed")
                        reading.Reason=native.Reason??"native_vector_observation_unavailable";
                    else
                    {
                        var frame=observation.CoordinateFrame;
                        var left=observation.Region.Points.Min(p=>p.X)*frame.PageWidthPoints;
                        var right=observation.Region.Points.Max(p=>p.X)*frame.PageWidthPoints;
                        var top=observation.Region.Points.Min(p=>p.Y)*frame.PageHeightPoints;
                        var bottom=observation.Region.Points.Max(p=>p.Y)*frame.PageHeightPoints;
                        var inRegion=native.Rules.Where(r=>r.Axis=="h" && Math.Max(r.Start,left)<Math.Min(r.End,right) && r.Coordinate>top && r.Coordinate<=bottom).ToArray();
                        var lower=inRegion.Length==0?(double?)null:inRegion.Max(r=>r.Coordinate);
                        var supports=inRegion.Where(r=>r.Coordinate==lower).OrderBy(r=>r.Start).ToArray();
                        if(supports.Length is >0 and <=CanonicalTableRegionObservationValidator.MaximumBands)
                        {
                            reading.VectorRules=supports.Select(r=>new CanonicalTableVectorRule{RuleId=r.RuleId,Axis=r.Axis,
                                Coordinate=r.Coordinate,Start=r.Start,End=r.End,Thickness=r.Thickness,PrimitiveIds=r.PrimitiveIds.ToList()}).ToList();
                            reading.Availability="candidate_only";
                            reading.Reason="observed_bottom_rule_spans_not_cells";
                            reading.Bands=CanonicalTableVectorEvidence.BuildBands(observation);
                            var assigned=reading.Bands.SelectMany(b=>b.OrderedLineIds).ToHashSet(StringComparer.Ordinal);
                            reading.UnassignedLineIds=observation.Lines.Where(l=>!assigned.Contains(l.LineId)).Select(l=>l.LineId).ToList();
                            reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation);
                        }
                        else reading.Reason=supports.Length>CanonicalTableRegionObservationValidator.MaximumBands
                            ?"physical_band_limit":"no_unique_bottom_rule_spans";
                    }
                }
                var after=Bytes(observation);
                if(after>CanonicalTableRegionObservationValidator.MaximumCanonicalBytes
                    || documentBytes-before+after>CanonicalTableRegionObservationValidator.MaximumDocumentBytes)
                {
                    // This optional channel cannot invalidate or truncate an admitted literal observation.
                    observation.PhysicalReading=Unknown(observation,"vector_proof_byte_limit");
                    if(Bytes(observation)>before) observation.PhysicalReading=original;
                    after=Bytes(observation);refused++;
                }
                else if(reading.Availability=="candidate_only") candidates++;
                else if(native?.Availability=="refused") refused++;
                else unavailable++;
                documentBytes=documentBytes-before+after;
                CanonicalTableRegionObservationValidator.ValidateOrThrow(observation);
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(observations.Length,candidates,unavailable,refused);
    }

    private static string? Bind(CanonicalDocument document,CanonicalPage page,CanonicalTableRegionObservation observation,PdfNativeVectorObservation native)
    {
        if(native.SourceSha256!=document.Source.Sha256 || native.SourceSha256!=observation.SourceSha256
            || native.PhysicalPage!=page.PageNumber || observation.PhysicalPage!=page.PageNumber)
            return "native_vector_source_identity_mismatch";
        if(page.Geometry.Unit!="pt" || page.Geometry.Width!=native.PageWidthPoints || page.Geometry.Height!=native.PageHeightPoints
            || observation.CoordinateFrame.PageWidthPoints!=native.PageWidthPoints
            || observation.CoordinateFrame.PageHeightPoints!=native.PageHeightPoints)
            return "native_vector_page_geometry_mismatch";
        if(!CanonicalTableVectorEvidence.IsRectangularRegion(observation.Region)) return "non_rectangular_observation_region";
        if(native.Frame is null || native.Frame.Availability!="available") return "native_coordinate_frame_unavailable";
        if(native.Frame.RotationDegrees!=0) return "unsupported_native_rotation";
        if(native.Frame.CropBoxLeft!=0 || native.Frame.CropBoxBottom!=0 || native.Frame.MediaBoxLeft!=0 || native.Frame.MediaBoxBottom!=0)
            return "unsupported_native_origin";
        if(native.Availability=="observed" && !ValidRules(native)) return "native_vector_rule_geometry_or_identity_unavailable";
        return null;
    }

    private static bool ValidRules(PdfNativeVectorObservation native)
    {
        if(native.Rules is null || native.Rules.Count>PdfNativeVectorGeometryObserver.MaximumCommandsPerPage) return false;
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var rule in native.Rules)
        {
            if(rule is null || string.IsNullOrWhiteSpace(rule.RuleId) || rule.RuleId.Length>200 || !ids.Add(rule.RuleId)
                || rule.Axis is not ("h" or "v") || !double.IsFinite(rule.Start) || !double.IsFinite(rule.End)
                || !double.IsFinite(rule.Coordinate) || !double.IsFinite(rule.Thickness) || rule.Thickness is <=0 or >2
                || rule.Start<0 || rule.End> (rule.Axis=="h"?native.PageWidthPoints:native.PageHeightPoints)
                || rule.Coordinate<0 || rule.Coordinate> (rule.Axis=="h"?native.PageHeightPoints:native.PageWidthPoints)
                || rule.End-rule.Start<10*rule.Thickness || rule.PrimitiveIds is not {Count:>0 and <=32}
                || rule.PrimitiveIds.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>200)
                || rule.PrimitiveIds.Distinct(StringComparer.Ordinal).Count()!=rule.PrimitiveIds.Count) return false;
        }
        return true;
    }

    private static CanonicalTablePhysicalReading Unknown(CanonicalTableRegionObservation observation,string reason)
        =>new(){Reason=reason,UnassignedLineIds=observation.Lines.Select(l=>l.LineId).ToList()};
    private static long Bytes(CanonicalTableRegionObservation observation)
        =>Encoding.UTF8.GetByteCount(CanonicalContractJson.Serialize(observation));
    private static void CopyFrame(CanonicalTablePhysicalReading reading,PdfNativeVectorObservation native)
    {
        reading.NativeFrameAvailability=native.Frame.Availability;
        reading.NativeFrameReason=native.Frame.Availability=="available"?null:"native_coordinate_frame_unavailable";
        reading.NativeRotationDegrees=native.Frame.RotationDegrees;
        reading.NativeCropBoxLeft=native.Frame.CropBoxLeft;reading.NativeCropBoxBottom=native.Frame.CropBoxBottom;
        reading.NativeMediaBoxLeft=native.Frame.MediaBoxLeft;reading.NativeMediaBoxBottom=native.Frame.MediaBoxBottom;
        reading.VectorObservationAvailability=native.Availability;reading.VectorObservationReason=native.Reason;
    }
}
