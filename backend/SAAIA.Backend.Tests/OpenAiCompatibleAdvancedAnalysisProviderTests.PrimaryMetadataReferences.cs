using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static JsonObject PrimaryMetadataQuality(string warning = "observed_warning") => new()
    {
        ["textStatus"] = "extracted", ["semanticAccuracy"] = "not_verified",
        ["signals"] = new JsonArray(Enumerable.Range(0, 8).Select(index =>
            JsonValue.Create(warning + "_" + index + "_" + new string('x', 40))).ToArray()),
        ["metadataMalformed"] = false, ["metadataTruncated"] = false, ["signalsOmittedCount"] = 0
    };

    private static JsonObject PrimaryMetadataOverview() => new()
    {
        ["firstIndexedPhysicalPage"] = 1, ["lastIndexedPhysicalPage"] = 40,
        ["canonicalChunkCount"] = 120,
        ["visualAssets"] = new JsonArray(Enumerable.Range(0, 4).Select(index => (JsonNode)new JsonObject
        { ["physicalPage"] = index + 1, ["description"] = new string('v', 80) }).ToArray())
    };

    private static JsonObject PrimaryMetadataRow(string id) => new()
    {
        ["evidenceId"] = id, ["sourceKey"] = "source-1", ["evidenceKind"] = "source_chunk",
        ["content"] = "An unchanged canonical body for " + id,
        ["sourceQuality"] = PrimaryMetadataQuality(), ["sourceOverview"] = PrimaryMetadataOverview()
    };

    private static OpenAiCompatibleAdvancedAnalysisProvider.PrimaryMetadataReferenceScope PrimaryMetadataScope(
        JsonObject row, bool primary = true, string doc = "doc", string revision = "revision",
        string? hash = null, string path = "Documents/source.pdf") => new(
        row["evidenceId"]!.GetValue<string>(), row["sourceKey"]!.GetValue<string>(),
        new AdvancedAnalysisResultEvidence { EvidenceId = row["evidenceId"]!.GetValue<string>(),
            DocId = doc, RevisionId = revision, SourceHash = hash ?? new string('a', 64), DocPath = path }, primary,
        row["sourceQuality"]?.ToJsonString(), row["sourceOverview"]?.ToJsonString());

    [Theory]
    [InlineData("evidence")]
    [InlineData("observations")]
    public void Primary_metadata_references_keep_one_complete_current_carrier_and_unchanged_bodies(string collection)
    {
        var first = PrimaryMetadataRow("E1"); var second = PrimaryMetadataRow("E2");
        var scopes = new[] { PrimaryMetadataScope(first), PrimaryMetadataScope(second) };
        var prompt = new JsonObject { [collection] = new JsonArray(first, second) }.ToJsonString();
        var compact = OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out var metrics);
        var rows = JsonNode.Parse(compact)![collection]!.AsArray();
        Assert.NotNull(rows[0]!["sourceQuality"]); Assert.NotNull(rows[0]!["sourceOverview"]);
        Assert.Null(rows[1]!["sourceQuality"]); Assert.Null(rows[1]!["sourceOverview"]);
        Assert.Equal("E1", rows[1]!["sourceQualityReference"]!["sourceEvidenceId"]!.GetValue<string>());
        Assert.Equal("current_user." + collection + "[evidenceId].sourceOverview",
            rows[1]!["sourceOverviewReference"]!["reference"]!.GetValue<string>());
        Assert.Equal(first["content"]!.GetValue<string>(), rows[0]!["content"]!.GetValue<string>());
        Assert.Equal(second["content"]!.GetValue<string>(), rows[1]!["content"]!.GetValue<string>());
        Assert.Equal(1, metrics.QualityReferences); Assert.Equal(1, metrics.OverviewReferences);
        Assert.True(metrics.SavedWireCharacters > 0);
        Assert.Equal(compact, OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(compact, scopes, out _));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("sourceKey")]
    public void Primary_metadata_references_never_cross_a_different_source_capability(string difference)
    {
        var a = PrimaryMetadataRow("E1"); var b = PrimaryMetadataRow("E2");
        if (difference == "sourceKey") b["sourceKey"] = "source-2";
        var scopes = new[] { PrimaryMetadataScope(a), PrimaryMetadataScope(b,
            doc: difference == "doc" ? "other" : "doc", revision: difference == "revision" ? "other" : "revision",
            hash: difference == "hash" ? new string('b', 64) : null,
            path: difference == "path" ? "Documents/other.pdf" : "Documents/source.pdf") };
        var prompt = new JsonObject { ["evidence"] = new JsonArray(a, b) }.ToJsonString();
        var result = OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out var metrics);
        Assert.Equal(prompt, result); Assert.Equal(0, metrics.QualityReferences + metrics.OverviewReferences);
    }

    [Fact]
    public void Primary_metadata_references_preserve_different_warnings_and_secondary_or_raw_metadata()
    {
        var a = PrimaryMetadataRow("E1"); var b = PrimaryMetadataRow("E2"); var raw = PrimaryMetadataRow("RAW");
        b["sourceQuality"] = PrimaryMetadataQuality("different_warning");
        var scopes = new[] { PrimaryMetadataScope(a), PrimaryMetadataScope(b), PrimaryMetadataScope(raw, primary: false) };
        var prompt = new JsonObject { ["evidence"] = new JsonArray(a, b, raw) }.ToJsonString();
        var rows = JsonNode.Parse(OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out _))!["evidence"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(b["sourceQuality"], rows[1]!["sourceQuality"]));
        Assert.True(JsonNode.DeepEquals(raw, rows[2]));
    }

    [Fact]
    public void Primary_metadata_references_reorder_safely_and_reject_an_orphan_or_forged_warning_binding()
    {
        var a = PrimaryMetadataRow("E1"); var b = PrimaryMetadataRow("E2");
        var scopes = new[] { PrimaryMetadataScope(a), PrimaryMetadataScope(b) };
        var prompt = new JsonObject { ["evidence"] = new JsonArray(a, b) }.ToJsonString();
        var compact = JsonNode.Parse(OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out _))!.AsObject();
        var original = compact["evidence"]!.AsArray();
        compact["evidence"] = new JsonArray(original[1]!.DeepClone(), original[0]!.DeepClone());
        var reordered = JsonNode.Parse(OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(compact.ToJsonString(), scopes, out _))!["evidence"]!.AsArray();
        Assert.NotNull(reordered[0]!["sourceQuality"]);
        Assert.Equal("E2", reordered[1]!["sourceQualityReference"]!["sourceEvidenceId"]!.GetValue<string>());
        compact["evidence"]!.AsArray().RemoveAt(1);
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(compact.ToJsonString(), scopes, out _));
        Assert.Equal("advanced_primary_metadata_reference_invalid", error.ErrorCode);
        var incompatible = scopes[1] with { Quality = PrimaryMetadataQuality("changed_warning").ToJsonString() };
        Assert.Throws<AdvancedAnalysisProviderException>(() => OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(
            OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out _), new[] { scopes[0], incompatible }, out _));
    }

    [Theory]
    [InlineData("sourceQuality")]
    [InlineData("sourceOverview")]
    public void Primary_metadata_references_reject_a_changed_carrier_value_with_unchanged_canonical_scopes(string field)
    {
        var a = PrimaryMetadataRow("E1"); var b = PrimaryMetadataRow("E2");
        var scopes = new[] { PrimaryMetadataScope(a), PrimaryMetadataScope(b) };
        var prompt = new JsonObject { ["evidence"] = new JsonArray(a, b) }.ToJsonString();
        var compact = JsonNode.Parse(OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(prompt, scopes, out _))!.AsObject();
        var carrier = compact["evidence"]![0]![field]!.AsObject();
        if (field == "sourceQuality") carrier["signals"]!.AsArray().Add("new_unattested_warning");
        else carrier["canonicalChunkCount"] = 121;
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() =>
            OpenAiCompatibleAdvancedAnalysisProvider.SharePrimaryMetadataForPrompt(compact.ToJsonString(), scopes, out _));
        Assert.Equal("advanced_primary_metadata_reference_invalid", error.ErrorCode);
    }

    [Fact]
    public void Primary_metadata_marginal_fitting_spends_the_compact_wire_once()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions("responses"); options.MaximumEvidencePromptCharacters = 8_000;
        options.NativeObservationReadEnabled = true; options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key");
        var codes = Enumerable.Range(0, 20).Select(index => "warning_" + index + "_" + new string('x', 40)).ToArray();
        var values = Enumerable.Range(1, 20).Select(index => new AdvancedAnalysisResolvedEvidence(
            new AdvancedAnalysisResultEvidence { EvidenceId = "E" + index, DocId = "doc", RevisionId = "revision",
                SourceHash = new string('a', 64), DocPath = "Documents/source.pdf", PageStart = 1, PageEnd = 1, ChunkId = "chunk" + index },
            "Canonical item " + index + " " + new string('c', 240)) { SourceQuality = new("extracted", false, codes) }).ToArray();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider);
        var method = type.GetMethod("BuildPromptEvidence", flags)!;
        object Project(AdvancedAnalysisResolvedEvidence[] items) => method.Invoke(provider,
            new object?[] { BuildDirectRequest(), items, new Dictionary<string, HashSet<string>>(), null })!;
        var shared = Project(values);
        var different = Project(values.Select((item, index) => item with { SourceQuality = new("extracted", false,
            [.. codes, "unique_warning_" + index]) }).ToArray());
        var sharedCount = ((System.Collections.IEnumerable)shared).Cast<object>().Count();
        var differentCount = ((System.Collections.IEnumerable)different).Cast<object>().Count();
        Assert.True(sharedCount > differentCount, $"shared={sharedCount}, distinct={differentCount}");
        Assert.All(values, item => Assert.Equal(codes, item.SourceQuality!.Signals));
        Assert.Empty(factory.Requests);
    }
}
