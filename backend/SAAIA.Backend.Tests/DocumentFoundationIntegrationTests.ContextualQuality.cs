using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Context_quality_real_publication_persists_chunk_and_added_unit_warnings_with_original_anchors()
        => await VerifyContextQualityPublicationAsync(unknown: false);

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Context_quality_real_publication_keeps_unknown_legacy_quality_nullable()
        => await VerifyContextQualityPublicationAsync(unknown: true);

    private static async Task VerifyContextQualityPublicationAsync(bool unknown)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

        var tenantId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        const string path = "Synthetic/Context-quality.pdf";
        var chunk = ContextualTextProjectorQualityTests.Chunk(unknown ? null : ["canonical_table_native_region_coverage_unresolved"]);
        if (unknown)
            chunk = chunk with { ExtractionTextStatus = null };
        ExtractedDocumentUnit[] units = unknown ? [] :
        [
            ContextualTextProjectorQualityTests.Unit(0, "Previous", ["previous_observation"]),
            ContextualTextProjectorQualityTests.Unit(1, "Current", ["current_observation"]),
            ContextualTextProjectorQualityTests.Unit(2, "Next", ["next_observation"])
        ];
        ExtractedDocumentSection[] sections = [new(0, "Inspection", 1, 1, 1, null, null)];
        if (unknown)
            chunk = chunk with { SectionOrdinal = null, UnitOrdinal = null };
        var entry = Assert.Single(ContextualTextProjector.Project(path, sections, units, [chunk]));
        var pageText = string.Join("\n", units.Select(unit => unit.Text).Append(chunk.Text));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pageText));
        await db.SeedRunningJobAsync(tenantId, docId, jobId, path, ingestionVersion: 1, indexedVersion: 0);
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        Assert.True(await JobRepo.CompleteUpsertAsync(ds, tenantId, jobId, path, hash, pageText.Length,
            DateTime.UtcNow, 1, [new ExtractedPdfPage(1, pageText, 100, pageText.Length, hash)],
            sections, units, [chunk], [], [entry], CancellationToken.None, leaseOwner: "test-worker"));

        await using var connection = await ds.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<ContextQualityStoredRow>("""
SELECT text_content AS "Text", metadata::text AS "Metadata", page_start AS "PageStart",
       page_end AS "PageEnd", retrieval_chunk_id AS "ChunkId", checksum AS "Checksum"
FROM contextual_text_entries WHERE tenant_id=@tenantId;
""", new { tenantId });
        Assert.Equal((1, 1), (row.PageStart, row.PageEnd));
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 1, 0), row.ChunkId);
        using var document = JsonDocument.Parse(row.Metadata);
        var metadata = document.RootElement;
        Assert.Equal(new[] { 1 }, metadata.GetProperty("sourceUnitOrdinals").EnumerateArray().Select(item => item.GetInt32()));
        if (unknown)
        {
            Assert.Equal("unavailable", metadata.GetProperty("extractionQualityAvailability").GetString());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("extractionTextStatus").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("extractionTextSparse").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("extractionOcrCandidate").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("extractionQualitySignals").ValueKind);
        }
        else
        {
            Assert.Equal("complete", metadata.GetProperty("extractionQualityAvailability").GetString());
            Assert.Equal("ok", metadata.GetProperty("extractionTextStatus").GetString());
            Assert.False(metadata.GetProperty("extractionOcrCandidate").GetBoolean());
            Assert.False(metadata.GetProperty("extractionTextSparse").GetBoolean());
            Assert.Equal(new[] { "canonical_table_native_region_coverage_unresolved", "current_observation", "next_observation", "previous_observation" },
                metadata.GetProperty("extractionQualitySignals").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(new[] { 0, 1, 2 }, metadata.GetProperty("contributorUnitOrdinals").EnumerateArray().Select(item => item.GetInt32()));
        }
        // PostgreSQL storage already normalizes Windows line endings. Check
        // those exact bytes independently of the in-memory projection hash.
        var expectedStoredText = entry.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expectedStoredText, row.Text);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(expectedStoredText)), row.Checksum);
    }

    private sealed class ContextQualityStoredRow
    {
        public string Text { get; set; } = "";
        public string Metadata { get; set; } = "";
        public int PageStart { get; set; }
        public int PageEnd { get; set; }
        public Guid ChunkId { get; set; }
        public byte[] Checksum { get; set; } = [];
    }
}
