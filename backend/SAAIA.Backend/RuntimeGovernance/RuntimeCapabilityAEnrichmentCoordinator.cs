using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAEnrichmentCoordinator
{
    internal static async Task<AdminRuntimeCapabilityAEnqueueResponseDto> EnqueueAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string cdcAlignment,
        string capabilityKey,
        string environmentName,
        string profileKey,
        IngestionOptions ingest,
        AdminRuntimeCapabilityAEnqueueRequestDto? req,
        AdminRuntimeCapabilityAEnrichmentCandidateDto[] candidates,
        CancellationToken ct)
    {
        var campaignId = Guid.NewGuid();

        var selectedDocIds = req?.DocIds?.Where(id => id != Guid.Empty).ToHashSet() ?? [];
        var selectedDocPaths = req?.DocPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathUtil.NormalizeRelativePath)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        if (selectedDocIds.Count > 0 || selectedDocPaths.Count > 0)
        {
            candidates = candidates
                .Where(candidate => selectedDocIds.Contains(candidate.DocId) || selectedDocPaths.Contains(candidate.DocPath))
                .ToArray();
        }

        var items = new List<AdminRuntimeCapabilityAEnqueueItemDto>(candidates.Length);
        var plannedCount = 0;
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var unsafeReasons = ResolveUnsafeReasons(candidate);
            if (req?.AllowUnsafeCandidates != true && unsafeReasons.Length > 0)
            {
                var policyReason = $"policy_blocked:{string.Join(",", unsafeReasons.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                items.Add(CreateEnqueueItem(candidate, queued: false, reason: policyReason));
                IncrementReasonCounts(reasonCounts, candidate.Reasons);
                IncrementReasonCounts(reasonCounts, unsafeReasons.Select(static reason => $"blocked:{reason}"));
                continue;
            }

            if (!candidate.FileExists)
            {
                items.Add(CreateEnqueueItem(candidate, queued: false, reason: "document_file_not_found"));
                IncrementReasonCounts(reasonCounts, candidate.Reasons);
                IncrementReasonCounts(reasonCounts, ["blocked:document_file_not_found"]);
                continue;
            }

            var activeJob = await LoadActiveIngestionJobAsync(conn, tenantId, candidate.DocPath, ct);
            if (activeJob.HasValue)
            {
                items.Add(CreateEnqueueItem(candidate, queued: false, jobId: activeJob.Value, reason: "active_job_exists"));
                IncrementReasonCounts(reasonCounts, candidate.Reasons);
                IncrementReasonCounts(reasonCounts, ["blocked:active_job_exists"]);
                continue;
            }

            plannedCount++;
            IncrementReasonCounts(reasonCounts, candidate.Reasons);

            if (req?.DryRun == true)
            {
                items.Add(CreateEnqueueItem(candidate, queued: false, reason: "dry_run_preview"));
                continue;
            }

            var absolutePath = DocPathNormalizer.ToAbsoluteFromRelative(candidate.DocPath, ingest.DocumentsRoot);
            if (!File.Exists(absolutePath))
            {
                items.Add(CreateEnqueueItem(candidate, queued: false, reason: "document_file_not_found"));
                IncrementReasonCounts(reasonCounts, ["blocked:document_file_not_found"]);
                continue;
            }

            var fileInfo = new FileInfo(absolutePath);
            var queued = await IngestionEnqueue.EnqueueUpsertAsync(
                conn,
                tenantId,
                candidate.DocPath,
                candidate.Category,
                fileInfo,
                ct,
                enqueueSource: "capability_a",
                capabilityAProfileSeed: BuildCapabilityAProfileSeed(candidate));

            items.Add(CreateEnqueueItem(candidate, queued: true, jobId: queued.JobId));

            await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                conn,
                RuntimeGovernanceService.CreateCapabilityEvent(
                    capabilityKey: capabilityKey,
                    profileKey: profileKey,
                    eventType: "capability_a_enqueued",
                    reason: string.Join(",", candidate.Reasons),
                    details: new Dictionary<string, object?>
                    {
                        ["docId"] = candidate.DocId,
                        ["docPath"] = candidate.DocPath,
                        ["category"] = candidate.Category,
                        ["jobId"] = queued.JobId,
                        ["previewText"] = candidate.PreviewText,
                        ["keySectionTitles"] = candidate.KeySectionTitles,
                        ["suggestedTags"] = candidate.SuggestedTags,
                        ["hypotheticalQuestions"] = candidate.HypotheticalQuestions,
                        ["qualityScore"] = candidate.QualityScore,
                        ["qualitySignals"] = candidate.QualitySignals,
                        ["reasons"] = candidate.Reasons.ToArray(),
                        ["campaignId"] = campaignId
                    }),
                ct);
        }

        var queuedCount = items.Count(static item => item.Queued);
        var skippedCount = items.Count - queuedCount;

        await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
            conn,
            RuntimeGovernanceService.CreateCapabilityEvent(
                capabilityKey: capabilityKey,
                profileKey: profileKey,
                eventType: req?.DryRun == true ? "capability_a_campaign_dry_run" : "capability_a_campaign_executed",
                reason: req?.DryRun == true ? "dry_run_preview" : "campaign_completed",
                details: new Dictionary<string, object?>
                {
                    ["campaignId"] = campaignId,
                    ["candidateCount"] = candidates.Length,
                    ["plannedCount"] = plannedCount,
                    ["queuedCount"] = queuedCount,
                    ["skippedCount"] = skippedCount,
                    ["dryRun"] = req?.DryRun == true,
                    ["allowUnsafeCandidates"] = req?.AllowUnsafeCandidates == true,
                    ["reasonCounts"] = reasonCounts,
                    ["items"] = items.Select(static item => new Dictionary<string, object?>
                    {
                        ["docId"] = item.DocId,
                        ["docPath"] = item.DocPath,
                        ["queued"] = item.Queued,
                        ["jobId"] = item.JobId,
                        ["reason"] = item.Reason,
                        ["previewText"] = item.PreviewText,
                        ["keySectionTitles"] = item.KeySectionTitles,
                        ["suggestedTags"] = item.SuggestedTags,
                        ["hypotheticalQuestions"] = item.HypotheticalQuestions,
                        ["qualityScore"] = item.QualityScore,
                        ["qualitySignals"] = item.QualitySignals
                    }).ToArray(),
                    ["docIds"] = selectedDocIds.ToArray(),
                    ["docPaths"] = selectedDocPaths.ToArray()
                }),
            ct);

        return new AdminRuntimeCapabilityAEnqueueResponseDto(
            CdcAlignment: cdcAlignment,
            Environment: environmentName,
            CapabilityKey: capabilityKey,
            CampaignId: campaignId,
            DryRun: req?.DryRun == true,
            AllowUnsafeCandidates: req?.AllowUnsafeCandidates == true,
            CandidateCount: candidates.Length,
            PlannedCount: plannedCount,
            QueuedCount: queuedCount,
            SkippedCount: skippedCount,
            ReasonCounts: reasonCounts,
            Items: items);
    }

    private static async Task<Guid?> LoadActiveIngestionJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            """
SELECT job_id
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running','paused')
ORDER BY created_at DESC
LIMIT 1;
""",
            new { tenant = tenantId, docPath },
            cancellationToken: ct));

    private static string[] ResolveUnsafeReasons(AdminRuntimeCapabilityAEnrichmentCandidateDto candidate)
        => candidate.Reasons
            .Where(reason =>
                string.Equals(reason, "auto_ingest_paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "revision_missing", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static AdminRuntimeCapabilityAEnqueueItemDto CreateEnqueueItem(
        AdminRuntimeCapabilityAEnrichmentCandidateDto candidate,
        bool queued,
        Guid? jobId = null,
        string? reason = null)
        => new(
            DocId: candidate.DocId,
            DocPath: candidate.DocPath,
            Queued: queued,
            JobId: jobId,
            Reason: reason,
            PreviewText: candidate.PreviewText,
            KeySectionTitles: candidate.KeySectionTitles,
            SuggestedTags: candidate.SuggestedTags,
            HypotheticalQuestions: candidate.HypotheticalQuestions,
            QualityScore: candidate.QualityScore,
            QualitySignals: candidate.QualitySignals);

    private static IngestionCapabilityAProfileSeed? BuildCapabilityAProfileSeed(AdminRuntimeCapabilityAEnrichmentCandidateDto candidate)
        => IngestionJobPayloadJson.NormalizeCapabilityAProfileSeed(new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: candidate.HypotheticalQuestions ?? [],
            SuggestedTags: candidate.SuggestedTags ?? [],
            KeySectionTitles: candidate.KeySectionTitles ?? [],
            PreviewText: candidate.PreviewText,
            BasedOnIndexedVersion: candidate.IndexedVersion));
}
