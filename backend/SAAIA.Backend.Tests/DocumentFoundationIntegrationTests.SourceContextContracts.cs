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
    public async Task SearchTitleAnchorRouteMatchesAsync_direct_route_handles_accents_and_ignores_navigation_chunks()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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
        Assert.Equal("direct_title_token_route", RetrievalNavigation.ResolveRetriever(match));

        var anchorOnlyMatches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_content_card_full_title_lead_over_dense_partial_page_chunk(int topK)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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
        Assert.Equal("title_anchor_route", RetrievalNavigation.ResolveRetriever(match));
        Assert.Contains(title, match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dense OCR partial", match.Text, StringComparison.OrdinalIgnoreCase);

        var anchorOnlyMatches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_anchor_page_over_adjacent_partial_token_hit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
            ds,
            tenantId,
            title,
            category: "operations",
            docId: docId.ToString(),
            docPath: docPath,
            topK: 3,
            CancellationToken.None);

        var anchorOnlyMatches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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
        Assert.True(RetrievalCandidateSignals.TitleAnchorRouteHasTargetTitleEvidence(anchorOnly));
        var match = Assert.Single(matches);
        Assert.Equal(DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 9, 1).ToString(), match.ChunkId);
        Assert.Equal("title_anchor_route_v1", match.EmbeddingBasis);
        Assert.StartsWith("Matched title_anchor_exact_page_route:", match.EmbedText, StringComparison.Ordinal);
        Assert.Contains("anchored page", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adjacent footer", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileTheory("IntegrationPg")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task SearchTitleAnchorRouteMatchesAsync_prefers_same_page_section_title_hit_over_adjacent_dense_chunk(int topK)
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchTitleAnchorRouteMatchesAsync(
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
}
