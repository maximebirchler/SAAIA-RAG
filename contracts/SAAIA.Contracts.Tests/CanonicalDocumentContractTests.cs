using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalDocumentContractTests
{
    [Fact]
    public void Document_round_trips_with_stable_camel_case_schema()
    {
        var document = CanonicalContractFixture.CreateDocument();

        var json = CanonicalContractJson.Serialize(document);
        var restored = CanonicalContractJson.Deserialize<CanonicalDocument>(json);

        Assert.Contains("\"schemaVersion\":\"canonical_document_v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"manifestSha256\":", json, StringComparison.Ordinal);
        Assert.Equal(document.DocumentId, restored.DocumentId);
        Assert.Equal(CanonicalContractFixture.SpanId, restored.Pages[0].Blocks[0].Spans[0].SpanId);
        Assert.Empty(CanonicalContractValidator.Validate(restored));
    }

    [Fact]
    public void Validator_rejects_out_of_bounds_geometry_and_broken_references()
    {
        var document = CanonicalContractFixture.CreateDocument();
        document.Pages[0].Blocks[0].Polygon!.Points[0].X = 1.1;
        document.Pages[0].Tables[0].CaptionBlockIds = ["missing_block"];

        var issues = CanonicalContractValidator.Validate(document);

        Assert.Contains(issues, issue => issue.Code == "normalized_coordinate");
        Assert.Contains(issues, issue => issue.Code == "broken_reference");
    }

    [Fact]
    public void Stable_id_is_deterministic_and_length_delimited()
    {
        var left = CanonicalStableId.Create("block", "ab", "c");
        var same = CanonicalStableId.Create("block", "ab", "c");
        var ambiguousWithoutLengths = CanonicalStableId.Create("block", "a", "bc");

        Assert.Equal(left, same);
        Assert.NotEqual(left, ambiguousWithoutLengths);
        Assert.StartsWith("block_", left, StringComparison.Ordinal);
    }
}
