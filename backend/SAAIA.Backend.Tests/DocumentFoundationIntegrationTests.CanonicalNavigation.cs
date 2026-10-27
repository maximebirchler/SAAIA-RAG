using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_navigation_pg_preserves_source_evidence_and_physical_target_in_handler()
        => await VerifyCanonicalNavigationStorageAsync(resolveTarget: true);

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_navigation_pg_unresolved_printed_page_has_null_target_in_handler()
        => await VerifyCanonicalNavigationStorageAsync(resolveTarget: false);

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_navigation_pg_persists_diagnostics_even_when_row_is_refused()
        => await VerifyCanonicalNavigationStorageAsync(resolveTarget: false, refuseRow: true);

    [Trait("Profile", "IntegrationPg")]
    [InlineData(false)]
    [InlineData(true)]
    [ProfileTheory("IntegrationPg")]
    public async Task Native_title_navigation_pg_preserves_qualification_and_never_fabricates_target(bool contextOnly)
        => await VerifyCanonicalNavigationStorageAsync(resolveTarget: true, nativeTitle: true, contextOnly: contextOnly);

    private static async Task VerifyCanonicalNavigationStorageAsync(bool resolveTarget, bool refuseRow = false,
        bool nativeTitle = false, bool contextOnly = false)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        Assert.NotNull(db);
        var tenant = Guid.NewGuid(); var docId = Guid.NewGuid(); var job = Guid.NewGuid();
        const string path = "Synthetic/Canonical-navigation.pdf";
        var (document, chunks, sections) = nativeTitle
            ? DocumentTitleNavigationCanonicalTests.NativeFixture(.6, printedPage: "VII")
            : DocumentTitleNavigationCanonicalTests.Fixture("VII");
        if (contextOnly) chunks[2] = chunks[2] with { CanonicalBlockIds = [], CanonicalContextBlockIds = ["heading"] };
        if (!resolveTarget) document.Pages[1].Blocks.Clear();
        if (refuseRow) document.Pages[0].Tables[0].Cells[1].Polygon!.Points[0].X = .6; // nonrectangle: explicit unavailable
        var sourceHash = Enumerable.Repeat((byte)0xaa, 32).ToArray();
        var pages = Enumerable.Range(1, 8).Select(page => new ExtractedPdfPage(page,
            string.Join('\n', chunks.Where(chunk => chunk.PageStart == page).Select(chunk => chunk.Text)), 5, 50, [1])).ToArray();
        await db!.SeedRunningJobAsync(tenant, docId, job, path, 1, 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenant, job, path, sourceHash, 500, DateTime.UtcNow,
            1, pages, sections, [], chunks, [], [], CancellationToken.None, leaseOwner: "test-worker"));
        await using var connection = await ds.OpenConnectionAsync();
        var revision = await connection.ExecuteScalarAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=1;", new { tenant, docId });
        document.DocumentId = docId; document.RevisionId = revision;
        if (document.Pages[0].NativeTypographyObservations is { } native)
        { native.DocumentId = docId; native.RevisionId = revision; }
        DocumentFoundationRepo.ValidateCanonicalNavigationIdentity(document, docId, revision, sourceHash);
        var profile = DocumentProfileProjector.Project(path, pages, sections, [], []);
        var index = DocumentTitleNavigationProjector.Project(sections, [], chunks, chunks, profile, document);
        var method = typeof(DocumentFoundationRepo).GetMethod("UpsertDocumentTitleNavigationIndexAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await (Task)method!.Invoke(null, [connection, transaction, tenant, docId, revision, 1, profile, index,
                chunks.Select(chunk => chunk.ChunkIndex).ToHashSet(), CancellationToken.None])!;
            await transaction.CommitAsync();
        }
        var diagnostics = await connection.ExecuteScalarAsync<string>(
            "SELECT (metadata->'canonicalNavigation')::text FROM document_profiles WHERE tenant_id=@tenant AND revision_id=@revision;", new { tenant, revision });
        using var diagnosticJson = JsonDocument.Parse(diagnostics!);
        Assert.Equal(refuseRow || nativeTitle ? 1 : 0, diagnosticJson.RootElement.GetProperty("rowsRefused").GetInt32());
        Assert.Equal(refuseRow ? 0 : 1, diagnosticJson.RootElement.GetProperty("entriesProduced").GetInt32());
        var metadata = await connection.QueryAsync<string>(
            "SELECT metadata::text FROM document_navigation_entries WHERE tenant_id=@tenant AND revision_id=@revision;", new { tenant, revision });
        if (refuseRow) Assert.Empty(metadata);
        else
        {
            using var stored = JsonDocument.Parse(Assert.Single(metadata));
            Assert.Equal("VII", stored.RootElement.GetProperty("printedPageLabel").GetString());
            Assert.Equal(new[] { "title", "page" }, stored.RootElement.GetProperty("sourceCellIds").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("physical", stored.RootElement.GetProperty("targetPageSpace").GetString());
            if (nativeTitle)
            {
                var proof = stored.RootElement.GetProperty("nativeTitleCorroboration");
                Assert.Equal("source_row_physical_overlap_observed", proof.GetProperty("geometryWarning").GetString());
                Assert.Equal("not_corroborated", proof.GetProperty("printedPageLabelCorroboration").GetString());
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(stored.RootElement.GetRawText()) <= 16 * 1024);
            }
        }
        // Executes the real route handler and serializer against PostgreSQL; this is not an
        // HTTP transport claim and does not publish a canonical bundle to the artifact store.
        var api = typeof(DocumentsEndpoints).GetMethod("NavigationAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(api);
        foreach (var requestTenant in new[] { tenant, Guid.NewGuid() })
        {
            var context = BuildRagHttpContext(requestTenant);
            var result = await (Task<IResult>)api!.Invoke(null, [context, ds, null, null, docId, null, null, "navigation_entry", 100, 0])!;
            await result.ExecuteAsync(context);
            context.Response.Body.Position = 0;
            using var response = await JsonDocument.ParseAsync(context.Response.Body);
            var items = response.RootElement.GetProperty("items");
            if (requestTenant != tenant || refuseRow) { Assert.Equal(0, items.GetArrayLength()); continue; }
            var item = Assert.Single(items.EnumerateArray());
            Assert.Equal(docId, item.GetProperty("docId").GetGuid());
            Assert.Equal(revision, item.GetProperty("revisionId").GetGuid());
            Assert.Equal("VII", item.GetProperty("printedPageLabel").GetString());
            Assert.Equal(1, item.GetProperty("sourcePage").GetInt32());
            if (resolveTarget)
            {
                Assert.Equal(7, item.GetProperty("targetPageStart").GetInt32());
                Assert.Equal("heading", item.GetProperty("targetCanonicalBlockId").GetString());
                if (nativeTitle)
                {
                    Assert.Equal("canonical_title_native_corroborated", item.GetProperty("resolutionMethod").GetString());
                    Assert.Equal("not_corroborated", item.GetProperty("nativeTitleCorroboration")
                        .GetProperty("printedPageLabelCorroboration").GetString());
                    Assert.Equal(JsonValueKind.Null, item.GetProperty("targetAnchorId").ValueKind);
                    if (contextOnly) Assert.Equal(JsonValueKind.Null, item.GetProperty("targetChunkId").ValueKind);
                }
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, item.GetProperty("targetPageStart").ValueKind);
                Assert.Equal(JsonValueKind.Null, item.GetProperty("targetChunkId").ValueKind);
                Assert.Equal("canonical_title_unresolved", item.GetProperty("resolutionMethod").GetString());
            }
        }
    }
}
