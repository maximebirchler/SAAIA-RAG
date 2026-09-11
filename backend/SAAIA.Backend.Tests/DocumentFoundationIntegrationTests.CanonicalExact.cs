using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Theory]
    [InlineData("direct", true)]
    [InlineData("composed", true)]
    [InlineData("unlinked", false)]
    [InlineData("different_text", false)]
    public async Task Canonical_exact_search_returns_only_current_chunks_linked_to_the_matched_source_unit(
        string linkage, bool expectedMatch)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null) return;
        var tenant = Guid.NewGuid();
        var doc = Guid.NewGuid();
        const string path = "Knowledge/ZX-71.pdf";
        const string query = "ZX-71";
        const string current = "The ZX-71 instrument records the current inspection result. The operator signs the reference check before accepting the measured value.";
        const string indexText = "A deliberately separate lexical fixture surface for isolating the exact retrieval channel without dense results.";
        static ExtractedExactMatchEntry[] Entries() =>
            [new(0, 0, 0, 1, 1, query, ExactMatchEntryExtractor.NormalizeForLookup(query), query.Length, 1, [4], "code_ref")];
        await PublishIndexedDocumentAsync(db, tenant, doc, Guid.NewGuid(), path, 1, "Previous record",
            "The ZX-71 instrument has an obsolete inspection marker which must not survive publication of a replacement revision.", indexText, Entries());
        await PublishIndexedDocumentAsync(db, tenant, doc, Guid.NewGuid(), path, 2, "Current record", current, indexText, Entries());
        await PublishIndexedDocumentAsync(db, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path, 1, "Foreign record",
            "The ZX-71 instrument has a private foreign tenant marker which must never appear in another tenant's retrieval result.", indexText, Entries());
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var revision = DocumentFoundationRepo.BuildStableRevisionId(tenant, doc, 2);
        var chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(doc, 2, 0);
        await using var conn = await ds.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM exact_match_entries WHERE tenant_id=@tenant AND revision_id=@revision;", new { tenant, revision }));
        if (linkage is "composed" or "unlinked")
        {
            Assert.Equal(1, await conn.ExecuteAsync("""
                UPDATE retrieval_chunks SET unit_id=NULL,
                    metadata=jsonb_set(metadata, '{sourceUnitOrdinals}', @ordinals::jsonb)
                WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunkId;
                """, new { tenant, chunkId, ordinals = linkage == "composed" ? "[0]" : "[9]" }));
        }
        else if (linkage == "different_text")
        {
            Assert.Equal(1, await conn.ExecuteAsync("""
                UPDATE retrieval_chunks SET text_content=@text
                WHERE tenant_id=@tenant AND retrieval_chunk_id=@chunkId;
                """, new { tenant, chunkId, text = "A different fragment of the same unit describes the operator signature but does not contain the indexed reference." }));
        }

        var ctx = BuildRagHttpContext(tenant);
        var response = await InvokeRagSearchAsync(ctx, ds, Options.Create(CreateTestRagOptions()),
            new StubHttpClientFactory(), new RagSearchRequestDto(query, DocId: doc.ToString(), TopK: 5, SourceBackedCanonical: true));
        await response.ExecuteAsync(ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(ctx));
        var export = Environment.GetEnvironmentVariable("SAAIA_TEST_CONTRACT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(export))
            await File.WriteAllTextAsync(Path.Combine(export, $"canonical-exact-{linkage}.json"), json.GetRawText());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        if (expectedMatch)
        {
            var item = Assert.Single(items);
            Assert.Equal(doc.ToString(), item.GetProperty("docId").GetString());
            Assert.Equal(revision.ToString(), item.GetProperty("revisionId").GetString());
            Assert.Equal(chunkId.ToString(), item.GetProperty("chunkId").GetString());
            Assert.Equal(current, item.GetProperty("text").GetString());
            Assert.Equal(1, item.GetProperty("pageStart").GetInt32());
            Assert.Equal(1, item.GetProperty("pageEnd").GetInt32());
            Assert.Equal("exact_match", item.GetProperty("retriever").GetString());
        }
        else Assert.Empty(items);
        var metrics = json.GetProperty("metrics");
        if (metrics.TryGetProperty("degradedRetrieverErrors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            Assert.False(errors.TryGetProperty("source_identity", out _), "Exact candidates must already carry real chunk identities.");
    }
}
