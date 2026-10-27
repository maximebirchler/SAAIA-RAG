using System.Text;
using System.Diagnostics;
using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "Unit")]
public sealed class NativeRawPageProjectionTests
{
    [Fact]
    public void Projection_keeps_literal_unicode_provenance_and_unavailable_distinct_from_empty()
    {
        var bundle = NativeRawPageFixture.Bundle("");
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        NativeRawPageValidation.Catalog(prepared.Catalog, prepared.Catalog.Root);
        Assert.Equal("raw_pages", prepared.Catalog.Mode);
        Assert.Equal(2, prepared.Pages.Count);
        var first = Decode(prepared, 0);
        Assert.Equal("", Assert.Single(first.Blocks!).Raw);
        Assert.Equal("observed", first.Availability);
        var second = Decode(prepared, 1);
        Assert.Equal("unavailable", second.Availability); Assert.Null(second.Blocks);
        Assert.Null(prepared.Catalog.Pages[1].BlockCount);
    }

    [Fact]
    public void Derived_bytes_do_not_mutate_bundle_or_share_mutable_polygon_and_geometry()
    {
        var bundle = NativeRawPageFixture.Bundle("Native’s 😀\r\n\"quoted\" \\ final.");
        var before = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, before, default);
        var page = Decode(prepared, 0);
        Assert.Equal(bundle.Document.Pages[0].Blocks[^1].Text.Raw, Assert.Single(page.Blocks!).Raw);
        page.Geometry.Width = 1;
        Assert.NotEqual(1, bundle.Document.Pages[0].Geometry.Width);
        Assert.Equal(before.Payload, CanonicalArtifactRepo.BuildBundleArtifact(bundle).Payload);
    }

    [Fact]
    public void Exhausted_own_work_deadline_produces_explicit_whole_projection_inline_receipt()
    {
        var bundle = NativeRawPageFixture.Bundle();
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle),
            default, () => TimeSpan.FromSeconds(2));
        Assert.Equal("inline_fallback", prepared.Catalog.Mode);
        Assert.Equal("native_raw_page_time_limit", prepared.Catalog.Reason);
        Assert.Empty(prepared.Pages); Assert.Empty(prepared.Catalog.Pages);
        NativeRawPageValidation.Catalog(prepared.Catalog, prepared.Catalog.Root);
    }

    [Fact]
    public void Serialization_cap_rejects_whole_page_and_never_publishes_a_truncated_literal()
    {
        var bundle = NativeRawPageFixture.Bundle(new string('"', 800_000));
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        Assert.Equal("inline_fallback", prepared.Catalog.Mode);
        Assert.Equal("native_raw_page_size_limit", prepared.Catalog.Reason);
        Assert.Empty(prepared.Pages);
    }

    [Fact]
    public void Cancellation_propagates_instead_of_becoming_an_inline_budget_refusal()
    {
        var bundle = NativeRawPageFixture.Bundle(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle,
            CanonicalArtifactRepo.BuildBundleArtifact(bundle), cancel.Token));
    }

    [Theory]
    [InlineData("{\"mode\":\"raw_pages\",\"mode\":\"inline_fallback\"}")]
    [InlineData("{\"mode\":\"raw_pages\",\"Mode\":\"inline_fallback\"}")]
    [InlineData("{\"unknown\":true}")]
    public void Ambiguous_or_unknown_private_json_is_a_fixed_integrity_refusal(string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => NativeRawPageCodec.Deserialize<NativeRawPageCatalog>(
            Encoding.UTF8.GetBytes(value), 1000));
        Assert.Equal("native_observation_page_integrity_invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Gzip_inflation_refuses_declared_size_and_expansion_over_cap()
    {
        var work = new NativeRawPageWork(default, TimeSpan.FromSeconds(2));
        var payload = NativeRawPageCodec.Compress(Encoding.UTF8.GetBytes(new string('a', 1000)), work);
        Assert.Throws<InvalidDataException>(() => NativeRawPageCodec.Inflate(payload, 2));
        Assert.Throws<InvalidDataException>(() => NativeRawPageCodec.Inflate(payload, 1001));
    }

    [Fact]
    public void Valid_legacy_noncontiguous_pages_get_explicit_inline_instead_of_breaking_publication()
    {
        var bundle = NativeRawPageFixture.Bundle(); bundle.Document.Pages[1].PageNumber = 4;
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(bundle); // This is a valid legacy canonical inventory.
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, artifact, default);
        Assert.Equal("inline_fallback", prepared.Catalog.Mode);
        Assert.Equal("native_raw_page_inventory_noncontiguous", prepared.Catalog.Reason);
        Assert.Empty(prepared.Pages);
        NativeRawPageValidation.Catalog(prepared.Catalog, prepared.Catalog.Root);
    }

    [Fact]
    public void Valid_empty_canonical_document_has_a_known_zero_page_catalogue()
    {
        var bundle = NativeRawPageFixture.Bundle();
        bundle.Document.Pages.Clear(); bundle.Document.Assets.Clear(); bundle.Document.Relations.Clear();
        bundle.Document.SectionTree.Clear(); bundle.Document.PageCount = 0;
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, artifact, default);
        NativeRawPageValidation.Catalog(prepared.Catalog, prepared.Catalog.Root);
        Assert.Equal("raw_pages", prepared.Catalog.Mode); Assert.Equal(0, prepared.Catalog.PageCount); Assert.Empty(prepared.Pages);
    }

    [Fact]
    public void Optional_legacy_metadata_not_representable_in_standalone_page_gets_explicit_inline()
    {
        var bundle = NativeRawPageFixture.Bundle(""); bundle.Document.Pages[0].Blocks[^1].Text.RawSha256 = "";
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, artifact, default);
        Assert.Equal("inline_fallback", prepared.Catalog.Mode);
        Assert.Equal("native_raw_page_projection_unrepresentable", prepared.Catalog.Reason);
        Assert.Empty(prepared.Pages);
    }

    [Fact]
    public void Legacy_negative_order_values_are_preserved_without_inventing_a_new_canonical_restriction()
    {
        var bundle = NativeRawPageFixture.Bundle();
        bundle.Document.Pages[0].Blocks[^1].Ordinal = -2; bundle.Document.Pages[0].Blocks[^1].ReadingOrder = -1;
        var artifact = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, artifact, default);
        var block = Assert.Single(Decode(prepared, 0).Blocks!);
        Assert.Equal(-2, block.Ordinal); Assert.Equal(-1, block.ReadingOrder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Standalone_geometry_with_observed_dimensions_requires_its_unit(string? unit)
    {
        var bundle = NativeRawPageFixture.Bundle();
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        var page = Decode(prepared, 0); page.Geometry.Unit = unit;
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page, prepared.Catalog, prepared.Pages[0].Descriptor));
    }

    [Fact]
    public void Standalone_geometry_requires_paired_dimensions_but_unknown_dimensions_remain_valid()
    {
        var bundle = NativeRawPageFixture.Bundle();
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        var page = Decode(prepared, 0); page.Geometry.Width = null;
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page, prepared.Catalog, prepared.Pages[0].Descriptor));
        page.Geometry.Height = null; page.Geometry.Unit = null;
        NativeRawPageValidation.Page(page, prepared.Catalog, prepared.Pages[0].Descriptor);
    }

    [Fact]
    public void Standalone_validation_rejects_forged_literal_geometry_frame_and_duplicate_native_id()
    {
        var bundle = NativeRawPageFixture.Bundle();
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        var page = Decode(prepared, 0); var block = Assert.Single(page.Blocks!);
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page with
            { Blocks = [block with { Raw = "forged" }] }, prepared.Catalog, prepared.Pages[0].Descriptor));
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page with
            { PhysicalPage = 2 }, prepared.Catalog, prepared.Pages[0].Descriptor));
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page with
            { Blocks = [block, block] }, prepared.Catalog, prepared.Pages[0].Descriptor with { BlockCount = 2 }));
        page.Geometry.Width = double.NaN;
        Assert.Throws<InvalidDataException>(() => NativeRawPageValidation.Page(page, prepared.Catalog, prepared.Pages[0].Descriptor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Page_decode_uses_distinct_observed_phases_and_off_emits_no_details(bool enabled)
    {
        var bundle = NativeRawPageFixture.Bundle("Exact’s 😀.");
        var prepared = NativeRawPageProjection.Prepare(Guid.NewGuid(), bundle, CanonicalArtifactRepo.BuildBundleArtifact(bundle), default);
        var logger = new DiagnosticRecordingLogger<NativeRawPageProjectionTests>();
        using (var parent = BackendDiagnostics.Start("test.native_page", logger))
        {
            Activity.Current!.SetCustomProperty(NativeReadDiagnostics.OptInProperty, enabled);
            var page = CanonicalNativeRawPageRepo.DecodePage(prepared.Pages[0].Payload, prepared.Catalog, prepared.Pages[0].Descriptor, default);
            Assert.Equal("Exact’s 😀.", Assert.Single(page.Blocks!).Raw); parent.Complete();
        }
        var details = logger.Events.Where(entry => entry.Values.TryGetValue("Operation", out var operation)
            && operation is "native.page_decode" or "native.read.phase").ToArray();
        if (!enabled) { Assert.Empty(details); return; }
        var decode = Assert.Single(details, entry => Equals(entry.Values["Operation"], "native.page_decode"));
        var phases = details.Where(entry => Equals(entry.Values["Operation"], "native.read.phase")).ToArray();
        Assert.Equal(new[] { "gzip_inflate_copy", "hash_validate", "utf8_decode", "json_deserialize", "native_page_validate" },
            phases.Select(entry => Fields(entry).GetProperty("phase").GetString()));
        Assert.All(phases, entry => Assert.Equal(decode.Values["SpanId"], Fields(entry).GetProperty("parent_span_id").GetString()));
        Assert.Equal(prepared.Pages[0].Descriptor.Utf8Bytes, Fields(phases[0]).GetProperty("inflated_bytes_produced").GetInt32());
        Assert.Equal(prepared.Pages[0].Descriptor.Utf8Bytes, Fields(phases[1]).GetProperty("sha256_bytes_processed").GetInt32());
        Assert.Equal(prepared.Pages[0].Descriptor.Utf8Bytes, Fields(phases[2]).GetProperty("utf8_input_bytes").GetInt32());
    }

    private static JsonElement Fields(DiagnosticRecordingLogger<NativeRawPageProjectionTests>.Entry entry)
    {
        using var parsed = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        return parsed.RootElement.Clone();
    }

    private static NativeRawPageSnapshot Decode(NativeRawPagePreparation prepared, int index)
    {
        var item = prepared.Pages[index];
        var page = NativeRawPageCodec.Deserialize<NativeRawPageSnapshot>(NativeRawPageCodec.Inflate(item.Payload,
            item.Descriptor.Utf8Bytes), NativeRawPageSchema.MaximumPageBytes);
        NativeRawPageValidation.Page(page, prepared.Catalog, item.Descriptor);
        return page;
    }
}
