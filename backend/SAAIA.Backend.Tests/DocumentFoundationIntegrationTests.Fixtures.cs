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
            CancellationToken.None, leaseOwner: "test-worker");

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
            CancellationToken.None, leaseOwner: "test-worker");

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
            }))
            { BaseAddress = new Uri("http://stub.test/") };
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
            var migrationsDir = TestRepositoryPaths.MigrationsDirectory;
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
