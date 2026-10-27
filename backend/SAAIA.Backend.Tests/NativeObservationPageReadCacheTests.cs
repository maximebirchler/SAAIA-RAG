using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeObservationPageReadCacheTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("generation")]
    [InlineData("kind")]
    public void One_call_never_adopts_a_new_root_generation_or_storage_mode(string changed)
    {
        var load = Load();
        var key = Key(load);
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        cache.Pin(key, load);
        var replacement = changed switch
        {
            "root" => load with { Root = load.Root! with { CanonicalBundleSha256 = new string('9', 64) } },
            "generation" => load with { Generation = load.Generation! with { GenerationId = new string('8', 64) } },
            _ => load with { Kind = NativeRawPageReadKind.ExplicitInline, Page = null }
        };
        var failure = Assert.Throws<AdvancedAnalysisToolException>(() => cache.Pin(key, replacement));
        Assert.Equal("native_observation_generation_changed", failure.ErrorCode);
        Assert.Single(cache.Bindings);
        Assert.Equal(new string('e', 64), cache.Bindings[key].CanonicalBundleSha256);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("source")]
    public void Cache_admission_rejects_a_result_for_another_requested_identity(string changed)
    {
        var load = Load();
        var identity = load.Root!.Identity;
        identity = changed switch
        {
            "tenant" => identity with { TenantId = Guid.NewGuid() },
            "document" => identity with { DocumentId = Guid.NewGuid() },
            "revision" => identity with { RevisionId = Guid.NewGuid() },
            _ => identity with { SourceSha256 = new string('b', 64) }
        };
        var wrong = load with { Root = load.Root with { Identity = identity } };
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        var failure = Assert.Throws<AdvancedAnalysisToolException>(() => cache.Pin(Key(load), wrong));
        Assert.Equal("native_observation_page_integrity_invalid", failure.ErrorCode);
        Assert.Empty(cache.Bindings);
    }

    [Fact]
    public async Task Repeated_requested_page_is_charged_once_and_a_second_page_keeps_the_same_binding()
    {
        var load = Load();
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        var context = NativeObservationPageReadContext.FromStored(load.Page!);
        var page = new AdvancedAnalysisNativeObservationReader.StoredPage(context, "native.pdf", "Synthetic/native.pdf");
        cache.AdmitPage(Key(load), load, page);
        Assert.Same(page, cache.AdmitPage(Key(load), load, page));
        Assert.Equal(123, cache.UncompressedBytes);
        var secondLoad = load with { Page = load.Page! with { PhysicalPage = 2 }, PageUtf8BytesAdmitted = 101 };
        var second = new AdvancedAnalysisNativeObservationReader.StoredPage(
            NativeObservationPageReadContext.FromStored(secondLoad.Page), page.FileName, page.DocPath);
        cache.AdmitPage(Key(load), secondLoad, second);
        Assert.Equal(2, cache.Pages.Count);
        Assert.Equal(224, cache.UncompressedBytes);
        var identity = load.Root!.Identity;
        Assert.Same(page, await AdvancedAnalysisNativeObservationReader.LoadPageAsync(null!, identity.TenantId,
            identity.DocumentId, identity.RevisionId, identity.SourceSha256, 1, cache, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AdvancedAnalysisNativeObservationReader.LoadPageAsync(null!,
            identity.TenantId, identity.DocumentId, identity.RevisionId, identity.SourceSha256, 1, cache, canceled.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4_194_305L)]
    public void Unknown_or_invalid_actual_page_bytes_never_become_zero_or_complete(long? bytes)
    {
        var load = Load() with { PageUtf8BytesAdmitted = bytes };
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        var page = new AdvancedAnalysisNativeObservationReader.StoredPage(
            NativeObservationPageReadContext.FromStored(load.Page!), "native.pdf", "native.pdf");
        var failure = Assert.Throws<AdvancedAnalysisToolException>(() => cache.AdmitPage(Key(load), load, page));
        Assert.Equal("native_observation_page_integrity_invalid", failure.ErrorCode);
        Assert.Empty(cache.Pages);
        Assert.Equal(0, cache.UncompressedBytes);
    }

    [Fact]
    public void Existing_128MiB_admitted_byte_budget_still_refuses_before_a_new_page_is_cached()
    {
        var load = Load();
        var cache = new AdvancedAnalysisNativeObservationReader.Cache { UncompressedBytes = 134_217_728 - 100 };
        var page = new AdvancedAnalysisNativeObservationReader.StoredPage(
            NativeObservationPageReadContext.FromStored(load.Page!), "native.pdf", "native.pdf");
        var failure = Assert.Throws<AdvancedAnalysisToolException>(() => cache.AdmitPage(Key(load), load, page));
        Assert.Equal("native_observation_cache_budget_exceeded", failure.ErrorCode);
        Assert.Equal(134_217_728 - 100, cache.UncompressedBytes);
        Assert.Empty(cache.Pages);
    }

    [Fact]
    public void Existing_64_document_budget_counts_inline_and_derived_scopes_once_each()
    {
        var load = Load();
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        for (var index = 0; index < 64; index++)
        {
            var item = load with { Root = load.Root! with { Identity = load.Root.Identity with { DocumentId = Guid.NewGuid() } } };
            cache.Pin(Key(item), item);
        }
        var overflow = load with { Root = load.Root! with { Identity = load.Root.Identity with { DocumentId = Guid.NewGuid() } } };
        var failure = Assert.Throws<AdvancedAnalysisToolException>(() => cache.Pin(Key(overflow), overflow));
        Assert.Equal("native_observation_cache_budget_exceeded", failure.ErrorCode);
        Assert.Equal(64, cache.Bindings.Count);
    }

    [Fact]
    public void Stored_page_line_quality_and_reference_are_identical_to_the_original_document_source()
    {
        var document = NativeObservationPageReadContextTests.Document();
        var expected = new AdvancedAnalysisNativeObservationReader.StoredDocument(document, "native.pdf", "native.pdf");
        var context = NativeObservationPageReadContext.FromStored(NativeObservationPageReadContextTests.Snapshot(document));
        var stored = new AdvancedAnalysisNativeObservationReader.StoredPage(context, "native.pdf", "native.pdf");
        var line = AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit", context.Blocks![0].RawSha256,
            0, 1, 14_000).Lines[0];
        expected.Resolve(line.Source, CancellationToken.None);
        stored.Resolve(line.Source, CancellationToken.None);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Source(line)),
            System.Text.Json.JsonSerializer.Serialize(stored.Source(line)));
    }

    [Fact]
    public void Sql_header_normalization_does_not_replace_original_canonical_hash_strings_or_line_ids()
    {
        var load = Load();
        var normalized = load with
        {
            Root = load.Root! with { Identity = load.Root.Identity with { SourceSha256 = load.Root.Identity.SourceSha256.ToLowerInvariant() } },
            Generation = load.Generation! with { ManifestSha256 = load.Generation.ManifestSha256.ToLowerInvariant() }
        };
        var context = AdvancedAnalysisNativeObservationReader.ContextFromLoad(normalized, 1);
        Assert.Equal(load.Page!.Root.Identity.SourceSha256, context.Identity.SourceSha256);
        Assert.Equal(load.Page.Generation.ManifestSha256, context.Identity.ManifestSha256);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(
                AdvancedAnalysisNativeObservationWindows.Inventory(NativeObservationPageReadContext.FromStored(load.Page), 0, 16, 14_000)),
            System.Text.Json.JsonSerializer.Serialize(AdvancedAnalysisNativeObservationWindows.Inventory(context, 0, 16, 14_000)));
    }

    private static NativeRawPageLoadResult Load()
    {
        var page = NativeObservationPageReadContextTests.Snapshot(NativeObservationPageReadContextTests.Document());
        return new(NativeRawPageReadKind.RawPage, page.Root, page.Generation, page, null,
            "native.pdf", "Synthetic/native.pdf", 99, 123);
    }

    private static (Guid Tenant, Guid Document, Guid Revision, string Source) Key(NativeRawPageLoadResult load)
    {
        var identity = load.Root!.Identity;
        return (identity.TenantId, identity.DocumentId, identity.RevisionId, identity.SourceSha256.ToLowerInvariant());
    }
}
