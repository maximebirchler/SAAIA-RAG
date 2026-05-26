using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class CapabilityBBackofficeWorker : BackgroundService
{
    private const string ExecutorId = "capability_b_worker";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CapabilityBBackofficeWorker> _logger;
    private readonly DateTimeOffset _workerStartedAt = DateTimeOffset.UtcNow;

    public CapabilityBBackofficeWorker(
        IServiceProvider serviceProvider,
        ILogger<CapabilityBBackofficeWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var startupScope = _serviceProvider.CreateScope();
        var startupOptions = startupScope.ServiceProvider.GetRequiredService<IOptions<RuntimeGovernanceOptions>>().Value;
        if (!startupOptions.CapabilityBWorkerEnabled)
        {
            _logger.LogInformation("Capability B backoffice worker is disabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextJobOnceAsync(stoppingToken);
                if (!processed)
                    await Task.Delay(Math.Max(100, startupOptions.CapabilityBWorkerEmptyDelayMs), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capability B worker loop failed");
                await Task.Delay(Math.Max(100, startupOptions.CapabilityBWorkerErrorDelayMs), stoppingToken);
            }
        }
    }

    internal async Task<bool> ProcessNextJobOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var services = scope.ServiceProvider;
        var ds = services.GetRequiredService<NpgsqlDataSource>();
        var options = services.GetRequiredService<IOptions<RuntimeGovernanceOptions>>().Value;
        var rag = services.GetRequiredService<IOptions<RagOptions>>().Value;
        var env = services.GetRequiredService<IHostEnvironment>();
        var summaryService = services.GetService<CapabilityBBackofficeSummaryService>();
        var profileEnrichmentService = services.GetService<DocumentProfileEnrichmentService>();

        if (!options.CapabilityBWorkerEnabled)
            return false;

        if (options.CapabilityBRequireRagIdleForExecution)
        {
            var ragIdleSnapshot = RuntimeCapabilityBRagIdleCoordinator.LoadSnapshot(options, DateTimeOffset.UtcNow);
            if (!ragIdleSnapshot.IsIdle)
            {
                _logger.LogDebug(
                    "Capability B worker waits for RAG idle reason={Reason} active_retrievals={ActiveRetrievals} idle_for_seconds={IdleForSeconds} required_idle_seconds={RequiredIdleSeconds}",
                    ragIdleSnapshot.Reason,
                    ragIdleSnapshot.ActiveRetrievals,
                    ragIdleSnapshot.IdleFor?.TotalSeconds,
                    ragIdleSnapshot.RequiredIdleDelay.TotalSeconds);
                return false;
            }
        }

        if (options.CapabilityBRequireIngestionIdleForExecution)
        {
            var idleSnapshot = await RuntimeCapabilityBIngestionIdleCoordinator.LoadGlobalIngestionIdleSnapshotAsync(
                ds,
                options,
                DateTimeOffset.UtcNow,
                ct);
            if (!idleSnapshot.IsIdle)
            {
                _logger.LogDebug(
                    "Capability B worker detected non-idle ingestion globally and will look for idle tenants reason={Reason} active_ingestion_jobs={ActiveIngestionJobs} idle_for_seconds={IdleForSeconds} required_idle_seconds={RequiredIdleSeconds}",
                    idleSnapshot.Reason,
                    idleSnapshot.ActiveIngestionJobs,
                    idleSnapshot.IdleFor?.TotalSeconds,
                    idleSnapshot.RequiredIdleDelay.TotalSeconds);
            }
        }

        if (options.CapabilityBRunningJobLeaseTimeoutSeconds > 0)
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var staleBefore = MaxUtc(
                DateTimeOffset.UtcNow.AddSeconds(-options.CapabilityBRunningJobLeaseTimeoutSeconds),
                _workerStartedAt);
            var requeued = await RuntimeCapabilityBExecutionStore.RequeueStaleCapabilityBWorkerJobsAsync(
                conn,
                staleBefore,
                ExecutorId,
                ct);
            if (requeued > 0)
            {
                _logger.LogWarning(
                    "Capability B worker requeued {RequeuedCount} interrupted or stale running summary jobs claimed before {StaleBefore}",
                    requeued,
                    staleBefore);
            }
        }

        CapabilityBQueuedJobLocator? locator;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            locator = await LoadNextQueuedCapabilityBJobLocatorAsync(conn, options, DateTimeOffset.UtcNow, ct);
        }

        if (locator is null && options.CapabilityBAutoEnqueueWhenIngestionIdleEnabled)
        {
            var queued = await TryAutoEnqueueCapabilityBJobsAsync(ds, options, rag, env, ct);
            if (queued > 0)
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                locator = await LoadNextQueuedCapabilityBJobLocatorAsync(conn, options, DateTimeOffset.UtcNow, ct);
            }
        }

        if (locator is null)
            return false;

        var claim = await RuntimeCapabilityBExecutionCommandService.ClaimCapabilityBBackofficeExecutionAsync(
            locator.TenantId,
            ds,
            options,
            rag,
            env,
            new AdminRuntimeCapabilityBClaimRequestDto(JobId: locator.JobId, ExecutorId: ExecutorId),
            ct);

        if (claim.Error is not null)
        {
            _logger.LogDebug(
                "Capability B worker skipped job {JobId} for tenant {TenantId}: {Error}",
                locator.JobId,
                locator.TenantId,
                claim.Error);
            return false;
        }

        var execution = claim.Payload!;
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunCapabilityBHeartbeatLoopAsync(
            ds,
            locator.TenantId,
            execution.JobId,
            execution.LeaseToken,
            options,
            heartbeatCts.Token);

        try
        {
            CapabilityBGeneratedSummaryWork generated;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            {
                generated = await BuildGeneratedSummaryAsync(conn, locator.TenantId, execution, options, summaryService, profileEnrichmentService, _logger, ct);
            }

            var completion = await RuntimeCapabilityBExecutionCommandService.CompleteCapabilityBBackofficeExecutionWithGeneratedProfileAsync(
                locator.TenantId,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: execution.JobId,
                    LeaseToken: execution.LeaseToken,
                    SummaryText: generated.Summary.SummaryText,
                    DocLanguage: generated.Summary.DocLanguage,
                    SourceHash: generated.SourceHash,
                    Meta: generated.Summary.Meta),
                generated.ProfileRevisionId,
                generated.Profile,
                ct);

            if (completion.Error is not null)
            {
                _logger.LogWarning(
                    "Capability B worker could not complete job {JobId}: {Error}",
                    execution.JobId,
                    completion.Error);
                if (IsStaleSourceCompletionError(completion.Error))
                    await TryCancelStaleSourceJobAsync(locator.TenantId, ds, execution, completion.Error, ct);
                else
                    await TryFailClaimedJobAsync(locator.TenantId, ds, execution, completion.Error, ct);
                return true;
            }

            _logger.LogInformation(
                "Capability B worker stored summary for job {JobId} doc {DocPath}",
                execution.JobId,
                execution.DocPath);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capability B worker failed job {JobId} doc {DocPath}", execution.JobId, execution.DocPath);
            await TryFailClaimedJobAsync(locator.TenantId, ds, execution, ex.Message, ct);
            return true;
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
            {
            }
        }
    }

    private static DateTimeOffset MaxUtc(DateTimeOffset left, DateTimeOffset right)
        => left.UtcDateTime >= right.UtcDateTime ? left : right;

    private async Task RunCapabilityBHeartbeatLoopAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid jobId,
        string leaseToken,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var interval = ResolveCapabilityBHeartbeatInterval(options);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
                var updated = await RuntimeCapabilityBExecutionStore.TryHeartbeatJobAsync(
                    conn,
                    tenantId,
                    jobId,
                    leaseToken,
                    DateTimeOffset.UtcNow,
                    ct).ConfigureAwait(false);
                if (!updated && !ct.IsCancellationRequested)
                {
                    _logger.LogDebug(
                        "Capability B heartbeat did not update job {JobId}; job may already be terminal or lease changed",
                        jobId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Capability B heartbeat failed for job {JobId}", jobId);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan ResolveCapabilityBHeartbeatInterval(RuntimeGovernanceOptions options)
    {
        var leaseSeconds = options.CapabilityBRunningJobLeaseTimeoutSeconds;
        if (leaseSeconds <= 0)
            return TimeSpan.FromSeconds(30);

        return TimeSpan.FromSeconds(Math.Clamp(leaseSeconds / 4, 10, 60));
    }

    private async Task<int> TryAutoEnqueueCapabilityBJobsAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        var batchSize = Math.Clamp(options.CapabilityBAutoEnqueueBatchSize, 1, 500);
        var tenantLimit = Math.Clamp(options.CapabilityBAutoEnqueueTenantLimit, 1, 64);

        IReadOnlyList<Guid> tenantIds;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            tenantIds = await RuntimeCapabilityBIngestionIdleCoordinator.LoadTenantsWithSummaryBacklogAsync(
                conn,
                tenantLimit,
                ct);
        }

        var queuedTotal = 0;
        foreach (var tenantId in tenantIds)
        {
            if (!await IsTenantIdleForAutoEnqueueAsync(ds, options, tenantId, ct).ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Capability B auto-enqueue skipped tenant {TenantId}: ingestion is not idle for this tenant",
                    tenantId);
                continue;
            }

            var remaining = Math.Max(1, batchSize - queuedTotal);
            var result = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
                tenantId,
                ds,
                options,
                rag,
                env,
                new AdminRuntimeCapabilityBEnqueueRequestDto(MaxCandidates: remaining),
                ct);

            if (result.Error is not null)
            {
                _logger.LogDebug(
                    "Capability B auto-enqueue skipped tenant {TenantId}: {Error}",
                    tenantId,
                    result.Error);
                continue;
            }

            var payload = result.Payload;
            var queued = payload?.QueuedCount ?? 0;
            queuedTotal += queued;
            if (queued > 0)
            {
                _logger.LogInformation(
                    "Capability B auto-enqueued {QueuedCount} summary jobs for tenant {TenantId}",
                    queued,
                    tenantId);
            }
            else if (payload is not null && (payload.CandidateCount > 0 || payload.PlannedCount > 0 || payload.SkippedCount > 0))
            {
                _logger.LogInformation(
                    "Capability B auto-enqueue evaluated tenant {TenantId}: candidates={CandidateCount} planned={PlannedCount} queued=0 skipped={SkippedCount} reasons={ReasonCounts}",
                    tenantId,
                    payload.CandidateCount,
                    payload.PlannedCount,
                    payload.SkippedCount,
                    JsonSerializer.Serialize(payload.ReasonCounts));
            }

            if (queuedTotal >= batchSize)
                break;
        }

        return queuedTotal;
    }

    private static async Task TryFailClaimedJobAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBClaimResponseDto execution,
        string error,
        CancellationToken ct)
    {
        await RuntimeCapabilityBExecutionCommandService.FailCapabilityBBackofficeExecutionAsync(
            tenantId,
            ds,
            new StubWorkerHostEnvironment(),
            new AdminRuntimeCapabilityBFailRequestDto(
                JobId: execution.JobId,
                LeaseToken: execution.LeaseToken,
                Error: string.IsNullOrWhiteSpace(error) ? "capability_b_worker_failed" : error),
            ct);
    }

    private static async Task TryCancelStaleSourceJobAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBClaimResponseDto execution,
        string error,
        CancellationToken ct)
    {
        var canceledAt = DateTimeOffset.UtcNow;
        var resultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = execution.DocId,
            ["docPath"] = execution.DocPath,
            ["level"] = execution.Level,
            ["stored"] = false,
            ["canceledBy"] = execution.ClaimedBy,
            ["reason"] = "stale_source",
            ["error"] = error,
            ["executionMode"] = execution.ExecutionMode,
            ["runtimeCapabilityKey"] = execution.RuntimeCapabilityKey,
            ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
            ["runtimeCapabilitySelected"] = true,
            ["runtimeProfileKey"] = execution.RuntimeProfileKey,
            ["source"] = "capability_b",
            ["campaignId"] = execution.CampaignId,
            ["executionLeaseToken"] = execution.LeaseToken
        });

        await using var conn = await ds.OpenConnectionAsync(ct);
        var canceled = await RuntimeCapabilityBExecutionStore.TryCancelJobAsync(
            conn,
            tenantId,
            execution.JobId,
            execution.LeaseToken,
            canceledAt,
            resultJson,
            error,
            ct);

        if (!canceled)
            return;

        await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
            conn,
            RuntimeGovernanceService.CreateCapabilityEvent(
                capabilityKey: "capability_b.backoffice_generation",
                profileKey: execution.RuntimeProfileKey,
                eventType: "capability_b_job_canceled",
                reason: "stale_source",
                details: new Dictionary<string, object?>
                {
                    ["jobId"] = execution.JobId,
                    ["docId"] = execution.DocId,
                    ["docPath"] = execution.DocPath,
                    ["level"] = execution.Level,
                    ["campaignId"] = execution.CampaignId,
                    ["canceledBy"] = execution.ClaimedBy,
                    ["canceledAt"] = canceledAt,
                    ["leaseToken"] = execution.LeaseToken,
                    ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
                    ["error"] = error
                }),
            ct);
    }

    private static bool IsStaleSourceCompletionError(string? error)
        => string.Equals(error, "source_hash_mismatch", StringComparison.Ordinal)
           || string.Equals(error, "source_hash_changed_during_completion", StringComparison.Ordinal);

    private static async Task<CapabilityBQueuedJobLocator?> LoadNextQueuedCapabilityBJobLocatorAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string orderBy = """
ORDER BY
  COALESCE(
    CASE
      WHEN jsonb_typeof(payload->'priorityScore')='number' THEN (payload->>'priorityScore')::int
      ELSE NULL::int
    END,
    0
  ) DESC,
  created_at ASC
LIMIT 1;
""";

        if (!options.CapabilityBRequireIngestionIdleForExecution)
        {
            return await conn.QueryFirstOrDefaultAsync<CapabilityBQueuedJobLocator>(new CommandDefinition(
                $"""
SELECT
  tenant_id AS "TenantId",
  job_id AS "JobId"
FROM admin_jobs
WHERE job_type='summary.generate'
  AND status='queued'
  AND payload ->> 'source' = 'capability_b'
{orderBy}
""",
                cancellationToken: ct));
        }

        var idleCutoff = now - RuntimeCapabilityBIngestionIdleCoordinator.ResolveRequiredIdleDelay(options);
        return await conn.QueryFirstOrDefaultAsync<CapabilityBQueuedJobLocator>(new CommandDefinition(
            $"""
SELECT
  j.tenant_id AS "TenantId",
  j.job_id AS "JobId"
FROM admin_jobs j
WHERE j.job_type='summary.generate'
  AND j.status='queued'
  AND j.payload ->> 'source' = 'capability_b'
  AND NOT EXISTS (
    SELECT 1
    FROM ingestion_jobs active
    WHERE active.tenant_id = j.tenant_id
      AND active.status IN ('queued','running')
  )
  AND (
    NOT EXISTS (
      SELECT 1
      FROM ingestion_jobs any_ingestion
      WHERE any_ingestion.tenant_id = j.tenant_id
    )
    OR (
      SELECT MAX(COALESCE(recent.locked_at, recent.started_at, recent.finished_at, recent.created_at))
      FROM ingestion_jobs recent
      WHERE recent.tenant_id = j.tenant_id
    ) <= @idleCutoff
  )
{orderBy}
""",
            new { idleCutoff },
            cancellationToken: ct));
    }

    private static async Task<bool> IsTenantIdleForAutoEnqueueAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!options.CapabilityBRequireIngestionIdleForExecution)
            return true;

        await using var conn = await ds.OpenConnectionAsync(ct);
        return await RuntimeCapabilityBIngestionIdleCoordinator.IsTenantIdleForCapabilityBAsync(
            conn,
            options,
            tenantId,
            DateTimeOffset.UtcNow,
            ct);
    }

    private static async Task<CapabilityBGeneratedSummaryWork> BuildGeneratedSummaryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        AdminRuntimeCapabilityBClaimResponseDto execution,
        RuntimeGovernanceOptions options,
        CapabilityBBackofficeSummaryService? summaryService,
        DocumentProfileEnrichmentService? profileEnrichmentService,
        ILogger<CapabilityBBackofficeWorker> logger,
        CancellationToken ct)
    {
        var doc = await RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(conn, tenantId, execution.DocId, ct);
        if (doc is null)
            throw new InvalidOperationException("capability_b_document_not_found");

        var sourceHash = doc.SourceHash;
        if (string.IsNullOrWhiteSpace(sourceHash))
            throw new InvalidOperationException("capability_b_source_hash_unavailable");

        var sectionTitleLimit = Math.Clamp(options.CapabilityBDocumentProfileSectionTitleLimit, 3, 20);
        var excerptLimit = Math.Clamp(options.CapabilityBDocumentProfileExcerptLimit, 3, 30);

        var sectionTitles = doc.IndexedVersion > 0
            ? await RuntimeGovernanceService.LoadCapabilityBSectionTitlesAsync(conn, tenantId, doc.DocId, doc.IndexedVersion, sectionTitleLimit, ct)
            : Array.Empty<string>();
        var excerpts = doc.IndexedVersion > 0
            ? await RuntimeGovernanceService.LoadCapabilityBRepresentativeUnitExcerptsAsync(conn, tenantId, doc.DocId, doc.IndexedVersion, excerptLimit, ct)
            : Array.Empty<string>();

        DocumentProfileSnapshot? baseline = null;
        if (doc.IndexedVersion > 0)
        {
            try
            {
                baseline = await DocumentFoundationRepo.LoadDocumentProfileAsync(
                    conn,
                    tenantId,
                    doc.DocId,
                    "deterministic_v1",
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Capability B worker could not load deterministic profile for job {JobId} doc {DocPath}",
                    execution.JobId,
                    execution.DocPath);
            }
        }

        var documentLanguage = ResolveDocumentLanguage(baseline?.Language, doc.ProfileLanguage);
        var summary = summaryService is not null
            ? await summaryService.BuildSummaryAsync(doc, sectionTitles, excerpts, ct, documentLanguage)
            : CapabilityBBackofficeSummaryService.BuildDeterministicSummary(doc, sectionTitles, excerpts, "summary_service_unavailable", documentLanguage);

        ProjectedDocumentProfile? projectedProfile = null;
        Guid? profileRevisionId = null;
        if (baseline is not null)
        {
            profileRevisionId = baseline.RevisionId;
            try
            {
                var enrichedProfile = profileEnrichmentService is null
                    ? null
                    : await profileEnrichmentService.BuildEnrichedProfileAsync(doc, baseline, sectionTitles, excerpts, ct);

                projectedProfile = enrichedProfile ?? BuildBackofficeProfileFromSummary(doc, baseline, summary, documentLanguage);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Capability B worker could not enrich document profile for job {JobId} doc {DocPath}",
                    execution.JobId,
                    execution.DocPath);
                projectedProfile = BuildBackofficeProfileFromSummary(doc, baseline, summary, documentLanguage);
            }
        }

        var storedDocLanguage = ResolveDocumentLanguage(documentLanguage, projectedProfile?.Language, summary.DocLanguage) ?? "und";
        return new CapabilityBGeneratedSummaryWork(
            summary with { DocLanguage = storedDocLanguage },
            sourceHash.Trim(),
            profileRevisionId,
            projectedProfile);
    }

    internal static ProjectedDocumentProfile BuildBackofficeProfileFromSummary(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        CapabilityBGeneratedSummaryPayload summary,
        string? documentLanguage)
        => DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: ResolveDocumentLanguage(documentLanguage, summary.DocLanguage, baseline.Language, doc.ProfileLanguage) ?? "und",
            summaryText: summary.SummaryText,
            keywords: baseline.Keywords,
            entities: baseline.Entities,
            topics: baseline.Topics,
            hypotheticalQuestions: baseline.HypotheticalQuestions,
            limits: CapabilityBExtractionQualityPrompting.BuildProfileLimits(
                doc.ExtractionQuality,
                ResolveDocumentLanguage(documentLanguage, summary.DocLanguage, baseline.Language, doc.ProfileLanguage))
                .Concat(baseline.Limits),
            docPath: doc.DocPath,
            docName: doc.DocName,
            contentCards: SelectSafeBaselineContentCardsForBackoffice(baseline.ContentCards ?? []));

    private static IReadOnlyList<DocumentProfileContentCard> SelectSafeBaselineContentCardsForBackoffice(
        IReadOnlyList<DocumentProfileContentCard> contentCards)
        => contentCards
            .Where(IsSafeBaselineContentCardForBackoffice)
            .ToArray();

    private static bool IsSafeBaselineContentCardForBackoffice(DocumentProfileContentCard card)
    {
        if (HasBackofficeContentCardEvidence(card.Evidence))
            return true;

        if (!string.IsNullOrWhiteSpace(card.Title)
            && ExactMatchEntryExtractor.ExtractTargetedReferences(card.Title).Any())
        {
            return true;
        }

        return IsDeterministicBackofficeContentCardKind(card.Kind)
               && (card.PageStart is > 0 || card.PageEnd is > 0);
    }

    private static bool IsDeterministicBackofficeContentCardKind(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind)
            ? string.Empty
            : kind.Trim().ToLowerInvariant();
        return normalized is "section" or "exact_lead" or "page_embedded_title" or "unit_lead" or "standard_ref" or "code_ref";
    }

    private static bool HasBackofficeContentCardEvidence(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return false;

        if ((evidence.Facts ?? []).Any(static fact =>
                !string.IsNullOrWhiteSpace(fact.SourceText)
                || fact.PageStart is > 0
                || fact.PageEnd is > 0))
        {
            return true;
        }

        return (evidence.QuantityFacts ?? []).Any(static fact => !string.IsNullOrWhiteSpace(fact.SourceText));
    }

    private static string? ResolveDocumentLanguage(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeDocumentLanguageTag(candidate);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(normalized, "und", StringComparison.Ordinal))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeDocumentLanguageTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (string.Equals(normalized, "und", StringComparison.Ordinal))
            return "und";
        if (normalized.Length is < 2 or > 35)
            return null;

        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return null;
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return null;

        return parts.Skip(1).All(static part =>
            part.Length is >= 2 and <= 8
            && part.All(static ch => char.IsLetterOrDigit(ch)))
            ? normalized
            : null;
    }

    private sealed record CapabilityBQueuedJobLocator(Guid TenantId, Guid JobId);

    private sealed record CapabilityBGeneratedSummaryWork(
        CapabilityBGeneratedSummaryPayload Summary,
        string SourceHash,
        Guid? ProfileRevisionId,
        ProjectedDocumentProfile? Profile);

    private sealed class StubWorkerHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "BackgroundWorker";
        public string ApplicationName { get; set; } = "SAAIA.Backend";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
