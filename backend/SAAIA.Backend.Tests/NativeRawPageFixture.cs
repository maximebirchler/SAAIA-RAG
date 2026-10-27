using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Contracts.Tests;

namespace SAAIA.Backend.Tests;

internal static class NativeRawPageFixture
{
    internal static CanonicalIngestionBundle Bundle(string raw = "Native’s exact 😀.\r\nSecond line.")
    {
        var document = CanonicalContractFixture.CreateDocument();
        var bundle = LegacyCanonicalBundleFactory.Create(new(document.DocumentId, document.RevisionId, Guid.NewGuid(), 1,
            document.Source.Sha256, 42, "sample.pdf", "fixture", DateTimeOffset.UtcNow,
            new() { ProfileId = "fixture", LogicalProcessorCount = 1, MemoryBytes = 1024,
                Devices = [new() { DeviceId = "cpu", DeviceType = "cpu" }] },
            [new() { StageId = "native_parser", StageType = "canonical_projection", Engine = "fixture", EngineVersion = "1",
                OptionsSha256 = new string('d', 64), DeviceId = "cpu" }],
            new([], [], PdfExtractionQualitySummary.FromPages([])), [], []));
        document.ManifestSha256 = bundle.ManifestEnvelope.ManifestSha256;
        document.Pages[0].Blocks.Add(new()
        {
            BlockId = "native-audit", BlockType = "native_layout_recovery", Text = CanonicalContractFixture.Text(raw),
            Provenance = new() { Engine = "PdfPig", EngineVersion = "fixture", Method = "native_layout_reading_order_reconciliation", StageId = "native_parser" }
        });
        document.Pages.Add(new() { PageNumber = 2, Geometry = new() { Width = 595, Height = 842, Unit = "pt" } });
        document.PageCount = 2;
        bundle.Document = document;
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        return bundle;
    }

    internal static async Task Seed(NpgsqlConnection connection, Guid tenant, CanonicalIngestionBundle bundle)
    {
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic native pages');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
              VALUES(@tenant,@doc,'Synthetic/native.pdf','native.pdf','synthetic','indexed',1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
              VALUES(@revision,@tenant,@doc,'Synthetic/native.pdf',@hash,1,1);
            """, new { tenant, doc = bundle.Document.DocumentId, revision = bundle.Document.RevisionId,
            hash = Convert.FromHexString(bundle.Document.Source.Sha256) });
    }

    internal static async Task Publish(NpgsqlConnection connection, Guid tenant, CanonicalIngestionBundle bundle)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, tenant, bundle.Document.RevisionId, bundle, CancellationToken.None);
        await transaction.CommitAsync();
    }
}
