using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed class AdvancedAnalysisJobStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public AdvancedAnalysisJobStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<int> RecoverExpiredAsync(
        int maximumAttempts,
        int retryDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 20);
        retryDelayMilliseconds = Math.Clamp(
            retryDelayMilliseconds,
            0,
            3_600_000);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string expiredQueuedSql = """
            UPDATE advanced_analysis_jobs
            SET status='failed',
                revision=revision+1,
                finished_at=COALESCE(finished_at, now()),
                updated_at=now(),
                last_error_code='job_expired',
                lease_owner=NULL,
                lease_expires_at=NULL
            WHERE status='queued'
              AND expires_at <= now();
            """;
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            expiredQueuedSql,
            cancellationToken: cancellationToken));

        const string expiredRunningSql = """
            UPDATE advanced_analysis_jobs
            SET status = CASE
                    WHEN cancel_requested_at IS NOT NULL THEN 'canceled'
                    WHEN expires_at <= now() THEN 'failed'
                    WHEN attempt_count >= @maximum_attempts THEN 'failed'
                    ELSE 'queued'
                END,
                revision=revision+1,
                available_at = CASE
                    WHEN cancel_requested_at IS NULL
                     AND expires_at > now()
                     AND attempt_count < @maximum_attempts
                    THEN now() + (@retry_delay_ms * interval '1 millisecond')
                    ELSE available_at
                END,
                finished_at = CASE
                    WHEN cancel_requested_at IS NOT NULL
                      OR expires_at <= now()
                      OR attempt_count >= @maximum_attempts
                    THEN COALESCE(finished_at, now())
                    ELSE NULL
                END,
                updated_at=now(),
                last_error_code = CASE
                    WHEN cancel_requested_at IS NOT NULL THEN last_error_code
                    WHEN expires_at <= now() THEN 'job_expired'
                    WHEN attempt_count >= @maximum_attempts THEN 'lease_retry_exhausted'
                    ELSE 'lease_expired_retry'
                END,
                lease_owner=NULL,
                lease_expires_at=NULL
            WHERE status='running'
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at <= now();
            """;
        changed += await connection.ExecuteAsync(new CommandDefinition(
            expiredRunningSql,
            new
            {
                maximum_attempts = maximumAttempts,
                retry_delay_ms = retryDelayMilliseconds
            },
            cancellationToken: cancellationToken));
        return changed;
    }

    public async Task<AdvancedAnalysisJobLease?> TryClaimAsync(
        string workerId,
        string providerKey,
        string providerModel,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        providerModel = (providerModel ?? string.Empty).Trim();
        if (providerModel.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(providerModel));
        leaseSeconds = Math.Clamp(leaseSeconds, 5, 3_600);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            WITH candidate AS (
              SELECT job_id
              FROM advanced_analysis_jobs
              WHERE status='queued'
                AND cancel_requested_at IS NULL
                AND available_at <= now()
                AND expires_at > now()
                AND (provider_key IS NULL OR provider_key=@provider_key)
                AND (provider_model IS NULL OR provider_model=@provider_model)
              ORDER BY available_at, created_at, job_id
              FOR UPDATE SKIP LOCKED
              LIMIT 1
            )
            UPDATE advanced_analysis_jobs j
            SET status='running',
                revision=j.revision+1,
                attempt_count=j.attempt_count+1,
                provider_key=COALESCE(j.provider_key, @provider_key),
                provider_model=COALESCE(j.provider_model, @provider_model),
                lease_owner=@worker_id,
                lease_expires_at=now() + (@lease_seconds * interval '1 second'),
                started_at=COALESCE(j.started_at, now()),
                finished_at=NULL,
                updated_at=now(),
                last_error_code=NULL
            FROM candidate
            WHERE j.job_id=candidate.job_id
            RETURNING
              j.job_id AS "JobId",
              j.tenant_id AS "TenantId",
              j.user_id AS "UserId",
              j.session_id AS "SessionId",
              j.handoff_id AS "HandoffId",
              j.attempt_count AS "AttemptCount",
              j.handoff::text AS "HandoffJson";
            """;
        var row = await connection.QuerySingleOrDefaultAsync<LeaseRow>(
            new CommandDefinition(
                sql,
                new
                {
                    worker_id = workerId,
                    provider_key = providerKey,
                    provider_model = providerModel,
                    lease_seconds = leaseSeconds
                },
                cancellationToken: cancellationToken));
        return row is null
            ? null
            : new AdvancedAnalysisJobLease(
                row.JobId,
                row.TenantId,
                row.UserId,
                row.SessionId,
                row.HandoffId,
                row.AttemptCount,
                row.HandoffJson);
    }

    public Task<AdvancedAnalysisJobLease?> TryClaimAsync(
        string workerId,
        string providerKey,
        int leaseSeconds,
        CancellationToken cancellationToken)
        => TryClaimAsync(
            workerId,
            providerKey,
            string.Empty,
            leaseSeconds,
            cancellationToken);

    public async Task<int> FailQueuedProviderMismatchesAsync(
        string providerKey,
        string providerModel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        providerModel = (providerModel ?? string.Empty).Trim();
        if (providerModel.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(providerModel));
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            UPDATE advanced_analysis_jobs
            SET status='failed',
                revision=revision+1,
                last_error_code='provider_configuration_changed',
                finished_at=now(),
                updated_at=now(),
                lease_owner=NULL,
                lease_expires_at=NULL
            WHERE status='queued'
              AND provider_key IS NOT NULL
              AND (
                provider_key <> @provider_key
                OR (
                  provider_model IS NOT NULL
                  AND provider_model <> @provider_model));
            """;
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                provider_key = providerKey,
                provider_model = providerModel
            },
            cancellationToken: cancellationToken));
    }

    public async Task<AdvancedAnalysisLeaseState> RenewAndReadStateAsync(
        Guid jobId,
        string workerId,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        leaseSeconds = Math.Clamp(leaseSeconds, 5, 3_600);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string renewSql = """
            UPDATE advanced_analysis_jobs
            SET lease_expires_at=now() + (@lease_seconds * interval '1 second'),
                updated_at=now()
            WHERE job_id=@job_id
              AND status='running'
              AND lease_owner=@worker_id
              AND cancel_requested_at IS NULL
              AND lease_expires_at > now()
            RETURNING 1;
            """;
        var renewed = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                renewSql,
                new
                {
                    job_id = jobId,
                    worker_id = workerId,
                    lease_seconds = leaseSeconds
                },
                cancellationToken: cancellationToken));
        if (renewed.HasValue)
            return AdvancedAnalysisLeaseState.Active;

        const string stateSql = """
            SELECT status
            FROM advanced_analysis_jobs
            WHERE job_id=@job_id;
            """;
        var status = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                stateSql,
                new { job_id = jobId },
                cancellationToken: cancellationToken));
        return string.Equals(status, "canceled", StringComparison.Ordinal)
            ? AdvancedAnalysisLeaseState.Canceled
            : AdvancedAnalysisLeaseState.Lost;
    }

    public async Task<int?> TryAppendToolEventAsync(
        Guid tenantId,
        Guid jobId,
        string workerId,
        int attemptCount,
        int maximumEvents,
        string status,
        AdvancedAnalysisSearchRequest request,
        IReadOnlyList<AdvancedAnalysisResultEvidence> evidence,
        IReadOnlyList<string> degradedRetrievers,
        long elapsedMilliseconds,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        maximumEvents = Math.Clamp(maximumEvents, 1, 2_048);
        if (attemptCount < 1
            || status is not ("succeeded" or "failed")
            || evidence.Count > 256)
        {
            return null;
        }

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string reserveSql = """
            UPDATE advanced_analysis_jobs
            SET tool_event_count=tool_event_count+1,
                revision=revision+1,
                updated_at=now()
            WHERE tenant_id=@tenant
              AND job_id=@job_id
              AND status='running'
              AND lease_owner=@worker_id
              AND cancel_requested_at IS NULL
              AND lease_expires_at > now()
              AND tool_event_count < @maximum_events
            RETURNING tool_event_count;
            """;
        var sequence = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                reserveSql,
                new
                {
                    tenant = tenantId,
                    job_id = jobId,
                    worker_id = workerId,
                    maximum_events = maximumEvents
                },
                transaction,
                cancellationToken: cancellationToken));
        if (!sequence.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        const string insertSql = """
            INSERT INTO advanced_analysis_tool_events(
              tenant_id, job_id, event_sequence, attempt_count, worker_id,
              tool_name, status, request, evidence_references,
              degraded_retrievers, elapsed_milliseconds, error_code)
            VALUES(
              @tenant, @job_id, @event_sequence, @attempt_count, @worker_id,
              'source_backed_canonical_search', @status,
              CAST(@request AS jsonb), CAST(@evidence AS jsonb),
              @degraded_retrievers, @elapsed_ms, @error_code);
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                tenant = tenantId,
                job_id = jobId,
                event_sequence = sequence.Value,
                attempt_count = attemptCount,
                worker_id = workerId,
                status,
                request = JsonSerializer.Serialize(request, JsonOptions),
                evidence = JsonSerializer.Serialize(evidence, JsonOptions),
                degraded_retrievers = degradedRetrievers
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray(),
                elapsed_ms = Math.Clamp(elapsedMilliseconds, 0, 3_600_000),
                error_code = errorCode is null
                    ? null
                    : NormalizeErrorCode(errorCode)
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sequence.Value;
    }

    public async Task<IReadOnlyList<AdvancedAnalysisToolEventSummary>>
        LoadToolHistoryAsync(
            Guid tenantId,
            Guid jobId,
            CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            SELECT
              event_sequence AS "EventSequence",
              attempt_count AS "AttemptCount",
              tool_name AS "ToolName",
              status AS "Status",
              request::text AS "RequestJson",
              evidence_references::text AS "EvidenceJson",
              degraded_retrievers AS "DegradedRetrievers",
              elapsed_milliseconds AS "ElapsedMilliseconds",
              error_code AS "ErrorCode"
            FROM advanced_analysis_tool_events
            WHERE tenant_id=@tenant
              AND job_id=@job_id
            ORDER BY event_sequence;
            """;
        var rows = (await connection.QueryAsync<ToolEventRow>(
            new CommandDefinition(
                sql,
                new { tenant = tenantId, job_id = jobId },
                cancellationToken: cancellationToken))).AsList();
        var result = new List<AdvancedAnalysisToolEventSummary>(rows.Count);
        foreach (var row in rows)
        {
            var request = JsonSerializer.Deserialize<AdvancedAnalysisSearchRequest>(
                row.RequestJson,
                JsonOptions);
            var evidence = JsonSerializer.Deserialize<List<AdvancedAnalysisResultEvidence>>(
                row.EvidenceJson,
                JsonOptions);
            if (request is null || evidence is null)
            {
                throw new InvalidDataException(
                    "Advanced analysis tool history contains invalid JSON.");
            }
            result.Add(new AdvancedAnalysisToolEventSummary(
                row.EventSequence,
                row.AttemptCount,
                row.ToolName,
                row.Status,
                request,
                evidence,
                row.DegradedRetrievers ?? [],
                row.ElapsedMilliseconds,
                row.ErrorCode));
        }
        return result;
    }

    public Task<bool> TryMarkSucceededAsync(
        Guid jobId,
        string workerId,
        AdvancedAnalysisResultEnvelope result,
        CancellationToken cancellationToken)
        => TransitionTerminalAsync(
            jobId,
            workerId,
            "succeeded",
            result,
            errorCode: null,
            cancellationToken);

    public Task<bool> TryMarkFailedAsync(
        Guid jobId,
        string workerId,
        string errorCode,
        CancellationToken cancellationToken)
        => TransitionTerminalAsync(
            jobId,
            workerId,
            "failed",
            result: null,
            NormalizeErrorCode(errorCode),
            cancellationToken);

    private async Task<bool> TransitionTerminalAsync(
        Guid jobId,
        string workerId,
        string status,
        AdvancedAnalysisResultEnvelope? result,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var resultJson = result is null
            ? null
            : JsonSerializer.Serialize(result, JsonOptions);
        const string sql = """
            UPDATE advanced_analysis_jobs
            SET status=@status,
                revision=revision+1,
                result=CASE
                    WHEN @result_json IS NULL THEN NULL
                    ELSE CAST(@result_json AS jsonb)
                END,
                last_error_code=@error_code,
                finished_at=now(),
                updated_at=now(),
                lease_owner=NULL,
                lease_expires_at=NULL
            WHERE job_id=@job_id
              AND status='running'
              AND lease_owner=@worker_id
              AND cancel_requested_at IS NULL
              AND lease_expires_at > now();
            """;
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                job_id = jobId,
                worker_id = workerId,
                status,
                result_json = resultJson,
                error_code = errorCode
            },
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    private static string NormalizeErrorCode(string? errorCode)
    {
        var normalized = errorCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "advanced_analysis_failed";
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private sealed class LeaseRow
    {
        public Guid JobId { get; init; }
        public Guid TenantId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public Guid SessionId { get; init; }
        public Guid HandoffId { get; init; }
        public int AttemptCount { get; init; }
        public string HandoffJson { get; init; } = string.Empty;
    }

    private sealed class ToolEventRow
    {
        public int EventSequence { get; init; }
        public int AttemptCount { get; init; }
        public string ToolName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string RequestJson { get; init; } = string.Empty;
        public string EvidenceJson { get; init; } = string.Empty;
        public string[]? DegradedRetrievers { get; init; }
        public long ElapsedMilliseconds { get; init; }
        public string? ErrorCode { get; init; }
    }
}

internal sealed record AdvancedAnalysisJobLease(
    Guid JobId,
    Guid TenantId,
    string UserId,
    Guid SessionId,
    Guid HandoffId,
    int AttemptCount,
    string HandoffJson);

internal enum AdvancedAnalysisLeaseState
{
    Active,
    Canceled,
    Lost
}
