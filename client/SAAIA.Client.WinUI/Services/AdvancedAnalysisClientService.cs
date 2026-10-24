using System.Net;
using System.Net.Http;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Contracts;
using SAAIA.Client.Core.AdvancedAnalysis;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal sealed class AdvancedAnalysisClientPollingOptions
{
    public int MaximumCreateAttempts { get; init; } = 3;

    public int MaximumPolls { get; init; } = 600;

    public int MaximumConsecutiveTransportFailures { get; init; } = 8;

    public TimeSpan PollDelay { get; init; } = TimeSpan.FromMilliseconds(1_500);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan MaximumScheduledPollDelay { get; init; } = TimeSpan.FromMinutes(1);
}

internal sealed record AdvancedAnalysisClientExecutionResult(
    bool Handled,
    string Outcome,
    string? FinalAnswer,
    object? SourcesPayload,
    AdvancedAnalysisJobDto? Job);

internal sealed class AdvancedAnalysisClientService
{
    private readonly ApiClient _api;
    private readonly Action<List<ToolMemory.SourceRef>, string>? _onCompleted;
    internal AdvancedAnalysisClientService(ApiClient api, Action<List<ToolMemory.SourceRef>, string>? onCompleted = null)
        => (_api, _onCompleted) = (api, onCompleted);

    private static readonly HashSet<string> AdvancedAnalysisResultOutcomes = new(
        ["answered", "insufficient_documentation", "clarification_required"],
        StringComparer.Ordinal);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ExecuteAdvancedAnalysisHandoffAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            CancellationToken cancellationToken,
            Action<string>? onProgress = null,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => ExecuteAdvancedAnalysisHandoffCoreAsync(
            sessionId,
            handoff,
            new AdvancedAnalysisClientPollingOptions(),
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken,
            onProgress,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ExecuteAdvancedAnalysisHandoffForTestsAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => ExecuteAdvancedAnalysisHandoffCoreAsync(
            sessionId,
            handoff,
            options,
            delayAsync,
            cancellationToken,
            onProgress: null,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ResumeAdvancedAnalysisJobAsync(
            Guid sessionId,
            Guid handoffId,
            Guid jobId,
            int minimumRevision,
            string language,
            CancellationToken cancellationToken,
            Action<string>? onProgress = null,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => ResumeAdvancedAnalysisJobCoreAsync(
            sessionId,
            handoffId,
            jobId,
            minimumRevision,
            language,
            new AdvancedAnalysisClientPollingOptions(),
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken,
            onProgress,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ResumeAdvancedAnalysisJobForTestsAsync(
            Guid sessionId,
            Guid handoffId,
            Guid jobId,
            int minimumRevision,
            string language,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => ResumeAdvancedAnalysisJobCoreAsync(
            sessionId,
            handoffId,
            jobId,
            minimumRevision,
            language,
            options,
            delayAsync,
            cancellationToken,
            onProgress: null,
            onSnapshot);

    internal async Task<AdvancedAnalysisClientExecutionResult>
        ExecuteAdvancedAnalysisHandoffCoreAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Action<string>? onProgress,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (sessionId == Guid.Empty || handoff.HandoffId == Guid.Empty)
        {
            return InvalidAdvancedAnalysisResult(
                handoff.Language,
                null,
                "invalid_request_identity");
        }

        AdvancedAnalysisJobDto? job = null;
        try
        {
            job = await CreateAdvancedAnalysisJobWithRetryAsync(
                    sessionId,
                    handoff,
                    options,
                    delayAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (job is null)
            {
                return new AdvancedAnalysisClientExecutionResult(
                    Handled: false,
                    Outcome: "transport_unavailable",
                    FinalAnswer: null,
                    SourcesPayload: null,
                    Job: null);
            }

            var identityError = ValidateAdvancedAnalysisJobSnapshot(
                job,
                expectedJobId: null,
                handoff.HandoffId,
                sessionId,
                minimumRevision: 1);
            if (identityError is not null)
                return InvalidAdvancedAnalysisResult(handoff.Language, job, identityError);
            ClientDiagnostics.Journal.Configuration(new Dictionary<string, string>
                { ["jobId"] = job.JobId.ToString("D"), ["handoffId"] = job.HandoffId.ToString("D") });
            var createdSnapshot = SnapshotAdvancedAnalysisResult(
                handoff.Language,
                job,
                "job_created");
            await EmitAdvancedAnalysisSnapshotAsync(
                    createdSnapshot,
                    onSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsAdvancedAnalysisTerminal(job.Status))
                return createdSnapshot;

            return await PollAdvancedAnalysisJobAsync(
                    job,
                    handoff.Language,
                    options,
                    delayAsync,
                    cancellationToken,
                    onProgress,
                    onSnapshot)
                .ConfigureAwait(false);
        }
        catch (AdvancedAnalysisApiException exception) when (
            exception.StatusCode == HttpStatusCode.Forbidden
            && string.Equals(
                exception.ErrorCode,
                "advanced_analysis_not_entitled",
                StringComparison.Ordinal))
        {
            return new AdvancedAnalysisClientExecutionResult(
                Handled: false,
                Outcome: "not_entitled",
                FinalAnswer: null,
                SourcesPayload: null,
                Job: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (job is not null && !IsAdvancedAnalysisTerminal(job.Status))
                await TryCancelAdvancedAnalysisJobAsync(job.JobId).ConfigureAwait(false);
            throw;
        }
        catch (AdvancedAnalysisApiException exception)
        {
            ClientLog.Warn(
                "Advanced analysis request rejected by the server: " +
                $"code={exception.ErrorCode}|status={(int?)exception.StatusCode ?? 0}");
            return new AdvancedAnalysisClientExecutionResult(
                Handled: true,
                Outcome: "server_rejected",
                FinalAnswer: DeterministicAgentText.AdvancedAnalysisFailed(
                    handoff.Language,
                    exception.ErrorCode),
                SourcesPayload: job is null
                    ? null
                    : BuildAdvancedAnalysisSourcesPayload(
                        job,
                        [],
                        exception.ErrorCode),
                Job: job);
        }
        catch (InvalidDataException exception)
        {
            return InvalidAdvancedAnalysisResult(
                handoff.Language,
                job,
                exception.Message);
        }
        finally
        {
            onProgress?.Invoke(string.Empty);
        }
    }

    internal async Task<AdvancedAnalysisClientExecutionResult>
        ResumeAdvancedAnalysisJobCoreAsync(
            Guid sessionId,
            Guid handoffId,
            Guid jobId,
            int minimumRevision,
            string language,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Action<string>? onProgress,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot)
    {
        if (sessionId == Guid.Empty || handoffId == Guid.Empty || jobId == Guid.Empty)
            return InvalidAdvancedAnalysisResult(language, null, "invalid_request_identity");

        try
        {
            var job = await _api
                .GetAdvancedAnalysisJobAsync(jobId, cancellationToken)
                .ConfigureAwait(false);
            var identityError = ValidateAdvancedAnalysisJobSnapshot(
                job,
                jobId,
                handoffId,
                sessionId,
                Math.Max(1, minimumRevision));
            if (identityError is not null)
                return InvalidAdvancedAnalysisResult(language, job, identityError);

            ClientDiagnostics.Journal.Configuration(new Dictionary<string, string>
                { ["jobId"] = job.JobId.ToString("D"), ["handoffId"] = job.HandoffId.ToString("D") });
            var snapshot = SnapshotAdvancedAnalysisResult(language, job, "job_resumed");
            await EmitAdvancedAnalysisSnapshotAsync(snapshot, onSnapshot, cancellationToken)
                .ConfigureAwait(false);
            if (IsAdvancedAnalysisTerminal(job.Status))
                return snapshot;

            return await PollAdvancedAnalysisJobAsync(
                    job,
                    language,
                    options,
                    delayAsync,
                    cancellationToken,
                    onProgress,
                    onSnapshot)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping a local restart tracker must leave the durable server job running.
            throw;
        }
        catch (Exception exception) when (IsTransientAdvancedAnalysisException(exception))
        {
            return new AdvancedAnalysisClientExecutionResult(
                Handled: false,
                Outcome: "transport_unavailable",
                FinalAnswer: null,
                SourcesPayload: null,
                Job: null);
        }
        catch (AdvancedAnalysisApiException exception)
        {
            ClientLog.Warn(
                "Advanced analysis resume rejected by the server: " +
                $"code={exception.ErrorCode}|status={(int?)exception.StatusCode ?? 0}");
            return new AdvancedAnalysisClientExecutionResult(
                Handled: true,
                Outcome: "server_rejected",
                FinalAnswer: DeterministicAgentText.AdvancedAnalysisFailed(
                    language,
                    exception.ErrorCode),
                SourcesPayload: null,
                Job: null);
        }
        finally
        {
            onProgress?.Invoke(string.Empty);
        }
    }

    private async Task<AdvancedAnalysisClientExecutionResult> PollAdvancedAnalysisJobAsync(
        AdvancedAnalysisJobDto job, string language, AdvancedAnalysisClientPollingOptions options,
        Func<TimeSpan, CancellationToken, Task> delayAsync, CancellationToken cancellationToken,
        Action<string>? onProgress,
        Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot)
    {
        AdvancedAnalysisClientExecutionResult? lastSnapshot = null;
        var poller = new AdvancedAnalysisJobPoller(_api.GetAdvancedAnalysisJobAsync, delayAsync,
            observe: probe => ClientDiagnostics.Journal.Emit("advanced.poll." + probe.Operation,
                probe.Outcome, probe.Reason, new Dictionary<string, long?>
                {
                    ["attempt"] = probe.Attempt, ["consecutiveFailures"] = probe.ConsecutiveFailures,
                    ["revision"] = probe.Revision, ["durationMs"] = probe.DurationMilliseconds
                }),
            retryAfter: exception => (exception as AdvancedAnalysisApiException)?.RetryAfter);
        var result = await poller.PollAsync(job, new AdvancedAnalysisPollingPolicy
        {
            MaximumPolls = options.MaximumPolls,
            MaximumConsecutiveTransportFailures = options.MaximumConsecutiveTransportFailures,
            PollDelay = options.PollDelay, RetryBaseDelay = options.RetryBaseDelay,
            MaximumRetryDelay = options.MaximumRetryDelay,
            MaximumScheduledPollDelay = options.MaximumScheduledPollDelay
        }, cancellationToken,
            onSnapshot: async (snapshotJob, token) =>
            {
                lastSnapshot = SnapshotAdvancedAnalysisResult(language, snapshotJob, "job_polled");
                await EmitAdvancedAnalysisSnapshotAsync(lastSnapshot, onSnapshot, token).ConfigureAwait(false);
            },
            onWaiting: waiting => onProgress?.Invoke(waiting.Status == "running"
                ? DeterministicAgentText.ProgressAdvancedAnalysisRunning(language)
                : waiting.LastErrorCode == "advanced_llm_http_429"
                    ? DeterministicAgentText.ProgressAdvancedAnalysisRateLimitRetry(language)
                    : DeterministicAgentText.ProgressAdvancedAnalysisQueued(language))).ConfigureAwait(false);
        return result.Outcome switch
        {
            "invalid" => InvalidAdvancedAnalysisResult(language, result.Job, result.Reason ?? "invalid_snapshot"),
            "pending" => PendingAdvancedAnalysisResult(language, result.Job, result.Reason ?? "pending"),
            _ => lastSnapshot ?? CompleteAdvancedAnalysisResult(language, result.Job)
        };
    }

    private AdvancedAnalysisClientExecutionResult SnapshotAdvancedAnalysisResult(
        string language,
        AdvancedAnalysisJobDto job,
        string pendingReason)
        => IsAdvancedAnalysisTerminal(job.Status)
            ? CompleteAdvancedAnalysisResult(language, job)
            : PendingAdvancedAnalysisResult(language, job, pendingReason);

    private static Task EmitAdvancedAnalysisSnapshotAsync(
        AdvancedAnalysisClientExecutionResult snapshot,
        Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot,
        CancellationToken cancellationToken)
        => onSnapshot is null
            ? Task.CompletedTask
            : onSnapshot(snapshot, cancellationToken);

    private async Task<AdvancedAnalysisJobDto?>
        CreateAdvancedAnalysisJobWithRetryAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken)
    {
        var maximumAttempts = Math.Clamp(options.MaximumCreateAttempts, 1, 20);
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                ClientDiagnostics.Journal.Emit("advanced.create.attempt", "success", measurements:
                    new Dictionary<string, long?> { ["attempt"] = attempt });
                return await _api
                    .CreateAdvancedAnalysisJobAsync(
                        sessionId,
                        handoff,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsTransientAdvancedAnalysisException(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                ClientDiagnostics.Journal.Emit("advanced.create.retry", "degraded", exception.GetType().Name,
                    new Dictionary<string, long?> { ["attempt"] = attempt, ["maximumAttempts"] = maximumAttempts });
                if (attempt == maximumAttempts)
                    return null;
                await delayAsync(
                        ResolveAdvancedAnalysisRetryDelay(exception, attempt, options),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task TryCancelAdvancedAnalysisJobAsync(Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _api
                .CancelAdvancedAnalysisJobAsync(jobId, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.Journal.Emit("advanced.cancel", "failed", exception.GetType().Name);
            ClientLog.Warn($"Advanced analysis cancellation failed for jobId={jobId:D}: {exception.GetType().Name}");
        }
    }

    private AdvancedAnalysisClientExecutionResult CompleteAdvancedAnalysisResult(
        string language,
        AdvancedAnalysisJobDto job)
    {
        if (string.Equals(job.Status, "failed", StringComparison.Ordinal))
        {
            return new AdvancedAnalysisClientExecutionResult(
                Handled: true,
                Outcome: "failed",
                FinalAnswer: DeterministicAgentText.AdvancedAnalysisFailed(
                    language,
                    job.LastErrorCode),
                SourcesPayload: BuildAdvancedAnalysisSourcesPayload(job, [], "failed"),
                Job: job);
        }
        if (string.Equals(job.Status, "canceled", StringComparison.Ordinal))
        {
            return new AdvancedAnalysisClientExecutionResult(
                Handled: true,
                Outcome: "canceled",
                FinalAnswer: DeterministicAgentText.AdvancedAnalysisCanceled(language),
                SourcesPayload: BuildAdvancedAnalysisSourcesPayload(job, [], "canceled"),
                Job: job);
        }

        var validation = ValidateAdvancedAnalysisResult(job);
        if (!validation.IsValid || validation.Result is null)
        {
            return InvalidAdvancedAnalysisResult(
                language,
                job,
                validation.ErrorCode ?? "advanced_result_invalid");
        }

        var sources = validation.Result.Evidence
            .Select(evidence => MapAdvancedAnalysisSource(evidence, language))
            .GroupBy(
                static source => string.Join(
                    '|',
                    source.DocId,
                    source.RevisionId,
                    source.PageStart,
                    source.PageEnd,
                    source.SourceHash),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        _onCompleted?.Invoke(sources, validation.Result.AnswerText);
        ClientDiagnostics.Journal.Emit("advanced.result.validation", "success", measurements:
            new Dictionary<string, long?> { ["evidence"] = validation.Result.Evidence.Count,
                ["claims"] = validation.Result.Claims.Count, ["visibleSources"] = sources.Count });
        return new AdvancedAnalysisClientExecutionResult(
            Handled: true,
            Outcome: "succeeded",
            FinalAnswer: validation.Result.AnswerText,
            SourcesPayload: BuildAdvancedAnalysisSourcesPayload(
                job,
                sources,
                validation.Result.Outcome,
                validation.Result),
            Job: job);
    }

    private static AdvancedAnalysisClientExecutionResult PendingAdvancedAnalysisResult(
        string language,
        AdvancedAnalysisJobDto job,
        string reason)
        => new(
            Handled: true,
            Outcome: "pending",
            FinalAnswer: DeterministicAgentText.AdvancedAnalysisPending(language),
            SourcesPayload: BuildAdvancedAnalysisSourcesPayload(job, [], reason),
            Job: job);

    private static AdvancedAnalysisClientExecutionResult InvalidAdvancedAnalysisResult(
        string language,
        AdvancedAnalysisJobDto? job,
        string reason)
    {
        ClientDiagnostics.Journal.Emit("advanced.result.validation", "failed", reason);
        ClientLog.Warn(
            "Advanced analysis result blocked by the client contract: " +
            $"reason={reason}|jobId={job?.JobId.ToString("D") ?? "none"}");
        return new AdvancedAnalysisClientExecutionResult(
            Handled: true,
            Outcome: "invalid_result",
            FinalAnswer: DeterministicAgentText.AdvancedAnalysisInvalidResult(language),
            SourcesPayload: job is null
                ? null
                : BuildAdvancedAnalysisSourcesPayload(job, [], reason),
            Job: job);
    }

    private static string? ValidateAdvancedAnalysisJobSnapshot(
        AdvancedAnalysisJobDto job, Guid? expectedJobId, Guid expectedHandoffId,
        Guid expectedSessionId, int minimumRevision)
        => AdvancedAnalysisJobPoller.ValidateSnapshot(job, expectedJobId, expectedHandoffId, expectedSessionId, minimumRevision);

    private static bool IsAdvancedAnalysisTerminal(string? status)
        => status is "succeeded" or "failed" or "canceled";

    private static AdvancedAnalysisClientResultValidation
        ValidateAdvancedAnalysisResult(AdvancedAnalysisJobDto job)
    {
        if (!string.Equals(job.Status, "succeeded", StringComparison.Ordinal)
            || job.Result is not { ValueKind: JsonValueKind.Object } resultJson)
        {
            return AdvancedAnalysisClientResultValidation.Invalid(
                "advanced_result_required");
        }

        AdvancedAnalysisResultEnvelope? result;
        try
        {
            result = resultJson.Deserialize<AdvancedAnalysisResultEnvelope>(
                ClientJson.CamelCase);
        }
        catch (JsonException)
        {
            return AdvancedAnalysisClientResultValidation.Invalid(
                "advanced_result_json_invalid");
        }

        if (result is null
            || !string.Equals(
                result.SchemaVersion,
                AdvancedAnalysisResultEnvelope.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return AdvancedAnalysisClientResultValidation.Invalid(
                "advanced_result_schema_invalid");
        }
        if (!AdvancedAnalysisResultOutcomes.Contains(result.Outcome ?? string.Empty))
            return AdvancedAnalysisClientResultValidation.Invalid("advanced_result_outcome_invalid");
        if (string.IsNullOrWhiteSpace(result.AnswerText)
            || result.AnswerText.Length > 200_000)
        {
            return AdvancedAnalysisClientResultValidation.Invalid("advanced_result_answer_invalid");
        }
        if (string.IsNullOrWhiteSpace(result.ProviderKey)
            || result.ProviderKey.Length > 100
            || !string.Equals(result.ProviderKey, job.ProviderKey, StringComparison.Ordinal))
        {
            return AdvancedAnalysisClientResultValidation.Invalid("advanced_result_provider_invalid");
        }
        if (result.ProviderModel.Length > 256
            || result.ProviderCallCount is < 0 or > 1_024
            || !IsValidAdvancedUsage(result.InputTokens)
            || !IsValidAdvancedUsage(result.OutputTokens)
            || !IsValidAdvancedUsage(result.CachedInputTokens)
            || (result.CachedInputTokens.HasValue
                && result.InputTokens.HasValue
                && result.CachedInputTokens > result.InputTokens)
            || result.EstimatedCostUsd is < 0 or > 1_000_000m)
        {
            return AdvancedAnalysisClientResultValidation.Invalid(
                "advanced_result_metrics_invalid");
        }
        if (result.CompletedAtUtc == default
            || result.ElapsedMilliseconds < 0
            || result.Evidence is null
            || result.Evidence.Count > 256
            || result.Claims is null
            || result.Claims.Count > 512)
        {
            return AdvancedAnalysisClientResultValidation.Invalid("advanced_result_bounds_invalid");
        }
        if (string.Equals(result.Outcome, "answered", StringComparison.Ordinal)
            && (result.Evidence.Count == 0 || result.Claims.Count == 0))
        {
            return AdvancedAnalysisClientResultValidation.Invalid(
                "advanced_answer_requires_evidence");
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var canonicalIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in result.Evidence)
        {
            if (evidence is null
                || string.IsNullOrWhiteSpace(evidence.EvidenceId)
                || evidence.EvidenceId.Length > 200
                || !evidenceIds.Add(evidence.EvidenceId)
                || !Guid.TryParse(evidence.DocId, out _)
                || !Guid.TryParse(evidence.RevisionId, out _)
                || string.IsNullOrWhiteSpace(evidence.FileName)
                || string.IsNullOrWhiteSpace(evidence.DocPath)
                || string.IsNullOrWhiteSpace(evidence.SourceHash)
                || evidence.PageStart <= 0
                || evidence.PageEnd < evidence.PageStart)
            {
                return AdvancedAnalysisClientResultValidation.Invalid(
                    "advanced_evidence_identity_invalid");
            }

            var canonicalIdentity = string.Join(
                '|',
                evidence.DocId,
                evidence.RevisionId,
                evidence.ChunkId ?? string.Empty,
                evidence.AnchorId ?? string.Empty,
                evidence.ContentCardId ?? string.Empty,
                evidence.PageStart,
                evidence.PageEnd,
                evidence.SourceHash);
            if (!canonicalIdentities.Add(canonicalIdentity))
            {
                return AdvancedAnalysisClientResultValidation.Invalid(
                    "advanced_evidence_duplicate");
            }
        }

        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in result.Claims)
        {
            if (claim is null
                || string.IsNullOrWhiteSpace(claim.ClaimId)
                || claim.ClaimId.Length > 100
                || !claimIds.Add(claim.ClaimId)
                || claim.SelectedItem?.Length > 1_000
                || string.IsNullOrWhiteSpace(claim.Text)
                || claim.Text.Length > 8_000
                || claim.EvidenceIds is null
                || claim.EvidenceIds.Count is 0 or > 64)
            {
                return AdvancedAnalysisClientResultValidation.Invalid(
                    "advanced_claim_invalid");
            }

            var cited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidenceId in claim.EvidenceIds)
            {
                if (string.IsNullOrWhiteSpace(evidenceId)
                    || !evidenceIds.Contains(evidenceId)
                    || !cited.Add(evidenceId))
                {
                    return AdvancedAnalysisClientResultValidation.Invalid(
                        "advanced_citation_invalid");
                }
            }
        }

        return AdvancedAnalysisClientResultValidation.Valid(result);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidAdvancedUsage(int? value)
        => value is null or >= 0;

    private static ToolMemory.SourceRef MapAdvancedAnalysisSource(
        AdvancedAnalysisResultEvidence evidence,
        string language)
    {
        var name = evidence.FileName.Trim();
        var pages = evidence.PageStart == evidence.PageEnd
            ? $"p. {evidence.PageStart}"
            : $"pp. {evidence.PageStart}-{evidence.PageEnd}";
        return new ToolMemory.SourceRef
        {
            EvidenceId = evidence.EvidenceId.Trim(),
            DocId = evidence.DocId.Trim(),
            RevisionId = evidence.RevisionId.Trim(),
            DocName = name,
            DocPath = evidence.DocPath.Trim(),
            SourceHash = evidence.SourceHash.Trim(),
            PageStart = evidence.PageStart,
            PageEnd = evidence.PageEnd,
            ChunkId = NullIfBlank(evidence.ChunkId),
            AnchorId = NullIfBlank(evidence.AnchorId),
            ContentCardId = NullIfBlank(evidence.ContentCardId),
            Label = $"{name} — {pages}",
            ContentRole = "content"
        };
    }

    private static object BuildAdvancedAnalysisSourcesPayload(
        AdvancedAnalysisJobDto job,
        List<ToolMemory.SourceRef> sources,
        string resultOutcome,
        AdvancedAnalysisResultEnvelope? result = null)
        => new
        {
            intent = "advanced_analysis.answer",
            sources = sources.Select(source => new
            {
                evidenceId = source.EvidenceId, docId = source.DocId, docPath = source.DocPath,
                docName = source.DocName, pageStart = source.PageStart, pageEnd = source.PageEnd,
                label = source.Label, sourceHash = source.SourceHash, revisionId = source.RevisionId,
                chunkId = source.ChunkId, anchorId = source.AnchorId, contentCardId = source.ContentCardId,
                contentSignals = new { contentRole = source.ContentRole }
            }).ToArray(),
            advancedAnalysis = new
            {
                schemaVersion = "saaia.advanced-analysis-client-state.v1",
                traceId = ClientDiagnostics.Journal.CurrentTraceId,
                turnId = ClientDiagnostics.Journal.CurrentTurnId,
                originTraceId = ClientDiagnostics.Journal.ConfigurationValue("originTraceId") ?? ClientDiagnostics.Journal.CurrentTraceId,
                jobId = job.JobId,
                handoffId = job.HandoffId,
                sessionId = job.SessionId,
                status = job.Status,
                revision = job.Revision,
                attemptCount = job.AttemptCount,
                cancelRequested = job.CancelRequested,
                updatedAtUtc = job.UpdatedAtUtc,
                availableAtUtc = job.AvailableAtUtc,
                expiresAtUtc = job.ExpiresAtUtc,
                providerKey = job.ProviderKey,
                providerModel = result?.ProviderModel ?? job.ProviderModel,
                providerCallCount = result?.ProviderCallCount,
                claimCount = result?.Claims.Count,
                evidenceCount = result?.Evidence.Count,
                inputTokens = result?.InputTokens,
                outputTokens = result?.OutputTokens,
                cachedInputTokens = result?.CachedInputTokens,
                estimatedCostUsd = result?.EstimatedCostUsd,
                resultOutcome,
                lastErrorCode = job.LastErrorCode
            }
        };

    private static bool IsTransientAdvancedAnalysisException(Exception exception)
        => AdvancedAnalysisJobPoller.IsTransient(exception);

    private static TimeSpan ResolveAdvancedAnalysisRetryDelay(
        Exception exception,
        int attempt,
        AdvancedAnalysisClientPollingOptions options)
    {
        if (exception is AdvancedAnalysisApiException { RetryAfter: { } retryAfter })
            return ClampAdvancedAnalysisDelay(retryAfter, options.MaximumRetryDelay);
        var exponent = Math.Clamp(attempt - 1, 0, 10);
        var milliseconds = Math.Max(0, options.RetryBaseDelay.TotalMilliseconds)
                           * Math.Pow(2, exponent);
        return ClampAdvancedAnalysisDelay(
            TimeSpan.FromMilliseconds(milliseconds),
            options.MaximumRetryDelay);
    }

    private static TimeSpan ClampAdvancedAnalysisDelay(
        TimeSpan delay,
        TimeSpan maximum)
    {
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;
        var boundedMaximum = maximum <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(8)
            : maximum;
        return delay > boundedMaximum ? boundedMaximum : delay;
    }

    private sealed record AdvancedAnalysisClientResultValidation(
        bool IsValid,
        string? ErrorCode,
        AdvancedAnalysisResultEnvelope? Result)
    {
        public static AdvancedAnalysisClientResultValidation Valid(
            AdvancedAnalysisResultEnvelope result)
            => new(true, null, result);

        public static AdvancedAnalysisClientResultValidation Invalid(string errorCode)
            => new(false, errorCode, null);
    }
}
