using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class AdvancedAnalysisWorkerTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_find_searches_published_literal_table_rows_but_returns_html_and_original_anchors()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");
        var seed = await database.SeedEvidenceAsync("literal-structured-table", "Placeholder replaced by the actual publication method.");
        var source = FindTableSource();
        var sourceHash = Convert.ToHexString(Enumerable.Range(0, 32).Select(index => (byte)(index + 1)).ToArray()).ToLowerInvariant();
        var canonical = DoclingCanonicalDocumentAdapter.Project(new(seed.DocId, seed.RevisionId, sourceHash,
            100, seed.FileName, new string('a', 64), "canonical_projection", "fixture", "fixture"), source);
        var projected = DoclingCanonicalRetrievalProjector.Project(canonical, source, maxWords: 220, minWords: 1);
        var table = Assert.Single(projected);
        Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(table));
        Assert.Contains("data-source-table-ordinal", table.Text);
        var chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(seed.DocId, 1, table.ChunkIndex);
        var bundle = DoclingCanonicalBundleFactory.Create(new(seed.DocId, seed.RevisionId, Guid.NewGuid(), 1,
            sourceHash, 100, seed.FileName, "fixture", DateTimeOffset.UtcNow,
            new IngestionHardwareProfile
            {
                ProfileId="integration_fixture", LogicalProcessorCount=1, MemoryBytes=1_000_000,
                Devices=[new() { DeviceId="cpu:0", DeviceType="cpu" }]
            }, new(), [new()
            {
                StageId="canonical_projection", StageType="canonical_projection", Engine="SAAIA.Backend",
                EngineVersion="fixture", OptionsSha256=new string('b',64), DeviceId="cpu:0"
            }], canonical, new() { Document=new() { JsonContent=source } }, projected));
        var anchor = Assert.Single(bundle.SourceAnchors);

        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // Exercise the production SQL projection, including literalSearchText. No metadata
            // value is inserted by the fixture. SeedEvidence only supplies the scoped revision.
            var publish = typeof(DocumentFoundationRepo).GetMethod("UpsertRetrievalChunksAsync",
                BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException();
            await (Task)(publish.Invoke(null,
            [connection, transaction, seed.TenantId, seed.DocId, seed.RevisionId, 1,
                Array.Empty<ExtractedDocumentSection>(), Array.Empty<ExtractedDocumentUnit>(), projected,
                CancellationToken.None]) ?? throw new InvalidOperationException());
            await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, seed.TenantId, seed.RevisionId,
                bundle, CancellationToken.None);
            await transaction.CommitAsync();
        }

        string metadataBefore;
        string anchorBefore;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            metadataBefore = await connection.QuerySingleAsync<string>(
                "SELECT metadata::text FROM retrieval_chunks WHERE retrieval_chunk_id=@chunk", new { chunk=chunkId });
            anchorBefore = await connection.QuerySingleAsync<string>(
                "SELECT payload::text FROM document_source_anchors WHERE revision_id=@revision", new { revision=seed.RevisionId });
        }
        using (var metadata = JsonDocument.Parse(metadataBefore))
        {
            var literal = metadata.RootElement.GetProperty("literalSearchText").GetString()!;
            Assert.Contains("-5 °C +5 °C", literal); // The production storage view normalizes cell whitespace.
            Assert.Contains("AC|DC <limit>", literal);
            Assert.Contains("Do not open & remove interlock", literal);
            Assert.DoesNotContain("data-source-table", literal);
            Assert.Equal(table.CanonicalTableCellIds!.Order(StringComparer.Ordinal), metadata.RootElement
                .GetProperty("canonicalTableCellIds").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal));
        }
        var settings = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(settings));
        var initial = await resolver.ResolveAsync(seed.TenantId,
            [new AdvancedAnalysisEvidenceReference { ChunkId=chunkId.ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, httpFactory, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, settings);
        var request = new AdvancedAnalysisSearchRequest("-5 °C +5 °C", TopK: 10, DocId: seed.DocId.ToString(),
            DocPath: seed.DocPath, RevisionId: seed.RevisionId.ToString(), Operation: "find_source_text");
        foreach (var query in new[] { "-5 °C +5 °C", "AC|DC <limit>", "open & remove" })
        {
            var observation = await gateway.SearchAsync(request with { Query=query }, CancellationToken.None);
            var found = Assert.Single(observation.Evidence);
            Assert.Equal(table.Text, found.Content);
            Assert.Contains("&lt;limit&gt;", found.Content);
            Assert.Contains("&amp;", found.Content);
            Assert.Equal(chunkId.ToString(), found.Reference.ChunkId);
            Assert.Equal(anchor.AnchorId, found.Reference.AnchorId);
            Assert.Equal(seed.DocId.ToString(), found.Reference.DocId);
            Assert.Equal(seed.RevisionId.ToString(), found.Reference.RevisionId);
            Assert.Equal(sourceHash, found.Reference.SourceHash);
            Assert.Equal(2, found.Reference.PageStart);
            Assert.Equal(2, found.Reference.PageEnd);
        }
        var technical = await gateway.SearchAsync(request with { Query="data-source-table" }, CancellationToken.None);
        Assert.Empty(technical.Evidence);
        Assert.Equal("no_literal_match_at_offset_in_observed_revision", technical.FindDiagnostic!.Status);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(metadataBefore, await connection.QuerySingleAsync<string>(
                "SELECT metadata::text FROM retrieval_chunks WHERE retrieval_chunk_id=@chunk", new { chunk=chunkId }));
            Assert.Equal(anchorBefore, await connection.QuerySingleAsync<string>(
                "SELECT payload::text FROM document_source_anchors WHERE revision_id=@revision", new { revision=seed.RevisionId }));
        }
    }

    private static DoclingDocument FindTableSource()
    {
        var cells = new List<DoclingTableCell>();
        var values = new[] { new[] { "Assembly", "Minimum", "Maximum", "Instruction" },
            new[] { "AC|DC <limit>", "-5 °C", "+5 °C", "Do not open & remove interlock" } };
        for (var row=0; row<2; row++) for (var column=0; column<4; column++)
            cells.Add(new()
            {
                RowIndex=row, ColumnIndex=column, Text=values[row][column], IsColumnHeader=row==0,
                BoundingBox=new() { Left=10+column*100, Right=110+column*100, Top=30+row*30, Bottom=60+row*30, CoordinateOrigin="TOPLEFT" }
            });
        return new()
        {
            SchemaName="DoclingDocument", Version="1.4.0",
            Body=new() { Children=[new() { Ref="#/tables/0" }] },
            Pages={ ["2"]=new() { PageNumber=2, Size=new() { Width=600, Height=800 } } },
            Tables=[new()
            {
                SelfRef="#/tables/0", Label="table", ContentLayer="body",
                Provenance=[new() { PageNumber=2, BoundingBox=new()
                    { Left=10, Right=410, Top=30, Bottom=90, CoordinateOrigin="TOPLEFT" } }],
                Data=new() { RowCount=2, ColumnCount=4, Cells=cells }
            }]
        };
    }
}
