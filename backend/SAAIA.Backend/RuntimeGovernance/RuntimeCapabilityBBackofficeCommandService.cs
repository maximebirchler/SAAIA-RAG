using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBBackofficeCommandService
{
    private const string CdcAlignment = "v3.1";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>> EnqueueCapabilityBBackofficeAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeCapabilityBEnqueueRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_enqueue");
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
                    "capability_b_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>(null, gate.Error);
            }

            var selectedDocIds = (req?.DocIds ?? Array.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToHashSet();
            var selectedDocPaths = (req?.DocPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasExplicitSelection = selectedDocIds.Count > 0 || selectedDocPaths.Count > 0;
            var includeFreshSelectedDocuments = req?.Force == true
                && hasExplicitSelection;
            var requestedMaxCandidates = Math.Clamp(req?.MaxCandidates ?? 200, 1, 500);

            var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
                conn,
                tenantId,
                req?.Category,
                hasExplicitSelection ? null : requestedMaxCandidates,
                options,
                includeFreshSelectedDocuments,
                ct);

            if (hasExplicitSelection)
            {
                candidates = candidates
                    .Where(candidate => selectedDocIds.Contains(candidate.DocId) || selectedDocPaths.Contains(candidate.DocPath))
                    .Take(requestedMaxCandidates)
                    .ToArray();
            }

            var items = new List<AdminRuntimeCapabilityBEnqueueItemDto>(candidates.Length);
            var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var campaignId = Guid.NewGuid();
            var plannedCount = 0;

            foreach (var candidate in candidates)
            {
                if (candidate.HasActiveJob)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "active_summary_job_exists"));
                    IncrementReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementReasonCounts(reasonCounts, ["blocked:active_summary_job_exists"]);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.PolicyBlockReason) && req?.Force != true)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: candidate.PolicyBlockReason));
                    IncrementReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementReasonCounts(reasonCounts, [$"blocked:{candidate.PolicyBlockReason}"]);
                    continue;
                }

                plannedCount++;
                IncrementReasonCounts(reasonCounts, candidate.Reasons);

                if (req?.DryRun == true)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "dry_run_preview"));
                    continue;
                }

                var jobId = await InsertCapabilityBAdminJobAsync(conn, tenantId, candidate.DocId, candidate.DocPath, req?.Force == true, campaignId, gate.State!.ProfileKey, ct);
                items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: true, JobId: jobId));

                await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                    conn,
                    RuntimeGovernanceService.CreateCapabilityEvent(
                        capabilityKey: CapabilityBBackofficeGenerationKey,
                        profileKey: gate.State!.ProfileKey,
                        eventType: "capability_b_enqueued",
                        reason: string.Join(",", candidate.Reasons),
                        details: new Dictionary<string, object?>
                        {
                            ["docId"] = candidate.DocId,
                            ["docPath"] = candidate.DocPath,
                            ["category"] = candidate.Category,
                            ["summaryState"] = candidate.SummaryState,
                            ["jobId"] = jobId,
                            ["reasons"] = candidate.Reasons.ToArray(),
                            ["policyBlocked"] = candidate.PolicyBlocked,
                            ["policyBlockReason"] = candidate.PolicyBlockReason,
                            ["campaignId"] = campaignId
                        }),
                    ct);
            }

            var queuedCount = items.Count(item => item.Queued);
            var skippedCount = items.Count(item => !item.Queued);

            await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                conn,
                RuntimeGovernanceService.CreateCapabilityEvent(
                    capabilityKey: CapabilityBBackofficeGenerationKey,
                    profileKey: gate.State!.ProfileKey,
                    eventType: req?.DryRun == true ? "capability_b_campaign_dry_run" : "capability_b_campaign_executed",
                    reason: req?.DryRun == true ? "dry_run_preview" : "campaign_completed",
                    details: new Dictionary<string, object?>
                    {
                        ["campaignId"] = campaignId,
                        ["candidateCount"] = candidates.Length,
                        ["plannedCount"] = plannedCount,
                        ["queuedCount"] = queuedCount,
                        ["skippedCount"] = skippedCount,
                        ["dryRun"] = req?.DryRun == true,
                        ["force"] = req?.Force == true,
                        ["reasonCounts"] = reasonCounts,
                        ["items"] = items.Select(static item => new Dictionary<string, object?>
                        {
                            ["docId"] = item.DocId,
                            ["docPath"] = item.DocPath,
                            ["queued"] = item.Queued,
                            ["jobId"] = item.JobId,
                            ["reason"] = item.Reason
                        }).ToArray(),
                        ["docIds"] = selectedDocIds.ToArray(),
                        ["docPaths"] = selectedDocPaths.ToArray()
                    }),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_enqueue",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length,
                plannedCount: plannedCount,
                queuedCount: queuedCount,
                skippedCount: skippedCount,
                dryRun: req?.DryRun == true);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>(
                new AdminRuntimeCapabilityBEnqueueResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    campaignId,
                    req?.DryRun == true,
                    req?.Force == true,
                    candidates.Length,
                    plannedCount,
                    queuedCount,
                    skippedCount,
                    reasonCounts,
                    items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_enqueue",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                queuedCount: 0,
                skippedCount: 0,
                dryRun: req?.DryRun == true,
                errorReason: ex.Message);
            throw;
        }
    }

    private static Task<Guid> InsertCapabilityBAdminJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        string docPath,
        bool force,
        Guid campaignId,
        string? profileKey,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.InsertCapabilityBAdminJobAsync(conn, tenantId, docId, docPath, force, campaignId, profileKey, ct);

    private static void IncrementReasonCounts(
        IDictionary<string, int> counts,
        IEnumerable<string> reasons)
    {
        foreach (var reason in reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)))
        {
            counts[reason] = counts.TryGetValue(reason, out var current)
                ? current + 1
                : 1;
        }
    }
}
