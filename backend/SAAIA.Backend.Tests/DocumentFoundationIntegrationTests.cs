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
    [Fact]
    public async Task Canonical_search_preserves_real_pdf_hash_pages_and_extracted_text_for_client_citations()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
                ContextualTextProjector.Project(docPath, sections, units, chunks), CancellationToken.None));

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
                    schemaVersion = 1, syntheticCorpus = true, postgresVerified = true,
                    sourceTest = nameof(Canonical_search_preserves_real_pdf_hash_pages_and_extracted_text_for_client_citations),
                    pdfFile = "Calibration-record.pdf", sourceHash = Convert.ToHexString(hash).ToLowerInvariant(),
                    docId, revisionId, docPath, extraction.Source,
                    pages = extraction.Pages.Select(page => new { page.PageNumber, page.Text }), response
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

    [Fact]
    public async Task Canonical_response_reports_revision_change_instead_of_publishing_stale_chunks_with_new_identity()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;
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
                schemaVersion = 1, syntheticCorpus = true, postgresVerified = true,
                sourceTest = nameof(Canonical_response_reports_revision_change_instead_of_publishing_stale_chunks_with_new_identity),
                reindexDuringRerank = true, interrupted, stable
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public async Task Canonical_source_identity_does_not_reassign_an_observed_chunk_after_reindexing()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;
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
        var originalIdentities = await RagEndpoints.LoadRagCanonicalDocumentSourceIdentitiesAsync(ds, tenantId, [firstMatch], CancellationToken.None);
        var originalIdentity = RagEndpoints.ResolveDocumentSourceIdentity(firstMatch, originalIdentities);
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
        var currentIdentities = await RagEndpoints.LoadRagCanonicalDocumentSourceIdentitiesAsync(ds, tenantId,
            [firstMatch, currentMatch, wrongDocument, wrongPath], CancellationToken.None);
        var currentIdentity = RagEndpoints.ResolveDocumentSourceIdentity(currentMatch, currentIdentities);
        Assert.NotNull(currentIdentity);
        Assert.Equal(originalIdentity.SourceHash, currentIdentity!.SourceHash);
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 2).ToString(), currentIdentity.RevisionId);
        Assert.Null(RagEndpoints.ResolveDocumentSourceIdentity(firstMatch, currentIdentities));
        Assert.Null(RagEndpoints.ResolveDocumentSourceIdentity(wrongDocument, currentIdentities));
        Assert.Null(RagEndpoints.ResolveDocumentSourceIdentity(wrongPath, currentIdentities));
    }

    [Fact]
    public async Task Canonical_context_exposes_revision_change_after_search_without_relabeling_the_old_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
                schemaVersion = 1, syntheticCorpus = true, postgresVerified = true,
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

    [Fact]
    public async Task Canonical_search_preserves_current_source_identity_and_explicit_scope_without_server_guidance()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
                    source.ChunkId, source.RevisionId, source.DocId, source.Text, source.Hash, source.PageStart, source.PageEnd
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public void Stable_content_card_id_uses_normalized_title_not_card_order()
    {
        var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var first = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "  Control   Source  ");
        var second = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "control source");
        var different = DocumentFoundationRepo.BuildStableDocumentProfileContentCardId(profileId, "Other source");

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_canonical_bundle_and_queryable_source_anchor_atomically()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

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
            canonicalBundle: bundle);

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

    [Fact]
    public async Task CompleteUpsertAsync_publishes_revision_foundation_and_artifact_summaries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Pressure verification", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.",
                CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."),
                [3],
                "unit_exact_v1",
                SourceUnitOrdinals: [0],
                SourceUnitStartOrdinal: 0,
                SourceUnitEndOrdinal: 0,
                SourceUnitCount: 1,
                ChunkComposition: "single_unit")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", "Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.".Length, CountWords("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened."), [4], "verbatim_excerpt")
        };
        var contextualTextEntries = ContextualTextProjector.Project(docPath, sections, units, retrievalChunks);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 9],
            size: 123,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var pageCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_page_index;");
        var sectionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_sections;");
        var unitCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_units;");
        var chunkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunks;");
        var exactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM exact_match_entries;");
        var contextualCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contextual_text_entries;");
        var profileCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profiles;");
        var profileCardCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profile_content_cards;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(1, revisionCount);
        Assert.Equal(1, pageCount);
        Assert.Equal(1, sectionCount);
        Assert.Equal(1, unitCount);
        Assert.Equal(1, chunkCount);
        Assert.Equal(1, exactCount);
        Assert.Equal(1, contextualCount);
        Assert.Equal(1, profileCount);
        Assert.Equal(1, profileCardCount);
        Assert.Equal(10, artifactCount);

        var contextualMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM contextual_text_entries LIMIT 1;");
        using var contextualMetadata = JsonDocument.Parse(contextualMetadataJson ?? "{}");
        var metadataRoot = contextualMetadata.RootElement;
        Assert.Equal("contextual_text_v3", metadataRoot.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, metadataRoot.GetProperty("chunkIndex").GetInt32());
        Assert.Equal("unit_exact_v1", metadataRoot.GetProperty("chunkType").GetString());
        Assert.Equal("content", metadataRoot.GetProperty("contentRole").GetString());
        Assert.Equal("Pressure verification", metadataRoot.GetProperty("sectionTitle").GetString());
        Assert.Equal("Pressure verification", metadataRoot.GetProperty("headingPath").GetString());
        Assert.Equal("single_unit", metadataRoot.GetProperty("chunkComposition").GetString());
        Assert.Equal([0], metadataRoot.GetProperty("sourceUnitOrdinals").EnumerateArray().Select(item => item.GetInt32()).ToArray());

        var unitMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM document_units LIMIT 1;");
        using var unitMetadata = JsonDocument.Parse(unitMetadataJson ?? "{}");
        Assert.Equal("content", unitMetadata.RootElement.GetProperty("contentRole").GetString());
        Assert.Equal(0.0, unitMetadata.RootElement.GetProperty("navigationScore").GetDouble());

        var revision = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM document_revisions LIMIT 1;");
        Assert.Equal(1, revision.ingestion_version);
        Assert.Equal(1, revision.indexed_version);

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='exact_match_entries';");
        using var artifactMetadata = JsonDocument.Parse(artifactPayload!);
        Assert.Equal(1, artifactMetadata.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("canonical_text_v1", artifactMetadata.RootElement.GetProperty("hashBasis").GetString());

        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using (var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}"))
        {
            Assert.Equal("pdf_text", processingMetadata.RootElement.GetProperty("extractionSource").GetString());
            Assert.False(processingMetadata.RootElement.GetProperty("ocrAttempted").GetBoolean());
            Assert.False(processingMetadata.RootElement.GetProperty("ocrApplied").GetBoolean());
            Assert.Equal(JsonValueKind.Null, processingMetadata.RootElement.GetProperty("ocrDurationMs").ValueKind);
            Assert.True(processingMetadata.RootElement.TryGetProperty("documentLanguage", out _));
            Assert.Equal("document_profile_projector", processingMetadata.RootElement.GetProperty("documentLanguageSource").GetString());
            Assert.Equal("deterministic_v1", processingMetadata.RootElement.GetProperty("documentProfileVersion").GetString());
            Assert.Equal(JsonValueKind.Null, processingMetadata.RootElement.GetProperty("nativeExtractionQuality").ValueKind);
            var extractionQuality = processingMetadata.RootElement.GetProperty("extractionQuality");
            Assert.Equal(1, extractionQuality.GetProperty("pageCount").GetInt32());
            Assert.Equal(1, extractionQuality.GetProperty("textPageCount").GetInt32());
            Assert.Equal("ok", extractionQuality.GetProperty("textStatus").GetString());
            Assert.False(extractionQuality.GetProperty("ocrRecommended").GetBoolean());
            var retrievalChunkQuality = processingMetadata.RootElement.GetProperty("retrievalChunkQuality");
            Assert.Equal(1, retrievalChunkQuality.GetProperty("totalChunkCount").GetInt32());
            Assert.Equal(1, retrievalChunkQuality.GetProperty("searchableChunkCount").GetInt32());
            Assert.Equal(0, retrievalChunkQuality.GetProperty("rejectedChunkCount").GetInt32());
            Assert.False(retrievalChunkQuality.GetProperty("manualReviewRecommended").GetBoolean());
        }

        var pageMetadataJson = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM document_page_index LIMIT 1;");
        using (var pageMetadata = JsonDocument.Parse(pageMetadataJson ?? "{}"))
        {
            Assert.Equal(pages[0].WordCount, pageMetadata.RootElement.GetProperty("wordCount").GetInt32());
            var extractionQuality = pageMetadata.RootElement.GetProperty("extractionQuality");
            Assert.Equal("page_extraction_quality_v2", extractionQuality.GetProperty("diagnosticVersion").GetString());
            Assert.Equal("page_ok", extractionQuality.GetProperty("qualityStatus").GetString());
            Assert.Equal(1.0, extractionQuality.GetProperty("extractionConfidence").GetDouble());
            Assert.False(extractionQuality.GetProperty("manualReviewRecommended").GetBoolean());
            Assert.Equal("ok", extractionQuality.GetProperty("textStatus").GetString());
            Assert.False(extractionQuality.GetProperty("textSparse").GetBoolean());
            Assert.False(extractionQuality.GetProperty("ocrCandidate").GetBoolean());
            Assert.Equal(1, extractionQuality.GetProperty("unitCount").GetInt32());
            Assert.Equal(1, extractionQuality.GetProperty("chunkCount").GetInt32());
        }

        var profile = await conn.QuerySingleAsync<(string summary_text, string search_text, string[] keywords, string metadata)>(
            "SELECT summary_text, search_text, keywords, metadata::text FROM document_profiles LIMIT 1;");
        Assert.Contains("CEN.pdf", profile.summary_text, StringComparison.Ordinal);
        Assert.Contains("Pressure verification uses a calibrated gauge and records the measured value before the isolation valve is opened.", profile.search_text, StringComparison.Ordinal);
        Assert.Contains("pressure", profile.keywords);
        using (var profileMetadata = JsonDocument.Parse(profile.metadata))
        {
            Assert.Equal(1, profileMetadata.RootElement.GetProperty("contentCardCount").GetInt32());
            Assert.Contains(
                profileMetadata.RootElement.GetProperty("contentCards").EnumerateArray(),
                card => string.Equals(card.GetProperty("title").GetString(), "Pressure verification", StringComparison.Ordinal));
        }

        var profileCard = await conn.QuerySingleAsync<(string title, string search_text, string[] signals)>(
            "SELECT title, search_text, signals FROM document_profile_content_cards LIMIT 1;");
        Assert.Equal("Pressure verification", profileCard.title);
        Assert.Contains(profileCard.title, profileCard.search_text, StringComparison.Ordinal);
        Assert.Contains("pressure", profileCard.signals);

        var profileMatches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "pressure verification",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal(docId.ToString(), profileMatch.DocId);
        Assert.Equal(docPath, profileMatch.DocPath);
        Assert.Equal(1, profileMatch.IngestionVersion);
        Assert.Equal("090909", profileMatch.HashDoc);
        Assert.Contains("Pressure verification", profileMatch.Text, StringComparison.OrdinalIgnoreCase);

        var retrievalChunkMetadata = await conn.ExecuteScalarAsync<string>(
            "SELECT metadata::text FROM retrieval_chunks LIMIT 1;");
        using var chunkMetadata = JsonDocument.Parse(retrievalChunkMetadata!);
        Assert.Equal("unit_exact_v1", chunkMetadata.RootElement.GetProperty("chunkType").GetString());
        Assert.Equal("Pressure verification", chunkMetadata.RootElement.GetProperty("sectionTitle").GetString());
        Assert.Equal("Pressure verification", chunkMetadata.RootElement.GetProperty("headingPath").GetString());

        var retrievalChunkId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT retrieval_chunk_id FROM retrieval_chunks LIMIT 1;");
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 0), retrievalChunkId);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_only_searchable_retrieval_chunks_and_contextual_entries()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111112");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333334");
        const string docPath = "Ops/Manual.pdf";
        var noisyText = string.Join(
            ' ',
            Enumerable.Repeat(
                "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A 0 OQ =| Zz UO | W = cr O = 0",
                3));

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Reliable commissioning procedure with enough semantic body text.", 7, 62, [1]),
            new ExtractedPdfPage(2, noisyText, 120, noisyText.Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Commissioning", 1, 1, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].CharCount, pages[0].WordCount, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, noisyText, noisyText.Length, 120, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pages[0].Text, 7, [5], "unit_exact_v1", ExtractionTextStatus: "ok"),
            new ProjectedRetrievalChunk(1, 0, 1, 2, 2, noisyText, 120, [6], "section_window_v1", ExtractionTextStatus: "ok")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "excerpt:\n" + pages[0].Text, 72, 8, [7]),
            new ProjectedContextualTextEntry(1, 0, 1, 1, 2, 2, "excerpt:\n" + noisyText, noisyText.Length + 9, 121, [8])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunks;"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contextual_text_entries;"));
        Assert.DoesNotContain(
            "iS) m =|",
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM retrieval_chunks LIMIT 1;"),
            StringComparison.Ordinal);

        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}");
        var retrievalQuality = processingMetadata.RootElement.GetProperty("retrievalChunkQuality");
        Assert.Equal(2, retrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("ocrNoiseRejectedChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("rejectionReasons").GetProperty("ocrNoise").GetInt32());

        var artifactPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_revision_artifacts WHERE artifact_type='retrieval_chunks';");
        using var artifactMetadata = JsonDocument.Parse(artifactPayload!);
        Assert.Equal(1, artifactMetadata.RootElement.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"quality\":{\"existing\":\"preserved\"},\"traceTag\":\"preserved\"}")]
    [InlineData("{\"quality\":null}")]
    [InlineData("{\"quality\":7}")]
    public async Task CompleteUpsertAsync_does_not_publish_when_no_searchable_chunks_exist(string initialPayload)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111113");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222225");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333337");
        const string docPath = "Generic/navigation-only.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using (var seedConnection = new NpgsqlConnection(db.ConnectionString))
        {
            await seedConnection.OpenAsync();
            await seedConnection.ExecuteAsync(
                "UPDATE ingestion_jobs SET payload=@initialPayload::jsonb WHERE job_id=@jobId;",
                new { initialPayload, jobId });
        }

        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 8, 36, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 36, 8, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "1 Introduction 3\n2 Scope 8\n3 Terms 12",
                8,
                [3],
                RetrievalContentClassifier.NavigationChunkType,
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationScore: 0.95,
                ContentDensityScore: 0.1)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 8],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, int indexed_version, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            "SELECT status, indexed_version, auto_ingest_paused, auto_ingest_pause_reason FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("error", document.status);
        Assert.Equal(0, document.indexed_version);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("manual_review_no_searchable_chunks", document.auto_ingest_pause_reason);

        var job = await conn.QuerySingleAsync<(string status, string last_error, string payload)>(
            "SELECT status, last_error, payload::text FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });
        Assert.Equal("failed", job.status);
        Assert.Equal("manual_review_no_searchable_chunks", job.last_error);

        using var payload = JsonDocument.Parse(job.payload);
        Assert.Equal("failed_no_searchable_chunks", payload.RootElement.GetProperty("progress").GetProperty("phase").GetString());
        using var originalPayload = JsonDocument.Parse(initialPayload);
        if (originalPayload.RootElement.TryGetProperty("traceTag", out var traceTag))
        {
            Assert.Equal(traceTag.GetString(), payload.RootElement.GetProperty("traceTag").GetString());
            Assert.Equal("preserved", payload.RootElement.GetProperty("quality").GetProperty("existing").GetString());
        }
        var retrievalQuality = payload.RootElement.GetProperty("quality").GetProperty("retrieval");
        Assert.Equal(1, retrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, retrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("navigationChunkCount").GetInt32());
        Assert.Equal(1, retrievalQuality.GetProperty("rejectionReasons").GetProperty("navigationOnly").GetInt32());

        var processingRun = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version_before, int indexed_version_after, Guid? revision_id, string payload)>(
            """
            SELECT status, ingestion_version, indexed_version_before, indexed_version_after, revision_id, payload::text
            FROM document_processing_runs
            WHERE tenant_id=@tenant_id AND job_id=@job_id;
            """,
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal("failed", processingRun.status);
        Assert.Equal(1, processingRun.ingestion_version);
        Assert.Equal(0, processingRun.indexed_version_before);
        Assert.Equal(0, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);

        using var processingPayload = JsonDocument.Parse(processingRun.payload);
        Assert.False(processingPayload.RootElement.GetProperty("published").GetBoolean());
        Assert.False(processingPayload.RootElement.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("retrieval_quality_failure_v1", processingPayload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal("manual_review_no_searchable_chunks", processingPayload.RootElement.GetProperty("failureReason").GetString());
        var processingRetrievalQuality = processingPayload.RootElement.GetProperty("retrievalChunkQuality");
        Assert.Equal(1, processingRetrievalQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, processingRetrievalQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(1, processingRetrievalQuality.GetProperty("rejectionReasons").GetProperty("navigationOnly").GetInt32());
        var pageDiagnostics = processingPayload.RootElement.GetProperty("pageDiagnostics").EnumerateArray().ToArray();
        var firstPageDiagnostic = Assert.Single(pageDiagnostics);
        Assert.Equal(1, firstPageDiagnostic.GetProperty("unitCount").GetInt32());
        Assert.Equal(1, firstPageDiagnostic.GetProperty("chunkCount").GetInt32());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        var retrievalChunkCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM retrieval_chunks c
            JOIN document_revisions r ON r.revision_id=c.revision_id
            WHERE r.tenant_id=@tenant_id AND r.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
        Assert.Equal(0, retrievalChunkCount);
    }

    [Fact]
    public async Task CompleteUpsertAsync_preserves_previous_indexed_version_when_new_run_has_no_searchable_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111114");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222226");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333338");
        const string docPath = "Generic/previous-version-navigation-only.pdf";
        byte[] oldHash = [1, 2, 3, 4];
        byte[] newHash = [9, 9, 8, 8];
        var oldMtime = DateTime.UtcNow.AddDays(-3);
        var newMtime = DateTime.UtcNow;

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using (var seedConn = new NpgsqlConnection(db.ConnectionString))
        {
            await seedConn.OpenAsync();
            await seedConn.ExecuteAsync(
                """
                UPDATE documents
                SET status='indexed',
                    content_hash=@old_hash,
                    file_size=1234,
                    file_mtime=@old_mtime,
                    page_count=9
                WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
                """,
                new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    old_hash = oldHash,
                    old_mtime = DateTime.SpecifyKind(oldMtime, DateTimeKind.Utc)
                });
        }

        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 8, 36, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "1 Introduction 3\n2 Scope 8\n3 Terms 12", 36, 8, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "1 Introduction 3\n2 Scope 8\n3 Terms 12",
                8,
                [3],
                RetrievalContentClassifier.NavigationChunkType,
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationScore: 0.95,
                ContentDensityScore: 0.1)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            newHash,
            size: 9876,
            mtimeUtc: newMtime,
            version: 2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, string hash, long file_size, int ingestion_version, int indexed_version, int page_count, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            """
            SELECT status,
                   LOWER(ENCODE(content_hash, 'hex')) AS hash,
                   file_size,
                   ingestion_version,
                   indexed_version,
                   page_count,
                   auto_ingest_paused,
                   auto_ingest_pause_reason
            FROM documents
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal("indexed", document.status);
        Assert.Equal(Convert.ToHexString(oldHash).ToLowerInvariant(), document.hash);
        Assert.Equal(1234, document.file_size);
        Assert.Equal(2, document.ingestion_version);
        Assert.Equal(1, document.indexed_version);
        Assert.Equal(9, document.page_count);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("manual_review_no_searchable_chunks", document.auto_ingest_pause_reason);

        var processingRun = await conn.QuerySingleAsync<(string status, int indexed_version_before, int indexed_version_after, Guid? revision_id, string payload)>(
            """
            SELECT status, indexed_version_before, indexed_version_after, revision_id, payload::text
            FROM document_processing_runs
            WHERE tenant_id=@tenant_id AND job_id=@job_id;
            """,
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal("failed", processingRun.status);
        Assert.Equal(1, processingRun.indexed_version_before);
        Assert.Equal(1, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);

        using var processingPayload = JsonDocument.Parse(processingRun.payload);
        Assert.Equal("retrieval_quality_failure_v1", processingPayload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal(0, processingPayload.RootElement.GetProperty("retrievalChunkQuality").GetProperty("searchableChunkCount").GetInt32());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_job_version_when_indexed_version_has_gap()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-aaaa-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-bbbb-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-cccc-3333-3333-333333333333");
        const string docPath = "Generic/RetryGap.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Retry gap content preserves the original source across successful ingestion versions.", CountWords("Retry gap content preserves the original source across successful ingestion versions."), "Retry gap content preserves the original source across successful ingestion versions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Retry gap", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Retry gap content preserves the original source across successful ingestion versions.", "Retry gap content preserves the original source across successful ingestion versions.".Length, CountWords("Retry gap content preserves the original source across successful ingestion versions."), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Retry gap content preserves the original source across successful ingestion versions.", CountWords("Retry gap content preserves the original source across successful ingestion versions."), [3], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [5, 5, 5],
            size: 55,
            mtimeUtc: DateTime.UtcNow,
            version: 5,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var documentVersion = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version)>(
            "SELECT ingestion_version, indexed_version FROM documents WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(5, documentVersion.ingestion_version);
        Assert.Equal(5, documentVersion.indexed_version);

        var revisionVersion = await conn.QuerySingleAsync<(Guid revision_id, int ingestion_version, int indexed_version)>(
            "SELECT revision_id, ingestion_version, indexed_version FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 5), revisionVersion.revision_id);
        Assert.Equal(5, revisionVersion.ingestion_version);
        Assert.Equal(5, revisionVersion.indexed_version);

        var processingRun = await conn.QuerySingleAsync<(int ingestion_version, int indexed_version_before, int indexed_version_after)>(
            "SELECT ingestion_version, indexed_version_before, indexed_version_after FROM document_processing_runs WHERE tenant_id=@tenant_id AND job_id=@job_id;",
            new { tenant_id = tenantId, job_id = jobId });
        Assert.Equal(5, processingRun.ingestion_version);
        Assert.Equal(3, processingRun.indexed_version_before);
        Assert.Equal(5, processingRun.indexed_version_after);

        var retrievalChunkId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT c.retrieval_chunk_id FROM retrieval_chunks c JOIN document_revisions r ON r.revision_id=c.revision_id WHERE c.tenant_id=@tenant_id AND r.doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0), retrievalChunkId);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_ocr_diagnostics_in_processing_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333334");
        const string docPath = "OCR/image-diagnostics.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Native text plus image label OCR PANEL", 7, 38, [1], ImageCount: 1),
            new ExtractedPdfPage(2, "Native text without applied OCR", 5, 31, [2], ImageCount: 1)
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "OCR diagnostics", 1, 1, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].CharCount, pages[0].WordCount, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, pages[1].Text, pages[1].CharCount, pages[1].WordCount, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 2, string.Join('\n', pages.Select(static page => page.Text)), 12, [5], "section_unit_v1")
        };
        var exactMatchEntries = ExactMatchEntryExtractor.Extract(units);
        var contextualTextEntries = ContextualTextProjector.Project(docPath, sections, units, retrievalChunks);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "image_page",
            CandidatePageCount: 7,
            AttemptedPageCount: 3,
            SkippedPageCount: 4,
            MaxPages: 3,
            CandidatePages: [1, 2, 5, 8, 13, 21, 34],
            AttemptedPages: [1, 13, 34],
            SkippedPages: [2, 5, 8, 21],
            PagesWithOcrText: [1, 13],
            PagesWithNovelText: [1],
            ImagePageDiagnostics:
            [
                new PdfImagePageOcrDiagnostic(1, "novel_text_applied", OcrWordCount: 6, OcrCharCount: 38, ExitCode: 0),
                new PdfImagePageOcrDiagnostic(13, "no_novel_text", "duplicate_or_below_threshold", OcrWordCount: 4, OcrCharCount: 24, ExitCode: 0),
                new PdfImagePageOcrDiagnostic(34, "render_failed", "exit_code_non_zero", ExitCode: 1),
                new PdfImagePageOcrDiagnostic(2, "skipped", "budget")
            ]);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None,
            extractionSource: "pdf_text_plus_image_ocr",
            ocrAttempted: true,
            ocrApplied: true,
            ocrLanguages: "eng",
            ocrDurationMs: 321,
            ocrDiagnostics: diagnostics);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var processingPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM document_processing_runs LIMIT 1;");
        using var processingMetadata = JsonDocument.Parse(processingPayload ?? "{}");
        var ocrDiagnostics = processingMetadata.RootElement.GetProperty("ocrDiagnostics");
        Assert.Equal("image_page", ocrDiagnostics.GetProperty("mode").GetString());
        Assert.Equal(7, ocrDiagnostics.GetProperty("candidatePageCount").GetInt32());
        Assert.Equal(3, ocrDiagnostics.GetProperty("attemptedPageCount").GetInt32());
        Assert.Equal(4, ocrDiagnostics.GetProperty("skippedPageCount").GetInt32());
        Assert.Equal([1, 13, 34], ocrDiagnostics.GetProperty("attemptedPages").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        Assert.Equal([1], ocrDiagnostics.GetProperty("pagesWithNovelText").EnumerateArray().Select(static item => item.GetInt32()).ToArray());
        var imagePageDiagnostics = ocrDiagnostics.GetProperty("imagePageDiagnostics").EnumerateArray().ToArray();
        Assert.Contains(imagePageDiagnostics, item =>
            item.GetProperty("pageNumber").GetInt32() == 1
            && item.GetProperty("status").GetString() == "novel_text_applied"
            && item.GetProperty("ocrWordCount").GetInt32() == 6);
        Assert.Contains(imagePageDiagnostics, item =>
            item.GetProperty("pageNumber").GetInt32() == 34
            && item.GetProperty("status").GetString() == "render_failed"
            && item.GetProperty("reason").GetString() == "exit_code_non_zero");

        var pageMetadataRows = (await conn.QueryAsync<(int page_number, string metadata)>(
            "SELECT page_number, metadata::text FROM document_page_index ORDER BY page_number;"))
            .ToDictionary(static row => row.page_number, static row => row.metadata);
        using (var pageOneMetadata = JsonDocument.Parse(pageMetadataRows[1]))
        {
            Assert.Equal("novel_text_applied", pageOneMetadata.RootElement.GetProperty("imageOcrStatus").GetString());
            Assert.Equal(6, pageOneMetadata.RootElement.GetProperty("imageOcrWordCount").GetInt32());
            Assert.Equal(38, pageOneMetadata.RootElement.GetProperty("imageOcrCharCount").GetInt32());
            Assert.Equal(0, pageOneMetadata.RootElement.GetProperty("imageOcrExitCode").GetInt32());
            Assert.False(pageOneMetadata.RootElement.GetProperty("imageOcrTimedOut").GetBoolean());
        }
        using (var pageTwoMetadata = JsonDocument.Parse(pageMetadataRows[2]))
        {
            Assert.Equal("skipped", pageTwoMetadata.RootElement.GetProperty("imageOcrStatus").GetString());
            Assert.Equal("budget", pageTwoMetadata.RootElement.GetProperty("imageOcrReason").GetString());
            Assert.True(pageTwoMetadata.RootElement.GetProperty("imageOcrWordCount").ValueKind is JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_persists_diagnostics_and_non_indexable_document_state()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333335");
        const string docPath = "OCR/scanned-non-indexable.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "full_document",
            CandidatePageCount: 0,
            AttemptedPageCount: 0,
            SkippedPageCount: 0,
            MaxPages: 0,
            CandidatePages: [],
            AttemptedPages: [],
            SkippedPages: [],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            ExitCode: 2,
            TimedOut: false,
            TimeoutSeconds: 30,
            Stderr: PdfOcrTextExtractor.TruncateOcrProcessOutputForDiagnostics(new string('e', 5000)),
            FailureReason: "exit_code_non_zero",
            AppliedReason: "ocr_extraction_failed");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [9, 9, 9],
            sourceSize: 123,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 456,
            ocrDiagnostics: diagnostics,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None,
            extractedPages: [page]);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, bool auto_ingest_paused, string auto_ingest_pause_reason)>(
            "SELECT status, auto_ingest_paused, auto_ingest_pause_reason FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("error", document.status);
        Assert.True(document.auto_ingest_paused);
        Assert.Equal("scanned_pdf_not_indexable", document.auto_ingest_pause_reason);

        var run = await conn.QuerySingleAsync<(string status, string payload)>(
            "SELECT status, payload::text FROM document_processing_runs WHERE job_id=@job_id;",
            new { job_id = jobId });
        Assert.Equal("failed", run.status);
        using var payload = JsonDocument.Parse(run.payload);
        Assert.False(payload.RootElement.GetProperty("published").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("scanned_pdf_not_indexable", payload.RootElement.GetProperty("failureReason").GetString());
        Assert.Equal("extraction_failure_v1", payload.RootElement.GetProperty("diagnosticVersion").GetString());
        Assert.Equal("failed_run", payload.RootElement.GetProperty("diagnosticScope").GetString());

        var ocrDiagnostics = payload.RootElement.GetProperty("ocrDiagnostics");
        Assert.Equal("full_document", ocrDiagnostics.GetProperty("mode").GetString());
        Assert.Equal(2, ocrDiagnostics.GetProperty("exitCode").GetInt32());
        Assert.Equal(30, ocrDiagnostics.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("exit_code_non_zero", ocrDiagnostics.GetProperty("failureReason").GetString());
        Assert.Equal("ocr_extraction_failed", ocrDiagnostics.GetProperty("appliedReason").GetString());
        Assert.True(ocrDiagnostics.GetProperty("stderr").GetString()!.Length <= 4096);

        var pageDiagnostics = payload.RootElement.GetProperty("pageDiagnostics").EnumerateArray().ToArray();
        var pageDiagnostic = Assert.Single(pageDiagnostics);
        Assert.Equal(1, pageDiagnostic.GetProperty("pageNumber").GetInt32());
        Assert.Equal("manual_review_empty_text", pageDiagnostic.GetProperty("qualityStatus").GetString());
        Assert.True(pageDiagnostic.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.True(pageDiagnostic.GetProperty("ocrCandidate").GetBoolean());

        var revisionCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });
        var pageIndexCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_page_index pi
            JOIN document_revisions r ON r.revision_id=pi.revision_id
            WHERE r.tenant_id=@tenant_id AND r.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        Assert.Equal(0, revisionCount);
        Assert.Equal(0, pageIndexCount);
    }

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1212-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-3434-2222-2222-222222222223");
        var jobId = Guid.Parse("33333333-5656-3333-3333-333333333335");
        const string docPath = "OCR/stale-non-indexable.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var published = await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [9, 8, 7],
            sourceSize: 123,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 456,
            ocrDiagnostics: null,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var document = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version, bool auto_ingest_paused)>(
            "SELECT status, ingestion_version, indexed_version, auto_ingest_paused FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;",
            new { tenant_id = tenantId, doc_path = docPath });
        var runCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM document_processing_runs WHERE job_id=@job_id;",
            new { job_id = jobId });
        var job = await conn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });

        Assert.Equal("pending", document.status);
        Assert.Equal(2, document.ingestion_version);
        Assert.Equal(0, document.indexed_version);
        Assert.False(document.auto_ingest_paused);
        Assert.False(published);
        Assert.Equal(0, runCount);
        Assert.Equal("canceled", job.status);
        Assert.Equal("superseded_failed_ocr_publish", job.last_error);

        var followUp = await conn.QuerySingleAsync<(string status, string? version, string? source)>(
            """
            SELECT status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant_id AND doc_path=@doc_path AND action='upsert' AND status='queued';
            """,
            new { tenant_id = tenantId, doc_path = docPath });
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoints_include_failed_non_indexable_ocr_documents()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111112");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222224");
        var jobId = Guid.Parse("33333333-3333-3333-3333-333333333336");
        const string docPath = "OCR/scanned-ocr-disabled.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var page = new ExtractedPdfPage(1, "", 0, 0, [1], ImageCount: 1);
        var extractionQuality = PdfExtractionQualitySummary.FromPages([page]);
        var diagnostics = new PdfOcrDiagnostics(
            Mode: "ocr_disabled",
            CandidatePageCount: 1,
            AttemptedPageCount: 0,
            SkippedPageCount: 1,
            MaxPages: 0,
            CandidatePages: [1],
            AttemptedPages: [],
            SkippedPages: [1],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            FailureReason: "ocr_required_but_disabled",
            AppliedReason: "ocr_disabled");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            jobId,
            docPath,
            sourceHash: [7, 7, 7],
            sourceSize: 321,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 1,
            extractionSource: "pdf_text",
            ocrAttempted: false,
            ocrLanguages: null,
            ocrDurationMs: null,
            ocrDiagnostics: diagnostics,
            extractionQuality,
            nativeExtractionQuality: extractionQuality,
            failureReason: "ocr_required_but_disabled",
            CancellationToken.None,
            extractedPages: [page]);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE document_processing_runs SET payload = payload - 'documentIndexable' WHERE tenant_id=@tenantId AND job_id=@jobId;",
                new { tenantId, jobId });
        }

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "OCR", null, 20);
        await listResult.ExecuteAsync(listCtx);

        Assert.Equal(StatusCodes.Status200OK, listCtx.Response.StatusCode);
        using (var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx)))
        {
            var summary = listPayload.RootElement.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("totalDocuments").GetInt32());
            Assert.Equal(1, summary.GetProperty("emptyTextDocuments").GetInt32());
            Assert.Equal(1, summary.GetProperty("manualReviewRecommendedDocuments").GetInt32());
            Assert.Equal(0, summary.GetProperty("llmEnrichmentPendingDocuments").GetInt32());

            var item = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(docPath, item.GetProperty("docPath").GetString());
            Assert.Equal("error", item.GetProperty("documentStatus").GetString());
            Assert.Equal("failed", item.GetProperty("processingRunStatus").GetString());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("qualityStatus").GetString());
            Assert.False(item.GetProperty("documentIndexable").GetBoolean());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("failureReason").GetString());
            Assert.Equal("ocr_required_but_disabled", item.GetProperty("ocrFailureReason").GetString());
            Assert.True(item.GetProperty("manualReviewRecommended").GetBoolean());
            Assert.True(item.GetProperty("ocrRecommended").GetBoolean());
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.Equal("error", root.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", root.GetProperty("processingRunStatus").GetString());
        Assert.False(root.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("ocr_required_but_disabled", root.GetProperty("failureReason").GetString());
        Assert.Equal("failed_run", root.GetProperty("diagnosticScope").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("manualReviewRecommendedPages").GetInt32());
        var pageItem = Assert.Single(root.GetProperty("pages").EnumerateArray());
        Assert.Equal(1, pageItem.GetProperty("pageNumber").GetInt32());
        Assert.Equal("manual_review_empty_text", pageItem.GetProperty("qualityStatus").GetString());
        Assert.True(pageItem.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.True(pageItem.GetProperty("ocrCandidate").GetBoolean());
        Assert.Contains(
            pageItem.GetProperty("signals").EnumerateArray().Select(static item => item.GetString()),
            value => string.Equals(value, "no_chunks_on_page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishFailedOcrExtractionAsync_keeps_existing_indexed_source_metadata()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("11111111-1111-1111-1111-444444444444");
        var docId = Guid.Parse("22222222-2222-2222-2222-444444444444");
        var firstJobId = Guid.Parse("33333333-3333-3333-3333-444444444444");
        var failedJobId = Guid.Parse("33333333-3333-3333-3333-444444444445");
        const string docPath = "OCR/already-indexed.pdf";
        const string text = "Previously indexed text remains the published source.";
        var originalHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var failedHash = Enumerable.Repeat((byte)0x22, 32).ToArray();

        await db.SeedRunningJobAsync(tenantId, docId, firstJobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            firstJobId,
            docPath,
            originalHash,
            size: 1234,
            mtimeUtc: DateTime.UtcNow.AddDays(-1),
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Published", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET ingestion_version=2
                WHERE tenant_id=@tenant AND doc_id=@docId;

                INSERT INTO ingestion_jobs(
                  job_id, tenant_id, action, doc_path, category, status,
                  attempts, locked_by, locked_at, available_at, created_at, started_at, payload
                )
                VALUES(
                  @jobId, @tenant, 'upsert', @docPath, 'OCR', 'running',
                  1, 'test-worker', now(), now(), now(), now(), '{}'::jsonb
                );
                """,
                new { tenant = tenantId, docId, jobId = failedJobId, docPath });
        }

        var failedPage = new ExtractedPdfPage(1, "", 0, 0, [9], ImageCount: 1);
        var failedQuality = PdfExtractionQualitySummary.FromPages([failedPage]);
        await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
            ds,
            tenantId,
            docId,
            failedJobId,
            docPath,
            sourceHash: failedHash,
            sourceSize: 9876,
            sourceMtimeUtc: DateTime.UtcNow,
            ingestionVersion: 2,
            extractionSource: "pdf_text",
            ocrAttempted: true,
            ocrLanguages: "eng",
            ocrDurationMs: 55,
            ocrDiagnostics: new PdfOcrDiagnostics(
                Mode: "full_document",
                CandidatePageCount: 1,
                AttemptedPageCount: 1,
                SkippedPageCount: 0,
                MaxPages: 1,
                CandidatePages: [1],
                AttemptedPages: [1],
                SkippedPages: [],
                PagesWithOcrText: [],
                PagesWithNovelText: [],
                FailureReason: "exit_code_non_zero",
                AppliedReason: "ocr_extraction_failed"),
            extractionQuality: failedQuality,
            nativeExtractionQuality: failedQuality,
            failureReason: "scanned_pdf_not_indexable",
            CancellationToken.None,
            extractedPages: [failedPage]);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var document = await conn.QuerySingleAsync<(string hash, long file_size, int ingestion_version, int indexed_version, string status, bool auto_ingest_paused)>(
                """
                SELECT LOWER(ENCODE(content_hash, 'hex')) AS hash,
                       file_size,
                       ingestion_version,
                       indexed_version,
                       status,
                       auto_ingest_paused
                FROM documents
                WHERE tenant_id=@tenant AND doc_id=@docId;
                """,
                new { tenant = tenantId, docId });

            Assert.Equal(Convert.ToHexString(originalHash).ToLowerInvariant(), document.hash);
            Assert.Equal(1234, document.file_size);
            Assert.Equal(2, document.ingestion_version);
            Assert.Equal(1, document.indexed_version);
            Assert.Equal("indexed", document.status);
            Assert.True(document.auto_ingest_paused);
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.Equal("indexed", root.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", root.GetProperty("processingRunStatus").GetString());
        Assert.False(root.GetProperty("documentIndexable").GetBoolean());
        Assert.Equal("scanned_pdf_not_indexable", root.GetProperty("failureReason").GetString());
        Assert.Equal("failed_run", root.GetProperty("diagnosticScope").GetString());
        var publishedRevision = root.GetProperty("publishedRevision");
        Assert.True(publishedRevision.GetProperty("searchable").GetBoolean());
        Assert.Equal(1, publishedRevision.GetProperty("indexedVersion").GetInt32());
        var failedPageItem = Assert.Single(root.GetProperty("pages").EnumerateArray());
        Assert.Equal("manual_review_empty_text", failedPageItem.GetProperty("qualityStatus").GetString());
        Assert.True(failedPageItem.GetProperty("manualReviewRecommended").GetBoolean());

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "OCR", null, 20);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var failedItem = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("indexed", failedItem.GetProperty("documentStatus").GetString());
        Assert.Equal("failed", failedItem.GetProperty("processingRunStatus").GetString());
        Assert.Equal("scanned_pdf_not_indexable", failedItem.GetProperty("failureReason").GetString());

        // A diagnostic from an earlier ingestion attempt must not replace the
        // published revision's diagnostic once another attempt is current.
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE documents SET ingestion_version=3 WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
        }
        var newerAttemptCtx = BuildAdminDocumentsHttpContext(tenantId);
        var newerAttemptResult = await InvokeExtractionQualityPagesAsync(newerAttemptCtx, ds, docId);
        await newerAttemptResult.ExecuteAsync(newerAttemptCtx);
        using var newerAttemptPayload = JsonDocument.Parse(ReadResponseBody(newerAttemptCtx));
        Assert.Equal("done", newerAttemptPayload.RootElement.GetProperty("processingRunStatus").GetString());
        Assert.DoesNotContain("failed_run", newerAttemptPayload.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteUpsertAsync_strips_nul_characters_before_publishing_text_artifacts()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("10101010-1111-1111-1111-111111111111");
        var docId = Guid.Parse("20202020-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("30303030-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/NulText.pdf";
        const string rawText = "Intro\0 text with embedded nul";
        const string rawSection = "Intro\0 section";
        const string rawContext = "Document: NulText.pdf\nExcerpt:\nIntro\0 text with embedded nul";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: rawText.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, rawText, 5, rawText.Length, [1])],
            sections: [new ExtractedDocumentSection(0, rawSection, 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, rawText, rawText.Length, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, rawText, 5, [3], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, rawText, "intro\0 text", rawText.Length, 5, [4], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, rawContext, rawContext.Length, 8, [5])],
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var storedValues = new[]
        {
            await conn.ExecuteScalarAsync<string>("SELECT title FROM document_sections LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM document_units LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM retrieval_chunks LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM exact_match_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT normalized_text FROM exact_match_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT text_content FROM contextual_text_entries LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT summary_text FROM document_profiles LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT search_text FROM document_profiles LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT title FROM document_profile_content_cards LIMIT 1;"),
            await conn.ExecuteScalarAsync<string>("SELECT search_text FROM document_profile_content_cards LIMIT 1;")
        };

        foreach (var value in storedValues)
        {
            Assert.NotNull(value);
            Assert.DoesNotContain("\0", value!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpsertDocumentProfileAsync_purges_legacy_cards_with_stale_profile_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("40404040-1111-1111-1111-111111111111");
        var docId = Guid.Parse("50505050-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("60606060-3333-3333-3333-333333333333");
        const string docPath = "Operations/LegacyCards.pdf";
        const string text = "LEGACY CARD TITLE\nFresh details.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 5, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Legacy card title", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 5, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionId = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 1);
        var documentProfileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "deterministic_v1");
        await conn.ExecuteAsync(
            """
            UPDATE document_profile_content_cards
            SET profile_version='legacy_profile_mismatch'
            WHERE document_profile_id=@documentProfileId;
            """,
            new { documentProfileId });

        const string searchText = "Updated legacy profile search text";
        var profile = new ProjectedDocumentProfile(
            ProfileVersion: "deterministic_v1",
            Language: "en",
            SummaryText: "Updated legacy profile summary.",
            Keywords: ["legacy"],
            Entities: [],
            Topics: [],
            HypotheticalQuestions: [],
            Limits: [],
            SearchText: searchText,
            TokenCount: 5,
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(searchText)),
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "LEGACY CARD TITLE",
                    PageStart: 1,
                    PageEnd: 1,
                    Kind: "section",
                    Signals: ["legacy"])
            ]);

        await DocumentFoundationRepo.UpsertDocumentProfileAsync(
            conn,
            tx: null,
            tenantId,
            docId,
            revisionId,
            profile,
            CancellationToken.None);

        var cardRows = (await conn.QueryAsync<(string profile_version, int card_index, string title)>(
            """
            SELECT profile_version, card_index, title
            FROM document_profile_content_cards
            WHERE document_profile_id=@documentProfileId
            ORDER BY card_index;
            """,
            new { documentProfileId })).ToArray();

        var card = Assert.Single(cardRows);
        Assert.Equal("deterministic_v1", card.profile_version);
        Assert.Equal(0, card.card_index);
        Assert.Equal("LEGACY CARD TITLE", card.title);
    }

    [Fact]
    public async Task CompleteUpsertAsync_keeps_profile_content_cards_active_per_document_profile()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("44444444-1111-1111-1111-111111111111");
        var docId = Guid.Parse("55555555-2222-2222-2222-222222222222");
        var firstJobId = Guid.Parse("66666666-3333-3333-3333-333333333333");
        var secondJobId = Guid.Parse("77777777-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/ActiveCards.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, firstJobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var firstCommitted = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            firstJobId,
            docPath,
            hash: [1, 1, 1],
            size: 64,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, "OLD ACTIVE CARD\nOld details.", 5, 28, [1])],
            sections: [new ExtractedDocumentSection(0, "Old active heading", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", 28, 5, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", 5, [3], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "OLD ACTIVE CARD\nOld details.", "old active card old details", 28, 5, [4], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: ActiveCards.pdf\n\nExcerpt:\nOLD ACTIVE CARD\nOld details.", 62, 8, [5])],
            CancellationToken.None);

        Assert.True(firstCommitted);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(
            @"UPDATE documents
              SET status='pending',
                  ingestion_version=2,
                  indexed_version=1,
                  updated_at=now()
              WHERE tenant_id=@tenant_id AND doc_id=@doc_id;",
            new { tenant_id = tenantId, doc_id = docId });

        var payload = JsonSerializer.Serialize(new { });
        await conn.ExecuteAsync(
            @"INSERT INTO ingestion_jobs(
                  job_id, tenant_id, action, doc_path, category, status, attempts,
                  locked_by, locked_at, available_at, created_at, started_at, payload)
              VALUES(
                  @job_id, @tenant_id, 'upsert', @doc_path, 'Cuisine', 'running', 1,
                  'test-worker', now(), now(), now(), now(), CAST(@payload AS jsonb));",
            new { job_id = secondJobId, tenant_id = tenantId, doc_path = docPath, payload });

        var secondCommitted = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            secondJobId,
            docPath,
            hash: [2, 2, 2],
            size: 72,
            mtimeUtc: DateTime.UtcNow,
            version: 2,
            pages: [new ExtractedPdfPage(1, "NEW ACTIVE CARD\nFresh details.", 5, 30, [6])],
            sections: [new ExtractedDocumentSection(0, "New active heading", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", 30, 5, [7])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", 5, [8], "unit_exact_v1")],
            exactMatchEntries: [new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "NEW ACTIVE CARD\nFresh details.", "new active card fresh details", 30, 5, [9], "verbatim_excerpt")],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: ActiveCards.pdf\n\nExcerpt:\nNEW ACTIVE CARD\nFresh details.", 64, 8, [10])],
            CancellationToken.None);

        Assert.True(secondCommitted);

        var profileCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_profiles;");
        var cardRows = (await conn.QueryAsync<(string title, Guid revision_id)>(
            "SELECT title, revision_id FROM document_profile_content_cards ORDER BY title;")).ToArray();

        Assert.Equal(2, profileCount);
        var currentRevision = await conn.QuerySingleAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=2;",
            new { tenant=tenantId, docId });
        var currentCards = cardRows.Where(row => row.revision_id == currentRevision).ToArray();
        Assert.Contains(currentCards, row => string.Equals(row.title, "New active heading", StringComparison.Ordinal));
        Assert.DoesNotContain(currentCards, row => row.title.Contains("OLD", StringComparison.OrdinalIgnoreCase));
        // Published revisions keep their own cards. Updating one profile must
        // not purge cards owned by the earlier revision's profile.
        Assert.Contains(cardRows, row => row.revision_id != currentRevision && row.title == "Old active heading");
        Assert.Equal(2, cardRows.Select(row => row.revision_id).Distinct().Count());
    }

    [Fact]
    public async Task CompleteUpsertAsync_merges_capability_a_seed_without_unproved_content_cards()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-888888888888");
        var docId = Guid.Parse("99999999-2222-2222-2222-888888888888");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-888888888888");
        const string docPath = "Generic/CapabilityASeed.pdf";
        const string text = "Generic extracted text for a baseline document.";
        var seed = new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: ["When should the pressure envelope validation be reviewed?"],
            SuggestedTags: ["pressure-envelope", "validation-review"],
            KeySectionTitles: ["Pressure envelope validation"],
            PreviewText: "The preview explains pressure envelope validation and review ownership.",
            BasedOnIndexedVersion: 0);

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Baseline", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            ct: CancellationToken.None,
            capabilityAProfileSeed: seed);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var profile = await conn.QuerySingleAsync<(string summary_text, string search_text, string[] keywords, string[] topics, string[] hypothetical_questions)>(
            """
            SELECT summary_text, search_text, keywords, topics, hypothetical_questions
            FROM document_profiles
            WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';
            """,
            new { tenant = tenantId, docId });
        var unprovedSeedCardCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_profile_content_cards
            WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1'
              AND normalized_title='pressure envelope validation';
            """,
            new { tenant = tenantId, docId });

        Assert.Contains("pressure envelope validation", profile.summary_text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pressure-envelope", profile.keywords);
        Assert.Contains("validation-review", profile.topics);
        Assert.Contains("When should the pressure envelope validation be reviewed?", profile.hypothetical_questions);
        Assert.Contains("pressure envelope validation", profile.search_text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unprovedSeedCardCount);
    }

    [Fact]
    public async Task EnqueueUpsertAsync_preserves_capability_a_seed_until_dequeue()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-999999999999");
        const string docPath = "Generic/QueuedCapabilityASeed.pdf";
        var seed = new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: ["Which review owns the queued validation window?"],
            SuggestedTags: ["queued-validation", "profile-seed"],
            KeySectionTitles: ["Queued validation window"],
            PreviewText: "Queued preview for profile seed transport.",
            BasedOnIndexedVersion: 0);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'Test tenant');",
            new { tenant = tenantId });

        var queued = await IngestionEnqueue.EnqueueUpsertAsync(
            conn,
            tenantId,
            docPath,
            "Generic",
            fi: null,
            CancellationToken.None,
            enqueueSource: "capability_a",
            capabilityAProfileSeed: seed);

        var payloadJson = await conn.ExecuteScalarAsync<string>(
            """
            SELECT payload::text
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND job_id=@jobId;
            """,
            new { tenant = tenantId, jobId = queued.JobId });

        Assert.Contains("\"source\": \"capability_a\"", payloadJson);
        Assert.Contains("\"capabilityAProfileSeed\"", payloadJson);
        Assert.Contains("Which review owns the queued validation window?", payloadJson);
        Assert.Contains("queued-validation", payloadJson);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var job = await JobRepo.TryDequeueAsync(ds, "seed-worker", CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(queued.DocId, job!.DocId);
        Assert.Equal(queued.Version, job.Version);
        Assert.NotNull(job.CapabilityAProfileSeed);
        Assert.Equal(0, job.CapabilityAProfileSeed!.BasedOnIndexedVersion);
        Assert.Equal(["Which review owns the queued validation window?"], job.CapabilityAProfileSeed.HypotheticalQuestions);
        Assert.Equal(["queued-validation", "profile-seed"], job.CapabilityAProfileSeed.SuggestedTags);
        Assert.Equal(["Queued validation window"], job.CapabilityAProfileSeed.KeySectionTitles);
    }

    [Fact]
    public async Task EnqueueUpsertAsync_while_running_job_is_superseded_queues_current_version_after_commit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-2222-2222-2222-999999999999");
        var docId = Guid.Parse("99999999-3333-3333-3333-aaaaaaaaaaaa");
        var runningJobId = Guid.Parse("aaaaaaaa-4444-4444-4444-bbbbbbbbbbbb");
        const string docPath = "Generic/SupersededRunning.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, runningJobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var enqueued = await IngestionEnqueue.EnqueueUpsertAsync(
            conn,
            tenantId,
            docPath,
            "Generic",
            fi: null,
            CancellationToken.None,
            enqueueSource: "admin");

        Assert.Equal(runningJobId, enqueued.JobId);
        Assert.Equal(2, enqueued.Version);

        var runningPayloadVersion = await conn.ExecuteScalarAsync<string?>(
            "SELECT payload #>> '{version}' FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId = runningJobId });
        Assert.Null(runningPayloadVersion);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            runningJobId,
            docPath,
            hash: [1, 2, 3],
            size: 10,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [],
            sections: [],
            units: [],
            retrievalChunks: [],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.False(committed);

        var oldStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId = runningJobId });
        var followUp = await conn.QuerySingleAsync<(Guid job_id, string status, string? version, string? source)>(
            """
            SELECT job_id, status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldStatus);
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_materialized_content_cards_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-111111111111");
        var docId = Guid.Parse("99999999-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333");
        const string docPath = "Operations/WeeklyPlan.pdf";
        const string text = "WEEKLY MAINTENANCE PLAN\nInspection schedule and spare-part checklist.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [3, 3, 3],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Weekly Maintenance Plan", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, $"Document: WeeklyPlan.pdf\nExcerpt:\n{text}", text.Length + 35, 10, [4])],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic administrative note.',
                search_text='generic administrative note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "weekly maintenance plan",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Equal(1, match.PageStart);
        Assert.Equal(1, match.PageEnd);
        Assert.Contains("Weekly Maintenance Plan", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_ignores_pruned_cards_left_only_in_profile_search_text()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-555555555555");
        var docId = Guid.Parse("99999999-2222-2222-2222-555555555555");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-666666666666");
        const string docPath = "Operations/CurrentManual.pdf";
        const string text = "Current operating note. Daily checks and operator handover.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [5, 5, 5],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Current operating note", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Current operating note.',
                search_text='retired phantom calibration',
                keywords=ARRAY[]::text[],
                entities=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;

            DELETE FROM document_profile_content_cards
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "retired phantom calibration",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_fresh_stored_summary_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-222222222222");
        var docId = Guid.Parse("99999999-2222-2222-2222-444444444444");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-444444444444");
        const string docPath = "Operations/StoredSummary.pdf";
        const string text = "Generic operational source text.";
        const string storedSummary = "The stored server summary explains orbital calibration cadence and night audit ownership.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 4, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Generic", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 4, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 4, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic administrative note.',
                search_text='generic administrative note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;

            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            SELECT
              d.tenant_id,
              d.doc_id,
              'medium',
              'en',
              saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version),
              @storedSummary,
              '{"strategy":"llm","runtimeCapabilityStatus":"selected"}'::jsonb,
              now(),
              now()
            FROM documents d
            WHERE d.tenant_id=@tenant_id AND d.doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId, storedSummary });
        var storedSummaryRevisionId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id LIMIT 1;",
            new { tenant_id = tenantId, doc_id = docId });
        await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
            conn,
            null,
            tenantId,
            storedSummaryRevisionId,
            CancellationToken.None);

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "orbital calibration cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains("orbital calibration cadence", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orbital calibration cadence", match.EmbedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_summary_endpoint_refreshes_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-242424242424");
        var docId = Guid.Parse("99999999-2222-2222-2222-242424242424");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-242424242424");
        const string docPath = "Operations/DeleteSummaryProjection.pdf";
        const string text = "Generic operational baseline text.";
        const string summaryOnlyNeedle = "capstanquorinx vectraloz pyranelix";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: DeleteSummaryProjection.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO document_summaries(
                  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
                )
                SELECT
                  d.tenant_id,
                  d.doc_id,
                  'medium',
                  'en',
                  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version),
                  @summaryText,
                  '{}'::jsonb,
                  now(),
                  now()
                FROM documents d
                WHERE d.tenant_id=@tenant AND d.doc_id=@docId;
                """,
                new { tenant = tenantId, docId, summaryText = summaryOnlyNeedle });

            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });
            await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
                conn,
                null,
                tenantId,
                revisionId,
                CancellationToken.None);
        }

        var beforeDelete = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            summaryOnlyNeedle,
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        Assert.Contains(beforeDelete, match => match.DocPath == docPath);

        var deleteCtx = BuildAdminDocumentsHttpContext(tenantId);
        var deleteResult = await InvokeDeleteSummaryAsync(deleteCtx, ds, docId, "medium");
        await deleteResult.ExecuteAsync(deleteCtx);
        Assert.Equal(StatusCodes.Status200OK, deleteCtx.Response.StatusCode);

        var afterDelete = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            summaryOnlyNeedle,
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);
        Assert.DoesNotContain(afterDelete, match => match.DocPath == docPath);
        await using var projectionConn = await ds.OpenConnectionAsync();
        var projection = await projectionConn.QuerySingleAsync<string>(
            "SELECT search_text FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
            new { tenant=tenantId, docId });
        Assert.DoesNotContain(summaryOnlyNeedle, projection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_hypothetical_questions_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-333333333333");
        var docId = Guid.Parse("99999999-2222-2222-2222-333333333333");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-555555555555");
        const string docPath = "Operations/Audit.pdf";
        const string text = "Operational control note. The document itself uses generic wording.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [4, 4, 4],
            size: text.Length,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [new ExtractedPdfPage(1, text, 7, text.Length, [1])],
            sections: [new ExtractedDocumentSection(0, "Operational note", 1, 1, 1, 1, null)],
            units: [new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 7, [2])],
            retrievalChunks: [new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 7, [3], "unit_exact_v1")],
            exactMatchEntries: [],
            contextualTextEntries: [new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, $"Document: Audit.pdf\nExcerpt:\n{text}", text.Length + 31, 10, [4])],
            CancellationToken.None));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET summary_text='Generic operational note.',
                search_text='generic operational note',
                keywords=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY['When should the night audit cadence be reviewed?']::text[]
            WHERE tenant_id=@tenant_id AND doc_id=@doc_id;
            """,
            new { tenant_id = tenantId, doc_id = docId });
        var hypotheticalRevisionId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant_id AND doc_id=@doc_id LIMIT 1;",
            new { tenant_id = tenantId, doc_id = docId });
        await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
            conn,
            null,
            tenantId,
            hypotheticalRevisionId,
            CancellationToken.None);

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "night audit cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_llm_profile_terms_and_cards_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-777777777777");
        var docId = Guid.Parse("99999999-2222-2222-2222-777777777777");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-777777777777");
        const string docPath = "Generic/LlmProfileRecall.pdf";
        const string text = "Ordinary operational page text without the distinctive enrichment phrase.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: LlmProfileRecall.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });
            var llmProfileId = Guid.Parse("bbbbbbbb-4444-4444-4444-777777777777");
            var longKeyword = "pressure envelope validation " + new string('x', 220);

            await conn.ExecuteAsync(
                """
                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'LLM profile: the document explains pressure envelope validation and maintenance governance.',
                  ARRAY['pressure envelope validation', @longKeyword]::text[],
                  ARRAY['accumulator cluster']::text[],
                  ARRAY['maintenance governance']::text[],
                  ARRAY['When should the pressure envelope validation be reviewed?']::text[],
                  ARRAY['Use page chunks for exact thresholds.']::text[],
                  'LLM profile pressure envelope validation accumulator cluster maintenance governance reviewed',
                  10,
                  decode(repeat('47', 32), 'hex'),
                  '{}'::jsonb
                );

                INSERT INTO document_profile_content_cards(
                  content_card_id, tenant_id, document_profile_id, revision_id, doc_id, profile_version,
                  card_index, title, normalized_title, page_start, page_end, kind, signals,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @cardId, @tenant, @llmProfileId, @revision, @docId, 'llm_backoffice_v1',
                  0,
                  'Pressure envelope validation',
                  'pressure envelope validation',
                  1,
                  1,
                  'llm_content_card',
                  ARRAY['accumulator cluster','validation review']::text[],
                  'Pressure envelope validation accumulator cluster validation review',
                  7,
                  decode(repeat('48', 32), 'hex'),
                  '{
                    "generatedBy":"llm_backoffice_v1",
                    "evidence":{
                      "facts":[
                        {
                          "sourceText":"Pressure envelope validation accumulator cluster validation review",
                          "pageStart":1,
                          "pageEnd":1
                        }
                      ]
                    }
                  }'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId,
                    longKeyword,
                    cardId = Guid.Parse("cccccccc-5555-5555-5555-777777777777")
                });
            await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
                conn,
                null,
                tenantId,
                revisionId,
                CancellationToken.None);
        }

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "pressure envelope validation",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains("LLM profile", match.Text, StringComparison.Ordinal);
        Assert.Contains("maintenance governance", match.EmbedText, StringComparison.OrdinalIgnoreCase);
        var card = Assert.Single(match.MatchedContentCards!);
        Assert.Equal("Pressure envelope validation", card.Title);
        Assert.Equal("llm_content_card", card.Kind);
        Assert.Contains("accumulator cluster", card.Signals ?? []);
        Assert.NotNull(match.ProfileSignals);
        Assert.Equal("llm_backoffice_v1", match.ProfileSignals!.ProfileVersion);
        Assert.Equal("en", match.ProfileSignals.Language);
        Assert.Contains("pressure envelope validation", match.ProfileSignals.Keywords ?? []);
        Assert.All(match.ProfileSignals.Keywords ?? [], value => Assert.True(value.Length <= 160));
        Assert.Contains("maintenance governance", match.ProfileSignals.Topics ?? []);
        Assert.Contains("Use page chunks for exact thresholds.", match.ProfileSignals.Limits ?? []);
        Assert.Contains("pressure envelope validation", match.ProfileSignals.MatchedTerms ?? []);

        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto(
                Query: "pressure envelope validation",
                CategoryPath: "Generic",
                TopK: 2,
                Mode: "balanced"));
        await result.ExecuteAsync(ctx);

        await ExportRetrievalObservationAsync(
            "llm-profile-recall-observation.json",
            new { directProfileMatches = matches, httpBody = JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx)) });
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(response);
        var item = Assert.Single(response!.Items, static candidate => candidate.DocPath == docPath);
        Assert.Equal("document_profile", item.Retriever);
        Assert.Equal("en", item.DocLanguage);
        Assert.Equal("en", item.ProfileLanguage);
        Assert.False(string.IsNullOrWhiteSpace(item.SourceHash));
        var itemCard = Assert.Single(item.MatchedContentCards!);
        Assert.Equal("Pressure envelope validation", itemCard.Title);
        Assert.Equal("llm_content_card", itemCard.Kind);
        Assert.NotNull(item.SelectionHints);
        Assert.NotNull(item.ProfileSignals);
        Assert.Equal("llm_backoffice_v1", item.ProfileSignals!.ProfileVersion);
        Assert.Equal("en", item.ProfileSignals.Language);
        Assert.Contains("pressure envelope validation", item.ProfileSignals.Keywords ?? []);
        Assert.All(item.ProfileSignals.Keywords ?? [], value => Assert.True(value.Length <= 160));
        Assert.Contains("maintenance governance", item.ProfileSignals.Topics ?? []);
        Assert.Contains("Use page chunks for exact thresholds.", item.ProfileSignals.Limits ?? []);

        var inventoryContext = BuildRagHttpContext(tenantId);
        var inventoryResult = await InvokeDocumentsContentCardsAsync(
            inventoryContext,
            ds,
            categoryPath: "Generic",
            q: "pressure envelope validation",
            inventoryMode: null,
            limit: 20,
            offset: 0);
        await inventoryResult.ExecuteAsync(inventoryContext);

        Assert.Equal(StatusCodes.Status200OK, inventoryContext.Response.StatusCode);
        using var inventoryPayload = JsonDocument.Parse(ReadResponseBody(inventoryContext));
        var inventoryRoot = inventoryPayload.RootElement;
        Assert.True(inventoryRoot.GetProperty("citable").GetBoolean());
        Assert.Equal(1, inventoryRoot.GetProperty("total").GetInt32());
        var inventoryCard = Assert.Single(inventoryRoot.GetProperty("items").EnumerateArray());
        Assert.Equal("Pressure envelope validation", inventoryCard.GetProperty("title").GetString());
        Assert.Equal(docPath, inventoryCard.GetProperty("docPath").GetString());
        Assert.Equal(1, inventoryCard.GetProperty("pageStart").GetInt32());
        Assert.Equal(1, inventoryCard.GetProperty("pageEnd").GetInt32());
        Assert.Equal(
            "Pressure envelope validation accumulator cluster validation review",
            inventoryCard
                .GetProperty("evidence")
                .GetProperty("facts")[0]
                .GetProperty("sourceText")
                .GetString());
    }

    [Fact]
    public async Task RefreshDocumentProfileSearchEntryAsync_filters_unsafe_legacy_profile_cards()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-787878787878");
        var docId = Guid.Parse("99999999-2222-2222-2222-787878787878");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-787878787878");
        const string docPath = "Generic/LegacyProfileCards.pdf";
        const string text = "Grounded profile card source paragraph with validated inspection evidence.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Legacy profile cards",
            text,
            $"Document: LegacyProfileCards.pdf\nHeading Path: Legacy profile cards\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        var revisionId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
            new { tenant = tenantId, docId });
        var profileId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT document_profile_id FROM document_profiles WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
            new { tenant = tenantId, docId });

        await conn.ExecuteAsync(
            "DELETE FROM document_profile_content_cards WHERE tenant_id=@tenant AND document_profile_id=@profileId;",
            new { tenant = tenantId, profileId });

        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET search_text = 'LegacyProfileCards.pdf Legacy profile cards Unsafe legacy invention invented Grounded profile card validated',
                metadata = '{
              "contentCardCount": 2,
              "contentCards": [
                {
                  "title": "Unsafe legacy invention",
                  "kind": "llm_content_card",
                  "pageStart": 1,
                  "pageEnd": 1,
                  "signals": ["invented"]
                },
                {
                  "title": "Grounded profile card",
                  "kind": "llm_content_card",
                  "pageStart": 1,
                  "pageEnd": 1,
                  "signals": ["validated"],
                  "evidence": {
                    "facts": [
                      {
                        "sourceText": "Grounded profile card source paragraph",
                        "pageStart": 1,
                        "pageEnd": 1
                      }
                    ]
                  }
                }
              ]
            }'::jsonb
            WHERE tenant_id=@tenant
              AND doc_id=@docId;

            INSERT INTO document_profile_content_cards(
              content_card_id, tenant_id, document_profile_id, revision_id, doc_id, profile_version,
              card_index, title, normalized_title, page_start, page_end, kind, signals,
              search_text, token_count, checksum, metadata
            )
            VALUES
            (
              @unsafeCardId, @tenant, @profileId, @revision, @docId, 'llm_backoffice_v1',
              0, 'Unsafe legacy invention', 'unsafe legacy invention', 1, 1, 'llm_content_card',
              ARRAY['invented']::text[], 'Unsafe legacy invention invented', 4, decode(repeat('61', 32), 'hex'),
              '{"generatedBy":"legacy"}'::jsonb
            ),
            (
              @safeCardId, @tenant, @profileId, @revision, @docId, 'llm_backoffice_v1',
              1, 'Grounded profile card', 'grounded profile card', 1, 1, 'llm_content_card',
              ARRAY['validated']::text[], 'Grounded profile card validated', 4, decode(repeat('62', 32), 'hex'),
              '{
                "generatedBy":"legacy",
                "evidence":{
                  "facts":[
                    {
                      "sourceText":"Grounded profile card source paragraph",
                      "pageStart":1,
                      "pageEnd":1
                    }
                  ]
                }
              }'::jsonb
            );
            """,
            new
            {
                tenant = tenantId,
                docId,
                profileId,
                revision = revisionId,
                unsafeCardId = Guid.Parse("bbbbbbbb-4444-4444-4444-787878787878"),
                safeCardId = Guid.Parse("cccccccc-5555-5555-5555-787878787878")
            });

        await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
            conn,
            null,
            tenantId,
            revisionId,
            CancellationToken.None);

        var projection = await conn.QuerySingleAsync<(string search_text, string metadata_json)>(
            """
            SELECT search_text, metadata_json::text
            FROM document_profile_search_entries
            WHERE tenant_id=@tenant
              AND revision_id=@revision;
            """,
            new { tenant = tenantId, revision = revisionId });

        Assert.DoesNotContain("Unsafe legacy invention", projection.search_text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsafe legacy invention", projection.metadata_json, StringComparison.Ordinal);
        Assert.Contains("Grounded profile card", projection.search_text, StringComparison.Ordinal);
        Assert.Contains("Grounded profile card", projection.metadata_json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_uses_content_cards_beyond_legacy_projection_limit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-818181818181");
        var docId = Guid.Parse("99999999-2222-2222-2222-818181818181");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-818181818181");
        const string docPath = "Generic/ManyContentCards.pdf";
        const string text = "Ordinary operational page text without the late card phrase.";
        const string lateCardNeedle = "late orbit card needle";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: ManyContentCards.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });
            var profileId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT document_profile_id FROM document_profiles WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                "DELETE FROM document_profile_content_cards WHERE tenant_id=@tenant AND document_profile_id=@profileId;",
                new { tenant = tenantId, profileId });

            for (var i = 0; i < 95; i++)
            {
                var title = i == 94 ? "Late orbit card" : $"Generic card {i:00}";
                var searchText = i == 94
                    ? $"{lateCardNeedle} actionable late content card"
                    : $"generic filler card {i:00}";
                await conn.ExecuteAsync(
                    """
                    INSERT INTO document_profile_content_cards(
                      content_card_id, tenant_id, document_profile_id, revision_id, doc_id, profile_version,
                      card_index, title, normalized_title, page_start, page_end, kind, signals,
                      search_text, token_count, checksum, metadata
                    )
                    VALUES(
                      @cardId, @tenant, @profileId, @revision, @docId, 'deterministic_v1',
                      @cardIndex, @title, @normalizedTitle, 1, 1, 'deterministic_content_card',
                      ARRAY[@signal]::text[], @searchText, 6, decode(repeat('51', 32), 'hex'),
                      jsonb_build_object(
                        'evidence',
                        jsonb_build_object(
                          'facts',
                          jsonb_build_array(jsonb_build_object('sourceText', @searchText, 'pageStart', 1, 'pageEnd', 1))))
                    );
                    """,
                    new
                    {
                        cardId = Guid.NewGuid(),
                        tenant = tenantId,
                        profileId,
                        revision = revisionId,
                        docId,
                        cardIndex = i,
                        title,
                        normalizedTitle = ExactMatchEntryExtractor.NormalizeForLookup(title),
                        signal = i == 94 ? lateCardNeedle : $"generic filler {i:00}",
                        searchText
                    });
            }

            await DocumentFoundationRepo.RefreshDocumentProfileSearchEntryAsync(
                conn,
                null,
                tenantId,
                revisionId,
                CancellationToken.None);
        }

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            lateCardNeedle,
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docPath, match.DocPath);
        Assert.NotNull(match.MatchedContentCards);
        Assert.Contains(match.MatchedContentCards!, card => card.Title == "Late orbit card");

        var orderedContext = BuildRagHttpContext(tenantId);
        var orderedResult = await InvokeDocumentsContentCardsAsync(
            orderedContext,
            ds,
            categoryPath: "Generic",
            q: null,
            inventoryMode: "ordered",
            limit: 1,
            offset: 0);
        await orderedResult.ExecuteAsync(orderedContext);
        using var orderedPayload = JsonDocument.Parse(ReadResponseBody(orderedContext));
        var orderedCard = Assert.Single(
            orderedPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.InRange(orderedCard.GetProperty("cardIndex").GetInt32(), 0, 1);

        var representativeContext = BuildRagHttpContext(tenantId);
        var representativeResult = await InvokeDocumentsContentCardsAsync(
            representativeContext,
            ds,
            categoryPath: "Generic",
            q: null,
            inventoryMode: "representative",
            limit: 1,
            offset: 0);
        await representativeResult.ExecuteAsync(representativeContext);
        using var representativePayload = JsonDocument.Parse(
            ReadResponseBody(representativeContext));
        Assert.Equal(
            "representative",
            representativePayload.RootElement
                .GetProperty("inventoryMode")
                .GetString());
        var representativeCard = Assert.Single(
            representativePayload.RootElement
                .GetProperty("items")
                .EnumerateArray());
        Assert.InRange(
            representativeCard.GetProperty("cardIndex").GetInt32(),
            40,
            60);
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_repairs_missing_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-919191919191");
        var docId = Guid.Parse("99999999-2222-2222-2222-919191919191");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-919191919191");
        const string docPath = "Generic/MissingProfileProjection.pdf";
        const string text = "The maintenance dossier explains zephyr pump alignment and audit cadence.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Maintenance dossier",
            text,
            $"Document: MissingProfileProjection.pdf\nHeading Path: Maintenance dossier\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "DELETE FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });

            var beforeCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Equal(0, beforeCount);
        }

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "zephyr pump alignment",
            category: null,
            docId: null,
            docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var afterCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Equal(1, afterCount);
        }
    }

    [Fact]
    public async Task SearchDocumentOverviewProfileMatchesAsync_repairs_missing_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-929292929292");
        var docId = Guid.Parse("99999999-2222-2222-2222-929292929292");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-929292929292");
        const string docPath = "Generic/MissingOverviewProjection.pdf";
        const string text = "The overview dossier explains archive workflow ownership and review cadence.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Overview dossier",
            text,
            $"Document: MissingOverviewProjection.pdf\nHeading Path: Overview dossier\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "DELETE FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
        }

        var matches = await RagEndpoints.SearchDocumentOverviewProfileMatchesAsync(
            ds,
            tenantId,
            "give me an overview",
            category: null,
            docId: null,
            docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var afterCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Equal(1, afterCount);
        }
    }

    [Fact]
    public async Task SearchDocumentProfileMatchesAsync_refreshes_stale_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("88888888-1111-1111-1111-939393939393");
        var docId = Guid.Parse("99999999-2222-2222-2222-939393939393");
        var jobId = Guid.Parse("aaaaaaaa-3333-3333-3333-939393939393");
        const string docPath = "Generic/StaleProfileProjection.pdf";
        const string text = "Generic operational baseline text.";
        const string freshNeedle = "cobalt calibration ownership";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: StaleProfileProjection.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                UPDATE document_profiles
                SET summary_text=@freshNeedle,
                    search_text=@freshNeedle,
                    keywords=ARRAY[@freshNeedle]::text[],
                    updated_at=now()
                WHERE tenant_id=@tenant AND doc_id=@docId;

                UPDATE document_profile_search_entries
                SET summary_text='old projection text',
                    search_text='old projection text',
                    keywords=ARRAY[]::text[],
                    updated_at=now() - interval '1 hour'
                WHERE tenant_id=@tenant AND doc_id=@docId;
                """,
                new { tenant = tenantId, docId, freshNeedle });
        }

        var matches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            freshNeedle,
            category: null,
            docId: null,
            docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains(freshNeedle, match.EmbedText, StringComparison.OrdinalIgnoreCase);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var refreshedSearchText = await conn.ExecuteScalarAsync<string>(
                "SELECT search_text FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Contains(freshNeedle, refreshedSearchText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CompleteUpsertAsync_does_not_publish_when_version_is_superseded()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
        const string docPath = "Programming/Test.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: 99,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages: [],
            sections: [],
            units: [],
            retrievalChunks: [],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.False(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
    }

    [Fact]
    public async Task CompleteDeleteAsync_publishes_processing_run_without_revision()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dddddddd-1111-1111-1111-111111111111");
        var docId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("ffffffff-3333-3333-3333-333333333333");
        const string docPath = "General/DeleteMe.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 3, indexedVersion: 2, action: "delete");

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteDeleteAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            version: 3,
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var processingRun = await conn.QuerySingleAsync<(string action, string status, int indexed_version_before, int indexed_version_after, Guid? revision_id)>(
            "SELECT action, status, indexed_version_before, indexed_version_after, revision_id FROM document_processing_runs LIMIT 1;");
        var docStatus = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version)>(
            "SELECT status, ingestion_version, indexed_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc;",
            new { tenant = tenantId, doc = docId });

        Assert.Equal(0, revisionCount);
        Assert.Equal("delete", processingRun.action);
        Assert.Equal("done", processingRun.status);
        Assert.Equal(2, processingRun.indexed_version_before);
        Assert.Equal(0, processingRun.indexed_version_after);
        Assert.Null(processingRun.revision_id);
        Assert.Equal("deleted", docStatus.status);
        Assert.Equal(0, docStatus.ingestion_version);
        Assert.Equal(0, docStatus.indexed_version);
    }

    [Fact]
    public async Task MarkFailedAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("12121212-1111-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-3333-3333-3333-333333333333");
        const string docPath = "General/Failure.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("failed", jobStatus);
    }

    [Fact]
    public async Task DeferTransientAsync_requeues_running_job_without_publishing_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("12121212-aaaa-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-bbbb-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-cccc-3333-3333-333333333333");
        const string docPath = "General/Deferred.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.DeferTransientAsync(ds, jobId, "OCR bulkhead: wait timeout after 1800s (max=1).", TimeSpan.FromSeconds(90), CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var job = await conn.QuerySingleAsync<(string status, string last_error, string? locked_by, DateTimeOffset? locked_at, DateTimeOffset? started_at, DateTimeOffset? finished_at, DateTimeOffset available_at)>(
            "SELECT status, last_error, locked_by, locked_at, started_at, finished_at, available_at FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("queued", job.status);
        Assert.Equal("OCR bulkhead: wait timeout after 1800s (max=1).", job.last_error);
        Assert.Null(job.locked_by);
        Assert.Null(job.locked_at);
        Assert.Null(job.started_at);
        Assert.Null(job.finished_at);
        Assert.True(job.available_at > DateTimeOffset.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task MarkFailedAsync_when_running_upsert_payload_version_is_superseded_queues_current_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("12121212-4444-1111-1111-111111111111");
        var docId = Guid.Parse("34343434-5555-2222-2222-222222222222");
        var jobId = Guid.Parse("56565656-6666-3333-3333-333333333333");
        const string docPath = "General/SupersededFailure.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE ingestion_jobs
                SET payload=CAST(@payload AS jsonb)
                WHERE tenant_id=@tenant AND job_id=@jobId;
                """,
                new
                {
                    tenant = tenantId,
                    jobId,
                    payload = IngestionJobPayloadJson.Serialize(
                        docId,
                        version: 1,
                        source: "test",
                        indexedVersionBefore: 1)
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None);

        await using var verifyConn = new NpgsqlConnection(db.ConnectionString);
        await verifyConn.OpenAsync();
        var oldJob = await verifyConn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId });
        var followUp = await verifyConn.QuerySingleAsync<(string status, string? version, string? source)>(
            """
            SELECT status, payload #>> '{version}' AS version, payload #>> '{source}' AS source
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldJob.status);
        Assert.Equal("superseded_failed_before_commit", oldJob.last_error);
        Assert.Equal("queued", followUp.status);
        Assert.Equal("2", followUp.version);
        Assert.Equal("superseded", followUp.source);
    }

    [Fact]
    public async Task MarkFailedAsync_when_superseded_current_version_is_already_indexed_does_not_queue_redundant_follow_up()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("13131313-4444-1111-1111-111111111111");
        var docId = Guid.Parse("35353535-5555-2222-2222-222222222222");
        var jobId = Guid.Parse("57575757-6666-3333-3333-333333333333");
        const string docPath = "General/SupersededAlreadyIndexed.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 2);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET status='indexed'
                WHERE tenant_id=@tenant AND doc_path=@docPath;

                UPDATE ingestion_jobs
                SET payload=CAST(@payload AS jsonb)
                WHERE tenant_id=@tenant AND job_id=@jobId;
                """,
                new
                {
                    tenant = tenantId,
                    docPath,
                    jobId,
                    payload = IngestionJobPayloadJson.Serialize(
                        docId,
                        version: 1,
                        source: "test",
                        indexedVersionBefore: 1)
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkFailedAsync(ds, jobId, "boom", CancellationToken.None);

        await using var verifyConn = new NpgsqlConnection(db.ConnectionString);
        await verifyConn.OpenAsync();
        var oldJob = await verifyConn.QuerySingleAsync<(string status, string last_error)>(
            "SELECT status, last_error FROM ingestion_jobs WHERE job_id=@jobId;",
            new { jobId });
        var queuedFollowUps = await verifyConn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM ingestion_jobs
            WHERE tenant_id=@tenant AND doc_path=@docPath AND action='upsert' AND status='queued';
            """,
            new { tenant = tenantId, docPath });

        Assert.Equal("canceled", oldJob.status);
        Assert.Equal("superseded_failed_before_commit", oldJob.last_error);
        Assert.Equal(0, queuedFollowUps);
    }

    [Fact]
    public async Task MarkCanceledAsync_does_not_publish_document_foundation()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("78787878-1111-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-3333-3333-3333-333333333333");
        const string docPath = "General/Canceled.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 2);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.MarkCanceledAsync(ds, jobId, "canceled_by_admin_test", CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var revisionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revisions;");
        var artifactCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_revision_artifacts;");
        var jobStatus = await conn.ExecuteScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE job_id=@job_id;", new { job_id = jobId });

        Assert.Equal(0, revisionCount);
        Assert.Equal(0, artifactCount);
        Assert.Equal("canceled", jobStatus);
    }

    [Fact]
    public async Task StabilizeDocumentAfterCancelAsync_reverts_indexed_document_to_published_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("78787878-1212-1111-1111-111111111111");
        var docId = Guid.Parse("90909090-3434-2222-2222-222222222222");
        var jobId = Guid.Parse("abababab-5656-3333-3333-333333333333");
        const string docPath = "General/CanceledIndexed.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 3);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE documents SET status='indexed' WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.StabilizeDocumentAfterCancelAsync(ds, tenantId, docPath, CancellationToken.None);

        var document = await conn.QuerySingleAsync<(string status, int ingestion_version, int indexed_version, bool auto_ingest_paused)>(
            "SELECT status, ingestion_version, indexed_version, auto_ingest_paused FROM documents WHERE tenant_id=@tenant AND doc_path=@docPath;",
            new { tenant = tenantId, docPath });

        Assert.Equal("indexed", document.status);
        Assert.Equal(3, document.ingestion_version);
        Assert.Equal(3, document.indexed_version);
        Assert.False(document.auto_ingest_paused);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_returns_active_exact_match_hits_before_dense_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdcdcdcd-1111-1111-1111-111111111111");
        var docId = Guid.Parse("efefefef-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("ababcdcd-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "EN 15281 applies to the documented safety procedure and its operating conditions.",
                "EN 15281 applies to the documented safety procedure and its operating conditions.".Length,
                CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."),
                [2],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [3], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref",
                OffsetStart: 0,
                OffsetEnd: "EN 15281".Length,
                ExtractionTextStatus: units[0].ExtractionTextStatus,
                ExtractionTextSparse: units[0].ExtractionTextSparse,
                ExtractionOcrCandidate: units[0].ExtractionOcrCandidate,
                ExtractionQualitySignals: units[0].ExtractionQualitySignals)
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\nExcerpt:\nEN 15281", 34, 4, [5])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 8, 7],
            size: 42,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None);

        Assert.True(committed);

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit la norme EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.True(match.Score >= 1.0);
        Assert.Equal("EN 15281", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.Equal("Introduction", match.SectionTitle);
        Assert.Equal(0, match.OffsetStart);
        Assert.Equal("EN 15281".Length, match.OffsetEnd);
        Assert.Equal(1, match.IngestionVersion);
        Assert.Equal("low_text", match.ExtractionTextStatus);
        Assert.True(match.ExtractionTextSparse);
        Assert.True(match.ExtractionOcrCandidate);
        Assert.Contains("sparse_text_on_page", match.ExtractionQualitySignals!);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_matches_quoted_heading_when_extraction_glues_following_text()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aa55aa55-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bb66bb66-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cc77cc77-3333-3333-3333-333333333333");
        const string docPath = "Ops/Inspection.pdf";
        const string gluedHeading = "Safety valve inspectionThe inspection schedule starts here.";
        const string body = "Inspect the valve body, record the set pressure, verify the seal, confirm the technician name, capture the inspection date, and keep the signed checklist for audit.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(4, $"{gluedHeading}\n{body}", 24, gluedHeading.Length + body.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inspection", Level: 1, PageStart: 4, PageEnd: 4, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 4, 4, gluedHeading, gluedHeading.Length, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 4, 4, body, body.Length, 17, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 4, 4, gluedHeading, 7, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 4, 4, body, 17, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 4, 4, gluedHeading, ExactMatchEntryExtractor.NormalizeForLookup(gluedHeading), gluedHeading.Length, 7, [6], "verbatim_excerpt"),
            new ExtractedExactMatchEntry(1, 0, 0, 4, 4, gluedHeading, ExactMatchEntryExtractor.NormalizeForLookup(gluedHeading), gluedHeading.Length, 7, [7], "verbatim_excerpt")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 4, 4, $"Document: Inspection.pdf\nExcerpt:\n{gluedHeading}\n{body}", 180, 25, [8])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            64,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None));

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me a clear sheet for \"Safety valve inspection\".",
            "ops",
            null,
            null,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(4, match.PageStart);
        Assert.Contains("Safety valve inspectionThe inspection schedule starts here.", match.Text);
        Assert.Contains("Inspect the valve body", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.InRange(match.Score, 0.96, 0.969);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_matches_short_heading_inside_glued_extraction_excerpt()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("da55da55-1111-1111-1111-111111111111");
        var docId = Guid.Parse("eb66eb66-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("fc77fc77-3333-3333-3333-333333333333");
        const string docPath = "Ops/Maintenance.pdf";
        const string gluedExcerpt = "Allow 12 hours before start.Mode A JD1.";
        const string body = "Inspect the valve body, record the set pressure, verify the seal, confirm the technician name, capture the inspection date, and keep the signed checklist for audit.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(6, $"{gluedExcerpt}\n{body}", 24, gluedExcerpt.Length + body.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Maintenance", Level: 1, PageStart: 6, PageEnd: 6, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 6, 6, gluedExcerpt, gluedExcerpt.Length, 8, [2]),
            new ExtractedDocumentUnit(1, 0, 6, 6, body, body.Length, 17, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 6, 6, gluedExcerpt, 8, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 6, 6, body, 17, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 6, 6, gluedExcerpt, ExactMatchEntryExtractor.NormalizeForLookup(gluedExcerpt), gluedExcerpt.Length, 8, [6], "verbatim_excerpt")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 6, 6, $"Document: Maintenance.pdf\nExcerpt:\n{gluedExcerpt}\n{body}", 190, 25, [7])
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [7, 7, 7],
            64,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            CancellationToken.None));

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me Mode A JD.",
            "ops",
            null,
            null,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(6, match.PageStart);
        Assert.Contains("Mode A JD1", match.Text);
        Assert.Contains("Inspect the valve body", match.Text);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_falls_back_to_document_metadata_for_reference_visible_in_filename()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("41414141-1111-1111-1111-111111111111");
        var docId = Guid.Parse("51515151-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("61616161-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Guidance on inerting and safety controls.", 5, 41, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Overview", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Guidance on inerting and safety controls.", 41, 6, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Guidance on inerting and safety controls.", 6, [3], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [4, 4, 4],
            55,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var matches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Ou trouve-t-on EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Equal(docPath, match.DocPath);
        Assert.Equal("document_metadata_ref", match.ChunkType);
        Assert.Equal("exact_match_v1", match.EmbeddingBasis);
        Assert.Contains("15281", match.Text, StringComparison.Ordinal);
        Assert.True(match.Score >= 0.95);
    }

    [Fact]
    public async Task RuntimeGovernance_batch_loaders_return_per_doc_titles_and_excerpts_without_cross_mix()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("01010101-1111-1111-1111-111111111111");
        var doc1Id = Guid.Parse("02020202-2222-2222-2222-222222222222");
        var doc2Id = Guid.Parse("03030303-3333-3333-3333-333333333333");
        var job1Id = Guid.Parse("04040404-4444-4444-4444-444444444444");
        var job2Id = Guid.Parse("05050505-5555-5555-5555-555555555555");

        const string doc1A = "Document one describes the initial inspection and records the measured operating pressure.";
        const string doc1B = "Document one lists the safety checks required before opening the isolation valve.";
        const string doc2A = "Document two introduces the maintenance schedule and identifies the responsible service team.";
        const string doc2B = "Document two provides the annex describing replacement intervals and required maintenance records.";
        await PublishIndexedDocumentAsync(db, tenantId, doc1Id, job1Id, "ATEX/Doc1.pdf", 1,
            [new RuntimeSeedSection("Doc1 Intro", doc1A), new RuntimeSeedSection("Doc1 Safety", doc1B)]);
        await PublishIndexedDocumentAsync(db, tenantId, doc2Id, job2Id, "ATEX/Doc2.pdf", 1,
            [new RuntimeSeedSection("Doc2 Overview", doc2A), new RuntimeSeedSection("Doc2 Annex", doc2B)]);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var docVersions = new[]
        {
            (doc1Id, 1),
            (doc2Id, 1)
        };

        var sectionTitlesByDocId = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 2,
            CancellationToken.None);
        var excerptsByDocId = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
            conn,
            tenantId,
            docVersions,
            limit: 2,
            CancellationToken.None);

        Assert.Equal(new[] { "Doc1 Intro", "Doc1 Safety" }, sectionTitlesByDocId[doc1Id]);
        Assert.Equal(new[] { "Doc2 Overview", "Doc2 Annex" }, sectionTitlesByDocId[doc2Id]);
        Assert.Equal(new[] { doc1A, doc1B }, excerptsByDocId[doc1Id]);
        Assert.Equal(new[] { doc2A, doc2B }, excerptsByDocId[doc2Id]);
    }

    [Fact]
    public async Task RuntimeGovernance_excerpt_loaders_prefer_content_chunks_over_navigation_units()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("71717171-2222-4444-8888-111111111112");
        var docId = Guid.Parse("72727272-3333-5555-9999-222222222223");
        var jobId = Guid.Parse("73737373-4444-6666-aaaa-333333333334");
        const string docPath = "Knowledge/ChunkRoles.pdf";
        var navigation = "Contents Safety 3 Operation 8 Maintenance 12 Appendix 20";
        var firstContent = "The operating section explains setup validation and repeatable checks.";
        var secondContent = "The maintenance section explains inspection intervals and follow-up records.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3, 4, 5],
            size: 456,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, navigation, 5, navigation.Length, [1]),
                new ExtractedPdfPage(2, firstContent, 10, firstContent.Length, [2]),
                new ExtractedPdfPage(3, secondContent, 10, secondContent.Length, [3])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Operation", Level: 1, PageStart: 2, PageEnd: 2, StartLine: 1, EndLine: null),
                new ExtractedDocumentSection(2, "Maintenance", Level: 1, PageStart: 3, PageEnd: 3, StartLine: 1, EndLine: null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, navigation, navigation.Length, 8, [4]),
                new ExtractedDocumentUnit(1, 1, 2, 2, firstContent, firstContent.Length, 9, [5]),
                new ExtractedDocumentUnit(2, 2, 3, 3, secondContent, secondContent.Length, 9, [6])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, navigation, 8, [7], "navigation_index_v1", ContentRole: RetrievalContentClassifier.NavigationRole, NavigationScore: 0.95, ContentDensityScore: 0.10),
                new ProjectedRetrievalChunk(1, 1, 1, 2, 2, firstContent, 9, [8], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.05, ContentDensityScore: 0.95),
                new ProjectedRetrievalChunk(2, 2, 2, 3, 3, secondContent, 9, [9], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.04, ContentDensityScore: 0.94)
            ],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var excerpts = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsAsync(
            conn,
            tenantId,
            docId,
            indexedVersion: 1,
            limit: 2,
            CancellationToken.None);
        var batch = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
            conn,
            tenantId,
            [(docId, 1)],
            limit: 2,
            CancellationToken.None);

        Assert.Equal([firstContent, secondContent], excerpts);
        Assert.Equal([firstContent, secondContent], batch[docId]);
    }

    [Fact]
    public async Task LoadCapabilityBRepresentativeUnitExcerptsAsync_prefers_content_over_low_value_long_units()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("71717171-2222-4444-8888-111111111111");
        var docId = Guid.Parse("72727272-3333-5555-9999-222222222222");
        var jobId = Guid.Parse("73737373-4444-6666-aaaa-333333333333");
        const string docPath = "Knowledge/Guide.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var navigationList = "Safety overview 3 Operation details 8 Maintenance records 12 Validation checks 16 Follow-up actions 20";
        var lowValueCredits = "Writing and acknowledgements: editorial contributors, correction credits, copyright notices, ISBN details, and production thanks should not dominate the representative summary sample.";
        var firstContentExcerpt = "This guide explains how to plan weekly work, prepare reusable materials, and organize recurring tasks.";
        var lowValueReferences = "References: https://example.invalid/source-one https://example.invalid/source-two Long bibliography entry with many details that should not dominate the representative sample.";
        var secondContentExcerpt = "The practical section describes setup steps, validation checks, and follow-up actions for repeatable execution.";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3, 4],
            size: 123,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, firstContentExcerpt, 10, firstContentExcerpt.Length, [1]),
                new ExtractedPdfPage(2, secondContentExcerpt, 10, secondContentExcerpt.Length, [2])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Planning", 1, 1, 1, 1, null),
                new ExtractedDocumentSection(1, "Execution", Level: 1, PageStart: 2, PageEnd: 2, StartLine: 1, EndLine: null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, navigationList, navigationList.Length, 20, [3]),
                new ExtractedDocumentUnit(1, 0, 1, 1, lowValueCredits, lowValueCredits.Length, 18, [4]),
                new ExtractedDocumentUnit(2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [5]),
                new ExtractedDocumentUnit(3, 1, 2, 2, lowValueReferences, lowValueReferences.Length, 18, [6]),
                new ExtractedDocumentUnit(4, 1, 2, 2, secondContentExcerpt, secondContentExcerpt.Length, 12, [7])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, navigationList, 20, [8], "navigation_index_v1", ContentRole: RetrievalContentClassifier.NavigationRole, NavigationScore: 0.95, ContentDensityScore: 0.10),
                new ProjectedRetrievalChunk(1, 0, 2, 1, 1, firstContentExcerpt, 12, [9], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.05, ContentDensityScore: 0.95),
                new ProjectedRetrievalChunk(2, 1, 4, 2, 2, secondContentExcerpt, 12, [10], "unit_exact_v1", ContentRole: RetrievalContentClassifier.ContentRole, NavigationScore: 0.04, ContentDensityScore: 0.94)
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 2, 1, 1, firstContentExcerpt, firstContentExcerpt.ToLowerInvariant(), firstContentExcerpt.Length, 12, [10], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 2, 0, 1, 1, firstContentExcerpt, firstContentExcerpt.Length, 12, [11])
            ],
            CancellationToken.None);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var excerpts = await RuntimeGovernanceService.LoadCapabilityBRepresentativeUnitExcerptsAsync(
            conn,
            tenantId,
            docId,
            indexedVersion: 1,
            limit: 2,
            CancellationToken.None);

        Assert.Equal([firstContentExcerpt, secondContentExcerpt], excerpts);
    }

    [Fact]
    public async Task SearchSparseMatchesAsync_returns_contextual_chunk_hits_for_business_query()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("71717171-1111-1111-1111-111111111111");
        var docId = Guid.Parse("72727272-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("73737373-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Guidance on inerting and safety controls.", 5, 41, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Overview", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Guidance on inerting and safety controls.", 41, 6, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "Guidance on inerting and safety controls.", 6, [3], "unit_exact_v1")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(
                0,
                0,
                0,
                0,
                1,
                1,
                "Document: CEN TR 15281 2006 Guidance on inerting.pdf\nHeading Path: Overview\nExcerpt:\nGuidance on inerting and safety controls.",
                124,
                17,
                [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [5, 5, 5],
            55,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None));

        long measuredSparseMs = -1;
        var matches = await RagEndpoints.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "inerting safety controls",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            value => measuredSparseMs = value);

        var match = Assert.Single(matches);
        Assert.Equal("sparse_bm25_v1", match.EmbeddingBasis);
        Assert.Equal(docId.ToString(), match.DocId);
        Assert.Contains("inerting and safety controls", match.Text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Heading Path", match.EmbedText!, StringComparison.OrdinalIgnoreCase);
        Assert.True(match.Score > 0.0);
        Assert.True(measuredSparseMs >= 0);
    }

    [Fact]
    public async Task SearchSparseMatchesAsync_lexical_fallback_matches_accents_and_ligatures()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("74747474-1111-1111-1111-111111111111");
        var docId = Guid.Parse("75757575-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("76767676-3333-3333-3333-333333333333");
        const string docPath = "Cuisine/AccentLigature.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        const string text = "Recette: Bœuf à l'aïoli épicé. Servir avec une crème brûlée.\nSalade \nde lentilles.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, text.Length, 12, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recettes accentuées", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 12, [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, text, 12, [3], "unit_exact_v1")
        };
        var contextualTextEntries = new[]
        {
            new ProjectedContextualTextEntry(
                0,
                0,
                0,
                0,
                1,
                1,
                $"Document: AccentLigature.pdf\nHeading Path: Recettes accentuées\nExcerpt:\n{text}",
                text.Length + 70,
                18,
                [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [6, 6, 6],
            66,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries,
            CancellationToken.None));

        var matches = await RagEndpoints.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "boeuf aioli epice creme brulee",
            "cuisine",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            _ => { });

        var match = Assert.Single(matches);
        Assert.Equal("sparse_bm25_v1", match.EmbeddingBasis);
        Assert.Contains("Bœuf", match.Text!, StringComparison.Ordinal);
        Assert.Contains("crème brûlée", match.Text!, StringComparison.Ordinal);

        var splitTitleMatches = await RagEndpoints.SearchSparseMatchesAsync(
            ds,
            tenantId,
            "salade de lentilles",
            "cuisine",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None,
            _ => { });

        var splitTitleMatch = Assert.Single(splitTitleMatches);
        Assert.Contains("Salade", splitTitleMatch.Text!, StringComparison.Ordinal);
        Assert.Contains("lentilles", splitTitleMatch.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchSparseMatchesAsync_uses_profile_cards_from_non_preferred_profiles_as_recall_anchors()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("74747474-aaaa-bbbb-cccc-111111111111");
        var docId = Guid.Parse("75757575-aaaa-bbbb-cccc-222222222222");
        var jobId = Guid.Parse("76767676-aaaa-bbbb-cccc-333333333333");
        const string docPath = "Generic/ProfileCardRecall.pdf";
        const string hiddenCardPhrase = "hydraulic accumulator pressure envelope";
        const string sourceText = "The target page contains ordinary operational notes. The operator checks the equipment and records each maintenance action before the supervisor reviews the completed inspection record.";
        Assert.DoesNotContain(hiddenCardPhrase, sourceText, StringComparison.OrdinalIgnoreCase);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Maintenance inspection",
            sourceText,
            "Document: ProfileCardRecall.pdf\nHeading Path: Maintenance inspection\nExcerpt:\n" + sourceText);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

            var seedCardCount = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM document_profile_content_cards
                WHERE tenant_id=@tenant AND revision_id=@revision AND profile_version='deterministic_v1';
                """, new { tenant = tenantId, revision = revisionId });
            Assert.True(seedCardCount > 0, "The recall fixture has no deterministic profile card to update.");

            await conn.ExecuteAsync(
                """
                UPDATE document_profile_content_cards
                SET
                  title = 'Hydraulic accumulator pressure envelope',
                  normalized_title = 'hydraulic accumulator pressure envelope',
                  search_text = 'Hydraulic accumulator pressure envelope validation window',
                  page_start = 1,
                  page_end = 1,
                  signals = ARRAY['recall_anchor']::text[]
                WHERE tenant_id=@tenant
                  AND revision_id=@revision
                  AND profile_version='deterministic_v1';

                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'Short backoffice profile without content cards.',
                  ARRAY['generic']::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                  'Short backoffice profile without content cards.',
                  6, decode(repeat('42', 32), 'hex'), '{}'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId = Guid.Parse("78787878-aaaa-bbbb-cccc-444444444444")
                });
        }

        long measuredSparseMs = -1;
        var sparseMatches = await RagEndpoints.SearchSparseMatchesAsync(
            ds,
            tenantId,
            hiddenCardPhrase,
            category: null,
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None,
            value => measuredSparseMs = value);

        var sparseMatch = Assert.Single(sparseMatches);
        Assert.Equal("sparse_bm25_v1", sparseMatch.EmbeddingBasis);
        Assert.Equal(docPath, sparseMatch.DocPath);
        Assert.Contains("ordinary operational notes", sparseMatch.Text!, StringComparison.Ordinal);
        Assert.Contains("Matched profile title", sparseMatch.EmbedText!, StringComparison.Ordinal);
        Assert.True(sparseMatch.Score > 0.0);
        Assert.True(measuredSparseMs >= 0);

        var profileMatches = await RagEndpoints.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            hiddenCardPhrase,
            category: null,
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None);

        var profileMatch = Assert.Single(profileMatches);
        Assert.Equal("document_profile", RagEndpoints.ResolveRetriever(profileMatch));
        Assert.Equal(docPath, profileMatch.DocPath);
        Assert.NotNull(profileMatch.MatchedContentCards);
        Assert.Contains(
            profileMatch.MatchedContentCards!,
            card => string.Equals(card.Title, "Hydraulic accumulator pressure envelope", StringComparison.Ordinal));
    }

    [Fact]
    public void AddRankedMatches_prefers_real_chunks_over_document_profile_when_slots_are_constrained()
    {
        var selected = new List<RagMatch>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = new RagMatch(
            Score: 0.99,
            DocId: "doc-1",
            DocPath: "Generic/ProfileCardRecall.pdf",
            DocName: "ProfileCardRecall.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: Guid.NewGuid().ToString(),
            ChunkIndex: -1,
            Text: "Document profile summary",
            IngestionVersion: 1,
            HashDoc: null,
            EmbedText: "Document profile summary",
            EmbeddingBasis: "document_profile_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Document profile",
            HeadingPath: "Document profile",
            ChunkType: "document_profile",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var chunk = new RagMatch(
            Score: 0.80,
            DocId: "doc-1",
            DocPath: "Generic/ProfileCardRecall.pdf",
            DocName: "ProfileCardRecall.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: Guid.NewGuid().ToString(),
            ChunkIndex: 0,
            Text: "Concrete chunk evidence",
            IngestionVersion: 1,
            HashDoc: null,
            EmbedText: "Concrete chunk evidence",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Operations",
            HeadingPath: "Operations",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var method = typeof(RagEndpoints).GetMethod("AddRankedMatches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [selected, selectedKeys, new[] { profile, chunk }, 1, 0.0, 1, 1, 2, false, null]);

        var selectedMatch = Assert.Single(selected);
        Assert.Equal("Concrete chunk evidence", selectedMatch.Text);
        Assert.Equal("sparse_bm25", RagEndpoints.ResolveRetriever(selectedMatch));
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_supports_exact_match_anchor_when_it_resolves_to_a_retrieval_chunk()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("10101010-1111-1111-1111-111111111111");
        var docId = Guid.Parse("20202020-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("30303030-3333-3333-3333-333333333333");
        const string docPath = "ATEX/ExactAnchor.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence."), "EN 15281 applies to the documented safety procedure and its operating conditions. gamma delta documents the second step with its own evidence.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [6], "standard_ref")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [9, 9, 8],
            42,
            DateTime.UtcNow,
            2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries: [],
            CancellationToken.None));

        var exactMatches = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None);

        var exactAnchor = Assert.Single(exactMatches);
        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [exactAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 2, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("Safety", linked.SectionTitle);
        Assert.Equal("Safety", linked.HeadingPath);
        Assert.True(linked.Score < exactAnchor.Score);
        Assert.True(linked.Score >= 0.75);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_falls_back_to_same_page_chunks_for_exact_heading_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1111aaaa-2222-3333-4444-555555555555");
        var docId = Guid.Parse("2222bbbb-3333-4444-5555-666666666666");
        var jobId = Guid.Parse("3333cccc-4444-5555-6666-777777777777");
        const string docPath = "Ops/ExactHeading.pdf";
        const string title = "Safety valve inspectionThe inspection schedule starts here.";
        const string body = "Inspect the valve body, record the set pressure, and keep the signed checklist.";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var pages = new[] { new ExtractedPdfPage(4, $"{title}\n{body}", 18, title.Length + body.Length, [1]) };
        var sections = new[] { new ExtractedDocumentSection(0, "Inspection", Level: 1, PageStart: 4, PageEnd: 4, StartLine: 1, EndLine: null) };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 4, 4, title, title.Length, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 4, 4, body, body.Length, 13, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 4, 4, title, 7, [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 4, 4, body, 13, [5], "unit_exact_v1")
        };
        var exactMatchEntries = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 4, 4, title, ExactMatchEntryExtractor.NormalizeForLookup(title), title.Length, 7, [6], "verbatim_excerpt")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [3, 3, 7],
            42,
            DateTime.UtcNow,
            1,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM retrieval_chunk_links WHERE tenant_id=@tenant;", new { tenant = tenantId });
            await conn.ExecuteAsync(
                "UPDATE exact_match_entries SET unit_id=NULL, section_id=NULL WHERE tenant_id=@tenant AND text_content=@title;",
                new { tenant = tenantId, title });
        }

        var exactAnchor = Assert.Single(await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Give me a clear sheet for \"Safety valve inspection\".",
            "ops",
            docId.ToString(),
            docPath,
            5,
            CancellationToken.None));

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [exactAnchor],
            category: "ops",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var fallback = linkedMatches.FirstOrDefault(
            item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 1).ToString()
                    && item.Text == body
                    && item.EmbeddingBasis == "linked_context_v1");
        Assert.NotNull(fallback);
        Assert.Equal("linked_context_v1", fallback.ChunkType);
        Assert.Equal("content", fallback.ContentRole);
    }

    [Fact]
    public async Task SearchExactMatchesAsync_honors_category_and_document_filters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaabbbb-1111-1111-1111-111111111111");
        var docAId = Guid.Parse("ccccdddd-2222-2222-2222-222222222222");
        var docBId = Guid.Parse("eeeeffff-3333-3333-3333-333333333333");
        var jobAId = Guid.Parse("11112222-4444-4444-4444-444444444444");
        var jobBId = Guid.Parse("55556666-5555-5555-5555-555555555555");
        const string docAPath = "ATEX/CEN.pdf";
        const string docBPath = "Maintenance/Guide.pdf";

        await db.SeedRunningJobAsync(tenantId, docAId, jobAId, docAPath, ingestionVersion: 1, indexedVersion: 0);
        await db.SeedRunningJobAsync(tenantId, docBId, jobBId, docBPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var docAPages = new[] { new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [1]) };
        var docASections = new[] { new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null) };
        var docAUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [2]) };
        var docAChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [3], "unit_exact_v1") };
        var docAExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref") };
        var docAContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: CEN.pdf\nExcerpt:\nEN 15281", 34, 4, [5]) };

        var docBPages = new[] { new ExtractedPdfPage(1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, [6]) };
        var docBSections = new[] { new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null) };
        var docBUnits = new[] { new ExtractedDocumentUnit(0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", "EN 15281 applies to the documented safety procedure and its operating conditions.".Length, CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [7]) };
        var docBChunks = new[] { new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "EN 15281 applies to the documented safety procedure and its operating conditions.", CountWords("EN 15281 applies to the documented safety procedure and its operating conditions."), [8], "unit_exact_v1") };
        var docBExact = new[] { new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [9], "standard_ref") };
        var docBContext = new[] { new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, "Document: Guide.pdf\nExcerpt:\nEN 15281", 36, 4, [10]) };

        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobAId, docAPath, [1, 1, 1], 10, DateTime.UtcNow, 1, docAPages, docASections, docAUnits, docAChunks, docAExact, docAContext, CancellationToken.None));
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobBId, docBPath, [2, 2, 2], 10, DateTime.UtcNow, 1, docBPages, docBSections, docBUnits, docBChunks, docBExact, docBContext, CancellationToken.None));

        var byCategory = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            "atex",
            null,
            null,
            10,
            CancellationToken.None);

        var byDoc = await RagEndpoints.SearchExactMatchesAsync(
            ds,
            tenantId,
            "Que dit EN 15281 ?",
            null,
            docBId.ToString(),
            docBPath,
            10,
            CancellationToken.None);

        Assert.Single(byCategory);
        Assert.Equal(docAId.ToString(), byCategory[0].DocId);
        Assert.Equal("ATEX/CEN.pdf", byCategory[0].DocPath);

        Assert.Single(byDoc);
        Assert.Equal(docBId.ToString(), byDoc[0].DocId);
        Assert.Equal("Maintenance/Guide.pdf", byDoc[0].DocPath);
    }

    [Fact]
    public async Task SearchCoreAsync_honors_category_path_scope()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaabbbb-1212-3434-5656-111111111111");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("ccccdddd-1212-3434-5656-222222222222"),
            Guid.Parse("11112222-1212-3434-5656-333333333333"),
            "ATEX/Guidance/CEN.pdf",
            1,
            "Guidance",
            "shared scope token from guidance document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from guidance document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("eeeeffff-1212-3434-5656-444444444444"),
            Guid.Parse("55556666-1212-3434-5656-555555555555"),
            "ATEX/Installation/CEN.pdf",
            1,
            "Installation",
            "shared scope token from installation document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from installation document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("9999aaaa-1212-3434-5656-666666666666"),
            Guid.Parse("77778888-1212-3434-5656-777777777777"),
            "Safety/Guidance/CEN.pdf",
            1,
            "Safety guidance",
            "shared scope token from safety document",
            "Document: CEN.pdf\nExcerpt:\nshared scope token from safety document",
            [BuildExactMatchEntry(0, "shared scope token", "shared scope token", "verbatim_excerpt")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("shared scope token", CategoryPath: "ATEX/Guidance", TopK: 5));

        Assert.NotEmpty(response.Matches);
        Assert.All(response.Matches, match =>
            Assert.StartsWith("ATEX/Guidance/", match.DocPath ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(response.Matches, match =>
            string.Equals(match.DocPath, "ATEX/Installation/CEN.pdf", StringComparison.Ordinal));

        var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
            ds, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.True(built.Success);
        await using var conn = await ds.OpenConnectionAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        var refScoped = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("shared scope token", CategoryRef: $"cat_{displayOrder:000}", TopK: 5));

        Assert.NotEmpty(refScoped.Matches);
        Assert.All(refScoped.Matches, match =>
            Assert.StartsWith("ATEX/", match.DocPath ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(refScoped.Matches, match =>
            string.Equals(match.DocPath, "Safety/Guidance/CEN.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchCoreAsync_degrades_dense_retriever_when_embedding_service_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("abcddcba-1212-3434-5656-111111111111");
        const string docPath = "Ops/DenseFallback.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-1212-3434-5656-222222222222"),
            Guid.Parse("abcddcba-1212-3434-5656-333333333333"),
            docPath,
            1,
            "Hydraulics",
            "Hydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.",
            "Document: DenseFallback.pdf\nExcerpt:\nHydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto("hydraulic accumulator pressure verification", Category: "ops", TopK: 5));

        Assert.Contains(response.Matches, match => string.Equals(match.DocPath, docPath, StringComparison.Ordinal));
        Assert.Contains("dense_qdrant", response.DegradedRetrievers ?? Array.Empty<string>());
        Assert.NotNull(response.DegradedRetrieverErrors);
        Assert.True(response.DegradedRetrieverErrors!.ContainsKey("dense_qdrant"));
    }

    [Theory]
    [InlineData("hydraulic accumulator pressure verification", false, "title_lookup_combined", "final_selected", null)]
    [InlineData("recorded gauge inspection", false, "sparse_bm25", "final_selected", "fusion_rrf")]
    [InlineData("hydraulic accumulator pressure verification", true, "canonical_sparse", "canonical_selected", "canonical_fusion")]
    public async Task SearchCoreAsync_with_admin_diagnostics_captures_bounded_retrieval_phases(
        string query, bool canonical, string retrievalPhase, string finalPhase, string? fusionPhase)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("abcddcba-4545-6767-8989-111111111111");
        const string docPath = "Ops/AdminDiagnostics.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-4545-6767-8989-222222222222"),
            Guid.Parse("abcddcba-4545-6767-8989-333333333333"),
            docPath,
            1,
            "Admin diagnostics",
            "Hydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.",
            "Document: AdminDiagnostics.pdf\nExcerpt:\nHydraulic accumulator pressure verification requires a calibrated gauge and a recorded valve inspection.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto(
                query,
                Category: "ops",
                TopK: 5,
                IncludeDiagnostics: true,
                SourceBackedCanonical: canonical));

        Assert.NotEmpty(response.Matches);
        Assert.NotNull(response.Diagnostics);
        var diagnostics = response.Diagnostics!;
        Assert.Equal(query, diagnostics.Query);
        Assert.Equal(5, diagnostics.TopK);
        var phaseNames = diagnostics.Phases.Select(phase => phase.Name).ToArray();
        Assert.Contains(retrievalPhase, phaseNames);
        Assert.Contains(finalPhase, phaseNames);
        if (fusionPhase is not null)
            Assert.Contains(fusionPhase, phaseNames);
        else
        {
            // A title lookup may complete before the hybrid retrievers run.
            // Diagnostics must describe executed work, not fabricate phases.
            Assert.DoesNotContain("sparse_bm25", phaseNames);
            Assert.DoesNotContain("fusion_rrf", phaseNames);
        }
        Assert.All(diagnostics.Phases, static phase => Assert.True(phase.TopCandidates.Count <= 12));
        Assert.Equal(response.Matches.Count, diagnostics.Selection.Returned);
        Assert.Contains(diagnostics.Selection.Items, static item => string.Equals(item.DocPath, docPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_does_not_return_admin_diagnostics_from_public_endpoint()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("abcddcba-5656-7878-9090-111111111111");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("abcddcba-5656-7878-9090-222222222222"),
            Guid.Parse("abcddcba-5656-7878-9090-333333333333"),
            "Ops/PublicNoDiagnostics.pdf",
            1,
            "Public diagnostics guard",
            "Public endpoint retrieval diagnostics should stay hidden from normal search callers.",
            "Document: PublicNoDiagnostics.pdf\nExcerpt:\nPublic endpoint retrieval diagnostics should stay hidden from normal search callers.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new FailingTeiHttpClientFactory(),
            new RagSearchRequestDto(
                "Public endpoint retrieval diagnostics",
                TopK: 3,
                IncludeDiagnostics: true));

        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(response);
        Assert.Null(response!.Diagnostics);
    }

    [Fact]
    public async Task SearchAsync_returns_extraction_quality_for_rag_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaabbbb-2323-4545-6767-111111111111");
        const string sourceText = "Amber instruments require calibration before each recorded daily measurement check.";
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("ccccdddd-2323-4545-6767-222222222222"),
            Guid.Parse("11112222-2323-4545-6767-333333333333"),
            "ATEX/CEN.pdf",
            1,
            "Calibration record",
            sourceText,
            "Document: CEN.pdf\nExcerpt:\n" + sourceText,
            [BuildExactMatchEntry(0, sourceText, sourceText.ToLowerInvariant(), "verbatim_excerpt")]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto(sourceText, TopK: 3));

        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(ctx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(response);

        Assert.True(response!.Items.Count > 0, ReadResponseBody(ctx));
        var item = Assert.Single(
            response.Items,
            static match => string.Equals(match.DocPath, "ATEX/CEN.pdf", StringComparison.Ordinal));
        Assert.NotNull(item.ExtractionQuality);
        var quality = item.ExtractionQuality!;
        Assert.Equal("pdf_text", quality.ExtractionSource);
        Assert.False(quality.OcrAttempted);
        Assert.False(quality.OcrApplied);
        Assert.Equal("manual_review_low_text", quality.DocumentQualityStatus);
        Assert.Equal(0.35, quality.DocumentExtractionConfidence);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.Equal("page_ok_low_value_text", quality.PageQualityStatus);
        Assert.False(quality.PageManualReviewRecommended);
        Assert.Equal("low_text", quality.TextStatus);
        Assert.True(quality.OcrRecommended);
        Assert.Contains("ocr_recommended", quality.Signals ?? []);
    }

    [Fact]
    public async Task CompleteUpsertAsync_publishes_retrieval_chunk_links_for_adjacent_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1111aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("2222bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("3333cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/Links.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Introduction", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [5], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [7, 7, 7],
            size: 44,
            mtimeUtc: DateTime.UtcNow,
            version: 2,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var linkCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_chunk_links;");
        Assert.Equal(3, linkCount);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_accepts_sparse_bm25_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-333333333333");
        const string docPath = "ATEX/SparseLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 5, indexedVersion: 4);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins.", CountWords("recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins."), "recipe title describes the preparation steps and their required checks. ingredient list specifies the supplies needed before the preparation begins.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recipe", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "recipe title describes the preparation steps and their required checks.", "recipe title describes the preparation steps and their required checks.".Length, CountWords("recipe title describes the preparation steps and their required checks."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "ingredient list specifies the supplies needed before the preparation begins.", "ingredient list specifies the supplies needed before the preparation begins.".Length, CountWords("ingredient list specifies the supplies needed before the preparation begins."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "recipe title describes the preparation steps and their required checks.", CountWords("recipe title describes the preparation steps and their required checks."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "ingredient list specifies the supplies needed before the preparation begins.", CountWords("ingredient list specifies the supplies needed before the preparation begins."), [5], "section_window_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [7, 7, 7],
            65,
            DateTime.UtcNow,
            5,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var sparseAnchor = new RagMatch(
            Score: 0.90,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SparseLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 0).ToString(),
            ChunkIndex: 0,
            Text: "recipe title describes the preparation steps and their required checks.",
            IngestionVersion: 5,
            HashDoc: "hash",
            EmbedText: "recipe title describes the preparation steps and their required checks.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Recipe",
            HeadingPath: "Recipe",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [sparseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 5, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("section_window_v1", linked.ChunkType);
        Assert.True(linked.Score < sparseAnchor.Score);
        Assert.Equal(0.875, linked.Score, 3);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_recovers_inverse_same_section_companion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-7777-1111-2222-111111111111");
        var docId = Guid.Parse("5555bbbb-8888-2222-3333-222222222222");
        var jobId = Guid.Parse("6666cccc-9999-3333-4444-333333333333");
        const string docPath = "ATEX/InverseSameSection.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 8, indexedVersion: 7);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "The inspection sheet identifies the equipment and records its operating conditions.", CountWords("The inspection sheet identifies the equipment and records its operating conditions."), "The inspection sheet identifies the equipment and records its operating conditions.".Length, [1]),
            new ExtractedPdfPage(2, "The preparation details specify the required materials and the order of inspection steps.", CountWords("The preparation details specify the required materials and the order of inspection steps."), "The preparation details specify the required materials and the order of inspection steps.".Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inspection Sheet", Level: 1, PageStart: 1, PageEnd: 2, StartLine: 1, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "The inspection sheet identifies the equipment and records its operating conditions.", "The inspection sheet identifies the equipment and records its operating conditions.".Length, CountWords("The inspection sheet identifies the equipment and records its operating conditions."), [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "The preparation details specify the required materials and the order of inspection steps.", "The preparation details specify the required materials and the order of inspection steps.".Length, CountWords("The preparation details specify the required materials and the order of inspection steps."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "The inspection sheet identifies the equipment and records its operating conditions.", CountWords("The inspection sheet identifies the equipment and records its operating conditions."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 2, 2, "The preparation details specify the required materials and the order of inspection steps.", CountWords("The preparation details specify the required materials and the order of inspection steps."), [6], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            80,
            DateTime.UtcNow,
            8,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var anchorChunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 0);
        var companionChunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 1);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("DELETE FROM retrieval_chunk_links WHERE tenant_id=@tenantId;", new { tenantId });
            await conn.ExecuteAsync(
                """
                UPDATE retrieval_chunks
                SET metadata = COALESCE(metadata, '{}'::jsonb) - 'sameSectionChunkId' - 'nextChunkId' - 'prevChunkId'
                WHERE retrieval_chunk_id = @anchorChunkId;

                UPDATE retrieval_chunks
                SET metadata = jsonb_set(
                    COALESCE(metadata, '{}'::jsonb) - 'nextChunkId' - 'prevChunkId',
                    '{sameSectionChunkId}',
                    to_jsonb(CAST(@anchorChunkId AS text)),
                    true)
                WHERE retrieval_chunk_id = @companionChunkId;
                """,
                new { anchorChunkId, companionChunkId });
        }

        var sparseAnchor = new RagMatch(
            Score: 0.90,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "InverseSameSection.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: anchorChunkId.ToString(),
            ChunkIndex: 0,
            Text: "The inspection sheet identifies the equipment and records its operating conditions.",
            IngestionVersion: 8,
            HashDoc: "hash",
            EmbedText: "The inspection sheet identifies the equipment and records its operating conditions.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Inspection Sheet",
            HeadingPath: "Inspection Sheet",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [sparseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == companionChunkId.ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal(anchorChunkId.ToString(), linked.SameSectionChunkId);
        Assert.True(linked.Score < sparseAnchor.Score);
        Assert.Equal(0.875, linked.Score, 3);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_accepts_direct_title_token_route_anchor_for_same_page_expansion()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-7777-1111-1111-222222222222");
        var docId = Guid.Parse("5555bbbb-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("6666cccc-9999-3333-3333-444444444444");
        const string docPath = "ATEX/DirectRouteLinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 6, indexedVersion: 5);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion.", CountWords("alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion."), "alpha beta target body describes the valve inspection and its required measurements. The follow up detail explains how to record the measurements and verify completion.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta target body describes the valve inspection and its required measurements.", "alpha beta target body describes the valve inspection and its required measurements.".Length, CountWords("alpha beta target body describes the valve inspection and its required measurements."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "The follow up detail explains how to record the measurements and verify completion.", "The follow up detail explains how to record the measurements and verify completion.".Length, CountWords("The follow up detail explains how to record the measurements and verify completion."), [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta target body describes the valve inspection and its required measurements.", CountWords("alpha beta target body describes the valve inspection and its required measurements."), [4], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "The follow up detail explains how to record the measurements and verify completion.", CountWords("The follow up detail explains how to record the measurements and verify completion."), [5], "section_window_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [8, 8, 8],
            70,
            DateTime.UtcNow,
            6,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var routeAnchor = new RagMatch(
            Score: 0.91,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "DirectRouteLinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta target body describes the valve inspection and its required measurements.",
            IngestionVersion: 6,
            HashDoc: "hash",
            EmbedText: "Matched direct_title_token_route: alpha beta target\nalpha beta target body describes the valve inspection and its required measurements.",
            EmbeddingBasis: "direct_title_token_route_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Procedure",
            HeadingPath: "Procedure",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [routeAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var linked = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 1).ToString());
        Assert.Equal("linked_context_v1", linked.EmbeddingBasis);
        Assert.Equal("section_window_v1", linked.ChunkType);
        Assert.True(linked.Score < routeAnchor.Score);
    }

    [Fact]
    public async Task SearchTitleAnchorRouteMatchesAsync_direct_route_handles_accents_and_ignores_navigation_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1212aaaa-7777-1111-1111-222222222222");
        var docId = Guid.Parse("3434bbbb-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("5656cccc-9999-3333-3333-444444444444");
        const string docPath = "ATEX/AccentRoute.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 7, indexedVersion: 6);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "ÉPICE DOUCE index entry ÉPICE DOUCE operating details", 7, 54, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "ÉPICE DOUCE index entry", 10, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "ÉPICE DOUCE operating details", 11, 4, [3])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "ÉPICE DOUCE index entry",
                4,
                [4],
                "navigation_index_v1",
                ContentRole: "navigation",
                NavigationReason: "explicit_index_marker",
                NavigationScore: 0.95,
                ContentDensityScore: 0.10),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                1,
                1,
                "ÉPICE DOUCE operating details and concrete execution steps.",
                7,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.70)
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [9, 8, 7],
            80,
            DateTime.UtcNow,
            7,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            // Ingestion also generates title anchors. Isolate direct chunk
            // lookup here so disabling that route really leaves no candidate.
            await conn.ExecuteAsync(
                "DELETE FROM document_title_anchors WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant = tenantId, doc = docId });
            var publishedAnchors = await conn.QueryAsync<string>(
                "SELECT title FROM document_title_anchors WHERE tenant_id=@tenant AND doc_id=@doc;",
                new { tenant = tenantId, doc = docId });
            Assert.Empty(publishedAnchors);
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            "Donne-moi le epice douce.",
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 7, 1).ToString(), match.ChunkId);
        Assert.Equal("direct_title_token_route_v1", match.EmbeddingBasis);
        Assert.Equal("direct_title_token_route", RagEndpoints.ResolveRetriever(match));

        var anchorOnlyMatches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            "Donne-moi le epice douce.",
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 5,
            CancellationToken.None,
            allowDirectChunkRoute: false);

        Assert.Empty(anchorOnlyMatches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_content_card_full_title_lead_over_dense_partial_page_chunk(int topK)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1a1a0000-7777-1111-1111-222222222222");
        var docId = Guid.Parse("2b2b0000-8888-2222-2222-333333333333");
        var jobId = Guid.Parse("3c3c0000-9999-3333-3333-444444444444");
        const string docPath = "Operations/ContentCardAnchorRoute.pdf";
        const string title = "Alpha Beta Control Matrix";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 8, indexedVersion: 7);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Alpha dense OCR fragment. Alpha Beta Control Matrix lead. Later Alpha Beta Control Matrix reference.", 13, 93, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Alpha dense OCR fragment.", 10, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Alpha Beta Control Matrix lead with the actionable body.", 11, 9, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Later reference before Alpha Beta Control Matrix appears again.", 12, 9, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Dense OCR partial page with Alpha only and unrelated high-density operational filler.",
                11,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.99),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                1,
                1,
                "Alpha Beta Control Matrix lead with the actionable body and concrete execution detail.",
                12,
                [6],
                "unit_exact_v1",
                ContentRole: "mixed_navigation_content",
                NavigationReason: "numeric_title_catalog",
                NavigationScore: 0.50,
                ContentDensityScore: 0.50),
            new ProjectedRetrievalChunk(
                2,
                0,
                2,
                1,
                1,
                "Later reference text. Extra context first, then Alpha Beta Control Matrix appears after the lead.",
                12,
                [7],
                "unit_exact_v1",
                ContentDensityScore: 0.85)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [1, 2, 3],
            93,
            DateTime.UtcNow,
            8,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=8;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
INSERT INTO document_title_anchors(
    title_anchor_id,
    tenant_id,
    revision_id,
    doc_id,
    anchor_index,
    source_kind,
    source_ordinal,
    section_id,
    unit_id,
    retrieval_chunk_id,
    content_card_id,
    title,
    normalized_title,
    title_tokens,
    page_start,
    page_end,
    confidence,
    metadata)
VALUES(
    @anchorId,
    @tenant,
    @revisionId,
    @docId,
    100,
    'content_card',
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    @title,
    @normalizedTitle,
    @titleTokens,
    1,
    1,
    0.82,
    '{}'::jsonb);
""",
                new
                {
                    anchorId = Guid.Parse("4d4d0000-aaaa-4444-4444-555555555555"),
                    tenant = tenantId,
                    revisionId,
                    docId,
                    title,
                    normalizedTitle = TitleAnchorNormalizer.NormalizeTitle(title),
                    titleTokens = TitleAnchorNormalizer.BuildTitleTokens(title)
                });
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: topK,
            CancellationToken.None);

        Assert.InRange(matches.Count, 1, topK);
        var match = matches[0];
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 1).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.Equal("title_anchor_route", RagEndpoints.ResolveRetriever(match));
        Assert.Contains(title, match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dense OCR partial", match.Text, StringComparison.OrdinalIgnoreCase);

        var anchorOnlyMatches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: topK,
            CancellationToken.None,
            allowDirectChunkRoute: false);

        Assert.InRange(anchorOnlyMatches.Count, 1, topK);
        var anchorOnly = anchorOnlyMatches[0];
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 8, 1).ToString(), anchorOnly.ChunkId);
        Assert.Equal("title_anchor_route_v1", anchorOnly.EmbeddingBasis);
    }

    [Fact]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_anchor_page_over_adjacent_partial_token_hit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1a1a0000-7777-5555-5555-222222222222");
        var docId = Guid.Parse("2b2b0000-8888-6666-6666-333333333333");
        var jobId = Guid.Parse("3c3c0000-9999-7777-7777-444444444444");
        const string docPath = "Operations/ContentCardAdjacentRoute.pdf";
        const string title = "Alpha Beta Control Matrix";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 9, indexedVersion: 8);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Alpha Beta footer continuation unrelated to the target body.", 8, 56, [1]),
            new ExtractedPdfPage(2, "Control Matrix operating body and the actionable content.", 8, 58, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Procedure", Level: 1, PageStart: 1, PageEnd: 2, StartLine: 2, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Alpha Beta footer continuation unrelated to the target body.", 10, 8, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "Control Matrix operating body and the actionable content.", 11, 8, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Alpha Beta adjacent footer with unrelated high-density operational filler.",
                10,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.99),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                2,
                2,
                "Control Matrix operating body and the actionable content for the anchored page.",
                11,
                [6],
                "unit_exact_v1",
                ContentDensityScore: 0.40)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [2, 4, 6],
            114,
            DateTime.UtcNow,
            9,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=9;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
INSERT INTO document_title_anchors(
    title_anchor_id,
    tenant_id,
    revision_id,
    doc_id,
    anchor_index,
    source_kind,
    source_ordinal,
    section_id,
    unit_id,
    retrieval_chunk_id,
    content_card_id,
    title,
    normalized_title,
    title_tokens,
    page_start,
    page_end,
    confidence,
    metadata)
VALUES(
    @anchorId,
    @tenant,
    @revisionId,
    @docId,
    101,
    'content_card',
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    @title,
    @normalizedTitle,
    @titleTokens,
    2,
    2,
    0.82,
    '{}'::jsonb);
""",
                new
                {
                    anchorId = Guid.Parse("4d4d0000-aaaa-8888-8888-555555555555"),
                    tenant = tenantId,
                    revisionId,
                    docId,
                    title,
                    normalizedTitle = TitleAnchorNormalizer.NormalizeTitle(title),
                    titleTokens = TitleAnchorNormalizer.BuildTitleTokens(title)
                });
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var anchorOnlyMatches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None,
            allowDirectChunkRoute: false);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var anchors = await conn.QueryAsync(
                "SELECT title, source_kind, page_start, page_end, retrieval_chunk_id, title_tokens FROM document_title_anchors WHERE tenant_id=@tenant AND doc_id=@docId ORDER BY anchor_index;",
                new { tenant = tenantId, docId });
            await ExportRetrievalObservationAsync("adjacent-anchor-observation.json", new
            {
                query = title,
                anchors,
                anchorOnlyMatches,
                matches
            });
        }

        var anchorOnly = Assert.Single(anchorOnlyMatches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 9, 1).ToString(), anchorOnly.ChunkId);
        Assert.Equal("title_anchor_route_v1", anchorOnly.EmbeddingBasis);
        Assert.StartsWith("Matched title_anchor_exact_page_route:", anchorOnly.EmbedText, StringComparison.Ordinal);
        Assert.True(RagEndpoints.TitleAnchorRouteHasTargetTitleEvidence(anchorOnly));
        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 9, 1).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.StartsWith("Matched title_anchor_exact_page_route:", match.EmbedText, StringComparison.Ordinal);
        Assert.Contains("anchored page", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adjacent footer", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_same_page_section_title_hit_over_adjacent_dense_chunk(int topK)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("1a1a0000-7777-9999-9999-222222222222");
        var docId = Guid.Parse("2b2b0000-8888-aaaa-aaaa-333333333333");
        var jobId = Guid.Parse("3c3c0000-9999-bbbb-bbbb-444444444444");
        const string docPath = "Operations/SectionAnchorSamePageRoute.pdf";
        const string title = "Alpha Beta";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 10, indexedVersion: 9);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "Alpha Beta actionable body and setup.", 8, 37, [1]),
            new ExtractedPdfPage(2, "Adjacent dense continuation with beta and alpha control notes.", 8, 61, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, title, Level: 1, PageStart: 1, PageEnd: 2, StartLine: 2, EndLine: null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Alpha Beta actionable body and setup.", 10, 6, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, "Adjacent dense continuation with beta and alpha control notes.", 11, 8, [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Alpha Beta actionable body and setup.",
                10,
                [5],
                "unit_exact_v1",
                ContentDensityScore: 0.55),
            new ProjectedRetrievalChunk(
                1,
                0,
                1,
                2,
                2,
                "Adjacent dense continuation with beta and alpha control notes plus enough operational filler to look attractive.",
                12,
                [6],
                "unit_exact_v1",
                ContentDensityScore: 0.99)
        };

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [4, 5, 6],
            106,
            DateTime.UtcNow,
            10,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId AND indexed_version=10;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
INSERT INTO document_title_anchors(
    title_anchor_id,
    tenant_id,
    revision_id,
    doc_id,
    anchor_index,
    source_kind,
    source_ordinal,
    section_id,
    unit_id,
    retrieval_chunk_id,
    content_card_id,
    title,
    normalized_title,
    title_tokens,
    page_start,
    page_end,
    confidence,
    metadata)
VALUES(
    @anchorId,
    @tenant,
    @revisionId,
    @docId,
    102,
    'section',
    0,
    NULL,
    NULL,
    NULL,
    NULL,
    @title,
    @normalizedTitle,
    @titleTokens,
    1,
    1,
    0.94,
    '{}'::jsonb);
""",
                new
                {
                    anchorId = Guid.Parse("4d4d0000-aaaa-9999-9999-555555555555"),
                    tenant = tenantId,
                    revisionId,
                    docId,
                    title,
                    normalizedTitle = TitleAnchorNormalizer.NormalizeTitle(title),
                    titleTokens = TitleAnchorNormalizer.BuildTitleTokens(title)
                });
        }

        var matches = await RagEndpoints.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            $"Build a sourced operational sheet for \"{title}\" with steps and timing.",
            category: null,
            docId: null,
            docPath: null,
            topK: topK,
            CancellationToken.None,
            categoryPath: "Operations",
            allowDirectChunkRoute: false);

        Assert.InRange(matches.Count, 1, topK);
        var match = matches[0];
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 10, 0).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.Contains("actionable body", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Adjacent dense", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_checkpoint_round_trips_embedding_model_and_input_format()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("5e5e0000-1111-2222-3333-444444444444");
        var docId = Guid.Parse("6f6f0000-2222-3333-4444-555555555555");
        var jobId = Guid.Parse("70700000-3333-4444-5555-666666666666");
        const string docPath = "Operations/Checkpoint.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 2, indexedVersion: 1);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await JobRepo.UpdateProgressAsync(
            ds,
            jobId,
            "embedding",
            current: 16,
            total: 32,
            details: new
            {
                heartbeat = 3,
                elapsedSeconds = 61,
                pageCount = 12,
                languages = "eng+fra",
                forceOcr = true
            },
            CancellationToken.None);
        await JobRepo.StoreResumeCheckpointAsync(
            ds,
            jobId,
            "abc123",
            1234,
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1",
            CancellationToken.None);

        var checkpoint = await JobRepo.GetResumeCheckpointAsync(ds, jobId, CancellationToken.None);

        Assert.NotNull(checkpoint);
        Assert.Equal(16, checkpoint!.ProgressCurrent);
        Assert.Equal(32, checkpoint.ProgressTotal);
        Assert.Equal("abc123", checkpoint.SourceHash);
        Assert.Equal(1234, checkpoint.FileSize);
        Assert.Equal(32, checkpoint.ChunkTotal);
        Assert.Equal("intfloat/multilingual-e5-base", checkpoint.EmbeddingModel);
        Assert.Equal("e5_passage_v1", checkpoint.EmbeddingInputFormat);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var payloadJson = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM ingestion_jobs WHERE job_id=@job_id;",
            new { job_id = jobId });
        using var payloadDoc = JsonDocument.Parse(payloadJson ?? "{}");
        var details = payloadDoc.RootElement.GetProperty("progress").GetProperty("details");
        Assert.Equal(3, details.GetProperty("heartbeat").GetInt32());
        Assert.Equal(61, details.GetProperty("elapsedSeconds").GetInt32());
        Assert.Equal(12, details.GetProperty("pageCount").GetInt32());
        Assert.Equal("eng+fra", details.GetProperty("languages").GetString());
        Assert.True(details.GetProperty("forceOcr").GetBoolean());

        Assert.True(IngestionWorker.IsResumeCheckpointCompatible(
            checkpoint,
            "ABC123",
            32,
            "intfloat/multilingual-e5-base",
            "e5_passage_v1"));
        Assert.False(IngestionWorker.IsResumeCheckpointCompatible(
            checkpoint,
            "abc123",
            32,
            "sentence-transformers/all-MiniLM-L6-v2",
            "raw_passage_v1"));
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_returns_enriched_results_and_accepts_linked_context_anchor()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("4444aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("5555bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("6666cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/LinkedContext.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 3, indexedVersion: 2);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta records the final verification and reporting instructions.", "epsilon zeta records the final verification and reporting instructions.".Length, CountWords("epsilon zeta records the final verification and reporting instructions."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta records the final verification and reporting instructions.", CountWords("epsilon zeta records the final verification and reporting instructions."), [7], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [6, 6, 6],
            size: 55,
            mtimeUtc: DateTime.UtcNow,
            version: 3,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        var anchor = new RagMatch(
            Score: 0.81,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "LinkedContext.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta explains the first recorded operational step.",
            IngestionVersion: 3,
            HashDoc: "hash",
            EmbedText: "alpha beta explains the first recorded operational step.",
            EmbeddingBasis: "linked_context_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString());

        var linkedMatches = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [anchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var match = Assert.Single(linkedMatches, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 1).ToString());
        Assert.Equal("linked_context_v1", match.EmbeddingBasis);
        Assert.Equal("Safety", match.SectionTitle);
        Assert.Equal("Safety", match.HeadingPath);
        Assert.Equal("unit_exact_v1", match.ChunkType);
        Assert.NotNull(match.NextChunkId);
    }

    [Fact]
    public async Task SearchLinkedMatchesAsync_supports_real_second_wave_expansion_from_linked_results()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("7777aaaa-1111-1111-1111-111111111111");
        var docId = Guid.Parse("8888bbbb-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("9999cccc-3333-3333-3333-333333333333");
        const string docPath = "ATEX/SecondWave.pdf";

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 4, indexedVersion: 3);

        var pages = new[]
        {
            new ExtractedPdfPage(1, "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.", CountWords("alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions."), "alpha beta explains the first recorded operational step. gamma delta documents the second step with its own evidence. epsilon zeta records the final verification and reporting instructions.".Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta explains the first recorded operational step.", "alpha beta explains the first recorded operational step.".Length, CountWords("alpha beta explains the first recorded operational step."), [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "gamma delta documents the second step with its own evidence.", "gamma delta documents the second step with its own evidence.".Length, CountWords("gamma delta documents the second step with its own evidence."), [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "epsilon zeta records the final verification and reporting instructions.", "epsilon zeta records the final verification and reporting instructions.".Length, CountWords("epsilon zeta records the final verification and reporting instructions."), [4])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, "alpha beta explains the first recorded operational step.", CountWords("alpha beta explains the first recorded operational step."), [5], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 0, 1, 1, 1, "gamma delta documents the second step with its own evidence.", CountWords("gamma delta documents the second step with its own evidence."), [6], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 0, 2, 1, 1, "epsilon zeta records the final verification and reporting instructions.", CountWords("epsilon zeta records the final verification and reporting instructions."), [7], "unit_exact_v1")
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            [6, 6, 7],
            55,
            DateTime.UtcNow,
            4,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None));

        var denseAnchor = new RagMatch(
            Score: 0.82,
            DocId: docId.ToString(),
            DocPath: docPath,
            DocName: "SecondWave.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 0).ToString(),
            ChunkIndex: 0,
            Text: "alpha beta explains the first recorded operational step.",
            IngestionVersion: 4,
            HashDoc: "hash",
            EmbedText: "alpha beta explains the first recorded operational step.",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Safety",
            HeadingPath: "Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString(),
            SameSectionChunkId: DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString());

        var firstWave = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [denseAnchor],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var firstLinked = Assert.Single(firstWave, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 1).ToString());
        Assert.Equal("linked_context_v1", firstLinked.EmbeddingBasis);

        var secondWave = await RagEndpoints.SearchLinkedMatchesAsync(
            ds,
            tenantId,
            [firstLinked],
            category: "atex",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        Assert.Contains(secondWave, item => item.ChunkId == DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 4, 2).ToString());
    }

    [Fact]
    public async Task SearchCoreAsync_returns_answer_with_caveat_for_customer_compliance_question_on_en_15281()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");
        var jobId = Guid.Parse("cccc3333-3333-3333-3333-333333333333");
        const string docPath = "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            version: 1,
            sectionTitle: "Scope",
            chunkText: "EN 15281 guidance on inerting for the prevention of explosion.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Scope\nExcerpt:\nEN 15281 guidance on inerting for the prevention of explosion.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("EN 15281", "en 15281", "standard_ref")
            ]);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("J ai une discussion avec un client qui me demande si le projet respecte EN 15281, tu peux m en dire plus ?", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.NotEmpty(response.Matches);
        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(response.Matches, match => (match.DocPath ?? string.Empty).Contains("15281", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCoreAsync_uses_sparse_matches_for_cross_document_customer_selection_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dddd4444-4444-4444-4444-444444444444");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("eeee5555-5555-5555-5555-555555555555"),
            Guid.Parse("ffff6666-6666-6666-6666-666666666666"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            version: 1,
            sectionTitle: "Inerting",
            chunkText: "Inerting guidance covers oxygen concentration monitoring and explosion prevention.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Inerting\nExcerpt:\nInerting guidance covers oxygen concentration monitoring and explosion prevention.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("EN 15281", "en 15281", "standard_ref")
            ]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("11117777-7777-7777-7777-777777777777"),
            Guid.Parse("22228888-8888-8888-8888-888888888888"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            version: 1,
            sectionTitle: "PLC integration",
            chunkText: "IND570 manual explains PLC integration, PROFINET, EtherNet/IP and Modbus TCP.",
            contextualText: "Document: MettlerToledo_IND570.pdf\nSection: PLC integration\nExcerpt:\nIND570 manual explains PLC integration, PROFINET, EtherNet/IP and Modbus TCP.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("IND570", "ind570", "code_ref")
            ]);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Quel document faut il citer au client pour parler d inerting et d integration plc ?", TopK: 4));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);
        var docPaths = response.Matches.Select(m => m.DocPath ?? string.Empty).ToArray();

        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("15281", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains(docPaths, path => path.Contains("15281", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(docPaths, path => path.Contains("IND570", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCoreAsync_returns_ask_clarification_for_placeholder_standard_question()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("33339999-9999-9999-9999-999999999999");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("4444aaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("5555bbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            version: 1,
            sectionTitle: "Reliability",
            chunkText: "Reliability of inerting systems depends on monitoring and maintenance.",
            contextualText: "Document: CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf\nSection: Reliability\nExcerpt:\nReliability of inerting systems depends on monitoring and maintenance.");

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            NpgsqlDataSource.Create(db.ConnectionString),
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("J ai une discussion avec un client qui me demande si le projet respecte la norme xxx.", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

        Assert.Equal("ask_clarification", guidance.Behavior);
        Assert.False(string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion));
    }

    [Fact]
    public async Task SearchCoreAsync_returns_exact_ind570_document_for_manual_lookup()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("6666cccc-cccc-cccc-cccc-cccccccccccc");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("7777dddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("8888eeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            version: 1,
            sectionTitle: "Manual",
            chunkText: "The IND570 terminal manual includes installation, configuration and diagnostics.",
            contextualText: "Document: MettlerToledo_IND570.pdf\nSection: Manual\nExcerpt:\nThe IND570 terminal manual includes installation, configuration and diagnostics.",
            exactMatchEntries:
            [
                BuildExactMatchEntry("IND570", "ind570", "code_ref")
            ]);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var canonicalExactMatches = await RagEndpoints.SearchSourceBackedCanonicalExactMatchesAsync(
            ds,
            tenantId,
            "Est ce que tu as le manuel du terminal IND570 ?",
            category: null,
            docId: null,
            docPath: null,
            topK: 3,
            CancellationToken.None);

        var canonicalExact = Assert.Single(canonicalExactMatches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(
            Guid.Parse("7777dddd-dddd-dddd-dddd-dddddddddddd"),
            1,
            0).ToString(), canonicalExact.ChunkId);
        Assert.Equal("exact_match_v1", canonicalExact.EmbeddingBasis);
        Assert.Contains("installation, configuration and diagnostics", canonicalExact.Text, StringComparison.Ordinal);

        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Est ce que tu as le manuel du terminal IND570 ?", TopK: 3));

        var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);
        Assert.NotEmpty(response.Matches);
        var first = response.Matches[0];

        await ExportRetrievalObservationAsync("named-manual-observation.json", new { response, guidance });
        Assert.Equal("answer", guidance.Behavior);
        Assert.Contains("IND570", guidance.MatchedDocHints ?? Array.Empty<string>());
        Assert.Contains("IND570", $"{first.DocName} {first.DocPath}", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exact_match_v1", first.EmbeddingBasis);
    }

    [Fact]
    public async Task SearchCoreAsync_emits_retrieval_metrics_and_phase_activities()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("16161616-1616-1616-1616-161616161616");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var metricNames = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RetrievalTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => metricNames.Add(instrument.Name));
        meterListener.Start();

        var stoppedActivities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RetrievalTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var response = await RagEndpoints.SearchCoreAsync(
            BuildRagHttpContext(tenantId),
            ds,
            CreateTestRagOptions(),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Ou trouve-t-on EN 15281 ?", Category: "atex", TopK: 4));

        Assert.NotEmpty(response.Matches);
        Assert.Contains("saaia.retrieval.requests", metricNames);
        Assert.Contains("saaia.retrieval.duration", metricNames);
        Assert.Contains("saaia.retrieval.exact_match.duration", metricNames);
        Assert.Contains(stoppedActivities, activity => activity.OperationName == "rag.search");
        Assert.Contains(stoppedActivities, activity => activity.OperationName == "retrieval_exact_match");
    }

    [Fact]
    public async Task SearchCoreAsync_runtime_ready_v5_cases_match_expected_behavior_and_primary_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("9999ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = RetrievalQuestionBankFixture.LoadV5().QuestionCases
            .Where(static testCase => testCase.RuntimeReady)
            .ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

            if (!string.Equals(testCase.ExpectedBehavior, guidance.Behavior, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected behavior '{testCase.ExpectedBehavior}' but got '{guidance.Behavior}'.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedResponseShape)
                && !string.Equals(testCase.ExpectedResponseShape, guidance.ResponseShape, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected response shape '{testCase.ExpectedResponseShape}' but got '{guidance.ResponseShape ?? "<null>"}'.");
            }

            if (response.Matches.Count == 0)
            {
                failures.Add($"{testCase.Name}: expected at least one match.");
                continue;
            }

            var primary = response.Matches[0];
            var primaryDocHint = ResolveDocHint(primary);
            if (!string.Equals(testCase.ExpectedPrimaryDocHint, primaryDocHint, StringComparison.Ordinal))
            {
                failures.Add($"{testCase.Name}: expected primary doc '{testCase.ExpectedPrimaryDocHint}' but got '{primaryDocHint ?? "<none>"}'.");
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedPrimarySectionHint))
            {
                var primaryText = $"{primary.SectionTitle} {primary.HeadingPath} {primary.Text} {primary.EmbedText}";
                if (!primaryText.Contains(testCase.ExpectedPrimarySectionHint, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{testCase.Name}: expected primary section hint '{testCase.ExpectedPrimarySectionHint}' but top match was '{primary.SectionTitle ?? "<no-section>"}'.");
                }
            }

            Assert.NotEmpty(guidance.MatchedDocHints ?? Array.Empty<string>());
            Assert.Contains(testCase.ExpectedPrimaryDocHint!, guidance.MatchedDocHints!, StringComparer.Ordinal);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task SearchCoreAsync_runtime_ready_writer_cases_keep_prudent_guidance_shape()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("ababffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = RetrievalQuestionBankFixture.LoadV5().QuestionCases
            .Where(static testCase => testCase.RuntimeReady)
            .Where(static testCase =>
                string.Equals(testCase.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal)
                || string.Equals(testCase.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal))
            .ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);

            if (string.Equals(testCase.ExpectedBehavior, "answer_with_caveat", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(guidance.QualificationNote)
                    || (!guidance.QualificationNote.Contains("contexte", StringComparison.OrdinalIgnoreCase)
                        && !guidance.QualificationNote.Contains("conformite", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{testCase.Name}: expected a prudent qualification note, got '{guidance.QualificationNote ?? "<null>"}'.");
                }

                foreach (var token in testCase.ExpectedQualificationTokens ?? Array.Empty<string>())
                {
                    if (guidance.QualificationNote?.Contains(token, StringComparison.OrdinalIgnoreCase) != true)
                        failures.Add($"{testCase.Name}: qualification note should contain '{token}', got '{guidance.QualificationNote}'.");
                }
            }

            if (string.Equals(testCase.ExpectedBehavior, "ask_clarification", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(guidance.ClarifyingQuestion)
                    || !guidance.ClarifyingQuestion.Contains('?', StringComparison.Ordinal)
                    || (!guidance.ClarifyingQuestion.Contains("quelle", StringComparison.OrdinalIgnoreCase)
                        && !guidance.ClarifyingQuestion.Contains("quel", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{testCase.Name}: expected a useful clarification question, got '{guidance.ClarifyingQuestion ?? "<null>"}'.");
                }

                foreach (var token in testCase.ExpectedClarificationTokens ?? Array.Empty<string>())
                {
                    if (guidance.ClarifyingQuestion?.Contains(token, StringComparison.OrdinalIgnoreCase) != true)
                        failures.Add($"{testCase.Name}: clarifying question should contain '{token}', got '{guidance.ClarifyingQuestion}'.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task SearchCoreAsync_runtime_ready_customer_questions_keep_valid_sources_through_late_selection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("acacffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var cases = new[]
        {
            new
            {
                Query = "Quelles normes de securite instrumentee sont citees autour de l'inertage dans ce document ?",
                ExpectedSection = "Normative references"
            },
            new
            {
                Query = "Avant de dire au client que son inertage respecte EN 15281, qu'est-ce qu'il faut lui demander ?",
                ExpectedSection = "Definitions"
            }
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        foreach (var testCase in cases)
        {
            var response = await RagEndpoints.SearchCoreAsync(
                BuildRagHttpContext(tenantId),
                ds,
                CreateTestRagOptions(),
                new StubHttpClientFactory(),
                new RagSearchRequestDto(testCase.Query, TopK: 4));

            Assert.NotEmpty(response.Matches);
            Assert.Contains(response.Matches, match =>
                string.Equals(ResolveDocHint(match), "15281", StringComparison.Ordinal));
            Assert.Contains(response.Matches, match =>
                string.Equals(match.SectionTitle, testCase.ExpectedSection, StringComparison.OrdinalIgnoreCase));

            var guidance = RagEndpoints.BuildAnswerGuidance(response.Query, response.Matches);
            Assert.NotEqual("no_source_match", guidance.ResponseShape);
        }
    }

    [Fact]
    public async Task SnapshotAsync_returns_304_when_if_none_match_matches_snapshot_etag()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdcdffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var firstContext = BuildRagHttpContext(tenantId);
        var firstResult = await InvokeDocumentsSnapshotAsync(firstContext, ds);
        await firstResult.ExecuteAsync(firstContext);

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Contains("snapshotId", ReadResponseBody(firstContext), StringComparison.Ordinal);

        var secondContext = BuildRagHttpContext(tenantId);
        secondContext.Request.Headers.IfNoneMatch = etag;

        var secondResult = await InvokeDocumentsSnapshotAsync(secondContext, ds);
        await secondResult.ExecuteAsync(secondContext);

        Assert.Equal(etag, secondContext.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext));
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "INSERT INTO documents_catalog_summary(tenant_id, computed_at, total_docs) VALUES(@tenant, now(), 1);",
                new { tenant = tenantId });
        }

        var publishedContext = BuildRagHttpContext(tenantId);
        publishedContext.Request.Headers.IfNoneMatch = etag;
        var publishedResult = await InvokeDocumentsSnapshotAsync(publishedContext, ds);
        await publishedResult.ExecuteAsync(publishedContext);
        Assert.Equal(StatusCodes.Status200OK, publishedContext.Response.StatusCode);
        Assert.NotEqual(etag, publishedContext.Response.Headers.ETag.ToString());
        using var publishedPayload = JsonDocument.Parse(ReadResponseBody(publishedContext));
        Assert.Equal(1, publishedPayload.RootElement.GetProperty("totals").GetProperty("documents").GetInt32());
    }

    [Theory]
    [InlineData("catalog_categories", "value")]
    [InlineData("catalog_documents", "value")]
    [InlineData("documents_stats", "totalDocuments")]
    [InlineData("catalog_stats", "totalDocuments")]
    public async Task Catalog_cache_endpoints_return_304_when_if_none_match_matches_etag(string endpoint, string expectedBodyToken)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdceffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ingestionOptions = Options.Create(new IngestionOptions
        {
            DocumentsRoot = AppContext.BaseDirectory
        });

        var firstContext = BuildRagHttpContext(tenantId);
        var firstResult = await InvokeCatalogCacheEndpointAsync(endpoint, firstContext, ds, ingestionOptions);
        await firstResult.ExecuteAsync(firstContext);

        var etag = firstContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Contains(expectedBodyToken, ReadResponseBody(firstContext), StringComparison.Ordinal);

        var secondContext = BuildRagHttpContext(tenantId);
        secondContext.Request.Headers.IfNoneMatch = etag;

        var secondResult = await InvokeCatalogCacheEndpointAsync(endpoint, secondContext, ds, ingestionOptions);
        await secondResult.ExecuteAsync(secondContext);

        Assert.Equal(etag, secondContext.Response.Headers.ETag.ToString());
        Assert.Equal(StatusCodes.Status304NotModified, secondContext.Response.StatusCode);
        Assert.Equal(string.Empty, ReadResponseBody(secondContext));
    }

    [Fact]
    public async Task Rag_categories_merges_dynamic_indexed_categories_when_snapshot_is_partial()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdcf0000-ffff-ffff-ffff-ffffffffffff");
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("cdcf0001-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("cdcf0002-ffff-ffff-ffff-ffffffffffff"),
            "SnapshotOnly/Guide.pdf",
            1,
            "Guide",
            "Snapshot category source text.",
            "Document: Guide.pdf\nExcerpt:\nSnapshot category source text.");
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("cdcf0003-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("cdcf0004-ffff-ffff-ffff-ffffffffffff"),
            "DynamicOnly/Manual.pdf",
            1,
            "Manual",
            "Dynamic category source text.",
            "Document: Manual.pdf\nExcerpt:\nDynamic category source text.");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                DELETE FROM documents_catalog_categories WHERE tenant_id=@tenant;
                INSERT INTO documents_catalog_categories(
                  tenant_id, path, name, display_order, doc_count, direct_doc_count, subfolder_count
                )
                VALUES (@tenant, 'SnapshotOnly', 'Snapshot Only', 10, 1, 1, 0);
                """,
                new { tenant = tenantId });
        }

        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagCategoriesAsync(ctx, ds);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var categories = JsonSerializer.Deserialize<string[]>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(categories);
        Assert.Contains("SnapshotOnly", categories!);
        Assert.Contains("DynamicOnly", categories);
    }

    [Fact]
    public async Task Unified_documents_route_returns_user_safe_catalog_for_user_and_legacy_shape_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd0ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var tombstone = await conn.QuerySingleAsync<(Guid DocId, string DocPath)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
            ORDER BY doc_path ASC
            LIMIT 1;
            """,
            new { tenant = tenantId });
        await conn.ExecuteAsync(
            """
            UPDATE documents
            SET status='deleted', updated_at=now()
            WHERE tenant_id=@tenant AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = tombstone.DocId });
        var enriched = await SeedLegacyCatalogMetadataAsync(db, tenantId, tombstone.DocId);

        var userContext = BuildRagHttpContext(tenantId);
        var userResult = await InvokeUnifiedDocumentsListAsync(userContext, ds, limit: 10, offset: 0);
        await userResult.ExecuteAsync(userContext);
        var userBody = ReadResponseBody(userContext);

        Assert.Equal(StatusCodes.Status200OK, userContext.Response.StatusCode);
        Assert.Contains("CategoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("SourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("DocLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("ProfileLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("CategoryRef", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSize", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            var item = Assert.Single(
                userJson.RootElement.GetProperty("items").EnumerateArray(),
                entry => string.Equals(entry.GetProperty("DocPath").GetString(), enriched.DocPath, StringComparison.Ordinal));
            Assert.Equal(enriched.SourceHash, item.GetProperty("SourceHash").GetString());
            Assert.Equal("de", item.GetProperty("DocLanguage").GetString());
            Assert.Equal("de", item.GetProperty("ProfileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, item.GetProperty("CategoryRef").GetString());
        }

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsListAsync(adminContext, ds, limit: 10, offset: 0);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("FileSize", adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain(tombstone.DocPath, adminBody, StringComparison.Ordinal);

        var deletedContext = BuildAdminDocumentsHttpContext(tenantId);
        var deletedResult = await InvokeUnifiedDocumentsListAsync(deletedContext, ds, limit: 10, offset: 0, status: "deleted");
        await deletedResult.ExecuteAsync(deletedContext);
        var deletedBody = ReadResponseBody(deletedContext);

        Assert.Equal(StatusCodes.Status200OK, deletedContext.Response.StatusCode);
        Assert.Contains(tombstone.DocPath, deletedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unified_document_detail_route_returns_user_safe_detail_for_user_and_admin_detail_for_admin()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd1ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);
        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var enriched = await SeedLegacyCatalogMetadataAsync(db, tenantId);

        var userContext = BuildRagHttpContext(tenantId);
        var userResult = await InvokeUnifiedDocumentsGetAsync(userContext, ds, enriched.DocId);
        await userResult.ExecuteAsync(userContext);
        var userBody = ReadResponseBody(userContext);

        Assert.Equal(StatusCodes.Status200OK, userContext.Response.StatusCode);
        Assert.Contains("CategoryPath", userBody, StringComparison.Ordinal);
        Assert.Contains("SourceHash", userBody, StringComparison.Ordinal);
        Assert.Contains("DocLanguage", userBody, StringComparison.Ordinal);
        Assert.Contains("ProfileLanguage", userBody, StringComparison.Ordinal);
        using (var userJson = JsonDocument.Parse(userBody))
        {
            Assert.Equal(enriched.SourceHash, userJson.RootElement.GetProperty("SourceHash").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("DocLanguage").GetString());
            Assert.Equal("de", userJson.RootElement.GetProperty("ProfileLanguage").GetString());
            Assert.Equal(enriched.CategoryRef, userJson.RootElement.GetProperty("CategoryRef").GetString());
        }
        Assert.DoesNotContain("ContentHash", userBody, StringComparison.Ordinal);

        var adminContext = BuildAdminDocumentsHttpContext(tenantId);
        var adminResult = await InvokeUnifiedDocumentsGetAsync(adminContext, ds, enriched.DocId);
        await adminResult.ExecuteAsync(adminContext);
        var adminBody = ReadResponseBody(adminContext);

        Assert.Equal(StatusCodes.Status200OK, adminContext.Response.StatusCode);
        Assert.Contains("ContentHash", adminBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_category_endpoint_resolves_category_ref_and_returns_aliases()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd2ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using (var catalogDs = NpgsqlDataSource.Create(db.ConnectionString))
        {
            var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
                catalogDs, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            Assert.True(built.Success);
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        await conn.ExecuteAsync(
            """
            INSERT INTO documents_catalog_category_aliases(
              tenant_id, path, alias, alias_key, language, source, priority
            )
            VALUES (@tenant, 'ATEX', @alias, @aliasKey, 'fr', 'test', 1)
            ON CONFLICT (tenant_id, path, alias_key)
            DO UPDATE SET alias = EXCLUDED.alias, language = EXCLUDED.language, source = EXCLUDED.source, priority = EXCLUDED.priority;
            """,
            new
            {
                tenant = tenantId,
                alias = "Atmospheres explosibles",
                aliasKey = "atmospheres explosibles"
            });

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveCategoryAsync(
            ctx,
            ds,
            new ResolveCategoryRequest
            {
                CategoryRef = $"cat_{displayOrder:000}"
            });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<ResolveCategoryResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(response!.Items);
        Assert.Equal($"cat_{displayOrder:000}", item.CategoryRef);
        Assert.Equal("ATEX", item.CategoryPath);
        Assert.Equal("ATEX", item.DisplayName);
        Assert.Equal(displayOrder, item.Ordinal);
        Assert.True(item.TotalDocuments > 0);
        Assert.Contains("Atmospheres explosibles", item.Aliases);
    }

    [Fact]
    public async Task Resolve_source_endpoint_matches_filename_reference_to_indexed_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd3ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, int page_count, string category, string category_path, string source_hash)>(
            """
            SELECT
              doc_id,
              doc_path,
              doc_name,
              page_count,
              category,
              CASE WHEN doc_path LIKE '%/%' THEN regexp_replace(doc_path, '/[^/]+$', '') ELSE '' END AS category_path,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });
        var enrichedCard = await conn.QuerySingleAsync<(string content_card_id, string title)>(
            """
            WITH selected AS (
              SELECT content_card_id
              FROM document_profile_content_cards
              WHERE tenant_id=@tenant
                AND doc_id=@docId
              ORDER BY card_index
              LIMIT 1
            )
            UPDATE document_profile_content_cards c
            SET metadata = jsonb_set(
              COALESCE(c.metadata, '{}'::jsonb),
              '{evidence}',
              @evidence::jsonb,
              true)
            FROM selected
            WHERE c.content_card_id = selected.content_card_id
            RETURNING c.content_card_id::text AS content_card_id, c.title;
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                evidence = """
                {
                  "schemaVersion": "source_resolve_test_v1",
                  "scaleBasis": { "label": "control basis", "value": 2, "unit": "units" },
                  "quantityFacts": [
                    { "label": "Control points", "value": 4, "unit": "checks" }
                  ],
                  "confidence": 0.88
                }
                """
            });
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['source resolve pressure envelope', 'source resolve audit']::text[],
              entities=ARRAY['Mettler Toledo IND570']::text[],
              topics=ARRAY['source resolve enrichment']::text[],
              hypothetical_questions=ARRAY['Which source resolve diagnostic should be opened?']::text[],
              limits=ARRAY['Use page chunks for exact wiring.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest
            {
                PdfRef = expected.doc_name
            });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<SourceResolveResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        Assert.Equal(expected.doc_name, response.RequestedRef);
        Assert.NotNull(response.Source);
        Assert.Equal(expected.doc_id, response.Source!.DocId);
        Assert.Equal(expected.doc_path, response.Source.DocPath);
        Assert.Equal(expected.doc_name, response.Source.DocName);
        Assert.Equal(1, response.Source.PageStart);
        Assert.Equal(Math.Max(expected.page_count, 1), response.Source.PageEnd);
        Assert.Equal(expected.doc_name, response.Source.Label);
        Assert.Equal(expected.category, response.Source.Category);
        Assert.Equal(expected.category_path, response.Source.CategoryPath);
        Assert.Equal(expected.source_hash, response.Source.SourceHash);
        Assert.Equal("en", response.Source.DocLanguage);
        Assert.Equal("en", response.Source.ProfileLanguage);
        Assert.NotNull(response.Source.ExtractionQuality);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality!.DocumentQualityStatus);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality.PageQualityStatus);
        Assert.False(response.Source.ExtractionQuality.OcrAttempted);
        Assert.False(response.Source.ExtractionQuality.OcrApplied);
        Assert.NotNull(response.Source.MatchedContentCards);
        Assert.NotEmpty(response.Source.MatchedContentCards!);
        var resolvedCard = response.Source.MatchedContentCards!.Single(card =>
            string.Equals(card.ContentCardId, enrichedCard.content_card_id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(enrichedCard.title, resolvedCard.Title);
        Assert.True(resolvedCard.Evidence.HasValue);
        Assert.Equal("source_resolve_test_v1", resolvedCard.Evidence!.Value.GetProperty("schemaVersion").GetString());
        Assert.Equal("control basis", resolvedCard.Evidence.Value.GetProperty("scaleBasis").GetProperty("label").GetString());
        Assert.NotNull(response.Source.SelectionHints);
        Assert.Equal("supporting_context", response.Source.SelectionHints!.EvidenceRole);
        Assert.True(response.Source.SelectionHints.SupportScore > 0);
        Assert.NotNull(response.Source.ProfileSignals);
        Assert.Equal("en", response.Source.ProfileSignals!.Language);
        Assert.Contains("source resolve pressure envelope", response.Source.ProfileSignals.Keywords!);
        Assert.Contains("Mettler Toledo IND570", response.Source.ProfileSignals.Entities!);
        Assert.Contains("source resolve enrichment", response.Source.ProfileSignals.Topics!);
        Assert.Contains("Which source resolve diagnostic should be opened?", response.Source.ProfileSignals.HypotheticalQuestions!);
        Assert.Contains("Use page chunks for exact wiring.", response.Source.ProfileSignals.Limits!);

        var docRef = $"doc_{expected.doc_id:N}";
        var refCtx = BuildRagHttpContext(tenantId);
        var refResult = await InvokeResolveSourceAsync(
            refCtx,
            ds,
            new SourceResolveRequest
            {
                Ref = docRef
            });

        await refResult.ExecuteAsync(refCtx);

        var refResponse = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(refCtx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(refResponse);
        Assert.Null(refResponse!.Error);
        Assert.Equal(docRef, refResponse.RequestedRef);
        Assert.NotNull(refResponse.Source);
        Assert.Equal(expected.doc_id, refResponse.Source!.DocId);
        Assert.Equal(expected.source_hash, refResponse.Source.SourceHash);
        Assert.Contains("source resolve audit", refResponse.Source.ProfileSignals!.Keywords!);
        Assert.Contains(refResponse.Source.MatchedContentCards!, card =>
            string.Equals(card.ContentCardId, enrichedCard.content_card_id, StringComparison.OrdinalIgnoreCase)
            && card.Evidence.HasValue);
    }

    [Fact]
    public async Task Resolve_source_endpoint_keeps_profile_identity_when_profile_lists_are_empty()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd3ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_name, string profile_version)>(
            """
            SELECT d.doc_id, d.doc_name, p.profile_version
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            JOIN document_profiles p
              ON p.tenant_id=d.tenant_id
             AND p.doc_id=d.doc_id
             AND p.revision_id=r.revision_id
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            ORDER BY p.updated_at DESC
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET language='en',
                keywords=ARRAY[]::text[],
                entities=ARRAY[]::text[],
                topics=ARRAY[]::text[],
                hypothetical_questions=ARRAY[]::text[],
                limits=ARRAY[]::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });
        await result.ExecuteAsync(ctx);

        var response = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ProfileSignals);
        Assert.Equal(expected.profile_version, response!.Source!.ProfileSignals!.ProfileVersion);
        Assert.Equal("en", response.Source.ProfileSignals.Language);
        Assert.Null(response.Source.ProfileSignals.Keywords);
        Assert.Null(response.Source.ProfileSignals.Topics);
    }

    [Fact]
    public async Task Resolve_source_endpoint_exposes_compact_ocr_diagnostics_without_raw_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, Guid revision_id)>(
            """
            SELECT d.doc_id, d.doc_path, d.doc_name, r.revision_id
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_page_index
            SET char_count = 0,
                metadata = jsonb_set(
                    jsonb_set(metadata, '{wordCount}', '0'::jsonb, true),
                    '{imageCount}',
                    '1'::jsonb,
                    true)
            WHERE tenant_id=@tenant
              AND revision_id=@revisionId
              AND page_number=1;

            UPDATE document_processing_runs
            SET payload = jsonb_build_object(
                'extractionSource', 'pdf_text',
                'ocrAttempted', true,
                'ocrApplied', false,
                'ocrLanguages', 'fra+eng',
                'ocrDurationMs', 1234,
                'nativeExtractionQuality', jsonb_build_object(
                    'textStatus', 'empty_text',
                    'ocrRecommended', true),
                'ocrDiagnostics', jsonb_build_object(
                    'mode', 'image_page',
                    'attemptedPageCount', 3,
                    'skippedPageCount', 2,
                    'pagesWithNovelText', jsonb_build_array(2),
                    'candidatePages', jsonb_build_array(1, 2, 3, 4, 5),
                    'attemptedPages', jsonb_build_array(1, 2, 3),
                    'failureReason', 'ocr_failed',
                    'appliedReason', 'image_ocr_no_novel_text',
                    'timedOut', true,
                    'imagePageDiagnostics', jsonb_build_array(
                        jsonb_build_object(
                            'pageNumber', 1,
                            'status', 'ocr_failed',
                            'reason', 'timeout',
                            'timedOut', true,
                            'stderr', 'raw-secret-should-not-leak'))),
                'extractionQuality', jsonb_build_object(
                    'textStatus', 'low_text',
                    'ocrRecommended', true,
                    'signals', jsonb_build_array('low_text_extraction', 'ocr_recommended'),
                    'pageCount', 5,
                    'textPageCount', 1,
                    'emptyPageCount', 2,
                    'sparsePageCount', 2))
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND revision_id=@revisionId
              AND action='upsert'
              AND status='done';
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                revisionId = expected.revision_id
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<SourceResolveResponse>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality?.DiagnosticSummary);
        var quality = response!.Source!.ExtractionQuality!;
        Assert.Equal("ocr_failed_or_insufficient", quality.DocumentQualityStatus);
        Assert.Equal("ocr_failed_or_insufficient", quality.PageQualityStatus);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.True(quality.OcrAttempted);
        Assert.False(quality.OcrApplied);
        Assert.True(quality.OcrRecommended);

        var diagnostics = quality.DiagnosticSummary!;
        Assert.Equal("empty_text", diagnostics.NativeTextStatus);
        Assert.True(diagnostics.NativeOcrRecommended);
        Assert.Equal("image_page", diagnostics.OcrMode);
        Assert.Equal("fra+eng", diagnostics.OcrLanguages);
        Assert.Equal(1234, diagnostics.OcrDurationMs);
        Assert.Equal("ocr_failed", diagnostics.OcrFailureReason);
        Assert.Equal("image_ocr_no_novel_text", diagnostics.OcrAppliedReason);
        Assert.True(diagnostics.OcrTimedOut);
        Assert.Equal(3, diagnostics.OcrAttemptedPageCount);
        Assert.Equal(2, diagnostics.OcrSkippedPageCount);
        Assert.Equal(1, diagnostics.OcrPagesWithNovelTextCount);
        Assert.Equal(5, diagnostics.PageCount);
        Assert.Equal(1, diagnostics.TextPageCount);
        Assert.Equal(2, diagnostics.EmptyPageCount);
        Assert.Equal(2, diagnostics.SparsePageCount);
        Assert.True(diagnostics.ImagePageCount.GetValueOrDefault() >= 1);
        Assert.True(diagnostics.PageReviewRecommendedCount.GetValueOrDefault() >= 1);

        Assert.Contains("diagnosticSummary", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("imagePageDiagnostics", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("candidatePages", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("attemptedPages", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-should-not-leak", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_source_endpoint_infers_quality_from_native_status_and_page_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_name, Guid revision_id)>(
            """
            SELECT d.doc_id, d.doc_name, r.revision_id
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id=d.tenant_id
             AND r.doc_id=d.doc_id
             AND r.indexed_version=d.indexed_version
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND d.doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            UPDATE document_page_index
            SET char_count = 0,
                metadata = jsonb_set(metadata, '{wordCount}', '0'::jsonb, true)
            WHERE tenant_id=@tenant
              AND revision_id=@revisionId;

            UPDATE document_processing_runs
            SET payload = jsonb_build_object(
                'extractionSource', 'pdf_text',
                'ocrAttempted', true,
                'ocrApplied', false,
                'ocrLanguages', 'eng',
                'nativeExtractionQuality', jsonb_build_object(
                    'textStatus', 'empty_text',
                    'ocrRecommended', true),
                'ocrDiagnostics', jsonb_build_object(
                    'mode', 'native_text',
                    'attemptedPageCount', 0,
                    'skippedPageCount', 0,
                    'failureReason', 'not_attempted'))
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND revision_id=@revisionId
              AND action='upsert'
              AND status='done';
            """,
            new
            {
                tenant = tenantId,
                docId = expected.doc_id,
                revisionId = expected.revision_id
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await result.ExecuteAsync(ctx);

        var response = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality);
        var quality = response!.Source!.ExtractionQuality!;
        Assert.Equal("empty_text", quality.TextStatus);
        Assert.Equal("manual_review_empty_text", quality.DocumentQualityStatus);
        Assert.Equal(0.15, quality.DocumentExtractionConfidence);
        Assert.True(quality.DocumentManualReviewRecommended);
        Assert.NotNull(quality.DiagnosticSummary);
        Assert.Equal("empty_text", quality.DiagnosticSummary!.NativeTextStatus);
        Assert.True(quality.DiagnosticSummary.NativeOcrRecommended);
        Assert.True(quality.DiagnosticSummary.PageCount.GetValueOrDefault() > 0);
        Assert.Equal(0, quality.DiagnosticSummary.TextPageCount.GetValueOrDefault());
    }

    [Fact]
    public async Task Get_summary_endpoint_exposes_same_enriched_source_as_sources_resolve()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd5ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name, string category, string source_hash)>(
            """
            SELECT
              doc_id,
              doc_path,
              doc_name,
              category,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant, @docId, 'medium', 'en', @sourceHash, 'Stored technical summary.', '{"kind":"test"}'::jsonb, now(), now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              doc_language=EXCLUDED.doc_language,
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              summary_meta=EXCLUDED.summary_meta,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId = expected.doc_id, sourceHash = expected.source_hash });
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['summary source resolve enrichment']::text[],
              topics=ARRAY['summary profile signal propagation']::text[],
              limits=ARRAY['Do not use document profiles as exact numeric evidence.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var resolveCtx = BuildRagHttpContext(tenantId);
        var resolveResult = await InvokeResolveSourceAsync(
            resolveCtx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });

        await resolveResult.ExecuteAsync(resolveCtx);
        var resolvePayload = ReadResponseBody(resolveCtx);
        var resolveResponse = JsonSerializer.Deserialize<SourceResolveResponse>(resolvePayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(resolveResponse?.Source);

        var summaryCtx = BuildRagHttpContext(tenantId);
        var summaryResult = await InvokeGetSummaryAsync(summaryCtx, ds, expected.doc_id, "medium");
        await summaryResult.ExecuteAsync(summaryCtx);

        Assert.Equal(StatusCodes.Status200OK, summaryCtx.Response.StatusCode);
        var summaryPayload = ReadResponseBody(summaryCtx);
        var typedSummary = JsonSerializer.Deserialize<SummaryGetResponse>(summaryPayload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(typedSummary);
        Assert.Equal(expected.doc_id, typedSummary!.DocId);
        Assert.Equal("Stored technical summary.", typedSummary.SummaryText);
        Assert.Equal(expected.source_hash, typedSummary.SourceHash);
        Assert.Equal(expected.category, typedSummary.Category);
        Assert.NotNull(typedSummary.Source);
        Assert.Equal(resolveResponse!.Source!.SourceHash, typedSummary.Source!.SourceHash);

        using var summaryDoc = JsonDocument.Parse(summaryPayload);
        var root = summaryDoc.RootElement;
        Assert.Equal("Stored technical summary.", root.GetProperty("summaryText").GetString());
        Assert.Equal(expected.source_hash, root.GetProperty("sourceHash").GetString());
        Assert.Equal("en", root.GetProperty("docLanguage").GetString());
        Assert.Equal(expected.category, root.GetProperty("category").GetString());
        Assert.True(root.GetProperty("isFresh").GetBoolean());

        var source = root.GetProperty("source");
        Assert.Equal(resolveResponse.Source!.DocId.ToString(), source.GetProperty("docId").GetString());
        Assert.Equal(resolveResponse.Source.DocPath, source.GetProperty("docPath").GetString());
        Assert.Equal(resolveResponse.Source.DocName, source.GetProperty("docName").GetString());
        Assert.Equal(resolveResponse.Source.SourceHash, source.GetProperty("sourceHash").GetString());
        Assert.Equal(resolveResponse.Source.DocLanguage, source.GetProperty("docLanguage").GetString());
        Assert.Equal(resolveResponse.Source.ProfileLanguage, source.GetProperty("profileLanguage").GetString());
        Assert.Equal(resolveResponse.Source.Category, source.GetProperty("category").GetString());
        Assert.Equal(resolveResponse.Source.CategoryPath, source.GetProperty("categoryPath").GetString());
        Assert.Equal(resolveResponse.Source.ExtractionQuality!.DocumentQualityStatus, source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal(resolveResponse.Source.MatchedContentCards![0].Title, source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(resolveResponse.Source.SelectionHints!.EvidenceRole, source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal(resolveResponse.Source.ProfileSignals!.Language, typedSummary.ProfileSignals!.Language);
        Assert.Contains("summary source resolve enrichment", typedSummary.ProfileSignals.Keywords!);
        Assert.Contains("summary profile signal propagation", typedSummary.ProfileSignals.Topics!);
        Assert.Equal(resolveResponse.Source.ProfileSignals.Language, source.GetProperty("profileSignals").GetProperty("language").GetString());

        Assert.Equal(source.GetProperty("profileLanguage").GetString(), root.GetProperty("profileLanguage").GetString());
        Assert.Equal(source.GetProperty("category").GetString(), root.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("categoryPath").GetString(), root.GetProperty("categoryPath").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), root.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(source.GetProperty("profileSignals").GetProperty("language").GetString(), root.GetProperty("profileSignals").GetProperty("language").GetString());
    }

    [Fact]
    public async Task Search_summary_endpoint_exposes_enriched_source_projection_for_items()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd5ffff-ffff-ffff-ffff-eeeeeeeeeeee");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string category, string source_hash)>(
            """
            SELECT
              doc_id,
              category,
              saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) AS source_hash
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant,
              @docId,
              'medium',
              'en',
              @sourceHash,
              'Stored technical summary with PLC integration.',
              '{"generator":"capability_b_worker_v2","strategy":"llm_document_foundation","outputLanguage":"en","fallbackUsed":false,"extractionQuality":{"requiresCaution":true,"documentQualityStatus":"ocr_applied_ok"}}'::jsonb,
              now(),
              now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              doc_language=EXCLUDED.doc_language,
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              summary_meta=EXCLUDED.summary_meta,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId = expected.doc_id, sourceHash = expected.source_hash });
        await conn.ExecuteAsync(
            """
            UPDATE document_profiles
            SET
              language='en',
              keywords=ARRAY['summary search enrichment']::text[],
              topics=ARRAY['summary search profile propagation']::text[],
              limits=ARRAY['Treat profile signals as routing hints.']::text[]
            WHERE tenant_id=@tenant
              AND doc_id=@docId;
            """,
            new { tenant = tenantId, docId = expected.doc_id });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeSearchSummariesAsync(ctx, ds, "PLC integration", limit: 10, offset: 0);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(expected.source_hash, item.GetProperty("sourceHash").GetString());
        Assert.Equal("en", item.GetProperty("docLanguage").GetString());
        Assert.Equal("en", item.GetProperty("profileLanguage").GetString());
        Assert.Equal(expected.category, item.GetProperty("category").GetString());
        Assert.Equal("Programmation/Mettler", item.GetProperty("categoryPath").GetString());
        Assert.True(item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object);
        Assert.Equal(item.GetProperty("sourceHash").GetString(), source.GetProperty("sourceHash").GetString());
        Assert.Equal(item.GetProperty("profileLanguage").GetString(), source.GetProperty("profileLanguage").GetString());
        Assert.Equal(item.GetProperty("category").GetString(), source.GetProperty("category").GetString());
        Assert.Equal(source.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString(), item.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
        Assert.Equal(source.GetProperty("matchedContentCards")[0].GetProperty("title").GetString(), item.GetProperty("matchedContentCards")[0].GetProperty("title").GetString());
        Assert.Equal(source.GetProperty("selectionHints").GetProperty("evidenceRole").GetString(), item.GetProperty("selectionHints").GetProperty("evidenceRole").GetString());
        Assert.Equal("en", item.GetProperty("profileSignals").GetProperty("language").GetString());
        Assert.Equal("summary search enrichment", item.GetProperty("profileSignals").GetProperty("keywords")[0].GetString());
        Assert.Equal(source.GetProperty("profileSignals").GetProperty("topics")[0].GetString(), item.GetProperty("profileSignals").GetProperty("topics")[0].GetString());
        var meta = item.GetProperty("meta");
        Assert.Equal("capability_b_worker_v2", meta.GetProperty("generator").GetString());
        Assert.Equal("llm_document_foundation", meta.GetProperty("strategy").GetString());
        Assert.Equal("en", meta.GetProperty("outputLanguage").GetString());
        Assert.False(meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.True(meta.GetProperty("extractionQuality").GetProperty("requiresCaution").GetBoolean());
        Assert.Equal("ocr_applied_ok", meta.GetProperty("extractionQuality").GetProperty("documentQualityStatus").GetString());
    }

    [Fact]
    public async Task Get_summary_endpoint_keeps_stale_summary_behavior_before_source_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd6ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var docId = await conn.QuerySingleAsync<Guid>(
            """
            SELECT doc_id
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries(
              tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
            )
            VALUES(
              @tenant, @docId, 'medium', 'en', 'stale-source-hash', 'Stale summary.', '{}'::jsonb, now(), now()
            )
            ON CONFLICT (tenant_id, doc_id, level)
            DO UPDATE SET
              source_hash=EXCLUDED.source_hash,
              summary_text=EXCLUDED.summary_text,
              updated_at=EXCLUDED.updated_at;
            """,
            new { tenant = tenantId, docId });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeGetSummaryAsync(ctx, ds, docId, "medium");
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        Assert.Equal("summary_stale", payload.RootElement.GetProperty("error").GetString());

        var remaining = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM document_summaries
            WHERE tenant_id=@tenant
              AND doc_id=@docId
              AND level='medium';
            """,
            new { tenant = tenantId, docId });
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task Resolve_source_endpoint_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd7ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path, string doc_name)>(
            """
            SELECT doc_id, doc_path, doc_name
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd70000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd70000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd70000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('AA', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('AA', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeResolveSourceAsync(
            ctx,
            ds,
            new SourceResolveRequest { PdfRef = expected.doc_name });
        await result.ExecuteAsync(ctx);

        var response = JsonSerializer.Deserialize<SourceResolveResponse>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response?.Source?.ExtractionQuality);
        Assert.Equal("extraction_ok", response!.Source!.ExtractionQuality!.DocumentQualityStatus);
        Assert.Equal("extraction_ok", response.Source.ExtractionQuality.PageQualityStatus);
        Assert.DoesNotContain("stale_revision_should_not_leak", response.Source.ExtractionQuality.Signals ?? []);
        Assert.False(response.Source.ExtractionQuality.OcrRecommended.GetValueOrDefault());
    }

    [Fact]
    public async Task Rag_search_uses_extraction_quality_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd8ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd80000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd80000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd80000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('BB', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('BB', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(ReadResponseBody(ctx), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item =>
        {
            Assert.NotNull(item.ExtractionQuality);
            Assert.Equal("extraction_ok", item.ExtractionQuality!.DocumentQualityStatus);
            Assert.DoesNotContain("stale_revision_should_not_leak", item.ExtractionQuality.Signals ?? []);
            Assert.False(item.ExtractionQuality.OcrRecommended.GetValueOrDefault());
        });
    }

    [Fact]
    public async Task Rag_query_legacy_endpoint_returns_enriched_match_payload()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdda0000-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagQueryAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var root = payload.RootElement;
        Assert.True(root.TryGetProperty("matches", out var matches));
        Assert.InRange(matches.GetArrayLength(), 1, 3);
        var documentItems = matches.EnumerateArray().Where(
            static match => string.Equals(match.GetProperty("docPath").GetString(), "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item =>
        {
            Assert.True(item.TryGetProperty("sourceHash", out var sourceHash));
            Assert.False(string.IsNullOrWhiteSpace(sourceHash.GetString()));
            Assert.True(item.TryGetProperty("docLanguage", out var docLanguage));
            Assert.Equal("en", docLanguage.GetString());
            Assert.True(item.TryGetProperty("extractionQuality", out var quality));
            Assert.Equal("extraction_ok", quality.GetProperty("documentQualityStatus").GetString());
            Assert.True(item.TryGetProperty("matchedContentCards", out _));
        });
        Assert.True(root.TryGetProperty("guidance", out var guidance));
        Assert.Equal("answer", guidance.GetProperty("behavior").GetString());
        Assert.True(root.TryGetProperty("metrics", out var metrics));
        Assert.Equal(matches.GetArrayLength(), metrics.GetProperty("returned").GetInt32());
    }

    [Fact]
    public async Task Document_language_falls_back_to_ingestion_run_when_profiles_are_temporarily_missing()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cddb0000-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cddb0000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cddb0000-5555-6666-7777-888888888888");
        const string docPath = "Kennisbank/Handleiding.pdf";
        const string text = "De handleiding beschrijft onderhoud en veiligheidscontroles voor de installatie. Het document noemt deze inspectie en deze procedure.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Onderhoud",
            text,
            $"Document: Handleiding.pdf\nHeading Path: Onderhoud\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            var runLanguage = await conn.ExecuteScalarAsync<string>(
                """
SELECT payload ->> 'documentLanguage'
FROM document_processing_runs
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND status='done'
LIMIT 1;
""",
                new { tenant = tenantId, docId });
            Assert.Equal("nl", runLanguage);

            await conn.ExecuteAsync(
                """
DELETE FROM document_profile_content_cards WHERE tenant_id=@tenant AND doc_id=@docId;
DELETE FROM document_profiles WHERE tenant_id=@tenant AND doc_id=@docId;
DELETE FROM document_summaries WHERE tenant_id=@tenant AND doc_id=@docId;
""",
                new { tenant = tenantId, docId });
        }

        var resolveCtx = BuildRagHttpContext(tenantId);
        var resolveResult = await InvokeResolveSourceAsync(
            resolveCtx,
            ds,
            new SourceResolveRequest { PdfRef = "Handleiding.pdf" });
        await resolveResult.ExecuteAsync(resolveCtx);

        var resolveResponse = JsonSerializer.Deserialize<SourceResolveResponse>(
            ReadResponseBody(resolveCtx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resolveResponse?.Source);
        Assert.Equal("nl", resolveResponse!.Source!.DocLanguage);
        Assert.Null(resolveResponse.Source.ProfileLanguage);

        var searchCtx = BuildRagHttpContext(tenantId);
        var searchResult = await InvokeRagSearchAsync(
            searchCtx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("onderhoud installatie", TopK: 3));
        await searchResult.ExecuteAsync(searchCtx);

        var searchResponse = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ReadResponseBody(searchCtx),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var searchItem = Assert.Single(
            searchResponse!.Items,
            static item => string.Equals(item.DocPath, docPath, StringComparison.Ordinal));
        Assert.Equal("nl", searchItem.DocLanguage);
        Assert.Null(searchItem.ProfileLanguage);

        var listCtx = BuildRagHttpContext(tenantId);
        var listResult = await InvokeUnifiedDocumentsListAsync(listCtx, ds, limit: 10, offset: 0);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var listItem = Assert.Single(
            listPayload.RootElement.GetProperty("items").EnumerateArray(),
            entry => string.Equals(entry.GetProperty("DocPath").GetString(), docPath, StringComparison.Ordinal));
        Assert.Equal("nl", listItem.GetProperty("DocLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, listItem.GetProperty("ProfileLanguage").ValueKind);
    }

    [Fact]
    public async Task Admin_extraction_quality_uses_processing_run_from_current_revision_only()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd9ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var expected = await conn.QuerySingleAsync<(Guid doc_id, string doc_path)>(
            """
            SELECT doc_id, doc_path
            FROM documents
            WHERE tenant_id=@tenant
              AND status='indexed'
              AND doc_path='Programmation/Mettler/MettlerToledo_IND570.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var staleRevisionId = Guid.Parse("cdd90000-1111-2222-3333-444444444444");
        var staleRunId = Guid.Parse("cdd90000-5555-6666-7777-888888888888");
        var staleJobId = Guid.Parse("cdd90000-9999-aaaa-bbbb-cccccccccccc");
        await conn.ExecuteAsync(
            """
            INSERT INTO document_revisions(
              revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
              ingestion_version, indexed_version, published_at, created_at
            )
            VALUES(
              @staleRevisionId, @tenant, @docId, @docPath, decode('CC', 'hex'), 123, now() - interval '2 days',
              1, 0, now() - interval '2 days', now() - interval '2 days'
            )
            ON CONFLICT DO NOTHING;

            INSERT INTO document_processing_runs(
              processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
              ingestion_version, indexed_version_before, indexed_version_after, source_hash,
              started_at, finished_at, payload
            )
            VALUES(
              @staleRunId, @tenant, @staleJobId, @docId, @docPath, @staleRevisionId, 'upsert', 'done',
              1, 0, 0, decode('CC', 'hex'),
              now() + interval '1 day', now() + interval '1 day',
              '{
                "extractionSource": "pdf_text",
                "ocrAttempted": false,
                "ocrApplied": false,
                "extractionQuality": {
                  "textStatus": "empty_text",
                  "ocrRecommended": true,
                  "signals": ["stale_revision_should_not_leak"],
                  "pageCount": 8,
                  "textPageCount": 0,
                  "emptyPageCount": 8,
                  "sparsePageCount": 0
                }
              }'::jsonb
            )
            ON CONFLICT DO NOTHING;
            """,
            new
            {
                staleRevisionId,
                tenant = tenantId,
                docId = expected.doc_id,
                docPath = expected.doc_path,
                staleRunId,
                staleJobId
            });

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityAsync(ctx, ds, "Programmation", null, 50);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var item = Assert.Single(
            payload.RootElement.GetProperty("items").EnumerateArray(),
            entry => string.Equals(entry.GetProperty("docPath").GetString(), expected.doc_path, StringComparison.Ordinal));

        Assert.Equal("extraction_ok", item.GetProperty("qualityStatus").GetString());
        Assert.False(item.GetProperty("ocrRecommended").GetBoolean());
        Assert.DoesNotContain(
            item.GetProperty("signals").EnumerateArray(),
            signal => signal.GetString() == "stale_revision_should_not_leak");
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoint_reports_llm_enrichment_pending_counters()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cddeffff-ffff-ffff-ffff-ffffffffffff");
        var readyDocId = Guid.Parse("cdde0000-1111-2222-3333-444444444444");
        var pendingDocId = Guid.Parse("cdde0000-5555-6666-7777-888888888888");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            readyDocId,
            Guid.Parse("cdde0000-aaaa-bbbb-cccc-dddddddddddd"),
            "Diagnostics/ReadyForBackoffice.pdf",
            1,
            "Diagnostics",
            "Ready document has enough stable text for extraction quality diagnostics.",
            "document_name: ReadyForBackoffice.pdf\nsection_title: Diagnostics\nexcerpt:\nReady document has enough stable text for extraction quality diagnostics.");

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            pendingDocId,
            Guid.Parse("cdde0000-eeee-ffff-aaaa-bbbbbbbbbbbb"),
            "Diagnostics/PendingBackoffice.pdf",
            1,
            "Diagnostics",
            "Pending document has enough stable text but no server-side profile or summary yet.",
            "document_name: PendingBackoffice.pdf\nsection_title: Diagnostics\nexcerpt:\nPending document has enough stable text but no server-side profile or summary yet.");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var ready = await conn.QuerySingleAsync<(Guid revision_id, string source_hash)>(
                """
                SELECT
                  r.revision_id,
                  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS source_hash
                FROM documents d
                JOIN document_revisions r
                  ON r.tenant_id=d.tenant_id
                 AND r.doc_id=d.doc_id
                 AND r.indexed_version=d.indexed_version
                WHERE d.tenant_id=@tenant
                  AND d.doc_id=@docId
                LIMIT 1;
                """,
                new { tenant = tenantId, docId = readyDocId });

            await conn.ExecuteAsync(
                """
                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1', 'en',
                  'Backoffice profile ready.', ARRAY['diagnostic']::text[], ARRAY[]::text[], ARRAY[]::text[],
                  ARRAY[]::text[], ARRAY[]::text[], 'Backoffice profile ready.', 3, decode('CDDE', 'hex'),
                  '{"contentCardEvidenceSchemaVersion":2}'::jsonb
                )
                ON CONFLICT (revision_id, profile_version) DO UPDATE
                SET metadata=excluded.metadata,
                    updated_at=now();

                INSERT INTO document_summaries(
                  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
                )
                VALUES(
                  @tenant, @docId, 'medium', 'en', @sourceHash, 'Backoffice summary ready.', '{}'::jsonb, now(), now()
                )
                ON CONFLICT (tenant_id, doc_id, level) DO UPDATE
                SET source_hash=excluded.source_hash,
                    updated_at=now();
                """,
                new
                {
                    tenant = tenantId,
                    docId = readyDocId,
                    revisionId = ready.revision_id,
                    sourceHash = ready.source_hash,
                    profileId = Guid.Parse("cdde0000-9999-8888-7777-666666666666")
                });

            await conn.ExecuteAsync(
                """
                UPDATE document_processing_runs
                SET payload = jsonb_set(
                  COALESCE(payload, '{}'::jsonb),
                  '{retrievalChunkQuality}',
                  '{
                    "totalChunkCount": 12,
                    "searchableChunkCount": 0,
                    "rejectedChunkCount": 12,
                    "manualReviewRecommended": true,
                    "rejectionReasons": {
                      "sparse_text": 12
                    }
                  }'::jsonb,
                  true
                )
                WHERE tenant_id=@tenant
                  AND doc_id=@docId
                  AND action='upsert'
                  AND status='done';
                """,
                new { tenant = tenantId, docId = pendingDocId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityAsync(ctx, ds, "Diagnostics", null, 50);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));

        var summary = payload.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("llmEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("summaryEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("profileEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("contentCardEvidencePendingDocuments").GetInt32());
        Assert.Equal(1, summary.GetProperty("documentsWithRejectedChunks").GetInt32());
        Assert.Equal(1, summary.GetProperty("documentsWithNoSearchableChunks").GetInt32());
        Assert.Equal(1, summary.GetProperty("documentsWithRetrievalReviewRecommended").GetInt32());

        var category = Assert.Single(payload.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal("Diagnostics", category.GetProperty("categoryPath").GetString());
        Assert.Equal(1, category.GetProperty("llmEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("summaryEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("profileEnrichmentPendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("contentCardEvidencePendingDocuments").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithRejectedChunks").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithNoSearchableChunks").GetInt32());
        Assert.Equal(1, category.GetProperty("documentsWithRetrievalReviewRecommended").GetInt32());

        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var readyItem = Assert.Single(
            items,
            item => string.Equals(
                item.GetProperty("docPath").GetString(),
                "Diagnostics/ReadyForBackoffice.pdf",
                StringComparison.Ordinal));
        Assert.False(readyItem.GetProperty("llmEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("summaryEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("profileEnrichmentPending").GetBoolean());
        Assert.False(readyItem.GetProperty("contentCardEvidencePending").GetBoolean());

        var pendingItem = Assert.Single(
            items,
            item => string.Equals(
                item.GetProperty("docPath").GetString(),
                "Diagnostics/PendingBackoffice.pdf",
                StringComparison.Ordinal));
        Assert.True(pendingItem.GetProperty("llmEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("summaryEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("profileEnrichmentPending").GetBoolean());
        Assert.True(pendingItem.GetProperty("contentCardEvidencePending").GetBoolean());
        var retrievalChunkQuality = pendingItem.GetProperty("retrievalChunkQuality");
        Assert.Equal(12, retrievalChunkQuality.GetProperty("totalChunkCount").GetInt32());
        Assert.Equal(0, retrievalChunkQuality.GetProperty("searchableChunkCount").GetInt32());
        Assert.Equal(12, retrievalChunkQuality.GetProperty("rejectedChunkCount").GetInt32());
        Assert.True(retrievalChunkQuality.GetProperty("manualReviewRecommended").GetBoolean());
    }

    [Fact]
    public async Task Extraction_quality_admin_endpoints_tolerate_malformed_processing_payload_values()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd8ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd80000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd80000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/MalformedPayload.pdf";
        var pageText = string.Join(' ', Enumerable.Repeat("A documented inspection records operating conditions and confirms that every valve remains accessible.", 8));

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 1],
            size: 1024,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, pageText, CountWords(pageText), pageText.Length, [1])
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Diagnostics", 1, 1, 1, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, pageText, pageText.Length, CountWords(pageText), [2])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pageText, CountWords(pageText), [3], "unit_exact_v1")
            ],
            exactMatchEntries: [],
            contextualTextEntries: [],
            CancellationToken.None);

        Assert.True(committed);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                UPDATE document_processing_runs
                SET payload = '{
                  "extractionSource": "pdf_text",
                  "ocrAttempted": "not-a-bool",
                  "ocrApplied": "also-not-a-bool",
                  "ocrDurationMs": "not-a-number",
                  "nativeExtractionQuality": {
                    "textStatus": "ok",
                    "ocrRecommended": "maybe"
                  },
                  "extractionQuality": {
                    "ocrRecommended": "maybe",
                    "pageCount": "many",
                    "textPageCount": "enough",
                    "emptyPageCount": "none",
                    "sparsePageCount": "few",
                    "totalWordCount": "lots",
                    "totalCharCount": "lots",
                    "averageWordsPerPage": "NaN",
                    "textPageRatio": "almost"
                  }
                }'::jsonb
                WHERE tenant_id=@tenant
                  AND doc_id=@docId
                  AND action='upsert'
                  AND status='done';
                """,
                new { tenant = tenantId, docId });
        }

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "Diagnostics", null, 20);
        await listResult.ExecuteAsync(listCtx);

        Assert.Equal(StatusCodes.Status200OK, listCtx.Response.StatusCode);
        using (var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx)))
        {
            var item = Assert.Single(
                listPayload.RootElement.GetProperty("items").EnumerateArray(),
                entry => string.Equals(entry.GetProperty("docPath").GetString(), docPath, StringComparison.Ordinal));
            Assert.False(item.GetProperty("ocrAttempted").GetBoolean());
            Assert.False(item.GetProperty("ocrApplied").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("ocrDurationMs").ValueKind);
            Assert.Equal(1, item.GetProperty("pageCount").GetInt32());
            Assert.Equal("extraction_ok", item.GetProperty("qualityStatus").GetString());
        }

        var pagesCtx = BuildAdminDocumentsHttpContext(tenantId);
        var pagesResult = await InvokeExtractionQualityPagesAsync(pagesCtx, ds, docId);
        await pagesResult.ExecuteAsync(pagesCtx);

        Assert.Equal(StatusCodes.Status200OK, pagesCtx.Response.StatusCode);
        using var pagesPayload = JsonDocument.Parse(ReadResponseBody(pagesCtx));
        var root = pagesPayload.RootElement;
        Assert.False(root.GetProperty("ocrAttempted").GetBoolean());
        Assert.False(root.GetProperty("ocrApplied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ocrDurationMs").ValueKind);
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
    }

    [Fact]
    public async Task Extraction_quality_pages_endpoint_reports_page_level_review_signals()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("cdd4ffff-ffff-ffff-ffff-ffffffffffff");
        var docId = Guid.Parse("cdd40000-1111-2222-3333-444444444444");
        var jobId = Guid.Parse("cdd40000-5555-6666-7777-888888888888");
        const string docPath = "Diagnostics/Quality.pdf";
        var pageOne = string.Join(' ', Enumerable.Repeat("Alpha records the inspection outcome and identifies the operating conditions for each accessible valve.", 3));
        var pageTwoNoise = "yo o | | i Ne | a \\\"------- abgearbeitet __/ sel 48 a) |b";
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(pageTwoNoise), "This fixture must contain a published unit classified as probable OCR noise.");
        var pageTwo = pageOne + Environment.NewLine + pageTwoNoise;

        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: 1, indexedVersion: 0);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [1, 2, 3],
            size: 2048,
            mtimeUtc: DateTime.UtcNow,
            version: 1,
            pages:
            [
                new ExtractedPdfPage(1, pageOne, CountWords(pageOne), pageOne.Length, [1], null, ImageCount: 1),
                new ExtractedPdfPage(2, pageTwo, CountWords(pageTwo), pageTwo.Length, [2], ImageCount: 1)
            ],
            sections:
            [
                new ExtractedDocumentSection(0, "Diagnostics", 1, 1, 2, 1, null)
            ],
            units:
            [
                new ExtractedDocumentUnit(0, 0, 1, 1, pageOne, pageOne.Length, CountWords(pageOne), [3]),
                new ExtractedDocumentUnit(1, 0, 2, 2, pageTwoNoise, pageTwoNoise.Length, CountWords(pageTwoNoise), [4])
            ],
            retrievalChunks:
            [
                new ProjectedRetrievalChunk(0, 0, 0, 1, 1, pageOne, CountWords(pageOne), [5], "unit_exact_v1"),
                new ProjectedRetrievalChunk(1, 0, 1, 2, 2, pageTwo, CountWords(pageTwo), [6], "unit_exact_v1")
            ],
            exactMatchEntries:
            [
                new ExtractedExactMatchEntry(0, 0, 0, 1, 1, pageOne, "alpha", pageOne.Length, CountWords(pageOne), [7], "verbatim_excerpt")
            ],
            contextualTextEntries:
            [
                new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, pageOne, CountWords(pageOne), CountWords(pageOne), [8]),
                new ProjectedContextualTextEntry(1, 0, 1, 1, 2, 2, pageTwo, CountWords(pageTwo), CountWords(pageTwo), [9])
            ],
            CancellationToken.None,
            extractionSource: "pdf_text_plus_image_ocr",
            ocrAttempted: true,
            ocrApplied: true,
            ocrLanguages: "eng",
            ocrDurationMs: 123,
            ocrDiagnostics: new PdfOcrDiagnostics(
                Mode: "image_page",
                CandidatePageCount: 2,
                AttemptedPageCount: 1,
                SkippedPageCount: 1,
                MaxPages: 1,
                CandidatePages: [1, 2],
                AttemptedPages: [1],
                SkippedPages: [2],
                PagesWithOcrText: [1],
                PagesWithNovelText: [1],
                Stderr: "raw-secret-should-not-leak",
                ImagePageDiagnostics:
                [
                    new PdfImagePageOcrDiagnostic(1, "novel_text_applied", OcrWordCount: 90, OcrCharCount: pageOne.Length, ExitCode: 0),
                    new PdfImagePageOcrDiagnostic(2, "skipped", "budget")
                ]));

        Assert.True(committed);

        await using (var staleConn = new NpgsqlConnection(db.ConnectionString))
        {
            await staleConn.OpenAsync();
            await staleConn.ExecuteAsync(
                """
                INSERT INTO document_revisions(
                  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
                  ingestion_version, indexed_version, published_at, created_at
                )
                VALUES(
                  'cdd40000-aaaa-bbbb-cccc-dddddddddddd', @tenant, @docId, @docPath, decode('DD', 'hex'), 123, now() - interval '2 days',
                  1, 0, now() - interval '2 days', now() - interval '2 days'
                )
                ON CONFLICT DO NOTHING;

                INSERT INTO document_page_index(tenant_id, revision_id, page_number, char_count, metadata)
                VALUES(@tenant, 'cdd40000-aaaa-bbbb-cccc-dddddddddddd', 99, 100,
                  '{"wordCount":20,"extractionQuality":{"manualReviewRecommended":true}}'::jsonb);

                INSERT INTO document_processing_runs(
                  processing_run_id, tenant_id, job_id, doc_id, doc_path, revision_id, action, status,
                  ingestion_version, indexed_version_before, indexed_version_after, source_hash,
                  started_at, finished_at, payload
                )
                VALUES(
                  'cdd40000-eeee-ffff-aaaa-bbbbbbbbbbbb', @tenant, 'cdd40000-cccc-dddd-eeee-ffffffffffff', @docId, @docPath,
                  'cdd40000-aaaa-bbbb-cccc-dddddddddddd', 'upsert', 'done',
                  1, 0, 0, decode('DD', 'hex'),
                  now() + interval '1 day', now() + interval '1 day',
                  '{
                    "extractionSource": "pdf_text",
                    "ocrAttempted": true,
                    "ocrApplied": true,
                    "ocrDiagnostics": {
                      "mode": "image_page",
                      "imagePageDiagnostics": [
                        { "pageNumber": 1, "status": "stale_should_not_leak", "reason": "stale" },
                        { "pageNumber": 2, "status": "stale_should_not_leak", "reason": "stale" }
                      ]
                    }
                  }'::jsonb
                )
                ON CONFLICT DO NOTHING;
                """,
                new { tenant = tenantId, docId, docPath });
        }

        var ctx = BuildAdminDocumentsHttpContext(tenantId);
        var result = await InvokeExtractionQualityPagesAsync(ctx, ds, docId);
        await result.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

        using var payload = JsonDocument.Parse(ReadResponseBody(ctx));
        var root = payload.RootElement;
        Assert.DoesNotContain("stale_should_not_leak", root.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(docPath, root.GetProperty("docPath").GetString());
        Assert.True(root.GetProperty("ocrApplied").GetBoolean());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("pageCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("probableOcrNoisePages").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("indexedByContextPages").GetInt32());

        var pages = root.GetProperty("pages").EnumerateArray().ToArray();
        var first = Assert.Single(pages, page => page.GetProperty("pageNumber").GetInt32() == 1);
        Assert.Equal("page_ok_with_images", first.GetProperty("qualityStatus").GetString());
        Assert.False(first.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.Contains(
            first.GetProperty("signals").EnumerateArray(),
            signal => signal.GetString() == "page_contains_images");
        Assert.Equal("novel_text_applied", first.GetProperty("imageOcrStatus").GetString());
        Assert.Equal(90, first.GetProperty("imageOcrWordCount").GetInt32());
        Assert.Equal(0, first.GetProperty("imageOcrExitCode").GetInt32());
        Assert.False(first.GetProperty("imageOcrTimedOut").GetBoolean());
        Assert.Empty(first.GetProperty("unitPreviews").EnumerateArray());
        Assert.Empty(first.GetProperty("chunkPreviews").EnumerateArray());

        var second = Assert.Single(pages, page => page.GetProperty("pageNumber").GetInt32() == 2);
        Assert.Equal("manual_review_probable_ocr_noise", second.GetProperty("qualityStatus").GetString());
        Assert.True(second.GetProperty("manualReviewRecommended").GetBoolean());
        Assert.Equal("skipped", second.GetProperty("imageOcrStatus").GetString());
        Assert.Equal("budget", second.GetProperty("imageOcrReason").GetString());
        Assert.Equal(1, second.GetProperty("suspiciousUnitCount").GetInt32());
        Assert.Contains(
            second.GetProperty("unitPreviews").EnumerateArray(),
            preview => preview.GetString() == pageTwoNoise);
        Assert.Contains(
            second.GetProperty("chunkPreviews").EnumerateArray(),
            preview => preview.GetString() == pageTwo.Replace(Environment.NewLine, " "));

        var ragCtx = BuildRagHttpContext(tenantId);
        var ragResult = await InvokeRagSearchAsync(
            ragCtx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("abgearbeitet", TopK: 3));
        await ragResult.ExecuteAsync(ragCtx);

        Assert.Equal(StatusCodes.Status200OK, ragCtx.Response.StatusCode);
        var ragPayload = ReadResponseBody(ragCtx);
        var ragResponse = JsonSerializer.Deserialize<RagSearchResponseDto>(
            ragPayload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(ragResponse);
        var ragItem = Assert.Single(
            ragResponse!.Items,
            static item => string.Equals(item.DocPath, "Diagnostics/Quality.pdf", StringComparison.Ordinal) && item.PageStart == 2);
        Assert.NotNull(ragItem.ExtractionQuality);
        Assert.Equal("manual_review_probable_ocr_noise", ragItem.ExtractionQuality!.PageQualityStatus);
        Assert.True(ragItem.ExtractionQuality.PageManualReviewRecommended);
        Assert.Contains("probable_ocr_noise_units", ragItem.ExtractionQuality.Signals ?? []);
        Assert.NotNull(ragItem.ExtractionQuality.DiagnosticSummary);
        var diagnostics = ragItem.ExtractionQuality.DiagnosticSummary!;
        Assert.Equal("image_page", diagnostics.OcrMode);
        Assert.Equal("eng", diagnostics.OcrLanguages);
        Assert.Equal(123, diagnostics.OcrDurationMs);
        Assert.Equal(1, diagnostics.OcrAttemptedPageCount);
        Assert.Equal(1, diagnostics.OcrSkippedPageCount);
        Assert.Equal(1, diagnostics.OcrPagesWithNovelTextCount);
        Assert.Equal(2, diagnostics.PageCount);
        Assert.Equal(2, diagnostics.ImagePageCount);
        Assert.Equal(1, diagnostics.PageWarningCount);
        Assert.Equal(1, diagnostics.PageReviewRecommendedCount);
        Assert.Contains("diagnosticSummary", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("imagePageDiagnostics", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("candidatePages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("attemptedPages", ragPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr", ragPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret-should-not-leak", ragPayload, StringComparison.Ordinal);

        var listCtx = BuildAdminDocumentsHttpContext(tenantId);
        var listResult = await InvokeExtractionQualityAsync(listCtx, ds, "Diagnostics", null, 20);
        await listResult.ExecuteAsync(listCtx);
        using var listPayload = JsonDocument.Parse(ReadResponseBody(listCtx));
        var diagnosticItem = Assert.Single(listPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, diagnosticItem.GetProperty("pageWarningCount").GetInt32());
        Assert.Equal(1, diagnosticItem.GetProperty("pageReviewRecommendedCount").GetInt32());
    }

    [Fact]
    public async Task SearchAsync_populates_category_ref_and_category_path_from_matched_document()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("dedeffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        await using (var catalogDs = NpgsqlDataSource.Create(db.ConnectionString))
        {
            var built = await SAAIA.Backend.CatalogSnapshot.CatalogSnapshotBuilder.BuildTenantAsync(
                catalogDs, tenantId, new SAAIA.Backend.CatalogSnapshot.CatalogSnapshotOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
            Assert.True(built.Success);
        }

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var displayOrder = await conn.ExecuteScalarAsync<int>(
            "SELECT display_order FROM documents_catalog_categories WHERE tenant_id=@tenant AND path='ATEX' LIMIT 1;",
            new { tenant = tenantId });
        var expectedCategoryRef = $"cat_{displayOrder:000}";
        var expectedSourceHash = await conn.ExecuteScalarAsync<string>(
            """
            SELECT LOWER(ENCODE(content_hash, 'hex'))
            FROM documents
            WHERE tenant_id=@tenant
              AND doc_path='ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf'
            LIMIT 1;
            """,
            new { tenant = tenantId });

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("Ou trouve-t-on EN 15281 ?", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, match =>
        {
            Assert.Equal("atex", match.Category);
            Assert.Equal("ATEX", match.CategoryPath);
            Assert.Equal(expectedCategoryRef, match.CategoryRef);
            Assert.Equal(expectedSourceHash, match.SourceHash);
            Assert.NotNull(match.ProvenanceInfo);
            Assert.Equal(expectedSourceHash, match.ProvenanceInfo!.SourceHash);
            Assert.False(string.IsNullOrWhiteSpace(match.ProvenanceInfo.Channel));
            Assert.False(string.IsNullOrWhiteSpace(match.RevisionId));
            Assert.True(match.PageStart > 0);
            Assert.True(match.PageEnd >= match.PageStart);
        });
        // Search can legitimately return several channels for a document. Exact
        // offsets are covered by the dedicated SearchExactMatchesAsync scenario.
        var item = documentItems[0];

        Assert.Equal("atex", item.Category);
        Assert.Equal("ATEX", item.CategoryPath);
        Assert.Equal(expectedCategoryRef, item.CategoryRef);
        Assert.Equal(expectedSourceHash, item.SourceHash);
        Assert.False(string.IsNullOrWhiteSpace(item.Snippet));
        Assert.NotNull(item.Context);
        Assert.Equal(item.ChunkType, item.Context!.ChunkType);
        Assert.Equal(item.SectionTitle, item.Context.SectionTitle);
        Assert.Equal(item.HeadingPath, item.Context.HeadingPath);
        Assert.False(item.HypQuestionsMatched);
    }

    [Fact]
    public async Task SearchAsync_sets_hyp_questions_matched_when_query_overlaps_capability_a_questions()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("What does IND570 say about PLC integration?", Category: "programmation", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var documentItems = response!.Items.Where(
            static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(documentItems);
        Assert.All(documentItems, item => Assert.True(item.HypQuestionsMatched));
    }

    [Fact]
    public async Task SearchAsync_merges_hyp_questions_from_all_current_profiles()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-eeeeeeeeeeee");
        var docId = Guid.Parse("dededede-aaaa-bbbb-cccc-111111111111");
        var jobId = Guid.Parse("dededede-aaaa-bbbb-cccc-222222222222");
        const string docPath = "Generic/MergedProfileQuestions.pdf";
        const string text = "Generic operational source text with no night audit cadence phrase.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: MergedProfileQuestions.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var revisionId = await conn.ExecuteScalarAsync<Guid>(
                "SELECT revision_id FROM document_revisions WHERE tenant_id=@tenant AND doc_id=@docId LIMIT 1;",
                new { tenant = tenantId, docId });

            await conn.ExecuteAsync(
                """
                UPDATE document_profiles
                SET summary_text='Generic operational note.',
                    search_text='generic operational note when should the night audit cadence be reviewed',
                    keywords=ARRAY[]::text[],
                    topics=ARRAY[]::text[],
                    hypothetical_questions=ARRAY['When should the night audit cadence be reviewed?']::text[]
                WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';

                INSERT INTO document_profiles(
                  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
                  summary_text, keywords, entities, topics, hypothetical_questions, limits,
                  search_text, token_count, checksum, metadata
                )
                VALUES(
                  @llmProfileId, @tenant, @revision, @docId, 'llm_backoffice_v1', 'en',
                  'LLM profile without generated questions yet.',
                  ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                  'LLM profile without generated questions yet.',
                  6,
                  decode(repeat('49', 32), 'hex'),
                  '{}'::jsonb
                );
                """,
                new
                {
                    tenant = tenantId,
                    revision = revisionId,
                    docId,
                    llmProfileId = Guid.Parse("dededede-aaaa-bbbb-cccc-333333333333")
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var ctx = BuildRagHttpContext(tenantId);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("night audit cadence", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var item = Assert.Single(
            response!.Items,
            static match => string.Equals(match.DocPath, docPath, StringComparison.Ordinal));

        Assert.True(item.HypQuestionsMatched);
        Assert.Equal("en", item.ProfileLanguage);
    }

    [Fact]
    public async Task LoadHypQuestionsMatchedByDocPathAsync_normalizes_legacy_match_paths()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("efefffff-ffff-ffff-ffff-eeeeeeeeeeef");
        var docId = Guid.Parse("dededede-aaaa-bbbb-cccc-111111111112");
        var jobId = Guid.Parse("dededede-aaaa-bbbb-cccc-222222222223");
        const string docPath = "Generic/LegacyPathQuestions.pdf";
        const string text = "Generic source text without the readiness cadence wording.";

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            docId,
            jobId,
            docPath,
            1,
            "Operational baseline",
            text,
            $"Document: LegacyPathQuestions.pdf\nHeading Path: Operational baseline\nExcerpt:\n{text}");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
                UPDATE document_profiles
                SET hypothetical_questions=ARRAY['When should the readiness cadence be reviewed?']::text[]
                WHERE tenant_id=@tenant AND doc_id=@docId AND profile_version='deterministic_v1';
                """,
                new { tenant = tenantId, docId });
        }

        var matchedByPath = await RagEndpoints.LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            tenantId,
            "readiness cadence",
            [
                new RagMatch(
                    0.9,
                    docId.ToString(),
                    "\\Generic\\LegacyPathQuestions.pdf",
                    "LegacyPathQuestions.pdf",
                    1,
                    1,
                    "legacy-path-chunk",
                    0,
                    text,
                    1,
                    null,
                    text,
                    "unit_exact_v1",
                    0,
                    0,
                    "Operational baseline",
                    "Operational baseline",
                    "unit_exact",
                    null,
                    null,
                    null)
            ],
            CancellationToken.None);

        var matched = RagEndpoints.ResolveHypQuestionsMatched("\\Generic\\LegacyPathQuestions.pdf", matchedByPath);
        Assert.True(matched.HasValue);
        Assert.True(matched.Value);
    }

    [Fact]
    public async Task SearchAsync_does_not_call_capability_a_llm_questions_during_interactive_search()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("faf0ffff-ffff-ffff-ffff-ffffffffffff");
        await PublishRuntimeReadyQuestionBankDocumentsAsync(db, tenantId);

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var (requestServices, llmFactory) = BuildCapabilityARequestServices();
        var ctx = BuildRagHttpContext(tenantId, requestServices);
        var result = await InvokeRagSearchAsync(
            ctx,
            ds,
            Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(),
            new RagSearchRequestDto("When should the IND570 PLC integration guidance be retrieved?", Category: "programmation", TopK: 3));

        await result.ExecuteAsync(ctx);

        var payload = ReadResponseBody(ctx);
        var response = JsonSerializer.Deserialize<RagSearchResponseDto>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        var items = response!.Items
            .Where(static match => string.Equals(match.DocPath, "Programmation/Mettler/MettlerToledo_IND570.pdf", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.NotNull(item.HypQuestionsMatched));
        Assert.Equal(0, llmFactory.LlmRequestCount);
    }

    private static DefaultHttpContext BuildRagHttpContext(Guid tenantId, IServiceProvider? requestServices = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.Items[RequestIdMiddleware.RequestIdItemKey] = $"it-{tenantId:N}";
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = requestServices ?? new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();
        return ctx;
    }

    private static DefaultHttpContext BuildAdminDocumentsHttpContext(Guid tenantId, IServiceProvider? requestServices = null)
    {
        var ctx = BuildRagHttpContext(tenantId, requestServices);
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        return ctx;
    }

    private static async Task<IResult> InvokeDocumentsSnapshotAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("SnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds])!;
    }

    private static async Task<IResult> InvokeUnifiedDocumentsListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        int limit,
        int offset,
        string? status = null)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("UnifiedListAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, null, status, null, null, null, null, limit, offset])!;
    }

    private static async Task<IResult> InvokeUnifiedDocumentsGetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("UnifiedGetAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId])!;
    }

    private static async Task<IResult> InvokeDocumentsContentCardsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryPath,
        string? q,
        string? inventoryMode,
        int limit,
        int offset)
    {
        var method = typeof(DocumentsEndpoints).GetMethod(
            "ContentCardsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(
            null,
            [ctx, ds, categoryPath, null, null, null, q, inventoryMode, limit, offset])!;
    }

    private static async Task<IResult> InvokeResolveCategoryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ResolveCategoryRequest request)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ResolveCategoryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, request])!;
    }

    private static async Task<IResult> InvokeResolveSourceAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        SourceResolveRequest request)
    {
        var method = typeof(SourcesEndpoints).GetMethod("ResolveSourceAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, request])!;
    }

    private static async Task<IResult> InvokeGetSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId,
        string? level)
    {
        var method = typeof(SummaryEndpoints).GetMethod("GetSummaryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId, level])!;
    }

    private static async Task<IResult> InvokeDeleteSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId,
        string? level)
    {
        var method = typeof(SummaryEndpoints).GetMethod("DeleteSummaryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId, level])!;
    }

    private static async Task<IResult> InvokeSearchSummariesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? q,
        int? limit,
        int? offset)
    {
        var method = typeof(SummaryEndpoints).GetMethod("SearchSummariesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, q, limit, offset])!;
    }

    private static async Task<IResult> InvokeExtractionQualityPagesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ExtractionQualityPagesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, docId])!;
    }

    private static async Task<IResult> InvokeExtractionQualityAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? limit)
    {
        var method = typeof(DocumentsEndpoints).GetMethod("ExtractionQualityAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, path, categoryRef, limit])!;
    }

    private static async Task<IResult> InvokeCatalogCacheEndpointAsync(
        string endpoint,
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<IngestionOptions> ingestionOptions)
    {
        var (methodName, args) = endpoint switch
        {
            "catalog_categories" => (
                "CatalogCategoriesAsync",
                new object?[] { ctx, ds, null, null, 100, null, null }),
            "catalog_documents" => (
                "CatalogDocumentsAsync",
                new object?[] { ctx, ds, null, null, null, null, null, 50, null, null }),
            "documents_stats" => (
                "StatsAsync",
                new object?[] { ctx, ds, ingestionOptions, null, null }),
            "catalog_stats" => (
                "CatalogStatsAsync",
                new object?[] { ctx, ds, ingestionOptions, null, null }),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown catalog cache endpoint.")
        };

        var method = typeof(DocumentsEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, args)!;
    }

    private static async Task<IResult> InvokeRagSearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOptions,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto request)
    {
        var method = typeof(RagEndpoints).GetMethod("SearchAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, ragOptions, httpFactory,
            new RagSearchBulkhead(ragOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<RagSearchBulkhead>.Instance),
            new TeiWorkloadGovernor(), request])!;
    }

    private static async Task<IResult> InvokeRagQueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOptions,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto request)
    {
        var method = typeof(RagEndpoints).GetMethod("QueryAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds, ragOptions, httpFactory,
            new RagSearchBulkhead(ragOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<RagSearchBulkhead>.Instance),
            new TeiWorkloadGovernor(), request])!;
    }

    private static async Task<IResult> InvokeRagCategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var method = typeof(RagEndpoints).GetMethod("CategoriesAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return await (Task<IResult>)method!.Invoke(null, [ctx, ds])!;
    }

    private static (IServiceProvider Services, CountingLlmHttpClientFactory LlmFactory) BuildCapabilityARequestServices()
    {
        var llmFactory = new CountingLlmHttpClientFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        services.AddSingleton(new CapabilityAHypotheticalQuestionService(
            new LocalLlmChatClient(
                llmFactory,
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "local"
                })));
        return (services.BuildServiceProvider(), llmFactory);
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static RagOptions CreateTestRagOptions()
        => new()
        {
            EmbeddingsBaseUrl = "http://tei.test/",
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = "knowledge_base",
            DefaultTopK = 5,
            MaxTopK = 20,
            EnableRerank = false
        };

    private static ExtractedExactMatchEntry BuildExactMatchEntry(string text, string normalizedText, string kind)
        => BuildExactMatchEntry(0, text, normalizedText, kind);

    private static ExtractedExactMatchEntry BuildExactMatchEntry(int entryIndex, string text, string normalizedText, string kind)
        => new(
            EntryIndex: entryIndex,
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            PageStart: 1,
            PageEnd: 1,
            Text: text,
            NormalizedText: normalizedText,
            CharCount: text.Length,
            TokenCount: Math.Max(1, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
            Checksum: [1, 2, 3],
            Kind: kind);

    private static string? ResolveDocHint(RagMatch match)
    {
        var docText = $"{match.DocName} {match.DocPath}";
        if (docText.Contains("15281", StringComparison.OrdinalIgnoreCase))
            return "15281";
        if (docText.Contains("ind570", StringComparison.OrdinalIgnoreCase))
            return "IND570";
        if (docText.Contains("accord", StringComparison.OrdinalIgnoreCase))
            return "ACCORD";
        return null;
    }

    private sealed record RuntimeSeedSection(string Title, string Text);

    private sealed class LegacyCatalogMetadataSeed
    {
        public Guid DocId { get; set; }
        public Guid RevisionId { get; set; }
        public string DocPath { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string? CategoryRef { get; set; }
    }

    private static async Task<LegacyCatalogMetadataSeed> SeedLegacyCatalogMetadataAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid? excludeDocId = null)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var seed = await conn.QuerySingleAsync<LegacyCatalogMetadataSeed>(
            """
            SELECT
              d.doc_id AS "DocId",
              d.doc_path AS "DocPath",
              r.revision_id AS "RevisionId",
              saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS "SourceHash",
              CASE WHEN c.display_order IS NULL THEN NULL ELSE ('cat_' || lpad(c.display_order::text, 3, '0')) END AS "CategoryRef"
            FROM documents d
            JOIN document_revisions r
              ON r.tenant_id = d.tenant_id
             AND r.doc_id = d.doc_id
             AND r.indexed_version = d.indexed_version
            LEFT JOIN documents_catalog_categories c
              ON c.tenant_id = d.tenant_id
             AND c.path = split_part(d.doc_path, '/', 1)
            WHERE d.tenant_id=@tenant
              AND d.status='indexed'
              AND (@excludeDocId::uuid IS NULL OR d.doc_id <> @excludeDocId::uuid)
            ORDER BY d.doc_path ASC
            LIMIT 1;
            """,
            new { tenant = tenantId, excludeDocId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_profiles (
              document_profile_id,
              tenant_id,
              revision_id,
              doc_id,
              profile_version,
              language,
              summary_text,
              search_text,
              token_count,
              checksum
            )
            VALUES (
              @profileId,
              @tenant,
              @revisionId,
              @docId,
              'llm_backoffice_v1',
              'DE',
              'Profil de test.',
              'Profil de test.',
              3,
              decode('AB', 'hex')
            )
            ON CONFLICT (revision_id, profile_version) DO UPDATE
            SET language=excluded.language,
                updated_at=now();
            """,
            new { profileId = Guid.NewGuid(), tenant = tenantId, revisionId = seed.RevisionId, docId = seed.DocId });

        await conn.ExecuteAsync(
            """
            INSERT INTO document_summaries (
              tenant_id,
              doc_id,
              level,
              doc_language,
              source_hash,
              summary_text,
              summary_meta,
              created_at,
              updated_at
            )
            VALUES (
              @tenant,
              @docId,
              'medium',
              'IT',
              @sourceHash,
              'Riassunto di test.',
              '{}'::jsonb,
              now(),
              now()
            )
            ON CONFLICT (tenant_id, doc_id, level) DO UPDATE
            SET doc_language=excluded.doc_language,
                source_hash=excluded.source_hash,
                updated_at=now();
            """,
            new { tenant = tenantId, docId = seed.DocId, sourceHash = seed.SourceHash });

        return seed;
    }

    private static async Task PublishIndexedDocumentAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        int version,
        string sectionTitle,
        string chunkText,
        string contextualText,
        ExtractedExactMatchEntry[]? exactMatchEntries = null)
    {
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: version, indexedVersion: Math.Max(0, version - 1));

        var pages = new[]
        {
            new ExtractedPdfPage(1, chunkText, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), chunkText.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, sectionTitle, 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, chunkText, chunkText.Length, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [2])
        };
        var retrievalChunks = new[]
        {
            new ProjectedRetrievalChunk(0, 0, 0, 1, 1, chunkText, Math.Max(1, chunkText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [3], "unit_exact_v1")
        };
        var contextualEntries = new[]
        {
            new ProjectedContextualTextEntry(0, 0, 0, 0, 1, 1, contextualText, contextualText.Length, Math.Max(1, contextualText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length), [4])
        };

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [9, 9, 9],
            size: chunkText.Length,
            mtimeUtc: DateTime.UtcNow,
            version: version,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries ?? [],
            contextualEntries,
            CancellationToken.None);

        Assert.True(committed);
    }

    private static async Task PublishIndexedDocumentAsync(
        PostgresIntegrationDb db,
        Guid tenantId,
        Guid docId,
        Guid jobId,
        string docPath,
        int version,
        RuntimeSeedSection[] sections,
        ExtractedExactMatchEntry[]? exactMatchEntries = null,
        bool useProductionContextualProjection = false)
    {
        await db.SeedRunningJobAsync(tenantId, docId, jobId, docPath, ingestionVersion: version, indexedVersion: Math.Max(0, version - 1));

        var pages = sections
            .Select((section, index) => new ExtractedPdfPage(
                index + 1,
                section.Text,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                section.Text.Length,
                [(byte)(10 + index)]))
            .ToArray();
        var extractedSections = sections
            .Select((section, index) => new ExtractedDocumentSection(index, section.Title, index + 1, index + 1, index + 1, index + 1, null))
            .ToArray();
        var units = sections
            .Select((section, index) => new ExtractedDocumentUnit(
                index,
                index,
                index + 1,
                index + 1,
                section.Text,
                section.Text.Length,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                [(byte)(20 + index)]))
            .ToArray();
        var retrievalChunks = sections
            .Select((section, index) => new ProjectedRetrievalChunk(
                index,
                index,
                index,
                index + 1,
                index + 1,
                section.Text,
                Math.Max(1, section.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                [(byte)(30 + index)],
                "unit_exact_v1"))
            .ToArray();
        var contextualEntries = sections
            .Select((section, index) =>
            {
                var contextualText = $"Document: {Path.GetFileName(docPath)}\nSection: {section.Title}\nHeading Path: {section.Title}\nExcerpt:\n{section.Text}";
                return new ProjectedContextualTextEntry(
                    index,
                    index,
                    index,
                    index,
                    index + 1,
                    index + 1,
                    contextualText,
                    contextualText.Length,
                    Math.Max(1, contextualText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                    [(byte)(40 + index)]);
            })
            .ToArray();
        if (useProductionContextualProjection)
            contextualEntries = ContextualTextProjector.Project(docPath, extractedSections, units, retrievalChunks).ToArray();

        var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds,
            tenantId,
            jobId,
            docPath,
            hash: [8, 8, 8],
            size: sections.Sum(static section => section.Text.Length),
            mtimeUtc: DateTime.UtcNow,
            version: version,
            pages,
            extractedSections,
            units,
            retrievalChunks,
            exactMatchEntries ?? [],
            contextualEntries,
            CancellationToken.None);

        Assert.True(committed);
    }

    private static async Task PublishRuntimeReadyQuestionBankDocumentsAsync(PostgresIntegrationDb db, Guid tenantId)
    {
        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("1212aaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("1313bbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "ATEX/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
            1,
            [
                new RuntimeSeedSection("Definitions", "EN 15281. Absolute inerting means replacing air with an inert gas and maintaining oxygen concentration below the limiting oxygen concentration. LOC and MAOC are used to describe the safe oxygen threshold below which the mixture should no longer explode."),
                new RuntimeSeedSection("Normative references", "Normative references around inerting include EN 61508 and EN 61511 for safety instrumented functions and safety lifecycle context."),
                new RuntimeSeedSection("Inert gases", "Nitrogen, carbon dioxide, steam, flue gases and noble gases are discussed as inerting media with different practical constraints."),
                new RuntimeSeedSection("Inerting methods", "Pressure-swing, vacuum-swing, flow-through and displacement inerting are compared for explosion prevention and process safety. Flow-through inerting is useful for long pipelines or vessels when gas feed and venting are remote from each other."),
                new RuntimeSeedSection("Process scope", "The guide covers gas, vapour, dust, mist and hybrid mixtures when assessing inerting and explosion prevention."),
                new RuntimeSeedSection("Process parameters", "Limiting oxygen concentration depends on temperature, pressure, fuel concentration, particle size and process conditions."),
                new RuntimeSeedSection("Safety margin", "The safety margin is the difference between the set point and the trip point so the control system can react before the oxygen concentration becomes unsafe."),
                new RuntimeSeedSection("System components", "The inerting system includes inert gas supply, monitoring, control, alarms, shutdown logic and protective devices."),
                new RuntimeSeedSection("Reliability", "Reliability of inerting systems depends on monitoring, alarms, maintenance, trip point definition and equipment performance. The guide distinguishes direct oxygen measurement from inferential approaches based on purge flow, pressure or time."),
                new RuntimeSeedSection("Oxygen monitoring", "Oxygen monitoring technologies, oxygen analyzers, set point and trip point strategy are described for inerting systems.")
            ],
            [
                BuildExactMatchEntry(0, "EN 15281", "en 15281", "standard_ref") with { OffsetStart = 0, OffsetEnd = "EN 15281".Length }
            ]);

        await PublishIndexedDocumentAsync(
            db,
            tenantId,
            Guid.Parse("1414cccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("1515dddd-dddd-dddd-dddd-dddddddddddd"),
            "Programmation/Mettler/MettlerToledo_IND570.pdf",
            1,
            [
                new RuntimeSeedSection("PLC integration", "The IND570 terminal communicates with PLC systems and supports integration through industrial network interfaces so the scale can exchange data with the automate."),
                new RuntimeSeedSection("PROFINET", "The PROFINET interface supports Siemens environments, unique IP assignment and topology considerations for the IND570 terminal."),
                new RuntimeSeedSection("Supported protocols", "Supported protocols include EtherNet/IP, PROFINET, PROFIBUS, Modbus TCP, Modbus RTU, ControlNet and DeviceNet."),
                new RuntimeSeedSection("EtherNet/IP", "The IND570 guide documents EtherNet/IP Class 1 and Class 3 messaging for PLC integration scenarios."),
                new RuntimeSeedSection("DeviceNet", "DeviceNet configuration requires a unique node address and a suitable network speed such as 125 Kb, 250 Kb or 500 Kb."),
                new RuntimeSeedSection("Modbus TCP", "Modbus TCP configuration covers the IND570 IP address, master and slave register references, and communication settings."),
                new RuntimeSeedSection("Hazardous area", "Not all IND570 versions are approved for hazardous areas and relay options are not intended for hazardous zone use."),
                new RuntimeSeedSection("Analog output", "Analog output options include current and voltage outputs such as 4-20 mA and 0-10 V with installation guidance for sending weight or rate information."),
                new RuntimeSeedSection("Analog calibration", "Analog calibration of the IND570 output is performed through setup steps that define range, scaling and verification."),
                new RuntimeSeedSection("Programming examples", "The guide includes programming examples with Siemens S7-300 and explains shared data access, integer format, byte ordering and floating point exchanges for PLC integration. Floating point is recommended when possible to avoid small conversion errors."),
                new RuntimeSeedSection("Shared data", "Shared data access allows read and write operations from the PLC through explicit or Class 3 style messaging using category, instance, attribute and length information."),
                new RuntimeSeedSection("Data integrity", "PLC programs should filter values with Data_OK, Update_In_Progress and integrity bits before using returned data so invalid values are not consumed."),
                new RuntimeSeedSection("PLC commands", "The PLC interface can exchange tare, target values and tolerances with the IND570 in supported integration profiles.")
            ],
            [
                BuildExactMatchEntry(0, "IND570", "ind570", "code_ref")
            ]);
    }

    private sealed class ReindexOnRerankHttpClientFactory(Func<Task> reindex) : IHttpClientFactory
    {
        private int _reindexCount;
        public int ReindexCount => _reindexCount;
        public HttpClient CreateClient(string name)
            => new(new ReindexOnRerankHandler(async () =>
            {
                if (Interlocked.CompareExchange(ref _reindexCount, 1, 0) == 0)
                    await reindex();
            })) { BaseAddress = new Uri("http://stub.test/") };
    }

    private sealed class ReindexOnRerankHandler(Func<Task> reindex) : DelegatingHandler(new StubHttpMessageHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
                await reindex();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "tei" => new Uri("http://tei.test/"),
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class FailingTeiHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FailingTeiHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "tei" => new Uri("http://tei.test/"),
                    "qdrant" => new Uri("http://qdrant.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class FailingTeiHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("simulated TEI transport failure");

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class CountingLlmHttpClientFactory : IHttpClientFactory
    {
        private readonly CountingLlmHttpMessageHandler _handler = new();

        public int LlmRequestCount => _handler.LlmRequestCount;

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class CountingLlmHttpMessageHandler : HttpMessageHandler
    {
        private int _llmRequestCount;

        public int LlmRequestCount => _llmRequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if ((request.RequestUri?.AbsolutePath ?? string.Empty).EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                System.Threading.Interlocked.Increment(ref _llmRequestCount);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "{\"questions\":[\"When should the IND570 PLC integration guidance be retrieved?\"]}"
                          }
                        }
                      ]
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "data": [
                    { "embedding": [0.1, 0.2, 0.3, 0.4] }
                  ]
                }
                """));
            }

            if (path.EndsWith("/points/search", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "result": []
                }
                """));
            }

            if (path.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "results": []
                }
                """));
            }

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"questions\":[\"When should the IND570 PLC integration guidance be retrieved?\",\"How should operators apply the Control Loop Overview in IND570?\"]}"
                      }
                    }
                  ]
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private sealed class PostgresIntegrationDb : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        public string ConnectionString { get; }

        private PostgresIntegrationDb(string adminConnectionString, string databaseName, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public static async Task<PostgresIntegrationDb?> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable("SAAIA_TEST_PG_CONN");
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                return null;

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
            var adminConnectionString = adminBuilder.ConnectionString;
            var databaseName = $"saaia_it_{Guid.NewGuid():N}";

            await using (var adminConn = new NpgsqlConnection(adminConnectionString))
            {
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync($"CREATE DATABASE \"{databaseName}\";");
            }

            var dbBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName
            };

            var db = new PostgresIntegrationDb(adminConnectionString, databaseName, dbBuilder.ConnectionString);
            var migrationsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SAAIA.Backend", "Db", "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, CancellationToken.None);
            return db;
        }

        public async Task SeedRunningJobAsync(Guid tenantId, Guid docId, Guid jobId, string docPath, int ingestionVersion, int indexedVersion, string action = "upsert")
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "INSERT INTO tenants(tenant_id, name) VALUES(@tenant_id, @name) ON CONFLICT (tenant_id) DO NOTHING;",
                new { tenant_id = tenantId, name = "Test tenant" });

            var seeded = await conn.ExecuteAsync(
                @"INSERT INTO documents(
                    tenant_id, doc_id, doc_path, doc_name, category, status, ingestion_version, indexed_version, created_at, updated_at)
                  VALUES(
                    @tenant_id, @doc_id, @doc_path, @doc_name, @category, 'pending', @ingestion_version, @indexed_version, now(), now())
                  ON CONFLICT (tenant_id, doc_id) DO UPDATE
                  SET ingestion_version=EXCLUDED.ingestion_version
                  WHERE documents.doc_path=EXCLUDED.doc_path AND documents.indexed_version=EXCLUDED.indexed_version;",
                new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    doc_path = docPath,
                    doc_name = Path.GetFileName(docPath),
                    category = IngestionCategoryResolver.DeriveFromDocumentPath(docPath),
                    ingestion_version = ingestionVersion,
                    indexed_version = indexedVersion
                });
            Assert.Equal(1, seeded);

            var payload = JsonSerializer.Serialize(new { });
            await conn.ExecuteAsync(
                @"INSERT INTO ingestion_jobs(
                    job_id, tenant_id, action, doc_path, category, status, attempts, locked_by, locked_at, available_at, created_at, started_at, payload)
                  VALUES(
                    @job_id, @tenant_id, @action, @doc_path, @category, 'running', 1, 'test-worker', now(), now(), now(), now(), CAST(@payload AS jsonb));",
                new
                {
                    job_id = jobId,
                    tenant_id = tenantId,
                    action,
                    doc_path = docPath,
                    category = IngestionCategoryResolver.DeriveFromDocumentPath(docPath),
                    payload
                });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var adminConn = new NpgsqlConnection(_adminConnectionString);
                await adminConn.OpenAsync();
                await adminConn.ExecuteAsync(
                    $@"SELECT pg_terminate_backend(pid)
                       FROM pg_stat_activity
                       WHERE datname = '{_databaseName}'
                         AND pid <> pg_backend_pid();");
                await adminConn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\";");
            }
            catch
            {
                // Best-effort cleanup for optional integration tests.
            }
        }
    }
}
