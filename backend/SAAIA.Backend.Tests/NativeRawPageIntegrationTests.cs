using Dapper;
using SAAIA.Backend;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class NativeRawPageIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Publication_materializes_every_physical_page_with_explicit_unavailable_state()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_catalog"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        Assert.Equal("unavailable", await connection.ExecuteScalarAsync<string>("SELECT availability FROM document_revision_native_raw_pages WHERE page_number=2"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Published_native_page_is_read_as_a_small_source_bound_snapshot()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var result = await CanonicalNativeRawPageRepo.ReadAsync(connection, null, tenant, bundle.Document.DocumentId,
            bundle.Document.RevisionId, bundle.Document.Source.Sha256, 1, null, CancellationToken.None);
        Assert.Equal(NativeRawPageReadKind.RawPage, result.Kind);
        Assert.Equal(bundle.Document.Pages[0].Blocks[^1].Text.Raw, Assert.Single(result.Page!.Blocks!).Raw);
        Assert.True(result.PageUtf8BytesAdmitted > 0);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Publication_preserves_the_exact_primary_artifact_bytes()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        var expected = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        await NativeRawPageFixture.Seed(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var actual = await connection.QuerySingleAsync<(byte[] Hash, byte[] Payload)>("SELECT content_hash,payload FROM document_revision_binary_artifacts");
        Assert.Equal(expected.ContentHash, actual.Hash);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Scope_mismatches_never_disclose_a_catalogue_or_a_filename()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        foreach (var scope in new (Guid Tenant, Guid Doc, Guid Rev, string Source)[] {
            (Tenant: Guid.NewGuid(), Doc: bundle.Document.DocumentId, Rev: bundle.Document.RevisionId, Source: bundle.Document.Source.Sha256),
            (tenant, Guid.NewGuid(), bundle.Document.RevisionId, bundle.Document.Source.Sha256),
            (tenant, bundle.Document.DocumentId, Guid.NewGuid(), bundle.Document.Source.Sha256),
            (tenant, bundle.Document.DocumentId, bundle.Document.RevisionId, new string('e',64)) })
        {
            var read = await CanonicalNativeRawPageRepo.ReadAsync(connection, null, scope.Tenant, scope.Doc, scope.Rev, scope.Source, 1, null, default);
            Assert.Equal(NativeRawPageReadKind.ScopeMissing, read.Kind); Assert.Null(read.Root); Assert.Null(read.FileName);
        }
    }

    [ProfileFact("IntegrationPg")]
    public async Task New_generation_rotates_atomically_and_old_expected_binding_is_refused()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var first = NativeRawPageFixture.Bundle("first");
        await NativeRawPageFixture.Seed(connection, tenant, first); await NativeRawPageFixture.Publish(connection, tenant, first);
        var old = await Read(connection, tenant, first);
        var second = NativeRawPageFixture.Bundle("second"); await NativeRawPageFixture.Publish(connection, tenant, second);
        var current = await Read(connection, tenant, second);
        Assert.NotEqual(old.Generation, current.Generation);
        Assert.Equal("second", Assert.Single(current.Page!.Blocks!).Raw);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CanonicalNativeRawPageRepo.ReadAsync(connection, null,
            tenant, first.Document.DocumentId, first.Document.RevisionId, first.Document.Source.Sha256, 2,
            new(old.Root!.CanonicalBundleSha256, old.Kind, old.Generation!.GenerationId, old.Generation.ProjectionVersion), default));
        Assert.Equal("native_observation_generation_changed", error.Message);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Same_generation_cannot_change_receipt_or_repair_corrupt_immutable_page_bytes()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        await NativeRawPageFixture.Publish(connection, tenant, bundle); // Exact replay is idempotent.
        await connection.ExecuteAsync("UPDATE document_revision_native_raw_pages SET payload=set_byte(payload,0,0) WHERE page_number=1");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => NativeRawPageFixture.Publish(connection, tenant, bundle));
        Assert.Equal("native_observation_generation_conflict", failure.Message);
        var corrupt = await Assert.ThrowsAsync<InvalidDataException>(() => Read(connection, tenant, bundle));
        Assert.Equal("native_observation_page_integrity_invalid", corrupt.Message);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Failed_page_insert_rolls_back_bundle_catalogue_pages_and_source_anchors()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle("original");
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var before = await Read(connection, tenant, bundle);
        await connection.ExecuteAsync("""
            CREATE FUNCTION reject_synthetic_page() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.page_number=2 THEN RAISE EXCEPTION 'synthetic_page_failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_synthetic_page BEFORE INSERT ON document_revision_native_raw_pages
              FOR EACH ROW EXECUTE FUNCTION reject_synthetic_page();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => NativeRawPageFixture.Publish(connection, tenant, NativeRawPageFixture.Bundle("changed")));
        var after = await Read(connection, tenant, bundle);
        Assert.Equal(before.Root, after.Root); Assert.Equal(before.Generation, after.Generation);
        Assert.Equal("original", Assert.Single(after.Page!.Blocks!).Raw);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Concurrent_publications_serialize_to_one_complete_current_generation_then_purge_cascades()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var first = NativeRawPageFixture.Bundle("one"); var second = NativeRawPageFixture.Bundle("two");
        await NativeRawPageFixture.Seed(connection, tenant, first);
        await using var other = await database.DataSource.OpenConnectionAsync();
        await Task.WhenAll(NativeRawPageFixture.Publish(connection, tenant, first), NativeRawPageFixture.Publish(other, tenant, second));
        var current = await Read(connection, tenant, first);
        Assert.Contains(Assert.Single(current.Page!.Blocks!).Raw, new[] { "one", "two" });
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        await connection.ExecuteAsync("DELETE FROM document_revisions WHERE revision_id=@revision", new { revision = first.Document.RevisionId });
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_catalog"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Budget_refusal_is_explicit_inline_while_absent_legacy_and_missing_expected_page_stay_distinct()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle(new string('"', 800_000));
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var inline = await Read(connection, tenant, bundle);
        Assert.Equal(NativeRawPageReadKind.ExplicitInline, inline.Kind); Assert.Null(inline.Page);
        Assert.Equal("native_raw_page_size_limit", inline.Reason); Assert.Null(inline.PageUtf8BytesAdmitted);
        await connection.ExecuteAsync("DELETE FROM document_revision_native_raw_catalog");
        Assert.Equal(NativeRawPageReadKind.LegacyAbsent, (await Read(connection, tenant, bundle)).Kind);
        var small = NativeRawPageFixture.Bundle(); await NativeRawPageFixture.Publish(connection, tenant, small);
        await connection.ExecuteAsync("DELETE FROM document_revision_native_raw_pages WHERE page_number=1");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Read(connection, tenant, small));
        Assert.Equal("native_observation_page_integrity_invalid", error.Message);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Stale_catalogue_or_modified_catalogue_payload_never_becomes_legacy_fallback()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        await connection.ExecuteAsync("UPDATE document_revision_binary_artifacts SET content_hash=decode(repeat('ee',32),'hex')");
        Assert.Equal("native_observation_page_integrity_invalid", (await Assert.ThrowsAsync<InvalidDataException>(() => Read(connection, tenant, bundle))).Message);
        await connection.ExecuteAsync("UPDATE document_revision_binary_artifacts SET content_hash=@hash", new { hash=CanonicalArtifactRepo.BuildBundleArtifact(bundle).ContentHash });
        await connection.ExecuteAsync("UPDATE document_revision_native_raw_catalog SET payload=set_byte(payload,0,0)");
        Assert.Equal("native_observation_page_integrity_invalid", (await Assert.ThrowsAsync<InvalidDataException>(() => Read(connection, tenant, bundle))).Message);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Legacy_noncontiguous_inventory_keeps_inline_physical_page_access_and_primary_bytes()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle(); bundle.Document.Pages[1].PageNumber = 4;
        var original = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        var result = await CanonicalNativeRawPageRepo.ReadAsync(connection, null, tenant, bundle.Document.DocumentId,
            bundle.Document.RevisionId, bundle.Document.Source.Sha256, 4, null, default);
        Assert.Equal(NativeRawPageReadKind.ExplicitInline, result.Kind);
        Assert.Equal("native_raw_page_inventory_noncontiguous", result.Reason);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT page_count FROM document_revision_native_raw_catalog"));
        Assert.Equal(original.Payload, await connection.ExecuteScalarAsync<byte[]>("SELECT payload FROM document_revision_binary_artifacts"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Valid_zero_page_bundle_is_published_atomically_without_an_invented_page()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var tenant = Guid.NewGuid(); var bundle = NativeRawPageFixture.Bundle();
        bundle.Document.Pages.Clear(); bundle.Document.Assets.Clear(); bundle.Document.Relations.Clear();
        bundle.Document.SectionTree.Clear(); bundle.Document.PageCount = 0;
        await NativeRawPageFixture.Seed(connection, tenant, bundle); await NativeRawPageFixture.Publish(connection, tenant, bundle);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT page_count FROM document_revision_native_raw_catalog"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_native_raw_pages"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_binary_artifacts"));
    }

    private static Task<NativeRawPageLoadResult> Read(NpgsqlConnection connection, Guid tenant,
        SAAIA.Contracts.DocumentIntelligence.CanonicalIngestionBundle bundle)
        => CanonicalNativeRawPageRepo.ReadAsync(connection, null, tenant, bundle.Document.DocumentId,
            bundle.Document.RevisionId, bundle.Document.Source.Sha256, 1, null, default);
}
