using System.Text.Json;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBExecutionCoordinator
{
    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>> ClaimAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        string cdcAlignment,
        string environmentName,
        string capabilityKey,
        string adminRuntimeActor,
        string profileKey,
        AdminRuntimeCapabilityBClaimRequestDto? req,
        CancellationToken ct)
    {
        CapabilityBExecutionJobRow? job;
        if (req?.JobId is Guid requestedJobId && requestedJobId != Guid.Empty)
        {
            job = await RuntimeCapabilityBExecutionStore.LoadCapabilityBExecutionJobAsync(conn, tenantId, requestedJobId, ct);
            if (job is null)
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_found");
            if (!string.Equals(job.Status, "queued", StringComparison.Ordinal))
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_claimable");
        }
        else
        {
            job = await RuntimeCapabilityBExecutionStore.LoadNextQueuedCapabilityBExecutionJobAsync(conn, tenantId, ct);
            if (job is null)
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_no_queued_jobs");
        }

        if (!job.DocId.HasValue || job.DocId.Value == Guid.Empty || string.IsNullOrWhiteSpace(job.DocPath))
            return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_missing_document_reference");

        var claimedAt = DateTimeOffset.UtcNow;
        var claimedBy = string.IsNullOrWhiteSpace(req?.ExecutorId)
            ? adminRuntimeActor
            : req!.ExecutorId!.Trim();
        var leaseToken = Guid.NewGuid().ToString("N");
        var payload = new Dictionary<string, object?>(
            RuntimeGovernanceJson.ParseDetails(job.PayloadJson) ?? new Dictionary<string, object?>(),
            StringComparer.Ordinal)
        {
            ["executionLeaseToken"] = leaseToken,
            ["executionClaimedAt"] = claimedAt,
            ["executionClaimedBy"] = claimedBy,
            ["executionClaimCapabilityStatus"] = job.RuntimeCapabilityStatus ?? "selected",
            ["executionClaimProfileKey"] = profileKey
        };

        var claimed = await RuntimeCapabilityBExecutionStore.TryClaimJobAsync(
            conn,
            tenantId,
            job.JobId,
            claimedAt,
            JsonSerializer.Serialize(payload),
            ct);

        if (!claimed)
            return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_claimable");

        await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
            conn,
            RuntimeGovernanceService.CreateCapabilityEvent(
                capabilityKey: capabilityKey,
                profileKey: profileKey,
                eventType: "capability_b_job_claimed",
                reason: "execution_claimed",
                details: new Dictionary<string, object?>
                {
                    ["jobId"] = job.JobId,
                    ["docId"] = job.DocId,
                    ["docPath"] = job.DocPath,
                    ["level"] = job.Level,
                    ["campaignId"] = job.CampaignId,
                    ["claimedBy"] = claimedBy,
                    ["claimedAt"] = claimedAt,
                    ["leaseToken"] = leaseToken,
                    ["runtimeCapabilityStatus"] = job.RuntimeCapabilityStatus ?? "selected",
                    ["runtimeProfileKey"] = profileKey
                }),
            ct);

        return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(
            new AdminRuntimeCapabilityBClaimResponseDto(
                cdcAlignment,
                environmentName,
                capabilityKey,
                job.JobId,
                job.DocId.Value,
                job.DocPath!,
                job.Level ?? "medium",
                job.ExecutionMode ?? "server_backoffice",
                job.RuntimeCapabilityKey ?? capabilityKey,
                job.RuntimeCapabilityStatus ?? "selected",
                profileKey,
                job.CampaignId,
                leaseToken,
                claimedBy,
                claimedAt),
            null);
    }

    internal static async Task<RuntimeOperationResult<CapabilityBExecutionContext>> ValidateLeaseAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        string capabilityKey,
        string adminRuntimeActor,
        Guid jobId,
        string? leaseToken,
        CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "job_id_required");
        if (string.IsNullOrWhiteSpace(leaseToken))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "execution_lease_token_required");

        var job = await RuntimeCapabilityBExecutionStore.LoadCapabilityBExecutionJobAsync(conn, tenantId, jobId, ct);
        if (job is null)
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_not_found");
        if (!job.DocId.HasValue || job.DocId.Value == Guid.Empty || string.IsNullOrWhiteSpace(job.DocPath))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_missing_document_reference");
        if (!string.Equals(job.Status, "running", StringComparison.Ordinal))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_not_running");
        if (string.IsNullOrWhiteSpace(job.ExecutionLeaseToken))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_execution_not_claimed");
        if (!string.Equals(job.ExecutionLeaseToken, leaseToken.Trim(), StringComparison.Ordinal))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_invalid_execution_lease");

        return new RuntimeOperationResult<CapabilityBExecutionContext>(
            new CapabilityBExecutionContext(
                job.JobId,
                job.DocId.Value,
                job.DocPath!,
                job.Level ?? "medium",
                job.Status,
                job.ExecutionMode ?? "server_backoffice",
                job.RuntimeCapabilityKey ?? capabilityKey,
                job.RuntimeCapabilityStatus,
                job.RuntimeCapabilitySelected,
                job.RuntimeProfileKey,
                job.CampaignId,
                job.EnqueueSource,
                job.ExecutionLeaseToken!,
                job.ExecutionClaimedBy ?? adminRuntimeActor,
                ToUtcOffset(job.ExecutionClaimedAt)),
            null);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }

    private static string NormalizeDocLanguage(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "und"
            : value.Trim();

    internal static async Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        string adminRuntimeActor,
        string capabilityKey,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var validation = await ValidateLeaseAsync(
            tenantId,
            conn,
            capabilityKey,
            adminRuntimeActor,
            req?.JobId ?? Guid.Empty,
            req?.LeaseToken,
            ct);
        if (validation.Error is not null)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, validation.Error);
        if (string.IsNullOrWhiteSpace(req?.SummaryText))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "summary_text_required");

        var execution = validation.Payload!;
        var level = string.IsNullOrWhiteSpace(execution.Level)
            ? "medium"
            : execution.Level.Trim();
        var completedBy = string.IsNullOrWhiteSpace(execution.ClaimedBy)
            ? adminRuntimeActor
            : execution.ClaimedBy.Trim();

        var doc = await RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(conn, tenantId, execution.DocId, ct);
        if (doc is null)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "document_not_found");

        var sourceHash = string.IsNullOrWhiteSpace(req?.SourceHash)
            ? await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(conn, tenantId, execution.DocId, ct)
            : req.SourceHash.Trim();
        if (string.IsNullOrWhiteSpace(sourceHash))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_unavailable");

        var normalizedSummaryText = req!.SummaryText.Trim();
        var metaJson = req.Meta.HasValue
            ? req.Meta.Value.GetRawText()
            : null;

        await RuntimeCapabilityBExecutionStore.UpsertDocumentSummaryAsync(
            conn,
            tenantId,
            execution.DocId,
            level,
            NormalizeDocLanguage(req.DocLanguage),
            sourceHash,
            normalizedSummaryText,
            metaJson,
            ct);

        var summaryLength = normalizedSummaryText.Length;
        var finishedAt = DateTimeOffset.UtcNow;
        var resultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = execution.DocId,
            ["docPath"] = doc.DocPath,
            ["level"] = level,
            ["stored"] = true,
            ["sourceHash"] = sourceHash,
            ["summaryLength"] = summaryLength,
            ["completedBy"] = completedBy,
            ["executionMode"] = execution.ExecutionMode,
            ["runtimeCapabilityKey"] = execution.RuntimeCapabilityKey,
            ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
            ["runtimeCapabilitySelected"] = execution.RuntimeCapabilitySelected,
            ["runtimeProfileKey"] = execution.RuntimeProfileKey,
            ["source"] = execution.EnqueueSource,
            ["campaignId"] = execution.CampaignId,
            ["executionLeaseToken"] = execution.LeaseToken
        });

        var completed = await RuntimeCapabilityBExecutionStore.TryCompleteJobAsync(
            conn,
            tenantId,
            execution.JobId,
            finishedAt,
            resultJson,
            ct);

        if (!completed)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "capability_b_job_not_running");

        await RuntimeCapabilityBExecutionStore.RecordCapabilityBSummaryCompletedAsync(
            conn,
            execution.JobId,
            execution.DocId,
            doc.DocPath,
            level,
            sourceHash,
            summaryLength,
            execution.RuntimeProfileKey,
            execution.CampaignId,
            execution.RuntimeCapabilityStatus,
            ct);

        return new RuntimeOperationResult<CapabilityBCompletionResult>(
            new CapabilityBCompletionResult(
                execution.JobId,
                execution.DocId,
                doc.DocPath,
                level,
                sourceHash,
                summaryLength,
                completedBy,
                execution.CampaignId),
            null);
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>> FailAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        string cdcAlignment,
        string environmentName,
        string capabilityKey,
        string adminRuntimeActor,
        AdminRuntimeCapabilityBFailRequestDto? req,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var validation = await ValidateLeaseAsync(
            tenantId,
            conn,
            capabilityKey,
            adminRuntimeActor,
            req?.JobId ?? Guid.Empty,
            req?.LeaseToken,
            ct);
        if (validation.Error is not null)
            return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, validation.Error);
        if (string.IsNullOrWhiteSpace(req?.Error))
            return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, "summary_job_error_required");

        var execution = validation.Payload!;
        var failedAt = DateTimeOffset.UtcNow;
        var lastError = req!.Error.Trim();
        var resultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = execution.DocId,
            ["docPath"] = execution.DocPath,
            ["level"] = execution.Level,
            ["stored"] = false,
            ["failedBy"] = execution.ClaimedBy,
            ["executionMode"] = execution.ExecutionMode,
            ["runtimeCapabilityKey"] = execution.RuntimeCapabilityKey,
            ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
            ["runtimeCapabilitySelected"] = execution.RuntimeCapabilitySelected,
            ["runtimeProfileKey"] = execution.RuntimeProfileKey,
            ["source"] = execution.EnqueueSource,
            ["campaignId"] = execution.CampaignId,
            ["executionLeaseToken"] = execution.LeaseToken,
            ["error"] = lastError,
            ["details"] = req.Details.HasValue
                ? req.Details.Value.Clone()
                : null
        });

        var failed = await RuntimeCapabilityBExecutionStore.TryFailJobAsync(
            conn,
            tenantId,
            execution.JobId,
            failedAt,
            resultJson,
            lastError,
            ct);

        if (!failed)
            return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, "capability_b_job_not_running");

        await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
            conn,
            RuntimeGovernanceService.CreateCapabilityEvent(
                capabilityKey: capabilityKey,
                profileKey: execution.RuntimeProfileKey,
                eventType: "capability_b_job_failed",
                reason: lastError,
                details: new Dictionary<string, object?>
                {
                    ["jobId"] = execution.JobId,
                    ["docId"] = execution.DocId,
                    ["docPath"] = execution.DocPath,
                    ["level"] = execution.Level,
                    ["campaignId"] = execution.CampaignId,
                    ["failedBy"] = execution.ClaimedBy,
                    ["failedAt"] = failedAt,
                    ["leaseToken"] = execution.LeaseToken,
                    ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
                    ["error"] = lastError
                }),
            ct);

        return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(
            new AdminRuntimeCapabilityBFailResponseDto(
                cdcAlignment,
                environmentName,
                capabilityKey,
                execution.JobId,
                execution.DocId,
                execution.DocPath,
                execution.Level,
                execution.CampaignId,
                execution.LeaseToken,
                execution.ClaimedBy,
                lastError,
                "failed",
                failedAt),
            null);
    }
}
