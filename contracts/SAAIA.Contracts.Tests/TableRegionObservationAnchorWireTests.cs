using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class TableRegionObservationAnchorWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("tableRegionObservationId")]
    [InlineData("tableRegionObservationLineIds")]
    public void Secondary_observation_anchor_region_preserves_its_exact_typed_identity(string property)
    {
        const string input = """
            {"pageNumber":3,"blockIds":[],"spanIds":[],"tableCellIds":[],
             "rawTextSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "tableRegionObservationId":"observation-source-page-3",
             "tableRegionObservationLineIds":["line-0002","line-0003"]}
            """;
        var region = JsonSerializer.Deserialize<SourceAnchorRegion>(input, Web)!;
        var actual = JsonSerializer.SerializeToElement(region, Web);
        using var expected = JsonDocument.Parse(input);

        Assert.Equal(expected.RootElement.GetProperty(property).GetRawText(), actual.GetProperty(property).GetRawText());
    }

    [Fact]
    public void Legacy_region_serialized_json_is_byte_identical_when_secondary_observation_is_absent()
    {
        var region = new SourceAnchorRegion { PageNumber = 3, RawTextSha256 = new string('a', 64) };
        const string expected = """{"pageNumber":3,"blockIds":[],"spanIds":[],"tableCellIds":[],"rawTextSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";

        Assert.Equal(expected, JsonSerializer.Serialize(region, Web));
    }
}
