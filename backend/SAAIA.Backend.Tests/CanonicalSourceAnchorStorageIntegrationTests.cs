using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class CanonicalSourceAnchorStorageIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Forty_thousand_line_anchors_stream_in_one_transaction_with_exact_unicode_and_hashes()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid();
        await Seed(database, tenant, document, revision);
        var anchors = Enumerable.Range(0, 40_000).Select(index => Anchor(document, revision, index)).ToArray();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await CanonicalSourceAnchorStorage.WriteAsync(connection, transaction, tenant, revision, anchors, CancellationToken.None);
            await transaction.CommitAsync();
        }
        Assert.Equal(40_000, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_source_anchors WHERE tenant_id=@tenant AND revision_id=@revision", new { tenant, revision }));
        var payload = await connection.QuerySingleAsync<StoredAnchor>("""
            SELECT payload::text AS "Payload",encode(content_hash,'hex') AS "Hash"
            FROM document_source_anchors WHERE tenant_id=@tenant AND revision_id=@revision AND anchor_id=@anchor;
            """, new { tenant, revision, anchor = anchors[^1].AnchorId });
        var restored = CanonicalContractJson.Deserialize<SourceAnchor>(payload.Payload);
        var originalJson = CanonicalContractJson.SerializeCanonical(anchors[^1]);
        Assert.Equal(originalJson, CanonicalContractJson.SerializeCanonical(restored));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalJson))).ToLowerInvariant(), payload.Hash);
    }

    [ProfileFact("IntegrationPg")]
    public async Task A_late_duplicate_rolls_back_the_entire_stream_and_preserves_previously_committed_anchors()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid();
        await Seed(database, tenant, document, revision);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var original = Anchor(document, revision, 0);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await CanonicalSourceAnchorStorage.WriteAsync(connection, transaction, tenant, revision, [original], CancellationToken.None);
            await transaction.CommitAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var conflicting = Enumerable.Range(1, 256).Select(index => Anchor(document, revision, index)).Append(original).ToArray();
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                CanonicalSourceAnchorStorage.WriteAsync(connection, transaction, tenant, revision, conflicting, CancellationToken.None));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_source_anchors WHERE tenant_id=@tenant AND revision_id=@revision", new { tenant, revision }));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Artifact_publication_rejects_a_revision_outside_its_tenant_before_changing_artifacts()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid();
        await Seed(database, tenant, document, revision);
        var bundle = LegacyCanonicalBundleFactory.Create(new(document, revision, Guid.NewGuid(), 1,
            new string('a', 64), 123, "Synthetic/anchor.pdf", "fixture", DateTimeOffset.UtcNow,
            new() { ProfileId = "fixture", LogicalProcessorCount = 1, MemoryBytes = 1024,
                Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
            [new() { StageId = "parse", StageType = "canonical_projection", Engine = "fixture", EngineVersion = "1",
                OptionsSha256 = new string('d', 64), DeviceId = "cpu" }],
            new([], [], PdfExtractionQualitySummary.FromPages([])), [], []));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, Guid.NewGuid(), revision, bundle, CancellationToken.None));
            Assert.Equal("canonical_bundle_publication_revision_identity_invalid", failure.Message);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revision_binary_artifacts WHERE revision_id=@revision", new { revision }));
    }

    private static SourceAnchor Anchor(Guid document, Guid revision, int ordinal) => new()
    {
        AnchorId = $"anchor_{ordinal}", DocumentId = document, RevisionId = revision,
        SourceSha256 = new string('a', 64), ManifestSha256 = new string('b', 64),
        ProjectionId = $"line_{ordinal}_é_\"\\", ProjectionType = "table_region_observation", Precision = "line",
        Regions = [new() { PageNumber = 1, RawTextSha256 = new string('c', 64) }]
    };

    private static async Task Seed(LocalPostgresDiagnosticFixture database, Guid tenant, Guid document, Guid revision)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic anchor storage');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
                VALUES(@tenant,@document,'Synthetic/anchor.pdf','anchor.pdf','synthetic','indexed',1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                VALUES(@revision,@tenant,@document,'Synthetic/anchor.pdf',@hash,1,1);
            """, new { tenant, document, revision, hash = Convert.FromHexString(new string('a', 64)) });
    }

    private sealed class StoredAnchor
    {
        public string Payload { get; init; } = "";
        public string Hash { get; init; } = "";
    }
}
