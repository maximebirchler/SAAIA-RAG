using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ExtractionQualityAliasTransportTests
{
    [Theory]
    [InlineData("quality_signals", "signals_metadata", false)]
    [InlineData("chunk_quality_signals", "chunk_quality_signals_metadata", true)]
    [InlineData("qualitySignals", "qualitySignalsMetadata", false)]
    public void Supported_top_level_alias_survives_actual_evidence_builder(string signalsName, string metadataName, bool chunk)
    {
        var source = Hit();
        source[signalsName] = new JsonArray("observed_block_type_formula");
        source[metadataName] = Metadata("partial", true);
        var details = Assert.IsType<SAAIA.Contracts.RagItemExtractionQuality>(Build(source).ExtractionQualityDetails);
        Assert.Contains("observed_block_type_formula", chunk ? details.ChunkQualitySignals! : details.Signals!);
        var metadata = chunk ? details.ChunkQualitySignalsMetadata : details.SignalsMetadata;
        Assert.Equal("partial", metadata!.Status);
        Assert.Null(metadata.OmittedCount);
    }

    [Theory]
    [InlineData("signals_metadata", false)]
    [InlineData("chunkQualitySignalsMetadata", true)]
    public void Metadata_without_observed_array_is_retained_as_malformed(string name, bool chunk)
    {
        var source = Hit();
        source[name] = Metadata("partial", true);
        var details = Assert.IsType<SAAIA.Contracts.RagItemExtractionQuality>(Build(source).ExtractionQualityDetails);
        var metadata = chunk ? details.ChunkQualitySignalsMetadata : details.SignalsMetadata;
        Assert.Equal("malformed", metadata!.Status);
        Assert.Null(metadata.OmittedCount);
    }

    [Fact]
    public void Nested_snake_case_quality_remains_supported()
    {
        var source = Hit();
        source["extraction_quality"] = new JsonObject
        {
            ["chunk_quality_signals"] = new JsonArray("structured_transcription_not_verified"),
            ["chunk_quality_signals_metadata"] = Metadata("partial", true)
        };
        var details = Assert.IsType<SAAIA.Contracts.RagItemExtractionQuality>(Build(source).ExtractionQualityDetails);
        Assert.Contains("structured_transcription_not_verified", details.ChunkQualitySignals!);
        Assert.Equal("partial", details.ChunkQualitySignalsMetadata!.Status);
    }

    [Fact]
    public void Partial_metadata_cannot_claim_zero_omissions()
    {
        var result = SAAIA.Client.WinUI.Services.ExtractionQualitySignalTransport.Bound(
            new[] { "formula_recognition_uncertain" }, new SAAIA.Contracts.RagQualitySignalsMetadata("partial", 0, true, false));
        Assert.Equal("malformed", result.Metadata.Status);
        Assert.Null(result.Metadata.OmittedCount);
    }

    private static JsonObject Hit() => new()
    {
        ["docPath"] = "fixtures/reference.pdf", ["pageStart"] = 1, ["pageEnd"] = 1,
        ["text"] = "Grounded source text."
    };

    private static JsonObject Metadata(string status, bool truncated) => new()
    {
        ["status"] = status, ["omittedCount"] = null, ["truncated"] = truncated, ["malformed"] = false
    };

    private static EvidenceItem Build(JsonObject source)
    {
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(
            new JsonObject { ["hits"] = new JsonArray(source) }) });
        return Assert.Single(EvidenceBundleBuilder.FromToolResults(tools, "What is recorded?").Items);
    }
}
