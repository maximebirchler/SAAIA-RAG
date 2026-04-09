using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionJobPayloadJsonTests
{
    [Fact]
    public void Serialize_and_parse_roundtrip_preserves_doc_id_version_and_source()
    {
        var docId = Guid.NewGuid();

        var json = IngestionJobPayloadJson.Serialize(docId, 7, "ADMIN");
        var parsed = IngestionJobPayloadJson.Parse(json);

        Assert.Equal(docId, parsed.DocId);
        Assert.Equal(7, parsed.Version);
        Assert.Equal("admin", parsed.Source);
    }

    [Fact]
    public void Serialize_omits_source_when_missing()
    {
        var json = IngestionJobPayloadJson.Serialize(Guid.NewGuid(), 3);

        Assert.DoesNotContain("\"source\"", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not-json}")]
    public void Parse_returns_empty_payload_for_invalid_input(string? raw)
    {
        var parsed = IngestionJobPayloadJson.Parse(raw);

        Assert.Null(parsed.DocId);
        Assert.Null(parsed.Version);
        Assert.Null(parsed.Source);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("ADMIN", "admin")]
    [InlineData(" Manual ", "manual")]
    public void NormalizeSource_applies_expected_rules(string? raw, string? expected)
    {
        var normalized = IngestionJobPayloadJson.NormalizeSource(raw);

        Assert.Equal(expected, normalized);
    }
}
