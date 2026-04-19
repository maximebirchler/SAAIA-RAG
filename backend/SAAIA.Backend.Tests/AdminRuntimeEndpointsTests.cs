using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminRuntimeEndpointsTests
{
    [Fact]
    public async Task CatalogAsync_exposes_runtime_catalog_and_default_profile()
    {
        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CatalogAsync(
            ctx,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCatalogResponseDto>(result, ctx);

        Assert.Equal("v3.0", payload.CdcAlignment);
        Assert.Contains(payload.Runtimes, item => item.Key == "qdrant");
        Assert.Contains(payload.Runtimes, item => item.Key == "tei-embeddings");
        Assert.Contains(payload.WarmupProfiles, item => item.Key == "default-local" && item.PassCount == 3);
        Assert.Contains(payload.Capabilities, item => item.Key == "core.retrieval" && item.Implemented);
        Assert.Contains(payload.Capabilities, item => item.Key == "capability_a.corpus_enrichment" && !item.Implemented);
    }

    [Fact]
    public async Task RequalifyAsync_qualifies_core_retrieval_and_persists_state_and_warmup()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.RequalifyAsync(
            ctx,
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"));

        var payload = await ExecuteResultAsync<AdminRuntimeRequalifyResponseDto>(result, ctx);
        var state = Assert.Single(payload.Items);

        Assert.Equal("core.retrieval", state.Key);
        Assert.True(state.Installed);
        Assert.True(state.Configured);
        Assert.True(state.Healthy);
        Assert.True(state.Qualified);
        Assert.True(state.Authorized);
        Assert.True(state.Selected);
        Assert.Equal(3, state.PassCount);
        Assert.Single(payload.WarmupResults);
        Assert.True(payload.WarmupResults[0].Passed);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var persisted = await conn.QuerySingleAsync<(bool qualified, bool selected, int pass_count)>(
            "SELECT qualified, selected, pass_count FROM runtime_capability_state WHERE capability_key='core.retrieval';");
        Assert.True(persisted.qualified);
        Assert.True(persisted.selected);
        Assert.Equal(3, persisted.pass_count);

        var warmupCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM runtime_warmup_results WHERE capability_key='core.retrieval';");
        Assert.Equal(1, warmupCount);
    }

    [Fact]
    public async Task CapabilitiesAsync_returns_persisted_core_state_and_default_optional_capabilities()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.CapabilitiesAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            new StubHostEnvironment());

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilitiesResponseDto>(result, ctx);

        Assert.Equal(4, payload.Items.Count);
        Assert.Contains(payload.Items, item => item.Key == "core.retrieval" && item.Qualified);
        Assert.Contains(payload.Items, item => item.Key == "capability_a.corpus_enrichment" && !item.Implemented && !item.Qualified);
        Assert.Contains(payload.WarmupResults, item => item.CapabilityKey == "core.retrieval" && item.Passed);
    }

    [Fact]
    public async Task UpdateSelectionAsync_can_deselect_qualified_core_retrieval()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(Selected: false));

        var payload = await ExecuteResultAsync<AdminRuntimeCapabilityStateDto>(result, ctx);
        Assert.False(payload.Selected);
        Assert.True(payload.Authorized);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var persistedSelected = await conn.ExecuteScalarAsync<bool>(
            "SELECT selected FROM runtime_capability_state WHERE capability_key='core.retrieval';");
        Assert.False(persistedSelected);
    }

    [Fact]
    public async Task UpdateSelectionAsync_rejects_selecting_unqualified_capability()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var ctx = BuildAdminContext();
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var result = await AdminRuntimeEndpoints.UpdateSelectionAsync(
            ctx,
            ds,
            Options.Create(new RuntimeGovernanceOptions()),
            Options.Create(CreateRagOptions()),
            "core.retrieval",
            new AdminRuntimeCapabilitySelectionRequestDto(Authorized: true, Selected: true));

        var payload = await ExecuteAnonymousAsync(result, ctx);
        Assert.Equal("capability must be qualified before it can be authorized", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WarmupResultsAsync_returns_recent_runtime_warmup_rows()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await RuntimeGovernanceService.RequalifyAsync(
            ds,
            new RuntimeGovernanceHttpClientFactory(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            new AdminRuntimeRequalifyRequestDto(CapabilityKey: "core.retrieval"),
            CancellationToken.None);

        var ctx = BuildAdminContext();
        var result = await AdminRuntimeEndpoints.WarmupResultsAsync(
            ctx,
            ds,
            new StubHostEnvironment(),
            "core.retrieval",
            10);

        var payload = await ExecuteResultAsync<AdminRuntimeWarmupResultsResponseDto>(result, ctx);
        Assert.Single(payload.Items);
        Assert.Equal("core.retrieval", payload.Items[0].CapabilityKey);
        Assert.True(payload.Items[0].Passed);
    }

    private static RagOptions CreateRagOptions()
        => new()
        {
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = "knowledge_base",
            EmbeddingsBaseUrl = "http://tei.test/",
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EnableRerank = false
        };

    private static DefaultHttpContext BuildAdminContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return ctx;
    }

    private static async Task<T> ExecuteResultAsync<T>(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<JsonDocument> ExecuteAnonymousAsync(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    private sealed class RuntimeGovernanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RuntimeGovernanceHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class RuntimeGovernanceHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get && path.StartsWith("/collections/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""{ "result": { "status": "green", "points_count": 0 } }"""));
            }

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

            if (path.EndsWith("/rerank", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "results": [
                    { "index": 0, "score": 0.9 },
                    { "index": 1, "score": 0.8 }
                  ]
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
            var databaseName = $"saaia_rt_{Guid.NewGuid():N}";

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
