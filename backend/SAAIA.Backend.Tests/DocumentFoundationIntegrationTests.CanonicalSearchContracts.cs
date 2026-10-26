using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_search_preserves_real_pdf_hash_pages_and_extracted_text_for_client_citations()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(export))
            Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
        var ownedDirectory = Path.Combine(Path.GetTempPath(), $"saaia-pdf-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedDirectory);
        var pdfPath = Path.Combine(ownedDirectory, "Calibration-record.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            string[][] pageLines =
            [
                ["AMBER CALIBRATION", "The amber instrument records a calibration measurement of 47 millimetres.",
                 "The operator verifies the instrument before recording the measurement.",
                 "The inspection record contains the instrument name and the measured value.",
                 "This calibration record applies to the amber instrument only.",
                 "The operator records the date and signs the completed inspection sheet.",
                 "The supervisor compares the recorded value with the reference block.",
                 "The recorded value is valid only after the reference block check is signed."],
                ["COBALT CALIBRATION", "The cobalt instrument records a calibration measurement of 63 millimetres.",
                 "The operator checks the reference block before recording the measurement.",
                 "The inspection record contains the reference block and the measured value.",
                 "This calibration record applies to the cobalt instrument only.",
                 "The operator records the date and signs the completed inspection sheet.",
                 "The supervisor compares the recorded value with the reference block.",
                 "The recorded value is valid only after the reference block check is signed."]
            ];
            foreach (var lines in pageLines)
            {
                var page = builder.AddPage(PageSize.A4);
                for (var index = 0; index < lines.Length; index++)
                    page.AddText(lines[index], index == 0 ? 14 : 11, new PdfPoint(40, 780 - 28 * index), font);
            }
            var pdfBytes = builder.Build();
            File.WriteAllBytes(pdfPath, pdfBytes);
            var hash = SHA256.HashData(pdfBytes);
            var extraction = PdfExtractor.Extract(pdfPath);
            Assert.Equal(2, extraction.Pages.Count);
            Assert.Contains("47 millimetres", extraction.Pages[0].Text);
            Assert.Contains("63 millimetres", extraction.Pages[1].Text);
            var sections = DocumentSectionExtractor.Extract(extraction.Pages);
            var units = DocumentUnitExtractor.Extract(extraction.Pages, sections);
            var ingestion = new IngestionOptions();
            var chunks = RetrievalChunkProjector.ProjectStructureAware(sections, units,
                ingestion.ChunkMaxWords, ingestion.ChunkOverlapWords, ingestion.ChunkMinWords);
            Assert.NotEmpty(chunks);

            const string docPath = "Knowledge/Calibration-record.pdf";
            var tenantId = Guid.NewGuid();
            var docId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobId, docPath,
                hash, pdfBytes.Length, File.GetLastWriteTimeUtc(pdfPath), 1, extraction.Pages,
                sections, units, chunks, ExactMatchEntryExtractor.Extract(units),
                ContextualTextProjector.Project(docPath, sections, units, chunks), CancellationToken.None, leaseOwner: "test-worker"));

            var ctx = BuildRagHttpContext(tenantId);
            var result = await InvokeRagSearchAsync(ctx, ds, Options.Create(CreateTestRagOptions()),
                new StubHttpClientFactory(), new RagSearchRequestDto("calibration measurement", DocId: docId.ToString(),
                    TopK: 20, IncludeContextualSnippet: true, SourceBackedCanonical: true));
            await result.ExecuteAsync(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            var response = JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx));
            var items = response.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.Equal(new[] { 1, 2 }, items.Select(item => item.GetProperty("pageStart").GetInt32()).Distinct().Order().ToArray());
            var revisionId = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 1).ToString();
            Assert.All(items, item =>
            {
                Assert.Equal(Convert.ToHexString(hash).ToLowerInvariant(), item.GetProperty("sourceHash").GetString());
                Assert.Equal(revisionId, item.GetProperty("revisionId").GetString());
                Assert.Equal(docId.ToString(), item.GetProperty("docId").GetString());
                Assert.Equal(docPath, item.GetProperty("docPath").GetString());
                Assert.True(Guid.TryParse(item.GetProperty("chunkId").GetString(), out _));
                var start = item.GetProperty("pageStart").GetInt32();
                var end = item.GetProperty("pageEnd").GetInt32();
                Assert.Equal(start, end);
                Assert.InRange(start, 1, 2);
                var text = Assert.IsType<string>(item.GetProperty("text").GetString());
                Assert.True(text.Length > 420);
                Assert.Contains(start == 1 ? "47 millimetres" : "63 millimetres", text);
                var chunk = Assert.Single(chunks, chunk => chunk.PageStart == start && chunk.PageEnd == end);
                Assert.Equal(chunk.Text.Replace("\r\n", "\n", StringComparison.Ordinal), text);
            });
            if (!string.IsNullOrWhiteSpace(export))
            {
                var artifact = Path.Combine(export, "canonical-pdf-citation-contract.json");
                Assert.False(File.Exists(artifact));
                File.Copy(pdfPath, Path.Combine(export, "Calibration-record.pdf"), overwrite: false);
                File.WriteAllText(artifact, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    syntheticCorpus = true,
                    postgresVerified = true,
                    sourceTest = nameof(Canonical_search_preserves_real_pdf_hash_pages_and_extracted_text_for_client_citations),
                    pdfFile = "Calibration-record.pdf",
                    sourceHash = Convert.ToHexString(hash).ToLowerInvariant(),
                    docId,
                    revisionId,
                    docPath,
                    extraction.Source,
                    pages = extraction.Pages.Select(page => new { page.PageNumber, page.Text }),
                    response
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            if (File.Exists(pdfPath))
                File.Delete(pdfPath);
            Directory.Delete(ownedDirectory, recursive: false);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_response_reports_revision_change_instead_of_publishing_stale_chunks_with_new_identity()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");
        var tenantId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        const string path = "Knowledge/response-identity-race.pdf";
        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 1,
            [
                new RuntimeSeedSection("Initial instrument", "Silver verification records the first extraction and the original instrument measurement procedure."),
                new RuntimeSeedSection("Initial record", "Silver verification records the first extraction and the original inspection completion procedure.")
            ]);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var options = CreateTestRagOptions();
        options.EnableRerank = true;
        var factory = new ReindexOnRerankHttpClientFactory(() => PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 2,
            [
                new RuntimeSeedSection("Updated instrument", "Silver verification records the second extraction and a revised instrument measurement procedure."),
                new RuntimeSeedSection("Updated record", "Silver verification records the second extraction and a revised inspection completion procedure.")
            ]));
        var request = new RagSearchRequestDto("silver verification", DocId: docId.ToString(), TopK: 20, SourceBackedCanonical: true);
        async Task<JsonElement> SearchAsync(IHttpClientFactory clientFactory)
        {
            var ctx = BuildRagHttpContext(tenantId);
            var result = await InvokeRagSearchAsync(ctx, ds, Options.Create(options), clientFactory, request);
            await result.ExecuteAsync(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx));
        }

        var interrupted = await SearchAsync(factory);
        Assert.Equal(1, factory.ReindexCount);
        await using (var check = await ds.OpenConnectionAsync())
        {
            var publishedVersion = await check.QuerySingleAsync<int>(
                "SELECT indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant = tenantId, doc = docId });
            Assert.Equal(2, publishedVersion);
        }
        Assert.Empty(interrupted.GetProperty("items").EnumerateArray());
        Assert.Equal(0, interrupted.GetProperty("metrics").GetProperty("returned").GetInt32());
        Assert.Contains("source_identity", interrupted.GetProperty("metrics").GetProperty("degradedRetrievers")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("canonical_chunk_not_current", interrupted.GetProperty("metrics").GetProperty("degradedRetrieverErrors")
            .GetProperty("source_identity").GetString());

        var stable = await SearchAsync(new StubHttpClientFactory());
        Assert.Equal(2, stable.GetProperty("items").GetArrayLength());
        Assert.All(stable.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 2).ToString(), item.GetProperty("revisionId").GetString());
            Assert.Contains("second extraction", item.GetProperty("text").GetString());
        });
        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(export))
        {
            Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
            var artifact = Path.Combine(export, "canonical-identity-race-contract.json");
            Assert.False(File.Exists(artifact));
            File.WriteAllText(artifact, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                syntheticCorpus = true,
                postgresVerified = true,
                sourceTest = nameof(Canonical_response_reports_revision_change_instead_of_publishing_stale_chunks_with_new_identity),
                reindexDuringRerank = true,
                interrupted,
                stable
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_source_identity_does_not_reassign_an_observed_chunk_after_reindexing()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");
        var tenantId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        const string path = "Knowledge/identity-race.pdf";
        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 1,
            [new RuntimeSeedSection("First verification", "Silver verification records the first extraction and the original instrument measurement procedure.")]);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var request = new RagSearchRequestDto("silver verification", DocId: docId.ToString(), TopK: 20, SourceBackedCanonical: true);
        var firstSearch = await RagEndpoints.SearchCoreAsync(BuildRagHttpContext(tenantId), ds,
            CreateTestRagOptions(), new StubHttpClientFactory(), request);
        var firstMatch = Assert.Single(firstSearch.Matches);
        var originalIdentities = await CanonicalRetrieval.LoadRagCanonicalDocumentSourceIdentitiesAsync(ds, tenantId, [firstMatch], CancellationToken.None);
        var originalIdentity = RetrievalResultProjector.ResolveDocumentSourceIdentity(firstMatch, originalIdentities);
        Assert.NotNull(originalIdentity);
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 1).ToString(), originalIdentity!.RevisionId);

        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 2,
            [new RuntimeSeedSection("Second verification", "Silver verification records the second extraction and a revised instrument measurement procedure.")]);
        var currentSearch = await RagEndpoints.SearchCoreAsync(BuildRagHttpContext(tenantId), ds,
            CreateTestRagOptions(), new StubHttpClientFactory(), request);
        var currentMatch = Assert.Single(currentSearch.Matches);
        Assert.NotEqual(firstMatch.ChunkId, currentMatch.ChunkId);
        var wrongDocument = currentMatch with { DocId = Guid.NewGuid().ToString() };
        var wrongPath = currentMatch with { DocPath = "Knowledge/different-document.pdf" };
        var currentIdentities = await CanonicalRetrieval.LoadRagCanonicalDocumentSourceIdentitiesAsync(ds, tenantId,
            [firstMatch, currentMatch, wrongDocument, wrongPath], CancellationToken.None);
        var currentIdentity = RetrievalResultProjector.ResolveDocumentSourceIdentity(currentMatch, currentIdentities);
        Assert.NotNull(currentIdentity);
        Assert.Equal(originalIdentity.SourceHash, currentIdentity!.SourceHash);
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 2).ToString(), currentIdentity.RevisionId);
        Assert.Null(RetrievalResultProjector.ResolveDocumentSourceIdentity(firstMatch, currentIdentities));
        Assert.Null(RetrievalResultProjector.ResolveDocumentSourceIdentity(wrongDocument, currentIdentities));
        Assert.Null(RetrievalResultProjector.ResolveDocumentSourceIdentity(wrongPath, currentIdentities));
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_context_exposes_revision_change_after_search_without_relabeling_the_old_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        const string path = "Knowledge/revision-window.pdf";
        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 1,
            [new RuntimeSeedSection("Initial measurement", "Amber measurement records the first extraction and its original instrument verification procedure.")]);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        async Task<JsonElement> SearchAsync()
        {
            var ctx = BuildRagHttpContext(tenantId);
            var result = await InvokeRagSearchAsync(ctx, ds, Options.Create(CreateTestRagOptions()),
                new StubHttpClientFactory(), new RagSearchRequestDto("amber measurement", DocId: docId.ToString(),
                    TopK: 20, SourceBackedCanonical: true));
            await result.ExecuteAsync(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            var response = JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx));
            Assert.Single(response.GetProperty("items").EnumerateArray());
            return response;
        }

        async Task<JsonElement> ContextAsync(Guid anchor)
        {
            var ctx = BuildRagHttpContext(tenantId);
            var method = typeof(DocumentsEndpoints).GetMethod("ContextAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var result = await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId, path, anchor, null, null, 4, 2, 7, 0])!;
            await result.ExecuteAsync(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx));
        }

        var oldSearch = await SearchAsync();
        var oldHit = oldSearch.GetProperty("items")[0];
        var oldAnchor = Guid.Parse(oldHit.GetProperty("chunkId").GetString()!);
        var oldContext = await ContextAsync(oldAnchor);
        Assert.True(oldContext.GetProperty("anchorFound").GetBoolean());
        Assert.Equal(oldHit.GetProperty("revisionId").GetString(), oldContext.GetProperty("document").GetProperty("revisionId").GetString());

        await PublishIndexedDocumentAsync(db, tenantId, docId, Guid.NewGuid(), path, 2,
            [new RuntimeSeedSection("Updated measurement", "Amber measurement records the second extraction and a changed instrument verification procedure.")]);
        var changedContext = await ContextAsync(oldAnchor);
        var currentSearch = await SearchAsync();
        var currentHit = currentSearch.GetProperty("items")[0];
        var currentContext = await ContextAsync(Guid.Parse(currentHit.GetProperty("chunkId").GetString()!));

        Assert.True(changedContext.GetProperty("found").GetBoolean());
        Assert.False(changedContext.GetProperty("anchorFound").GetBoolean());
        Assert.Equal("document_scroll", changedContext.GetProperty("contextKind").GetString());
        Assert.NotEqual(oldHit.GetProperty("revisionId").GetString(), currentHit.GetProperty("revisionId").GetString());
        Assert.Equal(oldHit.GetProperty("sourceHash").GetString(), currentHit.GetProperty("sourceHash").GetString());
        Assert.Equal(currentHit.GetProperty("revisionId").GetString(), changedContext.GetProperty("document").GetProperty("revisionId").GetString());
        Assert.True(currentContext.GetProperty("anchorFound").GetBoolean());
        Assert.All(changedContext.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.NotEqual(oldAnchor.ToString(), item.GetProperty("chunkId").GetString());
            Assert.Contains("second extraction", item.GetProperty("text").GetString());
        });

        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(export))
        {
            Assert.True(Path.IsPathFullyQualified(export) && Directory.Exists(export));
            var artifact = Path.Combine(export, "canonical-context-revision-contract.json");
            Assert.False(File.Exists(artifact));
            File.WriteAllText(artifact, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                syntheticCorpus = true,
                postgresVerified = true,
                sourceTest = nameof(Canonical_context_exposes_revision_change_after_search_without_relabeling_the_old_anchor),
                cases = new[]
                {
                    new { name = "stable_initial_revision", search = oldSearch, context = oldContext },
                    new { name = "reextracted_between_search_and_context", search = oldSearch, context = changedContext },
                    new { name = "stable_current_revision", search = currentSearch, context = currentContext }
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Canonical_search_preserves_current_source_identity_and_explicit_scope_without_server_guidance()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var firstDocId = Guid.NewGuid();
        var secondDocId = Guid.NewGuid();
        var outsideDocId = Guid.NewGuid();
        var foreignDocId = Guid.NewGuid();
        await PublishIndexedDocumentAsync(db, tenantId, firstDocId, Guid.NewGuid(), "Knowledge/first.pdf", 1,
            [new RuntimeSeedSection("Previous extraction", "Cobalt calibration has an obsolete extraction marker that must not survive a new indexed revision.")]);
        await PublishIndexedDocumentAsync(db, tenantId, firstDocId, Guid.NewGuid(), "Knowledge/first.pdf", 2,
            [
                new RuntimeSeedSection("Pressure measurement", "Cobalt calibration records the current pressure measurement and identifies the instrument used for verification."),
                new RuntimeSeedSection("Recording results", "Cobalt calibration records the current verification result and the time when the inspection was completed.")
            ], useProductionContextualProjection: true);
        await PublishIndexedDocumentAsync(db, tenantId, secondDocId, Guid.NewGuid(), "Knowledge/second.pdf", 1,
            [new RuntimeSeedSection("Instrument record", "Cobalt calibration records the instrument identifier and the required verification interval for the equipment.")],
            useProductionContextualProjection: true);
        await PublishIndexedDocumentAsync(db, tenantId, outsideDocId, Guid.NewGuid(), "Archive/outside.pdf", 1,
            [new RuntimeSeedSection("Other category", "Cobalt calibration records a separate archived procedure outside the requested knowledge category.")]);
        await PublishIndexedDocumentAsync(db, foreignTenantId, foreignDocId, Guid.NewGuid(), "Knowledge/first.pdf", 1,
            [new RuntimeSeedSection("Other tenant", "Cobalt calibration contains a foreign tenant marker that must never be returned to another tenant.")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
            ds, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.True(built.Success);
        await using var conn = await ds.OpenConnectionAsync();
        var categoryOrder = await conn.QuerySingleAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='Knowledge';",
            new { tenant = tenantId });
        var currentSources = (await conn.QueryAsync<(Guid ChunkId, Guid RevisionId, Guid DocId, string Text, string Hash, int PageStart, int PageEnd)>(
            """
SELECT c.retrieval_chunk_id, r.revision_id, r.doc_id, c.text_content,
       LOWER(ENCODE(r.source_hash, 'hex')), c.page_start, c.page_end
FROM retrieval_chunks c
JOIN document_revisions r ON r.revision_id=c.revision_id AND r.tenant_id=c.tenant_id
JOIN documents d ON d.doc_id=r.doc_id AND d.tenant_id=r.tenant_id AND d.indexed_version=r.indexed_version
WHERE c.tenant_id=@tenant;
""",
            new { tenant = tenantId })).ToDictionary(row => row.ChunkId);

        var contractResponses = new List<object>();
        async Task<RagSearchResponseDto> SearchAsync(RagSearchRequestDto request)
        {
            var ctx = BuildRagHttpContext(tenantId);
            var result = await InvokeRagSearchAsync(ctx, ds, Options.Create(CreateTestRagOptions()),
                new StubHttpClientFactory(), request);
            await result.ExecuteAsync(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            var raw = ReadResponseBody(ctx);
            contractResponses.Add(new { request, response = JsonSerializer.Deserialize<JsonElement>(raw) });
            var response = JsonSerializer.Deserialize<RagSearchResponseDto>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(response);
            Assert.Null(response!.Guidance);
            // This scenario has no exact entries and Qdrant returns no points:
            // all nonempty results come from the canonical PostgreSQL FTS channel.
            Assert.Equal(response.Items.Count, response.Metrics.SparseReturned);
            Assert.Equal(0, response.Metrics.DenseReturned);
            Assert.All(response.Items, item =>
            {
                Assert.Equal("sparse_bm25", item.Retriever);
                Assert.True(Guid.TryParse(item.ChunkId, out var chunkId));
                Assert.True(currentSources.TryGetValue(chunkId, out var source), "Every returned chunk must belong to this tenant's currently published revision.");
                Assert.Equal(source.DocId.ToString(), item.DocId);
                Assert.Equal(source.RevisionId.ToString(), item.RevisionId);
                Assert.Equal(source.Hash, item.SourceHash);
                Assert.Equal(source.Text, item.Text);
                Assert.Equal(source.PageStart, item.PageStart);
                Assert.Equal(source.PageEnd, item.PageEnd);
                Assert.NotNull(item.ProvenanceInfo);
                Assert.Equal("sparse_bm25", item.ProvenanceInfo!.Channel);
                Assert.Equal("retriever:sparse_bm25", item.ProvenanceInfo.Label);
                Assert.Equal(item.SourceHash, item.ProvenanceInfo!.SourceHash);
                Assert.Equal(item.ChunkId, item.ProvenanceInfo.ChunkId);
                Assert.Null(item.SelectionHints);
                Assert.Null(item.ProfileSignals);
                Assert.Null(item.MatchedContentCards);
                Assert.DoesNotContain("obsolete extraction marker", item.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("foreign tenant marker", item.Text, StringComparison.Ordinal);
            });
            return response;
        }

        foreach (var request in new[]
        {
            new RagSearchRequestDto("cobalt calibration", CategoryPath: "Knowledge", TopK: 20, SourceBackedCanonical: true, IncludeContextualSnippet: true),
            new RagSearchRequestDto("cobalt calibration", CategoryRef: $"cat_{categoryOrder:000}", TopK: 20, SourceBackedCanonical: true, IncludeContextualSnippet: true)
        })
        {
            var response = await SearchAsync(request);
            Assert.Equal("cobalt calibration", response.Query);
            Assert.NotEmpty(response.Items);
            Assert.Contains(response.Items, item => !string.IsNullOrWhiteSpace(item.ContextualSnippet));
            Assert.All(response.Items, item => Assert.StartsWith("Knowledge/", item.DocPath, StringComparison.Ordinal));
            Assert.Contains(response.Items, item => item.DocId == firstDocId.ToString());
            Assert.Contains(response.Items, item => item.DocId == secondDocId.ToString());
        }

        var pageResponse = await SearchAsync(new RagSearchRequestDto("cobalt calibration", DocId: firstDocId.ToString(),
            PageStart: 2, PageEnd: 2, TopK: 20, SourceBackedCanonical: true, IncludeContextualSnippet: true));
        Assert.NotEmpty(pageResponse.Items);
        Assert.All(pageResponse.Items, item =>
        {
            Assert.Equal(firstDocId.ToString(), item.DocId);
            Assert.Equal(2, item.PageStart);
            Assert.Equal(2, item.PageEnd);
        });

        var emptyResponse = await SearchAsync(new RagSearchRequestDto("absentquartzreferencexyz", TopK: 20, SourceBackedCanonical: true));
        Assert.Empty(emptyResponse.Items);

        var contractExport = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(contractExport))
        {
            Assert.True(Path.IsPathFullyQualified(contractExport) && Directory.Exists(contractExport));
            var artifactPath = Path.Combine(contractExport, "canonical-search-contract.json");
            Assert.False(File.Exists(artifactPath));
            File.WriteAllText(artifactPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                syntheticCorpus = true,
                postgresVerified = true,
                sourceTest = nameof(Canonical_search_preserves_current_source_identity_and_explicit_scope_without_server_guidance),
                responses = contractResponses,
                currentSources = currentSources.Values.Select(source => new
                {
                    source.ChunkId,
                    source.RevisionId,
                    source.DocId,
                    source.Text,
                    source.Hash,
                    source.PageStart,
                    source.PageEnd
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task CompleteUpsertAsync_publishes_canonical_bundle_and_queryable_source_anchor_atomically()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.Parse("41111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("42222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("43333333-3333-3333-3333-333333333333");
        const string docPath = "Canonical/Sample.pdf";
        const string text = "Canonical source text with enough useful words for publication.";
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var page = new ExtractedPdfPage(
            1,
            text,
            9,
            text.Length,
            SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            RawText: "Raw source text with enough useful words for publication.",
            WidthPoints: 595,
            HeightPoints: 842);
        var pages = new[] { page };
        var sections = new[] { new ExtractedDocumentSection(0, "Canonical", 1, 1, 1, 0, 0) };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                text,
                text.Length,
                9,
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                text,
                9,
                SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                "unit_exact_v1")
        };
        var exact = ExactMatchEntryExtractor.Extract(units);
        var contextual = ContextualTextProjector.Project(docPath, sections, units, chunks);
        var sourceHash = SHA256.HashData(Encoding.UTF8.GetBytes("source"));
        var sourceSha256 = Convert.ToHexString(sourceHash).ToLowerInvariant();
        var revisionId = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 1);
        var hardware = new IngestionHardwareProfile
        {
            ProfileId = "hardware_integration",
            LogicalProcessorCount = 4,
            MemoryBytes = 8_000_000_000,
            Devices = [new() { DeviceId = "cpu:0", DeviceType = "cpu" }]
        };
        var extraction = new PdfExtractionResult(
            [],
            pages.ToList(),
            PdfExtractionQualitySummary.FromPages(pages));
        var bundle = LegacyCanonicalBundleFactory.Create(new(
            docId,
            revisionId,
            DocumentFoundationRepo.BuildStableProcessingRunId(jobId),
            1,
            sourceSha256,
            123,
            "Sample.pdf",
            "integration-source-revision",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            hardware,
            [
                new()
                {
                    StageId = "canonical_projection",
                    StageType = "canonical_projection",
                    Engine = "SAAIA.Backend",
                    EngineVersion = "integration-source-revision",
                    OptionsSha256 = new string('d', 64),
                    DeviceId = "cpu:0"
                }
            ],
            extraction,
            sections,
            chunks));

        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            dataSource,
            tenantId,
            jobId,
            docPath,
            sourceHash,
            123,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            chunks,
            exact,
            contextual,
            CancellationToken.None,
            canonicalBundle: bundle, leaseOwner: "test-worker");

        Assert.True(committed);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var artifact = await conn.QuerySingleAsync<(string schema_version, byte[] content_hash, long byte_size, byte[] payload)>(
            "SELECT schema_version, content_hash, byte_size, payload FROM document_revision_binary_artifacts WHERE revision_id=@revision_id;",
            new { revision_id = revisionId });
        var restored = CanonicalArtifactRepo.ReadBundleArtifact(new(
            CanonicalArtifactRepo.BundleArtifactType,
            artifact.schema_version,
            artifact.content_hash,
            artifact.byte_size,
            artifact.payload));
        var anchorCount = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM document_source_anchors WHERE revision_id=@revision_id;",
            new { revision_id = revisionId });

        Assert.Equal(revisionId, restored.Document.RevisionId);
        Assert.Single(restored.SourceAnchors);
        Assert.Equal(1, anchorCount);
    }
}
