using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
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
    private static readonly JsonSerializerOptions TableReadJson = new(JsonSerializerDefaults.Web);

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_read_source_table_cell_bindings_follow_coordinates_not_source_array_order()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("Isolated PostgreSQL fixture is required.");
        var fixture = await PublishReadTableAsync(database, shuffledSpanCells:true);
        var observation = await ReadPublishedTableAsync(database, fixture, TableReadRequest(fixture.Seed));
        var rows = observation.Evidence.SelectMany(item => item.TableFragment!.Rows)
            .Where(row => row.Index == 1).ToArray();
        var actual = Assert.Single(rows);
        Assert.Equal(fixture.ExpectedCellIdsByRow[1].Order(StringComparer.Ordinal),actual.CellIds.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.ExpectedCellIdsByRow[2][0],actual.CellIds);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_read_source_assembles_source_rows_without_mutating_fragment_evidence_or_anchors()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("Isolated PostgreSQL fixture is required.");
        var fixture = await PublishReadTableAsync(database);
        var observation = await ReadPublishedTableAsync(database, fixture, TableReadRequest(fixture.Seed));
        Assert.Equal(fixture.Chunks.Count, observation.Evidence.Count);
        foreach (var evidence in observation.Evidence)
        {
            var index = fixture.Ids.IndexOf(Guid.Parse(evidence.Reference.ChunkId!));
            Assert.Equal(fixture.Chunks[index].Text, evidence.Content);
            Assert.False(string.IsNullOrWhiteSpace(evidence.Reference.AnchorId));
        }
        var table = SingleTableReadView(observation);
        Assert.True(table.GetProperty("complete").GetBoolean());
        Assert.Equal(6, table.GetProperty("totalRows").GetInt32());
        Assert.Equal(4, table.GetProperty("totalColumns").GetInt32());
        Assert.Equal(Enumerable.Range(0, 6), ReadTableInts(table, "rowIndices"));
        Assert.Equal(fixture.Seed.DocId.ToString(), table.GetProperty("docId").GetString());
        Assert.Equal(fixture.Seed.RevisionId.ToString(), table.GetProperty("revisionId").GetString());
        Assert.Equal(2, table.GetProperty("physicalPage").GetInt32());
        Assert.Equal(0, table.GetProperty("tableOrdinal").GetInt32());
        var html = XDocument.Parse(table.GetProperty("html").GetString()!);
        Assert.Equal(6, html.Root!.Elements("tr").Count());
        Assert.Equal(1, html.Descendants("th").Count(cell => cell.Value == "Minimum"));
        Assert.Contains("-5 °C", html.ToString());
        Assert.Contains("+5 °C", html.ToString());
        Assert.Contains("AC|DC &lt;limit&gt;", html.ToString());
        Assert.Equal(observation.Evidence.Select(item => item.Reference.EvidenceId).Order(StringComparer.Ordinal),
            ReadTableStrings(table, "evidenceIds").Order(StringComparer.Ordinal));
        Assert.Equal(fixture.Ids.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            ReadTableStrings(table, "chunkIds").Order(StringComparer.Ordinal));
        Assert.Equal(fixture.Chunks.SelectMany(chunk => chunk.CanonicalTableCellIds!).Distinct().Order(StringComparer.Ordinal),
            ReadTableStrings(table, "canonicalTableCellIds").Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, table.GetProperty("nextRowOffset").ValueKind);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_read_source_reports_missing_rows_instead_of_calling_partial_table_complete()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("Isolated PostgreSQL fixture is required.");
        var fixture = await PublishReadTableAsync(database);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("DELETE FROM retrieval_chunks WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunk",
                new { tenant=fixture.Seed.TenantId, chunk=fixture.Ids[1] });
        var observation = await ReadPublishedTableAsync(database, fixture, TableReadRequest(fixture.Seed));
        var table = SingleTableReadView(observation);
        Assert.False(table.GetProperty("complete").GetBoolean());
        Assert.Equal(6, table.GetProperty("totalRows").GetInt32());
        Assert.NotEqual(Enumerable.Range(0, 6), ReadTableInts(table, "rowIndices"));
        Assert.DoesNotContain(fixture.Ids[1].ToString(), ReadTableStrings(table, "chunkIds"));
        Assert.All(ReadTableStrings(table, "evidenceIds"), id =>
            Assert.Contains(observation.Evidence, evidence => evidence.Reference.EvidenceId == id));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_read_source_table_pagination_preserves_headers_and_reports_next_original_row()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("Isolated PostgreSQL fixture is required.");
        var fixture = await PublishReadTableAsync(database);
        var json = JsonSerializer.SerializeToElement(TableReadRequest(fixture.Seed), TableReadJson);
        var fields = json.EnumerateObject().ToDictionary(property => property.Name,
            property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        fields["tableOrdinal"] = 0;
        fields["tableRowOffset"] = 3;
        fields["tableRowLimit"] = 2;
        var request = JsonSerializer.Deserialize<AdvancedAnalysisSearchRequest>(
            JsonSerializer.Serialize(fields, TableReadJson), TableReadJson)!;
        var observation = await ReadPublishedTableAsync(database, fixture, request);
        var table = SingleTableReadView(observation);
        Assert.False(table.GetProperty("complete").GetBoolean());
        Assert.Equal(new[] { 0, 3, 4 }, ReadTableInts(table, "rowIndices"));
        Assert.Equal(5, table.GetProperty("nextRowOffset").GetInt32());
        Assert.Equal(3, XDocument.Parse(table.GetProperty("html").GetString()!).Root!.Elements("tr").Count());
        Assert.Equal(fixture.Chunks.Count, observation.Evidence.Count);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_read_source_table_view_is_sent_to_the_actual_provider_request()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null) throw new InvalidOperationException("Isolated PostgreSQL fixture is required.");
        var fixture = await PublishReadTableAsync(database);
        var observation = await ReadPublishedTableAsync(database, fixture, TableReadRequest(fixture.Seed));
        using var http = new TableReadCompletionFactory();
        var settings = BuildAdvancedOptions();
        settings.Provider="customer-server";
        settings.LlmLocation="internal";
        settings.LlmBaseUrl="http://unit-test.invalid";
        settings.LlmModel="fixture";
        settings.SemanticCriticEnabled=false;
        settings.AdaptiveResearchEnabled=false;
        settings.MaximumEvidencePromptCharacters=12_000;
        var handoff = new AdvancedAnalysisHandoffEnvelope
        {
            HandoffId=Guid.NewGuid(), CreatedAtUtc=DateTimeOffset.UtcNow,
            RequestText="Compare the values in original rows 2 and 3 of the observed table.", Language="en",
            Load=new() { PlanKind="direct_answer", AnswerUnitCount=1, AtomicEvidenceCount=1 },
            ResearchState=new() { EvidenceRevalidationRequired=true, MemoryIsEvidence=false }
        };
        var gateway = new TableReadObservationGateway(observation);
        await new OpenAiCompatibleAdvancedAnalysisProvider(http, settings, null).ExecuteAsync(
            new(Guid.NewGuid(), fixture.Seed.TenantId, fixture.Seed.UserId.ToString(), handoff,
                observation.Evidence, []), gateway, CancellationToken.None);
        Assert.Equal(2, http.Requests.Count);
        using var writer = JsonDocument.Parse(http.Requests[1]);
        var prompt = writer.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        using var body = JsonDocument.Parse(prompt);
        Assert.True(body.RootElement.TryGetProperty("structuredTables", out var tables),
            "The source-assembled table must reach the provider, not only an unused gateway DTO.");
        var table = Assert.Single(tables.EnumerateArray());
        Assert.True(table.GetProperty("complete").GetBoolean());
        Assert.Equal(Enumerable.Range(0, 6), ReadTableInts(table, "rowIndices"));
        Assert.Contains("-5 °C", table.GetProperty("html").GetString());
        Assert.Contains("+5 °C", table.GetProperty("html").GetString());
        var evidence = body.RootElement.GetProperty("evidence");
        Assert.True(evidence.GetRawText().Length + tables.GetRawText().Length <= settings.MaximumEvidencePromptCharacters,
            "The structured view shares the existing evidence prompt budget.");
    }

    private static JsonElement SingleTableReadView(AdvancedAnalysisSearchObservation observation)
    {
        var json = JsonSerializer.SerializeToElement(observation, TableReadJson);
        Assert.True(json.TryGetProperty("structuredTables", out var tables),
            "read_source returned separate fragments without a source-bound structured table view.");
        return Assert.Single(tables.EnumerateArray());
    }

    private static int[] ReadTableInts(JsonElement table, string name)
        => table.GetProperty(name).EnumerateArray().Select(value => value.GetInt32()).ToArray();
    private static string[] ReadTableStrings(JsonElement table, string name)
        => table.GetProperty(name).EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static AdvancedAnalysisSearchRequest TableReadRequest(EvidenceSeed seed)
        => new("Read the observed table", TopK:20, DocId:seed.DocId.ToString(), DocPath:seed.DocPath,
            PageStart:2, PageEnd:2, RevisionId:seed.RevisionId.ToString(), Operation:"read_source");

    private static async Task<AdvancedAnalysisSearchObservation> ReadPublishedTableAsync(PostgresWorkerDatabase database,
        ReadTableFixture fixture, AdvancedAnalysisSearchRequest request)
    {
        var options = BuildAdvancedOptions();
        var resolver = new AdvancedAnalysisEvidenceResolver(database.DataSource, Options.Create(options));
        var initial = await resolver.ResolveAsync(fixture.Seed.TenantId,
            [new() { ChunkId=fixture.Ids[0].ToString() }], CancellationToken.None);
        Assert.True(initial.IsValid);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new UnavailableHttpClientFactory();
        var rag = Options.Create(BuildRagOptions());
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), fixture.Seed.TenantId, initial.Evidence,
            database.DataSource, rag.Value, http, services,
            new RagSearchBulkhead(rag, NullLogger<RagSearchBulkhead>.Instance), resolver, options);
        return await gateway.SearchAsync(request, CancellationToken.None);
    }

    private sealed record ReadTableFixture(EvidenceSeed Seed, IReadOnlyList<ProjectedRetrievalChunk> Chunks,
        List<Guid> Ids,IReadOnlyDictionary<int,string[]> ExpectedCellIdsByRow);

    private static async Task<ReadTableFixture> PublishReadTableAsync(PostgresWorkerDatabase database,bool shuffledSpanCells=false)
    {
        var seed = await database.SeedEvidenceAsync("split-table-read", "Replaced by production SQL projection.");
        var source = FindTableSource();
        var cells = source.Tables[0].Data!.Cells;
        for (var row=2; row<6; row++) for (var column=0; column<4; column++)
            cells.Add(new()
            {
                RowIndex=row, ColumnIndex=column,
                Text=column==0 ? "Assembly "+row : column==1 && row==2 ? "+5 °C" : "Value "+row+"/"+column,
                BoundingBox=new() { Left=10+column*100, Right=110+column*100,
                    Top=30+row*30, Bottom=60+row*30, CoordinateOrigin="TOPLEFT" }
            });
        source.Tables[0].Data!.RowCount=6;
        if (shuffledSpanCells)
        {
            cells.Single(cell=>cell.RowIndex==1 && cell.ColumnIndex==0).RowSpan=2;
            cells.RemoveAll(cell=>cell.RowIndex==2 && cell.ColumnIndex==0);
            cells.Reverse();
        }
        source.Tables[0].Provenance[0].BoundingBox!.Bottom=210;
        var sourceHash=Convert.ToHexString(Enumerable.Range(1,32).Select(value=>(byte)value).ToArray()).ToLowerInvariant();
        var canonical=DoclingCanonicalDocumentAdapter.Project(new(seed.DocId,seed.RevisionId,sourceHash,
            100,seed.FileName,new string('a',64),"canonical_projection","fixture","fixture"),source);
        var whole=Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical,source,10_000,1));
        var parts=DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(whole)!;
        Assert.Equal(2,parts.Count);
        var projected=parts.Select((part,index)=>part with { ChunkIndex=index }).ToArray();
        var bundle=DoclingCanonicalBundleFactory.Create(new(seed.DocId,seed.RevisionId,Guid.NewGuid(),1,
            sourceHash,100,seed.FileName,"fixture",DateTimeOffset.UtcNow,
            new IngestionHardwareProfile { ProfileId="fixture", LogicalProcessorCount=1,MemoryBytes=1_000_000,
                Devices=[new() { DeviceId="cpu:0",DeviceType="cpu" }] },new(),
            [new() { StageId="canonical_projection",StageType="canonical_projection",Engine="SAAIA.Backend",
                EngineVersion="fixture",OptionsSha256=new string('b',64),DeviceId="cpu:0" }],
            canonical,new() { Document=new() { JsonContent=source } },projected));
        await using var connection=await database.DataSource.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        var publish=typeof(DocumentFoundationRepo).GetMethod("UpsertRetrievalChunksAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        await (Task)publish.Invoke(null,[connection,transaction,seed.TenantId,seed.DocId,seed.RevisionId,1,
            Array.Empty<ExtractedDocumentSection>(),Array.Empty<ExtractedDocumentUnit>(),projected,CancellationToken.None])!;
        await CanonicalArtifactRepo.UpsertBundleAsync(connection,transaction,seed.TenantId,seed.RevisionId,bundle,CancellationToken.None);
        await transaction.CommitAsync();
        return new(seed,projected,projected.Select(part=>DocumentFoundationRepo.BuildStableRetrievalChunkId(seed.DocId,1,part.ChunkIndex)).ToList(),
            canonical.Pages.SelectMany(page=>page.Tables).SelectMany(table=>table.Cells)
                .GroupBy(cell=>cell.RowIndex).ToDictionary(group=>group.Key,group=>group.Select(cell=>cell.CellId).ToArray()));
    }

    private sealed class TableReadObservationGateway(AdvancedAnalysisSearchObservation observation) : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => observation.Evidence;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,CancellationToken token)
            => Task.FromResult(observation);
    }

    private sealed class TableReadCompletionFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        public List<string> Requests { get; }=[];
        public TableReadCompletionFactory() => _client=new HttpClient(new TableReadCompletionHandler(Requests));
        public HttpClient CreateClient(string name)=>_client;
        public void Dispose()=>_client.Dispose();
    }

    private sealed class TableReadCompletionHandler(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            requests.Add(await request.Content!.ReadAsStringAsync(token));
            var text=requests.Count==1 ? """{"queries":[{"query":"table overview","topK":20}]}"""
                : """{"outcome":"insufficient_documentation","answerText":"No conclusion is asserted.","claims":[]}""";
            return new(HttpStatusCode.OK) { Content=JsonContent.Create(new
            {
                choices=new[] { new { message=new { content=text },finish_reason="stop" } },
                usage=new { prompt_tokens=100,completion_tokens=30 }
            }) };
        }
    }
}
