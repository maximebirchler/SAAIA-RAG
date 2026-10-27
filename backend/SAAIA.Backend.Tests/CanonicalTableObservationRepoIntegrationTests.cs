using Dapper;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;
using Fixture = SAAIA.Contracts.Tests.CanonicalTableRegionObservationFixture;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class CanonicalTableObservationRepoIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Literal_observations_round_trip_idempotently_only_under_their_exact_revision_scope()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        await Seed(database, tenant, document);
        await Store(database, tenant, document);
        await Store(database, tenant, document);
        var original = Assert.Single(document.Pages[0].TableRegionObservations!.Observations!);
        var inventory = await List(database, tenant, document);
        Assert.Equal("observed", inventory!.Availability);
        Assert.Equal(1, inventory.ObservationCount);
        Assert.Equal(1, inventory.AttemptCount);
        var restored = await Read(database, tenant, document, original);
        Assert.Equal(CanonicalContractJson.SerializeCanonical(original), CanonicalContractJson.SerializeCanonical(restored));
        Assert.Equal(original.PublicationRawBytes, restored!.PublicationRawBytes);
        Assert.Null(await List(database, Guid.NewGuid(), document));
        Assert.Null(await CanonicalTableObservationRepo.ListAsync(database.DataSource, tenant, Guid.NewGuid(), document.RevisionId,
            document.Source.Sha256, null, null, CancellationToken.None));
        Assert.Null(await CanonicalTableObservationRepo.ListAsync(database.DataSource, tenant, document.DocumentId, Guid.NewGuid(),
            document.Source.Sha256, null, null, CancellationToken.None));
        Assert.Null(await CanonicalTableObservationRepo.ListAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            new string('f', 64), null, null, CancellationToken.None));
        Assert.Null(await CanonicalTableObservationRepo.ReadAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256, 2, original.TableId, original.ObservationId, CancellationToken.None));
        Assert.Null(await CanonicalTableObservationRepo.ReadAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256, 1, "another_table", original.ObservationId, CancellationToken.None));
        Assert.Null(await CanonicalTableObservationRepo.ReadAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256, 1, original.TableId, "another_observation", CancellationToken.None));
        var unknownPage = await CanonicalTableObservationRepo.ListAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256, 2, 2, CancellationToken.None);
        Assert.Equal("unavailable", unknownPage!.Availability);
        Assert.Null(unknownPage.ObservationCount);
        Assert.Null(unknownPage.Observations);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Missing_capture_observed_empty_and_unavailable_are_three_distinct_persisted_states()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        document.Pages[0].Tables.Clear();
        document.Pages[0].TableRegionObservations = null;
        await Seed(database, tenant, document);
        await Store(database, tenant, document);
        Assert.Null(await List(database, tenant, document));
        document.Pages[0].TableRegionObservations = new() { Availability = "observed", Observations = [], Attempts = [] };
        await Store(database, tenant, document);
        var empty = await List(database, tenant, document);
        Assert.Equal("observed", empty!.Availability);
        Assert.Equal(0, empty.ObservationCount);
        Assert.Empty(empty.Observations!);
        document.Pages[0].TableRegionObservations = null;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Store(database, tenant, document));
        Assert.Equal("canonical_table_observation_capture_cannot_be_erased_within_revision", failure.Message);

        var unknown = Fixture.Attach();
        unknown.RevisionId = Guid.NewGuid();
        unknown.Pages[0].TableRegionObservations = new() { Availability = "unavailable", Reason = "frame_not_verified" };
        await SeedRevision(database, tenant, unknown, 2);
        await Store(database, tenant, unknown);
        var unavailable = await List(database, tenant, unknown);
        Assert.Equal("unavailable", unavailable!.Availability);
        Assert.Null(unavailable.ObservationCount);
        Assert.Null(unavailable.Observations);
        Assert.NotNull(await List(database, tenant, document));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Observation_and_catalog_publication_roll_back_together()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        await Seed(database, tenant, document);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await CanonicalTableObservationRepo.StoreAsync(connection, transaction, tenant, document, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        Assert.Null(await List(database, tenant, document));
        await using var verify = await database.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_table_observations"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Reusing_an_observation_identity_with_different_canonical_bytes_refuses_publication()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        await Seed(database, tenant, document); await Store(database, tenant, document);
        var observation = document.Pages[0].TableRegionObservations!.Observations![0];
        var originalReason = observation.PhysicalReading.Reason;
        observation.PhysicalReading.Reason = "another_unavailable_reason";
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Store(database, tenant, document));
        Assert.Equal("canonical_table_observation_immutable_publication_conflict", failure.Message);
        var preserved = await Read(database, tenant, document, observation);
        Assert.Equal(originalReason, preserved!.PhysicalReading.Reason);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Corrupt_raw_bytes_and_catalog_hashes_are_refused_before_exposure()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        await Seed(database, tenant, document); await Store(database, tenant, document);
        var observation = document.Pages[0].TableRegionObservations!.Observations![0];
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE document_revision_table_observations SET raw_payload=set_byte(raw_payload,0,0)");
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(database, tenant, document, observation));
        await connection.ExecuteAsync("UPDATE document_revision_table_observation_catalog SET content_hash=decode(repeat('ff',32),'hex')");
        await Assert.ThrowsAsync<InvalidDataException>(() => List(database, tenant, document));
    }

    [ProfileFact("IntegrationPg")]
    public async Task A_deleted_document_does_not_expose_retained_secondary_observations()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        await Seed(database, tenant, document); await Store(database, tenant, document);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE documents SET status='deleted',indexed_version=0 WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant, doc = document.DocumentId });
        Assert.Null(await List(database, tenant, document));
        Assert.Null(await Read(database, tenant, document, document.Pages[0].TableRegionObservations!.Observations![0]));
    }

    private static Task<CanonicalTableObservationInventory?> List(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
        => CanonicalTableObservationRepo.ListAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256, null, null, CancellationToken.None);

    private static Task<CanonicalTableRegionObservation?> Read(LocalPostgresDiagnosticFixture database, Guid tenant,
        CanonicalDocument document, CanonicalTableRegionObservation observation)
        => CanonicalTableObservationRepo.ReadAsync(database.DataSource, tenant, document.DocumentId, document.RevisionId,
            document.Source.Sha256, observation.PhysicalPage, observation.TableId, observation.ObservationId, CancellationToken.None);

    private static async Task Store(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalTableObservationRepo.StoreAsync(connection, transaction, tenant, document, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task Seed(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic secondary observations');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
                VALUES(@tenant,@doc,'Synthetic/table.pdf','table.pdf','synthetic','indexed',1,1);
            """, new { tenant, doc = document.DocumentId });
        await SeedRevision(database, tenant, document, 1);
    }

    private static async Task SeedRevision(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document, int version)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                VALUES(@revision,@tenant,@doc,'Synthetic/table.pdf',@hash,@version,@version);
            UPDATE documents SET ingestion_version=@version,indexed_version=@version WHERE tenant_id=@tenant AND doc_id=@doc;
            """, new { tenant, doc = document.DocumentId, revision = document.RevisionId,
                hash = Convert.FromHexString(document.Source.Sha256), version });
    }
}
