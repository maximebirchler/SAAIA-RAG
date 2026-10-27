using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class NativeRawPageReaderIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Derived_reader_and_resolver_preserve_json_literal_and_publication_without_decoding_full_bundle()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle("Exact’s 😀 \"\\ < >\r\nSecond\rThird\n");
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        var before = await Digest(connection);
        var observed = Observed(bundle.Document);
        var block = bundle.Document.Pages[0].Blocks[^1];
        var expected = AdvancedAnalysisNativeObservationWindows.Raw(bundle.Document, 1, block.BlockId,
            block.Text.RawSha256, 0, 64, 14_000);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var read = await Read(database, tenant, observed, Request(observed) with
            {
                NativeObservationView = "raw", NativeObservationBlockId = block.BlockId,
                NativeObservationRawSha256 = block.Text.RawSha256, NativeObservationLimit = 64
            });
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(read.Readings.View));
            var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource,
                Options.Create(new AdvancedAnalysisOptions { NativeObservationReadEnabled = true }));
            var resolved = await resolver.ResolveAsync(tenant, read.References, CancellationToken.None);
            Assert.True(resolved.IsValid, resolved.ErrorCode);
            Assert.Equal(block.Text.Raw, string.Concat(resolved.Evidence.Select(item => item.Content)));
            Assert.All(resolved.Evidence, item => Assert.Equal("not_verified", item.SourceQuality!.SemanticAccuracy));
            parent.Complete();
        }
        Assert.Equal(0, FullBundleDecodes(logger));
        Assert.Equal(2, logger.Events.Count(entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "native.page_decode")));
        Assert.Equal(1 + expected.Lines.Count, logger.Events.Count(entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "native.page_load")));
        Assert.Equal(before, await Digest(connection));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_jobs"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM retrieval_chunks"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Absent_legacy_catalog_still_uses_the_original_bundle_once_for_a_reader_call()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        await connection.ExecuteAsync("DELETE FROM document_revision_native_raw_catalog");
        var before = await Digest(connection);
        var observed = Observed(bundle.Document);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var read = await Read(database, tenant, observed, Request(observed));
            Assert.Single(read.Readings.Inventory!.Observations!);
            parent.Complete();
        }
        Assert.Equal(1, FullBundleDecodes(logger));
        Assert.Equal(before, await Digest(connection));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Explicit_inline_budget_fallback_preserves_a_readable_inventory_without_truncating_large_raw()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        // UTF8 page exceeds4MiB while the existing raw-character ceiling is respected.
        var bundle = NativeRawPageFixture.Bundle(new string('é', 2_100_000));
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var load = await CanonicalNativeRawPageRepo.ReadAsync(connection, null, tenant, bundle.Document.DocumentId,
            bundle.Document.RevisionId, bundle.Document.Source.Sha256, 1, null, CancellationToken.None);
        Assert.Equal(NativeRawPageReadKind.ExplicitInline, load.Kind);
        Assert.NotNull(load.Generation);
        Assert.Null(load.Page);
        var observed = Observed(bundle.Document);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var read = await Read(database, tenant, observed, Request(observed));
            Assert.Equal(1, Assert.Single(read.Readings.Inventory!.Observations!).RawLineCount);
            parent.Complete();
        }
        Assert.Equal(1, FullBundleDecodes(logger));
    }

    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Profile", "IntegrationPg")]
    public async Task Corrupt_or_missing_expected_page_is_an_integrity_refusal_without_inline_fallback(bool missing)
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle("PRIVATE_RAW_SENTINEL");
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        await connection.ExecuteAsync(missing
            ? "DELETE FROM document_revision_native_raw_pages WHERE page_number=1"
            : "UPDATE document_revision_native_raw_pages SET payload=set_byte(payload,0,0) WHERE page_number=1");
        var observed = Observed(bundle.Document);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => Read(database, tenant, observed, Request(observed)));
            Assert.Equal("native_observation_page_integrity_invalid", error.ErrorCode);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("PRIVATE_RAW_SENTINEL", error.ToString(), StringComparison.Ordinal);
            parent.Complete();
        }
        Assert.Equal(0, FullBundleDecodes(logger));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Unavailable_derived_page_has_null_unknown_counts_and_no_full_bundle_decode()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var observed = Observed(bundle.Document);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var read = await Read(database, tenant, observed, Request(observed) with { PageStart = 2, PageEnd = 2 });
            Assert.Equal("unavailable", read.Readings.Inventory!.Availability);
            Assert.Null(read.Readings.Inventory.ObservationCount);
            Assert.Null(read.Readings.Inventory.Observations);
            Assert.Empty(read.References);
            parent.Complete();
        }
        Assert.Equal(0, FullBundleDecodes(logger));
    }

    [ProfileTheory("IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_call_cache_refuses_rotation_on_a_new_page_without_adopting_a_different_generation(bool legacy)
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        if (legacy)
            await connection.ExecuteAsync("DELETE FROM document_revision_native_raw_catalog");
        var document = bundle.Document;
        var cache = new AdvancedAnalysisNativeObservationReader.Cache();
        var first = await AdvancedAnalysisNativeObservationReader.LoadPageAsync(connection, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256, 1, cache, CancellationToken.None);
        Assert.NotNull(first);
        document.Pages[0].QualityFlags.Add("new-publication-warning");
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var failure = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            AdvancedAnalysisNativeObservationReader.LoadPageAsync(connection, tenant, document.DocumentId,
                document.RevisionId, document.Source.Sha256, 2, cache, CancellationToken.None));
        Assert.Equal("native_observation_generation_changed", failure.ErrorCode);
        Assert.Single(cache.Pages);
        // Already admitted bytes remain this call's original view. No new page
        // is loaded from the replacement publication under that old binding.
        Assert.Same(first, await AdvancedAnalysisNativeObservationReader.LoadPageAsync(connection, tenant,
            document.DocumentId, document.RevisionId, document.Source.Sha256, 1, cache, CancellationToken.None));
    }

    [ProfileFact("IntegrationPg")]
    public async Task A_catalogue_bound_to_a_stale_canonical_root_is_refused_before_any_bundle_decode()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid();
        var bundle = NativeRawPageFixture.Bundle("PRIVATE_STALE_ROOT_SENTINEL");
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        await connection.ExecuteAsync("UPDATE document_revision_binary_artifacts SET content_hash=decode(repeat('ee',32),'hex')");
        var observed = Observed(bundle.Document);
        var logger = new DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests>();
        using (var parent = Enabled(logger))
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => Read(database, tenant, observed, Request(observed)));
            Assert.Equal("native_observation_page_integrity_invalid", error.ErrorCode);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("PRIVATE_STALE_ROOT_SENTINEL", error.ToString(), StringComparison.Ordinal);
            parent.Complete();
        }
        Assert.Equal(0, FullBundleDecodes(logger));
    }

    private static BackendDiagnostics.Operation Enabled(DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests> logger)
    {
        var parent = BackendDiagnostics.Start("test.native.raw.reader", logger);
        Activity.Current!.SetCustomProperty(NativeReadDiagnostics.OptInProperty, true);
        return parent;
    }

    private static int FullBundleDecodes(DiagnosticRecordingLogger<NativeRawPageReaderIntegrationTests> logger)
        => logger.Events.Count(entry => entry.Values.TryGetValue("Operation", out var operation)
            && Equals(operation, "canonical.bundle_decode"));

    private static Task<AdvancedAnalysisNativeObservationRead> Read(LocalPostgresDiagnosticFixture database,
        Guid tenant, AdvancedAnalysisResultEvidence observed, AdvancedAnalysisSearchRequest request)
        => AdvancedAnalysisNativeObservationReader.ReadAsync(database.DataSource, tenant, request, observed, 14_000, CancellationToken.None);

    private static AdvancedAnalysisResultEvidence Observed(CanonicalDocument document) => new()
    {
        DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
        SourceHash = document.Source.Sha256, DocPath = "Synthetic/native.pdf", PageStart = 1, PageEnd = 1
    };

    private static AdvancedAnalysisSearchRequest Request(AdvancedAnalysisResultEvidence observed)
        => new("read", DocId: observed.DocId, RevisionId: observed.RevisionId, DocPath: observed.DocPath,
            Operation: "read_source", PageStart: 1, PageEnd: 1, NativeObservationView: "inventory");

    private static Task<string?> Digest(NpgsqlConnection connection)
        => connection.ExecuteScalarAsync<string>("""
            SELECT md5(string_agg(value,',' ORDER BY value)) FROM (
              SELECT 'bundle:'||encode(content_hash,'hex')||':'||encode(payload,'hex') AS value FROM document_revision_binary_artifacts
              UNION ALL SELECT 'catalog:'||encode(content_hash,'hex')||':'||encode(payload,'hex') FROM document_revision_native_raw_catalog
              UNION ALL SELECT 'page:'||encode(content_hash,'hex')||':'||encode(payload,'hex') FROM document_revision_native_raw_pages
            ) captured;
            """);
}
