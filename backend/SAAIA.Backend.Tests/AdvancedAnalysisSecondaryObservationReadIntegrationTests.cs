using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;
using Fixture = SAAIA.Contracts.Tests.CanonicalTableRegionObservationFixture;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class AdvancedAnalysisSecondaryObservationReadIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Secondary_read_uses_persisted_raw_line_anchor_and_preserves_unavailable_physical_view()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        var observation = document.Pages[0].TableRegionObservations!.Observations![0];
        await Seed(database, tenant, document);
        var request = Request(document, observation);
        var read = await AdvancedAnalysisTableObservationReader.ReadAsync(database.DataSource, tenant, request,
            Observed(document), 20_000, CancellationToken.None);
        var line = Assert.Single(read.Readings.View!.Lines!);
        Assert.Equal(observation.Lines[0].Text, line.Text);
        var reference = Assert.Single(read.References);
        Assert.Null(reference.ChunkId);
        Assert.Equal(CanonicalTableObservationAnchors.Build(document)[0].AnchorId, reference.AnchorId);
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(new AdvancedAnalysisOptions()));
        var resolved = await resolver.ResolveAsync(tenant, read.References, CancellationToken.None);
        Assert.True(resolved.IsValid);
        var actual = Assert.Single(resolved.Evidence);
        Assert.Equal(observation.Lines[0].Text, actual.Content);
        Assert.Equal(observation.ObservationId, actual.SourceObservation!.ObservationId);
        Assert.Equal(observation.Lines[0].LineId, actual.Reference.SourceObservation!.LineId);
        Assert.Equal("not_verified", actual.Reference.SourceObservation.SemanticAccuracy);
        Assert.Equal(0, actual.SourceOverview!.CanonicalChunkCount);
        Assert.False(actual.SourceOverview.VisualAssets!.ImageToolAvailable);
        Assert.Equal(1, actual.SourceOverview.TableRegionObservations!.ObservationCount);

        var unavailable = await AdvancedAnalysisTableObservationReader.ReadAsync(database.DataSource, tenant,
            request with { TableObservationView = "unassigned" }, Observed(document), 20_000, CancellationToken.None);
        Assert.Equal("physical_reading_unavailable", unavailable.Readings.View!.Status);
        Assert.Null(unavailable.Readings.View.Lines);
        Assert.Null(unavailable.Readings.View.ReturnedLineCount);
        Assert.Empty(unavailable.References);
        // Raw is still available; a refused candidate ordering never removes it.
        var rawAgain = await AdvancedAnalysisTableObservationReader.ReadAsync(database.DataSource, tenant,
            request, Observed(document), 20_000, CancellationToken.None);
        Assert.Equal(line.Text, Assert.Single(rawAgain.Readings.View!.Lines!).Text);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Secondary_read_rejects_wrong_revision_source_tenant_page_and_anchor_payload()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Fixture.Attach();
        var observation = document.Pages[0].TableRegionObservations!.Observations![0];
        await Seed(database, tenant, document);
        var request = Request(document, observation);
        var observed = Observed(document);
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, Guid.NewGuid(), request, observed, 20_000, CancellationToken.None));
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, tenant, request with { RevisionId = Guid.NewGuid().ToString("D") }, observed, 20_000, CancellationToken.None));
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, tenant, request with { PageStart = 2, PageEnd = 2 }, observed, 20_000, CancellationToken.None));
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, tenant, request with { TableObservationId = "unobserved" }, observed, 20_000, CancellationToken.None));
        var wrongHash = new AdvancedAnalysisResultEvidence { DocId = observed.DocId, RevisionId = observed.RevisionId,
            SourceHash = new string('f', 64) };
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, tenant, request, wrongHash, 20_000, CancellationToken.None));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE document_source_anchors SET payload=jsonb_set(payload,'{regions,0,tableRegionObservationLineIds}', '[\"wrong-line\"]'::jsonb)");
        await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationReader.ReadAsync(
            database.DataSource, tenant, request, observed, 20_000, CancellationToken.None));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Secondary_inventory_keeps_known_empty_unknown_and_legacy_catalog_absent_distinct()
    {
        foreach (var state in new[] { "legacy", "observed", "unavailable" })
        {
            await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
            var tenant = Guid.NewGuid(); var document = Fixture.Attach();
            if (state == "observed") document.Pages[0].Tables.Clear();
            document.Pages[0].TableRegionObservations = state switch
            {
                "legacy" => null,
                "observed" => new() { Availability = "observed", Observations = [], Attempts = [] },
                _ => new() { Availability = "unavailable", Reason = "geometry_unavailable" }
            };
            await Seed(database, tenant, document);
            var read = await AdvancedAnalysisTableObservationReader.ReadAsync(database.DataSource, tenant,
                new("", null, 20, DocId: document.DocumentId.ToString("D"), RevisionId: document.RevisionId.ToString("D"),
                    PageStart: 1, PageEnd: 1, Operation: "read_source", TableObservationView: "inventory"),
                Observed(document), 20_000, CancellationToken.None);
            Assert.Empty(read.References);
            if (state == "legacy") Assert.Null(read.Readings.Inventory);
            else
            {
                Assert.Equal(state, read.Readings.Inventory!.Availability);
                if (state == "observed")
                {
                    Assert.Equal(0, read.Readings.Inventory.ObservationCount);
                    Assert.Empty(read.Readings.Inventory.Observations!);
                }
                else
                {
                    Assert.Null(read.Readings.Inventory.ObservationCount);
                    Assert.Null(read.Readings.Inventory.Observations);
                }
            }
        }
    }

    private static AdvancedAnalysisSearchRequest Request(CanonicalDocument document, CanonicalTableRegionObservation observation)
        => new("", null, 20, DocId: document.DocumentId.ToString("D"), RevisionId: document.RevisionId.ToString("D"),
            PageStart: 1, PageEnd: 1, Operation: "read_source", TableObservationId: observation.ObservationId,
            TableObservationView: "raw", TableObservationLineOffset: 0, TableObservationLineLimit: 16);

    private static AdvancedAnalysisResultEvidence Observed(CanonicalDocument document)
        => new() { DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
            SourceHash = document.Source.Sha256, PageStart = 1, PageEnd = 1 };

    private static async Task Seed(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic secondary reader');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version)
                VALUES(@tenant,@doc,'Synthetic/table.pdf','table.pdf','synthetic','indexed',1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                VALUES(@revision,@tenant,@doc,'Synthetic/table.pdf',@hash,1,1);
            """, new { tenant, doc = document.DocumentId, revision = document.RevisionId,
                hash = Convert.FromHexString(document.Source.Sha256) });
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalTableObservationRepo.StoreAsync(connection, transaction, tenant, document, CancellationToken.None);
        await CanonicalSourceAnchorStorage.WriteAsync(connection, transaction, tenant, document.RevisionId,
            CanonicalTableObservationAnchors.Build(document), CancellationToken.None);
        await transaction.CommitAsync();
    }
}
