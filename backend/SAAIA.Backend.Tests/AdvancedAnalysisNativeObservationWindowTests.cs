using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisNativeObservationWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Native_audit_raw_preserves_unicode_and_explicit_scalar_offsets()
    {
        var document = Document();
        var block = Native(document);
        var first = Read(document, limit: 1);
        var line = Assert.Single(first.Lines);
        Assert.Equal("Native\u2019s not -20,5 °C ≤ 18\r\n", line.Text);
        Assert.Equal(0, line.Source.UnicodeScalarStart);
        Assert.Equal(line.Text.EnumerateRunes().Count(), line.Source.UnicodeScalarEndExclusive);
        Assert.Equal(1, first.NextOffset);
        var second = Read(document, offset: 1, limit: 1);
        Assert.Equal("Next 😀 line.", Assert.Single(second.Lines).Text);
        Assert.Equal(block.Text.Raw, line.Text + second.Lines[0].Text);
        Assert.Null(second.NextOffset);
        Assert.Equal("raw_line_ordinal", first.OffsetUnit);
    }

    [Fact]
    public void Native_audit_does_not_change_primary_or_raw_observations()
    {
        var document = Document();
        var before = CanonicalContractJson.SerializeCanonical(document);
        var inventory = AdvancedAnalysisNativeObservationWindows.Inventory(document, 1, 0, 16, 20_000);
        Assert.Equal("observed", inventory.Availability);
        var observed = Assert.Single(inventory.Observations!);
        Assert.Equal("native-audit", observed.BlockId);
        Assert.Equal("PdfPig", observed.Engine);
        Assert.Equal(Native(document).Text.RawSha256, observed.RawTextSha256);
        Read(document);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Fact]
    public void Native_audit_missing_capture_is_unavailable_not_known_empty_acquisition()
    {
        var document = Document();
        document.Pages[0].Blocks.Remove(Native(document));
        var result = AdvancedAnalysisNativeObservationWindows.Inventory(document, 1, 0, 16, 20_000);
        Assert.Equal("unavailable", result.Availability);
        Assert.Null(result.ObservationCount);
        Assert.Null(result.Observations);
        Assert.Equal("no_captured_native_observation", result.Reason);
    }

    [Theory]
    [InlineData(2, "native-audit", false)]
    [InlineData(1, "unknown-block", false)]
    [InlineData(1, "native-audit", true)]
    public void Native_audit_wrong_page_block_or_hash_never_falls_back(int page, string block, bool wrongHash)
    {
        var document = Document();
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationWindows.Raw(
            document, page, block, wrongHash ? new string('b', 64) : Native(document).Text.RawSha256,
            0, 16, 20_000));
    }

    [Fact]
    public void Native_audit_reference_is_resolved_from_exact_source_not_supplied_text()
    {
        var document = Document();
        var line = Assert.Single(Read(document, limit: 1).Lines);
        Assert.Equal(line.Text, AdvancedAnalysisNativeObservationWindows.ResolveLine(document, line.Source).Text);
        var wrong = new AdvancedAnalysisNativeObservationReference
        {
            BlockId = line.Source.BlockId, RawTextSha256 = line.Source.RawTextSha256,
            PhysicalPage = 1, LineId = line.Source.LineId, LineOrdinal = line.Source.LineOrdinal,
            UnicodeScalarStart = line.Source.UnicodeScalarStart,
            UnicodeScalarEndExclusive = line.Source.UnicodeScalarEndExclusive,
            LineTextSha256 = new string('c', 64)
        };
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationWindows.ResolveLine(document, wrong));
    }

    [Fact]
    public void Native_audit_budget_measures_escaped_json_and_never_cuts_a_line()
    {
        var document = Document();
        Native(document).Text = Text("\"\\😀\"" + new string('x', 5_000) + "\nTail");
        var refused = Read(document, limit: 1, budget: 1_000);
        Assert.Equal("line_does_not_fit", refused.Status);
        Assert.Empty(refused.Lines);
        Assert.Equal(0, refused.NextOffset);
        Assert.True(JsonSerializer.Serialize(refused, Web).Length <= 1_000);
        var full = Read(document);
        var size = JsonSerializer.Serialize(full, Web).Length;
        Assert.Equal(size, JsonSerializer.Serialize(Read(document, budget: size), Web).Length);
        var smaller = Read(document, budget: size - 1);
        Assert.True(JsonSerializer.Serialize(smaller, Web).Length <= size - 1);
        Assert.All(smaller.Lines, line => Assert.Equal(
            AdvancedAnalysisNativeObservationWindows.ResolveLine(document, line.Source).Text, line.Text));
    }

    [Theory]
    [InlineData("{\"blockId\":null}")]
    [InlineData("{\"rawTextSha256\":null}")]
    [InlineData("{\"lineId\":null}")]
    [InlineData("{\"lineTextSha256\":null}")]
    [InlineData("{\"lineOrdinal\":-1}")]
    [InlineData("{\"unicodeScalarEndExclusive\":0}")]
    public void Native_audit_json_null_and_malformed_reference_have_a_stable_safe_refusal(string mutation)
    {
        var document = Document();
        var reference = Read(document, limit: 1).Lines[0].Source;
        var json = JsonSerializer.SerializeToNode(reference, Web)!.AsObject();
        foreach (var property in System.Text.Json.Nodes.JsonNode.Parse(mutation)!.AsObject())
            json[property.Key] = property.Value?.DeepClone();
        var malformed = json.Deserialize<AdvancedAnalysisNativeObservationReference>(Web)!;
        var stored = new AdvancedAnalysisNativeObservationReader.StoredDocument(document, "synthetic.pdf", "Synthetic/synthetic.pdf");
        var error = Assert.Throws<AdvancedAnalysisToolException>(() => stored.Resolve(malformed, CancellationToken.None));
        Assert.Equal("native_observation_identity_or_window_invalid", error.ErrorCode);
    }

    [Fact]
    public void Native_audit_reference_is_bound_to_revision_and_source_even_with_identical_text()
    {
        var document = Document();
        var reference = Read(document, limit: 1).Lines[0].Source;
        document.RevisionId = Guid.NewGuid();
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationWindows.ResolveLine(document, reference));
        document = Document();
        document.Source.Sha256 = new string('b', 64);
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationWindows.ResolveLine(document, reference));
    }

    [Fact]
    public void Native_audit_observed_empty_and_inventory_pagination_do_not_become_unknown_or_inaccessible()
    {
        var document = Document();
        Native(document).Text = Text("");
        Assert.Equal(0, Assert.Single(AdvancedAnalysisNativeObservationWindows.Inventory(document, 1, 0, 1, 20_000).Observations!).RawLineCount);
        Assert.Empty(Read(document).Lines);
        var template = Native(document);
        for (var index = 1; index < 130; index++)
            document.Pages[0].Blocks.Add(new() { BlockId = "native-" + index, BlockType = template.BlockType, Text = Text(""), Provenance = template.Provenance });
        var all = new List<string>();
        var offset = 0;
        do
        {
            var page = AdvancedAnalysisNativeObservationWindows.Inventory(document, 1, offset, 64, 100_000);
            all.AddRange(page.Observations!.Select(item => item.BlockId));
            if (page.NextOffset is not { } next) break;
            Assert.True(next > offset);
            offset = next;
        } while (true);
        Assert.Equal(130, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Native_audit_evidence_caps_refuse_whole_long_line_at_the_exact_cursor_before_resolution()
    {
        var document = Document();
        Native(document).Text = Text("small\nsecond\n" + new string('x', 5_000) + "\ntail");
        var first = AdvancedAnalysisNativeObservationReader.BoundToEvidenceLimits(Read(document, limit: 4), 4_000, 8_000);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal("partial_evidence_item_limit", first.Status);
        var refused = AdvancedAnalysisNativeObservationReader.BoundToEvidenceLimits(Read(document, offset: 2), 4_000, 8_000);
        Assert.Empty(refused.Lines);
        Assert.Equal(2, refused.NextOffset);
        Assert.Equal("line_does_not_fit_evidence_item_limit", refused.Status);
        var total = AdvancedAnalysisNativeObservationReader.BoundToEvidenceLimits(Read(document, limit: 2), 4_000, 8);
        Assert.Single(total.Lines);
        Assert.Equal(1, total.NextOffset);
        Assert.Equal("partial_evidence_total_limit", total.Status);
    }

    [Theory]
    [InlineData("{\"pageEnd\":2}")]
    [InlineData("{\"operation\":\"search_corpus\"}")]
    [InlineData("{\"nativeObservationLimit\":65}")]
    [InlineData("{\"nativeObservationOffset\":-1}")]
    [InlineData("{\"nativeObservationView\":\"raw\"}")]
    [InlineData("{\"nativeObservationBlockId\":\"unexpected\"}")]
    [InlineData("{\"tableObservationView\":\"raw\"}")]
    [InlineData("{\"tableOrdinal\":0}")]
    public void Native_audit_invalid_or_mixed_windows_are_rejected_without_fallback(string mutation)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"query":"read","operation":"read_source","pageStart":1,"pageEnd":1,"nativeObservationView":"inventory"}""")!.AsObject();
        foreach (var property in System.Text.Json.Nodes.JsonNode.Parse(mutation)!.AsObject())
            node[property.Key] = property.Value?.DeepClone();
        var request = node.Deserialize<AdvancedAnalysisSearchRequest>(Web)!;
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationReader.ValidateRequest(request));
    }

    private static AdvancedAnalysisNativeObservationWindow Read(CanonicalDocument document,
        int offset = 0, int limit = 16, int budget = 20_000)
        => AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit",
            Native(document).Text.RawSha256, offset, limit, budget);

    private static CanonicalBlock Native(CanonicalDocument document)
        => document.Pages[0].Blocks.Single(b => b.BlockId == "native-audit");

    private static CanonicalDocument Document() => new()
    {
        DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Source = new() { Sha256 = new string('a', 64) },
        ManifestSha256 = new string('d', 64),
        Pages = [new() { PageNumber = 1, Blocks = [
            new() { BlockId = "primary", BlockType = "text", Text = Text("Native's not -20,5 °C ≤ 18") },
            new() { BlockId = "native-audit", BlockType = "native_layout_recovery",
                Text = Text("Native\u2019s not -20,5 °C ≤ 18\r\nNext 😀 line."),
                Provenance = new() { Engine = "PdfPig", Method = "native_layout_reading_order_reconciliation", StageId = "native-audit-stage" } }
        ] }]
    };

    private static CanonicalTextVariants Text(string text) => new()
    {
        Raw = text, Canonical = text, Display = text, Retrieval = text,
        RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
    };
}
