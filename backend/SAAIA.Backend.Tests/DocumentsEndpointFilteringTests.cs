using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentsEndpointFilteringTests
{
    [Fact]
    public async Task Admin_documents_default_excludes_tombstones_but_status_filters_can_include_them()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await SeedTenantAsync(ds, tenantId);
        await SeedDocumentAsync(ds, tenantId, "Active/Indexed.pdf", "indexed");
        await SeedDocumentAsync(ds, tenantId, "Active/Pending.pdf", "pending");
        await SeedDocumentAsync(ds, tenantId, "Gone/Missing.pdf", "missing");
        await SeedDocumentAsync(ds, tenantId, "Gone/Deleted.pdf", "deleted");

        using var defaultJson = await InvokeDocumentsListAsync(ds, tenantId, status: null);
        var defaultStatuses = ReadItemStrings(defaultJson.RootElement, "Status", "status").ToArray();

        Assert.Contains("indexed", defaultStatuses);
        Assert.Contains("pending", defaultStatuses);
        Assert.DoesNotContain("missing", defaultStatuses);
        Assert.DoesNotContain("deleted", defaultStatuses);

        using var deletedJson = await InvokeDocumentsListAsync(ds, tenantId, status: "deleted");
        Assert.Equal(["deleted"], ReadItemStrings(deletedJson.RootElement, "Status", "status").ToArray());

        using var missingJson = await InvokeDocumentsListAsync(ds, tenantId, status: "missing");
        Assert.Equal(["missing"], ReadItemStrings(missingJson.RootElement, "Status", "status").ToArray());

        using var allJson = await InvokeDocumentsListAsync(ds, tenantId, status: "all");
        var allStatuses = ReadItemStrings(allJson.RootElement, "Status", "status").OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(["deleted", "indexed", "missing", "pending"], allStatuses);
    }

    [Fact]
    public async Task Catalog_documents_respects_category_path_query()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        await SeedTenantAsync(ds, tenantId);
        await SeedDocumentAsync(ds, tenantId, "Alpha/Sub/Included.pdf", "indexed");
        await SeedDocumentAsync(ds, tenantId, "Alpha/Other/Excluded.pdf", "indexed");
        await SeedDocumentAsync(ds, tenantId, "Beta/Excluded.pdf", "indexed");
        await SeedDocumentAsync(ds, tenantId, "Alpha/Sub/Deleted.pdf", "deleted");

        var ctx = BuildContext(tenantId, isAdmin: false);
        var result = await InvokeEndpointAsync(
            "CatalogDocumentsAsync",
            ctx,
            ds,
            null,
            "Alpha/Sub",
            null,
            null,
            "name_asc",
            50,
            null,
            null);

        using var json = await ExecuteJsonAsync(result, ctx);
        var items = json.RootElement.GetProperty("value").EnumerateArray().ToArray();

        var item = Assert.Single(items);
        Assert.Equal("Alpha/Sub/Included.pdf", item.GetProperty("docPath").GetString());
        Assert.Equal("Alpha/Sub", item.GetProperty("categoryPath").GetString());
    }

    private static async Task<JsonDocument> InvokeDocumentsListAsync(NpgsqlDataSource ds, Guid tenantId, string? status)
    {
        var ctx = BuildContext(tenantId, isAdmin: true);
        var result = await InvokeEndpointAsync(
            "ListAsync",
            ctx,
            ds,
            null,
            status,
            null,
            50,
            0);

        return await ExecuteJsonAsync(result, ctx);
    }

    private static async Task SeedTenantAsync(NpgsqlDataSource ds, Guid tenantId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name, is_active)
VALUES(@tenant, 'Test Tenant', true)
ON CONFLICT (tenant_id) DO NOTHING;
""",
            new { tenant = tenantId });
    }

    private static async Task SeedDocumentAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, string status)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var docId = Guid.NewGuid();
        var indexedVersion = string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var ingestionVersion = indexedVersion;
        await conn.ExecuteAsync(
            """
INSERT INTO documents(
    tenant_id, doc_id, doc_path, doc_name, category, status,
    file_size, page_count, last_ingested_at, last_seen_at,
    ingestion_version, indexed_version, updated_at
)
VALUES(
    @tenant, @docId, @docPath, @docName, @category, @status,
    123, 1, now(), now(),
    @ingestionVersion, @indexedVersion, now()
);
""",
            new
            {
                tenant = tenantId,
                docId,
                docPath,
                docName = Path.GetFileName(docPath),
                category = docPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First().ToLowerInvariant(),
                status,
                ingestionVersion,
                indexedVersion
            });
    }

    private static DefaultHttpContext BuildContext(Guid tenantId, bool isAdmin)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost");
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        if (isAdmin)
        {
            ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
            ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = Guid.NewGuid();
        }

        return ctx;
    }

    private static async Task<IResult> InvokeEndpointAsync(string methodName, params object?[] args)
    {
        var method = typeof(DocumentsEndpoints).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(null, args) as Task<IResult>;
        Assert.NotNull(task);
        return await task!;
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    private static IEnumerable<string> ReadItemStrings(JsonElement root, params string[] propertyNames)
    {
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            foreach (var propertyName in propertyNames)
            {
                if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text!;
                    break;
                }
            }
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
            var databaseName = $"saaia_documents_{Guid.NewGuid():N}";

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
