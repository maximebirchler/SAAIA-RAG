using System.IO.Compression;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using SAAIA.Backend.Tests;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed class DoclingInputGeometryArtifactRepoTests
{
    [ProfileFact("IntegrationPg")]
    [Trait("Profile", "IntegrationPg")]
    public async Task Input_geometry_pg_same_transaction_roundtrip_and_identical_retry()
    {
        await using var db = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var bundle = Bundle(fixture);
        await Seed(db, fixture);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await CanonicalArtifactRepo.UpsertBundleAsync(conn, tx, fixture.Tenant, fixture.Document.RevisionId, bundle, CancellationToken.None);
        await DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None);
        await DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None);
        await tx.CommitAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_binary_artifacts"));
        var hash = await conn.ExecuteScalarAsync<byte[]>("SELECT content_hash FROM document_revision_binary_artifacts WHERE artifact_type='docling_projection_input_geometry'");
        Assert.NotNull(hash);
        Assert.Equal(fixture.Artifact.Sha256, Convert.ToHexString(hash).ToLowerInvariant());
    }

    [ProfileFact("IntegrationPg")]
    [Trait("Profile", "IntegrationPg")]
    public async Task Input_geometry_pg_wrong_tenant_or_descriptor_cannot_publish()
    {
        await using var db = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var bundle = Bundle(fixture);
        await Seed(db, fixture);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, Guid.NewGuid(), bundle, fixture.Artifact, CancellationToken.None));
        bundle.ManifestEnvelope.Manifest.Artifacts.Single(a => a.ArtifactType == DoclingInputGeometryArtifact.ArtifactType).Sha256 = new string('f', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None));
        await tx.RollbackAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_binary_artifacts"));
    }

    [ProfileFact("IntegrationPg")]
    [Trait("Profile", "IntegrationPg")]
    public async Task Input_geometry_pg_conflicting_payload_is_immutable()
    {
        await using var db = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var bundle = Bundle(fixture);
        await Seed(db, fixture);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None);
            await tx.CommitAsync();
        }
        var node = JsonNode.Parse(fixture.Artifact.CopyJson())!;
        node["backendCodeRevision"] = "another-proof";
        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        var changed = new DoclingInputGeometryArtifact(fixture.Tenant, fixture.Document.DocumentId, fixture.Document.RevisionId,
            fixture.Artifact.ProcessingRunId, fixture.SourceSha, DoclingInputGeometryCapture.Sha(bytes), bytes, compressed.ToArray());
        bundle.ManifestEnvelope.Manifest.Artifacts.RemoveAll(a => a.ArtifactType == DoclingInputGeometryArtifact.ArtifactType);
        bundle.ManifestEnvelope.Manifest.Artifacts.Add(DoclingInputGeometryArtifactRepo.Describe(changed));
        bundle.ManifestEnvelope.Manifest.CodeRevision = "another-proof";
        bundle.ManifestEnvelope = CanonicalContractJson.CreateEnvelope(bundle.ManifestEnvelope.Manifest);
        bundle.Document.ManifestSha256 = bundle.ManifestEnvelope.ManifestSha256;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, changed, CancellationToken.None));
            Assert.Equal("input_geometry_immutable_conflict", error.Message);
            await tx.RollbackAsync();
        }
        var hash = await conn.ExecuteScalarAsync<byte[]>("SELECT content_hash FROM document_revision_binary_artifacts");
        Assert.NotNull(hash);
        Assert.Equal(fixture.Artifact.Sha256, Convert.ToHexString(hash).ToLowerInvariant());
    }

    [ProfileFact("IntegrationPg")]
    [Trait("Profile", "IntegrationPg")]
    public async Task Input_geometry_pg_rollback_and_revision_purge_leave_no_orphan()
    {
        await using var db = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var bundle = Bundle(fixture);
        await Seed(db, fixture);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await CanonicalArtifactRepo.UpsertBundleAsync(conn, tx, fixture.Tenant, fixture.Document.RevisionId, bundle, CancellationToken.None);
            await DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None);
            await tx.RollbackAsync(); // Simulated failure of a later publication operation.
        }
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_binary_artifacts"));
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await CanonicalArtifactRepo.UpsertBundleAsync(conn, tx, fixture.Tenant, fixture.Document.RevisionId, bundle, CancellationToken.None);
            await DoclingInputGeometryArtifactRepo.StoreAsync(conn, tx, fixture.Tenant, bundle, fixture.Artifact, CancellationToken.None);
            await tx.CommitAsync();
        }
        await conn.ExecuteAsync("DELETE FROM document_revisions WHERE revision_id=@id", new { id = fixture.Document.RevisionId });
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM document_revision_binary_artifacts"));
    }

    [Fact]
    public async Task Input_geometry_carrier_is_not_mutable_through_returned_bytes()
    {
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var json = fixture.Artifact.CopyJson();
        json[0] = 0;
        var gzip = fixture.Artifact.CopyGzip();
        gzip[0] = 0;
        using var restored = DoclingInputGeometryArtifactRepo.Read(fixture.Artifact);
        Assert.Equal(fixture.SourceSha, restored.RootElement.GetProperty("serializedPdfSha256").GetString());
    }

    [Fact]
    public async Task Input_geometry_reader_refuses_foreign_fields_and_corrupt_compression()
    {
        var fixture = await DoclingCanonicalDocumentAdapterTests.GeometryFixture();
        var node = JsonNode.Parse(fixture.Artifact.CopyJson())!;
        node["unrecognizedProof"] = "not-admitted";
        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        var foreign = new DoclingInputGeometryArtifact(fixture.Tenant, fixture.Document.DocumentId, fixture.Document.RevisionId,
            fixture.Artifact.ProcessingRunId, fixture.SourceSha, DoclingInputGeometryCapture.Sha(bytes), bytes, compressed.ToArray());
        Assert.Throws<InvalidDataException>(() => DoclingInputGeometryArtifactRepo.Read(foreign));
        var badGzip = fixture.Artifact.CopyGzip();
        badGzip[0] = 0;
        var corrupt = new DoclingInputGeometryArtifact(fixture.Tenant, fixture.Document.DocumentId, fixture.Document.RevisionId,
            fixture.Artifact.ProcessingRunId, fixture.SourceSha, fixture.Artifact.Sha256, fixture.Artifact.CopyJson(), badGzip);
        Assert.Throws<InvalidDataException>(() => DoclingInputGeometryArtifactRepo.Read(corrupt));
    }

    private static CanonicalIngestionBundle Bundle(DoclingCanonicalDocumentAdapterTests.InputGeometryFixture fixture)
    {
        var manifest = new IngestionManifest
        {
            DocumentId = fixture.Document.DocumentId, RevisionId = fixture.Document.RevisionId,
            ProcessingRunId = fixture.Artifact.ProcessingRunId, SourceSha256 = fixture.SourceSha,
            CodeRevision = "source22-test", CreatedAtUtc = DateTimeOffset.Parse("2026-10-27T00:00:00Z"),
            Hardware = new() { ProfileId = "test", LogicalProcessorCount = 1, MemoryBytes = 1000000000, Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
            Stages = [new() { StageId = "stage_fixture", StageType = "canonical_projection", Engine = "fixture", EngineVersion = "1", OptionsSha256 = new string('a', 64), DeviceId = "cpu" }],
            Artifacts = [new() { ArtifactId = "source", ArtifactType = CanonicalArtifactTypes.SourceDocument, SchemaVersion = "application/pdf", Sha256 = fixture.SourceSha,
                SizeBytes = DoclingCanonicalDocumentAdapterTests.GeometrySourceBytes.Length, StorageKey = "content://sha256/" + fixture.SourceSha }, DoclingInputGeometryArtifactRepo.Describe(fixture.Artifact)]
        };
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        fixture.Document.ManifestSha256 = envelope.ManifestSha256;
        var bundle = new CanonicalIngestionBundle { Document = fixture.Document, ManifestEnvelope = envelope };
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        return bundle;
    }

    private static async Task Seed(LocalPostgresDiagnosticFixture db, DoclingCanonicalDocumentAdapterTests.InputGeometryFixture fixture)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic geometry');
            INSERT INTO documents(doc_id,tenant_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
            VALUES(@doc,@tenant,'synthetic-geometry.pdf','synthetic-geometry.pdf','synthetic','indexed',1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
            VALUES(@revision,@tenant,@doc,'synthetic-geometry.pdf',@source,1,1);
            """, new { tenant = fixture.Tenant, doc = fixture.Document.DocumentId, revision = fixture.Document.RevisionId, source = Convert.FromHexString(fixture.SourceSha) });
    }
}
