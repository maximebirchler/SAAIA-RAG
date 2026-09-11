using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static class AdvancedAnalysisEndpoints
{
    private const int MaximumRequestCharacters = 32_000;
    private const int MaximumEvidenceReferences = 64;
    private const int MaximumAnswerUnits = 10_000;
    private const int MaximumResearchItems = 32;
    private const int MaximumHandoffBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal sealed class LogTag { }

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/advanced-analysis/jobs");
        group.MapPost("", CreateAsync);
        group.MapGet("/{jobId:guid}", GetAsync);
        group.MapPost("/{jobId:guid}/cancel", CancelAsync);
    }

    internal static async Task<IResult> CreateAsync(
        HttpContext context,
        NpgsqlDataSource dataSource,
        IOptions<LicenseOptions> licenseOptions,
        IOptions<AdvancedAnalysisOptions> advancedOptions,
        ILogger<LogTag> logger,
        AdvancedAnalysisJobCreateRequest request)
    {
        if (!licenseOptions.Value.AdvancedAnalysisEnabled)
        {
            return Results.Json(
                new { error = "advanced_analysis_not_entitled" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var validation = ValidateCreateRequest(request);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new
            {
                error = validation.ErrorCode,
                detail = validation.Detail
            });
        }

        var tenantId = context.GetTenantId();
        var actorApiKeyId = context.GetApiKeyIdOrNull();
        var actorIsAdmin = context.IsAdmin();
        var cancellationToken = context.RequestAborted;
        var userId = request.UserId.Trim();
        var retentionDays = Math.Clamp(
            advancedOptions.Value.RetentionDays,
            1,
            365);
        var maximumQueuedJobs = Math.Clamp(
            advancedOptions.Value.MaximumQueuedJobsPerUser,
            1,
            1_000);

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sessionSql = """
            SELECT 1
            FROM chat_sessions
            WHERE tenant_id=@tenant
              AND session_id=@session
              AND user_id=@user_id
            LIMIT 1;
            """;
        var sessionExists = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                sessionSql,
                new
                {
                    tenant = tenantId,
                    session = request.SessionId,
                    user_id = userId
                },
                cancellationToken: cancellationToken));
        if (sessionExists is null)
            return Results.NotFound(new { error = "chat_session_not_found" });

        var existing = await ReadByHandoffAsync(
            connection,
            tenantId,
            userId,
            request.Handoff.HandoffId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        const string pendingCountSql = """
            SELECT count(*)
            FROM advanced_analysis_jobs
            WHERE tenant_id=@tenant
              AND user_id=@user_id
              AND status IN ('queued', 'running')
              AND expires_at > now();
            """;
        var pendingCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                pendingCountSql,
                new { tenant = tenantId, user_id = userId },
                cancellationToken: cancellationToken));
        if (pendingCount >= maximumQueuedJobs)
        {
            context.Response.Headers.RetryAfter = "5";
            return Results.Json(
                new
                {
                    error = "advanced_analysis_queue_limit_reached",
                    retryAfterSeconds = 5
                },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var jobId = Guid.NewGuid();
        var handoffJson = JsonSerializer.Serialize(request.Handoff, JsonOptions);
        const string insertSql = """
            INSERT INTO advanced_analysis_jobs(
              job_id, tenant_id, user_id, session_id, handoff_id,
              schema_version, status, revision, attempt_count, handoff,
              requested_by_api_key_id, available_at, created_at, updated_at,
              expires_at)
            VALUES(
              @job_id, @tenant, @user_id, @session, @handoff_id,
              @schema_version, 'queued', 1, 0, CAST(@handoff AS jsonb),
              @api_key_id, now(), now(), now(), now() + (@retention_days * interval '1 day'))
            ON CONFLICT (tenant_id, user_id, handoff_id) DO NOTHING
            RETURNING
              job_id AS "JobId", handoff_id AS "HandoffId",
              session_id AS "SessionId", status AS "Status",
              revision AS "Revision", attempt_count AS "AttemptCount",
              cancel_requested_at AS "CancelRequestedAt",
              created_at AS "CreatedAt", updated_at AS "UpdatedAt",
              expires_at AS "ExpiresAt", started_at AS "StartedAt",
              finished_at AS "FinishedAt", provider_key AS "ProviderKey",
              result::text AS "ResultJson",
              last_error_code AS "LastErrorCode";
            """;
        var inserted = await connection.QuerySingleOrDefaultAsync<JobRow>(
            new CommandDefinition(
                insertSql,
                new
                {
                    job_id = jobId,
                    tenant = tenantId,
                    user_id = userId,
                    session = request.SessionId,
                    handoff_id = request.Handoff.HandoffId,
                    schema_version = request.Handoff.SchemaVersion,
                    handoff = handoffJson,
                    api_key_id = actorApiKeyId,
                    retention_days = retentionDays
                },
                cancellationToken: cancellationToken));

        var stored = inserted ?? await ReadByHandoffAsync(
            connection,
            tenantId,
            userId,
            request.Handoff.HandoffId,
            cancellationToken).ConfigureAwait(false);
        if (stored is null)
            throw new InvalidOperationException("advanced_analysis_job_insert_failed");

        if (inserted is not null)
        {
            await AuditWriter.WriteAsync(
                connection,
                tenantId,
                actorApiKeyId,
                actorIsAdmin,
                action: "advanced_analysis.job.create",
                target: stored.JobId.ToString(),
                payload: new
                {
                    userId,
                    request.SessionId,
                    request.Handoff.HandoffId,
                    request.Handoff.ReasonCode,
                    request.Handoff.TransferStage
                },
                ip: context.Connection.RemoteIpAddress?.ToString(),
                ct: cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Advanced analysis job {JobId} queued for tenant {TenantId} and user {UserId}",
                stored.JobId,
                tenantId,
                userId);
        }

        var dto = ToDto(stored);
        return inserted is null
            ? Results.Ok(dto)
            : Results.Accepted(
                $"/advanced-analysis/jobs/{dto.JobId}?userId={Uri.EscapeDataString(userId)}",
                dto);
    }

    internal static async Task<IResult> GetAsync(
        HttpContext context,
        NpgsqlDataSource dataSource,
        Guid jobId,
        string? userId)
    {
        var normalizedUserId = NormalizeUserId(userId);
        if (normalizedUserId is null)
        {
            return Results.BadRequest(new
            {
                error = "user_id_required"
            });
        }

        await using var connection = await dataSource
            .OpenConnectionAsync(context.RequestAborted)
            .ConfigureAwait(false);
        var row = await ReadByJobIdAsync(
            connection,
            context.GetTenantId(),
            normalizedUserId,
            jobId,
            context.RequestAborted).ConfigureAwait(false);
        return row is null
            ? Results.NotFound(new { error = "advanced_analysis_job_not_found" })
            : Results.Ok(ToDto(row));
    }

    internal static async Task<IResult> CancelAsync(
        HttpContext context,
        NpgsqlDataSource dataSource,
        ILogger<LogTag> logger,
        Guid jobId,
        string? userId)
    {
        var normalizedUserId = NormalizeUserId(userId);
        if (normalizedUserId is null)
            return Results.BadRequest(new { error = "user_id_required" });

        var tenantId = context.GetTenantId();
        var cancellationToken = context.RequestAborted;
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            UPDATE advanced_analysis_jobs
            SET status = CASE
                    WHEN status IN ('succeeded', 'failed', 'canceled') THEN status
                    ELSE 'canceled'
                END,
                cancel_requested_at = CASE
                    WHEN status IN ('succeeded', 'failed') THEN cancel_requested_at
                    ELSE COALESCE(cancel_requested_at, now())
                END,
                finished_at = CASE
                    WHEN status IN ('queued', 'running') THEN COALESCE(finished_at, now())
                    ELSE finished_at
                END,
                revision = CASE
                    WHEN status IN ('queued', 'running') THEN revision + 1
                    ELSE revision
                END,
                updated_at = CASE
                    WHEN status IN ('queued', 'running') THEN now()
                    ELSE updated_at
                END,
                lease_owner = CASE
                    WHEN status IN ('queued', 'running') THEN NULL
                    ELSE lease_owner
                END,
                lease_expires_at = CASE
                    WHEN status IN ('queued', 'running') THEN NULL
                    ELSE lease_expires_at
                END
            WHERE tenant_id=@tenant
              AND user_id=@user_id
              AND job_id=@job_id
            RETURNING
              job_id AS "JobId", handoff_id AS "HandoffId",
              session_id AS "SessionId", status AS "Status",
              revision AS "Revision", attempt_count AS "AttemptCount",
              cancel_requested_at AS "CancelRequestedAt",
              created_at AS "CreatedAt", updated_at AS "UpdatedAt",
              expires_at AS "ExpiresAt", started_at AS "StartedAt",
              finished_at AS "FinishedAt", provider_key AS "ProviderKey",
              result::text AS "ResultJson",
              last_error_code AS "LastErrorCode";
            """;
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(
            new CommandDefinition(
                sql,
                new
                {
                    tenant = tenantId,
                    user_id = normalizedUserId,
                    job_id = jobId
                },
                cancellationToken: cancellationToken));
        if (row is null)
            return Results.NotFound(new { error = "advanced_analysis_job_not_found" });

        await AuditWriter.WriteAsync(
            connection,
            tenantId,
            context.GetApiKeyIdOrNull(),
            context.IsAdmin(),
            action: "advanced_analysis.job.cancel",
            target: jobId.ToString(),
            payload: new { userId = normalizedUserId, status = row.Status },
            ip: context.Connection.RemoteIpAddress?.ToString(),
            ct: cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Advanced analysis job {JobId} cancel resolved as {Status}",
            jobId,
            row.Status);
        return Results.Ok(ToDto(row));
    }

    internal static AdvancedAnalysisValidationResult ValidateCreateRequest(
        AdvancedAnalysisJobCreateRequest? request)
    {
        if (request is null)
            return AdvancedAnalysisValidationResult.Invalid("request_required");
        if (NormalizeUserId(request.UserId) is null)
            return AdvancedAnalysisValidationResult.Invalid("user_id_invalid");
        if (request.SessionId == Guid.Empty)
            return AdvancedAnalysisValidationResult.Invalid("session_id_invalid");

        var handoff = request.Handoff;
        if (handoff is null)
            return AdvancedAnalysisValidationResult.Invalid("handoff_required");
        if (!string.Equals(
                handoff.SchemaVersion,
                AdvancedAnalysisHandoffEnvelope.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "handoff_schema_unsupported");
        }
        if (handoff.HandoffId == Guid.Empty)
            return AdvancedAnalysisValidationResult.Invalid("handoff_id_invalid");
        if (string.IsNullOrWhiteSpace(handoff.RequestText)
            || handoff.RequestText.Length > MaximumRequestCharacters)
        {
            return AdvancedAnalysisValidationResult.Invalid("request_text_invalid");
        }
        if (string.IsNullOrWhiteSpace(handoff.ReasonCode)
            || string.IsNullOrWhiteSpace(handoff.TransferStage))
        {
            return AdvancedAnalysisValidationResult.Invalid("handoff_route_invalid");
        }
        if (handoff.Load is null
            || handoff.ResearchState is null
            || handoff.DataPolicy is null)
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "handoff_section_required");
        }
        if (handoff.Load.AnswerUnitCount is < 0 or > MaximumAnswerUnits
            || handoff.Load.RowCount is < 0 or > MaximumAnswerUnits
            || handoff.Load.ColumnCount is < 0 or > MaximumAnswerUnits
            || handoff.Load.AtomicEvidenceCount is < 0 or > MaximumAnswerUnits)
        {
            return AdvancedAnalysisValidationResult.Invalid("handoff_load_invalid");
        }
        if (handoff.ResearchState.MemoryIsEvidence
            || !handoff.ResearchState.EvidenceRevalidationRequired)
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "handoff_evidence_policy_invalid");
        }
        if (handoff.DataPolicy.ExternalProviderContentAuthorized
            || handoff.DataPolicy.ExternalProviderMetadataAuthorized)
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "external_provider_authorization_must_be_server_controlled");
        }
        if (!HasBoundedResearchCollections(handoff))
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "handoff_collection_limit_exceeded");
        }
        if (handoff.ResearchState.EvidenceReferences.Count
            > MaximumEvidenceReferences)
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "too_many_evidence_references");
        }
        if (handoff.ResearchState.EvidenceReferences.Any(
                static evidence => !HasCanonicalIdentity(evidence)))
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "evidence_reference_identity_required");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(handoff, JsonOptions).Length
            > MaximumHandoffBytes)
        {
            return AdvancedAnalysisValidationResult.Invalid(
                "handoff_payload_too_large");
        }

        return AdvancedAnalysisValidationResult.Valid;
    }

    private static bool HasBoundedResearchCollections(
        AdvancedAnalysisHandoffEnvelope handoff)
    {
        var research = handoff.ResearchState;
        var load = handoff.Load;
        if (research.ExecutedTools is null
            || research.ExecutedQueries is null
            || research.Attempts is null
            || research.EvidenceReferences is null
            || load.CandidateScopePaths is null
            || load.RowLabels is null
            || load.Columns is null)
        {
            return false;
        }

        return research.ExecutedTools.Count <= MaximumResearchItems
               && research.ExecutedQueries.Count <= MaximumResearchItems
               && research.Attempts.Count <= MaximumResearchItems
               && load.CandidateScopePaths.Count <= MaximumResearchItems
               && load.RowLabels.Count <= MaximumResearchItems
               && load.Columns.Count <= MaximumResearchItems
               && research.Attempts.All(static attempt =>
                   attempt is not null
                   && attempt.Queries is not null
                   && attempt.Queries.Count <= MaximumResearchItems);
    }

    private static bool HasCanonicalIdentity(AdvancedAnalysisEvidenceReference evidence)
        => evidence is not null
           && (!string.IsNullOrWhiteSpace(evidence.DocId)
               || !string.IsNullOrWhiteSpace(evidence.RevisionId)
               || !string.IsNullOrWhiteSpace(evidence.ChunkId)
               || !string.IsNullOrWhiteSpace(evidence.AnchorId)
               || !string.IsNullOrWhiteSpace(evidence.ContentCardId)
               || !string.IsNullOrWhiteSpace(evidence.SourceHash));

    private static string? NormalizeUserId(string? userId)
    {
        var normalized = userId?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200
            ? null
            : normalized;
    }

    private static Task<JobRow?> ReadByHandoffAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string userId,
        Guid handoffId,
        CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            SelectProjection + """

            WHERE tenant_id=@tenant
              AND user_id=@user_id
              AND handoff_id=@handoff_id
            LIMIT 1;
            """,
            new
            {
                tenant = tenantId,
                user_id = userId,
                handoff_id = handoffId
            },
            cancellationToken: cancellationToken));

    private static Task<JobRow?> ReadByJobIdAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string userId,
        Guid jobId,
        CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            SelectProjection + """

            WHERE tenant_id=@tenant
              AND user_id=@user_id
              AND job_id=@job_id
            LIMIT 1;
            """,
            new { tenant = tenantId, user_id = userId, job_id = jobId },
            cancellationToken: cancellationToken));

    private const string SelectProjection = """
        SELECT
          job_id AS "JobId", handoff_id AS "HandoffId",
          session_id AS "SessionId", status AS "Status",
          revision AS "Revision", attempt_count AS "AttemptCount",
          cancel_requested_at AS "CancelRequestedAt",
          created_at AS "CreatedAt", updated_at AS "UpdatedAt",
          expires_at AS "ExpiresAt", started_at AS "StartedAt",
          finished_at AS "FinishedAt", provider_key AS "ProviderKey",
          result::text AS "ResultJson",
          last_error_code AS "LastErrorCode"
        FROM advanced_analysis_jobs
        """;

    private static AdvancedAnalysisJobDto ToDto(JobRow row)
        => new()
        {
            JobId = row.JobId,
            HandoffId = row.HandoffId,
            SessionId = row.SessionId,
            Status = row.Status,
            Revision = row.Revision,
            AttemptCount = row.AttemptCount,
            CancelRequested = row.CancelRequestedAt.HasValue,
            CreatedAtUtc = AsUtc(row.CreatedAt),
            UpdatedAtUtc = AsUtc(row.UpdatedAt),
            ExpiresAtUtc = AsUtc(row.ExpiresAt),
            StartedAtUtc = AsUtcNullable(row.StartedAt),
            FinishedAtUtc = AsUtcNullable(row.FinishedAt),
            ProviderKey = row.ProviderKey,
            Result = ParseOptionalJson(row.ResultJson),
            LastErrorCode = row.LastErrorCode
        };

    private static JsonElement? ParseOptionalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? AsUtcNullable(DateTime? value)
        => value.HasValue ? AsUtc(value.Value) : null;

    private sealed class JobRow
    {
        public Guid JobId { get; init; }
        public Guid HandoffId { get; init; }
        public Guid SessionId { get; init; }
        public string Status { get; init; } = string.Empty;
        public int Revision { get; init; }
        public int AttemptCount { get; init; }
        public DateTime? CancelRequestedAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public string? ProviderKey { get; init; }
        public string? ResultJson { get; init; }
        public string? LastErrorCode { get; init; }
    }

    internal sealed record AdvancedAnalysisValidationResult(
        bool IsValid,
        string? ErrorCode,
        string? Detail)
    {
        public static AdvancedAnalysisValidationResult Valid { get; } =
            new(true, null, null);

        public static AdvancedAnalysisValidationResult Invalid(
            string errorCode,
            string? detail = null)
            => new(false, errorCode, detail);
    }
}
