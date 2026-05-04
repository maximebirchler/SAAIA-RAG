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

        CapabilityBQueuedJobLocator? locator;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            locator = await LoadNextQueuedCapabilityBJobLocatorAsync(conn, ct);
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

        try
        {
            CapabilityBGeneratedSummaryPayload generatedSummary;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            {
                generatedSummary = await BuildGeneratedSummaryAsync(conn, locator.TenantId, execution, summaryService, profileEnrichmentService, _logger, ct);
            }

            var completion = await RuntimeCapabilityBExecutionCommandService.CompleteCapabilityBBackofficeExecutionAsync(
                locator.TenantId,
                ds,
                new AdminRuntimeCapabilityBCompleteRequestDto(
                    JobId: execution.JobId,
                    LeaseToken: execution.LeaseToken,
                    SummaryText: generatedSummary.SummaryText,
                    DocLanguage: generatedSummary.DocLanguage,
                    SourceHash: null,
                    Meta: generatedSummary.Meta),
                ct);

            if (completion.Error is not null)
            {
                _logger.LogWarning(
                    "Capability B worker could not complete job {JobId}: {Error}",
                    execution.JobId,
                    completion.Error);
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

    private static async Task<CapabilityBQueuedJobLocator?> LoadNextQueuedCapabilityBJobLocatorAsync(
        NpgsqlConnection conn,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<CapabilityBQueuedJobLocator>(new CommandDefinition(
            """
SELECT
  tenant_id AS "TenantId",
  job_id AS "JobId"
FROM admin_jobs
WHERE job_type='summary.generate'
  AND status='queued'
  AND payload ->> 'source' = 'capability_b'
ORDER BY created_at ASC
LIMIT 1;
""",
            cancellationToken: ct));

    private static async Task<CapabilityBGeneratedSummaryPayload> BuildGeneratedSummaryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        AdminRuntimeCapabilityBClaimResponseDto execution,
        CapabilityBBackofficeSummaryService? summaryService,
        DocumentProfileEnrichmentService? profileEnrichmentService,
        ILogger<CapabilityBBackofficeWorker> logger,
        CancellationToken ct)
    {
        var doc = await RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(conn, tenantId, execution.DocId, ct);
        if (doc is null)
            throw new InvalidOperationException("capability_b_document_not_found");

        var sectionTitles = doc.IndexedVersion > 0
            ? await RuntimeGovernanceService.LoadCapabilityBSectionTitlesAsync(conn, tenantId, doc.DocId, doc.IndexedVersion, limit: 5, ct)
            : Array.Empty<string>();
        var excerpts = doc.IndexedVersion > 0
            ? await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsAsync(conn, tenantId, doc.DocId, doc.IndexedVersion, limit: 3, ct)
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

        var summary = summaryService is not null
            ? await summaryService.BuildSummaryAsync(doc, sectionTitles, excerpts, ct, baseline?.Language)
            : CapabilityBBackofficeSummaryService.BuildDeterministicSummary(doc, sectionTitles, excerpts, "summary_service_unavailable", baseline?.Language);

        if (baseline is not null)
        {
            try
            {
                var enrichedProfile = profileEnrichmentService is null
                    ? null
                    : await profileEnrichmentService.BuildEnrichedProfileAsync(doc, baseline, sectionTitles, excerpts, ct);

                await DocumentFoundationRepo.UpsertDocumentProfileAsync(
                    conn,
                    tx: null,
                    tenantId,
                    doc.DocId,
                    baseline.RevisionId,
                    enrichedProfile ?? BuildBackofficeProfileFromSummary(doc, baseline, summary),
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Capability B worker could not enrich document profile for job {JobId} doc {DocPath}",
                    execution.JobId,
                    execution.DocPath);
            }
        }

        return summary with { DocLanguage = baseline?.Language ?? "und" };
    }

    private static ProjectedDocumentProfile BuildBackofficeProfileFromSummary(
        CapabilityBDocumentRow doc,
        DocumentProfileSnapshot baseline,
        CapabilityBGeneratedSummaryPayload summary)
        => DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: baseline.Language,
            summaryText: summary.SummaryText,
            keywords: baseline.Keywords,
            entities: baseline.Entities,
            topics: baseline.Topics,
            hypotheticalQuestions: baseline.HypotheticalQuestions,
            limits: baseline.Limits,
            docPath: doc.DocPath,
            docName: doc.DocName,
            contentCards: baseline.ContentCards ?? []);

    private sealed record CapabilityBQueuedJobLocator(Guid TenantId, Guid JobId);

    private sealed class StubWorkerHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "BackgroundWorker";
        public string ApplicationName { get; set; } = "SAAIA.Backend";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
