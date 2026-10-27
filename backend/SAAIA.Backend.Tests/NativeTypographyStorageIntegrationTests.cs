using Dapper;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class NativeTypographyStorageIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Compressed_canonical_publication_preserves_observed_glyphs_and_observed_empty_typography_exactly()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        foreach (var content in new[] { "BT /F1 12 Tf 60 600 Td (Not italic; 5 - 2.) Tj ET\n", "" })
        {
            var (document, extraction) = NativeTypographyPdfFixture.Extract(NativeTypographyPdfFixture.Pdf(content));
            var bundle = LegacyCanonicalBundleFactory.Create(new(document.DocumentId, document.RevisionId,
                Guid.NewGuid(), 1, document.Source.Sha256, document.Source.SizeBytes, "Synthetic/typography.pdf",
                "fixture", DateTimeOffset.UtcNow,
                new() { ProfileId = "fixture", LogicalProcessorCount = 1, MemoryBytes = 1024,
                    Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
                [new() { StageId = "parse", StageType = "canonical_projection", Engine = "PdfPig",
                    EngineVersion = "fixture", OptionsSha256 = new string('d', 64), DeviceId = "cpu" }],
                extraction, [], []));
            CanonicalNativeTypographyProjector.Apply(bundle.Document, extraction);
            var observed = Assert.IsType<CanonicalNativeTypographySet>(bundle.Document.Pages[0].NativeTypographyObservations);
            Assert.Equal("observed", observed.RunAvailability);
            Assert.Equal(content.Length == 0, observed.ObservedGlyphCount == 0);
            var expected = CanonicalContractJson.SerializeCanonical(bundle);
            var sourcePath = $"Synthetic/{document.DocumentId:D}/typography.pdf";

            await using var connection = await database.DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync("""
                INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic typography') ON CONFLICT DO NOTHING;
                INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
                    VALUES(@tenant,@document,@sourcePath,'typography.pdf','synthetic','indexed',1,1);
                INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                    VALUES(@revision,@tenant,@document,@sourcePath,@hash,1,1);
                """, new { tenant, document = document.DocumentId, revision = document.RevisionId,
                    sourcePath, hash = Convert.FromHexString(document.Source.Sha256) });
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, tenant,
                    document.RevisionId, bundle, CancellationToken.None);
                await transaction.CommitAsync();
            }

            var stored = await connection.QuerySingleAsync<StoredArtifact>("""
                SELECT artifact_type AS "ArtifactType",schema_version AS "SchemaVersion",
                    content_hash AS "ContentHash",byte_size AS "ByteSize",payload AS "Payload"
                FROM document_revision_binary_artifacts
                WHERE tenant_id=@tenant AND revision_id=@revision AND artifact_type=@type;
                """, new { tenant, revision = document.RevisionId, type = CanonicalArtifactRepo.BundleArtifactType });
            var restored = CanonicalArtifactRepo.ReadBundleArtifact(new(stored.ArtifactType, stored.SchemaVersion,
                stored.ContentHash, stored.ByteSize, stored.Payload));
            Assert.Equal(expected, CanonicalContractJson.SerializeCanonical(restored));
            var restoredObservation = Assert.IsType<CanonicalNativeTypographySet>(restored.Document.Pages[0].NativeTypographyObservations);
            Assert.Equal(observed.ObservedGlyphCount, restoredObservation.ObservedGlyphCount);
            Assert.Equal(observed.Runs!.Select(run => run.TextSha256), restoredObservation.Runs!.Select(run => run.TextSha256));
        }
    }

    private sealed record StoredArtifact(string ArtifactType, string SchemaVersion, byte[] ContentHash,
        long ByteSize, byte[] Payload);
}
