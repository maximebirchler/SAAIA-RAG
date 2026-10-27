using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeObservationPageReadContextTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void Existing_inline_control_keeps_literal_terminators_and_primary_bytes()
    {
        var document = Document();
        var before = JsonSerializer.Serialize(document, Web);
        var window = Legacy(document);
        Assert.Equal(Native(document).Text.Raw, string.Concat(window.Lines.Select(line => line.Text)));
        Assert.Equal(before, JsonSerializer.Serialize(document, Web));
    }

    [Theory]
    [InlineData("native_layout_recovery")]
    [InlineData("native_text_recovery")]
    [InlineData("native_table_cell_observation")]
    public void Stored_page_and_inline_have_identical_inventory_lines_references_and_quality_json(string blockType)
    {
        var document = Document(blockType);
        var inline = NativeObservationPageReadContext.FromInline(Tenant, document, 1);
        var stored = NativeObservationPageReadContext.FromStored(Snapshot(document));
        Assert.Equal(JsonSerializer.Serialize(AdvancedAnalysisNativeObservationWindows.Inventory(document, 1, 0, 16, 20_000), Web),
            JsonSerializer.Serialize(AdvancedAnalysisNativeObservationWindows.Inventory(stored, 0, 16, 20_000), Web));
        var expected = JsonSerializer.Serialize(Legacy(document), Web);
        Assert.Equal(expected, JsonSerializer.Serialize(Read(inline), Web));
        Assert.Equal(expected, JsonSerializer.Serialize(Read(stored), Web));
        Assert.All(Read(stored).Lines, line => Assert.Equal(line.Text,
            AdvancedAnalysisNativeObservationWindows.ResolveLine(stored, line.Source).Text));
    }

    [Fact]
    public void Stored_page_line_ids_preserve_manifest_source_case_and_unicode_scalar_contract()
    {
        var document = Document();
        var expected = Legacy(document);
        var context = NativeObservationPageReadContext.FromStored(Snapshot(document));
        var actual = Read(context);
        Assert.Equal("Native’s 😀 \"\\ < >\r\n", actual.Lines[0].Text);
        Assert.Equal(expected.Lines.Select(line => line.Source.LineId), actual.Lines.Select(line => line.Source.LineId));
        Assert.Equal(0, actual.Lines[0].Source.UnicodeScalarStart);
        Assert.Equal(actual.Lines[0].Text.EnumerateRunes().Count(), actual.Lines[0].Source.UnicodeScalarEndExclusive);
        Assert.Equal(document.Source.Sha256, context.Identity.SourceSha256);
        Assert.Equal(document.ManifestSha256, context.Identity.ManifestSha256);
        Assert.Equal(Native(document).Text.RawSha256, actual.RawTextSha256);
    }

    [Fact]
    public void Stored_unavailable_is_unknown_while_a_retained_empty_block_is_observed_empty()
    {
        var document = Document();
        var unavailable = NativeObservationPageReadContext.FromStored(Snapshot(document) with
            { Availability = "unavailable", Reason = "no_captured_native_observation", Blocks = null });
        var inventory = AdvancedAnalysisNativeObservationWindows.Inventory(unavailable, 0, 16, 14_000);
        Assert.Equal("unavailable", inventory.Availability);
        Assert.Null(inventory.ObservationCount);
        Assert.Null(inventory.Observations);
        Native(document).Text = Text("");
        var empty = NativeObservationPageReadContext.FromStored(Snapshot(document));
        Assert.Equal(0, Assert.Single(AdvancedAnalysisNativeObservationWindows.Inventory(empty, 0, 16, 14_000).Observations!).RawLineCount);
        Assert.Empty(Read(empty).Lines);
        Assert.Equal("observed", Read(empty).Status);
    }

    [Fact]
    public void Adapters_do_not_share_mutable_geometry_or_quality_lists_with_primary_or_snapshot()
    {
        var document = Document();
        var snapshot = Snapshot(document);
        var inline = NativeObservationPageReadContext.FromInline(Tenant, document, 1);
        var stored = NativeObservationPageReadContext.FromStored(snapshot);
        var expectedInline = JsonSerializer.Serialize(inline, Web);
        var expectedStored = JsonSerializer.Serialize(stored, Web);
        document.Pages[0].Geometry.Width = 999;
        document.Pages[0].QualityFlags.Add("later-primary-change");
        Native(document).Polygon!.Points[0].X = .9;
        Native(document).QualityFlags.Add("later-primary-block-change");
        snapshot.Geometry.Height = 999;
        snapshot.Blocks![0].ObservedPolygon!.Points[0].Y = .9;
        Assert.Equal(expectedInline, JsonSerializer.Serialize(inline, Web));
        Assert.Equal(expectedStored, JsonSerializer.Serialize(stored, Web));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("manifest")]
    public void Reference_from_another_binding_is_not_relabelled_even_when_raw_is_identical(string changed)
    {
        var document = Document();
        var reference = Legacy(document).Lines[0].Source;
        var snapshot = Snapshot(document);
        snapshot = changed switch
        {
            "revision" => snapshot with { Root = snapshot.Root with { Identity = snapshot.Root.Identity with { RevisionId = Guid.NewGuid() } } },
            "source" => snapshot with { Root = snapshot.Root with { Identity = snapshot.Root.Identity with { SourceSha256 = new string('b', 64) } } },
            _ => snapshot with { Generation = snapshot.Generation with { ManifestSha256 = new string('c', 64) } }
        };
        var context = NativeObservationPageReadContext.FromStored(snapshot);
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationWindows.ResolveLine(context, reference));
    }

    [Fact]
    public void Context_budget_preserves_whole_lines_and_refused_cursor_with_escaped_json()
    {
        var document = Document();
        Native(document).Text = Text("small\r\n\"\\😀" + new string('x', 5_000) + "\r\ntail");
        var context = NativeObservationPageReadContext.FromStored(Snapshot(document));
        var refused = AdvancedAnalysisNativeObservationWindows.Raw(context, "native-audit",
            Native(document).Text.RawSha256, 1, 1, 1_000);
        Assert.Empty(refused.Lines);
        Assert.Equal(1, refused.NextOffset);
        Assert.Equal("line_does_not_fit", refused.Status);
        Assert.True(JsonSerializer.Serialize(refused, Web).Length <= 1_000);
        Assert.Equal(JsonSerializer.Serialize(AdvancedAnalysisNativeObservationWindows.Raw(document, 1,
            "native-audit", Native(document).Text.RawSha256, 0, 2, 14_000), Web),
            JsonSerializer.Serialize(AdvancedAnalysisNativeObservationWindows.Raw(context,
                "native-audit", Native(document).Text.RawSha256, 0, 2, 14_000), Web));
    }

    [Fact]
    public void Context_line_processing_obeys_cancellation_without_publishing_partial_text()
    {
        var context = NativeObservationPageReadContext.FromStored(Snapshot(Document()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => AdvancedAnalysisNativeObservationWindows.Raw(context,
            "native-audit", context.Blocks![0].RawSha256, 0, 16, 14_000, canceled.Token));
    }

    internal static NativeRawPageSnapshot Snapshot(CanonicalDocument document)
    {
        var page = document.Pages[0];
        var block = Native(document);
        return new(NativeRawPageSchema.Snapshot,
            new(new(Tenant, document.DocumentId, document.RevisionId, document.Source.Sha256), new string('e', 64)),
            new(new string('f', 64), NativeRawPageSchema.Projection,
                Guid.Parse("44444444-4444-4444-8444-444444444444"), document.ManifestSha256),
            1, 2, page.Geometry, page.QualityFlags, "observed", null,
            [new(block.BlockId, block.BlockType, block.Ordinal, block.ReadingOrder,
                block.Text.Raw, block.Text.RawSha256, block.Polygon, block.QualityFlags,
                block.Provenance.Engine, block.Provenance.EngineVersion, block.Provenance.Method, block.Provenance.StageId)]);
    }

    internal static CanonicalDocument Document(string blockType = "native_layout_recovery") => new()
    {
        DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
        RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Source = new() { Sha256 = new string('A', 64) }, ManifestSha256 = new string('D', 64), PageCount = 2,
        Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" },
            QualityFlags = ["page-warning"], Blocks = [
                new() { BlockId = "primary", BlockType = "text", Text = Text("Normalized primary's text.") },
                new() { BlockId = "native-audit", BlockType = blockType,
                    Text = Text("Native’s 😀 \"\\ < >\r\nNext\rFinal\n"), Ordinal = 7, ReadingOrder = 9,
                    Polygon = new() { Points = [new() { X = .1, Y = .2 }, new() { X = .8, Y = .2 }, new() { X = .8, Y = .3 }] },
                    QualityFlags = ["block-warning", "page-warning"],
                    Provenance = new() { Engine = "PdfPig", EngineVersion = "fixture", Method = "native_layout", StageId = "native-stage" } }
            ] }, new() { PageNumber = 2 }]
    };

    private static CanonicalBlock Native(CanonicalDocument document) => document.Pages[0].Blocks.Single(b => b.BlockId == "native-audit");
    private static CanonicalTextVariants Text(string value) => new()
    {
        Raw = value, Canonical = value, Display = value, Retrieval = value,
        RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
    };
    private static AdvancedAnalysisNativeObservationWindow Legacy(CanonicalDocument document)
        => AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit", Native(document).Text.RawSha256, 0, 16, 20_000);
    private static AdvancedAnalysisNativeObservationWindow Read(NativeObservationPageReadContext context)
        => AdvancedAnalysisNativeObservationWindows.Raw(context, "native-audit", context.Blocks![0].RawSha256, 0, 16, 20_000);
}
