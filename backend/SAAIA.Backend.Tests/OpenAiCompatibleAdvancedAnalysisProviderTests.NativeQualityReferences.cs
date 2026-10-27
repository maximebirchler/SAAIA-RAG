using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 93)]
    [InlineData("responses", 93)]
    [InlineData("chat-completions", 97)]
    [InlineData("responses", 97)]
    [InlineData("chat-completions", 112)]
    [InlineData("responses", 112)]
    public async Task Native_quality_reference_keeps_one_whole_line_and_all_effective_codes_under_default_wire_budget(
        string protocol, int characters)
    {
        var fixture = NativeQualityFixture(characters, 1);
        var body = await CaptureNativeBudgetAsync(protocol, fixture.Evidence, fixture.RawSha);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
        AssertNativeQualityReference(body, protocol, fixture);
    }

    [Theory]
    [InlineData("chat-completions", "different_source_quality")]
    [InlineData("responses", "different_source_quality")]
    [InlineData("chat-completions", "different_second_line")]
    [InlineData("responses", "different_second_line")]
    [InlineData("chat-completions", "different_second_native_quality")]
    [InlineData("responses", "different_second_native_quality")]
    [InlineData("chat-completions", "malformed_source_quality")]
    [InlineData("responses", "malformed_source_quality")]
    [InlineData("chat-completions", "truncated_source_quality")]
    [InlineData("responses", "truncated_source_quality")]
    [InlineData("chat-completions", "empty")]
    [InlineData("responses", "empty")]
    [InlineData("chat-completions", "null")]
    [InlineData("responses", "null")]
    [InlineData("chat-completions", "missing_source_quality")]
    [InlineData("responses", "missing_source_quality")]
    public async Task Native_quality_reference_preserves_the_original_array_when_effective_quality_is_not_identical(
        string protocol, string scenario)
    {
        var fixture = NativeQualityFixture(93, scenario.StartsWith("different_second", StringComparison.Ordinal) ? 2 : 1);
        var evidence = fixture.Evidence.ToArray();
        if (scenario == "different_source_quality")
            evidence[0] = evidence[0] with { SourceQuality = new(null, null, [.. NativeQualityCodes, "other_observed_warning"]) };
        else if (scenario == "different_second_line")
            evidence[1] = evidence[1] with { SourceQuality = new(null, null, [.. NativeQualityCodes, "other_observed_warning"]) };
        else if (scenario == "different_second_native_quality")
            evidence[1] = evidence[1] with
            {
                NativeObservation = evidence[1].NativeObservation! with { QualitySignals = [.. NativeQualityCodes, "other_observed_warning"] },
                SourceQuality = new(null, null, [.. NativeQualityCodes, "other_observed_warning"])
            };
        else if (scenario == "malformed_source_quality")
            evidence[0] = evidence[0] with { SourceQuality = evidence[0].SourceQuality! with { MetadataMalformed = true } };
        else if (scenario == "truncated_source_quality")
            evidence[0] = evidence[0] with { SourceQuality = evidence[0].SourceQuality! with { MetadataTruncated = true } };
        else if (scenario == "missing_source_quality")
            evidence[0] = evidence[0] with { SourceQuality = null };
        else
            evidence[0] = evidence[0] with
            {
                NativeObservation = evidence[0].NativeObservation! with { QualitySignals = scenario == "empty" ? [] : null! },
                SourceQuality = new(null, null, [])
            };
        var body = await CaptureNativeBudgetAsync(protocol, evidence, fixture.RawSha, 64_000);
        var current = WorkspaceUser(body);
        var view = current.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        Assert.False(view.TryGetProperty("qualitySignalsReference", out _));
        var expected = JsonSerializer.SerializeToElement(evidence[0].NativeObservation!.QualitySignals);
        Assert.Equal(expected.GetRawText(), view.GetProperty("qualitySignals").GetRawText());
        foreach (var item in current.GetProperty("evidence").EnumerateArray())
        {
            Assert.Equal(1, CountLiteralInJsonStrings(body, item.GetProperty("content").GetString()!));
            var original = Assert.Single(evidence, original => original.Reference.EvidenceId == item.GetProperty("evidenceId").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(original.NativeObservation!.QualitySignals).GetRawText(),
                item.GetProperty("nativeObservation").GetProperty("qualitySignals").GetRawText());
            if (original.SourceQuality is null)
                Assert.False(item.TryGetProperty("sourceQuality", out _));
            else
                Assert.Equal(JsonSerializer.Serialize(original.SourceQuality.Bound(), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    item.GetProperty("sourceQuality").GetRawText());
        }
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_quality_reference_points_to_the_native_evidence_when_another_primary_shares_the_source(string protocol)
    {
        var fixture = NativeQualityFixture(93, 1);
        var native = fixture.Evidence[0];
        var primary = new AdvancedAnalysisResolvedEvidence(new()
        {
            EvidenceId = "separate-primary", DocId = native.Reference.DocId, RevisionId = native.Reference.RevisionId,
            SourceHash = native.Reference.SourceHash, PageStart = 1, PageEnd = 1
        }, "Separate unchanged primary.", SourceOverview: native.SourceOverview)
        {
            SourceQuality = new(null, null, ["different_primary_warning"])
        };
        var body = await CaptureNativeBudgetAsync(protocol, [primary, native], fixture.RawSha, 64_000, [native]);
        AssertNativeQualityReference(body, protocol, fixture);
        var user = WorkspaceUser(body);
        var chosen = user.GetProperty("alternativeNativeReadings").GetProperty("views")[0]
            .GetProperty("qualitySignalsSourceEvidenceId").GetString();
        Assert.NotEqual(primary.Reference.EvidenceId, chosen);
        Assert.Equal(1, CountLiteralInJsonStrings(body, primary.Content));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_quality_reference_research_review_targets_the_actual_observations_collection(string protocol)
    {
        var fixture = NativeQualityFixture(93, 1);
        var body = await CaptureNativeBudgetAsync(protocol, fixture.Evidence, fixture.RawSha, 64_000, researchReview: true);
        Assert.False(WorkspaceUser(body).TryGetProperty("evidence", out _));
        AssertNativeQualityReference(body, protocol, fixture, "observations");
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_quality_reference_keeps_all_equal_line_qualities_in_a_multi_line_window(string protocol)
    {
        var fixture = NativeQualityFixture(93, 2);
        var body = await CaptureNativeBudgetAsync(protocol, fixture.Evidence, fixture.RawSha, 64_000);
        AssertNativeQualityReference(body, protocol, fixture);
        Assert.All(WorkspaceUser(body).GetProperty("evidence").EnumerateArray(), item =>
            Assert.Equal(NativeQualityCodes, item.GetProperty("sourceQuality").GetProperty("signals")
                .EnumerateArray().Select(value => value.GetString())));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_quality_reference_keeps_distinct_windows_bound_to_their_own_current_line(string protocol)
    {
        var fixture = NativeQualityFixture(93, 2);
        var evidence = fixture.Evidence.Select((item, index) => item with
        {
            NativeObservation = item.NativeObservation! with
            {
                LineOffset = index, LineLimit = 1, NextLineOffset = index == 0 ? 1 : null
            }
        }).ToArray();
        var body = await CaptureNativeBudgetAsync(protocol, evidence, fixture.RawSha, 64_000);
        var user = WorkspaceUser(body);
        var views = user.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray().ToArray();
        Assert.Equal(2, views.Length);
        foreach (var view in views)
        {
            var ordinal = view.GetProperty("offset").GetInt32();
            Assert.Equal(evidence[ordinal].Reference.EvidenceId, view.GetProperty("qualitySignalsSourceEvidenceId").GetString());
            Assert.Equal(1, CountLiteralInJsonStrings(body, evidence[ordinal].Content));
        }
    }

    private static void AssertNativeQualityReference(string body, string protocol, NativeQualityData fixture,
        string collection = "evidence")
    {
        var user = WorkspaceUser(body);
        var view = user.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        Assert.False(view.TryGetProperty("qualitySignals", out _));
        Assert.Equal("current_user." + collection + "[evidenceId].nativeObservation.qualitySignals",
            view.GetProperty("qualitySignalsReference").GetString());
        var id = view.GetProperty("qualitySignalsSourceEvidenceId").GetString();
        var proof = Assert.Single(user.GetProperty(collection).EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == id);
        Assert.Equal(fixture.Evidence[0].Reference.EvidenceId, id);
        Assert.Equal(NativeQualityCodes, proof.GetProperty("nativeObservation").GetProperty("qualitySignals").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(NativeQualityCodes, proof.GetProperty("sourceQuality").GetProperty("signals").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(fixture.Evidence[0].Content, proof.GetProperty("content").GetString());
        Assert.Equal(1, CountLiteralInJsonStrings(body, fixture.Evidence[0].Content));
        Assert.False(proof.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal(8, user.GetProperty("alternativeTableReadings").GetProperty("inventories")[0].GetProperty("observations").GetArrayLength());
        using var output = NativeBudgetToolOutput(body, protocol);
        var tool = output.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views")[0];
        Assert.True(tool.TryGetProperty("metadataReference", out _));
        foreach (var key in new[] { "sourceKey", "physicalPage", "blockId", "rawTextSha256", "offset", "limit" })
            Assert.Equal(view.GetProperty(key).GetRawText(), tool.GetProperty(key).GetRawText());
        Assert.All(tool.GetProperty("lines").EnumerateArray(), line =>
            Assert.Contains(user.GetProperty(collection).EnumerateArray(), item =>
                item.GetProperty("evidenceId").GetString() == line.GetProperty("evidenceId").GetString()));
    }

    private sealed record NativeQualityData(string RawSha, AdvancedAnalysisResolvedEvidence[] Evidence);

    private static NativeQualityData NativeQualityFixture(int characters, int lineCount)
    {
        const string prefix = "Literal’s “quoted” <value> 😀 ";
        var literal = prefix + new string('x', characters - prefix.Length - 2) + "\r\n";
        Assert.Equal(characters, literal.Length);
        var raw = string.Concat(Enumerable.Range(0, lineCount).Select(index => index == 0 ? literal : literal.Replace('x', 'y')));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var document = new CanonicalDocument
        {
            DocumentId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), Source = new() { Sha256 = new string('a', 64) },
            ManifestSha256 = new string('b', 64), Pages = [new() { PageNumber = 1, Blocks = [new()
            {
                BlockId = "native-audit", BlockType = "native_layout_recovery", Text = new() { Raw = raw, RawSha256 = sha }
            }] }]
        };
        var window = AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit", sha, 0, lineCount, 100_000);
        var readings = new AdvancedAnalysisAlternativeNativeReadings(document.DocumentId.ToString("D"),
            document.RevisionId.ToString("D"), document.Source.Sha256, 1, null, window);
        var overview = new AdvancedAnalysisSourceOverview(1, 53, 100)
        {
            TableRegionObservations = LargeNativeBudgetCatalog(), NativeObservations = readings,
            VisualAssets = new("pixels_not_available", 0, [], false, "not_performed", false)
        };
        return new(sha, window.Lines.Select(line => new AdvancedAnalysisResolvedEvidence(new()
        {
            EvidenceId = "advanced-native-evidence-" + line.Source.LineId["native_line_".Length..],
            DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
            SourceHash = document.Source.Sha256, FileName = "source.pdf", DocPath = "Documents/source.pdf",
            PageStart = 1, PageEnd = 1, NativeObservation = line.Source
        }, line.Text, SourceOverview: overview,
            SourceStructure: new(null, null, null, null) { ChunkType = "canonical_native_audit_line" })
        {
            NativeObservation = new(line.Source, lineCount, 0, lineCount, null, NativeQualityCodes),
            SourceQuality = new(null, null, NativeQualityCodes)
        }).ToArray());
    }

    private static readonly string[] NativeQualityCodes =
    [
        "document_intelligence_docling", "canonical_table_overlapping_grid",
        "canonical_table_native_region_coverage_unavailable", "canonical_native_text_gap_recovered",
        "canonical_native_literal_coverage_unresolved", "canonical_native_layout_disagreement",
        "canonical_native_layout_alternative_recovered", "canonical_layout_section_association_not_asserted",
        "spatial_anchor_page_only", "canonical_native_audit_literal_not_primary"
    ];
}
