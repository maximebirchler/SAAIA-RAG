using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBExecutionCommandService
{
    private const string CdcAlignment = "v3.1";
    private const string AdminRuntimeActor = "admin_runtime_endpoint";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>> ClaimCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeCapabilityBClaimRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_claim");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await RuntimeCapabilityGateService.EnsureCapabilityReadyAsync(conn, CapabilityBBackofficeGenerationKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_claim",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, gate.Error);
            }

            var result = await RuntimeCapabilityBExecutionCoordinator.ClaimAsync(
                tenantId,
                conn,
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                AdminRuntimeActor,
                gate.State!.ProfileKey,
                req,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_claim",
                success: result.Error is null,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: result.Error);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_claim",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static Task<RuntimeOperationResult<CapabilityBExecutionContext>> ValidateCapabilityBExecutionLeaseAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        Guid jobId,
        string? leaseToken,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionCoordinator.ValidateLeaseAsync(
            tenantId,
            conn,
            CapabilityBBackofficeGenerationKey,
            AdminRuntimeActor,
            jobId,
            leaseToken,
            ct);

    internal static Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionCoordinator.CompleteAsync(
            tenantId,
            ds,
            AdminRuntimeActor,
            CapabilityBBackofficeGenerationKey,
            req,
            ct);

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>> FailCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        AdminRuntimeCapabilityBFailRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_fail");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RuntimeCapabilityBExecutionCoordinator.FailAsync(
                tenantId,
                ds,
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                AdminRuntimeActor,
                req,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_fail",
                success: result.Error is null,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: result.Error);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_fail",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static Task RecordCapabilityBSummaryCompletedAsync(
        NpgsqlConnection conn,
        Guid jobId,
        Guid docId,
        string docPath,
        string level,
        string sourceHash,
        int summaryLength,
        string? profileKey,
        Guid? campaignId,
        string? runtimeCapabilityStatus,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.RecordCapabilityBSummaryCompletedAsync(
            conn,
            jobId,
            docId,
            docPath,
            level,
            sourceHash,
            summaryLength,
            profileKey,
            campaignId,
            runtimeCapabilityStatus,
            ct);
}
