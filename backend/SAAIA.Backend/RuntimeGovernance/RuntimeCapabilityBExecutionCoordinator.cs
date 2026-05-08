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
            ["executionHeartbeatAt"] = claimedAt,
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

    private static string ResolveStoredDocLanguage(string? requestedLanguage, string? profileLanguage)
    {
        var requested = NormalizeDocLanguage(requestedLanguage);
        return string.Equals(requested, "und", StringComparison.Ordinal)
            ? NormalizeDocLanguage(profileLanguage)
            : requested;
    }

    private static string ResolveStoredDocLanguageSource(string? requestedLanguage, string? profileLanguage)
        => !string.Equals(NormalizeDocLanguage(requestedLanguage), "und", StringComparison.Ordinal)
            ? "request"
            : !string.Equals(NormalizeDocLanguage(profileLanguage), "und", StringComparison.Ordinal)
                ? "profile"
                : "unknown";

    private static string NormalizeDocLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "und";

        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        return IsPlausibleLanguageTag(normalized) ? normalized : "und";
    }

    private static bool IsPlausibleLanguageTag(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "und", StringComparison.Ordinal))
            return string.Equals(value, "und", StringComparison.Ordinal);
        if (value.Length is < 2 or > 35)
            return false;

        var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return false;
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return false;

        return parts.Skip(1).All(static part =>
            part.Length is >= 2 and <= 8
            && part.All(static ch => char.IsLetterOrDigit(ch)));
    }

    internal static async Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        string adminRuntimeActor,
        string capabilityKey,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        CancellationToken ct)
        => await CompleteAsync(
            tenantId,
            ds,
            adminRuntimeActor,
            capabilityKey,
            req,
            generatedProfileRevisionId: null,
            generatedProfile: null,
            ct);

    internal static async Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        string adminRuntimeActor,
        string capabilityKey,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        Guid? generatedProfileRevisionId,
        ProjectedDocumentProfile? generatedProfile,
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
        if (generatedProfile is not null && !generatedProfileRevisionId.HasValue)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "generated_profile_revision_required");

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

        var expectedSourceHash = string.IsNullOrWhiteSpace(req?.SourceHash)
            ? null
            : req.SourceHash.Trim();
        var sourceHash = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(conn, tenantId, execution.DocId, ct);
        if (string.IsNullOrWhiteSpace(sourceHash))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_unavailable");
        sourceHash = sourceHash.Trim();
        if (!string.IsNullOrWhiteSpace(expectedSourceHash)
            && !string.Equals(expectedSourceHash, sourceHash, StringComparison.Ordinal))
        {
            await CancelStaleSourceJobAsync(tenantId, conn, capabilityKey, execution, "source_hash_mismatch", ct);
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_mismatch");
        }

        var normalizedSummaryText = PostgresTextSanitizer.Clean(req!.SummaryText).Trim();
        if (string.IsNullOrWhiteSpace(normalizedSummaryText))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "summary_text_required");
        var metaJson = PostgresTextSanitizer.CleanJson(req.Meta);
        var effectiveDocLanguage = ResolveStoredDocLanguage(req.DocLanguage, doc.ProfileLanguage);
        var effectiveDocLanguageSource = ResolveStoredDocLanguageSource(req.DocLanguage, doc.ProfileLanguage);

        var summaryLength = normalizedSummaryText.Length;
        var finishedAt = DateTimeOffset.UtcNow;
        var resultValues = new Dictionary<string, object?>
        {
            ["docId"] = execution.DocId,
            ["docPath"] = doc.DocPath,
            ["level"] = level,
            ["docLanguage"] = effectiveDocLanguage,
            ["docLanguageSource"] = effectiveDocLanguageSource,
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
            ["executionLeaseToken"] = execution.LeaseToken,
            ["generatedProfileStored"] = generatedProfile is not null,
            ["generatedProfileRevisionId"] = generatedProfileRevisionId,
            ["generatedProfileVersion"] = generatedProfile?.ProfileVersion
        };
        AddSummaryMetaDiagnosticsToJobResult(resultValues, req.Meta);
        var resultJson = JsonSerializer.Serialize(resultValues);

        await using var tx = await conn.BeginTransactionAsync(ct);
        var lockedSourceHash = await RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashForUpdateAsync(
            conn,
            tenantId,
            execution.DocId,
            ct,
            tx);
        if (string.IsNullOrWhiteSpace(lockedSourceHash))
        {
            await tx.RollbackAsync(ct);
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_unavailable");
        }

        lockedSourceHash = lockedSourceHash.Trim();
        if (!string.Equals(sourceHash, lockedSourceHash, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            await CancelStaleSourceJobAsync(tenantId, conn, capabilityKey, execution, "source_hash_changed_during_completion", ct);
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_changed_during_completion");
        }

        if (generatedProfile is not null && generatedProfileRevisionId.HasValue)
        {
            var revisionMatches = await RuntimeCapabilityBExecutionStore.RevisionMatchesCurrentDocumentSnapshotAsync(
                conn,
                tenantId,
                execution.DocId,
                generatedProfileRevisionId.Value,
                ct,
                tx);
            if (!revisionMatches)
            {
                await tx.RollbackAsync(ct);
                return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "generated_profile_revision_stale");
            }

            await DocumentFoundationRepo.UpsertDocumentProfileAsync(
                conn,
                tx,
                tenantId,
                execution.DocId,
                generatedProfileRevisionId.Value,
                generatedProfile,
                ct);
        }

        await RuntimeCapabilityBExecutionStore.UpsertDocumentSummaryAsync(
            conn,
            tenantId,
            execution.DocId,
            level,
            effectiveDocLanguage,
            sourceHash,
            normalizedSummaryText,
            metaJson,
            ct,
            tx);

        var completed = await RuntimeCapabilityBExecutionStore.TryCompleteJobAsync(
            conn,
            tenantId,
            execution.JobId,
            execution.LeaseToken,
            finishedAt,
            resultJson,
            ct,
            tx);

        if (!completed)
        {
            await tx.RollbackAsync(ct);
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "capability_b_job_not_running");
        }

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
            ct,
            tx);

        await tx.CommitAsync(ct);

        return new RuntimeOperationResult<CapabilityBCompletionResult>(
            new CapabilityBCompletionResult(
                execution.JobId,
                execution.DocId,
                doc.DocPath,
                level,
                effectiveDocLanguage,
                effectiveDocLanguageSource,
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
            execution.LeaseToken,
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

    private static void AddSummaryMetaDiagnosticsToJobResult(
        IDictionary<string, object?> resultValues,
        JsonElement? meta)
    {
        if (meta is null || meta.Value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var key in new[]
        {
            "fallbackUsed",
            "fallbackReason",
            "llmError",
            "llmFailureKind",
            "llmFailureCategory",
            "llmStatusCode",
            "llmDurationMs",
            "llmResponseHeadersMs",
            "llmFirstResponseMs",
            "llmBytesRead",
            "llmModel"
        })
        {
            if (!meta.Value.TryGetProperty(key, out var value))
                continue;

            if (TryReadSummaryMetaScalar(value, out var scalar))
                resultValues[key] = scalar;
        }
    }

    private static bool TryReadSummaryMetaScalar(JsonElement value, out object? scalar)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                scalar = PostgresTextSanitizer.Clean(value.GetString());
                return true;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var longValue))
                {
                    scalar = longValue;
                    return true;
                }

                if (value.TryGetDouble(out var doubleValue))
                {
                    scalar = doubleValue;
                    return true;
                }

                break;
            case JsonValueKind.True:
                scalar = true;
                return true;
            case JsonValueKind.False:
                scalar = false;
                return true;
            case JsonValueKind.Null:
                scalar = null;
                return true;
        }

        scalar = null;
        return false;
    }

    private static async Task CancelStaleSourceJobAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        string capabilityKey,
        CapabilityBExecutionContext execution,
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
            ["runtimeCapabilitySelected"] = execution.RuntimeCapabilitySelected,
            ["runtimeProfileKey"] = execution.RuntimeProfileKey,
            ["source"] = execution.EnqueueSource,
            ["campaignId"] = execution.CampaignId,
            ["executionLeaseToken"] = execution.LeaseToken
        });

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
                capabilityKey: capabilityKey,
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
}
