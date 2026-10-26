using Dapper;
using Microsoft.Extensions.Options;
using SAAIA.Backend;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

[Trait("Profile", "IntegrationPg")]
public sealed class CanonicalVisualAssetRepoIntegrationTests
{
    [ProfileFact("IntegrationPg")]
    public async Task A_single_page_collection_can_preserve_its_actual_physical_page_fifteen()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant=Guid.NewGuid(); var document=Document(Guid.NewGuid(),Guid.NewGuid(),physicalPage:15);
        Assert.Equal(1,document.PageCount);
        await Seed(database,tenant,document); await Store(database,tenant,document);
        var stored=await CanonicalVisualAssetRepo.ReadAsync(database.DataSource,tenant,document.DocumentId,
            document.RevisionId,document.Assets[0].AssetId,CancellationToken.None);
        Assert.Equal(15,stored!.Descriptor.PageNumber);
    }
    [ProfileFact("IntegrationPg")]
    public async Task Exact_revision_pixels_round_trip_and_do_not_cross_tenants_documents_or_revisions()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid();
        var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document);
        await Store(database, tenant, document);
        var asset = Assert.Single(document.Assets);
        var found = await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, asset.AssetId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(asset.PublicationBytes, found!.Bytes);
        Assert.Equal(document.Source.Sha256, found.Descriptor.SourceSha256);
        Assert.Null(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, Guid.NewGuid(), document.DocumentId,
            document.RevisionId, asset.AssetId, CancellationToken.None));
        Assert.Null(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, Guid.NewGuid(),
            document.RevisionId, asset.AssetId, CancellationToken.None));
        Assert.Null(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            Guid.NewGuid(), asset.AssetId, CancellationToken.None));
        var next = Document(document.DocumentId, Guid.NewGuid(), 'b');
        await Seed(database, tenant, next, existing: true);
        await Store(database, tenant, next);
        Assert.Null(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            next.RevisionId, asset.AssetId, CancellationToken.None));
        Assert.NotNull(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, asset.AssetId, CancellationToken.None));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Asset_publication_rolls_back_with_its_canonical_transaction()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await CanonicalVisualAssetRepo.StoreAsync(connection, transaction, tenant, document, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        var result = await CanonicalVisualAssetRepo.ListAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, null, null, CancellationToken.None);
        Assert.Equal(0, result.AvailableAssetCount);
        Assert.Equal("pixels_not_available", result.Status);
        Assert.False(result.ImageToolAvailable);
    }

    [ProfileFact("IntegrationPg")]
    public async Task Same_asset_identity_cannot_be_republished_with_other_pixels_or_a_wrong_source()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document);
        await Store(database, tenant, document);
        var asset = Assert.Single(document.Assets);
        var originalHash = asset.Sha256;
        asset.Sha256 = new string('f', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store(database, tenant, document));
        asset.Sha256 = originalHash;
        document.Source.Sha256 = new string('b', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store(database, tenant, document));
        var found = await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, asset.AssetId, CancellationToken.None);
        Assert.Equal(VisualAssetTestPng.Bytes, found!.Bytes);
    }

    [ProfileFact("IntegrationPg")]
    public async Task A_deleted_document_no_longer_exposes_retained_pixels()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document); await Store(database, tenant, document);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE documents SET indexed_version=0,status='deleted' WHERE doc_id=@doc", new { doc = document.DocumentId });
        Assert.Null(await CanonicalVisualAssetRepo.ReadAsync(database.DataSource, tenant, document.DocumentId,
            document.RevisionId, document.Assets[0].AssetId, CancellationToken.None));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Stored_pixels_are_verified_again_before_they_can_leave_the_server()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document); await Store(database, tenant, document);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE document_revision_visual_assets SET content_hash=decode(repeat('ff',32),'hex') WHERE revision_id=@revision", new { revision = document.RevisionId });
        await Assert.ThrowsAsync<InvalidDataException>(() => CanonicalVisualAssetRepo.ReadAsync(database.DataSource,
            tenant, document.DocumentId, document.RevisionId, document.Assets[0].AssetId, CancellationToken.None));
    }

    [ProfileFact("IntegrationPg")]
    public async Task Source_reading_exposes_pixels_availability_without_claiming_a_visual_model_tool()
    {
        await using var database = await LocalPostgresDiagnosticFixture.CreateAsync(migrateProduct: true);
        var tenant = Guid.NewGuid(); var document = Document(Guid.NewGuid(), Guid.NewGuid());
        await Seed(database, tenant, document); await Store(database, tenant, document);
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(new AdvancedAnalysisOptions()));
        var result = await resolver.ResolveAsync(tenant, [new AdvancedAnalysisEvidenceReference
        {
            DocId = document.DocumentId.ToString(), RevisionId = document.RevisionId.ToString(), PageStart = 1, PageEnd = 1
        }], CancellationToken.None);
        Assert.True(result.IsValid);
        var availability = Assert.Single(result.Evidence).SourceOverview!.VisualAssets!;
        Assert.Equal("pixels_available", availability.Status);
        Assert.Equal(1, availability.AvailableAssetCount);
        Assert.Equal("not_performed", availability.InterpretationStatus);
        Assert.False(availability.ImageToolAvailable);
    }

    private static CanonicalDocument Document(Guid document, Guid revision, char hash = 'a', int physicalPage = 1)
    {
        var source = new DoclingDocument
        {
            Pages = { [physicalPage.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new() { PageNumber = physicalPage, Size = new() { Width = 100, Height = 200 } } },
            Pictures = [new() { SelfRef = "#/pictures/0", Label = "picture", Image = VisualAssetTestPng.Image(),
                Provenance = [new() { PageNumber = physicalPage }] }]
        };
        return DoclingCanonicalDocumentAdapter.Project(new(document, revision, new string(hash,64),123,
            "synthetic.pdf",new string('d',64),"canonical_projection","fixture","test"), source);
    }

    private static async Task Seed(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document, bool existing = false)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        if (!existing) await connection.ExecuteAsync("""
            INSERT INTO tenants(tenant_id,name) VALUES(@tenant,'Synthetic visual assets');
            INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,status,ingestion_version,indexed_version,page_count)
                VALUES(@tenant,@doc,'Synthetic/visual.pdf','visual.pdf','synthetic','indexed',1,1,1);
            """, new { tenant, doc = document.DocumentId });
        else await connection.ExecuteAsync("UPDATE documents SET ingestion_version=2,indexed_version=2 WHERE tenant_id=@tenant AND doc_id=@doc",
            new { tenant,doc=document.DocumentId });
        await connection.ExecuteAsync("""
            INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,ingestion_version,indexed_version)
                VALUES(@revision,@tenant,@doc,'Synthetic/visual.pdf',@hash,@version,@version);
            INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,page_start,page_end,text_content)
                VALUES(@chunk,@tenant,@revision,0,1,1,'Synthetic source caption.');
            """, new { tenant, doc=document.DocumentId, revision=document.RevisionId, hash=Convert.FromHexString(document.Source.Sha256), chunk=Guid.NewGuid(),version=existing?2:1 });
    }

    private static async Task Store(LocalPostgresDiagnosticFixture database, Guid tenant, CanonicalDocument document)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CanonicalVisualAssetRepo.StoreAsync(connection,transaction,tenant,document,CancellationToken.None);
        await transaction.CommitAsync();
    }
}
