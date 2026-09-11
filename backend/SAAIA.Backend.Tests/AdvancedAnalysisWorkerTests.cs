using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisWorkerTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public void Result_validator_rejects_unknown_or_duplicate_citations(
        string mutation)
    {
        var evidence = BuildResolvedEvidence("evidence-1");
        var ids = mutation == "duplicate"
            ? new List<string> { "evidence-1", "evidence-1" }
            : new List<string> { "forged-evidence" };

        var validation = AdvancedAnalysisResultValidator.ValidateAndBuild(
            "fake-internal",
            new AdvancedAnalysisProviderResult
            {
                Outcome = "answered",
                AnswerText = "The inspection is required.",
                Claims =
                [
                    new AdvancedAnalysisResultClaim
                    {
                        ClaimId = "claim-1",
                        Text = "The inspection is required.",
                        EvidenceIds = ids
                    }
                ]
            },
            [evidence],
            10,
            DateTimeOffset.UtcNow);

        Assert.False(validation.IsValid);
        Assert.Equal(
            mutation == "duplicate" ? "citation_duplicate" : "citation_unknown",
            validation.ErrorCode);
    }

    [Fact]
    public void Result_validator_rejects_incoherent_provider_metrics()
    {
        var evidence = BuildResolvedEvidence("evidence-1");
        var validation = AdvancedAnalysisResultValidator.ValidateAndBuild(
            "fake-internal",
            new AdvancedAnalysisProviderResult
            {
                Outcome = "answered",
                AnswerText = "The inspection is required.",
                ModelId = "qualified-model",
                ProviderCallCount = 2,
                InputTokens = 10,
                CachedInputTokens = 11,
                Claims =
                [
                    new AdvancedAnalysisResultClaim
                    {
                        ClaimId = "claim-1",
                        Text = "The inspection is required.",
                        EvidenceIds = ["evidence-1"]
                    }
                ]
            },
            [evidence],
            10,
            DateTimeOffset.UtcNow);

        Assert.False(validation.IsValid);
        Assert.Equal("provider_metrics_invalid", validation.ErrorCode);
    }

    [Fact]
    public async Task Worker_revalidates_tenant_evidence_and_persists_cited_result()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("tenant-a", "Procedure alpha");
        var handoff = BuildHandoff(seed, "evidence-alpha");
        var jobId = await database.InsertJobAsync(seed, handoff);
        var provider = new DelegateProvider(async (request, cancellationToken) =>
        {
            var item = Assert.Single(request.Evidence);
            Assert.Equal(seed.TenantId, request.TenantId);
            Assert.Contains("Procedure alpha", item.Content, StringComparison.Ordinal);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return SuccessfulResult(item.Reference.EvidenceId);
        });
        var worker = CreateWorker(database.DataSource, provider);

        Assert.True(await worker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("succeeded", row.Status);
        Assert.Equal("fake-internal", row.ProviderKey);
        Assert.Null(row.LastErrorCode);
        using var result = JsonDocument.Parse(row.ResultJson!);
        Assert.Equal(
            AdvancedAnalysisResultEnvelope.CurrentSchemaVersion,
            result.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            "evidence-alpha",
            result.RootElement.GetProperty("claims")[0]
                .GetProperty("evidenceIds")[0].GetString());
        var storedEvidence = result.RootElement.GetProperty("evidence")[0];
        Assert.Equal(seed.DocId, storedEvidence.GetProperty("docId").GetGuid());
        Assert.Equal(seed.RevisionId, storedEvidence.GetProperty("revisionId").GetGuid());
        Assert.Equal(seed.ChunkId, storedEvidence.GetProperty("chunkId").GetGuid());
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Worker_rejects_cross_tenant_evidence_before_provider_call()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var owner = await database.SeedEvidenceAsync("tenant-owner", "Owner evidence");
        var foreign = await database.SeedEvidenceAsync("tenant-foreign", "Foreign evidence");
        var handoff = BuildHandoff(foreign, "foreign-evidence");
        var jobId = await database.InsertJobAsync(owner, handoff);
        var provider = new DelegateProvider((request, cancellationToken) =>
            Task.FromResult(SuccessfulResult("foreign-evidence")));
        var worker = CreateWorker(database.DataSource, provider);

        Assert.True(await worker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("failed", row.Status);
        Assert.Equal("evidence_revalidation_failed", row.LastErrorCode);
        Assert.Null(row.ResultJson);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Exclusive_claim_and_expired_lease_recovery_are_deterministic()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("lease-tenant", "Lease evidence");
        var jobId = await database.InsertJobAsync(
            seed,
            BuildHandoff(seed, "lease-evidence"));
        var firstStore = new AdvancedAnalysisJobStore(database.DataSource);
        var secondStore = new AdvancedAnalysisJobStore(database.DataSource);

        var claims = await Task.WhenAll(
            firstStore.TryClaimAsync("worker-a", "fake", 5, CancellationToken.None),
            secondStore.TryClaimAsync("worker-b", "fake", 5, CancellationToken.None));
        Assert.Single(claims, static claim => claim is not null);
        await database.ExpireLeaseAsync(jobId, attemptCount: 1);

        Assert.Equal(
            1,
            await firstStore.RecoverExpiredAsync(2, 0, CancellationToken.None));
        var retried = await database.ReadJobAsync(jobId);
        Assert.Equal("queued", retried.Status);
        Assert.Equal("lease_expired_retry", retried.LastErrorCode);

        Assert.NotNull(await firstStore.TryClaimAsync(
            "worker-a",
            "fake",
            5,
            CancellationToken.None));
        await database.ExpireLeaseAsync(jobId, attemptCount: 2);
        Assert.Equal(
            1,
            await firstStore.RecoverExpiredAsync(2, 0, CancellationToken.None));
        var exhausted = await database.ReadJobAsync(jobId);
        Assert.Equal("failed", exhausted.Status);
        Assert.Equal("lease_retry_exhausted", exhausted.LastErrorCode);
    }

    [Fact]
    public async Task Cancellation_during_execution_stops_provider_and_prevents_late_result()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("cancel-tenant", "Cancel evidence");
        var handoff = BuildHandoff(seed, "cancel-evidence");
        var jobId = await database.InsertJobAsync(seed, handoff);
        var provider = new BlockingProvider();
        var worker = CreateWorker(database.DataSource, provider, heartbeatMilliseconds: 25);

        var processing = worker.ProcessOnceAsync(CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = BuildContext(seed.TenantId);
        var cancelResult = await AdvancedAnalysisEndpoints.CancelAsync(
            context,
            database.DataSource,
            NullLogger<AdvancedAnalysisEndpoints.LogTag>.Instance,
            jobId,
            seed.UserId);
        await cancelResult.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        Assert.True(await processing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(provider.Canceled);
        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("canceled", row.Status);
        Assert.Null(row.ResultJson);
    }

    [Fact]
    public async Task Worker_rejects_provider_result_with_forged_citation()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("citation-tenant", "Citation evidence");
        var jobId = await database.InsertJobAsync(
            seed,
            BuildHandoff(seed, "citation-evidence"));
        var provider = new DelegateProvider((request, cancellationToken) =>
            Task.FromResult(SuccessfulResult("invented-evidence")));
        var worker = CreateWorker(database.DataSource, provider);

        Assert.True(await worker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("failed", row.Status);
        Assert.Equal("citation_unknown", row.LastErrorCode);
        Assert.Null(row.ResultJson);
    }

    [Theory]
    [InlineData("persisted_handoff", "handoff_revalidation_failed")]
    [InlineData("external_provider", "external_provider_not_authorized")]
    public async Task Worker_refuses_unsafe_persisted_or_external_execution(
        string mutation,
        string expectedError)
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync("policy-tenant", "Policy evidence");
        var jobId = await database.InsertJobAsync(
            seed,
            BuildHandoff(seed, "policy-evidence"));
        if (mutation == "persisted_handoff")
            await database.MarkPersistedMemoryAsEvidenceAsync(jobId);
        var provider = new DelegateProvider(
            (request, cancellationToken) =>
                Task.FromResult(SuccessfulResult("policy-evidence")),
            mutation == "external_provider"
                ? AdvancedAnalysisProviderLocation.ExternalService
                : AdvancedAnalysisProviderLocation.Internal);
        var worker = CreateWorker(database.DataSource, provider);

        Assert.True(await worker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("failed", row.Status);
        Assert.Equal(expectedError, row.LastErrorCode);
        Assert.Null(row.ResultJson);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Tool_gateway_matches_shared_canonical_retrieval_and_deduplicates()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var seed = await database.SeedEvidenceAsync(
            "shared-retrieval",
            "Procedure alpha requires a documented inspection.");
        var advancedSettings = BuildAdvancedOptions();
        advancedSettings.MaximumToolCalls = 2;
        var advancedOptions = Options.Create(advancedSettings);
        var ragOptions = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var resolver = new AdvancedAnalysisEvidenceResolver(
            database.DataSource,
            advancedOptions);
        var bulkhead = new RagSearchBulkhead(
            ragOptions,
            NullLogger<RagSearchBulkhead>.Instance);
        var gateway = new AdvancedAnalysisToolGateway(
            Guid.NewGuid(),
            seed.TenantId,
            [],
            database.DataSource,
            ragOptions.Value,
            httpFactory,
            services,
            bulkhead,
            resolver,
            advancedOptions.Value);
        var request = new AdvancedAnalysisSearchRequest(
            "Procedure alpha documented inspection",
            TopK: 10);

        var first = await gateway.SearchAsync(request, CancellationToken.None);
        var second = await gateway.SearchAsync(request, CancellationToken.None);
        var directContext = BuildContext(seed.TenantId);
        directContext.RequestServices = services;
        directContext.Items[RequestIdMiddleware.RequestIdItemKey] = "direct-canonical";
        var direct = await RagEndpoints.SearchCoreAsync(
            directContext,
            database.DataSource,
            ragOptions.Value,
            httpFactory,
            new RagSearchRequestDto(
                Query: request.Query,
                TopK: request.TopK,
                MinScore: 0.0,
                Mode: "broad",
                ResearchMode: "advanced_analysis",
                IncludeResearchSurfaces: false,
                SourceBackedCanonical: true));

        Assert.NotEmpty(first.Evidence);
        Assert.Equal(
            direct.Matches.Select(static match => match.ChunkId),
            first.Evidence.Select(static item => item.Reference.ChunkId));
        Assert.Equal(
            first.Evidence.Select(static item => item.Reference.EvidenceId),
            second.Evidence.Select(static item => item.Reference.EvidenceId));
        Assert.Equal(first.Evidence.Count, gateway.Evidence.Count);
        Assert.Contains("dense_qdrant", first.DegradedRetrievers);
        var invalidTopK = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => gateway.SearchAsync(
                new AdvancedAnalysisSearchRequest("alpha", TopK: 21),
                CancellationToken.None));
        Assert.Equal("search_top_k_invalid", invalidTopK.ErrorCode);
        var exhausted = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(
            () => gateway.SearchAsync(request, CancellationToken.None));
        Assert.Equal("tool_call_limit_exceeded", exhausted.ErrorCode);
    }

    [Fact]
    public async Task Provider_driven_shared_retrieval_publishes_twenty_cited_claims()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var groups = new[] { "alphaunique", "betaunique", "gammaunique", "deltaunique" };
        var owner = await database.SeedEvidenceAsync(
            "grid-00",
            "alphaunique documented item 01 with complete source content.");
        var ordinal = 2;
        foreach (var group in groups)
        {
            var firstIndex = group == groups[0] ? 2 : 1;
            for (var index = firstIndex; index <= 5; index++)
            {
                await database.SeedAdditionalEvidenceAsync(
                    owner,
                    $"grid-{ordinal++:00}",
                    $"{group} documented item {index:00} with complete source content.");
            }
        }
        var jobId = await database.InsertJobAsync(owner, BuildEmptyHandoff());
        var advancedOptions = Options.Create(BuildAdvancedOptions());
        var ragOptions = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var resolver = new AdvancedAnalysisEvidenceResolver(
            database.DataSource,
            advancedOptions);
        var toolFactory = new AdvancedAnalysisToolGatewayFactory(
            database.DataSource,
            ragOptions,
            httpFactory,
            services,
            new RagSearchBulkhead(
                ragOptions,
                NullLogger<RagSearchBulkhead>.Instance),
            resolver,
            new AdvancedAnalysisJobStore(database.DataSource),
            advancedOptions);
        var provider = new MultiSearchProvider(groups);
        var worker = new AdvancedAnalysisWorker(
            new AdvancedAnalysisJobStore(database.DataSource),
            resolver,
            toolFactory,
            provider,
            advancedOptions,
            NullLogger<AdvancedAnalysisWorker>.Instance);

        Assert.True(await worker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("succeeded", row.Status);
        using var result = JsonDocument.Parse(row.ResultJson!);
        Assert.Equal(20, result.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.Equal(20, result.RootElement.GetProperty("claims").GetArrayLength());
        Assert.Equal(4, provider.ToolCalls);
        var evidenceIds = result.RootElement.GetProperty("evidence")
            .EnumerateArray()
            .Select(static item => item.GetProperty("evidenceId").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            result.RootElement.GetProperty("claims").EnumerateArray(),
            claim => Assert.Contains(
                claim.GetProperty("evidenceIds")[0].GetString(),
                evidenceIds));
        var toolEvents = await database.ReadToolEventsAsync(jobId);
        Assert.Equal(4, toolEvents.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, toolEvents.Select(static item => item.EventSequence));
        Assert.All(toolEvents, static item => Assert.Equal("succeeded", item.Status));
        Assert.DoesNotContain(
            "complete source content",
            string.Join("", toolEvents.Select(static item => item.EvidenceJson)),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, row.ToolEventCount);
        Assert.Equal(7, row.Revision);
    }

    [Fact]
    public async Task Durable_tool_trace_is_revalidated_after_expired_lease_retry()
    {
        await using var database = await PostgresWorkerDatabase.CreateAsync();
        if (database is null)
            return;

        var owner = await database.SeedEvidenceAsync(
            "resume-tenant",
            "resumableunique PRIVATE_CORPUS_SENTINEL proves the current procedure.");
        var jobId = await database.InsertJobAsync(owner, BuildEmptyHandoff());
        var advancedOptions = Options.Create(BuildAdvancedOptions());
        var ragOptions = Options.Create(BuildRagOptions());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var httpFactory = new UnavailableHttpClientFactory();
        var store = new AdvancedAnalysisJobStore(database.DataSource);
        var resolver = new AdvancedAnalysisEvidenceResolver(
            database.DataSource,
            advancedOptions);
        var toolFactory = new AdvancedAnalysisToolGatewayFactory(
            database.DataSource,
            ragOptions,
            httpFactory,
            services,
            new RagSearchBulkhead(
                ragOptions,
                NullLogger<RagSearchBulkhead>.Instance),
            resolver,
            store,
            advancedOptions);
        var firstLease = await store.TryClaimAsync(
            "crashed-worker",
            "fake-crash",
            5,
            CancellationToken.None);
        Assert.NotNull(firstLease);
        var firstGateway = toolFactory.Create(
            jobId,
            owner.TenantId,
            "crashed-worker",
            firstLease!.AttemptCount,
            []);
        var observation = await firstGateway.SearchAsync(
            new AdvancedAnalysisSearchRequest("resumableunique", TopK: 5),
            CancellationToken.None);
        var discovered = Assert.Single(observation.Evidence);
        await database.ExpireLeaseAsync(jobId, attemptCount: 1);

        var provider = new ResumeAwareProvider(discovered.Reference.EvidenceId);
        var retryWorker = new AdvancedAnalysisWorker(
            store,
            resolver,
            toolFactory,
            provider,
            advancedOptions,
            NullLogger<AdvancedAnalysisWorker>.Instance);
        Assert.True(await retryWorker.ProcessOnceAsync(CancellationToken.None));

        var row = await database.ReadJobAsync(jobId);
        Assert.Equal("succeeded", row.Status);
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(1, row.ToolEventCount);
        Assert.True(provider.SawPreviousTrace);
        Assert.True(provider.SawRevalidatedContent);
        var history = await store.LoadToolHistoryAsync(
            owner.TenantId,
            jobId,
            CancellationToken.None);
        var historyEvent = Assert.Single(history);
        Assert.Equal(1, historyEvent.EventSequence);
        Assert.Equal("succeeded", historyEvent.Status);
        var rawEvents = await database.ReadToolEventsAsync(jobId);
        Assert.DoesNotContain(
            "PRIVATE_CORPUS_SENTINEL",
            Assert.Single(rawEvents).EvidenceJson,
            StringComparison.Ordinal);
        Assert.Empty(await store.LoadToolHistoryAsync(
            Guid.NewGuid(),
            jobId,
            CancellationToken.None));
        Assert.Null(await store.TryAppendToolEventAsync(
            owner.TenantId,
            jobId,
            "crashed-worker",
            1,
            8,
            "succeeded",
            new AdvancedAnalysisSearchRequest("resumableunique", TopK: 5),
            [discovered.Reference],
            [],
            1,
            null,
            CancellationToken.None));
    }

    [Fact]
    public void Durable_tool_trace_migration_is_bounded_and_tenant_scoped()
    {
        var migrationsDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "SAAIA.Backend", "Db", "Migrations"));
        var sql = File.ReadAllText(Path.Combine(
            migrationsDirectory,
            "066_advanced_analysis_tool_events.sql"));

        Assert.Contains(
            "FOREIGN KEY (tenant_id, job_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "jsonb_array_length(evidence_references) <= 256",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("evidence_text", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static AdvancedAnalysisWorker CreateWorker(
        NpgsqlDataSource dataSource,
        IAdvancedAnalysisProvider provider,
        int heartbeatMilliseconds = 50)
    {
        var options = Options.Create(new AdvancedAnalysisOptions
        {
            WorkerEnabled = true,
            LeaseSeconds = 5,
            HeartbeatMilliseconds = heartbeatMilliseconds,
            RetryDelayMilliseconds = 0,
            MaximumAttempts = 2,
            MaximumEvidenceCharactersPerItem = 10_000,
            MaximumEvidenceCharactersTotal = 20_000
        });
        return new AdvancedAnalysisWorker(
            new AdvancedAnalysisJobStore(dataSource),
            new AdvancedAnalysisEvidenceResolver(dataSource, options),
            new InitialEvidenceToolGatewayFactory(),
            provider,
            options,
            NullLogger<AdvancedAnalysisWorker>.Instance);
    }

    private static AdvancedAnalysisOptions BuildAdvancedOptions()
        => new()
        {
            WorkerEnabled = true,
            LeaseSeconds = 10,
            HeartbeatMilliseconds = 50,
            RetryDelayMilliseconds = 0,
            MaximumAttempts = 2,
            MaximumEvidenceCharactersPerItem = 10_000,
            MaximumEvidenceCharactersTotal = 400_000,
            MaximumToolCalls = 8,
            MaximumSearchTopK = 20,
            MaximumAccumulatedEvidenceItems = 40,
            MaximumToolElapsedMilliseconds = 30_000
        };

    private static RagOptions BuildRagOptions()
        => new()
        {
            EmbeddingsBaseUrl = "http://127.0.0.1:9/",
            QdrantBaseUrl = "http://127.0.0.1:9/",
            EnableRerank = false,
            DefaultTopK = 10,
            MaxTopK = 20,
            SearchMaxConcurrency = 2,
            SearchQueueLimit = 2,
            SearchQueueWaitTimeoutSeconds = 1,
            SearchDenseEmbeddingTimeoutSeconds = 1,
            SearchSparseCommandTimeoutSeconds = 5
        };

    private static AdvancedAnalysisHandoffEnvelope BuildHandoff(
        EvidenceSeed seed,
        string evidenceId)
        => new()
        {
            HandoffId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestText = "Compare the documented procedures.",
            Language = "en",
            OriginIntent = "rag.answer",
            ReasonCode = "documentary_comparison_outside_local_envelope",
            TransferStage = "after_retrieval",
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = "comparison",
                AnswerUnitCount = 2,
                AtomicEvidenceCount = 2
            },
            ResearchState = new AdvancedAnalysisResearchState
            {
                MemoryIsEvidence = false,
                EvidenceRevalidationRequired = true,
                EvidenceReferences =
                [
                    new AdvancedAnalysisEvidenceReference
                    {
                        EvidenceId = evidenceId,
                        DocId = seed.DocId.ToString("D"),
                        RevisionId = seed.RevisionId.ToString("D"),
                        FileName = seed.FileName,
                        DocPath = seed.DocPath,
                        PageStart = 2,
                        PageEnd = 2,
                        ChunkId = seed.ChunkId.ToString("D")
                    }
                ]
            },
            DataPolicy = new AdvancedAnalysisDataPolicy
            {
                ExternalProviderContentAuthorized = false,
                ExternalProviderMetadataAuthorized = false,
                AuthorizationSource = "server_policy_required"
            }
        };

    private static AdvancedAnalysisHandoffEnvelope BuildEmptyHandoff()
        => new()
        {
            HandoffId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestText = "Build a documented 5 by 4 comparison grid.",
            Language = "en",
            OriginIntent = "rag.answer",
            ReasonCode = "structured_grid_outside_local_envelope",
            TransferStage = "before_retrieval",
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = "structured_grid",
                AnswerUnitCount = 20,
                AtomicEvidenceCount = 20,
                RowCount = 5,
                ColumnCount = 4,
                StructuredLayout = true
            },
            ResearchState = new AdvancedAnalysisResearchState
            {
                MemoryIsEvidence = false,
                EvidenceRevalidationRequired = true
            },
            DataPolicy = new AdvancedAnalysisDataPolicy
            {
                ExternalProviderContentAuthorized = false,
                ExternalProviderMetadataAuthorized = false,
                AuthorizationSource = "server_policy_required"
            }
        };

    private static AdvancedAnalysisProviderResult SuccessfulResult(string evidenceId)
        => new()
        {
            Outcome = "answered",
            AnswerText = "The inspection is required.",
            Claims =
            [
                new AdvancedAnalysisResultClaim
                {
                    ClaimId = "claim-1",
                    Text = "The inspection is required.",
                    EvidenceIds = [evidenceId]
                }
            ]
        };

    private static AdvancedAnalysisResolvedEvidence BuildResolvedEvidence(
        string evidenceId)
        => new(
            new AdvancedAnalysisResultEvidence
            {
                EvidenceId = evidenceId,
                DocId = Guid.NewGuid().ToString("D"),
                RevisionId = Guid.NewGuid().ToString("D"),
                FileName = "procedure.pdf",
                DocPath = "procedures/procedure.pdf",
                SourceHash = new string('a', 64),
                PageStart = 2,
                PageEnd = 2,
                ChunkId = Guid.NewGuid().ToString("D")
            },
            "Inspection is required.");

    private static DefaultHttpContext BuildContext(Guid tenantId)
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
        return context;
    }

    private sealed class DelegateProvider : IAdvancedAnalysisProvider
    {
        private readonly Func<AdvancedAnalysisProviderRequest, CancellationToken,
            Task<AdvancedAnalysisProviderResult>> _handler;
        private readonly AdvancedAnalysisProviderLocation _location;

        public DelegateProvider(
            Func<AdvancedAnalysisProviderRequest, CancellationToken,
                Task<AdvancedAnalysisProviderResult>> handler,
            AdvancedAnalysisProviderLocation location =
                AdvancedAnalysisProviderLocation.Internal)
        {
            _handler = handler;
            _location = location;
        }

        public string ProviderKey => "fake-internal";
        public AdvancedAnalysisProviderLocation Location => _location;
        public List<AdvancedAnalysisProviderRequest> Requests { get; } = new();

        public Task<AdvancedAnalysisProviderResult> ExecuteAsync(
            AdvancedAnalysisProviderRequest request,
            IAdvancedAnalysisToolGateway tools,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _handler(request, cancellationToken);
        }
    }

    private sealed class BlockingProvider : IAdvancedAnalysisProvider
    {
        public string ProviderKey => "fake-blocking";
        public AdvancedAnalysisProviderLocation Location =>
            AdvancedAnalysisProviderLocation.Internal;
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }

        public async Task<AdvancedAnalysisProviderResult> ExecuteAsync(
            AdvancedAnalysisProviderRequest request,
            IAdvancedAnalysisToolGateway tools,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled = true;
                throw;
            }
            return SuccessfulResult(request.Evidence[0].Reference.EvidenceId);
        }
    }

    private sealed class MultiSearchProvider : IAdvancedAnalysisProvider
    {
        private readonly IReadOnlyList<string> _queries;

        public MultiSearchProvider(IReadOnlyList<string> queries)
        {
            _queries = queries;
        }

        public string ProviderKey => "fake-multi-search";
        public AdvancedAnalysisProviderLocation Location =>
            AdvancedAnalysisProviderLocation.Internal;
        public int ToolCalls { get; private set; }

        public async Task<AdvancedAnalysisProviderResult> ExecuteAsync(
            AdvancedAnalysisProviderRequest request,
            IAdvancedAnalysisToolGateway tools,
            CancellationToken cancellationToken)
        {
            foreach (var query in _queries)
            {
                await tools.SearchAsync(
                    new AdvancedAnalysisSearchRequest(
                        query,
                        TopK: 5,
                        MaxPerDocument: 1,
                        MaxPerPage: 1),
                    cancellationToken);
                ToolCalls++;
            }

            var evidence = tools.Evidence;
            return new AdvancedAnalysisProviderResult
            {
                Outcome = "answered",
                AnswerText = string.Join(
                    "\n",
                    evidence.Select((item, index) =>
                        $"{index + 1}. Documented item [{item.Reference.EvidenceId}]")),
                Claims = evidence.Select((item, index) =>
                    new AdvancedAnalysisResultClaim
                    {
                        ClaimId = $"claim-{index + 1:00}",
                        Text = $"Documented item {index + 1:00}",
                        EvidenceIds = [item.Reference.EvidenceId]
                    }).ToList()
            };
        }
    }

    private sealed class ResumeAwareProvider : IAdvancedAnalysisProvider
    {
        private readonly string _expectedEvidenceId;

        public ResumeAwareProvider(string expectedEvidenceId)
        {
            _expectedEvidenceId = expectedEvidenceId;
        }

        public string ProviderKey => "fake-resume-aware";
        public AdvancedAnalysisProviderLocation Location =>
            AdvancedAnalysisProviderLocation.Internal;
        public bool SawPreviousTrace { get; private set; }
        public bool SawRevalidatedContent { get; private set; }

        public Task<AdvancedAnalysisProviderResult> ExecuteAsync(
            AdvancedAnalysisProviderRequest request,
            IAdvancedAnalysisToolGateway tools,
            CancellationToken cancellationToken)
        {
            var previous = Assert.Single(request.PreviousToolEvents);
            SawPreviousTrace = previous.Status == "succeeded"
                               && previous.Evidence.Count == 1;
            var evidence = Assert.Single(request.Evidence);
            SawRevalidatedContent = evidence.Content.Contains(
                "PRIVATE_CORPUS_SENTINEL",
                StringComparison.Ordinal);
            Assert.Equal(_expectedEvidenceId, evidence.Reference.EvidenceId);
            return Task.FromResult(SuccessfulResult(_expectedEvidenceId));
        }
    }

    private sealed class UnavailableHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly ConcurrentBag<HttpClient> _clients = new();

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new UnavailableHandler());
            _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            while (_clients.TryTake(out var client))
                client.Dispose();
        }

        private sealed class UnavailableHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(
                    System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class InitialEvidenceToolGatewayFactory :
        IAdvancedAnalysisToolGatewayFactory
    {
        public IAdvancedAnalysisToolGateway Create(
            Guid jobId,
            Guid tenantId,
            string workerId,
            int attemptCount,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> initialEvidence)
            => new InitialEvidenceToolGateway(initialEvidence);
    }

    private sealed class InitialEvidenceToolGateway : IAdvancedAnalysisToolGateway
    {
        public InitialEvidenceToolGateway(
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
        {
            Evidence = evidence;
        }

        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence { get; }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "A758 provider tests must not perform retrieval.");
    }

    private sealed class PostgresWorkerDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        public NpgsqlDataSource DataSource { get; }

        private PostgresWorkerDatabase(
            string adminConnectionString,
            string databaseName,
            NpgsqlDataSource dataSource)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            DataSource = dataSource;
        }

        public static async Task<PostgresWorkerDatabase?> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(
                "SAAIA_TEST_PG_CONN");
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                return null;

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
            var databaseName = $"saaia_advanced_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE \"{databaseName}\";");
            }

            var databaseBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName
            };
            var dataSource = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
            var migrationsDirectory = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "SAAIA.Backend", "Db", "Migrations"));
            await DbMigrator.ApplyMigrationsAsync(
                databaseBuilder.ConnectionString,
                migrationsDirectory,
                CancellationToken.None);
            return new PostgresWorkerDatabase(
                adminBuilder.ConnectionString,
                databaseName,
                dataSource);
        }

        public async Task<EvidenceSeed> SeedEvidenceAsync(
            string tenantName,
            string content)
        {
            var tenantId = Guid.NewGuid();
            var userId = $"user-{Guid.NewGuid():N}";
            var sessionId = Guid.NewGuid();
            var docId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var chunkId = Guid.NewGuid();
            var fileName = $"{tenantName}.pdf";
            var docPath = $"procedures/{fileName}";
            var sourceHash = Enumerable.Range(0, 32)
                .Select(index => (byte)(index + 1)).ToArray();
            await using var connection = await DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO tenants(tenant_id, name, is_active)
                VALUES(@tenant, @tenant_name, true);
                INSERT INTO chat_sessions(
                  tenant_id, user_id, session_id, title, created_at, updated_at)
                VALUES(@tenant, @user_id, @session, 'Advanced worker', now(), now());
                INSERT INTO documents(
                  tenant_id, doc_id, doc_path, doc_name, category, content_hash,
                  status, ingestion_version, indexed_version)
                VALUES(
                  @tenant, @doc_id, @doc_path, @file_name, 'Procedures', @source_hash,
                  'indexed', 1, 1);
                INSERT INTO document_revisions(
                  revision_id, tenant_id, doc_id, doc_path, source_hash,
                  source_size, ingestion_version, indexed_version)
                VALUES(
                  @revision, @tenant, @doc_id, @doc_path, @source_hash,
                  100, 1, 1);
                INSERT INTO retrieval_chunks(
                  retrieval_chunk_id, tenant_id, revision_id, chunk_index,
                  page_start, page_end, text_content, token_count)
                VALUES(@chunk, @tenant, @revision, 0, 2, 2, @content, 4);
                INSERT INTO contextual_text_entries(
                  contextual_text_entry_id, tenant_id, revision_id,
                  retrieval_chunk_id, entry_index, page_start, page_end,
                  text_content, char_count, token_count, checksum)
                VALUES(
                  @contextual_entry, @tenant, @revision, @chunk, 0, 2, 2,
                  @content, length(@content), 4, @content_checksum);
                """,
                new
                {
                    tenant = tenantId,
                    tenant_name = tenantName,
                    user_id = userId,
                    session = sessionId,
                    doc_id = docId,
                    revision = revisionId,
                    chunk = chunkId,
                    contextual_entry = Guid.NewGuid(),
                    doc_path = docPath,
                    file_name = fileName,
                    source_hash = sourceHash,
                    content_checksum = SHA256.HashData(Encoding.UTF8.GetBytes(content)),
                    content
                });
            return new EvidenceSeed(
                tenantId,
                userId,
                sessionId,
                docId,
                revisionId,
                chunkId,
                fileName,
                docPath);
        }

        public async Task<Guid> InsertJobAsync(
            EvidenceSeed owner,
            AdvancedAnalysisHandoffEnvelope handoff)
        {
            var jobId = Guid.NewGuid();
            await using var connection = await DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO advanced_analysis_jobs(
                  job_id, tenant_id, user_id, session_id, handoff_id,
                  schema_version, status, revision, attempt_count, handoff,
                  available_at, created_at, updated_at, expires_at)
                VALUES(
                  @job, @tenant, @user_id, @session, @handoff_id,
                  @schema, 'queued', 1, 0, CAST(@handoff AS jsonb),
                  now(), now(), now(), now() + interval '1 day');
                """,
                new
                {
                    job = jobId,
                    tenant = owner.TenantId,
                    user_id = owner.UserId,
                    session = owner.SessionId,
                    handoff_id = handoff.HandoffId,
                    schema = handoff.SchemaVersion,
                    handoff = JsonSerializer.Serialize(
                        handoff,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });
            return jobId;
        }

        public async Task<EvidenceSeed> SeedAdditionalEvidenceAsync(
            EvidenceSeed owner,
            string documentName,
            string content)
        {
            var docId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var chunkId = Guid.NewGuid();
            var fileName = $"{documentName}.pdf";
            var docPath = $"procedures/{fileName}";
            var sourceHash = SHA256.HashData(
                Encoding.UTF8.GetBytes($"{docId:D}|{content}"));
            await using var connection = await DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO documents(
                  tenant_id, doc_id, doc_path, doc_name, category, content_hash,
                  status, ingestion_version, indexed_version)
                VALUES(
                  @tenant, @doc_id, @doc_path, @file_name, 'Procedures', @source_hash,
                  'indexed', 1, 1);
                INSERT INTO document_revisions(
                  revision_id, tenant_id, doc_id, doc_path, source_hash,
                  source_size, ingestion_version, indexed_version)
                VALUES(
                  @revision, @tenant, @doc_id, @doc_path, @source_hash,
                  100, 1, 1);
                INSERT INTO retrieval_chunks(
                  retrieval_chunk_id, tenant_id, revision_id, chunk_index,
                  page_start, page_end, text_content, token_count)
                VALUES(@chunk, @tenant, @revision, 0, 2, 2, @content, 8);
                INSERT INTO contextual_text_entries(
                  contextual_text_entry_id, tenant_id, revision_id,
                  retrieval_chunk_id, entry_index, page_start, page_end,
                  text_content, char_count, token_count, checksum)
                VALUES(
                  @contextual_entry, @tenant, @revision, @chunk, 0, 2, 2,
                  @content, length(@content), 8, @content_checksum);
                """,
                new
                {
                    tenant = owner.TenantId,
                    doc_id = docId,
                    revision = revisionId,
                    chunk = chunkId,
                    contextual_entry = Guid.NewGuid(),
                    doc_path = docPath,
                    file_name = fileName,
                    source_hash = sourceHash,
                    content_checksum = SHA256.HashData(Encoding.UTF8.GetBytes(content)),
                    content
                });
            return owner with
            {
                DocId = docId,
                RevisionId = revisionId,
                ChunkId = chunkId,
                FileName = fileName,
                DocPath = docPath
            };
        }

        public async Task ExpireLeaseAsync(Guid jobId, int attemptCount)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                UPDATE advanced_analysis_jobs
                SET lease_expires_at=now() - interval '1 second',
                    attempt_count=@attempt_count
                WHERE job_id=@job;
                """,
                new { job = jobId, attempt_count = attemptCount });
        }

        public async Task MarkPersistedMemoryAsEvidenceAsync(Guid jobId)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                UPDATE advanced_analysis_jobs
                SET handoff=jsonb_set(
                    handoff,
                    '{researchState,memoryIsEvidence}',
                    'true'::jsonb)
                WHERE job_id=@job;
                """,
                new { job = jobId });
        }

        public async Task<JobState> ReadJobAsync(Guid jobId)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            return await connection.QuerySingleAsync<JobState>(
                """
                SELECT status AS "Status",
                  provider_key AS "ProviderKey",
                  result::text AS "ResultJson",
                  last_error_code AS "LastErrorCode",
                  attempt_count AS "AttemptCount",
                  revision AS "Revision",
                  tool_event_count AS "ToolEventCount"
                FROM advanced_analysis_jobs
                WHERE job_id=@job;
                """,
                new { job = jobId });
        }

        public async Task<List<ToolEventState>> ReadToolEventsAsync(Guid jobId)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<ToolEventState>(
                """
                SELECT event_sequence AS "EventSequence",
                  status AS "Status",
                  request::text AS "RequestJson",
                  evidence_references::text AS "EvidenceJson"
                FROM advanced_analysis_tool_events
                WHERE job_id=@job
                ORDER BY event_sequence;
                """,
                new { job = jobId })).AsList();
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            try
            {
                await using var admin = new NpgsqlConnection(_adminConnectionString);
                await admin.OpenAsync();
                await admin.ExecuteAsync(
                    """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname=@database_name
                      AND pid <> pg_backend_pid();
                    """,
                    new { database_name = _databaseName });
                await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\";");
            }
            catch
            {
                // Best-effort cleanup for the isolated optional integration DB.
            }
        }
    }

    private sealed class JobState
    {
        public string Status { get; init; } = string.Empty;
        public string? ProviderKey { get; init; }
        public string? ResultJson { get; init; }
        public string? LastErrorCode { get; init; }
        public int AttemptCount { get; init; }
        public int Revision { get; init; }
        public int ToolEventCount { get; init; }
    }

    private sealed class ToolEventState
    {
        public int EventSequence { get; init; }
        public string Status { get; init; } = string.Empty;
        public string RequestJson { get; init; } = string.Empty;
        public string EvidenceJson { get; init; } = string.Empty;
    }

    private sealed record EvidenceSeed(
        Guid TenantId,
        string UserId,
        Guid SessionId,
        Guid DocId,
        Guid RevisionId,
        Guid ChunkId,
        string FileName,
        string DocPath);
}
