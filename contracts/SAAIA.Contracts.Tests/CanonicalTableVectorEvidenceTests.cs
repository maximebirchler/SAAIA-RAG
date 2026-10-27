using SAAIA.Contracts.DocumentIntelligence;
using Xunit;
namespace SAAIA.Contracts.Tests;
public sealed class CanonicalTableVectorEvidenceTests
{
    [Fact]
    public void Secondary_vector_proof_valid_candidate_preserves_literal_observation()
    {
        var observation = Candidate();
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation, true);
        Assert.Equal("Recorded limit -5 \u00b0C; do not open.", observation.Lines[0].Text);
    }
    [Theory]
    [InlineData("missing_rules")]
    [InlineData("hash")]
    [InlineData("frame")]
    [InlineData("rule_reference")]
    [InlineData("outside_band")]
    [InlineData("rule_endpoint")]
    public void Secondary_vector_proof_rejects_unlinked_or_unearned_candidate(string mutation)
    {
        var observation = Candidate(); var reading = observation.PhysicalReading;
        switch (mutation)
        {
            case "missing_rules": reading.VectorRules = null; break;
            case "hash": reading.VectorEvidenceSha256 = new string('0',64); break;
            case "frame": reading.NativeRotationDegrees = 90; break;
            case "rule_reference": reading.Bands![0].SupportingRuleIds = ["absent"]; break;
            case "outside_band": reading.Bands![0].Polygon.Points[2].Y = .36; reading.Bands[0].Polygon.Points[3].Y = .36; break;
            case "rule_endpoint": reading.VectorRules![0].End = .3; reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation); break;
        }
        Assert.Throws<CanonicalContractException>(()=>CanonicalTableRegionObservationValidator.ValidateOrThrow(observation,true));
    }
    [Fact]
    public void Secondary_vector_overlapping_physical_spans_leave_shared_line_unassigned_without_grid_claim()
    {
        var observation=Candidate();var reading=observation.PhysicalReading;
        reading.VectorRules=[new(){RuleId="left",Axis="h",Coordinate=421,Start=59.5,End=250,Thickness=.5,PrimitiveIds=["p0"]},
            new(){RuleId="right",Axis="h",Coordinate=421,Start=249.5,End=476,Thickness=.5,PrimitiveIds=["p1"]}];
        reading.Bands=CanonicalTableVectorEvidence.BuildBands(observation);
        reading.UnassignedLineIds=[observation.Lines[0].LineId];
        reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation);
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation,true);
        Assert.All(reading.Bands,b=>Assert.Empty(b.OrderedLineIds));Assert.False(reading.CellMembershipProven);
    }
    [Fact]
    public void Secondary_vector_proof_accepts_exact_intersection_but_keeps_source_span_unmodified()
    {
        var observation=Candidate();var reading=observation.PhysicalReading;
        reading.VectorRules![0].Start=59;reading.VectorRules[0].End=480;
        reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation);
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation,true);
        Assert.Equal(.1,reading.Bands![0].Polygon.Points.Min(p=>p.X));Assert.Equal(.8,reading.Bands[0].Polygon.Points.Max(p=>p.X));
        Assert.Equal(59,reading.VectorRules[0].Start);Assert.Equal(480,reading.VectorRules[0].End);
    }
    [Theory]
    [InlineData("unclipped_polygon")]
    [InlineData("source_outside_page")]
    [InlineData("zero_intersection")]
    [InlineData("raw_binding")]
    [InlineData("nonrectangular")]
    public void Secondary_vector_intersection_proof_rejects_unsupported_geometry_even_with_recomputed_hash(string mutation)
    {
        var observation=Candidate();var reading=observation.PhysicalReading;
        reading.VectorRules![0].Start=59;reading.VectorRules[0].End=480;
        if(mutation=="unclipped_polygon")
        {
            reading.Bands![0].Polygon=new(){Points=[new(){X=59d/595,Y=.3},new(){X=480d/595,Y=.3},
                new(){X=480d/595,Y=.5},new(){X=59d/595,Y=.5}]};
        }
        if(mutation=="source_outside_page"){reading.VectorRules[0].Start=-1;reading.VectorRules[0].End=600;}
        if(mutation=="zero_intersection"){reading.VectorRules[0].Start=0;reading.VectorRules[0].End=59.5;}
        if(mutation=="raw_binding"){observation.PublicationRawBytes=null;observation.RawObservationSha256=new string('0',64);}
        if(mutation=="nonrectangular") observation.Region.Points[1].Y=.31;
        if(mutation!="raw_binding") reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation);
        Assert.Throws<CanonicalContractException>(()=>CanonicalTableRegionObservationValidator.ValidateOrThrow(observation));
    }
    internal static CanonicalTableRegionObservation Candidate()
    {
        var observation = CanonicalTableRegionObservationFixture.Create();
        var reading = observation.PhysicalReading;
        reading.Availability="candidate_only"; reading.Reason=null;
        reading.NativeFrameAvailability="available"; reading.NativeRotationDegrees=0;
        reading.NativeCropBoxLeft=reading.NativeCropBoxBottom=reading.NativeMediaBoxLeft=reading.NativeMediaBoxBottom=0;
        reading.VectorObservationAvailability="observed";
        reading.VectorRules=[new(){RuleId="rule_0",Axis="h",Coordinate=842*.5,Start=595*.1,End=595*.8,Thickness=.5,PrimitiveIds=["path_0"]}];
        reading.Bands=[new(){BandOrdinal=0,Polygon=observation.Region,SupportingRuleIds=["rule_0"],OrderedLineIds=[observation.Lines[0].LineId]}];
        reading.UnassignedLineIds=[];
        reading.VectorEvidenceSha256=CanonicalTableVectorEvidence.ComputeSha256(observation);
        return observation;
    }
}
