using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisEndpointsTests
{
    [Fact]
    public void Validator_accepts_the_server_safe_A756_contract()
    {
        var validation = AdvancedAnalysisEndpoints.ValidateCreateRequest(
            BuildRequest());

        Assert.True(validation.IsValid);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("revalidation")]
    [InlineData("external_content")]
    [InlineData("external_metadata")]
    [InlineData("unsupported_schema")]
    [InlineData("missing_identity")]
    public void Validator_rejects_unsafe_or_unverifiable_handoffs(string mutation)
    {
        var request = BuildRequest(mutation);

        var validation = AdvancedAnalysisEndpoints.ValidateCreateRequest(request);

        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.ErrorCode!);
    }

    [Fact]
    public async Task Disabled_license_rejects_before_database_access()
    {
        var context = BuildContext(Guid.NewGuid());

        var result = await AdvancedAnalysisEndpoints.CreateAsync(
            context,
            dataSource: null!,
            Options.Create(new LicenseOptions
            {
                AdvancedAnalysisEnabled = false
            }),
            Options.Create(new AdvancedAnalysisOptions()),
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            BuildRequest());

        Assert.Equal(
            StatusCodes.Status403Forbidden,
            await ExecuteStatusCodeAsync(result, context));
    }

    [Fact]
    public async Task Durable_job_is_idempotent_user_scoped_and_cancelable()
    {
        await using var database = await PostgresIntegrationDb.CreateAsync();
        if (database is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var otherTenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var actorApiKeyId = Guid.NewGuid();
        const string userId = "user-alpha";
        var request = BuildRequest();
        await using var dataSource = NpgsqlDataSource.Create(database!.ConnectionString);
        await SeedSessionAsync(dataSource, tenantId, request.SessionId, userId);
        await SeedTenantAsync(dataSource, otherTenantId);
        var license = Options.Create(new LicenseOptions
        {
            AdvancedAnalysisEnabled = true
        });
        var options = Options.Create(new AdvancedAnalysisOptions
        {
            RetentionDays = 14,
            MaximumQueuedJobsPerUser = 4
        });

        var createContext = BuildContext(tenantId, actorApiKeyId);
        var createdResult = await AdvancedAnalysisEndpoints.CreateAsync(
            createContext,
            dataSource,
            license,
            options,
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            request);
        var created = await ExecuteJsonAsync(createdResult, createContext);
        Assert.Equal(StatusCodes.Status202Accepted, createContext.Response.StatusCode);
        var jobId = created.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal("queued", created.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, created.RootElement.GetProperty("revision").GetInt32());

        var replayContext = BuildContext(tenantId, actorApiKeyId);
        var replayResult = await AdvancedAnalysisEndpoints.CreateAsync(
            replayContext,
            dataSource,
            license,
            options,
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            request);
        var replayed = await ExecuteJsonAsync(replayResult, replayContext);
        Assert.Equal(StatusCodes.Status200OK, replayContext.Response.StatusCode);
        Assert.Equal(jobId, replayed.RootElement.GetProperty("jobId").GetGuid());

        var wrongUserContext = BuildContext(tenantId, actorApiKeyId);
        var wrongUserResult = await AdvancedAnalysisEndpoints.GetAsync(
            wrongUserContext,
            dataSource,
            jobId,
            "user-beta");
        Assert.Equal(
            StatusCodes.Status404NotFound,
            await ExecuteStatusCodeAsync(wrongUserResult, wrongUserContext));

        var wrongTenantContext = BuildContext(otherTenantId, actorApiKeyId);
        var wrongTenantResult = await AdvancedAnalysisEndpoints.GetAsync(
            wrongTenantContext,
            dataSource,
            jobId,
            userId);
        Assert.Equal(
            StatusCodes.Status404NotFound,
            await ExecuteStatusCodeAsync(wrongTenantResult, wrongTenantContext));

        var cancelContext = BuildContext(tenantId, actorApiKeyId);
        var canceledResult = await AdvancedAnalysisEndpoints.CancelAsync(
            cancelContext,
            dataSource,
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            jobId,
            userId);
        var canceled = await ExecuteJsonAsync(canceledResult, cancelContext);
        Assert.Equal("canceled", canceled.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, canceled.RootElement.GetProperty("revision").GetInt32());
        Assert.True(canceled.RootElement.GetProperty("cancelRequested").GetBoolean());

        var secondCancelContext = BuildContext(tenantId, actorApiKeyId);
        var secondCanceledResult = await AdvancedAnalysisEndpoints.CancelAsync(
            secondCancelContext,
            dataSource,
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            jobId,
            userId);
        var secondCanceled = await ExecuteJsonAsync(
            secondCanceledResult,
            secondCancelContext);
        Assert.Equal(2, secondCanceled.RootElement.GetProperty("revision").GetInt32());

        await using var connection = await dataSource.OpenConnectionAsync();
        var stored = await connection.QuerySingleAsync<(string status, string schema_version, string handoff, int audits)>(
            """
            SELECT j.status, j.schema_version, j.handoff::text,
              (SELECT count(*)::int FROM audit_events a
               WHERE a.tenant_id=j.tenant_id
                 AND a.action IN ('advanced_analysis.job.create', 'advanced_analysis.job.cancel')) AS audits
            FROM advanced_analysis_jobs j
            WHERE j.tenant_id=@tenant AND j.job_id=@job;
            """,
            new { tenant = tenantId, job = jobId });
        Assert.Equal("canceled", stored.status);
        Assert.Equal(AdvancedAnalysisHandoffEnvelope.CurrentSchemaVersion, stored.schema_version);
        Assert.Contains(request.Handoff.HandoffId.ToString(), stored.handoff, StringComparison.OrdinalIgnoreCase);
        Assert.True(stored.audits >= 2);
    }

    [Fact]
    public void Durable_job_migration_is_last_and_contains_the_isolation_constraints()
    {
        var migrationsDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SAAIA.Backend",
            "Db",
            "Migrations"));
        var versions = DbMigrator.GetOrderedMigrationVersions(
            migrationsDirectory);
        Assert.Equal("067_advanced_analysis_provider_affinity.sql", versions[^1]);

        var sql = File.ReadAllText(Path.Combine(
            migrationsDirectory,
            "065_advanced_analysis_jobs.sql"));
        Assert.Contains(
            "FOREIGN KEY (tenant_id, session_id, user_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE INDEX IF NOT EXISTS ux_advanced_analysis_handoff",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "status IN ('queued', 'running', 'succeeded', 'failed', 'canceled')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("jsonb_typeof(handoff) = 'object'", sql, StringComparison.Ordinal);

        var affinitySql = File.ReadAllText(Path.Combine(
            migrationsDirectory,
            "067_advanced_analysis_provider_affinity.sql"));
        Assert.Contains("provider_model TEXT NULL", affinitySql, StringComparison.Ordinal);
        Assert.Contains("ix_advanced_analysis_provider_affinity", affinitySql, StringComparison.Ordinal);
    }

    private static AdvancedAnalysisJobCreateRequest BuildRequest(string? mutation = null)
    {
        var evidence = new AdvancedAnalysisEvidenceReference
        {
            DocId = "11111111-1111-1111-1111-111111111111",
            RevisionId = "22222222-2222-2222-2222-222222222222",
            ChunkId = "chunk-1",
            PageStart = 2,
            PageEnd = 2
        };
        if (mutation == "missing_identity")
            evidence = new AdvancedAnalysisEvidenceReference();

        return new AdvancedAnalysisJobCreateRequest
        {
            UserId = "user-alpha",
            SessionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Handoff = new AdvancedAnalysisHandoffEnvelope
            {
                SchemaVersion = mutation == "unsupported_schema"
                    ? "saaia.advanced-analysis-handoff.v999"
                    : AdvancedAnalysisHandoffEnvelope.CurrentSchemaVersion,
                HandoffId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Compare the documented procedures.",
                Language = "en",
                OriginIntent = "rag.answer",
                ReasonCode = "explicit_documentary_comparison_outside_local_envelope",
                TransferStage = "before_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "comparison",
                    AnswerUnitCount = 2,
                    AtomicEvidenceCount = 2
                },
                ResearchState = new AdvancedAnalysisResearchState
                {
                    MemoryIsEvidence = mutation == "memory",
                    EvidenceRevalidationRequired = mutation != "revalidation",
                    EvidenceReferences = [evidence]
                },
                DataPolicy = new AdvancedAnalysisDataPolicy
                {
                    ExternalProviderContentAuthorized = mutation == "external_content",
                    ExternalProviderMetadataAuthorized = mutation == "external_metadata",
                    AuthorizationSource = "server_policy_required"
                }
            }
        };
    }

    private static async Task SeedSessionAsync(
        NpgsqlDataSource dataSource,
        Guid tenantId,
        Guid sessionId,
        string userId)
    {
        await SeedTenantAsync(dataSource, tenantId);
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO chat_sessions(
              tenant_id, user_id, session_id, title, created_at, updated_at)
            VALUES(@tenant, @user_id, @session, 'Advanced test', now(), now());
            """,
            new { tenant = tenantId, user_id = userId, session = sessionId });
    }

    private static async Task SeedTenantAsync(
        NpgsqlDataSource dataSource,
        Guid tenantId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO tenants(tenant_id, name, is_active)
            VALUES(@tenant, 'Advanced test tenant', true)
            ON CONFLICT (tenant_id) DO NOTHING;
            """,
            new { tenant = tenantId });
    }

    private static DefaultHttpContext BuildContext(
        Guid tenantId,
        Guid? actorApiKeyId = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Items[ApiKeyAuth.IsAdminItemKey] = false;
        context.Items[ApiKeyAuth.TenantIdItemKey] = tenantId;
        if (actorApiKeyId.HasValue)
            context.Items[ApiKeyAuth.ApiKeyIdItemKey] = actorApiKeyId.Value;
        return context;
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(
        IResult result,
        DefaultHttpContext context)
    {
        context.Response.Body.SetLength(0);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static async Task<int> ExecuteStatusCodeAsync(
        IResult result,
        DefaultHttpContext context)
    {
        context.Response.Body.SetLength(0);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private sealed class PostgresIntegrationDb : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        public string ConnectionString { get; }

        private PostgresIntegrationDb(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public static async Task<PostgresIntegrationDb?> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(
                "SAAIA_TEST_PG_CONN");
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                return null;

            var adminBuilder = new NpgsqlConnectionStringBuilder(
                baseConnectionString);
            var databaseName = $"saaia_advanced_{Guid.NewGuid():N}";
            await using (var adminConnection = new NpgsqlConnection(
                             adminBuilder.ConnectionString))
            {
                await adminConnection.OpenAsync();
                await adminConnection.ExecuteAsync(
                    $"CREATE DATABASE \"{databaseName}\";");
            }

            var databaseBuilder = new NpgsqlConnectionStringBuilder(
                baseConnectionString)
            {
                Database = databaseName
            };
            var database = new PostgresIntegrationDb(
                adminBuilder.ConnectionString,
                databaseName,
                databaseBuilder.ConnectionString);
            var migrationsDirectory = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SAAIA.Backend",
                "Db",
                "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(
                database.ConnectionString,
                migrationsDirectory,
                CancellationToken.None);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var adminConnection = new NpgsqlConnection(
                    _adminConnectionString);
                await adminConnection.OpenAsync();
                await adminConnection.ExecuteAsync(
                    $"""
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = '{_databaseName}'
                      AND pid <> pg_backend_pid();
                    """);
                await adminConnection.ExecuteAsync(
                    $"DROP DATABASE IF EXISTS \"{_databaseName}\";");
            }
            catch
            {
                // Best-effort cleanup for the isolated optional integration DB.
            }
        }
    }
}
