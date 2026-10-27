using System.Text.Json;
using Dapper;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class AdvancedAnalysisVisualSourceReadIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task Visual_citation_metadata_is_bound_to_tenant_document_revision_source_and_requested_physical_page()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var document = Document();
        await Publish(database, tenant, document);
        await using var connection = await database.DataSource.OpenConnectionAsync();

        var first = await CanonicalVisualAssetRepo.ListCitationsAsync(connection, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256.ToUpperInvariant(), 600, 600, 0, 1, CancellationToken.None);
        var second = await CanonicalVisualAssetRepo.ListCitationsAsync(connection, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256, 600, 600, 1, 1, CancellationToken.None);
        Assert.True(first.ScopeExists);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.NotEqual(Assert.Single(first.Assets!).AssetId, Assert.Single(second.Assets!).AssetId);
        Assert.All(first.Assets!, asset => Assert.Equal(600, asset.PageNumber));

        foreach (var scope in new[]
        {
            (Tenant: Guid.NewGuid(), Document: document.DocumentId, Revision: document.RevisionId, Hash: document.Source.Sha256),
            (Tenant: tenant, Document: Guid.NewGuid(), Revision: document.RevisionId, Hash: document.Source.Sha256),
            (Tenant: tenant, Document: document.DocumentId, Revision: Guid.NewGuid(), Hash: document.Source.Sha256),
            (Tenant: tenant, Document: document.DocumentId, Revision: document.RevisionId, Hash: new string('b', 64))
        })
        {
            var missing = await CanonicalVisualAssetRepo.ListCitationsAsync(connection, scope.Tenant, scope.Document,
                scope.Revision, scope.Hash, 600, 600, 0, 4, CancellationToken.None);
            Assert.False(missing.ScopeExists);
            Assert.Null(missing.TotalCount);
            Assert.Null(missing.Assets);
        }
    }

    [ProfileFact("IntegrationPg")]
    public async Task Visual_citation_empty_page_and_end_offset_keep_known_counts_distinct_from_unknown_scope()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var document = Document();
        await Publish(database, tenant, document);
        await using var connection = await database.DataSource.OpenConnectionAsync();

        var emptyPage = await CanonicalVisualAssetRepo.ListCitationsAsync(connection, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256, 599, 599, 0, 4, CancellationToken.None);
        Assert.True(emptyPage.ScopeExists);
        Assert.Equal(0, emptyPage.TotalCount);
        Assert.Empty(emptyPage.Assets!);

        var exhausted = await CanonicalVisualAssetRepo.ListCitationsAsync(connection, tenant, document.DocumentId,
            document.RevisionId, document.Source.Sha256, 600, 600, 2, 4, CancellationToken.None);
        Assert.True(exhausted.ScopeExists);
        Assert.Equal(2, exhausted.TotalCount);
        Assert.Empty(exhausted.Assets!);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Visual_reader_returns_exact_metadata_without_primary_text_or_pixels_and_retains_unknown_viewer()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var document = Document();
        await Publish(database, tenant, document);
        var observed = new AdvancedAnalysisResultEvidence
        {
            EvidenceId = "E1",
            DocId = document.DocumentId.ToString("D"),
            RevisionId = document.RevisionId.ToString("D"),
            SourceHash = document.Source.Sha256,
            PageStart = 600,
            PageEnd = 600
        };
        var request = new AdvancedAnalysisSearchRequest("read", DocId: observed.DocId, RevisionId: observed.RevisionId,
            PageStart: 600, PageEnd: 600, Operation: "read_source", IncludeVisualSources: true);
        var result = await AdvancedAnalysisVisualSourceReader.ReadAsync(database.DataSource, tenant,
            request, observed, [], 20_000, CancellationToken.None);

        Assert.Equal(2, result.Assets!.Count);
        Assert.All(result.Assets, asset =>
        {
            Assert.Equal(document.Source.Sha256, asset.SourceSha256);
            Assert.Equal(600, asset.PhysicalPage);
            Assert.Null(asset.ObservedRegion);
        });
        Assert.True(result.AuthenticatedPixelReadAvailable);
        Assert.Null(result.HumanViewerAvailable);
        Assert.False(result.ImageToolAvailable);
        Assert.False(result.ModelPixelsProvided);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM retrieval_chunks"));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Visual_reader_hash_checks_the_observed_anchor_and_never_assigns_its_region_to_a_figure()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var document = Document();
        await Publish(database, tenant, document);
        var anchor = new SourceAnchor
        {
            AnchorId = "anchor_visual_1",
            DocumentId = document.DocumentId,
            RevisionId = document.RevisionId,
            SourceSha256 = document.Source.Sha256,
            ManifestSha256 = new string('d', 64),
            ProjectionId = "source_block_1",
            ProjectionType = "block",
            Precision = "block",
            Regions =
            [
                new()
                {
                    PageNumber = 600,
                    BlockIds = ["source_block_1"],
                    RawTextSha256 = new string('e', 64),
                    Polygon = new()
                    {
                        Points = [new() { X = .1, Y = .1 }, new() { X = .2, Y = .1 }, new() { X = .2, Y = .2 }]
                    }
                }
            ]
        };
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO document_source_anchors(tenant_id,revision_id,anchor_id,projection_id,projection_type,
                precision,page_start,page_end,payload,content_hash,manifest_hash)
                VALUES(@tenant,@revision,@anchor,@projection,@type,'block',600,600,@payload::jsonb,@hash,@manifest);
            """, new
        {
            tenant,
            revision = document.RevisionId,
            anchor = anchor.AnchorId,
            projection = anchor.ProjectionId,
            type = anchor.ProjectionType,
            payload = CanonicalContractJson.SerializeCanonical(anchor),
            hash = Convert.FromHexString(CanonicalContractJson.ComputeSha256(anchor)),
            manifest = Convert.FromHexString(anchor.ManifestSha256)
        });
        var observed = new AdvancedAnalysisResultEvidence
        {
            DocId = document.DocumentId.ToString("D"),
            RevisionId = document.RevisionId.ToString("D"),
            SourceHash = document.Source.Sha256
        };
        var request = new AdvancedAnalysisSearchRequest("read", DocId: observed.DocId, RevisionId: observed.RevisionId,
            PageStart: 600, PageEnd: 600, Operation: "read_source", IncludeVisualSources: true);
        AdvancedAnalysisEvidenceReference[] references =
        [
            new()
            {
                EvidenceId = "E1",
                DocId = observed.DocId.ToUpperInvariant(),
                RevisionId = observed.RevisionId.ToUpperInvariant(),
                SourceHash = observed.SourceHash.ToUpperInvariant(),
                PageStart = 600,
                PageEnd = 600,
                AnchorId = anchor.AnchorId
            }
        ];
        var result = await AdvancedAnalysisVisualSourceReader.ReadAsync(database.DataSource, tenant,
            request, observed, references, 20_000, CancellationToken.None);
        var page = Assert.Single(result.Assets!, asset => asset.FigureId is null);
        Assert.Equal("page_with_observed_region", page.ReferenceKind);
        Assert.Equal("E1", page.ObservedRegion!.EvidenceId);
        Assert.Equal(anchor.AnchorId, page.ObservedRegion.AnchorId);
        Assert.Null(Assert.Single(result.Assets!, asset => asset.FigureId is not null).ObservedRegion);

        await connection.ExecuteAsync("""
            UPDATE document_source_anchors SET content_hash=decode(repeat('ff',32),'hex') WHERE anchor_id=@anchor;
            """, new { anchor = anchor.AnchorId });
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            AdvancedAnalysisVisualSourceReader.ReadAsync(database.DataSource, tenant,
                request, observed, references, 20_000, CancellationToken.None));
        Assert.Equal("visual_source_anchor_integrity_failed", error.ErrorCode);
    }

    private static CanonicalDocument Document()
    {
        var source = new DoclingDocument
        {
            Pages =
            {
                ["600"] = new()
                {
                    PageNumber = 600,
                    Size = new() { Width = 100, Height = 200 },
                    Image = VisualAssetTestPng.Image()
                }
            },
            Pictures =
            [
                new()
                {
                    SelfRef = "#/pictures/0",
                    Label = "picture",
                    Image = VisualAssetTestPng.Image(),
                    Provenance = [new() { PageNumber = 600 }]
                }
            ]
        };
        return DoclingCanonicalDocumentAdapter.Project(new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), 123,
            "synthetic.pdf", new string('d', 64), "canonical_projection", "fixture", "test"), source);
    }

    private static async Task Publish(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic visual citation');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version,page_count)
                VALUES(@tenant,@doc,'Synthetic/visual.pdf','visual.pdf','synthetic','indexed',1,1,1);
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                VALUES(@revision,@tenant,@doc,'Synthetic/visual.pdf',@hash,1,1);
            """, new
        {
            tenant,
            doc = document.DocumentId,
            revision = document.RevisionId,
            hash = Convert.FromHexString(document.Source.Sha256)
        });
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalVisualAssetRepo.StoreAsync(connection, transaction, tenant, document, CancellationToken.None);
        await transaction.CommitAsync();
    }
}
