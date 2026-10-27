using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Contracts.Tests;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class AdvancedAnalysisNativeObservationReadIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Native_audit_reader_and_resolver_replay_exact_unicode_lines_from_published_gzip_without_writes()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var bundle = Bundle("Native’s -5,25 °C.\r\nSecond 😀 exact line.");
        await Publish(database, tenant, bundle);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var before = await Digest(connection);
        var observed = Observed(bundle.Document);
        var inventory = await Read(database, tenant, observed, new("read", DocId: observed.DocId,
            RevisionId: observed.RevisionId, DocPath: observed.DocPath, Operation: "read_source", PageStart: 1, PageEnd: 1,
            NativeObservationView: "inventory"));
        var descriptor = Assert.Single(inventory.Readings.Inventory!.Observations!);
        var request = new AdvancedAnalysisSearchRequest("read", DocId: observed.DocId, RevisionId: observed.RevisionId,
            DocPath: observed.DocPath, Operation: "read_source", PageStart: 1, PageEnd: 1,
            NativeObservationView: "raw", NativeObservationBlockId: descriptor.BlockId,
            NativeObservationRawSha256: descriptor.RawTextSha256, NativeObservationLimit: 1);
        var first = await Read(database, tenant, observed, request);
        var second = await Read(database, tenant, observed, request with { NativeObservationOffset = 1 });
        var references = first.References.Concat(second.References).ToArray();
        Assert.All(references, reference => { Assert.Null(reference.ChunkId); Assert.Null(reference.AnchorId); });
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(new AdvancedAnalysisOptions
        {
            NativeObservationReadEnabled = true
        }));
        var resolved = await resolver.ResolveAsync(tenant, references, CancellationToken.None);
        Assert.True(resolved.IsValid, resolved.ErrorCode);
        Assert.Equal(bundle.Document.Pages[0].Blocks[^1].Text.Raw, string.Concat(resolved.Evidence.Select(item => item.Content)));
        Assert.All(resolved.Evidence, item =>
        {
            Assert.NotNull(item.NativeObservation);
            Assert.NotNull(item.Reference.NativeObservation);
            Assert.StartsWith("advanced-native-evidence-", item.Reference.EvidenceId!);
            Assert.Equal("not_verified", item.SourceQuality!.SemanticAccuracy);
        });
        Assert.Equal(before, await Digest(connection));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM retrieval_chunks"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM ingestion_jobs"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Native_audit_scope_and_raw_hash_mismatch_never_fall_back_to_primary_or_other_tenant()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var bundle = Bundle("Native’s exact literal.");
        await Publish(database, tenant, bundle);
        var actual = Observed(bundle.Document);
        var request = new AdvancedAnalysisSearchRequest("read", DocId: actual.DocId, RevisionId: actual.RevisionId,
            Operation: "read_source", PageStart: 1, PageEnd: 1, NativeObservationView: "inventory");
        foreach (var identity in new[]
        {
            (Tenant: Guid.NewGuid(), Document: actual.DocId, Revision: actual.RevisionId, Source: actual.SourceHash),
            (Tenant: tenant, Document: Guid.NewGuid().ToString("D"), Revision: actual.RevisionId, Source: actual.SourceHash),
            (Tenant: tenant, Document: actual.DocId, Revision: Guid.NewGuid().ToString("D"), Source: actual.SourceHash),
            (Tenant: tenant, Document: actual.DocId, Revision: actual.RevisionId, Source: new string('e', 64))
        })
        {
            var observed = new AdvancedAnalysisResultEvidence { DocId = identity.Document, RevisionId = identity.Revision,
                SourceHash = identity.Source, PageStart = 1, PageEnd = 1 };
            var failure = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
                Read(database, identity.Tenant, observed, request with { DocId = identity.Document, RevisionId = identity.Revision }));
            Assert.Equal("native_observation_source_unavailable", failure.ErrorCode);
        }
        var hashFailure = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => Read(database, tenant, actual,
            request with { NativeObservationView = "raw", NativeObservationBlockId = "native-audit", NativeObservationRawSha256 = new string('e', 64) }));
        Assert.Equal("native_observation_identity_or_window_invalid", hashFailure.ErrorCode);
        var pathFailure = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            Read(database, tenant, actual, request with { DocPath = "another.pdf" }));
        Assert.Equal("native_observation_source_identity_invalid", pathFailure.ErrorCode);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Native_audit_empty_retained_raw_and_missing_observation_have_distinct_known_and_unknown_states()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var bundle = Bundle("");
        await Publish(database, tenant, bundle);
        var observed = Observed(bundle.Document);
        var request = new AdvancedAnalysisSearchRequest("read", DocId: observed.DocId, RevisionId: observed.RevisionId,
            Operation: "read_source", PageStart: 1, PageEnd: 1, NativeObservationView: "inventory");
        var inventory = await Read(database, tenant, observed, request);
        var descriptor = Assert.Single(inventory.Readings.Inventory!.Observations!);
        Assert.Equal(0, descriptor.RawLineCount);
        var empty = await Read(database, tenant, observed, request with { NativeObservationView = "raw",
            NativeObservationBlockId = descriptor.BlockId, NativeObservationRawSha256 = descriptor.RawTextSha256 });
        Assert.Empty(empty.Readings.View!.Lines);
        Assert.Empty(empty.References);
        Assert.Equal("observed", empty.Readings.View.Status);
        bundle.Document.Pages[0].Blocks.RemoveAt(bundle.Document.Pages[0].Blocks.Count - 1);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, tenant, bundle.Document.RevisionId, bundle, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var unavailable = await Read(database, tenant, observed, request);
        Assert.Equal("unavailable", unavailable.Readings.Inventory!.Availability);
        Assert.Null(unavailable.Readings.Inventory.ObservationCount);
        Assert.Null(unavailable.Readings.Inventory.Observations);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Native_audit_corrupted_gzip_hash_is_a_safe_integrity_refusal_before_any_line_is_published()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var bundle = Bundle("PRIVATE_SENTINEL retained raw text.");
        await Publish(database, tenant, bundle);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE document_revision_binary_artifacts SET content_hash=decode(repeat('ee',32),'hex')");
        var observed = Observed(bundle.Document);
        var failure = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => Read(database, tenant, observed,
            new("read", DocId: observed.DocId, RevisionId: observed.RevisionId, PageStart: 1, PageEnd: 1,
                Operation: "read_source", NativeObservationView: "inventory")));
        Assert.Equal("native_observation_bundle_integrity_invalid", failure.ErrorCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("PRIVATE_SENTINEL", failure.ToString(), StringComparison.Ordinal);
    }

    private static Task<AdvancedAnalysisNativeObservationRead> Read(LocalPostgresDiagnosticFixture database, Guid tenant,
        AdvancedAnalysisResultEvidence observed, AdvancedAnalysisSearchRequest request)
        => AdvancedAnalysisNativeObservationReader.ReadAsync(database.DataSource, tenant, request, observed, 30_000, CancellationToken.None);

    private static AdvancedAnalysisResultEvidence Observed(CanonicalDocument document) => new()
    {
        DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
        SourceHash = document.Source.Sha256, DocPath = "Synthetic/native.pdf", PageStart = 1, PageEnd = 1
    };

    private static CanonicalIngestionBundle Bundle(string raw)
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
        bundle.Document = document;
        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        return bundle;
    }

    private static async Task Publish(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalIngestionBundle bundle)
    {
        var document = bundle.Document;
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic native audit');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
              VALUES(@tenant,@doc,'Synthetic/native.pdf','native.pdf','synthetic','indexed',1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
              VALUES(@revision,@tenant,@doc,'Synthetic/native.pdf',@hash,1,1);
            """, new { tenant, doc = document.DocumentId, revision = document.RevisionId, hash = Convert.FromHexString(document.Source.Sha256) });
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, tenant, document.RevisionId, bundle, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static Task<string?> Digest(Npgsql.NpgsqlConnection connection)
        => connection.ExecuteScalarAsync<string>("""
            SELECT md5(string_agg(encode(content_hash,'hex')||':'||encode(payload,'hex'),',' ORDER BY revision_id,artifact_type))
            FROM document_revision_binary_artifacts;
            """);
}
