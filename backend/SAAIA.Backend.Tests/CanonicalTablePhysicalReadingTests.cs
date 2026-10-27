using SAAIA.Contracts.DocumentIntelligence;
using Xunit;
using Fixture=SAAIA.Contracts.Tests.CanonicalTableRegionObservationFixture;
namespace SAAIA.Backend.Tests;
public sealed class CanonicalTablePhysicalReadingTests
{
    [Fact]
    public void Secondary_bands_use_only_source_bound_bottom_rules_and_keep_primary_and_raw_immutable()
    {
        var doc=Fixture.Attach();var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        var before=CanonicalContractJson.Serialize(doc.Pages[0].Tables);var raw=obs.PublicationRawBytes!.ToArray();
        var result=CanonicalTablePhysicalReadingEnricher.Apply(doc,Native(doc));
        Assert.Equal(1,result.CandidateCount);var band=Assert.Single(obs.PhysicalReading.Bands!);
        Assert.Equal(obs.Lines[0].LineId,Assert.Single(band.OrderedLineIds));
        Assert.Equal(CanonicalTableVectorEvidence.ComputeSha256(obs),obs.PhysicalReading.VectorEvidenceSha256);
        Assert.Equal(before,CanonicalContractJson.Serialize(doc.Pages[0].Tables));Assert.Equal(raw,obs.PublicationRawBytes);
        CanonicalContractValidator.ValidateOrThrow(doc);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("partial")]
    [InlineData("frame")]
    public void Secondary_bands_refuse_unbound_or_incomplete_native_evidence_without_losing_lines(string mutation)
    {
        var doc=Fixture.Attach();var native=Native(doc);var page=native.Pages[0];var vector=page.NativeVectorObservation!;
        vector=mutation switch {"source"=>vector with {SourceSha256=new string('0',64)},
            "partial"=>vector with {Availability="partial",Reason="unsupported_native_vector_geometry"},
            _=>vector with {Frame=new("available",90,0,0,0,0)}};
        native=native with{Pages=[page with{NativeVectorObservation=vector}]};
        var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        CanonicalTablePhysicalReadingEnricher.Apply(doc,native);
        Assert.Equal("unavailable",obs.PhysicalReading.Availability);Assert.Null(obs.PhysicalReading.Bands);
        Assert.NotEqual("no_continuous_vertical_walls",obs.PhysicalReading.Reason);
        Assert.Equal(obs.Lines.Select(l=>l.LineId),obs.PhysicalReading.UnassignedLineIds);
    }
    [Theory]
    [InlineData(300d)]
    [InlineData(249.5d)]
    public void Secondary_bands_shared_full_width_line_stays_unassigned_and_no_cell_relation_is_asserted(double secondStart)
    {
        var doc=Fixture.Attach();var native=Native(doc);var page=native.Pages[0];var vector=page.NativeVectorObservation!;
        var left=new CanonicalTableVectorRule {RuleId="left",Axis="h",Coordinate=421,Start=59.5,End=250,Thickness=.5,PrimitiveIds=["p0"]};
        var right=new CanonicalTableVectorRule {RuleId="right",Axis="h",Coordinate=421,Start=secondStart,End=476,Thickness=.5,PrimitiveIds=["p1"]};
        native=native with{Pages=[page with{NativeVectorObservation=vector with{Rules=[left,right]}}]};
        CanonicalTablePhysicalReadingEnricher.Apply(doc,native);
        var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        Assert.Equal("candidate_only",obs.PhysicalReading.Availability);
        Assert.Equal(2,obs.PhysicalReading.Bands!.Count);Assert.All(obs.PhysicalReading.Bands,b=>Assert.Empty(b.OrderedLineIds));
        Assert.Equal(obs.Lines[0].LineId,Assert.Single(obs.PhysicalReading.UnassignedLineIds));
        Assert.False(obs.PhysicalReading.CellMembershipProven);Assert.False(obs.PhysicalReading.SemanticParentLinksAsserted);
    }
    [Fact]
    public void Secondary_bands_cancellation_and_absent_native_evidence_preserve_every_literal_line()
    {
        var doc=Fixture.Attach();using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>CanonicalTablePhysicalReadingEnricher.Apply(doc,Native(doc),cancellation.Token));
        CanonicalTablePhysicalReadingEnricher.Apply(doc,null);
        var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        Assert.Equal("native_vector_observation_unavailable",obs.PhysicalReading.Reason);
        Assert.Null(obs.PhysicalReading.Bands);Assert.Equal(obs.Lines.Select(l=>l.LineId),obs.PhysicalReading.UnassignedLineIds);
    }
    [Fact]
    public void Secondary_bands_clip_only_the_candidate_span_and_preserve_the_literal_source_rule()
    {
        var doc=Fixture.Attach();var native=Native(doc);var page=native.Pages[0];var vector=page.NativeVectorObservation!;
        var rule=new CanonicalTableVectorRule{RuleId="extended",Axis="h",Coordinate=421,Start=59,End=480,Thickness=.5,PrimitiveIds=["p0"]};
        native=native with{Pages=[page with{NativeVectorObservation=vector with{Rules=[rule]}}]};
        var before=CanonicalContractJson.Serialize(native.Pages[0].NativeVectorObservation);
        CanonicalTablePhysicalReadingEnricher.Apply(doc,native);
        var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        Assert.Equal("candidate_only",obs.PhysicalReading.Availability);
        var band=Assert.Single(obs.PhysicalReading.Bands!);
        Assert.Equal(.1,band.Polygon.Points.Min(p=>p.X));Assert.Equal(.8,band.Polygon.Points.Max(p=>p.X));
        Assert.Equal(59,Assert.Single(obs.PhysicalReading.VectorRules!).Start);Assert.Equal(480,obs.PhysicalReading.VectorRules![0].End);
        Assert.Equal(before,CanonicalContractJson.Serialize(native.Pages[0].NativeVectorObservation));
        Assert.Equal(obs.Lines[0].LineId,Assert.Single(band.OrderedLineIds));
    }
    [Theory]
    [InlineData("nonrectangular")]
    [InlineData("outside_page")]
    [InlineData("zero_intersection")]
    [InlineData("duplicate_rule_id")]
    public void Secondary_bands_invalid_or_unintersecting_evidence_refuses_only_optional_physical_view(string mutation)
    {
        var doc=Fixture.Attach();var native=Native(doc);var page=native.Pages[0];var vector=page.NativeVectorObservation!;
        var obs=doc.Pages[0].TableRegionObservations!.Observations![0];
        if(mutation=="nonrectangular") obs.Region.Points[1].Y=.31;
        if(mutation=="outside_page") vector=vector with{Rules=[new(){RuleId="outside",Axis="h",Coordinate=421,Start=-1,End=600,Thickness=.5,PrimitiveIds=["p0"]}]};
        if(mutation=="zero_intersection") vector=vector with{Rules=[new(){RuleId="touch",Axis="h",Coordinate=421,Start=0,End=59.5,Thickness=.5,PrimitiveIds=["p0"]}]};
        if(mutation=="duplicate_rule_id") vector=vector with{Rules=[vector.Rules[0],vector.Rules[0]]};
        native=native with{Pages=[page with{NativeVectorObservation=vector}]};
        var raw=obs.PublicationRawBytes!.ToArray();
        CanonicalTablePhysicalReadingEnricher.Apply(doc,native);
        Assert.Equal("unavailable",obs.PhysicalReading.Availability);Assert.Null(obs.PhysicalReading.Bands);
        Assert.Equal(obs.Lines.Select(l=>l.LineId),obs.PhysicalReading.UnassignedLineIds);Assert.Equal(raw,obs.PublicationRawBytes);
    }
    internal static PdfExtractionResult Native(CanonicalDocument doc)
    {
        var vector=new PdfNativeVectorObservation(doc.Source.Sha256,1,595,842,new("available",0,0,0,0,0),"observed",null,
            [new(){RuleId="bottom",Axis="h",Coordinate=421,Start=59.5,End=476,Thickness=.5,PrimitiveIds=["p0"]}],5);
        var page=new ExtractedPdfPage(1,"",0,0,[],WidthPoints:595,HeightPoints:842,NativeVectorObservation:vector);
        return new PdfExtractionResult([], [page],PdfExtractionQualitySummary.FromPages([page]));
    }
}
