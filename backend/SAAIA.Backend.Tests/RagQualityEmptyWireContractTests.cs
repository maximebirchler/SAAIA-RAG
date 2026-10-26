using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagQualityEmptyWireContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("signals", "signalsMetadata")]
    [InlineData("chunkQualitySignals", "chunkQualitySignalsMetadata")]
    public void Known_empty_projection_emits_an_array_and_survives_the_client_contract(string list, string metadata)
    {
        var projected = Project(knownEmpty: true);
        var wire = JsonSerializer.SerializeToElement(projected, Web);
        Assert.Equal(JsonValueKind.Array, wire.GetProperty(list).ValueKind);
        Assert.Empty(wire.GetProperty(list).EnumerateArray());
        Assert.Equal("complete", wire.GetProperty(metadata).GetProperty("status").GetString());
        Assert.Equal(0, wire.GetProperty(metadata).GetProperty("omittedCount").GetInt32());
        var client = JsonSerializer.Deserialize<RagItemExtractionQuality>(wire.GetRawText(), Web)!;
        Assert.NotNull(list == "signals" ? client.Signals : client.ChunkQualitySignals);
        var roundtrip = JsonSerializer.SerializeToElement(client, Web);
        Assert.Equal(JsonValueKind.Array, roundtrip.GetProperty(list).ValueKind);
        Assert.Empty(roundtrip.GetProperty(list).EnumerateArray());
    }

    [Theory]
    [InlineData("signals", "signalsMetadata")]
    [InlineData("chunkQualitySignals", "chunkQualitySignalsMetadata")]
    public void Unknown_projection_does_not_fabricate_an_empty_observation(string list, string metadata)
    {
        var wire = JsonSerializer.SerializeToElement(Project(knownEmpty: false), Web);
        Assert.False(wire.TryGetProperty(list, out _));
        Assert.Equal("unavailable", wire.GetProperty(metadata).GetProperty("status").GetString());
        Assert.False(wire.GetProperty(metadata).TryGetProperty("omittedCount", out _));
        var client = JsonSerializer.Deserialize<RagItemExtractionQuality>(wire.GetRawText(), Web)!;
        Assert.Null(list == "signals" ? client.Signals : client.ChunkQualitySignals);
    }

    [Theory]
    [InlineData("partial", "signals", "signalsMetadata")]
    [InlineData("partial", "chunkQualitySignals", "chunkQualitySignalsMetadata")]
    [InlineData("malformed", "signals", "signalsMetadata")]
    [InlineData("malformed", "chunkQualitySignals", "chunkQualitySignalsMetadata")]
    public void Empty_retained_partial_or_malformed_observation_keeps_array_without_claiming_complete(
        string status, string list, string metadata)
    {
        var observation = new RagQualitySignalsObservation([], status == "partial"
            ? new(status, Truncated: true, Malformed: false)
            : new(status, Malformed: true));
        var wire = JsonSerializer.SerializeToElement(Project(observation), Web);

        Assert.Equal(JsonValueKind.Array, wire.GetProperty(list).ValueKind);
        Assert.Empty(wire.GetProperty(list).EnumerateArray());
        Assert.Equal(status, wire.GetProperty(metadata).GetProperty("status").GetString());
        Assert.False(wire.GetProperty(metadata).TryGetProperty("omittedCount", out _));
        var client = JsonSerializer.Deserialize<RagItemExtractionQuality>(wire.GetRawText(), Web)!;
        Assert.NotNull(list == "signals" ? client.Signals : client.ChunkQualitySignals);
        Assert.Equal(status, (list == "signals" ? client.SignalsMetadata : client.ChunkQualitySignalsMetadata)!.Status);
    }

    [Fact]
    public void No_quality_data_keeps_the_whole_quality_object_absent()
        => Assert.Null(Project(RetrievalResultProjector.ObserveExtractionQualitySignalsJson(null), textStatus: null));

    private static RagItemExtractionQualityDto Project(bool knownEmpty)
        => Project(RetrievalResultProjector.ObserveExtractionQualitySignalsJson(knownEmpty ? "[]" : null))!;

    private static RagItemExtractionQualityDto? Project(RagQualitySignalsObservation observed, string? textStatus = "ok")
    {
        var match = new RagMatch(
            DocId: "11111111-2222-3333-4444-555555555555", DocPath: "fixtures/reference.pdf",
            DocName: "reference.pdf", PageStart: 1, PageEnd: 1, Text: "Grounded source text.", Score: 1,
            ChunkId: "22222222-3333-4444-5555-666666666666", ChunkIndex: 0,
            IngestionVersion: 1, HashDoc: null, EmbedText: null, EmbeddingBasis: null,
            SectionOrdinal: null, UnitOrdinal: null, SectionTitle: null, HeadingPath: null,
            ChunkType: null, PrevChunkId: null, NextChunkId: null, SameSectionChunkId: null,
            ExtractionTextStatus: textStatus, ExtractionQualitySignals: observed.Signals,
            ExtractionQualityObservation: observed);
        return RetrievalResultProjector.ResolveExtractionQuality(match,
            new Dictionary<string, RagItemExtractionQualityDto>())!;
    }
}
