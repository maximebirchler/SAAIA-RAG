using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagExtractionQualitySignalCompletenessTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Api_quality_retains_ninth_source_warning_and_observes_complete_metadata()
    {
        var signals = Enumerable.Range(0, 8).Select(i => $"observed_info_{i}")
            .Append("canonical_table_native_region_coverage_unavailable").ToArray();
        var match = MatchFromPayload(JsonSerializer.SerializeToElement(signals));

        var quality = Project(match);
        var json = JsonSerializer.SerializeToElement(quality, Web);

        Assert.Contains("canonical_table_native_region_coverage_unavailable", quality.ChunkQualitySignals!);
        Assert.Equal(9, quality.ChunkQualitySignals!.Count);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");
        Assert.Equal("complete", metadata.GetProperty("status").GetString());
        Assert.Equal(0, metadata.GetProperty("omittedCount").GetInt32());
        Assert.False(metadata.GetProperty("truncated").GetBoolean());
        Assert.False(metadata.GetProperty("malformed").GetBoolean());
    }

    [Fact]
    public void Sql_signal_parser_does_not_silently_discard_the_ninth_warning()
    {
        var signals = Enumerable.Range(0, 8).Select(i => $"observed_info_{i}")
            .Append("spatial_anchor_page_only").ToArray();

        var parsed = RetrievalChannels.ParseExtractionQualitySignalsJson(JsonSerializer.Serialize(signals));

        Assert.Contains("spatial_anchor_page_only", parsed!);
        Assert.Equal(9, parsed!.Count);
    }

    [Fact]
    public void Api_quality_cap64_exposes_exact_omissions_and_survives_json_roundtrip()
    {
        var match = MatchFromPayload(JsonSerializer.SerializeToElement(
            Enumerable.Range(0, 70).Select(i => $"observed_warning_{i}").ToArray()));
        var projected = Project(match);
        var roundtrip = JsonSerializer.Deserialize<RagItemExtractionQualityDto>(
            JsonSerializer.Serialize(projected, Web), Web)!;
        var json = JsonSerializer.SerializeToElement(roundtrip, Web);

        Assert.Equal(64, roundtrip.ChunkQualitySignals!.Count);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");
        Assert.Equal("partial", metadata.GetProperty("status").GetString());
        Assert.Equal(6, metadata.GetProperty("omittedCount").GetInt32());
        Assert.True(metadata.GetProperty("truncated").GetBoolean());
        Assert.False(metadata.GetProperty("malformed").GetBoolean());
    }

    [Fact]
    public void Absent_signal_payload_does_not_claim_a_complete_empty_list()
    {
        var json = JsonSerializer.SerializeToElement(Project(MatchFromPayload(null)), Web);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");

        Assert.Equal("unavailable", metadata.GetProperty("status").GetString());
        AssertUnknown(metadata, "omittedCount");
        AssertUnknown(metadata, "truncated");
        AssertUnknown(metadata, "malformed");
    }

    [Fact]
    public void Explicit_empty_signal_array_is_observed_complete_not_unavailable()
    {
        var json = JsonSerializer.SerializeToElement(Project(MatchFromPayload(JsonSerializer.SerializeToElement(Array.Empty<string>()))), Web);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");

        Assert.Equal("complete", metadata.GetProperty("status").GetString());
        Assert.Equal(0, metadata.GetProperty("omittedCount").GetInt32());
        Assert.False(metadata.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("17")]
    public void Malformed_payload_is_not_disguised_as_complete_empty_metadata(string raw)
    {
        using var input = JsonDocument.Parse(raw);
        var json = JsonSerializer.SerializeToElement(Project(MatchFromPayload(input.RootElement)), Web);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");

        Assert.Equal("malformed", metadata.GetProperty("status").GetString());
        Assert.True(metadata.GetProperty("malformed").GetBoolean());
        AssertUnknown(metadata, "omittedCount");
    }

    [Fact]
    public void Mixed_payload_keeps_valid_warning_and_marks_invalid_entries()
    {
        using var input = JsonDocument.Parse("[\"native_geometry_unavailable\",17,null]");
        var quality = Project(MatchFromPayload(input.RootElement));
        var json = JsonSerializer.SerializeToElement(quality, Web);

        Assert.Contains("native_geometry_unavailable", quality.ChunkQualitySignals!);
        var metadata = json.GetProperty("chunkQualitySignalsMetadata");
        Assert.Equal("malformed", metadata.GetProperty("status").GetString());
        Assert.True(metadata.GetProperty("malformed").GetBoolean());
        AssertUnknown(metadata, "omittedCount");
    }

    [Fact]
    public void Merge_preserves_prior_truncation_and_never_resets_unknown_omissions_to_zero()
    {
        var previous = JsonSerializer.Deserialize<RagItemExtractionQualityDto>("""
            {"textStatus":"ok","signals":["prior_warning"],
             "signalsMetadata":{"status":"partial","omittedCount":5,"truncated":true,"malformed":false}}
            """, Web)!;
        var quality = Project(MatchFromPayload(null), previous);
        var json = JsonSerializer.SerializeToElement(quality, Web);
        var metadata = json.GetProperty("signalsMetadata");

        Assert.Contains("prior_warning", quality.Signals!);
        Assert.Equal("partial", metadata.GetProperty("status").GetString());
        Assert.True(metadata.GetProperty("truncated").GetBoolean());
        Assert.True(!metadata.TryGetProperty("omittedCount", out var omitted)
            || omitted.ValueKind == JsonValueKind.Null || omitted.GetInt32() >= 5);
    }

    [Fact]
    public void Record_copy_keeps_signal_observation_and_legacy_unobserved_list_stays_unknown()
    {
        var observed = MatchFromPayload(JsonSerializer.SerializeToElement(new[] { "observed_warning" }));
        var copied = observed with { Score = 0.7 };
        var observedJson = JsonSerializer.SerializeToElement(Project(copied), Web);
        Assert.Equal("complete", observedJson.GetProperty("chunkQualitySignalsMetadata").GetProperty("status").GetString());

        var legacy = MatchFromPayload(null) with { ExtractionQualitySignals = ["legacy_warning"] };
        var legacyJson = JsonSerializer.SerializeToElement(Project(legacy), Web);
        Assert.Equal("unavailable", legacyJson.GetProperty("chunkQualitySignalsMetadata").GetProperty("status").GetString());
        AssertUnknown(legacyJson.GetProperty("chunkQualitySignalsMetadata"), "omittedCount");
    }

    [Fact]
    public void Changing_a_known_record_signal_list_does_not_keep_false_complete_provenance()
    {
        var observed = MatchFromPayload(JsonSerializer.SerializeToElement(new[] { "observed_warning" }));
        var changed = observed with
        {
            ExtractionQualitySignals = observed.ExtractionQualitySignals!.Concat(["added_without_observation"]).ToArray()
        };
        var json = JsonSerializer.SerializeToElement(Project(changed), Web);

        Assert.Equal("malformed", json.GetProperty("chunkQualitySignalsMetadata").GetProperty("status").GetString());
        AssertUnknown(json.GetProperty("chunkQualitySignalsMetadata"), "omittedCount");
    }

    [Fact]
    public void Writer_source_quality_remains_separate_and_keeps_ninth_warning()
    {
        var signals = Enumerable.Range(0, 8).Select(i => $"observed_info_{i}")
            .Append("canonical_table_native_region_coverage_unavailable").ToArray();
        var quality = AdvancedAnalysisSourceQuality.FromMetadata("ok", "false", JsonSerializer.Serialize(signals))!;

        Assert.Contains("canonical_table_native_region_coverage_unavailable", quality.Signals);
        Assert.False(quality.MetadataTruncated);
        Assert.Equal(0, quality.SignalsOmittedCount);
        Assert.Equal("not_verified", quality.SemanticAccuracy);
    }

    private static void AssertUnknown(JsonElement json, string property)
        => Assert.True(!json.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null);

    private static RagItemExtractionQualityDto Project(RagMatch match, RagItemExtractionQualityDto? previous = null)
    {
        var lookup = new Dictionary<string, RagItemExtractionQualityDto>();
        if (previous is not null)
            lookup[RetrievalResultProjector.BuildExtractionQualityMatchKey(match)] = previous;
        return RetrievalResultProjector.ResolveExtractionQuality(match, lookup)!;
    }

    private static RagMatch MatchFromPayload(JsonElement? signals)
    {
        var payload = new Dictionary<string, object?>
        {
            ["doc_id"] = "11111111-2222-3333-4444-555555555555", ["doc_path"] = "fixture.pdf",
            ["chunk_id"] = "22222222-3333-4444-5555-666666666666", ["page_start"] = 1, ["page_end"] = 1,
            ["text"] = "Observed source content.", ["extraction_text_status"] = "ok"
        };
        if (signals is not null) payload["extraction_quality_signals"] = signals;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            result = new[] { new { id = "22222222-3333-4444-5555-666666666666", score = 0.9, payload } }
        }));
        return Assert.Single(QdrantClient.ParseSearchResults(json));
    }
}
