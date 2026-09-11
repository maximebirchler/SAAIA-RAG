using System.Text;
using System.Text.Json;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
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

public sealed class SummaryBackofficeGovernanceTests
{
    [Fact]
    public async Task ResolveSummaryGenerationExecutionAsync_returns_client_admin_when_backoffice_is_disabled()
    {
        var ctx = BuildAdminContext(backofficeEnabled: false);

        var decision = await SummaryEndpoints.ResolveSummaryGenerationExecutionAsync(
            ctx,
            CreateUnusedDataSource(),
            new RuntimeGovernanceOptions(),
            CreateRagOptions(),
            new StubHostEnvironment(),
            CancellationToken.None);

        Assert.Equal("client_admin", decision.ExecutionMode);
        Assert.Equal("backoffice_disabled", decision.Status);
        Assert.False(decision.UsesCapabilityB);
    }

    [Fact]
    public async Task ResolveSummaryGenerationExecutionAsync_returns_client_admin_when_capability_b_is_not_selected()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            var ctx = BuildAdminContext(backofficeEnabled: true);

            var decision = await SummaryEndpoints.ResolveSummaryGenerationExecutionAsync(
                ctx,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                CancellationToken.None);

            Assert.Equal("client_admin", decision.ExecutionMode);
            Assert.Equal("not_ready", decision.Status);
            Assert.Equal("capability_b.backoffice_generation", decision.CapabilityKey);
            Assert.False(decision.UsesCapabilityB);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task ResolveSummaryGenerationExecutionAsync_returns_server_backoffice_when_capability_b_is_selected()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var ctx = BuildAdminContext(backofficeEnabled: true);
            var decision = await SummaryEndpoints.ResolveSummaryGenerationExecutionAsync(
                ctx,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                CancellationToken.None);

            Assert.Equal("server_backoffice", decision.ExecutionMode);
            Assert.Equal("selected", decision.Status);
            Assert.Equal("capability_b.backoffice_generation", decision.CapabilityKey);
            Assert.True(decision.UsesCapabilityB);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task GenerateSummaryAsync_marks_server_backoffice_job_as_capability_b_claimable()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-runtime-server-claimable.pdf', 'b-runtime-server-claimable.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var generateCtx = BuildAdminContext(backofficeEnabled: true);
            var generateResult = await InvokeGenerateSummaryAsync(
                generateCtx,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium"));
            var generatePayload = await ExecuteResultAsync<JsonElement>(generateResult, generateCtx);

            Assert.Equal("queued", generatePayload.GetProperty("status").GetString());
            Assert.Equal("server_backoffice", generatePayload.GetProperty("executionMode").GetString());
            Assert.Equal("selected", generatePayload.GetProperty("runtimeCapabilityStatus").GetString());
            var jobId = generatePayload.GetProperty("jobId").GetGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var claimable = await RuntimeCapabilityBExecutionStore.LoadCapabilityBExecutionJobAsync(
                    conn,
                    tenantId,
                    jobId,
                    CancellationToken.None);

                Assert.NotNull(claimable);
                Assert.Equal("capability_b", claimable!.EnqueueSource);
                Assert.Equal("server_backoffice", claimable.ExecutionMode);
                Assert.True(claimable.RuntimeCapabilitySelected);
                Assert.Equal(1000, claimable.PriorityScore);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task LoadNextQueuedCapabilityBExecutionJobAsync_prioritizes_payload_priority_score()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var lowPriorityDocId = Guid.NewGuid();
        var highPriorityDocId = Guid.NewGuid();
        var lowPriorityJobId = Guid.NewGuid();
        var highPriorityJobId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var lowPriorityPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = lowPriorityDocId,
            ["docPath"] = "ATEX/low-priority-stale-summary.pdf",
            ["level"] = "medium",
            ["source"] = "capability_b",
            ["executionMode"] = "server_backoffice",
            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
            ["runtimeCapabilityStatus"] = "selected",
            ["runtimeCapabilitySelected"] = true,
            ["priorityScore"] = 120
        });
        var highPriorityPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = highPriorityDocId,
            ["docPath"] = "ATEX/high-priority-missing-summary.pdf",
            ["level"] = "medium",
            ["source"] = "capability_b",
            ["executionMode"] = "server_backoffice",
            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
            ["runtimeCapabilityStatus"] = "selected",
            ["runtimeCapabilitySelected"] = true,
            ["priorityScore"] = 200
        });

        await conn.ExecuteAsync(
            """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES
(
  @tenant, @lowPriorityDocId, 'ATEX/low-priority-stale-summary.pdf', 'low-priority-stale-summary.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
),
(
  @tenant, @highPriorityDocId, 'ATEX/high-priority-missing-summary.pdf', 'high-priority-missing-summary.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, level, payload, created_at)
VALUES
(@lowPriorityJobId, @tenant, 'summary.generate', 'queued', @lowPriorityDocId, 'medium', @lowPriorityPayload::jsonb, now() - interval '10 minutes'),
(@highPriorityJobId, @tenant, 'summary.generate', 'queued', @highPriorityDocId, 'medium', @highPriorityPayload::jsonb, now());
""",
            new
            {
                tenant = tenantId,
                lowPriorityDocId,
                highPriorityDocId,
                lowPriorityJobId,
                highPriorityJobId,
                lowPriorityPayload,
                highPriorityPayload
            });

        var next = await RuntimeCapabilityBExecutionStore.LoadNextQueuedCapabilityBExecutionJobAsync(
            conn,
            tenantId,
            CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal(highPriorityJobId, next!.JobId);
        Assert.Equal(200, next.PriorityScore);
        Assert.Equal("ATEX/high-priority-missing-summary.pdf", next.DocPath);
    }

    [Fact]
    public async Task CapabilityB_candidates_block_active_summary_jobs_regardless_of_source()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var legacyDocId = Guid.NewGuid();
        var capabilityBDocId = Guid.NewGuid();
        var legacyJobId = Guid.NewGuid();
        var capabilityBJobId = Guid.NewGuid();
        var legacyPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = legacyDocId,
            ["docPath"] = "ATEX/legacy-client-summary-request.pdf",
            ["level"] = "medium",
            ["executionMode"] = "client_admin"
        });
        var capabilityBPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = capabilityBDocId,
            ["docPath"] = "ATEX/active-capability-b-summary.pdf",
            ["level"] = "medium",
            ["source"] = "capability_b",
            ["executionMode"] = "server_backoffice"
        });

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES
(
  @tenant, @legacyDocId, 'ATEX/legacy-client-summary-request.pdf', 'legacy-client-summary-request.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
),
(
  @tenant, @capabilityBDocId, 'ATEX/active-capability-b-summary.pdf', 'active-capability-b-summary.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, level, payload, created_at)
VALUES
(@legacyJobId, @tenant, 'summary.request', 'queued', @legacyDocId, 'medium', @legacyPayload::jsonb, now()),
(@capabilityBJobId, @tenant, 'summary.generate', 'queued', @capabilityBDocId, 'medium', @capabilityBPayload::jsonb, now());
""",
            new
            {
                tenant = tenantId,
                legacyDocId,
                capabilityBDocId,
                legacyJobId,
                capabilityBJobId,
                legacyPayload,
                capabilityBPayload
            });

        var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: null,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);

        var legacyCandidate = Assert.Single(candidates, candidate => candidate.DocId == legacyDocId);
        Assert.True(legacyCandidate.HasActiveJob);
        Assert.True(legacyCandidate.PolicyBlocked);
        Assert.Equal("active_summary_job_exists", legacyCandidate.PolicyBlockReason);
        Assert.Equal("review_active_summary_job", legacyCandidate.RecommendedAction);
        Assert.Contains("summary_missing", legacyCandidate.Reasons);
        Assert.Contains("summary_job_active", legacyCandidate.Reasons);

        var capabilityBCandidate = Assert.Single(candidates, candidate => candidate.DocId == capabilityBDocId);
        Assert.True(capabilityBCandidate.HasActiveJob);
        Assert.True(capabilityBCandidate.PolicyBlocked);
        Assert.Equal("active_summary_job_exists", capabilityBCandidate.PolicyBlockReason);
        Assert.Contains("summary_job_active", capabilityBCandidate.Reasons);
    }

    [Fact]
    public async Task CapabilityB_candidates_include_fresh_summaries_when_backoffice_profile_schema_is_stale()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        const string docPath = "Knowledge/backoffice-profile-schema-refresh.pdf";

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, @docPath, 'backoffice-profile-schema-refresh.pdf', 'knowledge', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('b1', 32), 'hex'), 4096, now(), false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, @docPath,
  decode(repeat('b1', 32), 'hex'), 4096, now(), 1, 1, now()
);

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES(
  @tenant,
  @docId,
  'medium',
  'en',
  saaia_document_summary_source_hash(decode(repeat('b1', 32), 'hex'), @docPath, 4096, now(), 1),
  'Fresh summary that should not hide stale profile evidence schema.',
  '{"qualityScore":0.90,"strategy":"llm","runtimeCapabilityStatus":"selected"}'::jsonb,
  now(),
  now()
);

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
  summary_text, keywords, entities, topics, hypothetical_questions, limits,
  search_text, token_count, checksum, metadata
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1', 'en',
  'Existing backoffice profile with an old metadata schema.',
  ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
  'Existing backoffice profile with an old metadata schema.',
  8,
  decode(repeat('b2', 32), 'hex'),
  '{"contentCardEvidenceSchemaVersion":1}'::jsonb
);
""",
            new { tenant = tenantId, docId, revisionId, profileId, docPath });

        var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: null,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);

        var candidate = Assert.Single(candidates, item => item.DocId == docId);
        Assert.Equal("fresh", candidate.SummaryState);
        Assert.Equal("stale", candidate.ProfileState);
        Assert.True(candidate.HasBackofficeProfile);
        Assert.Contains("summary_force_refresh", candidate.Reasons);
        Assert.Contains("profile_schema_stale", candidate.Reasons);
        Assert.Equal("enqueue_summary_generation", candidate.RecommendedAction);
        Assert.False(candidate.PolicyBlocked);
    }

    [Fact]
    public async Task ResolveSummaryGenerationExecutionAsync_falls_back_to_client_admin_when_live_runtime_probe_fails()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var ctx = BuildAdminContext(backofficeEnabled: true, httpClientFactory: new BrokenRuntimeGovernanceHttpClientFactory());
            var decision = await SummaryEndpoints.ResolveSummaryGenerationExecutionAsync(
                ctx,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                CancellationToken.None);

            Assert.Equal("client_admin", decision.ExecutionMode);
            Assert.Equal("runtime_unavailable", decision.Status);
            Assert.Equal("capability_b.backoffice_generation", decision.CapabilityKey);
            Assert.False(decision.UsesCapabilityB);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task GenerateSummaryAsync_returns_backoffice_unavailable_without_job_when_b_runtime_probe_fails()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-runtime-fallback-metadata.pdf', 'b-runtime-fallback-metadata.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var generateCtx = BuildAdminContext(backofficeEnabled: true, httpClientFactory: new BrokenRuntimeGovernanceHttpClientFactory());
            var generateResult = await InvokeGenerateSummaryAsync(
                generateCtx,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium"));
            var generatePayload = await ExecuteResultAsync<JsonElement>(generateResult, generateCtx);

            Assert.Null(generatePayload.GetProperty("jobId").GetString());
            Assert.Equal("backoffice_unavailable", generatePayload.GetProperty("status").GetString());
            Assert.False(generatePayload.GetProperty("queued").GetBoolean());
            Assert.Equal("backoffice_unavailable", generatePayload.GetProperty("error").GetString());
            Assert.Equal("client_admin", generatePayload.GetProperty("executionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", generatePayload.GetProperty("runtimeCapabilityKey").GetString());
            Assert.Equal("runtime_unavailable", generatePayload.GetProperty("runtimeCapabilityStatus").GetString());

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var jobCount = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM admin_jobs
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND job_type='summary.generate';
""",
                    new { tenant = tenantId, docId });

                Assert.Equal(0, jobCount);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Admin_job_surfaces_project_capability_b_metadata_for_server_backoffice_jobs()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-governed-summary.pdf', 'b-governed-summary.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var payload = enqueue.Payload!;
            Assert.Equal(1, payload.QueuedCount);
            var jobId = Assert.Single(payload.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var listCtx = BuildAdminContext(backofficeEnabled: true);
            var listResult = await SummaryEndpoints.ListAdminJobsAsync(
                listCtx,
                ds,
                type: "summary",
                limit: 20,
                offset: 0,
                dateField: null,
                dateFrom: null,
                dateTo: null,
                sortDir: null);
            var listPayload = await ExecuteResultAsync<JsonElement>(listResult, listCtx);
            var listedJob = Assert.Single(
                listPayload.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("JobId").GetGuid() == jobId!.Value);

            Assert.Equal("ATEX/b-governed-summary.pdf", listedJob.GetProperty("DocPath").GetString());
            Assert.Equal("capability_b", listedJob.GetProperty("EnqueueSource").GetString());
            Assert.Equal("server_backoffice", listedJob.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", listedJob.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("selected", listedJob.GetProperty("RuntimeCapabilityStatus").GetString());
            Assert.True(listedJob.GetProperty("RuntimeCapabilitySelected").GetBoolean());
            Assert.Equal(payload.CampaignId, listedJob.GetProperty("CampaignId").GetGuid());
            Assert.False(listedJob.GetProperty("Force").GetBoolean());

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);

            Assert.Equal("ATEX/b-governed-summary.pdf", detailPayload.GetProperty("DocPath").GetString());
            Assert.Equal("capability_b", detailPayload.GetProperty("EnqueueSource").GetString());
            Assert.Equal("server_backoffice", detailPayload.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", detailPayload.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("selected", detailPayload.GetProperty("RuntimeCapabilityStatus").GetString());
            Assert.True(detailPayload.GetProperty("RuntimeCapabilitySelected").GetBoolean());
            Assert.Equal(payload.CampaignId, detailPayload.GetProperty("CampaignId").GetGuid());

            var statusCtx = BuildAdminContext(backofficeEnabled: true);
            var statusResult = await SummaryEndpoints.SummaryStatusAsync(statusCtx, ds, jobId.Value);
            var statusPayload = await ExecuteResultAsync<JsonElement>(statusResult, statusCtx);

            Assert.Equal(jobId.Value, statusPayload.GetProperty("JobId").GetGuid());
            Assert.Equal("ATEX/b-governed-summary.pdf", statusPayload.GetProperty("DocPath").GetString());
            Assert.Equal("capability_b", statusPayload.GetProperty("EnqueueSource").GetString());
            Assert.Equal("server_backoffice", statusPayload.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", statusPayload.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("selected", statusPayload.GetProperty("RuntimeCapabilityStatus").GetString());
            Assert.True(statusPayload.GetProperty("RuntimeCapabilitySelected").GetBoolean());
            Assert.Equal(payload.CampaignId, statusPayload.GetProperty("CampaignId").GetGuid());
            Assert.True(statusPayload.TryGetProperty("Payload", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task CapabilityB_force_enqueue_can_regenerate_existing_fresh_summary_for_quality_review()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            var newerDistractorDocId = Guid.NewGuid();
            const string docPath = "ATEX/b-quality-fresh-regenerate.pdf";

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, @docPath, 'b-quality-fresh-regenerate.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
),(
  @tenant, @newerDistractorDocId, 'ATEX/b-quality-newer-missing.pdf', 'b-quality-newer-missing.pdf', 'atex', 'indexed',
  now() + interval '5 minutes', now(), 1, 1, false
);

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
SELECT
  @tenant,
  gen_random_uuid(),
  'ATEX/b-quality-newer-missing-' || gs::text || '.pdf',
  'b-quality-newer-missing-' || gs::text || '.pdf',
  'atex',
  'indexed',
  now() + interval '10 minutes' + (gs * interval '1 second'),
  now(),
  1,
  1,
  false
FROM generate_series(1, 505) AS gs;

INSERT INTO document_summaries(
  tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at
)
VALUES(
  @tenant,
  @docId,
  'medium',
  'fr',
  (SELECT saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version) FROM documents WHERE tenant_id=@tenant AND doc_id=@docId),
  'Too short.',
  '{"qualityScore":0.20,"strategy":"llm","runtimeCapabilityStatus":"selected"}'::jsonb,
  now(),
  now()
);
""",
                    new { tenant = tenantId, docId, newerDistractorDocId, docPath });
                await SeedCurrentLlmProfileAsync(conn, tenantId, docId);
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var defaultEnqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(defaultEnqueue.Error);
            Assert.NotNull(defaultEnqueue.Payload);
            Assert.Equal(0, defaultEnqueue.Payload!.CandidateCount);
            Assert.Equal(0, defaultEnqueue.Payload.QueuedCount);

            var forcedEnqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(
                    DocIds: [docId],
                    MaxCandidates: 1,
                    Force: true),
                CancellationToken.None);

            Assert.Null(forcedEnqueue.Error);
            Assert.NotNull(forcedEnqueue.Payload);
            var payload = forcedEnqueue.Payload!;
            Assert.True(payload.Force);
            Assert.Equal(1, payload.CandidateCount);
            Assert.Equal(1, payload.PlannedCount);
            Assert.Equal(1, payload.QueuedCount);
            Assert.Equal(0, payload.SkippedCount);
            Assert.Equal(1, payload.ReasonCounts["summary_force_refresh"]);
            var queued = Assert.Single(payload.Items, item => item.Queued);
            Assert.Equal(docId, queued.DocId);
            Assert.Equal(docPath, queued.DocPath);
            Assert.NotNull(queued.JobId);

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, queued.JobId!.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);

            Assert.Equal(docPath, detailPayload.GetProperty("DocPath").GetString());
            Assert.Equal("capability_b", detailPayload.GetProperty("EnqueueSource").GetString());
            Assert.True(detailPayload.GetProperty("Force").GetBoolean());
            Assert.Equal("server_backoffice", detailPayload.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", detailPayload.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("selected", detailPayload.GetProperty("RuntimeCapabilityStatus").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Admin_jobs_pause_and_resume_support_queued_capability_b_backoffice_jobs()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var jobId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Test tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at)
VALUES(
  @jobId,
  @tenant,
  'summary.generate',
  'queued',
  gen_random_uuid(),
  'Backoffice/pending-summary.pdf',
  'medium',
  '{"source":"capability_b","executionMode":"server_backoffice","runtimeCapabilityKey":"capability_b.backoffice_generation"}'::jsonb,
  now()
);
""",
                new { tenant = tenantId, jobId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

        var pauseCtx = BuildAdminContext(backofficeEnabled: true);
        var pauseResult = await InvokePauseAdminJobAsync(pauseCtx, ds, jobId);
        var pausePayload = await ExecuteResultAsync<JsonElement>(pauseResult, pauseCtx);

        Assert.True(pausePayload.GetProperty("paused").GetBoolean());
        Assert.Equal("summary", pausePayload.GetProperty("type").GetString());
        Assert.Equal("paused", pausePayload.GetProperty("status").GetString());
        Assert.Equal("paused", pausePayload.GetProperty("result").GetString());

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var paused = await conn.QuerySingleAsync<(string status, string requested_action)>(
                """
SELECT status, payload #>> '{control,requestedAction}' AS requested_action
FROM admin_jobs
WHERE tenant_id=@tenant AND job_id=@jobId;
""",
                new { tenant = tenantId, jobId });
            Assert.Equal("paused", paused.status);
            Assert.Equal("pause", paused.requested_action);
        }

        var listCtx = BuildAdminContext(backofficeEnabled: true);
        var listResult = await SummaryEndpoints.ListAdminJobsAsync(
            listCtx,
            ds,
            type: "summary",
            limit: 20,
            offset: 0,
            dateField: null,
            dateFrom: null,
            dateTo: null,
            sortDir: null);
        var listPayload = await ExecuteResultAsync<JsonElement>(listResult, listCtx);
        var listed = Assert.Single(
            listPayload.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("JobId").GetGuid() == jobId);
        Assert.Equal("paused", listed.GetProperty("Status").GetString());

        var resumeCtx = BuildAdminContext(backofficeEnabled: true);
        var resumeResult = await InvokeResumeAdminJobAsync(resumeCtx, ds, jobId);
        var resumePayload = await ExecuteResultAsync<JsonElement>(resumeResult, resumeCtx);

        Assert.True(resumePayload.GetProperty("resumed").GetBoolean());
        Assert.Equal("summary", resumePayload.GetProperty("type").GetString());
        Assert.Equal("queued", resumePayload.GetProperty("status").GetString());

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var resumed = await conn.QuerySingleAsync<(string status, string? requested_action)>(
                """
SELECT status, payload #>> '{control,requestedAction}' AS requested_action
FROM admin_jobs
WHERE tenant_id=@tenant AND job_id=@jobId;
""",
                new { tenant = tenantId, jobId });
            Assert.Equal("queued", resumed.status);
            Assert.Null(resumed.requested_action);
        }
    }

    [Fact]
    public async Task Summary_management_surfaces_expose_active_capability_b_jobs_for_missing_or_stale_docs()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-visible-in-summary-lists.pdf', 'b-visible-in-summary-lists.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var enqueuePayload = enqueue.Payload!;
            Assert.Equal(1, enqueuePayload.QueuedCount);

            var missingCtx = BuildAdminContext(backofficeEnabled: true);
            var missingResult = await SummaryEndpoints.MissingSummariesAsync(
                missingCtx,
                ds,
                categoryPath: null,
                categoryRef: null,
                limit: 20,
                offset: 0);
            var missingPayload = await ExecuteResultAsync<JsonElement>(missingResult, missingCtx);
            var missingItem = Assert.Single(
                missingPayload.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("docId").GetGuid() == docId);

            Assert.Equal("missing", missingItem.GetProperty("summaryState").GetString());
            Assert.True(missingItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.Equal("summary.generate", missingItem.GetProperty("activeSummaryJobType").GetString());
            Assert.Equal("queued", missingItem.GetProperty("activeSummaryJobStatus").GetString());
            Assert.Equal("server_backoffice", missingItem.GetProperty("activeSummaryJobExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", missingItem.GetProperty("activeSummaryJobRuntimeCapabilityKey").GetString());
            Assert.Equal("selected", missingItem.GetProperty("activeSummaryJobRuntimeCapabilityStatus").GetString());
            Assert.Equal("capability_b", missingItem.GetProperty("activeSummaryJobEnqueueSource").GetString());
            Assert.Equal(enqueuePayload.CampaignId, missingItem.GetProperty("activeSummaryJobCampaignId").GetGuid());
            Assert.False(missingItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
            Assert.Equal("review_active_summary_job", missingItem.GetProperty("capabilityBRecommendedAction").GetString());
            Assert.True(missingItem.GetProperty("capabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("active_summary_job_exists", missingItem.GetProperty("capabilityBPolicyBlockReason").GetString());
            Assert.Equal("queued", missingItem.GetProperty("capabilityBLastJobStatus").GetString());
            Assert.Contains("summary_missing", GetStringArray(missingItem.GetProperty("capabilityBReasons")));
            Assert.Contains("summary_job_active", GetStringArray(missingItem.GetProperty("capabilityBReasons")));

            var catalogCtx = BuildAdminContext(backofficeEnabled: true);
            var catalogResult = await SummaryEndpoints.CatalogSummariesAsync(
                catalogCtx,
                ds,
                categoryRef: null,
                categoryPath: null,
                pageSize: 20,
                maxpagesize: null,
                cursor: null);
            var catalogPayload = await ExecuteResultAsync<JsonElement>(catalogResult, catalogCtx);
            var catalogItem = Assert.Single(
                catalogPayload.GetProperty("value").EnumerateArray(),
                item => item.GetProperty("docId").GetGuid() == docId);

            Assert.Equal("missing", catalogItem.GetProperty("summaryState").GetString());
            Assert.True(catalogItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.Equal("server_backoffice", catalogItem.GetProperty("activeSummaryJobExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", catalogItem.GetProperty("activeSummaryJobRuntimeCapabilityKey").GetString());
            Assert.Equal(enqueuePayload.CampaignId, catalogItem.GetProperty("activeSummaryJobCampaignId").GetGuid());
            Assert.False(catalogItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
            Assert.True(catalogItem.GetProperty("capabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("active_summary_job_exists", catalogItem.GetProperty("capabilityBPolicyBlockReason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Summary_management_surfaces_expose_capability_b_cooldown_policy_for_recent_failures()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-recent-failure.pdf', 'b-recent-failure.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });

                await conn.ExecuteAsync(
                    """
INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, level, payload, created_at, finished_at, last_error
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'failed', @docId, 'medium', @payload::jsonb,
  now() - interval '2 hours', now() - interval '30 minutes', 'model timeout'
);
""",
                    new
                    {
                        jobId = Guid.NewGuid(),
                        tenant = tenantId,
                        docId,
                        payload = JsonSerializer.Serialize(new
                        {
                            docPath = "ATEX/b-recent-failure.pdf",
                            source = "capability_b",
                            executionMode = "server_backoffice",
                            runtimeCapabilityKey = "capability_b.backoffice_generation",
                            runtimeCapabilityStatus = "selected",
                            runtimeCapabilitySelected = true
                        })
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);

            var missingCtx = BuildAdminContext(backofficeEnabled: true);
            var missingResult = await SummaryEndpoints.MissingSummariesAsync(
                missingCtx,
                ds,
                categoryPath: null,
                categoryRef: null,
                limit: 20,
                offset: 0);
            var missingPayload = await ExecuteResultAsync<JsonElement>(missingResult, missingCtx);
            var missingItem = Assert.Single(
                missingPayload.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("docId").GetGuid() == docId);

            Assert.Equal("missing", missingItem.GetProperty("summaryState").GetString());
            Assert.False(missingItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.False(missingItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
            Assert.True(missingItem.GetProperty("capabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("recent_summary_job_failure", missingItem.GetProperty("capabilityBPolicyBlockReason").GetString());
            Assert.Equal("inspect_recent_summary_failure", missingItem.GetProperty("capabilityBRecommendedAction").GetString());
            Assert.Equal("failed", missingItem.GetProperty("capabilityBLastJobStatus").GetString());
            Assert.Equal("model timeout", missingItem.GetProperty("capabilityBLastJobError").GetString());
            Assert.Contains("summary_missing", GetStringArray(missingItem.GetProperty("capabilityBReasons")));
            Assert.Contains("recent_summary_failure", GetStringArray(missingItem.GetProperty("capabilityBReasons")));

            var catalogCtx = BuildAdminContext(backofficeEnabled: true);
            var catalogResult = await SummaryEndpoints.CatalogSummariesAsync(
                catalogCtx,
                ds,
                categoryRef: null,
                categoryPath: null,
                pageSize: 20,
                maxpagesize: null,
                cursor: null);
            var catalogPayload = await ExecuteResultAsync<JsonElement>(catalogResult, catalogCtx);
            var catalogItem = Assert.Single(
                catalogPayload.GetProperty("value").EnumerateArray(),
                item => item.GetProperty("docId").GetGuid() == docId);

            Assert.False(catalogItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
            Assert.True(catalogItem.GetProperty("capabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("recent_summary_job_failure", catalogItem.GetProperty("capabilityBPolicyBlockReason").GetString());
            Assert.Equal("inspect_recent_summary_failure", catalogItem.GetProperty("capabilityBRecommendedAction").GetString());
            Assert.Equal("failed", catalogItem.GetProperty("capabilityBLastJobStatus").GetString());
            Assert.Equal("model timeout", catalogItem.GetProperty("capabilityBLastJobError").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_execution_contract_claims_jobs_and_requires_valid_lease_for_completion()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            const string summaryText = "Capability B execution contract summary";

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-execution-contract.pdf', 'b-execution-contract.pdf', 'atex', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('a1', 32), 'hex'), 4096, now(), false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, 'ATEX/b-execution-contract.pdf',
  decode(repeat('a1', 32), 'hex'), 4096, now(), 1, 1, now()
);

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
  summary_text, keywords, entities, topics, hypothetical_questions, limits,
  search_text, token_count, checksum, metadata
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'deterministic_v1', 'fr',
  'Profil français initial potentiellement obsolète.',
  ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
  'Profil français initial potentiellement obsolète.',
  6,
  decode(repeat('a2', 32), 'hex'),
  '{}'::jsonb
);
""",
                    new
                    {
                        tenant = tenantId,
                        docId,
                        revisionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000001"),
                        profileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000002")
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var enqueuePayload = enqueue.Payload!;
            var jobId = Assert.Single(enqueuePayload.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var claimCtx = BuildAdminContext(backofficeEnabled: true);
            var claimResult = await AdminRuntimeEndpoints.CapabilityBClaimAsync(
                claimCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBClaimRequestDto(JobId: jobId, ExecutorId: "capability_b_worker"));
            var claimPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBClaimResponseDto>(claimResult, claimCtx);

            Assert.Equal(jobId.Value, claimPayload.JobId);
            Assert.Equal(docId, claimPayload.DocId);
            Assert.Equal("capability_b_worker", claimPayload.ClaimedBy);
            Assert.False(string.IsNullOrWhiteSpace(claimPayload.LeaseToken));

            var runningStatusCtx = BuildAdminContext(backofficeEnabled: true);
            var runningStatusResult = await SummaryEndpoints.SummaryStatusAsync(runningStatusCtx, ds, jobId.Value);
            var runningStatusPayload = await ExecuteResultAsync<JsonElement>(runningStatusResult, runningStatusCtx);
            Assert.Equal("running", runningStatusPayload.GetProperty("Status").GetString());
            Assert.NotEqual(JsonValueKind.Null, runningStatusPayload.GetProperty("StartedAt").ValueKind);

            var directSubmitCtx = BuildAdminContext(backofficeEnabled: true);
            var directSubmitResult = await SummaryEndpoints.SubmitSummaryAsync(
                directSubmitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    JobId: jobId,
                    SummaryText: "manual bypass should fail"));
            var directSubmitPayload = await ExecuteResultAsync<JsonElement>(directSubmitResult, directSubmitCtx);
            Assert.Equal("capability_b_execution_lease_required", directSubmitPayload.GetProperty("error").GetString());

            var invalidLeaseSubmitCtx = BuildAdminContext(backofficeEnabled: true);
            var invalidLeaseSubmitResult = await SummaryEndpoints.SubmitSummaryAsync(
                invalidLeaseSubmitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    JobId: jobId,
                    ExecutionLeaseToken: "invalid-lease-token",
                    SummaryText: "manual bypass with invalid lease should fail"));
            var invalidLeaseSubmitPayload = await ExecuteResultAsync<JsonElement>(invalidLeaseSubmitResult, invalidLeaseSubmitCtx);
            Assert.Equal("capability_b_invalid_execution_lease", invalidLeaseSubmitPayload.GetProperty("error").GetString());

            var invalidCompleteCtx = BuildAdminContext(backofficeEnabled: true);
            var invalidCompleteResult = await AdminRuntimeEndpoints.CapabilityBCompleteAsync(
                invalidCompleteCtx,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: jobId.Value,
                    LeaseToken: "invalid-lease-token",
                    SummaryText: summaryText));
            var invalidCompletePayload = await ExecuteResultAsync<JsonElement>(invalidCompleteResult, invalidCompleteCtx);
            Assert.Equal("capability_b_invalid_execution_lease", invalidCompletePayload.GetProperty("error").GetString());

            var completeCtx = BuildAdminContext(backofficeEnabled: true);
            var completeResult = await AdminRuntimeEndpoints.CapabilityBCompleteAsync(
                completeCtx,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: jobId.Value,
                    LeaseToken: claimPayload.LeaseToken,
                    SummaryText: summaryText,
                    DocLanguage: "de"));
            var completePayload = await ExecuteResultAsync<JsonElement>(completeResult, completeCtx);
            Assert.True(completePayload.GetProperty("stored").GetBoolean());
            Assert.Equal("de", completePayload.GetProperty("docLanguage").GetString());
            Assert.Equal("request", completePayload.GetProperty("docLanguageSource").GetString());

            await using (var conn = await ds.OpenConnectionAsync())
            {
                var stored = await conn.QuerySingleAsync<(string DocLanguage, string Result)>(
                    """
SELECT s.doc_language AS "DocLanguage", j.result::text AS "Result"
FROM document_summaries s
JOIN admin_jobs j
  ON j.tenant_id=s.tenant_id
 AND j.doc_id=s.doc_id
WHERE s.tenant_id=@tenant
  AND s.doc_id=@docId
  AND s.level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Equal("de", stored.DocLanguage);
                using var storedResult = JsonDocument.Parse(stored.Result);
                Assert.Equal("request", storedResult.RootElement.GetProperty("docLanguageSource").GetString());
                var storedMetadata = await conn.ExecuteScalarAsync<string>(
                    "SELECT summary_meta::text FROM document_summaries WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium';",
                    new { tenant = tenantId, docId });
                using var metadata = JsonDocument.Parse(storedMetadata!);
                Assert.Equal(JsonValueKind.Object, metadata.RootElement.ValueKind);
                Assert.Empty(metadata.RootElement.EnumerateObject());
            }

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);
            Assert.Equal("done", detailPayload.GetProperty("Status").GetString());
            Assert.True(detailPayload.GetProperty("ResultStored").GetBoolean());
            Assert.Equal("capability_b_worker", detailPayload.GetProperty("ResultCompletedBy").GetString());

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_job_claimed");
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_summary_completed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_execution_contract_can_fail_claimed_jobs()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-failed-execution.pdf', 'b-failed-execution.pdf', 'atex', 'indexed',
  now(), now(), 1, 1, false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var claimCtx = BuildAdminContext(backofficeEnabled: true);
            var claimResult = await AdminRuntimeEndpoints.CapabilityBClaimAsync(
                claimCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBClaimRequestDto(JobId: jobId));
            var claimPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBClaimResponseDto>(claimResult, claimCtx);

            var failCtx = BuildAdminContext(backofficeEnabled: true);
            var failResult = await AdminRuntimeEndpoints.CapabilityBFailAsync(
                failCtx,
                ds,
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBFailRequestDto(
                    JobId: jobId.Value,
                    LeaseToken: claimPayload.LeaseToken,
                    Error: "worker_crashed"));
            var failPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBFailResponseDto>(failResult, failCtx);

            Assert.Equal(jobId.Value, failPayload.JobId);
            Assert.Equal("failed", failPayload.Status);
            Assert.Equal("worker_crashed", failPayload.LastError);

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);
            Assert.Equal("failed", detailPayload.GetProperty("Status").GetString());
            Assert.Equal("worker_crashed", detailPayload.GetProperty("LastError").GetString());
            Assert.False(detailPayload.GetProperty("ResultStored").GetBoolean());

            var completeAfterFailCtx = BuildAdminContext(backofficeEnabled: true);
            var completeAfterFailResult = await AdminRuntimeEndpoints.CapabilityBCompleteAsync(
                completeAfterFailCtx,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: jobId.Value,
                    LeaseToken: claimPayload.LeaseToken,
                    SummaryText: "should not complete"));
            var completeAfterFailPayload = await ExecuteResultAsync<JsonElement>(completeAfterFailResult, completeAfterFailCtx);
            Assert.Equal("capability_b_job_not_running", completeAfterFailPayload.GetProperty("error").GetString());

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_job_claimed");
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_job_failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_processes_next_queued_job_and_stores_deterministic_summary()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var unitId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-worker-generated-summary.pdf', 'b-worker-generated-summary.pdf', 'atex', 'indexed',
  12, decode(repeat('cd', 32), 'hex'), now(), now(), 1, 1, false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
  ingestion_version, indexed_version, published_at, created_at
)
VALUES(
  @revisionId, @tenant, @docId, 'ATEX/b-worker-generated-summary.pdf', decode(repeat('cd', 32), 'hex'), 1024, now(),
  1, 1, now(), now()
);

INSERT INTO document_sections(
  section_id, tenant_id, revision_id, ordinal, title, section_level, page_start, page_end, metadata, created_at
)
VALUES(
  @sectionId, @tenant, @revisionId, 0, 'Scope and Purpose', 1, 1, 2, '{}'::jsonb, now()
);

INSERT INTO document_units(
  unit_id, tenant_id, revision_id, section_id, ordinal, page_start, page_end, text_content,
  char_count, token_count, metadata, created_at
)
VALUES(
  @unitId, @tenant, @revisionId, @sectionId, 0, 1, 1,
  'This document defines the operational perimeter and the required safety controls for classified areas.',
  98, 18, '{}'::jsonb, now()
);

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
  summary_text, keywords, entities, topics, hypothetical_questions, limits,
  search_text, token_count, checksum, metadata
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'deterministic_v1', 'en',
  'Deterministic profile for b-worker-llm-summary.pdf.',
  ARRAY['baseline-keyword']::text[],
  ARRAY['baseline-entity']::text[],
  ARRAY['baseline-topic']::text[],
  ARRAY['What does b-worker-llm-summary.pdf say about safety controls?']::text[],
  ARRAY['Use page chunks for exact facts.']::text[],
  'b-worker-llm-summary.pdf deterministic baseline safety controls classified areas',
  8, decode(repeat('01', 32), 'hex'), '{}'::jsonb
);
""",
                    new
                    {
                        tenant = tenantId,
                        docId,
                        revisionId,
                        sectionId,
                        unitId,
                        profileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "deterministic_v1")
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions()));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);
            Assert.True(processed);

            var processedAgain = await worker.ProcessNextJobOnceAsync(CancellationToken.None);
            Assert.False(processedAgain);

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);
            Assert.Equal("done", detailPayload.GetProperty("Status").GetString());
            Assert.True(detailPayload.GetProperty("ResultStored").GetBoolean());
            Assert.Equal("capability_b_worker", detailPayload.GetProperty("ResultCompletedBy").GetString());

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var storedSummary = await conn.ExecuteScalarAsync<string>(
                    """
SELECT summary_text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Contains("b-worker-generated-summary.pdf", storedSummary, StringComparison.Ordinal);
                Assert.Contains("Scope and Purpose", storedSummary, StringComparison.Ordinal);
                Assert.Contains("operational perimeter", storedSummary, StringComparison.OrdinalIgnoreCase);
            }

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_job_claimed");
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_summary_completed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_waits_when_ingestion_is_active()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var docId = Guid.NewGuid();
        var summaryJobId = Guid.NewGuid();
        var ingestionJobId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Idle scheduler tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Idle/scheduler-summary.pdf', 'scheduler-summary.pdf', 'idle', 'indexed',
  3, decode(repeat('11', 32), 'hex'), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES(
  @summaryJobId, @tenant, 'summary.generate', 'queued', @docId, 'Idle/scheduler-summary.pdf', 'medium',
  @summaryPayload::jsonb, now()
);

INSERT INTO ingestion_jobs(
  job_id, tenant_id, action, doc_path, category, status, attempts, locked_by, locked_at, available_at, created_at, started_at, payload
)
VALUES(
  @ingestionJobId, @tenant, 'upsert', 'Idle/current-ingestion.pdf', 'idle', 'running',
  1, 'test-worker', now(), now(), now(), now(), '{}'::jsonb
);
""",
                new
                {
                    tenant = tenantId,
                    docId,
                    summaryJobId,
                    ingestionJobId,
                    summaryPayload = JsonSerializer.Serialize(new
                    {
                        docPath = "Idle/scheduler-summary.pdf",
                        source = "capability_b",
                        executionMode = "server_backoffice",
                        runtimeCapabilityKey = "capability_b.backoffice_generation",
                        runtimeCapabilityStatus = "selected",
                        runtimeCapabilitySelected = true
                    })
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ds);
        services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
        {
            CapabilityBWorkerEnabled = true,
            CapabilityBRequireIngestionIdleForExecution = true,
            CapabilityBAutoEnqueueWhenIngestionIdleEnabled = true,
            CapabilityBIngestionIdleDelaySeconds = 900
        }));
        services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var worker = new CapabilityBBackofficeWorker(
            provider,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

        var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

        Assert.False(processed);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var summaryStatus = await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                new { jobId = summaryJobId });
            var ingestionStatus = await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM ingestion_jobs WHERE job_id=@jobId;",
                new { jobId = ingestionJobId });

            Assert.Equal("queued", summaryStatus);
            Assert.Equal("running", ingestionStatus);
        }
    }

    [Fact]
    public async Task Capability_b_worker_waits_when_rag_activity_is_recent()
    {
        RuntimeCapabilityBRagIdleCoordinator.ResetForTests();
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeef");
        var docId = Guid.NewGuid();
        var summaryJobId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'RAG idle scheduler tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Idle/rag-recent-summary.pdf', 'rag-recent-summary.pdf', 'idle', 'indexed',
  3, decode(repeat('12', 32), 'hex'), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES(
  @summaryJobId, @tenant, 'summary.generate', 'queued', @docId, 'Idle/rag-recent-summary.pdf', 'medium',
  @summaryPayload::jsonb, now()
);
""",
                new
                {
                    tenant = tenantId,
                    docId,
                    summaryJobId,
                    summaryPayload = JsonSerializer.Serialize(new
                    {
                        docPath = "Idle/rag-recent-summary.pdf",
                        source = "capability_b",
                        executionMode = "server_backoffice",
                        runtimeCapabilityKey = "capability_b.backoffice_generation",
                        runtimeCapabilityStatus = "selected",
                        runtimeCapabilitySelected = true
                    })
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ds);
        services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
        {
            CapabilityBWorkerEnabled = true,
            CapabilityBRequireRagIdleForExecution = true,
            CapabilityBRagIdleDelaySeconds = 120,
            CapabilityBAutoEnqueueWhenIngestionIdleEnabled = false,
            CapabilityBRunningJobLeaseTimeoutSeconds = 0
        }));
        services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var worker = new CapabilityBBackofficeWorker(
            provider,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        using var activity = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            new RuntimeGovernanceOptions { CapabilityBRequireRagIdleForExecution = true },
            now);

        var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

        Assert.False(processed);

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var summaryStatus = await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                new { jobId = summaryJobId });

            Assert.Equal("queued", summaryStatus);
        }

        RuntimeCapabilityBRagIdleCoordinator.ResetForTests();
    }

    [Fact]
    public async Task Capability_b_worker_can_process_idle_tenant_when_another_tenant_is_ingesting()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var activeTenantId = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
            var idleTenantId = Guid.Parse("f1f1f1f1-f1f1-f1f1-f1f1-f1f1f1f1f1f1");
            var activeDocId = Guid.NewGuid();
            var idleDocId = Guid.NewGuid();
            var activeJobId = Guid.NewGuid();
            var idleJobId = Guid.NewGuid();
            var ingestionJobId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO tenants(tenant_id, name)
VALUES
(@activeTenant, 'Active ingestion tenant'),
(@idleTenant, 'Idle summary tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES
(
  @activeTenant, @activeDocId, 'Idle/active-tenant-summary.pdf', 'active-tenant-summary.pdf', 'idle', 'indexed',
  3, decode(repeat('66', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
),
(
  @idleTenant, @idleDocId, 'Idle/idle-tenant-summary.pdf', 'idle-tenant-summary.pdf', 'idle', 'indexed',
  3, decode(repeat('77', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES
(
  @activeJobId, @activeTenant, 'summary.generate', 'queued', @activeDocId, 'Idle/active-tenant-summary.pdf', 'medium',
  @activePayload::jsonb, now() - interval '1 minute'
),
(
  @idleJobId, @idleTenant, 'summary.generate', 'queued', @idleDocId, 'Idle/idle-tenant-summary.pdf', 'medium',
  @idlePayload::jsonb, now()
);

INSERT INTO ingestion_jobs(
  job_id, tenant_id, action, doc_path, category, status, attempts, locked_by, locked_at, available_at, created_at, started_at, payload
)
VALUES(
  @ingestionJobId, @activeTenant, 'upsert', 'Idle/current-ingestion.pdf', 'idle', 'running',
  1, 'test-worker', now(), now(), now(), now(), '{}'::jsonb
);
""",
                    new
                    {
                        activeTenant = activeTenantId,
                        idleTenant = idleTenantId,
                        activeDocId,
                        idleDocId,
                        activeJobId,
                        idleJobId,
                        ingestionJobId,
                        activePayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = activeDocId,
                            ["docPath"] = "Idle/active-tenant-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["priorityScore"] = 300
                        }),
                        idlePayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = idleDocId,
                            ["docPath"] = "Idle/idle-tenant-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["priorityScore"] = 100
                        })
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
            {
                CapabilityBWorkerEnabled = true,
                CapabilityBRequireIngestionIdleForExecution = true,
                CapabilityBAutoEnqueueWhenIngestionIdleEnabled = false,
                CapabilityBIngestionIdleDelaySeconds = 900,
                CapabilityBRunningJobLeaseTimeoutSeconds = 0
            }));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var activeStatus = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = activeJobId });
                var idleStatus = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = idleJobId });
                var storedSummary = await conn.ExecuteScalarAsync<string>(
                    """
SELECT summary_text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = idleTenantId, docId = idleDocId });

                Assert.Equal("queued", activeStatus);
                Assert.Equal("done", idleStatus);
                Assert.Contains("idle-tenant-summary.pdf", storedSummary, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_does_not_starve_idle_tenant_behind_busy_tenant_backlog()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var idleTenantId = Guid.Parse("f2f2f2f2-f2f2-f2f2-f2f2-f2f2f2f2f2f2");
            var idleDocId = Guid.NewGuid();
            var idleJobId = Guid.NewGuid();
            var busyRows = Enumerable.Range(0, 36)
                .Select(i => new
                {
                    TenantId = Guid.Parse($"e1e1e1e1-e1e1-e1e1-e1e1-{i + 1:000000000000}"),
                    DocId = Guid.NewGuid(),
                    JobId = Guid.NewGuid(),
                    IngestionJobId = Guid.NewGuid(),
                    PriorityScore = 1000 - i,
                    DocPath = $"CapabilityB/busy-tenant-{i + 1:00}.pdf"
                })
                .ToArray();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();

                await conn.ExecuteAsync(
                    """
INSERT INTO tenants(tenant_id, name)
VALUES (@tenant, 'Idle tenant behind busy backlog');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'CapabilityB/idle-tenant-summary.pdf', 'idle-tenant-summary.pdf', 'capability-b', 'indexed',
  3, decode(repeat('88', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'queued', @docId, 'CapabilityB/idle-tenant-summary.pdf', 'medium',
  @payload::jsonb, now()
);
""",
                    new
                    {
                        tenant = idleTenantId,
                        docId = idleDocId,
                        jobId = idleJobId,
                        payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = idleDocId,
                            ["docPath"] = "CapabilityB/idle-tenant-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["priorityScore"] = 1
                        })
                    });

                foreach (var row in busyRows)
                {
                    await conn.ExecuteAsync(
                        """
INSERT INTO tenants(tenant_id, name)
VALUES (@tenant, @tenantName);

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES(
  @tenant, @docId, @docPath, @docName, 'capability-b', 'indexed',
  3, decode(repeat('99', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'queued', @docId, @docPath, 'medium',
  @payload::jsonb, now() - interval '10 minutes'
);

INSERT INTO ingestion_jobs(
  job_id, tenant_id, action, doc_path, category, status, attempts, locked_by, locked_at, available_at, created_at, started_at, payload
)
VALUES(
  @ingestionJobId, @tenant, 'upsert', @docPath, 'capability-b', 'running',
  1, 'test-worker', now(), now(), now(), now(), '{}'::jsonb
);
""",
                        new
                        {
                            tenant = row.TenantId,
                            tenantName = $"Busy tenant {row.PriorityScore}",
                            docId = row.DocId,
                            docPath = row.DocPath,
                            docName = Path.GetFileName(row.DocPath),
                            jobId = row.JobId,
                            ingestionJobId = row.IngestionJobId,
                            payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                            {
                                ["docId"] = row.DocId,
                                ["docPath"] = row.DocPath,
                                ["level"] = "medium",
                                ["source"] = "capability_b",
                                ["executionMode"] = "server_backoffice",
                                ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                                ["runtimeCapabilityStatus"] = "selected",
                                ["runtimeCapabilitySelected"] = true,
                                ["priorityScore"] = row.PriorityScore
                            })
                        });
                }
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
            {
                CapabilityBWorkerEnabled = true,
                CapabilityBRequireIngestionIdleForExecution = true,
                CapabilityBAutoEnqueueWhenIngestionIdleEnabled = false,
                CapabilityBIngestionIdleDelaySeconds = 900,
                CapabilityBRunningJobLeaseTimeoutSeconds = 0
            }));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var idleStatus = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = idleJobId });
                var busyDoneCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM admin_jobs WHERE job_id = ANY(@jobIds) AND status='done';",
                    new { jobIds = busyRows.Select(row => row.JobId).ToArray() });

                Assert.Equal("done", idleStatus);
                Assert.Equal(0, busyDoneCount);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_requeues_interrupted_running_job_before_processing()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("fafafafa-fafa-fafa-fafa-fafafafafafa");
            var docId = Guid.NewGuid();
            var summaryJobId = Guid.NewGuid();
            var interruptedClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Stale Capability B lease tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Idle/stale-running-summary.pdf', 'stale-running-summary.pdf', 'idle', 'indexed',
  5, decode(repeat('33', 32), 'hex'), 8192, now(), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at, started_at
)
VALUES(
  @summaryJobId, @tenant, 'summary.generate', 'running', @docId, 'Idle/stale-running-summary.pdf', 'medium',
  @summaryPayload::jsonb, now() - interval '2 hours', now() - interval '2 hours'
);
""",
                    new
                    {
                        tenant = tenantId,
                        docId,
                        summaryJobId,
                        summaryPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = docId,
                            ["docPath"] = "Idle/stale-running-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["executionLeaseToken"] = "interrupted-token",
                            ["executionClaimedAt"] = interruptedClaimedAt,
                            ["executionClaimedBy"] = "capability_b_worker"
                        })
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
            {
                CapabilityBWorkerEnabled = true,
                CapabilityBRequireIngestionIdleForExecution = false,
                CapabilityBAutoEnqueueWhenIngestionIdleEnabled = false,
                CapabilityBRunningJobLeaseTimeoutSeconds = 3600
            }));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var status = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = summaryJobId });
                var payloadJson = await conn.ExecuteScalarAsync<string>(
                    "SELECT payload::text FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = summaryJobId });
                var storedSummary = await conn.ExecuteScalarAsync<string>(
                    """
SELECT summary_text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Equal("done", status);
                Assert.Contains("stale-running-summary.pdf", storedSummary, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(payloadJson));

                using var payload = JsonDocument.Parse(payloadJson!);
                Assert.Equal("capability_b_worker", payload.RootElement.GetProperty("staleLeasePreviousClaimedBy").GetString());
                Assert.True(payload.RootElement.TryGetProperty("staleLeaseRequeuedAt", out _));
                var replacementLeaseToken = payload.RootElement.GetProperty("executionLeaseToken").GetString();
                Assert.False(string.IsNullOrWhiteSpace(replacementLeaseToken));
                Assert.NotEqual("interrupted-token", replacementLeaseToken);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_processes_highest_priority_queued_job_first()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0");
            var lowPriorityDocId = Guid.NewGuid();
            var highPriorityDocId = Guid.NewGuid();
            var lowPriorityJobId = Guid.NewGuid();
            var highPriorityJobId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Priority Capability B worker tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES
(
  @tenant, @lowPriorityDocId, 'Priority/low-priority-worker-summary.pdf', 'low-priority-worker-summary.pdf', 'priority', 'indexed',
  3, decode(repeat('44', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
),
(
  @tenant, @highPriorityDocId, 'Priority/high-priority-worker-summary.pdf', 'high-priority-worker-summary.pdf', 'priority', 'indexed',
  3, decode(repeat('55', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at
)
VALUES
(
  @lowPriorityJobId, @tenant, 'summary.generate', 'queued', @lowPriorityDocId, 'Priority/low-priority-worker-summary.pdf', 'medium',
  @lowPriorityPayload::jsonb, now() - interval '10 minutes'
),
(
  @highPriorityJobId, @tenant, 'summary.generate', 'queued', @highPriorityDocId, 'Priority/high-priority-worker-summary.pdf', 'medium',
  @highPriorityPayload::jsonb, now()
);
""",
                    new
                    {
                        tenant = tenantId,
                        lowPriorityDocId,
                        highPriorityDocId,
                        lowPriorityJobId,
                        highPriorityJobId,
                        lowPriorityPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = lowPriorityDocId,
                            ["docPath"] = "Priority/low-priority-worker-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["priorityScore"] = 120
                        }),
                        highPriorityPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["docId"] = highPriorityDocId,
                            ["docPath"] = "Priority/high-priority-worker-summary.pdf",
                            ["level"] = "medium",
                            ["source"] = "capability_b",
                            ["executionMode"] = "server_backoffice",
                            ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                            ["runtimeCapabilityStatus"] = "selected",
                            ["runtimeCapabilitySelected"] = true,
                            ["priorityScore"] = 200
                        })
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
            {
                CapabilityBWorkerEnabled = true,
                CapabilityBRequireIngestionIdleForExecution = false,
                CapabilityBAutoEnqueueWhenIngestionIdleEnabled = false,
                CapabilityBRunningJobLeaseTimeoutSeconds = 0
            }));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var lowStatus = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = lowPriorityJobId });
                var highStatus = await conn.ExecuteScalarAsync<string>(
                    "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
                    new { jobId = highPriorityJobId });

                Assert.Equal("queued", lowStatus);
                Assert.Equal("done", highStatus);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_auto_enqueues_missing_summary_when_ingestion_is_idle()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("edededed-eded-eded-eded-edededededed");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Auto enqueue tenant');

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, file_size, file_mtime, updated_at, created_at,
  ingestion_version, indexed_version, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Idle/auto-summary.pdf', 'auto-summary.pdf', 'idle', 'indexed',
  4, decode(repeat('22', 32), 'hex'), 4096, now(), now(), now(), 1, 1, false
);
""",
                    new
                    {
                        tenant = tenantId,
                        docId
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions
            {
                CapabilityBWorkerEnabled = true,
                CapabilityBRequireIngestionIdleForExecution = true,
                CapabilityBAutoEnqueueWhenIngestionIdleEnabled = true,
                CapabilityBIngestionIdleDelaySeconds = 900,
                CapabilityBAutoEnqueueBatchSize = 5
            }));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);

            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var storedSummary = await conn.ExecuteScalarAsync<string>(
                    """
SELECT summary_text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });
                var generatedJobs = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM admin_jobs
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND job_type='summary.generate'
  AND status='done'
  AND payload ->> 'source' = 'capability_b';
""",
                    new { tenant = tenantId, docId });

                Assert.Contains("auto-summary.pdf", storedSummary, StringComparison.Ordinal);
                Assert.Equal(1, generatedJobs);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Capability_b_worker_processes_next_queued_job_and_stores_llm_summary_when_runtime_is_available()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var sectionId = Guid.NewGuid();
            var unitId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  page_count, content_hash, updated_at, created_at, ingestion_version, indexed_version,
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-worker-llm-summary.pdf', 'b-worker-llm-summary.pdf', 'atex', 'indexed',
  7, decode(repeat('ef', 32), 'hex'), now(), now(), 1, 1, false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size, source_mtime,
  ingestion_version, indexed_version, published_at, created_at
)
VALUES(
  @revisionId, @tenant, @docId, 'ATEX/b-worker-llm-summary.pdf', decode(repeat('ef', 32), 'hex'), 2048, now(),
  1, 1, now(), now()
);

INSERT INTO document_sections(
  section_id, tenant_id, revision_id, ordinal, title, section_level, page_start, page_end, metadata, created_at
)
VALUES(
  @sectionId, @tenant, @revisionId, 0, 'Scope and Purpose', 1, 1, 2, '{}'::jsonb, now()
);

INSERT INTO document_units(
  unit_id, tenant_id, revision_id, section_id, ordinal, page_start, page_end, text_content,
  char_count, token_count, metadata, created_at
)
VALUES(
  @unitId, @tenant, @revisionId, @sectionId, 0, 1, 1,
  'This document defines the operational perimeter and the required safety controls for classified areas.',
  98, 18, '{}'::jsonb, now()
);

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version,
  language, summary_text, keywords, search_text, token_count, checksum, metadata
)
VALUES(
  gen_random_uuid(), @tenant, @revisionId, @docId, 'deterministic_v1',
  'en', 'Baseline profile for b-worker-llm-summary.pdf.',
  ARRAY['baseline-keyword']::text[],
  'Operational perimeter and required safety controls for classified areas.',
  10, decode(repeat('01', 32), 'hex'), '{}'::jsonb
);
""",
                    new
                    {
                        tenant = tenantId,
                        docId,
                        revisionId,
                        sectionId,
                        unitId
                    });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ds);
            services.AddSingleton<IOptions<RuntimeGovernanceOptions>>(Options.Create(new RuntimeGovernanceOptions()));
            services.AddSingleton<IOptions<RagOptions>>(Options.Create(CreateRagOptions()));
            services.AddSingleton<IOptions<ChatOptions>>(Options.Create(new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));
            services.AddSingleton<IHttpClientFactory>(new WorkerProfileEnrichmentHttpClientFactory());
            services.AddSingleton<LocalLlmChatClient>(sp => new LocalLlmChatClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<ChatOptions>>().Value));
            services.AddSingleton<CapabilityBBackofficeSummaryService>(sp => new CapabilityBBackofficeSummaryService(
                sp.GetRequiredService<LocalLlmChatClient>(),
                sp.GetRequiredService<IOptions<ChatOptions>>().Value));
            services.AddSingleton<DocumentProfileEnrichmentService>();
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());

            using var provider = services.BuildServiceProvider();
            var worker = new CapabilityBBackofficeWorker(
                provider,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CapabilityBBackofficeWorker>>());

            var processed = await worker.ProcessNextJobOnceAsync(CancellationToken.None);
            Assert.True(processed);

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var storedSummary = await conn.ExecuteScalarAsync<string>(
                    """
SELECT summary_text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Contains("Operational summary for b-worker-llm-summary.pdf", storedSummary, StringComparison.Ordinal);
                Assert.Contains("Scope and Purpose", storedSummary, StringComparison.Ordinal);

                var enrichedProfile = await conn.QuerySingleAsync<(string SummaryText, string[] Keywords, string SearchText)>(
                    """
SELECT summary_text, keywords, search_text
FROM document_profiles
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND profile_version='llm_backoffice_v1'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Contains("LLM enriched profile", enrichedProfile.SummaryText, StringComparison.Ordinal);
                Assert.Contains("classified safety controls", enrichedProfile.Keywords);
                Assert.Contains("baseline-keyword", enrichedProfile.Keywords);
                Assert.Contains("classified safety controls", enrichedProfile.SearchText, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task Summary_source_hash_changes_when_indexed_version_changes_without_file_hash_change()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'OCR/revision-aware-summary.pdf', 'revision-aware-summary.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('ab', 32), 'hex'), 4096, now(), false
);
""",
            new { tenant = tenantId, docId });

        var sourceHashV1 = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(sourceHashV1));

        await RuntimeCapabilityBExecutionStore.UpsertDocumentSummaryAsync(
            conn,
            tenantId,
            docId,
            "medium",
            "fr",
            sourceHashV1!,
            "Summary generated from indexed version 1.",
            "{}",
            CancellationToken.None);

        await SeedCurrentLlmProfileAsync(conn, tenantId, docId);
        var freshCandidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: null,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);
        Assert.DoesNotContain(freshCandidates, candidate => candidate.DocId == docId);

        await conn.ExecuteAsync(
            """
UPDATE documents
SET indexed_version = 2,
    ingestion_version = 2,
    updated_at = now() + interval '1 minute'
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
            new { tenant = tenantId, docId });

        var sourceHashV2 = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(sourceHashV2));
        Assert.NotEqual(sourceHashV1, sourceHashV2);

        var staleCandidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: null,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);
        var candidate = Assert.Single(staleCandidates, candidate => candidate.DocId == docId);
        Assert.Equal("stale", candidate.SummaryState);
        Assert.Contains("summary_stale", candidate.Reasons);

        var backlogTenants = await RuntimeCapabilityBIngestionIdleCoordinator.LoadTenantsWithSummaryBacklogAsync(
            conn,
            maxTenants: 10,
            CancellationToken.None);
        Assert.Contains(tenantId, backlogTenants);
    }

    [Fact]
    public async Task CapabilityB_candidates_repair_missing_llm_profile_even_when_summary_is_fresh()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Profile repair tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/profile-repair.pdf', 'profile-repair.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('d2', 32), 'hex'), 4096, now(), false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, 'Generic/profile-repair.pdf',
  decode(repeat('d2', 32), 'hex'), 4096, now(), 1, 1, now()
);
""",
            new { tenant = tenantId, docId, revisionId });

        var sourceHash = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(sourceHash));

        await RuntimeCapabilityBExecutionStore.UpsertDocumentSummaryAsync(
            conn,
            tenantId,
            docId,
            "medium",
            "en",
            sourceHash!,
            "Fresh summary without a backoffice profile.",
            "{}",
            CancellationToken.None);

        var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: 10,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);

        var candidate = Assert.Single(candidates, item => item.DocId == docId);
        Assert.Equal("fresh", candidate.SummaryState);
        Assert.Equal("missing", candidate.ProfileState);
        Assert.False(candidate.HasBackofficeProfile);
        Assert.False(candidate.PolicyBlocked);
        Assert.Contains("profile_missing", candidate.Reasons);

        await conn.ExecuteAsync(
            """
INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
  summary_text, keywords, entities, topics, hypothetical_questions, limits,
  search_text, token_count, checksum, metadata
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1', 'en',
  'LLM profile stored for profile repair test.',
  ARRAY['profile-repair']::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  'LLM profile stored for profile repair test.',
  7,
  decode(repeat('d3', 32), 'hex'),
  jsonb_build_object('contentCardEvidenceSchemaVersion', @evidenceSchemaVersion)
);
""",
            new
            {
                profileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "llm_backoffice_v1"),
                tenant = tenantId,
                revisionId,
                docId,
                evidenceSchemaVersion = DocumentFoundationRepo.ContentCardEvidenceSchemaVersion
            });

        var repairedCandidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: 10,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);

        Assert.DoesNotContain(repairedCandidates, item => item.DocId == docId);
    }

    [Fact]
    public async Task Summary_management_surfaces_include_fresh_summaries_missing_llm_profile()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Summary surface profile tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/surface-profile-missing.pdf', 'surface-profile-missing.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('d4', 32), 'hex'), 4096, now(), false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, 'Generic/surface-profile-missing.pdf',
  decode(repeat('d4', 32), 'hex'), 4096, now(), 1, 1, now()
);
""",
            new { tenant = tenantId, docId, revisionId });

        var sourceHash = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(sourceHash));

        await RuntimeCapabilityBExecutionStore.UpsertDocumentSummaryAsync(
            conn,
            tenantId,
            docId,
            "medium",
            "en",
            sourceHash!,
            "Fresh summary that still needs an LLM backoffice profile.",
            "{}",
            CancellationToken.None);

        var missingCtx = BuildAdminContext(backofficeEnabled: true);
        var missingResult = await SummaryEndpoints.MissingSummariesAsync(
            missingCtx,
            ds,
            categoryPath: null,
            categoryRef: null,
            limit: 20,
            offset: 0);
        var missingPayload = await ExecuteResultAsync<JsonElement>(missingResult, missingCtx);
        var missingItem = Assert.Single(
            missingPayload.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("docId").GetGuid() == docId);

        Assert.Equal(1, missingPayload.GetProperty("total").GetInt32());
        Assert.Equal(0, missingPayload.GetProperty("missingStored").GetInt32());
        Assert.Equal(0, missingPayload.GetProperty("staleStored").GetInt32());
        Assert.Equal(1, missingPayload.GetProperty("profileMissing").GetInt32());
        Assert.Equal("fresh", missingItem.GetProperty("summaryState").GetString());
        Assert.Equal("missing", missingItem.GetProperty("capabilityBProfileState").GetString());
        Assert.False(missingItem.GetProperty("capabilityBHasBackofficeProfile").GetBoolean());
        Assert.True(missingItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
        Assert.Contains("profile_missing", GetStringArray(missingItem.GetProperty("capabilityBReasons")));

        var catalogCtx = BuildAdminContext(backofficeEnabled: true);
        var catalogResult = await SummaryEndpoints.CatalogSummariesAsync(
            catalogCtx,
            ds,
            categoryRef: null,
            categoryPath: null,
            pageSize: 20,
            maxpagesize: null,
            cursor: null);
        var catalogPayload = await ExecuteResultAsync<JsonElement>(catalogResult, catalogCtx);
        var catalogItem = Assert.Single(
            catalogPayload.GetProperty("value").EnumerateArray(),
            item => item.GetProperty("docId").GetGuid() == docId);

        var catalogTotals = catalogPayload.GetProperty("totals");
        Assert.Equal(1, catalogTotals.GetProperty("total").GetInt32());
        Assert.Equal(0, catalogTotals.GetProperty("missingStored").GetInt32());
        Assert.Equal(0, catalogTotals.GetProperty("staleStored").GetInt32());
        Assert.Equal(1, catalogTotals.GetProperty("profileMissing").GetInt32());
        Assert.Equal("fresh", catalogItem.GetProperty("summaryState").GetString());
        Assert.Equal("missing", catalogItem.GetProperty("capabilityBProfileState").GetString());
        Assert.False(catalogItem.GetProperty("capabilityBHasBackofficeProfile").GetBoolean());
        Assert.True(catalogItem.GetProperty("capabilityBReadyToEnqueue").GetBoolean());
        Assert.Contains("profile_missing", GetStringArray(catalogItem.GetProperty("capabilityBReasons")));

        await conn.ExecuteAsync(
            """
INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version, language,
  summary_text, keywords, entities, topics, hypothetical_questions, limits,
  search_text, token_count, checksum, metadata
)
VALUES(
  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1', 'en',
  'LLM profile stored for surface profile test.',
  ARRAY['surface-profile']::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  ARRAY[]::text[],
  'LLM profile stored for surface profile test.',
  7,
  decode(repeat('d5', 32), 'hex'),
  jsonb_build_object('contentCardEvidenceSchemaVersion', @evidenceSchemaVersion)
);
""",
            new
            {
                profileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "llm_backoffice_v1"),
                tenant = tenantId,
                revisionId,
                docId,
                evidenceSchemaVersion = DocumentFoundationRepo.ContentCardEvidenceSchemaVersion
            });

        var repairedCtx = BuildAdminContext(backofficeEnabled: true);
        var repairedResult = await SummaryEndpoints.MissingSummariesAsync(
            repairedCtx,
            ds,
            categoryPath: null,
            categoryRef: null,
            limit: 20,
            offset: 0);
        var repairedPayload = await ExecuteResultAsync<JsonElement>(repairedResult, repairedCtx);

        Assert.Equal(0, repairedPayload.GetProperty("total").GetInt32());
        Assert.Empty(repairedPayload.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task LoadCapabilityBDocumentAsync_returns_source_hash_from_the_same_document_snapshot()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();

        await conn.ExecuteAsync(
            """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/snapshot-source-hash.pdf', 'snapshot-source-hash.pdf', 'generic', 'indexed',
  now(), now(), 3, 3,
  decode(repeat('cd', 32), 'hex'), 8192, now(), false
);
""",
            new { tenant = tenantId, docId });

        var doc = await RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);
        var separatelyComputed = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            conn,
            tenantId,
            docId,
            CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal(3, doc!.IndexedVersion);
        Assert.False(string.IsNullOrWhiteSpace(doc.SourceHash));
        Assert.Equal(separatelyComputed, doc.SourceHash);
    }

    [Fact]
    public async Task SubmitSummaryAsync_rejects_stale_expected_source_hash_and_does_not_store_summary()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'OCR/manual-stale-summary.pdf', 'manual-stale-summary.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('ab', 32), 'hex'), 4096, now(), false
);
""",
                new { tenant = tenantId, docId });

            var sourceHashV1 = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
                conn,
                tenantId,
                docId,
                CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(sourceHashV1));

            await conn.ExecuteAsync(
                """
UPDATE documents
SET indexed_version = 2,
    ingestion_version = 2,
    updated_at = now() + interval '1 minute'
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                new { tenant = tenantId, docId });

            var staleSubmitCtx = BuildAdminContext(backofficeEnabled: true);
            var staleSubmitResult = await SummaryEndpoints.SubmitSummaryAsync(
                staleSubmitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    SourceHash: sourceHashV1,
                    SummaryText: "Stale summary should not be stored."));
            var staleSubmitPayload = await ExecuteResultAsync<JsonElement>(staleSubmitResult, staleSubmitCtx);
            Assert.Equal("source_hash_mismatch", staleSubmitPayload.GetProperty("error").GetString());

            var storedCount = await conn.ExecuteScalarAsync<int>(
                """
SELECT COUNT(*)
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                new { tenant = tenantId, docId });
            Assert.Equal(0, storedCount);
        }
    }

    [Fact]
    public async Task SubmitSummaryAsync_requires_expected_source_hash_and_does_not_store_summary()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/source-hash-required.pdf', 'source-hash-required.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('be', 32), 'hex'), 2048, now(), false
);
""",
                new { tenant = tenantId, docId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var submitCtx = BuildAdminContext(backofficeEnabled: true);
        var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
            submitCtx,
            ds,
            new SummaryEndpoints.SummaryCommand(
                DocId: docId,
                Level: "medium",
                SummaryText: "Summary without a source hash should not be stored."));
        var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);
        Assert.Equal("source_hash_required", submitPayload.GetProperty("error").GetString());

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var storedCount = await conn.ExecuteScalarAsync<int>(
                """
SELECT COUNT(*)
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                new { tenant = tenantId, docId });
            Assert.Equal(0, storedCount);
        }
    }

    [Fact]
    public async Task SubmitSummaryAsync_strips_nul_characters_from_summary_text_and_meta()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'OCR/nul-submit-summary.pdf', 'nul-submit-summary.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('bd', 32), 'hex'), 2048, now(), false
);
""",
                new { tenant = tenantId, docId });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        var meta = JsonSerializer.SerializeToElement(new
        {
            note = "meta\0value",
            nested = new { value = "inner\0value" }
        });
        await using var sourceHashConn = await ds.OpenConnectionAsync();
        var sourceHash = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
            sourceHashConn,
            tenantId,
            docId,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(sourceHash));

        var submitCtx = BuildAdminContext(backofficeEnabled: true);
        var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
            submitCtx,
            ds,
            new SummaryEndpoints.SummaryCommand(
                DocId: docId,
                Level: "medium",
                SourceHash: sourceHash,
                SummaryText: "Stored\0 summary",
                Meta: meta));

        var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);
        Assert.True(submitPayload.GetProperty("stored").GetBoolean());

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var stored = await conn.QuerySingleAsync<(string SummaryText, string SummaryMeta)>(
                """
SELECT summary_text AS "SummaryText", summary_meta::text AS "SummaryMeta"
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                new { tenant = tenantId, docId });

            Assert.Equal("Stored summary", stored.SummaryText);
            Assert.DoesNotContain("\0", stored.SummaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u0000", stored.SummaryMeta, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("metavalue", stored.SummaryMeta, StringComparison.Ordinal);
            Assert.Contains("innervalue", stored.SummaryMeta, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CapabilityBCompleteAsync_cancels_stale_source_without_storing_summary()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'OCR/capability-b-stale-summary.pdf', 'capability-b-stale-summary.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('bc', 32), 'hex'), 4096, now(), false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var claimCtx = BuildAdminContext(backofficeEnabled: true);
            var claimResult = await AdminRuntimeEndpoints.CapabilityBClaimAsync(
                claimCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBClaimRequestDto(JobId: jobId, ExecutorId: "capability_b_worker"));
            var claimPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBClaimResponseDto>(claimResult, claimCtx);

            await using (var conn = await ds.OpenConnectionAsync())
            {
                var sourceHashV1 = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
                    conn,
                    tenantId,
                    docId,
                    CancellationToken.None);
                Assert.False(string.IsNullOrWhiteSpace(sourceHashV1));

                await conn.ExecuteAsync(
                    """
UPDATE documents
SET indexed_version = 2,
    ingestion_version = 2,
    updated_at = now() + interval '1 minute'
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                    new { tenant = tenantId, docId });

                var staleCompleteCtx = BuildAdminContext(backofficeEnabled: true);
                var staleCompleteResult = await AdminRuntimeEndpoints.CapabilityBCompleteAsync(
                    staleCompleteCtx,
                    ds,
                    new AdminRuntimeCapabilityBCompleteRequestDto(
                        JobId: jobId.Value,
                        LeaseToken: claimPayload.LeaseToken,
                        SummaryText: "Stale capability B summary should not be stored.",
                        DocLanguage: "en",
                        SourceHash: sourceHashV1));
                var staleCompletePayload = await ExecuteResultAsync<JsonElement>(staleCompleteResult, staleCompleteCtx);
                Assert.Equal("source_hash_mismatch", staleCompletePayload.GetProperty("error").GetString());

                var storedCount = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                    new { tenant = tenantId, docId });
                Assert.Equal(0, storedCount);

                var job = await conn.QuerySingleAsync<(string Status, string Result)>(
                    """
SELECT status AS "Status", result::text AS "Result"
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id=@jobId;
""",
                    new { tenant = tenantId, jobId = jobId.Value });
                Assert.Equal("canceled", job.Status);
                using var jobResult = JsonDocument.Parse(job.Result);
                Assert.Equal("stale_source", jobResult.RootElement.GetProperty("reason").GetString());
                Assert.Equal("source_hash_mismatch", jobResult.RootElement.GetProperty("error").GetString());
            }

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.Contains(eventsPayload.Items, item => item.EventType == "capability_b_job_canceled");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task CapabilityBCompleteWithGeneratedProfile_rejects_stale_profile_revision_without_storing_partial_results()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/capability-b-atomic-profile.pdf', 'capability-b-atomic-profile.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('bd', 32), 'hex'), 4096, now(), false
);

INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
VALUES(
  @revisionId, @tenant, @docId, 'Generic/capability-b-atomic-profile.pdf',
  decode(repeat('bd', 32), 'hex'), 4096, now(), 1, 1, now()
);
""",
                    new { tenant = tenantId, docId, revisionId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var claim = await RuntimeCapabilityBExecutionCommandService.ClaimCapabilityBBackofficeExecutionAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBClaimRequestDto(JobId: jobId, ExecutorId: "capability_b_worker"),
                CancellationToken.None);

            Assert.Null(claim.Error);
            var lease = claim.Payload!;

            var generatedProfile = new ProjectedDocumentProfile(
                ProfileVersion: "llm_backoffice_v1",
                Language: "en",
                SummaryText: "Generated profile that must not be persisted with a stale revision.",
                Keywords: ["atomic-profile"],
                Entities: [],
                Topics: [],
                HypotheticalQuestions: [],
                Limits: [],
                SearchText: "Generated profile atomic profile",
                TokenCount: 5,
                Checksum: Enumerable.Repeat((byte)0x5a, 32).ToArray(),
                ContentCards:
                [
                    new DocumentProfileContentCard(
                        "Atomic profile card",
                        PageStart: 1,
                        PageEnd: 1,
                        Kind: "section",
                        Signals: ["atomic-profile"])
                ]);

            var completion = await RuntimeCapabilityBExecutionCommandService.CompleteCapabilityBBackofficeExecutionWithGeneratedProfileAsync(
                tenantId,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: lease.JobId,
                    LeaseToken: lease.LeaseToken,
                    SummaryText: "Summary must roll back with the stale generated profile.",
                    DocLanguage: "en"),
                generatedProfileRevisionId: Guid.NewGuid(),
                generatedProfile: generatedProfile,
                ct: CancellationToken.None);

            Assert.Equal("generated_profile_revision_stale", completion.Error);
            Assert.Null(completion.Payload);

            await using (var conn = await ds.OpenConnectionAsync())
            {
                var storedSummaryCount = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId;
""",
                    new { tenant = tenantId, docId });
                Assert.Equal(0, storedSummaryCount);

                var generatedProfileCount = await conn.ExecuteScalarAsync<int>(
                    """
SELECT COUNT(*)
FROM document_profiles
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND profile_version='llm_backoffice_v1';
""",
                    new { tenant = tenantId, docId });
                Assert.Equal(0, generatedProfileCount);

                var jobStatus = await conn.ExecuteScalarAsync<string>(
                    """
SELECT status
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id=@jobId;
""",
                    new { tenant = tenantId, jobId = lease.JobId });
                Assert.Equal("running", jobStatus);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task CapabilityBTerminalUpdates_require_current_execution_lease_token()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var jobId = Guid.NewGuid();
        const string currentLease = "lease-current";

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Lease terminal guard tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, level, payload, created_at, started_at
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'running', 'medium',
  @payload::jsonb, now(), now()
);
""",
            new
            {
                tenant = tenantId,
                jobId,
                payload = JsonSerializer.Serialize(new
                {
                    source = "capability_b",
                    executionLeaseToken = currentLease,
                    executionClaimedBy = "worker-b"
                })
            });

        var staleComplete = await RuntimeCapabilityBExecutionStore.TryCompleteJobAsync(
            conn,
            tenantId,
            jobId,
            leaseToken: "lease-stale",
            finishedAt: DateTimeOffset.UtcNow,
            resultJson: "{}",
            ct: CancellationToken.None);
        Assert.False(staleComplete);

        var staleFail = await RuntimeCapabilityBExecutionStore.TryFailJobAsync(
            conn,
            tenantId,
            jobId,
            leaseToken: "lease-stale",
            failedAt: DateTimeOffset.UtcNow,
            resultJson: "{}",
            lastError: "stale failure",
            ct: CancellationToken.None);
        Assert.False(staleFail);

        var statusAfterStaleAttempts = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
            new { jobId });
        Assert.Equal("running", statusAfterStaleAttempts);

        var currentFail = await RuntimeCapabilityBExecutionStore.TryFailJobAsync(
            conn,
            tenantId,
            jobId,
            leaseToken: currentLease,
            failedAt: DateTimeOffset.UtcNow,
            resultJson: """{"stored":false}""",
            lastError: "expected failure",
            ct: CancellationToken.None);
        Assert.True(currentFail);
    }

    [Fact]
    public async Task CapabilityBHeartbeat_requires_current_lease_and_prevents_stale_requeue()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var jobId = Guid.NewGuid();
        const string currentLease = "lease-current";
        var oldClaimedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var heartbeatAt = DateTimeOffset.UtcNow;

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Capability B heartbeat tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, level, payload, created_at, started_at
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'running', 'medium',
  @payload::jsonb, now(), @oldClaimedAt
);
""",
            new
            {
                tenant = tenantId,
                jobId,
                oldClaimedAt = oldClaimedAt.UtcDateTime,
                payload = JsonSerializer.Serialize(new
                {
                    source = "capability_b",
                    executionLeaseToken = currentLease,
                    executionClaimedAt = oldClaimedAt,
                    executionClaimedBy = "capability_b_worker"
                })
            });

        var staleHeartbeat = await RuntimeCapabilityBExecutionStore.TryHeartbeatJobAsync(
            conn,
            tenantId,
            jobId,
            leaseToken: "lease-stale",
            heartbeatAt,
            CancellationToken.None);
        Assert.False(staleHeartbeat);

        var currentHeartbeat = await RuntimeCapabilityBExecutionStore.TryHeartbeatJobAsync(
            conn,
            tenantId,
            jobId,
            currentLease,
            heartbeatAt,
            CancellationToken.None);
        Assert.True(currentHeartbeat);

        var requeued = await RuntimeCapabilityBExecutionStore.RequeueStaleCapabilityBWorkerJobsAsync(
            conn,
            staleBefore: DateTimeOffset.UtcNow.AddHours(-1),
            executorId: "capability_b_worker",
            CancellationToken.None);
        Assert.Equal(0, requeued);

        var status = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM admin_jobs WHERE job_id=@jobId;",
            new { jobId });
        Assert.Equal("running", status);

        var payloadJson = await conn.ExecuteScalarAsync<string>(
            "SELECT payload::text FROM admin_jobs WHERE job_id=@jobId;",
            new { jobId });
        using var payload = JsonDocument.Parse(payloadJson!);
        Assert.True(payload.RootElement.TryGetProperty("executionHeartbeatAt", out _));
    }

    [Fact]
    public async Task CapabilityBCandidates_do_not_cool_down_stale_source_cancellations()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(
            """
INSERT INTO tenants(tenant_id, name)
VALUES(@tenant, 'Stale source candidate tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/stale-source-candidate.pdf', 'stale-source-candidate.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('c1', 32), 'hex'), 4096, now(), false
);

INSERT INTO admin_jobs(
  job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, result, last_error,
  created_at, started_at, finished_at, canceled_at
)
VALUES(
  @jobId, @tenant, 'summary.generate', 'canceled', @docId, 'Generic/stale-source-candidate.pdf', 'medium',
  @payload::jsonb, @result::jsonb, 'source_hash_mismatch',
  now() - interval '1 minute', now() - interval '1 minute', now(), now()
);
""",
            new
            {
                tenant = tenantId,
                docId,
                jobId,
                payload = JsonSerializer.Serialize(new
                {
                    source = "capability_b",
                    executionMode = "server_backoffice"
                }),
                result = JsonSerializer.Serialize(new
                {
                    stored = false,
                    reason = "stale_source",
                    error = "source_hash_mismatch"
                })
            });

        var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
            conn,
            tenantId,
            category: null,
            limit: 10,
            options: new RuntimeGovernanceOptions(),
            includeFresh: false,
            ct: CancellationToken.None);

        var candidate = Assert.Single(candidates, item => item.DocId == docId);
        Assert.Equal("missing", candidate.SummaryState);
        Assert.Equal("canceled", candidate.LastJobStatus);
        Assert.Equal("source_hash_mismatch", candidate.LastJobError);
        Assert.False(candidate.PolicyBlocked);
        Assert.Null(candidate.PolicyBlockReason);
        Assert.Equal("enqueue_summary_generation", candidate.RecommendedAction);
        Assert.Contains("summary_missing", candidate.Reasons);
        Assert.DoesNotContain("recent_summary_cancellation", candidate.Reasons);
    }

    [Fact]
    public async Task CapabilityBCompleteAsync_strips_nul_characters_from_summary_text_and_meta()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'OCR/capability-b-nul-summary.pdf', 'capability-b-nul-summary.pdf', 'ocr', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('be', 32), 'hex'), 4096, now(), false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var jobId = Assert.Single(enqueue.Payload!.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var claimCtx = BuildAdminContext(backofficeEnabled: true);
            var claimResult = await AdminRuntimeEndpoints.CapabilityBClaimAsync(
                claimCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBClaimRequestDto(JobId: jobId, ExecutorId: "capability_b_worker"));
            var claimPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBClaimResponseDto>(claimResult, claimCtx);

            var meta = JsonSerializer.SerializeToElement(new
            {
                strategy = "llm\0backoffice",
                qualitySignals = new[] { "signal\0one" },
                fallbackUsed = true,
                fallbackReason = "llm_timeout",
                llmError = "llm_timeout",
                llmFailureKind = "llm_timeout",
                llmFailureCategory = "timeout",
                llmDurationMs = 123,
                llmBytesRead = 0,
                llmModel = "backoffice\0model"
            });
            var completeCtx = BuildAdminContext(backofficeEnabled: true);
            var completeResult = await AdminRuntimeEndpoints.CapabilityBCompleteAsync(
                completeCtx,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: jobId.Value,
                    LeaseToken: claimPayload.LeaseToken,
                    SummaryText: "Capability\0 summary",
                    DocLanguage: "en",
                    Meta: meta));
            var completePayload = await ExecuteResultAsync<JsonElement>(completeResult, completeCtx);
            Assert.True(completePayload.GetProperty("stored").GetBoolean());

            await using (var conn = await ds.OpenConnectionAsync())
            {
                var stored = await conn.QuerySingleAsync<(string SummaryText, string SummaryMeta, string JobResult)>(
                    """
SELECT
  s.summary_text AS "SummaryText",
  s.summary_meta::text AS "SummaryMeta",
  j.result::text AS "JobResult"
FROM document_summaries s
JOIN admin_jobs j
  ON j.tenant_id=s.tenant_id
 AND j.doc_id=s.doc_id
WHERE s.tenant_id=@tenant AND s.doc_id=@docId AND s.level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.Equal("Capability summary", stored.SummaryText);
                Assert.DoesNotContain("\0", stored.SummaryText, StringComparison.Ordinal);
                Assert.DoesNotContain("\\u0000", stored.SummaryMeta, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("llmbackoffice", stored.SummaryMeta, StringComparison.Ordinal);
                Assert.Contains("signalone", stored.SummaryMeta, StringComparison.Ordinal);
                Assert.DoesNotContain("\\u0000", stored.JobResult, StringComparison.OrdinalIgnoreCase);
                using var jobResult = JsonDocument.Parse(stored.JobResult);
                Assert.True(jobResult.RootElement.GetProperty("fallbackUsed").GetBoolean());
                Assert.Equal("llm_timeout", jobResult.RootElement.GetProperty("fallbackReason").GetString());
                Assert.Equal("llm_timeout", jobResult.RootElement.GetProperty("llmError").GetString());
                Assert.Equal("llm_timeout", jobResult.RootElement.GetProperty("llmFailureKind").GetString());
                Assert.Equal("timeout", jobResult.RootElement.GetProperty("llmFailureCategory").GetString());
                Assert.Equal(123, jobResult.RootElement.GetProperty("llmDurationMs").GetInt32());
                Assert.Equal(0, jobResult.RootElement.GetProperty("llmBytesRead").GetInt32());
                Assert.Contains("backofficemodel", stored.JobResult, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    [Fact]
    public async Task SubmitSummaryAsync_rejects_non_summary_and_mismatched_admin_jobs()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var otherDocId = Guid.NewGuid();
        var nonSummaryJobId = Guid.NewGuid();
        var mismatchedJobId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES
(
  @tenant, @docId, 'Generic/submit-guard.pdf', 'submit-guard.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('c1', 32), 'hex'), 2048, now(), false
),
(
  @tenant, @otherDocId, 'Generic/other-submit-guard.pdf', 'other-submit-guard.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('c2', 32), 'hex'), 4096, now(), false
);

INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, level, payload, created_at)
VALUES
(
  @nonSummaryJobId,
  @tenant,
  'ingestion.reindex',
  'queued',
  @docId,
  'medium',
  '{}'::jsonb,
  now()
),
(
  @mismatchedJobId,
  @tenant,
  'summary.request',
  'queued',
  @otherDocId,
  'medium',
  jsonb_build_object('docId', @otherDocId, 'executionMode', 'client_admin'),
  now()
);
""",
                new
                {
                    tenant = tenantId,
                    docId,
                    otherDocId,
                    nonSummaryJobId,
                    mismatchedJobId
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        string sourceHash;
        await using (var conn = await ds.OpenConnectionAsync())
        {
            sourceHash = (await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
                conn,
                tenantId,
                docId,
                CancellationToken.None))!;
            Assert.False(string.IsNullOrWhiteSpace(sourceHash));
        }

        var nonSummaryCtx = BuildAdminContext(backofficeEnabled: true);
        var nonSummaryResult = await SummaryEndpoints.SubmitSummaryAsync(
            nonSummaryCtx,
            ds,
            new SummaryEndpoints.SummaryCommand(
                DocId: docId,
                Level: "medium",
                JobId: nonSummaryJobId,
                SourceHash: sourceHash,
                SummaryText: "This should not attach to a non-summary job."));
        var nonSummaryPayload = await ExecuteResultAsync<JsonElement>(nonSummaryResult, nonSummaryCtx);
        Assert.Equal("summary_job_required", nonSummaryPayload.GetProperty("error").GetString());

        var mismatchCtx = BuildAdminContext(backofficeEnabled: true);
        var mismatchResult = await SummaryEndpoints.SubmitSummaryAsync(
            mismatchCtx,
            ds,
            new SummaryEndpoints.SummaryCommand(
                DocId: docId,
                Level: "medium",
                JobId: mismatchedJobId,
                SourceHash: sourceHash,
                SummaryText: "This should not attach to another document's job."));
        var mismatchPayload = await ExecuteResultAsync<JsonElement>(mismatchResult, mismatchCtx);
        Assert.Equal("summary_job_document_mismatch", mismatchPayload.GetProperty("error").GetString());
        Assert.Equal(otherDocId, mismatchPayload.GetProperty("jobDocId").GetGuid());

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var storedSummaryCount = await conn.QuerySingleAsync<int>(
                """
SELECT count(*)::int
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND level='medium';
""",
                new { tenant = tenantId, docId });
            Assert.Equal(0, storedSummaryCount);
        }
    }

    [Fact]
    public async Task SubmitSummaryAsync_rejects_expired_capability_b_execution_lease()
    {
        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
            return;

        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        const string leaseToken = "expired-lease-token";

        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            var oldLeaseAt = DateTimeOffset.UtcNow.AddHours(-2);
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["docId"] = docId,
                ["docPath"] = "Generic/expired-lease-summary.pdf",
                ["level"] = "medium",
                ["executionMode"] = "server_backoffice",
                ["runtimeCapabilityKey"] = "capability_b.backoffice_generation",
                ["runtimeCapabilityStatus"] = "selected",
                ["runtimeCapabilitySelected"] = true,
                ["source"] = "capability_b",
                ["executionLeaseToken"] = leaseToken,
                ["executionClaimedAt"] = oldLeaseAt,
                ["executionHeartbeatAt"] = oldLeaseAt,
                ["executionClaimedBy"] = "capability_b_worker"
            });

            await conn.ExecuteAsync(
                """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'Generic/expired-lease-summary.pdf', 'expired-lease-summary.pdf', 'generic', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('c3', 32), 'hex'), 2048, now(), false
);

INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at, started_at)
VALUES(
  @jobId,
  @tenant,
  'summary.generate',
  'running',
  @docId,
  'Generic/expired-lease-summary.pdf',
  'medium',
  @payload::jsonb,
  now() - interval '2 hours',
  now() - interval '2 hours'
);
""",
                new
                {
                    tenant = tenantId,
                    docId,
                    jobId,
                    payload
                });
        }

        await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
        string sourceHash;
        await using (var conn = await ds.OpenConnectionAsync())
        {
            sourceHash = (await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
                conn,
                tenantId,
                docId,
                CancellationToken.None))!;
            Assert.False(string.IsNullOrWhiteSpace(sourceHash));
        }

        var submitCtx = BuildAdminContext(backofficeEnabled: true);
        var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
            submitCtx,
            ds,
            new SummaryEndpoints.SummaryCommand(
                DocId: docId,
                Level: "medium",
                JobId: jobId,
                SourceHash: sourceHash,
                SummaryText: "This should not complete with an expired lease.",
                ExecutionLeaseToken: leaseToken));
        var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);

        Assert.Equal("capability_b_execution_lease_expired", submitPayload.GetProperty("error").GetString());

        await using (var conn = await ds.OpenConnectionAsync())
        {
            var state = await conn.QuerySingleAsync<(string Status, int SummaryCount)>(
                """
SELECT
  j.status AS "Status",
  (
    SELECT count(*)::int
    FROM document_summaries s
    WHERE s.tenant_id=j.tenant_id
      AND s.doc_id=j.doc_id
      AND s.level='medium'
  ) AS "SummaryCount"
FROM admin_jobs j
WHERE j.tenant_id=@tenant
  AND j.job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId });
            Assert.Equal("running", state.Status);
            Assert.Equal(0, state.SummaryCount);
        }
    }

    [Fact]
    public async Task SubmitSummaryAsync_rejects_unclaimed_capability_b_job_and_leaves_it_queued()
    {
        var previousBackoffice = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");

        await using var db = await PostgresIntegrationDb.CreateAsync();
        if (db is null)
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
            return;
        }

        try
        {
            var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var docId = Guid.NewGuid();
            const string summaryText = "Capability B completed summary";

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category, status,
  updated_at, created_at, ingestion_version, indexed_version,
  content_hash, file_size, file_mtime, auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-completed-summary.pdf', 'b-completed-summary.pdf', 'atex', 'indexed',
  now(), now(), 1, 1,
  decode(repeat('bf', 32), 'hex'), 2048, now(), false
);
""",
                    new { tenant = tenantId, docId });
            }

            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            await RuntimeGovernanceCommandService.RequalifyAsync(
                ds,
                new RuntimeGovernanceHttpClientFactory(),
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeRequalifyRequestDto(
                    CapabilityKey: "capability_b.backoffice_generation",
                    SelectWhenQualified: true),
                CancellationToken.None);

            var enqueue = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                new RuntimeGovernanceOptions(),
                CreateRagOptions(),
                new StubHostEnvironment(),
                new AdminRuntimeCapabilityBEnqueueRequestDto(DocIds: [docId]),
                CancellationToken.None);

            Assert.Null(enqueue.Error);
            Assert.NotNull(enqueue.Payload);
            var enqueuePayload = enqueue.Payload!;
            var jobId = Assert.Single(enqueuePayload.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            string sourceHash;
            await using (var conn = await ds.OpenConnectionAsync())
            {
                sourceHash = (await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(
                    conn,
                    tenantId,
                    docId,
                    CancellationToken.None))!;
                Assert.False(string.IsNullOrWhiteSpace(sourceHash));
            }

            var submitCtx = BuildAdminContext(backofficeEnabled: true);
            var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
                submitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    JobId: jobId,
                    DocLanguage: "fr",
                    SourceHash: sourceHash,
                    SummaryText: summaryText));
            var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);

            Assert.Equal("capability_b_job_not_running", submitPayload.GetProperty("error").GetString());

            await using (var conn = await ds.OpenConnectionAsync())
            {
                var storedSummaryCount = await conn.QuerySingleAsync<int>(
                    """
SELECT count(*)::int
FROM document_summaries
WHERE tenant_id=@tenant
  AND doc_id=@docId
  AND level='medium';
""",
                    new { tenant = tenantId, docId });
                Assert.Equal(0, storedSummaryCount);

                var jobState = await conn.QuerySingleAsync<(string Status, string? Result)>(
                    """
SELECT status AS "Status", result::text AS "Result"
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id=@jobId
LIMIT 1;
""",
                    new { tenant = tenantId, jobId });
                Assert.Equal("queued", jobState.Status);
                Assert.Null(jobState.Result);
            }

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            Assert.DoesNotContain(eventsPayload.Items, item => item.EventType == "capability_b_summary_completed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previousBackoffice);
        }
    }

    private static RagOptions CreateRagOptions()
        => new()
        {
            QdrantBaseUrl = "http://qdrant.test/",
            QdrantCollection = "knowledge_base",
            EmbeddingsBaseUrl = "http://tei.test/",
            EmbeddingsModel = "intfloat/multilingual-e5-base"
        };

    private static async Task<IResult> InvokeGenerateSummaryAsync(
        DefaultHttpContext ctx,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions runtimeOptions,
        RagOptions ragOptions,
        IHostEnvironment env,
        SummaryEndpoints.SummaryCommand cmd)
    {
        var method = typeof(SummaryEndpoints).GetMethod(
            "GenerateSummaryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<IResult>)method!.Invoke(null,
        [
            ctx,
            ds,
            Options.Create(runtimeOptions),
            Options.Create(ragOptions),
            env,
            cmd
        ])!;

        return await task;
    }

    private static async Task<IResult> InvokePauseAdminJobAsync(
        DefaultHttpContext ctx,
        NpgsqlDataSource ds,
        Guid jobId)
    {
        var method = typeof(SummaryEndpoints).GetMethod(
            "PauseAdminJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<IResult>)method!.Invoke(null,
        [
            ctx,
            ds,
            new IngestionJobCancellationRegistry(),
            new SummaryEndpoints.JobPauseCommand(jobId)
        ])!;

        return await task;
    }

    private static async Task<IResult> InvokeResumeAdminJobAsync(
        DefaultHttpContext ctx,
        NpgsqlDataSource ds,
        Guid jobId)
    {
        var method = typeof(SummaryEndpoints).GetMethod(
            "ResumeAdminJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<IResult>)method!.Invoke(null,
        [
            ctx,
            ds,
            Options.Create(new IngestionOptions()),
            new SummaryEndpoints.JobResumeCommand(jobId)
        ])!;

        return await task;
    }

    private static DefaultHttpContext BuildAdminContext(bool backofficeEnabled, IHttpClientFactory? httpClientFactory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<JsonOptions>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BACKOFFICE_LLM_ENABLED"] = backofficeEnabled ? "true" : "false"
            })
            .Build());
        services.AddSingleton<IHttpClientFactory>(httpClientFactory ?? new RuntimeGovernanceHttpClientFactory());

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = true;
        ctx.Items[ApiKeyAuth.TenantIdItemKey] = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return ctx;
    }

    private static NpgsqlDataSource CreateUnusedDataSource()
        => NpgsqlDataSource.Create("Host=localhost;Port=1;Username=unused;Password=unused;Database=unused;Timeout=1;Command Timeout=1");

    private static async Task<T> ExecuteResultAsync<T>(IResult result, DefaultHttpContext ctx)
    {
        ctx.Response.Body.SetLength(0);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<T>(ctx.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static string[] GetStringArray(JsonElement element)
        => element.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();

    private sealed class RuntimeGovernanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RuntimeGovernanceHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class WorkerProfileEnrichmentHttpClientFactory : IHttpClientFactory
    {
        private int _chatCompletionCount;

        public HttpClient CreateClient(string name)
            => new(new WorkerProfileEnrichmentHttpMessageHandler(this))
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };

        private int NextChatCompletionCount()
            => Interlocked.Increment(ref _chatCompletionCount);

        private sealed class WorkerProfileEnrichmentHttpMessageHandler(WorkerProfileEnrichmentHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;

                if (request.Method == HttpMethod.Get && path.StartsWith("/collections/", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(JsonResponse("""{ "result": { "status": "green", "points_count": 0 } }"""));

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

                if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    var call = owner.NextChatCompletionCount();
                    return Task.FromResult(call == 1
                        ? ChatCompletionResponse("Operational summary for b-worker-llm-summary.pdf: Scope and Purpose frames the operational perimeter. Operators should apply the required safety controls before deployment.")
                        : ChatCompletionResponse("""
                        {
                          "language": "en",
                          "summary": "LLM enriched profile: b-worker-llm-summary.pdf covers classified safety controls and operational perimeter checks.",
                          "keywords": ["classified safety controls", "operational perimeter"],
                          "entities": ["classified areas"],
                          "topics": ["Safety controls"],
                          "questions": ["Which document covers classified safety controls?"],
                          "limits": ["Use page chunks for exact operational requirements."]
                        }
                        """));
                }

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage ChatCompletionResponse(string content)
                => JsonResponse(JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content
                            }
                        }
                    }
                }));

            private static HttpResponseMessage JsonResponse(string json)
                => new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
        }
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

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "Operational summary for b-worker-llm-summary.pdf: Scope and Purpose frames the operational perimeter. Operators should apply the required safety controls before deployment."
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
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }

    private sealed class BrokenRuntimeGovernanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new BrokenRuntimeGovernanceHttpMessageHandler())
            {
                BaseAddress = name switch
                {
                    "qdrant" => new Uri("http://qdrant.test/"),
                    "tei" => new Uri("http://tei.test/"),
                    "llm" => new Uri("http://llm.test/"),
                    _ => new Uri("http://stub.test/")
                }
            };
    }

    private sealed class BrokenRuntimeGovernanceHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get && path.StartsWith("/collections/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(BuildJsonResponse("""{ "result": { "status": "green", "points_count": 0 } }"""));
            }

            if (path.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(BuildJsonResponse("""
                {
                  "data": [
                    { "embedding": [0.1, 0.2, 0.3, 0.4] }
                  ]
                }
                """));
            }

            if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{ "error": "llm_runtime_unavailable" }""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage BuildJsonResponse(string json)
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

    private static async Task SeedCurrentLlmProfileAsync(NpgsqlConnection conn, Guid tenantId, Guid docId)
    {
        var revisionId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
INSERT INTO document_revisions(
  revision_id, tenant_id, doc_id, doc_path, source_hash, source_size,
  source_mtime, ingestion_version, indexed_version, published_at
)
SELECT @revisionId, tenant_id, doc_id, doc_path,
       COALESCE(content_hash, decode(repeat('a1', 32), 'hex')), file_size,
       file_mtime, ingestion_version, indexed_version, now()
FROM documents WHERE tenant_id=@tenant AND doc_id=@docId;

INSERT INTO document_profiles(
  document_profile_id, tenant_id, revision_id, doc_id, profile_version,
  language, summary_text, search_text, token_count, checksum, metadata
)
VALUES (
  @profileId, @tenant, @revisionId, @docId, 'llm_backoffice_v1',
  'en', 'Current profile for summary freshness isolation.',
  'Current profile for summary freshness isolation.', 7,
  decode(repeat('a2', 32), 'hex'),
  jsonb_build_object('contentCardEvidenceSchemaVersion', @schemaVersion)
);
""",
            new
            {
                tenant = tenantId,
                docId,
                revisionId,
                profileId = DocumentFoundationRepo.BuildStableDocumentProfileId(revisionId, "llm_backoffice_v1"),
                schemaVersion = DocumentFoundationRepo.ContentCardEvidenceSchemaVersion
            });
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
            var databaseName = $"saaia_sum_{Guid.NewGuid():N}";

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
            // Match the default tenant used by BuildAdminContext and these scenario fixtures.
            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "INSERT INTO tenants(tenant_id, name) VALUES(@tenant, 'Test tenant');",
                    new { tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") });
            }
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
