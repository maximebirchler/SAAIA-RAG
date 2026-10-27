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
    private readonly AdvancedAnalysisRuntimeState _runtimeState;
    private readonly string _workerId =
        $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public AdvancedAnalysisWorker(
        AdvancedAnalysisJobStore store,
        AdvancedAnalysisEvidenceResolver resolver,
        IAdvancedAnalysisToolGatewayFactory toolGatewayFactory,
        IAdvancedAnalysisProvider provider,
        IOptions<LicenseOptions> license,
        IOptions<AdvancedAnalysisOptions> options,
        ILogger<AdvancedAnalysisWorker> logger,
        AdvancedAnalysisRuntimeState? runtimeState = null)
    {
        _store = store;
        _resolver = resolver;
        _toolGatewayFactory = toolGatewayFactory;
        _provider = provider;
        _license = license.Value;
        _options = options.Value;
        _logger = logger;
        _runtimeState = runtimeState ?? new AdvancedAnalysisRuntimeState();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_license.AdvancedAnalysisEnabled)
        {
            _runtimeState.WorkerHeartbeat("not_entitled");
            _logger.LogInformation(
                "Advanced analysis worker is disabled by the current license");
            return;
        }
        if (!_options.WorkerEnabled)
        {
            _runtimeState.WorkerHeartbeat("disabled");
            _logger.LogInformation("Advanced analysis worker is disabled");
            return;
        }

        var pollDelay = Math.Clamp(
            _options.PollDelayMilliseconds,
            100,
            60_000);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _runtimeState.WorkerHeartbeat("idle");
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
                    _runtimeState.WorkerHeartbeat("cycle_failed");
                    _logger.LogError(ex, "Advanced analysis worker cycle failed");
                    await Task.Delay(pollDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally { _runtimeState.WorkerHeartbeat("stopped"); }
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
        var recovered = await _store.RecoverExpiredAsync(
            maximumAttempts,
            retryDelay,
            cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
            _logger.LogWarning("Recovered {JobCount} expired advanced-analysis leases/jobs worker_id={WorkerId}", recovered, _workerId);

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

        _runtimeState.WorkerHeartbeat("executing");
        var parent = ActivityContext.TryParse(lease.ParentTraceparent, null, out var parentContext)
            ? parentContext : (ActivityContext?)null;
        using var probe = BackendDiagnostics.Start("advanced.job_attempt", _logger, parent);
        probe.Set("job_id", lease.JobId);
        probe.Set("tenant_id", lease.TenantId);
        probe.Set("attempt", lease.AttemptCount);
        probe.Set("worker_id", _workerId);
        probe.Set("request_id", lease.RequestId);
        probe.Set("turn_id", lease.TurnId);
        probe.Set("session_id", lease.SessionId);
        probe.Set("client_session_id", lease.DiagnosticSessionId);
        probe.Set("client_session_id_mismatch", lease.DiagnosticSessionId is not null
            && (!Guid.TryParse(lease.DiagnosticSessionId, out var claimedSession) || claimedSession != lease.SessionId));
        probe.Set("queue_age_ms", lease.QueueAgeMilliseconds);
        using var logScope = DiagnosticLogScope.Begin(_logger, new Dictionary<string, object?>
        {
            ["job_id"] = lease.JobId, ["tenant_id"] = lease.TenantId, ["attempt"] = lease.AttemptCount,
            ["worker_id"] = _workerId, ["request_id"] = lease.RequestId,
            ["turn_id"] = lease.TurnId, ["trace_id"] = probe.TraceId,
            ["session_id"] = lease.SessionId, ["client_session_id"] = lease.DiagnosticSessionId
        });
        try
        {
            return await ProcessClaimedAsync(lease, leaseSeconds, maximumAttempts, probe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            probe.Fail(ex);
            throw;
        }
        finally { _runtimeState.WorkerHeartbeat("idle"); }
    }

    private async Task<bool> ProcessClaimedAsync(AdvancedAnalysisJobLease lease, int leaseSeconds,
        int maximumAttempts, BackendDiagnostics.Operation probe, CancellationToken cancellationToken)
    {
        async Task FailClaimedAsync(string errorCode, CancellationToken token)
        {
            probe.Complete("failed", errorCode);
            await FailAsync(lease.JobId, errorCode, token).ConfigureAwait(false);
        }

        AdvancedAnalysisHandoffEnvelope? handoff;
        try
        {
            handoff = JsonSerializer.Deserialize<AdvancedAnalysisHandoffEnvelope>(
                lease.HandoffJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            await FailClaimedAsync(
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
            await FailClaimedAsync(
                "handoff_revalidation_failed",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_provider.Location == AdvancedAnalysisProviderLocation.ExternalService
            && (!_options.AllowExternalProviderContent
                || !_options.AllowExternalProviderMetadata))
        {
            await FailClaimedAsync(
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
            await FailClaimedAsync(
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
            await FailClaimedAsync(
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
            evidenceResolution.Evidence,
            previousToolEvents);
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
                probe.Complete(retryStored ? "retry_scheduled" : "lease_lost", ex.ErrorCode);
                _logger.LogInformation("Advanced-analysis retry outcome job_id={JobId} attempt={Attempt} stored={Stored} reason={ReasonCode} retry_delay_ms={RetryDelayMs}",
                    lease.JobId, lease.AttemptCount, retryStored, ex.ErrorCode, retryDelayMilliseconds);
                return true;
            }
            await FailClaimedAsync(
                ex.ErrorCode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AdvancedAnalysisToolException ex)
        {
            await FailClaimedAsync(
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
            await FailClaimedAsync(
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
            probe.Complete(execution.State == AdvancedAnalysisLeaseState.Canceled ? "cancelled" : "lease_lost");
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
            await FailClaimedAsync(
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
        probe.Complete(stored ? "success" : "lease_lost");
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
        // Preparation can outlive the original claim. Recheck ownership before
        // starting any provider work, including a potentially billed request.
        var initialState = await RenewLeaseAsync().ConfigureAwait(false);
        if (initialState != AdvancedAnalysisLeaseState.Active)
            return new ProviderExecution(initialState, null);

        using var providerCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(stoppingToken);
        var providerTask = _provider.ExecuteAsync(
            request,
            tools,
            providerCancellation.Token);
        try
        {
            while (!providerTask.IsCompleted)
            {
                var delay = Task.Delay(heartbeatMilliseconds, stoppingToken);
                if (await Task.WhenAny(providerTask, delay).ConfigureAwait(false)
                    == providerTask)
                    break;

                var state = await RenewLeaseAsync().ConfigureAwait(false);
                if (state != AdvancedAnalysisLeaseState.Active)
                    return new ProviderExecution(state, null);
            }

            return new ProviderExecution(
                AdvancedAnalysisLeaseState.Active,
                await providerTask.ConfigureAwait(false));
        }
        finally
        {
            // Losing a lease by exception must close the same child lifetime as
            // a returned Lost/Canceled state. Disposing a CTS alone does not
            // cancel its task. Observe completion before another attempt runs.
            using var lifetimeProbe = BackendDiagnostics.Start("advanced.provider_cleanup", _logger);
            lifetimeProbe.Set("job_id", jobId);
            lifetimeProbe.Set("cancel_requested", !providerTask.IsCompleted);
            if (!providerTask.IsCompleted)
            {
                try { providerCancellation.Cancel(); }
                catch (Exception exception) { lifetimeProbe.Set("cancellation_callback_failure", exception.GetType().Name); }
            }
            try
            {
                await providerTask.ConfigureAwait(false);
                lifetimeProbe.Complete();
            }
            catch (OperationCanceledException) when (providerCancellation.IsCancellationRequested)
            {
                lifetimeProbe.Complete("cancelled", "provider_cancellation_observed");
            }
            catch (Exception exception)
            {
                // A cleanup fault must not replace the original lease failure
                // or the provider exception already propagating from the try.
                lifetimeProbe.Fail(exception, "provider_completion_observed");
            }
            lifetimeProbe.Set("completion_observed", true);
        }

        async Task<AdvancedAnalysisLeaseState> RenewLeaseAsync()
        {
            using (var heartbeatProbe = BackendDiagnostics.Start("advanced.lease_renew", _logger))
            {
                heartbeatProbe.Set("job_id", jobId);
                try
                {
                    var state = await _store.RenewAndReadStateAsync(jobId, _workerId, leaseSeconds, stoppingToken).ConfigureAwait(false);
                    _runtimeState.WorkerHeartbeat("executing");
                    heartbeatProbe.Complete(state == AdvancedAnalysisLeaseState.Active ? "success"
                        : state == AdvancedAnalysisLeaseState.Canceled ? "cancelled" : "lease_lost");
                    return state;
                }
                catch (Exception ex) { heartbeatProbe.Fail(ex); throw; }
            }
        }
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
                             ContentCardId = item.ContentCardId,
                             NativeObservation = item.NativeObservation
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
                reference.SourceHash ?? string.Empty,
                reference.NativeObservation?.LineId ?? string.Empty);
            if (keys.Add(key))
                result.Add(reference);
        }
        return result;
    }

    private sealed record ProviderExecution(
        AdvancedAnalysisLeaseState State,
        AdvancedAnalysisProviderResult? Result);
}
