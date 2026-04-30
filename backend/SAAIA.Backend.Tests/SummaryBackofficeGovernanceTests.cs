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
    public async Task Generate_and_submit_summary_preserve_client_admin_runtime_unavailable_metadata_when_b_runtime_falls_back_live()
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

            Assert.Equal("queued", generatePayload.GetProperty("status").GetString());
            Assert.Equal("client_admin", generatePayload.GetProperty("executionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", generatePayload.GetProperty("runtimeCapabilityKey").GetString());
            Assert.Equal("runtime_unavailable", generatePayload.GetProperty("runtimeCapabilityStatus").GetString());
            var jobId = generatePayload.GetProperty("jobId").GetGuid();

            var submitCtx = BuildAdminContext(backofficeEnabled: true);
            var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
                submitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    JobId: jobId,
                    SummaryText: "Fallback summary stored via client admin after runtime probe failure."));
            var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);

            Assert.True(submitPayload.GetProperty("stored").GetBoolean());
            var sourceHash = submitPayload.GetProperty("sourceHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sourceHash));

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);

            Assert.Equal("done", detailPayload.GetProperty("Status").GetString());
            Assert.Equal("client_admin", detailPayload.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", detailPayload.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("runtime_unavailable", detailPayload.GetProperty("RuntimeCapabilityStatus").GetString());
            Assert.False(detailPayload.GetProperty("RuntimeCapabilitySelected").GetBoolean());
            Assert.Equal(sourceHash, detailPayload.GetProperty("ResultSourceHash").GetString());
            Assert.Equal("admin_submit_summary", detailPayload.GetProperty("ResultCompletedBy").GetString());

            var statusCtx = BuildAdminContext(backofficeEnabled: true);
            var statusResult = await SummaryEndpoints.SummaryStatusAsync(statusCtx, ds, jobId);
            var statusPayload = await ExecuteResultAsync<JsonElement>(statusResult, statusCtx);

            Assert.Equal("done", statusPayload.GetProperty("Status").GetString());
            Assert.Equal("client_admin", statusPayload.GetProperty("ExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", statusPayload.GetProperty("RuntimeCapabilityKey").GetString());
            Assert.Equal("runtime_unavailable", statusPayload.GetProperty("RuntimeCapabilityStatus").GetString());
            Assert.True(statusPayload.GetProperty("StoredSummaryExists").GetBoolean());
            Assert.Equal("fresh", statusPayload.GetProperty("StoredSummaryFreshness").GetString());

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                var summaryMeta = await conn.ExecuteScalarAsync<string?>(
                    """
SELECT summary_meta::text
FROM document_summaries
WHERE tenant_id=@tenant AND doc_id=@docId AND level='medium'
LIMIT 1;
""",
                    new { tenant = tenantId, docId });

                Assert.True(string.IsNullOrWhiteSpace(summaryMeta) || string.Equals(summaryMeta, "null", StringComparison.OrdinalIgnoreCase));
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
  md5(@docPath || '||'),
  'Too short.',
  '{"qualityScore":0.20,"strategy":"llm","runtimeCapabilityStatus":"selected"}'::jsonb,
  now(),
  now()
);
""",
                    new { tenant = tenantId, docId, newerDistractorDocId, docPath });
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
                item => item.GetProperty("DocId").GetGuid() == docId);

            Assert.Equal("missing", missingItem.GetProperty("SummaryState").GetString());
            Assert.True(missingItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.Equal("summary.generate", missingItem.GetProperty("ActiveSummaryJobType").GetString());
            Assert.Equal("queued", missingItem.GetProperty("ActiveSummaryJobStatus").GetString());
            Assert.Equal("server_backoffice", missingItem.GetProperty("ActiveSummaryJobExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", missingItem.GetProperty("ActiveSummaryJobRuntimeCapabilityKey").GetString());
            Assert.Equal("selected", missingItem.GetProperty("ActiveSummaryJobRuntimeCapabilityStatus").GetString());
            Assert.Equal("capability_b", missingItem.GetProperty("ActiveSummaryJobEnqueueSource").GetString());
            Assert.Equal(enqueuePayload.CampaignId, missingItem.GetProperty("ActiveSummaryJobCampaignId").GetGuid());
            Assert.False(missingItem.GetProperty("CapabilityBReadyToEnqueue").GetBoolean());
            Assert.Equal("review_active_summary_job", missingItem.GetProperty("CapabilityBRecommendedAction").GetString());
            Assert.True(missingItem.GetProperty("CapabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("active_summary_job_exists", missingItem.GetProperty("CapabilityBPolicyBlockReason").GetString());
            Assert.Equal("queued", missingItem.GetProperty("CapabilityBLastJobStatus").GetString());
            Assert.Contains("summary_missing", GetStringArray(missingItem.GetProperty("CapabilityBReasons")));
            Assert.Contains("summary_job_active", GetStringArray(missingItem.GetProperty("CapabilityBReasons")));

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
                item => item.GetProperty("DocId").GetGuid() == docId);

            Assert.Equal("missing", catalogItem.GetProperty("SummaryState").GetString());
            Assert.True(catalogItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.Equal("server_backoffice", catalogItem.GetProperty("ActiveSummaryJobExecutionMode").GetString());
            Assert.Equal("capability_b.backoffice_generation", catalogItem.GetProperty("ActiveSummaryJobRuntimeCapabilityKey").GetString());
            Assert.Equal(enqueuePayload.CampaignId, catalogItem.GetProperty("ActiveSummaryJobCampaignId").GetGuid());
            Assert.False(catalogItem.GetProperty("CapabilityBReadyToEnqueue").GetBoolean());
            Assert.True(catalogItem.GetProperty("CapabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("active_summary_job_exists", catalogItem.GetProperty("CapabilityBPolicyBlockReason").GetString());
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
                item => item.GetProperty("DocId").GetGuid() == docId);

            Assert.Equal("missing", missingItem.GetProperty("SummaryState").GetString());
            Assert.False(missingItem.GetProperty("hasActiveSummaryJob").GetBoolean());
            Assert.False(missingItem.GetProperty("CapabilityBReadyToEnqueue").GetBoolean());
            Assert.True(missingItem.GetProperty("CapabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("recent_summary_job_failure", missingItem.GetProperty("CapabilityBPolicyBlockReason").GetString());
            Assert.Equal("inspect_recent_summary_failure", missingItem.GetProperty("CapabilityBRecommendedAction").GetString());
            Assert.Equal("failed", missingItem.GetProperty("CapabilityBLastJobStatus").GetString());
            Assert.Equal("model timeout", missingItem.GetProperty("CapabilityBLastJobError").GetString());
            Assert.Contains("summary_missing", GetStringArray(missingItem.GetProperty("CapabilityBReasons")));
            Assert.Contains("recent_summary_failure", GetStringArray(missingItem.GetProperty("CapabilityBReasons")));

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
                item => item.GetProperty("DocId").GetGuid() == docId);

            Assert.False(catalogItem.GetProperty("CapabilityBReadyToEnqueue").GetBoolean());
            Assert.True(catalogItem.GetProperty("CapabilityBPolicyBlocked").GetBoolean());
            Assert.Equal("recent_summary_job_failure", catalogItem.GetProperty("CapabilityBPolicyBlockReason").GetString());
            Assert.Equal("inspect_recent_summary_failure", catalogItem.GetProperty("CapabilityBRecommendedAction").GetString());
            Assert.Equal("failed", catalogItem.GetProperty("CapabilityBLastJobStatus").GetString());
            Assert.Equal("model timeout", catalogItem.GetProperty("CapabilityBLastJobError").GetString());
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
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-execution-contract.pdf', 'b-execution-contract.pdf', 'atex', 'indexed',
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
                    DocLanguage: "fr"));
            var completePayload = await ExecuteResultAsync<JsonElement>(completeResult, completeCtx);
            Assert.True(completePayload.GetProperty("stored").GetBoolean());

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
    public async Task SubmitSummaryAsync_marks_capability_b_job_done_and_emits_completion_result_and_runtime_event()
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
  auto_ingest_paused
)
VALUES(
  @tenant, @docId, 'ATEX/b-completed-summary.pdf', 'b-completed-summary.pdf', 'atex', 'indexed',
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
            var jobId = Assert.Single(enqueuePayload.Items, item => item.Queued).JobId;
            Assert.NotNull(jobId);

            var submitCtx = BuildAdminContext(backofficeEnabled: true);
            var submitResult = await SummaryEndpoints.SubmitSummaryAsync(
                submitCtx,
                ds,
                new SummaryEndpoints.SummaryCommand(
                    DocId: docId,
                    Level: "medium",
                    JobId: jobId,
                    DocLanguage: "fr",
                    SummaryText: summaryText));
            var submitPayload = await ExecuteResultAsync<JsonElement>(submitResult, submitCtx);

            Assert.True(submitPayload.GetProperty("stored").GetBoolean());
            var sourceHash = submitPayload.GetProperty("sourceHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sourceHash));

            var detailCtx = BuildAdminContext(backofficeEnabled: true);
            var detailResult = await SummaryEndpoints.GetAdminJobAsync(detailCtx, ds, jobId!.Value);
            var detailPayload = await ExecuteResultAsync<JsonElement>(detailResult, detailCtx);

            Assert.Equal("done", detailPayload.GetProperty("Status").GetString());
            Assert.True(detailPayload.GetProperty("ResultStored").GetBoolean());
            Assert.Equal(sourceHash, detailPayload.GetProperty("ResultSourceHash").GetString());
            Assert.Equal(summaryText.Length, detailPayload.GetProperty("ResultSummaryLength").GetInt32());
            Assert.Equal("admin_submit_summary", detailPayload.GetProperty("ResultCompletedBy").GetString());

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
                item => item.GetProperty("JobId").GetGuid() == jobId.Value);

            Assert.True(listedJob.GetProperty("ResultStored").GetBoolean());
            Assert.Equal(sourceHash, listedJob.GetProperty("ResultSourceHash").GetString());
            Assert.Equal(summaryText.Length, listedJob.GetProperty("ResultSummaryLength").GetInt32());
            Assert.Equal("admin_submit_summary", listedJob.GetProperty("ResultCompletedBy").GetString());

            var statusCtx = BuildAdminContext(backofficeEnabled: true);
            var statusResult = await SummaryEndpoints.SummaryStatusAsync(statusCtx, ds, jobId.Value);
            var statusPayload = await ExecuteResultAsync<JsonElement>(statusResult, statusCtx);

            Assert.Equal("done", statusPayload.GetProperty("Status").GetString());
            Assert.True(statusPayload.GetProperty("ResultStored").GetBoolean());
            Assert.Equal(sourceHash, statusPayload.GetProperty("ResultSourceHash").GetString());
            Assert.Equal(summaryText.Length, statusPayload.GetProperty("ResultSummaryLength").GetInt32());
            Assert.True(statusPayload.GetProperty("StoredSummaryExists").GetBoolean());
            Assert.Equal("fresh", statusPayload.GetProperty("StoredSummaryFreshness").GetString());
            Assert.Equal(sourceHash, statusPayload.GetProperty("StoredSummarySourceHash").GetString());

            var eventsCtx = BuildAdminContext(backofficeEnabled: true);
            var eventsResult = await AdminRuntimeEndpoints.EventsAsync(
                eventsCtx,
                ds,
                new StubHostEnvironment(),
                capabilityKey: "capability_b.backoffice_generation",
                limit: 20);
            var eventsPayload = await ExecuteResultAsync<AdminRuntimeEventsResponseDto>(eventsResult, eventsCtx);
            var completionEvent = Assert.Single(eventsPayload.Items, item => item.EventType == "capability_b_summary_completed");
            Assert.NotNull(completionEvent.Details);
            Assert.Equal(jobId.Value, Guid.Parse(completionEvent.Details!["jobId"]!.ToString()!));
            Assert.Equal(docId, Guid.Parse(completionEvent.Details["docId"]!.ToString()!));

            var campaignsCtx = BuildAdminContext(backofficeEnabled: true);
            var campaignsResult = await AdminRuntimeEndpoints.CapabilityBCampaignsAsync(
                campaignsCtx,
                ds,
                new StubHostEnvironment(),
                limit: 10);
            var campaignsPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBCampaignsResponseDto>(campaignsResult, campaignsCtx);
            var campaign = Assert.Single(campaignsPayload.Items, item => item.CampaignId == enqueuePayload.CampaignId);
            Assert.Equal(1, campaign.JobStatusCounts["done"]);
            Assert.Equal(1, campaign.TrackedJobCount);
            Assert.Equal(0, campaign.ActiveJobCount);
            Assert.Equal(1, campaign.TerminalJobCount);
            Assert.Equal(100, campaign.ProgressPercent);

            var campaignDetailCtx = BuildAdminContext(backofficeEnabled: true);
            var campaignDetailResult = await AdminRuntimeEndpoints.CapabilityBCampaignAsync(
                campaignDetailCtx,
                ds,
                new StubHostEnvironment(),
                enqueuePayload.CampaignId);
            var campaignDetailPayload = await ExecuteResultAsync<AdminRuntimeCapabilityBCampaignDetailResponseDto>(campaignDetailResult, campaignDetailCtx);
            Assert.Equal(1, campaignDetailPayload.Item.JobStatusCounts["done"]);
            Assert.Equal(1, campaignDetailPayload.Item.TrackedJobCount);
            Assert.Equal(0, campaignDetailPayload.Item.ActiveJobCount);
            Assert.Equal(1, campaignDetailPayload.Item.TerminalJobCount);
            Assert.Equal(1, campaignDetailPayload.Item.StoredSummaryCount);
            Assert.Equal(100, campaignDetailPayload.Item.ProgressPercent);
            var campaignItem = Assert.Single(campaignDetailPayload.Item.Items, item => item.JobId == jobId);
            Assert.Equal("done", campaignItem.JobStatus);
            Assert.True(campaignItem.JobResultStored);
            Assert.NotNull(campaignItem.JobFinishedAt);
            Assert.Equal("fresh", campaignItem.StoredSummaryFreshness);

            var diagnosticsCtx = BuildAdminContext(backofficeEnabled: true);
            var diagnosticsResult = await AdminRuntimeEndpoints.DiagnosticsAsync(
                diagnosticsCtx,
                ds,
                Options.Create(new RuntimeGovernanceOptions()),
                Options.Create(CreateRagOptions()),
                new StubHostEnvironment());
            var diagnosticsPayload = await ExecuteResultAsync<AdminRuntimeDiagnosticsResponseDto>(diagnosticsResult, diagnosticsCtx);
            var capabilityB = Assert.Single(diagnosticsPayload.Items, item => item.Key == "capability_b.backoffice_generation");
            Assert.NotNull(capabilityB.OperationalSummary);
            Assert.Equal(0, capabilityB.OperationalSummary!.CandidateCount);
            Assert.Equal(0, capabilityB.OperationalSummary.ReadyToEnqueueCount);
            Assert.Equal(0, capabilityB.OperationalSummary.BlockedByActiveJobCount);
            Assert.Equal(0, capabilityB.OperationalSummary.ActiveCapabilityJobCount);
            Assert.Equal(1, capabilityB.OperationalSummary.TotalCampaignCount);
            Assert.Equal(0, capabilityB.OperationalSummary.ActiveCampaignCount);
            Assert.Equal(1, capabilityB.OperationalSummary.TerminalCapabilityJobCount);
            Assert.Equal(1, capabilityB.OperationalSummary.StoredSummaryCount);
            Assert.Equal(100, capabilityB.OperationalSummary.LatestCampaignProgressPercent);
            Assert.Equal(enqueuePayload.CampaignId, capabilityB.OperationalSummary.LatestCampaignId);
            Assert.Equal("executed", capabilityB.OperationalSummary.LatestCampaignStatus);
            Assert.Contains(capabilityB.Recommendations, item => item.Contains("backlog is currently clear", StringComparison.OrdinalIgnoreCase));
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
