using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminKeysAndAuditEndpointsTests
{
    [Fact]
    public async Task Create_key_list_keys_and_admin_audit_surfaces_stay_consistent()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);

        var createCtx = BuildAdminContext(tenantId, actorApiKeyId);
        var createResult = await InvokeEndpointAsync(
            typeof(AdminKeysEndpoints),
            "CreateKeyAsync",
            createCtx,
            ds,
            Options.Create(new ApiKeyAuthOptions { Pepper = "pepper-test" }),
            new AdminKeysEndpoints.CreateKeyRequest("  Ops Admin  ", true));

        var created = await ExecuteResultAsync<AdminKeysEndpoints.CreateKeyResponse>(createResult, createCtx);

        Assert.Equal("Ops Admin", created.Label);
        Assert.True(created.IsAdmin);
        Assert.StartsWith("saaia_", created.ApiKey, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, created.ApiKeyId);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();

            var row = await conn.QuerySingleAsync<(string label, bool is_admin, string key_prefix, DateTimeOffset? revoked_at)>(
                """
SELECT label, is_admin, key_prefix, revoked_at
FROM api_keys
WHERE tenant_id=@tenant AND api_key_id=@id;
""",
                new { tenant = tenantId, id = created.ApiKeyId });

            Assert.Equal("Ops Admin", row.label);
            Assert.True(row.is_admin);
            Assert.Equal(ApiKeyAuth.Prefix(created.ApiKey), row.key_prefix);
            Assert.Null(row.revoked_at);
        }

        var listCtx = BuildAdminContext(tenantId, actorApiKeyId);
        var listResult = await InvokeEndpointAsync(typeof(AdminKeysEndpoints), "ListKeysAsync", listCtx, ds);
        using var listedJson = await ExecuteAnonymousAsync(listResult, listCtx);
        var listed = Assert.Single(listedJson.RootElement.EnumerateArray());

        Assert.Equal(created.ApiKeyId, listed.GetProperty("ApiKeyId").GetGuid());
        Assert.Equal("Ops Admin", listed.GetProperty("Label").GetString());
        Assert.True(listed.GetProperty("IsAdmin").GetBoolean());
        Assert.Equal(ApiKeyAuth.Prefix(created.ApiKey), listed.GetProperty("KeyPrefix").GetString());
        Assert.True(listed.TryGetProperty("CreatedAt", out _));
        Assert.True(listed.TryGetProperty("RevokedAt", out _));
        Assert.True(listed.TryGetProperty("LastUsedAt", out _));

        var auditListCtx = BuildAdminContext(tenantId, actorApiKeyId);
        var auditListResult = await InvokeEndpointAsync(
            typeof(AdminAuditEndpoints),
            "ListAsync",
            auditListCtx,
            ds,
            "admin.key.create",
            created.ApiKeyId.ToString(),
            actorApiKeyId,
            true,
            null,
            null,
            50,
            0);

        using var auditListJson = await ExecuteAnonymousAsync(auditListResult, auditListCtx);
        Assert.Equal(1, auditListJson.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(50, auditListJson.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(0, auditListJson.RootElement.GetProperty("offset").GetInt32());

        var auditItem = Assert.Single(auditListJson.RootElement.GetProperty("items").EnumerateArray());
        var auditId = auditItem.GetProperty("auditId").GetGuid();
        Assert.Equal("admin.key.create", auditItem.GetProperty("action").GetString());
        Assert.Equal(created.ApiKeyId.ToString(), auditItem.GetProperty("target").GetString());
        Assert.Equal(actorApiKeyId, auditItem.GetProperty("actorApiKeyId").GetGuid());
        Assert.True(auditItem.GetProperty("actorIsAdmin").GetBoolean());

        var auditGetCtx = BuildAdminContext(tenantId, actorApiKeyId);
        var auditGetResult = await InvokeEndpointAsync(typeof(AdminAuditEndpoints), "GetAsync", auditGetCtx, ds, auditId);
        using var auditGetJson = await ExecuteAnonymousAsync(auditGetResult, auditGetCtx);
        Assert.Equal(auditId, auditGetJson.RootElement.GetProperty("auditId").GetGuid());
        Assert.Equal("admin.key.create", auditGetJson.RootElement.GetProperty("action").GetString());
        Assert.Contains("Ops Admin", auditGetJson.RootElement.GetProperty("payloadJson").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_key_revokes_old_key_creates_new_key_and_records_audit_event()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        var oldApiKeyId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);
        await SeedApiKeyAsync(ds, tenantId, oldApiKeyId, "rotating-key", "Rotating Key", isAdmin: true, pepper: "pepper-test");

        var rotateCtx = BuildAdminContext(tenantId, actorApiKeyId);
        var rotateResult = await InvokeEndpointAsync(
            typeof(AdminKeysEndpoints),
            "RotateKeyAsync",
            rotateCtx,
            ds,
            Options.Create(new ApiKeyAuthOptions { Pepper = "pepper-test" }),
            oldApiKeyId);

        using var rotateJson = await ExecuteAnonymousAsync(rotateResult, rotateCtx);
        var newApiKeyId = rotateJson.RootElement.GetProperty("newApiKeyId").GetGuid();
        var newApiKey = rotateJson.RootElement.GetProperty("apiKey").GetString();

        Assert.Equal(oldApiKeyId, rotateJson.RootElement.GetProperty("oldApiKeyId").GetGuid());
        Assert.Equal("Rotating Key", rotateJson.RootElement.GetProperty("label").GetString());
        Assert.True(rotateJson.RootElement.GetProperty("isAdmin").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(newApiKey));
        Assert.NotEqual(oldApiKeyId, newApiKeyId);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();

            var keys = (await conn.QueryAsync<(Guid api_key_id, string label, bool is_admin, DateTimeOffset? revoked_at)>(
                """
SELECT api_key_id, label, is_admin, revoked_at
FROM api_keys
WHERE tenant_id=@tenant
ORDER BY created_at;
""",
                new { tenant = tenantId })).ToArray();

            Assert.Equal(2, keys.Length);
            Assert.Contains(keys, item => item.api_key_id == oldApiKeyId && item.revoked_at is not null);
            Assert.Contains(keys, item => item.api_key_id == newApiKeyId && item.revoked_at is null && item.label == "Rotating Key" && item.is_admin);

            var audit = await conn.QuerySingleAsync<(string action, string target, string payload_json)>(
                """
SELECT action, target, payload_json
FROM audit_events
WHERE tenant_id=@tenant AND action='admin.key.rotate'
ORDER BY ts DESC
LIMIT 1;
""",
                new { tenant = tenantId });

            Assert.Equal("admin.key.rotate", audit.action);
            Assert.Equal(oldApiKeyId.ToString(), audit.target);
            Assert.Contains(newApiKeyId.ToString(), audit.payload_json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rotate_key_rolls_back_when_audit_write_fails()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        var oldApiKeyId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);
        await SeedApiKeyAsync(ds, tenantId, oldApiKeyId, "rollback-key", "Rollback Key", isAdmin: true, pepper: "pepper-test");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("DROP TABLE audit_events;");
        }

        var rotateCtx = BuildAdminContext(tenantId, actorApiKeyId);
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeEndpointAsync(
            typeof(AdminKeysEndpoints),
            "RotateKeyAsync",
            rotateCtx,
            ds,
            Options.Create(new ApiKeyAuthOptions { Pepper = "pepper-test" }),
            oldApiKeyId));

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();

            var keys = (await conn.QueryAsync<(Guid api_key_id, DateTimeOffset? revoked_at)>(
                """
SELECT api_key_id, revoked_at
FROM api_keys
WHERE tenant_id=@tenant
ORDER BY created_at;
""",
                new { tenant = tenantId })).ToArray();

            Assert.Single(keys);
            Assert.Equal(oldApiKeyId, keys[0].api_key_id);
            Assert.Null(keys[0].revoked_at);
        }
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

    private static async Task SeedApiKeyAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid apiKeyId,
        string plainApiKey,
        string label,
        bool isAdmin,
        string pepper)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin, created_at, revoked_at, last_used_at)
VALUES(@id, @tenant, @prefix, @hash, @label, @isAdmin, now(), NULL, NULL);
""",
            new
            {
                id = apiKeyId,
                tenant = tenantId,
                prefix = ApiKeyAuth.Prefix(plainApiKey),
                hash = ApiKeyAuth.Sha256Bytes(plainApiKey, pepper),
                label,
                isAdmin
            });
    }

    private static DefaultHttpContext BuildAdminContext(Guid tenantId, Guid actorApiKeyId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = actorApiKeyId;
        return ctx;
    }

    private static async Task<IResult> InvokeEndpointAsync(Type declaringType, string methodName, params object?[] args)
    {
        var method = declaringType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(null, args) as Task<IResult>;
        Assert.NotNull(task);
        return await task!;
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
            var databaseName = $"saaia_admin_{Guid.NewGuid():N}";

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
