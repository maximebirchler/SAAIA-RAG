using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAEnrichmentCommandService
{
    private const string CdcAlignment = "v3.0";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>> EnqueueCapabilityAEnrichmentAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        AdminRuntimeCapabilityAEnqueueRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_enqueue");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await RuntimeCapabilityGateService.EnsureCapabilityReadyAsync(conn, CapabilityACorpusEnrichmentKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, gate.Error);
            }

            if (string.IsNullOrWhiteSpace(ingest.DocumentsRoot))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: "documents_root_not_configured");
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, "documents_root_not_configured");
            }

            if (!Directory.Exists(ingest.DocumentsRoot))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: "documents_root_not_found");
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, "documents_root_not_found");
            }

            var candidates = await RuntimeCapabilityAEnrichmentStore.LoadCandidatesAsync(
                conn,
                tenantId,
                ingest,
                hypotheticalQuestionService,
                req?.Category,
                req?.MaxCandidates ?? 50,
                req?.ReasonFilters,
                ct);
            var response = await RuntimeCapabilityAEnrichmentCoordinator.EnqueueAsync(
                conn,
                tenantId,
                CdcAlignment,
                CapabilityACorpusEnrichmentKey,
                env.EnvironmentName,
                gate.State!.ProfileKey,
                ingest,
                req,
                candidates,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_enqueue",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: response.CandidateCount,
                plannedCount: response.PlannedCount,
                queuedCount: response.QueuedCount,
                skippedCount: response.SkippedCount,
                dryRun: req?.DryRun == true);

            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(
                response,
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_enqueue",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                plannedCount: 0,
                queuedCount: 0,
                skippedCount: 0,
                dryRun: req?.DryRun == true,
                errorReason: ex.Message);
            throw;
        }
    }
}
