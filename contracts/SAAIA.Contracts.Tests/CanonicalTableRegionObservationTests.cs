using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalTableRegionObservationTests
{
    [Fact]
    public void Secondary_table_valid_literal_observation_preserves_legacy_null_bytes_and_raw_transport_exclusion()
    {
        var legacy = CanonicalContractFixture.CreateDocument();
        Assert.DoesNotContain("tableRegionObservations", CanonicalContractJson.Serialize(legacy));
        var document = CanonicalTableRegionObservationFixture.Attach();
        var observation = Assert.Single(document.Pages[0].TableRegionObservations!.Observations!);
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation, requireRawObservation: true);
        Assert.Empty(CanonicalContractValidator.Validate(document));
        var json = CanonicalContractJson.Serialize(observation);
        Assert.DoesNotContain("publicationRawBytes", json);
        var restored = CanonicalContractJson.Deserialize<CanonicalTableRegionObservation>(json);
        Assert.Null(restored.PublicationRawBytes);
        Assert.Equal(observation.Lines[0].Text, restored.Lines[0].Text);
        Assert.Empty(CanonicalTableRegionObservationValidator.Validate(restored));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("raw")]
    [InlineData("polygon")]
    [InlineData("confidence")]
    [InlineData("frame")]
    [InlineData("semantics")]
    [InlineData("line_conservation")]
    [InlineData("missing_raw")]
    public void Secondary_table_rejects_changed_literal_geometry_raw_and_unearned_semantics(string mutation)
    {
        var observation = CanonicalTableRegionObservationFixture.Create();
        switch (mutation)
        {
            case "text": observation.Lines[0].Text = "Recorded limit 5 °C; do open."; break;
            case "raw": observation.PublicationRawBytes![0] ^= 1; break;
            case "polygon": observation.Lines[0].Polygon.Points[0].X = .9; break;
            case "confidence": observation.Lines[0].Confidence = double.NaN; break;
            case "frame": observation.CoordinateFrame.PageWidthPoints = double.PositiveInfinity; break;
            case "semantics": observation.CanonicalGridRepaired = true; break;
            case "line_conservation": observation.PhysicalReading.UnassignedLineIds = []; break;
            case "missing_raw": observation.PublicationRawBytes = null; break;
        }
        Assert.Throws<CanonicalContractException>(() =>
            CanonicalTableRegionObservationValidator.ValidateOrThrow(observation, requireRawObservation: true));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("table")]
    [InlineData("page")]
    [InlineData("source")]
    [InlineData("frame")]
    [InlineData("attempt")]
    public void Secondary_table_document_binding_and_completed_attempt_must_match(string mutation)
    {
        var document = CanonicalTableRegionObservationFixture.Attach();
        var set = document.Pages[0].TableRegionObservations!;
        var observation = set.Observations![0];
        switch (mutation)
        {
            case "document": observation.DocumentId = Guid.NewGuid(); break;
            case "revision": observation.RevisionId = Guid.NewGuid(); break;
            case "table": observation.TableId = "foreign_table"; break;
            case "page": observation.PhysicalPage = 2; break;
            case "source": observation.SourceSha256 = new('f', 64); break;
            case "frame": observation.CoordinateFrame.PageHeightPoints += 1; break;
            case "attempt": set.Attempts![0].RawObservationSha256 = new('f', 64); break;
        }
        Assert.NotEmpty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Secondary_table_disabled_partial_and_unavailable_are_not_fabricated_observed_empty()
    {
        var document = CanonicalContractFixture.CreateDocument();
        document.Pages[0].TableRegionObservations = new() { Availability = "unavailable", Observations = [], Attempts = [] };
        Assert.NotEmpty(CanonicalContractValidator.Validate(document));
    }
}
