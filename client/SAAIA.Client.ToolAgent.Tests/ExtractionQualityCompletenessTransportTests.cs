using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ExtractionQualityCompletenessTransportTests
{
    private static JsonObject Quality(int count = 12) => new()
    {
        ["textStatus"] = "ok",
        ["signals"] = new JsonArray(Enumerable.Range(1, count).Select(i => JsonValue.Create($"signal_{i}")).ToArray()),
        ["chunkQualitySignals"] = new JsonArray("canonical_native_glyph_geometry_unavailable"),
        ["signalsMetadata"] = Metadata("complete", 0, false),
        ["chunkQualitySignalsMetadata"] = Metadata("complete", 0, false)
    };

    private static JsonObject Metadata(string status, int? omitted, bool? truncated) => new()
    {
        ["status"] = status, ["omittedCount"] = omitted,
        ["truncated"] = truncated, ["malformed"] = status == "unavailable" ? null : false
    };

    private static JsonObject Hit(JsonObject quality) => new()
    {
        ["docPath"] = "fixtures/reference.pdf", ["docName"] = "reference.pdf",
        ["docId"] = "11111111-2222-3333-4444-555555555555",
        ["chunkId"] = "22222222-3333-4444-5555-666666666666",
        ["pageStart"] = 1, ["pageEnd"] = 1,
        ["text"] = "A grounded source measurement is recorded here.",
        ["extractionQuality"] = quality
    };

    private static JsonElement Normalize(JsonObject quality) =>
        ToolAgentOrchestrator.NormalizeRagHitsForTests(
            new JsonObject { ["items"] = new JsonArray(Hit(quality)) }.ToJsonString(), true)
            .GetProperty("hits")[0].GetProperty("extractionQuality");

    private static JsonElement Property(JsonElement value, string name) =>
        value.EnumerateObject().Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static object Invoke(string method, params object?[] args) =>
        typeof(ToolAgentOrchestrator).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;

    [Fact]
    public void Canonical_normalization_preserves_both_signal_lists_and_the_ninth_warning()
    {
        var quality = Normalize(Quality());
        Assert.Contains(quality.GetProperty("signals").EnumerateArray(), x => x.GetString() == "signal_12");
        Assert.Equal("canonical_native_glyph_geometry_unavailable", quality.GetProperty("chunkQualitySignals")[0].GetString());
        Assert.Equal("complete", quality.GetProperty("signalsMetadata").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("rag.search")]
    [InlineData("rag.query")]
    public void Writer_payload_preserves_warnings_and_upstream_partial_state(string tool)
    {
        var quality = Quality();
        quality["signalsMetadata"] = Metadata("partial", null, true);
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(
            new JsonObject { ["items"] = new JsonArray(Hit(quality)) }.ToJsonString(), true);
        var wire = ToolAgentOrchestrator.SerializeWriterRagResultsForTests(tool, normalized.GetRawText(), "Give the precise recorded measurement.");
        Assert.Contains("signal_12", wire);
        Assert.Contains("canonical_native_glyph_geometry_unavailable", wire);
        Assert.Contains("signalsMetadata", wire);
        Assert.Contains("partial", wire);
        Assert.DoesNotContain("\"omittedCount\":0", wire.Split("chunkQualitySignalsMetadata")[0]);
    }

    [Fact]
    public void Typed_contract_and_RagChatAgent_fallback_preserve_completeness()
    {
        var quality = Quality();
        quality["signalsMetadata"] = Metadata("partial", 3, true);
        var typed = JsonSerializer.Deserialize<RagItem>(Hit(quality).ToJsonString())!;
        var payload = typeof(RagChatAgent).GetMethod("BuildSearchOnlyFallbackSourcePayload", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [typed]);
        var result = JsonSerializer.SerializeToElement(payload);
        var meta = Property(Property(result, "extractionQuality"), "signalsMetadata");
        Assert.Equal("partial", Property(meta, "status").GetString());
        Assert.Equal(3, Property(meta, "omittedCount").GetInt32());
    }

    [Fact]
    public void Missing_legacy_metadata_is_unavailable_not_complete_zero()
    {
        var quality = Quality(1);
        quality.Remove("signalsMetadata");
        var result = Normalize(quality).GetProperty("signalsMetadata");
        Assert.Equal("unavailable", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("omittedCount").ValueKind);
    }

    [Fact]
    public void Explicit_empty_observation_remains_complete_zero()
    {
        var quality = Normalize(Quality(0));
        Assert.Empty(quality.GetProperty("signals").EnumerateArray());
        Assert.Equal("complete", quality.GetProperty("signalsMetadata").GetProperty("status").GetString());
        Assert.Equal(0, quality.GetProperty("signalsMetadata").GetProperty("omittedCount").GetInt32());
    }

    [Fact]
    public void Local_sixty_four_signal_limit_reports_exact_known_omissions()
    {
        var quality = Normalize(Quality(70));
        Assert.Equal(64, quality.GetProperty("signals").GetArrayLength());
        var meta = quality.GetProperty("signalsMetadata");
        Assert.Equal("partial", meta.GetProperty("status").GetString());
        Assert.Equal(6, meta.GetProperty("omittedCount").GetInt32());
        Assert.True(meta.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Malformed_signal_array_keeps_readable_warning_but_never_claims_complete()
    {
        var quality = Quality(0);
        quality["signals"] = new JsonArray("formula_recognition_uncertain", 42);
        var result = Normalize(quality);
        Assert.Equal("formula_recognition_uncertain", result.GetProperty("signals")[0].GetString());
        Assert.Equal("malformed", result.GetProperty("signalsMetadata").GetProperty("status").GetString());
    }

    [Fact]
    public void Source_memory_roundtrip_and_merge_keep_partial_state_without_summing_unknown_union()
    {
        ToolMemory.SourceRef Read(string signal)
        {
            var quality = Quality(0);
            quality["signals"] = new JsonArray(signal);
            quality["signalsMetadata"] = Metadata("partial", 4, true);
            return (ToolMemory.SourceRef)Invoke("TryBuildSourceRefFromJsonElement", JsonSerializer.SerializeToElement(Hit(quality)));
        }
        var merged = (ToolMemory.SourceRef)Invoke("MergeSourceRefGroup", new[] { Read("first"), Read("second") }, 5, false);
        var result = JsonSerializer.SerializeToElement(Invoke("BuildSourceExtractionQualityPayload", merged));
        var metadata = result.GetProperty("signalsMetadata");
        Assert.Equal("partial", metadata.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("omittedCount").ValueKind);
        Assert.Equal(2, result.GetProperty("signals").GetArrayLength());
    }

    [Fact]
    public void Source_card_parser_keeps_completeness_separate_from_display_limit()
    {
        var quality = Quality();
        quality["chunkQualitySignalsMetadata"] = Metadata("unavailable", null, null);
        var card = Assert.Single(SourceCardParser.Parse(new JsonObject { ["sources"] = new JsonArray(Hit(quality)) }.ToJsonString()));
        var value = JsonSerializer.SerializeToElement(card);
        Assert.Equal(12, card.QualitySignals.Count);
        Assert.Equal("unavailable", Property(Property(value, "chunkQualitySignalsMetadata"), "status").GetString());
    }

    private static EvidenceBundle BuildEvidence()
    {
        var quality = Quality();
        quality["signalsMetadata"] = Metadata("partial", null, true);
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(
            new JsonObject { ["items"] = new JsonArray(Hit(quality)) }.ToJsonString(), true);
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        return EvidenceBundleBuilder.FromToolResults(tools, "What measurement is recorded?");
    }

    [Fact]
    public void Source_backed_evidence_to_final_writer_transports_concrete_warning_and_partial_state()
    {
        var item = Assert.Single(BuildEvidence().Items);
        var context = new System.Text.StringBuilder();
        typeof(SourceBackedAgentV2Runner).GetMethod("AppendCitableEvidenceWriterBlock", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [context, item, 1200]);
        var wire = context.ToString();
        Assert.Contains("canonical_native_glyph_geometry_unavailable", wire);
        Assert.Contains("signal_12", wire);
        Assert.Contains("partial", wire);
    }

    [Fact]
    public void Source_backed_verified_UI_mapping_keeps_full_quality_for_next_turn()
    {
        var item = Assert.Single(BuildEvidence().Items);
        var source = (ToolMemory.SourceRef)typeof(SourceBackedUiPayloadMapper).GetMethod("ToSourceRef", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [item])!;
        var value = JsonSerializer.SerializeToElement(Invoke("BuildSourceExtractionQualityPayload", source));
        Assert.Equal("partial", value.GetProperty("signalsMetadata").GetProperty("status").GetString());
        Assert.Contains(value.GetProperty("signals").EnumerateArray(), code => code.GetString() == "signal_12");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("false")]
    [InlineData("{\"status\":\"complete\"}")]
    [InlineData("{\"status\":\"partial\",\"truncated\":false}")]
    public void Invalid_metadata_is_not_promoted_to_complete(string metadata)
    {
        var quality = Quality();
        quality["signalsMetadata"] = JsonNode.Parse(metadata);
        var result = Normalize(quality);
        Assert.Equal("malformed", result.GetProperty("signalsMetadata").GetProperty("status").GetString());
        Assert.Contains(result.GetProperty("signals").EnumerateArray(), code => code.GetString() == "signal_12");
    }

    [Fact]
    public void Repeated_normalization_and_budgeted_writer_do_not_reset_upstream_partial_state()
    {
        var quality = Quality();
        quality["signalsMetadata"] = Metadata("partial", null, true);
        var raw = new JsonObject { ["items"] = new JsonArray(Hit(quality)) }.ToJsonString();
        var once = ToolAgentOrchestrator.NormalizeRagHitsForTests(raw, true);
        var twice = ToolAgentOrchestrator.NormalizeRagHitsForTests(once.GetRawText(), true);
        var wire = ToolAgentOrchestrator.SerializeBudgetedWriterRagResultsForTests(
            "rag.search", twice.GetRawText(), "Give the precise recorded measurement.", 8192);
        Assert.Contains("signal_12", wire);
        Assert.Contains("canonical_native_glyph_geometry_unavailable", wire);
        Assert.Contains("partial", wire);
        Assert.Equal(JsonValueKind.Null, twice.GetProperty("hits")[0].GetProperty("extractionQuality")
            .GetProperty("signalsMetadata").GetProperty("omittedCount").ValueKind);
    }

    [Fact]
    public void Source_backed_fast_review_and_semantic_review_keep_quality_in_constrained_context()
    {
        var item = Assert.Single(BuildEvidence().Items);
        var metadata = typeof(SourceBackedAgentV2Runner).GetMethod("BuildFastEvidenceReviewMetadata", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fast = (string)metadata.Invoke(null, [item, false])!;
        Assert.Contains("signal_12", fast);
        Assert.Contains("partial", fast);
        var context = new System.Text.StringBuilder();
        typeof(SourceBackedAgentV2Runner).GetMethod("AppendSemanticReviewEvidence", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [context, item, 100, true]);
        Assert.Contains("canonical_native_glyph_geometry_unavailable", context.ToString());
        Assert.Contains("partial", context.ToString());
    }

    [Fact]
    public void Source_card_merge_counts_only_locally_known_omissions()
    {
        var first = Quality(40);
        var second = Quality(40);
        second["signals"] = new JsonArray(Enumerable.Range(41, 40).Select(i => JsonValue.Create($"signal_{i}")).ToArray());
        var cards = SourceCardParser.Parse(new JsonObject { ["sources"] = new JsonArray(Hit(first), Hit(second)) }.ToJsonString());
        var card = Assert.Single(cards);
        Assert.Equal(64, card.QualitySignals.Count);
        var metadata = Property(JsonSerializer.SerializeToElement(card), "qualitySignalsMetadata");
        Assert.Equal("partial", Property(metadata, "status").GetString());
        Assert.Equal(16, Property(metadata, "omittedCount").GetInt32());
    }

    [Fact]
    public void Source_backed_dedup_keeps_selected_observation_status_and_unions_warnings()
    {
        var first = Hit(Quality(1));
        var secondQuality = Quality(0);
        secondQuality["textStatus"] = "low_text";
        secondQuality["signals"] = new JsonArray("formula_recognition_uncertain");
        var second = Hit(secondQuality);
        second["selectionHints"] = new JsonObject { ["hasGroundedEvidence"] = true };
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(
            new JsonObject { ["hits"] = new JsonArray(first, second) }) });
        var item = Assert.Single(EvidenceBundleBuilder.FromToolResults(tools, "What measurement is recorded?").Items);
        Assert.True(bool.Parse(item.SelectionHints["hasGroundedEvidence"]));
        var quality = Assert.IsType<RagItemExtractionQuality>(item.ExtractionQualityDetails);
        Assert.Equal("low_text", quality.TextStatus);
        Assert.Contains("signal_1", quality.Signals!);
        Assert.Contains("formula_recognition_uncertain", quality.Signals!);
    }

    [Fact]
    public void Typed_missing_list_cannot_claim_complete_zero_from_metadata_alone()
    {
        var quality = Quality();
        quality["signals"] = null;
        var typed = JsonSerializer.Deserialize<RagItem>(Hit(quality).ToJsonString())!;
        var payload = typeof(RagChatAgent).GetMethod("BuildSearchOnlyFallbackSourcePayload", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [typed]);
        var metadata = Property(Property(JsonSerializer.SerializeToElement(payload), "extractionQuality"), "signalsMetadata");
        Assert.Equal("malformed", Property(metadata, "status").GetString());
        Assert.Equal(JsonValueKind.Null, Property(metadata, "omittedCount").ValueKind);
    }

    [Fact]
    public void Unknown_selected_quality_does_not_borrow_clean_status_from_another_observation()
    {
        var known = JsonSerializer.Deserialize<RagItemExtractionQuality>(Quality().ToJsonString())!;
        var merged = ExtractionQualitySignalTransport.MergeQualities(new[] { known, null }, selected: null)!;
        Assert.Null(merged.TextStatus);
        Assert.Contains("signal_12", merged.Signals!);
        Assert.Equal("unavailable", merged.SignalsMetadata!.Status);
        Assert.Null(merged.SignalsMetadata.OmittedCount);
    }
}
