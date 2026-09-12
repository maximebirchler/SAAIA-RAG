using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagLiveSummaryMultilingualBackendTests
{
    [Fact]
    public void ResolveDocumentLanguageInfo_preserves_non_english_language_metadata_for_ui_language_clients()
    {
        var docId = Guid.NewGuid();
        var languages = new Dictionary<string, RagDocumentLanguageInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [docId.ToString()] = new("de", "de")
        };

        var resolved = InvokeResolveDocumentLanguageInfo(
            CreateMatch(docId.ToString("N"), "Knowledge/maschinenhandbuch.pdf"),
            languages);

        Assert.Equal("de", resolved.DocLanguage);
        Assert.Equal("de", resolved.ProfileLanguage);
        Assert.NotEqual("en", resolved.DocLanguage);
    }

    [Fact]
    public async Task Rag_search_response_preserves_document_and_profile_language_for_live_summary_consumers()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("f8b1e3aa-7700-4d1c-8e21-dc83b5adf001");
        var docId = Guid.Parse("f8b1e3aa-7700-4d1c-8e21-dc83b5adf101");
        const string query = "SAAIA-LIVE-4242";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedExactMatchDocumentAsync(
            ds,
            tenantId,
            docId,
            docPath: "Knowledge/maschinenhandbuch.pdf",
            profileLanguage: "DE",
            summaryLanguage: "en",
            exactText: query);

        var ctx = BuildContext(tenantId);
        var result = await InvokeSearchAsync(
            ctx,
            ds,
            new RagSearchRequestDto(
                Query: query,
                TopK: 1,
                DocId: docId.ToString(),
                Mode: "focused"));

        using var json = await ExecuteJsonAsync(result, ctx);
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(docId.ToString(), item.GetProperty("docId").GetString());
        Assert.Equal("de", item.GetProperty("docLanguage").GetString());
        Assert.Equal("de", item.GetProperty("profileLanguage").GetString());
        Assert.NotEqual("en", item.GetProperty("docLanguage").GetString());
    }

    [Fact]
    public async Task LoadRagDocumentLanguagesAsync_keeps_profile_language_and_uses_summary_language_only_when_profile_unknown()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("f8b1e3aa-7700-4d1c-8e21-dc83b5adf002");
        var profileDocId = Guid.Parse("f8b1e3aa-7700-4d1c-8e21-dc83b5adf201");
        var summaryDocId = Guid.Parse("f8b1e3aa-7700-4d1c-8e21-dc83b5adf202");

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedDocumentLanguageMetadataAsync(
            ds,
            tenantId,
            profileDocId,
            docPath: "Knowledge/handleiding.pdf",
            profileLanguage: "NL_BE",
            summaryLanguage: "en");
        await SeedDocumentLanguageMetadataAsync(
            ds,
            tenantId,
            summaryDocId,
            docPath: "Knowledge/manuale.pdf",
            profileLanguage: "und",
            summaryLanguage: "IT_it");

        var languages = await RagEndpoints.LoadRagDocumentLanguagesAsync(
            ds,
            tenantId,
            [
                CreateMatch(profileDocId.ToString("N"), "Knowledge/handleiding.pdf"),
                CreateMatch(summaryDocId.ToString(), "Knowledge/manuale.pdf")
            ],
            CancellationToken.None);

        var profileLanguage = Assert.Contains(profileDocId.ToString(), languages);
        Assert.Equal("nl-be", profileLanguage.DocLanguage);
        Assert.Equal("nl-be", profileLanguage.ProfileLanguage);

        var summaryLanguage = Assert.Contains(summaryDocId.ToString(), languages);
        Assert.Equal("it-it", summaryLanguage.DocLanguage);
        Assert.Null(summaryLanguage.ProfileLanguage);

        var resolvedFromCompactMatch = InvokeResolveDocumentLanguageInfo(
            CreateMatch(profileDocId.ToString("N"), "Knowledge/handleiding.pdf"),
            languages);
        Assert.Equal("nl-be", resolvedFromCompactMatch.DocLanguage);
        Assert.Equal("nl-be", resolvedFromCompactMatch.ProfileLanguage);
    }

    [Theory]
    [InlineData("samenvatting doel hoofdsecties")]
    [InlineData("riassunto scopo sezioni principali")]
    [InlineData("zusammenfassung zweck hauptabschnitte")]
    public void ExpandRetrievalQuery_preserves_non_english_live_summary_queries_without_english_translation(
        string query)
    {
        var expanded = RagEndpoints.ExpandRetrievalQuery(query, category: null);

        Assert.StartsWith(query, expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("summary purpose main sections", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("summary", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("purpose", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("main sections", expanded, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> InvokeSearchAsync(
        DefaultHttpContext ctx,
        NpgsqlDataSource ds,
        RagSearchRequestDto request)
    {
        var method = typeof(RagEndpoints).GetMethod(
            "SearchAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ragOptions = Options.Create(new RagOptions { DefaultTopK = 5, MaxTopK = 20 });
        var task = method!.Invoke(
            null,
            [
                ctx,
                ds,
                ragOptions,
                new EmptyRetrievalHttpClientFactory(),
                new RagSearchBulkhead(ragOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<RagSearchBulkhead>.Instance),
                new TeiWorkloadGovernor(),
                request
            ]) as Task<IResult>;
        Assert.NotNull(task);
        return await task!;
    }

    private static RagDocumentLanguageInfo InvokeResolveDocumentLanguageInfo(
        RagMatch match,
        IReadOnlyDictionary<string, RagDocumentLanguageInfo> languagesByDocId)
    {
        var method = typeof(RagEndpoints).GetMethod(
            "ResolveDocumentLanguageInfo",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [match, languagesByDocId]);
        return Assert.IsType<RagDocumentLanguageInfo>(result);
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    private static DefaultHttpContext BuildContext(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost");
        ctx.Response.Body = new MemoryStream();
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.TraceIdentifier = "rag-live-summary-language-test";
        return ctx;
    }

    private static async Task SeedExactMatchDocumentAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid docId,
        string docPath,
        string profileLanguage,
        string summaryLanguage,
        string exactText)
    {
        var revisionId = await SeedDocumentLanguageMetadataAsync(
            ds,
            tenantId,
            docId,
            docPath,
            profileLanguage,
            summaryLanguage);

        await using var conn = await ds.OpenConnectionAsync();
        var unitId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
INSERT INTO document_units(
  unit_id, tenant_id, revision_id, ordinal,
  page_start, page_end, text_content,
  char_count, token_count, checksum, metadata
)
VALUES(
  @unitId, @tenant, @revisionId, 0,
  1, 1, @exactText,
  length(@exactText), 1, decode('AA', 'hex'), '{}'::jsonb
);

INSERT INTO retrieval_chunks(
  retrieval_chunk_id, tenant_id, revision_id, unit_id, chunk_index,
  page_start, page_end, text_content,
  token_count, checksum, metadata
)
VALUES(
  @chunkId, @tenant, @revisionId, @unitId, 0,
  1, 1, @exactText,
  1, decode('BB', 'hex'), '{"chunkType":"unit_exact_v1"}'::jsonb
);

INSERT INTO exact_match_entries(
  exact_match_entry_id, tenant_id, revision_id, unit_id, entry_index,
  page_start, page_end, text_content, normalized_text,
  char_count, token_count, checksum, metadata
)
VALUES(
  @entryId, @tenant, @revisionId, @unitId, 0,
  1, 1, @exactText, @normalizedText,
  length(@exactText), 1, decode('CC', 'hex'), '{"kind":"standard_ref"}'::jsonb
);
""",
            new
            {
                entryId = Guid.NewGuid(),
                unitId,
                chunkId = Guid.NewGuid(),
                tenant = tenantId,
                revisionId,
                exactText,
                normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(exactText)
            });
    }

    private static async Task<Guid> SeedDocumentLanguageMetadataAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid docId,
        string docPath,
        string profileLanguage,
        string summaryLanguage)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var revisionId = Guid.NewGuid();
        var docName = Path.GetFileName(docPath);

        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name, is_active)
VALUES(@tenant, 'RAG language test tenant', true)
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  content_hash, file_size, file_mtime, page_count,
  ingestion_version, indexed_version, last_ingested_at, updated_at
)
VALUES(
  @tenant, @docId, @docPath, @docName, 'knowledge', 'indexed',
  decode('AABBCCDDEEFF00112233445566778899', 'hex'), 4242, now(), 3,
  7, 7, now(), now()
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
  ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, @docPath, decode('AABBCCDDEEFF00112233445566778899', 'hex'), 4242, now(),
  7, 7, now()
);

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version,
  language, summary_text, search_text, token_count, checksum
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1',
  @profileLanguage, 'Document language profile summary.', 'Document language profile search text.', 5, decode('BB', 'hex')
);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta
)
SELECT
  @tenant,
  @docId,
  'medium',
  @summaryLanguage,
  saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version),
  'Stored summary in a different language.',
  '{}'::jsonb
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId;
""",
            new
            {
                tenant = tenantId,
                docId,
                docPath,
                docName,
                revisionId,
                profileId = Guid.NewGuid(),
                profileLanguage,
                summaryLanguage
            });

        return revisionId;
    }

    private static RagMatch CreateMatch(string docId, string docPath)
        => new(
            Score: 1.0,
            DocId: docId,
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "chunk-1",
            ChunkIndex: 0,
            Text: "language-bearing retrieval chunk",
            IngestionVersion: 7,
            HashDoc: "hash",
            EmbedText: "language-bearing retrieval chunk",
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Language",
            HeadingPath: "Language",
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

    private sealed class EmptyRetrievalHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new EmptyRetrievalHttpMessageHandler())
            {
                BaseAddress = new Uri("http://retrieval.test/")
            };
    }

    private sealed class EmptyRetrievalHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.EndsWith(
                "/v1/embeddings",
                StringComparison.OrdinalIgnoreCase)
                ? "{\"data\":[{\"embedding\":[0.1,0.2,0.3,0.4]}]}"
                : "{\"result\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
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
            var databaseName = $"saaia_rag_lang_{Guid.NewGuid():N}";

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
            var migrationsDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SAAIA.Backend",
                "Db",
                "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, CancellationToken.None);
            return db;
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
            }
        }
    }
}
