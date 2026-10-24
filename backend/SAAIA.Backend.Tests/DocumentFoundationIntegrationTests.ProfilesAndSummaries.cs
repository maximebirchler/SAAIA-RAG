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
    public async Task UpsertDocumentProfileAsync_purges_legacy_cards_with_stale_profile_version()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_uses_materialized_content_cards_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "weekly maintenance plan",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Equal(1, match.PageStart);
        Assert.Equal(1, match.PageEnd);
        Assert.Contains("Weekly Maintenance Plan", match.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_ignores_pruned_cards_left_only_in_profile_search_text()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_uses_fresh_stored_summary_when_profile_search_text_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "orbital calibration cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
        Assert.Contains("orbital calibration cadence", match.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orbital calibration cadence", match.EmbedText, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_uses_hypothetical_questions_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "night audit cadence",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_uses_llm_profile_terms_and_cards_as_candidates()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "pressure envelope validation",
            category: null,
            docId: null,
            docPath: null,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task RefreshDocumentProfileSearchEntryAsync_filters_unsafe_legacy_profile_cards()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_uses_content_cards_beyond_legacy_projection_limit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
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

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_repairs_missing_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
            ds,
            tenantId,
            "zephyr pump alignment",
            category: null,
            docId: null,
            docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var afterCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Equal(1, afterCount);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentOverviewProfileMatchesAsync_repairs_missing_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentOverviewProfileMatchesAsync(
            ds,
            tenantId,
            "give me an overview",
            category: null,
            docId: null,
            docPath,
            topK: 5,
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("document_profile", RetrievalNavigation.ResolveRetriever(match));
        Assert.Equal(docPath, match.DocPath);

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var afterCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM document_profile_search_entries WHERE tenant_id=@tenant AND doc_id=@docId;",
                new { tenant = tenantId, docId });
            Assert.Equal(1, afterCount);
        }
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task SearchDocumentProfileMatchesAsync_refreshes_stale_scoped_profile_search_projection()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            throw new InvalidOperationException("IntegrationPg selected but isolated PostgreSQL fixture was not created.");

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

        var matches = await RetrievalChannels.SearchDocumentProfileMatchesAsync(
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
}
