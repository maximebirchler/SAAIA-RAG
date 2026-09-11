using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ChatStoreEndpointsTests
{
    [Fact]
    public async Task Session_lifecycle_routes_preserve_user_scope_and_emit_audit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        const string userId = "user-alpha";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);

        var createCtx = BuildUserContext(tenantId, actorApiKeyId);
        var createResult = await InvokeEndpointAsync(
            "CreateSessionAsync",
            createCtx,
            ds,
            CreateLogTagLogger(),
            new ChatSessionCreateRequestDto(userId, "  Session title  ", "  desktop-user  "));

        var created = await ExecuteResultAsync<ChatStoreEndpoints.CreateSessionResponse>(createResult, createCtx);
        Assert.Equal("Session title", created.Title);
        Assert.Equal("desktop-user", created.ClientUser);

        var listCtx = BuildUserContext(tenantId, actorApiKeyId);
        var listResult = await InvokeEndpointAsync("ListSessionsAsync", listCtx, ds, userId, 20, 0);
        using var listJson = await ExecuteAnonymousAsync(listResult, listCtx);
        var listed = Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.Equal(created.SessionId, listed.GetProperty("SessionId").GetGuid());
        Assert.Equal("Session title", listed.GetProperty("Title").GetString());

        var getCtx = BuildUserContext(tenantId, actorApiKeyId);
        var getResult = await InvokeEndpointAsync("GetSessionAsync", getCtx, ds, created.SessionId, userId);
        using var getJson = await ExecuteAnonymousAsync(getResult, getCtx);
        Assert.Equal(created.SessionId, getJson.RootElement.GetProperty("SessionId").GetGuid());
        Assert.Equal("Session title", getJson.RootElement.GetProperty("Title").GetString());

        var updateCtx = BuildUserContext(tenantId, actorApiKeyId);
        var updateResult = await InvokeEndpointAsync(
            "UpdateSessionAsync",
            updateCtx,
            ds,
            CreateLogTagLogger(),
            created.SessionId,
            userId,
            new ChatStoreEndpoints.UpdateSessionRequest("  Updated title  "));
        using var updateJson = await ExecuteAnonymousAsync(updateResult, updateCtx);
        Assert.Equal(created.SessionId, updateJson.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal("Updated title", updateJson.RootElement.GetProperty("title").GetString());

        var deleteCtx = BuildUserContext(tenantId, actorApiKeyId);
        var deleteResult = await InvokeEndpointAsync(
            "DeleteSessionAsync",
            deleteCtx,
            ds,
            CreateLogTagLogger(),
            created.SessionId,
            userId);
        using var deleteJson = await ExecuteAnonymousAsync(deleteResult, deleteCtx);
        Assert.Equal(created.SessionId, deleteJson.RootElement.GetProperty("sessionId").GetGuid());

        var verifyCtx = BuildUserContext(tenantId, actorApiKeyId);
        var verifyResult = await InvokeEndpointAsync("GetSessionAsync", verifyCtx, ds, created.SessionId, userId);
        Assert.Equal(StatusCodes.Status404NotFound, await ExecuteStatusCodeAsync(verifyResult, verifyCtx));

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var auditActions = (await conn.QueryAsync<string>(
            """
SELECT action
FROM audit_events
WHERE tenant_id=@tenant
ORDER BY ts ASC;
""",
            new { tenant = tenantId })).ToArray();

        Assert.Contains("chat.session.create", auditActions);
        Assert.Contains("chat.session.update", auditActions);
        Assert.Contains("chat.session.delete", auditActions);
    }

    [Fact]
    public async Task Message_routes_and_cdc_alias_preserve_tracking_fields_and_emit_audit()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        const string userId = "user-beta";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);

        var createSessionCtx = BuildUserContext(tenantId, actorApiKeyId);
        var createSessionResult = await InvokeEndpointAsync(
            "CreateSessionAsync",
            createSessionCtx,
            ds,
            CreateLogTagLogger(),
            new ChatSessionCreateRequestDto(userId, "Messages", null));
        var session = await ExecuteResultAsync<ChatStoreEndpoints.CreateSessionResponse>(createSessionResult, createSessionCtx);

        var addCtx = BuildUserContext(tenantId, actorApiKeyId);
        var addResult = await InvokeEndpointAsync(
            "AddMessageCdcAsync",
            addCtx,
            ds,
            CreateLogTagLogger(),
            session.SessionId,
            (string?)null,
            new ChatMessageCreateRequestDto(
                userId,
                "assistant",
                "  payload generated  ",
                """{"sources":["rag"]}""",
                "  Running  ",
                "  Phase 1  ",
                """{"jobId":"11111111-1111-1111-1111-111111111111","jobType":"ingestion"}"""));

        var created = await ExecuteResultAsync<ChatMessageDto>(addResult, addCtx);
        Assert.Equal("assistant", created.Role);
        Assert.Equal("payload generated", created.Content);
        Assert.Equal("Running", created.StatusNote);
        Assert.Equal("Phase 1", created.ProgressText);
        Assert.Contains("jobType", created.TrackingMetaJson, StringComparison.Ordinal);

        var patchPayload = JsonDocument.Parse(
            """
{
  "userId": "user-beta",
  "statusNote": "Completed",
  "progressText": "Done",
  "trackingMeta": {
    "jobId": "11111111-1111-1111-1111-111111111111",
    "jobType": "ingestion",
    "isTerminal": true
  }
}
""").RootElement.Clone();

        var patchCtx = BuildUserContext(tenantId, actorApiKeyId);
        var patchResult = await InvokeEndpointAsync(
            "PatchMessageAsync",
            patchCtx,
            ds,
            CreateLogTagLogger(),
            created.MessageId,
            (string?)null,
            patchPayload);

        var patched = await ExecuteResultAsync<ChatMessageDto>(patchResult, patchCtx);
        Assert.Equal("Completed", patched.StatusNote);
        Assert.Equal("Done", patched.ProgressText);
        using var trackingMetadata = JsonDocument.Parse(patched.TrackingMetaJson!);
        Assert.True(trackingMetadata.RootElement.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(TimeSpan.Zero, patched.CreatedAt.Offset);
        Assert.Equal(created.MessageId, patched.MessageId);
        Assert.Equal(created.Content, patched.Content);
        using var patchedSources = JsonDocument.Parse(patched.SourcesJson!);
        Assert.Equal("rag", Assert.Single(patchedSources.RootElement.GetProperty("sources").EnumerateArray()).GetString());

        var listCtx = BuildUserContext(tenantId, actorApiKeyId);
        var listResult = await InvokeEndpointAsync("ListMessagesCdcAsync", listCtx, ds, session.SessionId, userId, 20);
        using var listJson = await ExecuteAnonymousAsync(listResult, listCtx);
        var listed = Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.Equal(created.MessageId, listed.GetProperty("MessageId").GetGuid());
        Assert.Equal("Completed", listed.GetProperty("StatusNote").GetString());
        Assert.Equal("Done", listed.GetProperty("ProgressText").GetString());
        Assert.Contains("isTerminal", listed.GetProperty("TrackingMetaJson").GetString(), StringComparison.Ordinal);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var auditActions = (await conn.QueryAsync<string>(
            """
SELECT action
FROM audit_events
WHERE tenant_id=@tenant
ORDER BY ts ASC;
""",
            new { tenant = tenantId })).ToArray();

        Assert.Contains("chat.message.add", auditActions);
        Assert.Contains("chat.message.patch", auditActions);
    }

    [Fact]
    public async Task Tracking_route_projects_live_ingestion_job_state()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        const string userId = "user-gamma";
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO chat_sessions(tenant_id, user_id, session_id, title, client_user, created_at, updated_at, last_message_at)
VALUES(@tenant, @userId, @sessionId, 'Tracking', NULL, now(), now(), now());
""",
                new { tenant = tenantId, userId, sessionId });

            await conn.ExecuteAsync(
                """
INSERT INTO chat_messages(tenant_id, session_id, message_id, role, content, sources_json, status_note, progress_text, tracking_meta_json, created_at)
VALUES(
  @tenant,
  @sessionId,
  @messageId,
  'assistant',
  'tracking',
  NULL,
  NULL,
  NULL,
  @tracking::jsonb,
  now()
);
""",
                new
                {
                    tenant = tenantId,
                    sessionId,
                    messageId,
                    tracking = JsonSerializer.Serialize(new
                    {
                        jobId,
                        jobType = "ingestion",
                        docId,
                        docPath = "ATEX/tracked.pdf"
                    })
                });

            await conn.ExecuteAsync(
                """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, priority, status, attempts, available_at, payload, created_at, started_at)
VALUES(
  @jobId,
  @tenant,
  'upsert',
  'ATEX/tracked.pdf',
  'atex',
  100,
  'queued',
  0,
  now(),
  @payload::jsonb,
  now(),
  now()
);
""",
                new
                {
                    jobId,
                    tenant = tenantId,
                    payload = JsonSerializer.Serialize(new
                    {
                        docId,
                        progress = new
                        {
                            phase = "embedding",
                            current = 2,
                            total = 5,
                            percent = 40
                        }
                    })
                });
        }

        var trackingCtx = BuildUserContext(tenantId, actorApiKeyId);
        var trackingResult = await InvokeEndpointAsync("GetMessageTrackingAsync", trackingCtx, ds, messageId, userId);
        using var trackingJson = await ExecuteAnonymousAsync(trackingResult, trackingCtx);

        Assert.Equal(jobId, trackingJson.RootElement.GetProperty("jobId").GetGuid());
        Assert.Equal("ingestion", trackingJson.RootElement.GetProperty("type").GetString());
        Assert.Equal("upsert", trackingJson.RootElement.GetProperty("jobType").GetString());
        Assert.Equal("running", trackingJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("embedding", trackingJson.RootElement.GetProperty("progressPhase").GetString());
        Assert.Equal(2, trackingJson.RootElement.GetProperty("progressCurrent").GetInt32());
        Assert.Equal(5, trackingJson.RootElement.GetProperty("progressTotal").GetInt32());
        Assert.Equal(40, trackingJson.RootElement.GetProperty("progressPercent").GetInt32());
        Assert.Equal("ATEX/tracked.pdf", trackingJson.RootElement.GetProperty("docPath").GetString());
        Assert.False(trackingJson.RootElement.GetProperty("isTerminal").GetBoolean());
    }

    [Fact]
    public async Task Tracking_route_falls_back_to_message_metadata_when_job_is_gone()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var actorApiKeyId = Guid.NewGuid();
        const string userId = "user-delta";
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await SeedTenantAsync(ds, tenantId);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO chat_sessions(tenant_id, user_id, session_id, title, client_user, created_at, updated_at, last_message_at)
VALUES(@tenant, @userId, @sessionId, 'Tracking fallback', NULL, now(), now(), now());
""",
                new { tenant = tenantId, userId, sessionId });

            await conn.ExecuteAsync(
                """
INSERT INTO chat_messages(tenant_id, session_id, message_id, role, content, sources_json, status_note, progress_text, tracking_meta_json, created_at)
VALUES(
  @tenant,
  @sessionId,
  @messageId,
  'assistant',
  'tracking fallback',
  NULL,
  'Completed',
  'Done',
  @tracking::jsonb,
  now()
);
""",
                new
                {
                    tenant = tenantId,
                    sessionId,
                    messageId,
                    tracking = JsonSerializer.Serialize(new
                    {
                        jobId,
                        jobType = "summary",
                        docId,
                        docPath = "ATEX/fallback.pdf",
                        lastKnownStatus = "done",
                        lastKnownProgressPhase = "finalize",
                        lastKnownProgressCurrent = 5,
                        lastKnownProgressTotal = 5,
                        lastKnownProgressPercent = 100,
                        startedAtUtc = "2026-04-20T10:00:00Z",
                        lastSnapshotAtUtc = "2026-04-20T10:05:00Z",
                        isTerminal = true
                    })
                });
        }

        var trackingCtx = BuildUserContext(tenantId, actorApiKeyId);
        var trackingResult = await InvokeEndpointAsync("GetMessageTrackingAsync", trackingCtx, ds, messageId, userId);
        using var trackingJson = await ExecuteAnonymousAsync(trackingResult, trackingCtx);

        Assert.Equal(jobId, trackingJson.RootElement.GetProperty("jobId").GetGuid());
        Assert.Equal("summary", trackingJson.RootElement.GetProperty("type").GetString());
        Assert.Equal("summary", trackingJson.RootElement.GetProperty("jobType").GetString());
        Assert.Equal("done", trackingJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("ATEX/fallback.pdf", trackingJson.RootElement.GetProperty("docPath").GetString());
        Assert.Equal("finalize", trackingJson.RootElement.GetProperty("progressPhase").GetString());
        Assert.Equal(5, trackingJson.RootElement.GetProperty("progressCurrent").GetInt32());
        Assert.Equal(5, trackingJson.RootElement.GetProperty("progressTotal").GetInt32());
        Assert.Equal(100, trackingJson.RootElement.GetProperty("progressPercent").GetInt32());
        Assert.True(trackingJson.RootElement.GetProperty("isTerminal").GetBoolean());
    }

    private static object CreateLogTagLogger()
    {
        var logTagType = typeof(ChatStoreEndpoints).GetNestedType("LogTag", BindingFlags.NonPublic);
        Assert.NotNull(logTagType);
        var loggerType = typeof(NullLogger<>).MakeGenericType(logTagType!);
        var instance = loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        Assert.NotNull(instance);
        return instance!;
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

    private static DefaultHttpContext BuildUserContext(Guid tenantId, Guid actorApiKeyId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = false;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = actorApiKeyId;
        return ctx;
    }

    private static async Task<IResult> InvokeEndpointAsync(string methodName, params object?[] args)
    {
        var method = typeof(ChatStoreEndpoints).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
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

    private static async Task<int> ExecuteStatusCodeAsync(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
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
            var databaseName = $"saaia_chat_{Guid.NewGuid():N}";

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
