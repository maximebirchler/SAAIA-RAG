using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Endpoints;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed class AdvancedAnalysisWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly AdvancedAnalysisJobStore _store;
    private readonly AdvancedAnalysisEvidenceResolver _resolver;
    private readonly IAdvancedAnalysisToolGatewayFactory _toolGatewayFactory;
    private readonly IAdvancedAnalysisProvider _provider;
    private readonly LicenseOptions _license;
    private readonly AdvancedAnalysisOptions _options;
    private readonly ILogger<AdvancedAnalysisWorker> _logger;
    private readonly string _workerId =
        $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public AdvancedAnalysisWorker(
        AdvancedAnalysisJobStore store,
        AdvancedAnalysisEvidenceResolver resolver,
        IAdvancedAnalysisToolGatewayFactory toolGatewayFactory,
        IAdvancedAnalysisProvider provider,
        IOptions<LicenseOptions> license,
        IOptions<AdvancedAnalysisOptions> options,
        ILogger<AdvancedAnalysisWorker> logger)
    {
        _store = store;
        _resolver = resolver;
        _toolGatewayFactory = toolGatewayFactory;
        _provider = provider;
        _license = license.Value;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_license.AdvancedAnalysisEnabled)
        {
            _logger.LogInformation(
                "Advanced analysis worker is disabled by the current license");
            return;
        }
        if (!_options.WorkerEnabled)
        {
            _logger.LogInformation("Advanced analysis worker is disabled");
            return;
        }

        var pollDelay = Math.Clamp(
            _options.PollDelayMilliseconds,
            100,
            60_000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOnceAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (!processed)
                    await Task.Delay(pollDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advanced analysis worker cycle failed");
                await Task.Delay(pollDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        if (!_license.AdvancedAnalysisEnabled)
            return false;

        var maximumAttempts = Math.Clamp(_options.MaximumAttempts, 1, 20);
        var retryDelay = Math.Clamp(
            _options.RetryDelayMilliseconds,
            0,
            3_600_000);
        var leaseSeconds = Math.Clamp(_options.LeaseSeconds, 5, 3_600);
        await _store.RecoverExpiredAsync(
            maximumAttempts,
            retryDelay,
            cancellationToken).ConfigureAwait(false);

        var incompatibleJobs = await _store.FailQueuedProviderMismatchesAsync(
            _provider.ProviderKey,
            _provider.ModelId,
            cancellationToken).ConfigureAwait(false);
        if (incompatibleJobs > 0)
        {
            _logger.LogWarning(
                "Failed {JobCount} advanced-analysis job(s) because the configured provider or model changed before retry",
                incompatibleJobs);
            return true;
        }

        var lease = await _store.TryClaimAsync(
            _workerId,
            _provider.ProviderKey,
            _provider.ModelId,
            leaseSeconds,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return false;

        AdvancedAnalysisHandoffEnvelope? handoff;
        try
        {
            handoff = JsonSerializer.Deserialize<AdvancedAnalysisHandoffEnvelope>(
                lease.HandoffJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            await FailAsync(
                lease.JobId,
                "handoff_deserialization_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var handoffValidation = AdvancedAnalysisEndpoints.ValidateCreateRequest(
            new AdvancedAnalysisJobCreateRequest
            {
                UserId = lease.UserId,
                SessionId = lease.SessionId,
                Handoff = handoff!
            });
        if (!handoffValidation.IsValid || handoff is null)
        {
            await FailAsync(
                lease.JobId,
                "handoff_revalidation_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_provider.Location == AdvancedAnalysisProviderLocation.ExternalService
            && (!_options.AllowExternalProviderContent
                || !_options.AllowExternalProviderMetadata))
        {
            await FailAsync(
                lease.JobId,
                "external_provider_not_authorized",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        IReadOnlyList<AdvancedAnalysisToolEventSummary> previousToolEvents;
        try
        {
            previousToolEvents = await _store.LoadToolHistoryAsync(
                lease.TenantId,
                lease.JobId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Advanced analysis tool history could not be loaded for job {JobId}",
                lease.JobId);
            await FailAsync(
                lease.JobId,
                "tool_trace_revalidation_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var evidenceReferences = BuildResumeEvidenceReferences(
            handoff.ResearchState.EvidenceReferences,
            previousToolEvents);
        var evidenceResolution = await _resolver.ResolveAsync(
            lease.TenantId,
            evidenceReferences,
            cancellationToken).ConfigureAwait(false);
        if (!evidenceResolution.IsValid)
        {
            await FailAsync(
                lease.JobId,
                evidenceResolution.ErrorCode ?? "evidence_revalidation_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        var tools = _toolGatewayFactory.Create(
            lease.JobId,
            lease.TenantId,
            _workerId,
            lease.AttemptCount,
            evidenceResolution.Evidence);
        ProviderExecution execution;
        try
        {
            execution = await ExecuteProviderWithLeaseAsync(
                new AdvancedAnalysisProviderRequest(
                    lease.JobId,
                    lease.TenantId,
                    lease.UserId,
                    handoff,
                    evidenceResolution.Evidence,
                    previousToolEvents),
                tools,
                lease.JobId,
                leaseSeconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AdvancedAnalysisProviderException ex)
        {
            if (ex.IsRetryable)
            {
                var retryDelayMilliseconds = ResolveJobRetryDelayMilliseconds(ex);
                var retryStored = await _store.TryScheduleRetryAsync(
                    lease.JobId,
                    _workerId,
                    ex.ErrorCode,
                    retryDelayMilliseconds,
                    maximumAttempts,
                    cancellationToken).ConfigureAwait(false);
                if (!retryStored)
                {
                    _logger.LogInformation(
                        "Advanced analysis retry {ErrorCode} was not scheduled because job {JobId} no longer owns its lease",
                        ex.ErrorCode,
                        lease.JobId);
                }
                return true;
            }
            await FailAsync(
                lease.JobId,
                ex.ErrorCode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AdvancedAnalysisToolException ex)
        {
            await FailAsync(
                lease.JobId,
                ex.ErrorCode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Advanced analysis provider failed for job {JobId}",
                lease.JobId);
            await FailAsync(
                lease.JobId,
                "provider_execution_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (execution.State != AdvancedAnalysisLeaseState.Active
            || execution.Result is null)
        {
            return true;
        }

        var validated = AdvancedAnalysisResultValidator.ValidateAndBuild(
            _provider.ProviderKey,
            execution.Result,
            tools.Evidence,
            stopwatch.ElapsedMilliseconds,
            DateTimeOffset.UtcNow);
        if (!validated.IsValid || validated.Result is null)
        {
            await FailAsync(
                lease.JobId,
                validated.ErrorCode ?? "advanced_result_invalid",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var stored = await _store.TryMarkSucceededAsync(
            lease.JobId,
            _workerId,
            validated.Result,
            cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            _logger.LogInformation(
                "Advanced analysis result was not published because job {JobId} no longer owns its lease",
                lease.JobId);
        }
        return true;
    }

    private int ResolveJobRetryDelayMilliseconds(
        AdvancedAnalysisProviderException exception)
    {
        var maximumDelay = Math.Clamp(
            _options.MaximumJobRetryDelayMilliseconds,
            1_000,
            604_800_000);
        var configuredDelay = Math.Clamp(
            _options.RetryDelayMilliseconds,
            0,
            maximumDelay);
        var providerDelay = exception.RetryAfterMilliseconds is null
            ? 0L
            : Math.Clamp(
                exception.RetryAfterMilliseconds.Value,
                0L,
                maximumDelay);
        return (int)Math.Max(configuredDelay, providerDelay);
    }

    private async Task<ProviderExecution> ExecuteProviderWithLeaseAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        Guid jobId,
        int leaseSeconds,
        CancellationToken stoppingToken)
    {
        var heartbeatMilliseconds = Math.Clamp(
            _options.HeartbeatMilliseconds,
            25,
            Math.Max(25, leaseSeconds * 500));
        using var providerCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(stoppingToken);
        var providerTask = _provider.ExecuteAsync(
            request,
            tools,
            providerCancellation.Token);

        while (!providerTask.IsCompleted)
        {
            var delay = Task.Delay(heartbeatMilliseconds, stoppingToken);
            if (await Task.WhenAny(providerTask, delay).ConfigureAwait(false)
                == providerTask)
            {
                break;
            }

            var state = await _store.RenewAndReadStateAsync(
                jobId,
                _workerId,
                leaseSeconds,
                stoppingToken).ConfigureAwait(false);
            if (state == AdvancedAnalysisLeaseState.Active)
                continue;

            providerCancellation.Cancel();
            try
            {
                await providerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (providerCancellation.IsCancellationRequested)
            {
            }
            return new ProviderExecution(state, null);
        }

        return new ProviderExecution(
            AdvancedAnalysisLeaseState.Active,
            await providerTask.ConfigureAwait(false));
    }

    private async Task FailAsync(
        Guid jobId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var stored = await _store.TryMarkFailedAsync(
            jobId,
            _workerId,
            errorCode,
            cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            _logger.LogInformation(
                "Advanced analysis failure {ErrorCode} was not published because job {JobId} no longer owns its lease",
                errorCode,
                jobId);
        }
    }

    private static IReadOnlyList<AdvancedAnalysisEvidenceReference>
        BuildResumeEvidenceReferences(
            IReadOnlyList<AdvancedAnalysisEvidenceReference> handoffEvidence,
            IReadOnlyList<AdvancedAnalysisToolEventSummary> toolEvents)
    {
        var result = new List<AdvancedAnalysisEvidenceReference>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in handoffEvidence.Concat(
                     toolEvents
                         .Where(static item => item.Status == "succeeded")
                         .SelectMany(static item => item.Evidence)
                         .Select(static item => new AdvancedAnalysisEvidenceReference
                         {
                             EvidenceId = item.EvidenceId,
                             DocId = item.DocId,
                             RevisionId = item.RevisionId,
                             FileName = item.FileName,
                             DocPath = item.DocPath,
                             SourceHash = item.SourceHash,
                             PageStart = item.PageStart,
                             PageEnd = item.PageEnd,
                             ChunkId = item.ChunkId,
                             AnchorId = item.AnchorId,
                             ContentCardId = item.ContentCardId
                         })))
        {
            var key = string.Join(
                "|",
                reference.DocId ?? string.Empty,
                reference.RevisionId ?? string.Empty,
                reference.ChunkId ?? string.Empty,
                reference.AnchorId ?? string.Empty,
                reference.ContentCardId ?? string.Empty,
                reference.PageStart,
                reference.PageEnd,
                reference.SourceHash ?? string.Empty);
            if (keys.Add(key))
                result.Add(reference);
        }
        return result;
    }

    private sealed record ProviderExecution(
        AdvancedAnalysisLeaseState State,
        AdvancedAnalysisProviderResult? Result);
}
