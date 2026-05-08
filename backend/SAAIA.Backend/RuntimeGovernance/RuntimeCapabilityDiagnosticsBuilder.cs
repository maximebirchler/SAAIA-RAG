using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityDiagnosticsBuilder
{
    internal static async Task<AdminRuntimeCapabilityOperationalSummaryDto> LoadCapabilityAOperationalSummaryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string capabilityAKey,
        CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<CapabilityAOperationalRow>(new CommandDefinition(
            """
WITH current_docs AS (
  SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.status AS "Status",
    COALESCE(d.ingestion_version, 0) AS "IngestionVersion",
    COALESCE(d.indexed_version, 0) AS "IndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "AutoIngestPaused"
  FROM documents d
  WHERE d.tenant_id = @tenant
    AND COALESCE(d.status, '') NOT IN ('missing', 'deleted')
)
SELECT
  cd."DocId",
  cd."DocPath",
  cd."Status",
  cd."IngestionVersion",
  cd."IndexedVersion",
  cd."AutoIngestPaused",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRevision",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN retrieval_chunks rc ON rc.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRetrievalChunks",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN retrieval_chunks rc ON rc.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion"
      AND (
        NOT (rc.metadata ? 'offsetStart')
        OR NOT (rc.metadata ? 'offsetEnd')
        OR jsonb_typeof(rc.metadata->'offsetStart') <> 'number'
        OR jsonb_typeof(rc.metadata->'offsetEnd') <> 'number')) AS "HasRetrievalChunkOffsetsMissing",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN exact_match_entries eme ON eme.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasExactMatchEntries",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN exact_match_entries eme ON eme.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion"
      AND (
        NOT (eme.metadata ? 'offsetStart')
        OR NOT (eme.metadata ? 'offsetEnd')
        OR jsonb_typeof(eme.metadata->'offsetStart') <> 'number'
        OR jsonb_typeof(eme.metadata->'offsetEnd') <> 'number')) AS "HasExactMatchOffsetsMissing",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN contextual_text_entries cte ON cte.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasContextualTextEntries",
  EXISTS(
    SELECT 1
    FROM ingestion_jobs i
    WHERE i.tenant_id = @tenant
      AND i.doc_path = cd."DocPath"
      AND i.action = 'upsert'
      AND i.status IN ('queued', 'running', 'paused')) AS "HasActiveUpsertJob"
FROM current_docs cd;
""",
            new { tenant = tenantId },
            cancellationToken: ct))).ToArray();

        var candidateRows = rows
            .Where(static row => BuildCapabilityAReasons(row).Count > 0)
            .ToArray();
        var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in candidateRows)
        {
            foreach (var reason in BuildCapabilityAReasons(row))
            {
                reasonCounts[reason] = reasonCounts.TryGetValue(reason, out var count) ? count + 1 : 1;
            }
        }

        var campaignRows = (await conn.QueryAsync<CapabilityACampaignOperationalRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  event_type AS "EventType",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_a_campaign_dry_run', 'capability_a_campaign_executed')
  AND details ? 'campaignId'
ORDER BY occurred_at DESC;
""",
            new { capabilityKey = capabilityAKey },
            cancellationToken: ct))).ToArray();

        var latestCampaign = campaignRows.FirstOrDefault();
        var activeCapabilityJobCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
SELECT COUNT(*)::int
FROM ingestion_jobs
WHERE payload ->> 'source' = 'capability_a'
  AND action = 'upsert'
  AND status IN ('queued', 'running', 'paused');
""",
            cancellationToken: ct));

        var terminalCapabilityJobCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
SELECT COUNT(*)::int
FROM ingestion_jobs
WHERE payload ->> 'source' = 'capability_a'
  AND action = 'upsert'
  AND status IN ('done', 'failed', 'canceled', 'cancelled');
""",
            cancellationToken: ct));

        var blockedByActiveJobCount = candidateRows.Count(static row => row.HasActiveUpsertJob);
        var blockedByPolicyCount = candidateRows.Count(static row => row.AutoIngestPaused);
        var readyToEnqueueCount = candidateRows.Count(row => !row.AutoIngestPaused && !row.HasActiveUpsertJob);
        var offsetBackfillCandidateCount = candidateRows.Count(static row =>
            row.HasRetrievalChunkOffsetsMissing || row.HasExactMatchOffsetsMissing);

        return new AdminRuntimeCapabilityOperationalSummaryDto(
            CandidateCount: candidateRows.Length,
            ReadyToEnqueueCount: readyToEnqueueCount,
            BlockedByActiveJobCount: blockedByActiveJobCount,
            BlockedByCooldownCount: blockedByPolicyCount,
            ActiveCapabilityJobCount: activeCapabilityJobCount,
            TotalCampaignCount: campaignRows.Length,
            ActiveCampaignCount: 0,
            TerminalCapabilityJobCount: terminalCapabilityJobCount,
            StoredSummaryCount: 0,
            ReasonCounts: reasonCounts,
            OffsetBackfillCandidateCount: offsetBackfillCandidateCount,
            LatestCampaignProgressPercent: null,
            LatestCampaignId: latestCampaign?.CampaignId,
            LatestCampaignStatus: latestCampaign is null
                ? null
                : string.Equals(latestCampaign.EventType, "capability_a_campaign_dry_run", StringComparison.Ordinal)
                    ? "dry_run"
                    : "executed",
            LatestCampaignOccurredAt: ToUtcOffset(latestCampaign?.OccurredAt));
    }

    internal static AdminRuntimeCapabilityDiagnosticDto BuildCapabilityDiagnostic(
        AdminRuntimeCapabilityStateDto state,
        string capabilityAKey,
        string capabilityBKey,
        AdminRuntimeCapabilityOperationalSummaryDto? operationalSummary = null)
    {
        var blockers = BuildBlockers(state);
        var recommendations = BuildRecommendations(state, capabilityAKey, capabilityBKey, blockers, operationalSummary);

        return new AdminRuntimeCapabilityDiagnosticDto(
            Key: state.Key,
            DisplayName: state.DisplayName,
            Family: state.Family,
            Status: ResolveDiagnosticStatus(state),
            ProfileKey: state.ProfileKey,
            Implemented: state.Implemented,
            Stale: state.Stale,
            Qualified: state.Qualified,
            Authorized: state.Authorized,
            Selected: state.Selected,
            PersistedAuthorized: state.PersistedAuthorized,
            PersistedSelected: state.PersistedSelected,
            StaleReason: state.StaleReason,
            QualificationAgeHours: state.QualificationAgeHours,
            QualificationExpiresAt: state.QualificationExpiresAt,
            Blockers: blockers,
            Recommendations: recommendations,
            LastCheckedAt: state.LastCheckedAt,
            LastQualifiedAt: state.LastQualifiedAt,
            LastError: state.LastError,
            OperationalSummary: operationalSummary);
    }

    internal static AdminRuntimeDiagnosticsSummaryDto BuildDiagnosticsSummary(
        IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> items,
        string capabilityAKey,
        string capabilityBKey)
        => new(
            TotalCapabilities: items.Count,
            ImplementedCapabilities: items.Count(item => item.Implemented),
            QualifiedCapabilities: items.Count(item => item.Qualified && !item.Stale),
            SelectedCapabilities: items.Count(item => item.Selected && !item.Stale),
            PersistedSelectedCapabilities: items.Count(item => item.PersistedSelected),
            BlockedCapabilities: items.Count(item => item.Blockers.Count > 0),
            StaleCapabilities: items.Count(item => item.Stale),
            Operational: BuildOperationalSummary(items, capabilityAKey, capabilityBKey));

    internal static IReadOnlyList<AdminRuntimeOperationalCapabilitySummaryDto> BuildOperationalItems(
        IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> items)
        => items
            .Where(static item => item.OperationalSummary is not null)
            .Select(static item => new AdminRuntimeOperationalCapabilitySummaryDto(
                Key: item.Key,
                DisplayName: item.DisplayName,
                Family: item.Family,
                Status: item.Status,
                Implemented: item.Implemented,
                Qualified: item.Qualified,
                Selected: item.Selected,
                Stale: item.Stale,
                Summary: item.OperationalSummary!,
                Recommendations: item.Recommendations))
            .ToArray();

    internal static async Task<AdminRuntimeCapabilityOperationalSummaryDto> LoadCapabilityBOperationalSummaryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string capabilityBKey,
        IReadOnlyList<AdminRuntimeCapabilityBBackofficeCandidateDto> candidates,
        CancellationToken ct)
    {
        var candidateCount = candidates.Count;
        var readyToEnqueueCount = candidates.Count(static candidate => !candidate.HasActiveJob && string.IsNullOrWhiteSpace(candidate.PolicyBlockReason));
        var blockedByActiveJobCount = candidates.Count(static candidate => candidate.HasActiveJob);
        var blockedByCooldownCount = candidates.Count(static candidate =>
            string.Equals(candidate.PolicyBlockReason, "recent_summary_job_failure", StringComparison.Ordinal)
            || string.Equals(candidate.PolicyBlockReason, "recent_summary_job_cancellation", StringComparison.Ordinal));

        var activeCapabilityJobCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
SELECT COUNT(*)::int
FROM admin_jobs
WHERE tenant_id = @tenantId
  AND payload ->> 'source' = 'capability_b'
  AND job_type = 'summary.generate'
  AND status IN ('queued', 'running', 'paused');
""",
            new { tenantId },
            cancellationToken: ct));

        var campaignRows = (await conn.QueryAsync<CapabilityBCampaignOperationalRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  event_type AS "EventType",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_b_campaign_dry_run', 'capability_b_campaign_executed')
  AND details ? 'campaignId'
  AND EXISTS (
    SELECT 1
    FROM admin_jobs a
    WHERE a.tenant_id = @tenantId
      AND a.payload ->> 'source' = 'capability_b'
      AND a.payload ->> 'campaignId' = details ->> 'campaignId'
  )
ORDER BY occurred_at DESC;
""",
            new { capabilityKey = capabilityBKey, tenantId },
            cancellationToken: ct))).ToArray();

        var latestCampaignRow = campaignRows.FirstOrDefault();
        var totalCampaignCount = campaignRows.Length;

        var campaignStates = campaignRows.Length == 0
            ? Array.Empty<CapabilityBCampaignJobAggregateRow>()
            : (await conn.QueryAsync<CapabilityBCampaignJobAggregateRow>(new CommandDefinition(
                """
SELECT
  CAST(a.payload ->> 'campaignId' AS uuid) AS "CampaignId",
  SUM(CASE WHEN a.status IN ('queued', 'running', 'paused') THEN 1 ELSE 0 END)::int AS "ActiveJobCount",
  SUM(CASE WHEN a.status IN ('done', 'failed', 'canceled', 'cancelled') THEN 1 ELSE 0 END)::int AS "TerminalJobCount",
  SUM(
    CASE
      WHEN jsonb_typeof(a.result->'stored')='boolean' AND (a.result->>'stored')::boolean THEN 1
      ELSE 0
    END
  )::int AS "StoredSummaryCount"
FROM admin_jobs a
WHERE a.tenant_id = @tenantId
  AND a.payload ->> 'source' = 'capability_b'
  AND a.payload ? 'campaignId'
GROUP BY CAST(a.payload ->> 'campaignId' AS uuid);
""",
                new { tenantId },
                cancellationToken: ct))).ToArray();

        var activeCampaignCount = campaignStates.Count(static row => row.ActiveJobCount > 0);
        var terminalCapabilityJobCount = campaignStates.Sum(static row => row.TerminalJobCount);
        var storedSummaryCount = campaignStates.Sum(static row => row.StoredSummaryCount);
        var llmFailureRows = (await conn.QueryAsync<NamedCountRow>(new CommandDefinition(
            """
SELECT
  lower(btrim(result ->> 'llmFailureCategory')) AS "Key",
  COUNT(*)::int AS "Count"
FROM admin_jobs
WHERE tenant_id = @tenantId
  AND payload ->> 'source' = 'capability_b'
  AND job_type = 'summary.generate'
  AND status IN ('done', 'failed', 'canceled', 'cancelled')
  AND NULLIF(btrim(result ->> 'llmFailureCategory'), '') IS NOT NULL
GROUP BY lower(btrim(result ->> 'llmFailureCategory'));
""",
            new { tenantId },
            cancellationToken: ct))).ToArray();

        int? latestCampaignProgressPercent = null;
        string? latestCampaignStatus = null;
        Guid? latestCampaignId = latestCampaignRow?.CampaignId;
        DateTimeOffset? latestCampaignOccurredAt = ToUtcOffset(latestCampaignRow?.OccurredAt);
        if (latestCampaignRow is not null)
        {
            latestCampaignStatus = string.Equals(latestCampaignRow.EventType, "capability_b_campaign_dry_run", StringComparison.Ordinal)
                ? "dry_run"
                : "executed";

            latestCampaignProgressPercent = (await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignDetailAsync(
                conn,
                capabilityBKey,
                latestCampaignRow.CampaignId,
                ct))?.ProgressPercent;
        }

        return new AdminRuntimeCapabilityOperationalSummaryDto(
            CandidateCount: candidateCount,
            ReadyToEnqueueCount: readyToEnqueueCount,
            BlockedByActiveJobCount: blockedByActiveJobCount,
            BlockedByCooldownCount: blockedByCooldownCount,
            ActiveCapabilityJobCount: activeCapabilityJobCount,
            TotalCampaignCount: totalCampaignCount,
            ActiveCampaignCount: activeCampaignCount,
            TerminalCapabilityJobCount: terminalCapabilityJobCount,
            StoredSummaryCount: storedSummaryCount,
            ReasonCounts: candidates
                .SelectMany(static candidate => candidate.Reasons)
                .GroupBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            OffsetBackfillCandidateCount: null,
            LatestCampaignProgressPercent: latestCampaignProgressPercent,
            LatestCampaignId: latestCampaignId,
            LatestCampaignStatus: latestCampaignStatus,
            LatestCampaignOccurredAt: latestCampaignOccurredAt,
            LlmFailureCounts: llmFailureRows.ToDictionary(
                static row => row.Key,
                static row => row.Count,
                StringComparer.OrdinalIgnoreCase));
    }

    private static AdminRuntimeDiagnosticsOperationalSummaryDto BuildOperationalSummary(
        IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> items,
        string capabilityAKey,
        string capabilityBKey)
    {
        var capabilityA = items.FirstOrDefault(item => string.Equals(item.Key, capabilityAKey, StringComparison.Ordinal));
        var capabilityB = items.FirstOrDefault(item => string.Equals(item.Key, capabilityBKey, StringComparison.Ordinal));
        var aSummary = capabilityA?.OperationalSummary;
        var bSummary = capabilityB?.OperationalSummary;

        return new AdminRuntimeDiagnosticsOperationalSummaryDto(
            CapabilityACandidateCount: aSummary?.CandidateCount ?? 0,
            CapabilityAReadyToEnqueueCount: aSummary?.ReadyToEnqueueCount ?? 0,
            CapabilityAOffsetBackfillCandidateCount: aSummary?.OffsetBackfillCandidateCount ?? 0,
            CapabilityBBacklogCount: bSummary?.CandidateCount ?? 0,
            CapabilityBReadyToEnqueueCount: bSummary?.ReadyToEnqueueCount ?? 0,
            CapabilityBActiveJobCount: bSummary?.ActiveCapabilityJobCount ?? 0,
            CapabilityBLatestCampaignProgressPercent: bSummary?.LatestCampaignProgressPercent);
    }

    private static string ResolveDiagnosticStatus(AdminRuntimeCapabilityStateDto state)
    {
        if (!state.Implemented)
            return "not_implemented";
        if (state.Stale)
            return "stale";
        if (state.Selected)
            return "selected";
        if (state.Authorized)
            return "authorized";
        if (state.Qualified)
            return "qualified";
        if (state.Healthy)
            return "healthy";
        if (state.Configured)
            return "configured";
        if (state.Installed)
            return "installed";
        return "missing_dependencies";
    }

    private static string[] BuildBlockers(AdminRuntimeCapabilityStateDto state)
    {
        var blockers = new List<string>();

        if (!state.Implemented)
            blockers.Add("not_implemented");
        if (!state.Installed)
            blockers.Add("missing_dependencies");
        else if (!state.Configured)
            blockers.Add("not_configured");
        if (ContainsError(state, "hardware gate failed"))
            blockers.Add("hardware_gate_failed");
        if (ContainsError(state, "runtime gate failed"))
            blockers.Add("runtime_gate_failed");
        if (state.Stale)
            blockers.Add("stale_qualification");
        if (ContainsError(state, "performance budget failed"))
            blockers.Add("performance_budget_failed");
        if (ContainsError(state, "profile policy failed"))
            blockers.Add("profile_policy_failed");
        if (state.Configured && !state.Healthy && state.Implemented)
            blockers.Add("warmup_failed");
        if (state.Qualified && !state.Authorized)
            blockers.Add("not_authorized");
        if (state.Authorized && !state.Selected && state.DesiredEnabled)
            blockers.Add("not_selected");

        return blockers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] BuildRecommendations(
        AdminRuntimeCapabilityStateDto state,
        string capabilityAKey,
        string capabilityBKey,
        IReadOnlyList<string> blockers,
        AdminRuntimeCapabilityOperationalSummaryDto? operationalSummary = null)
    {
        var recommendations = new List<string>();

        if (blockers.Contains("missing_dependencies", StringComparer.Ordinal))
            recommendations.Add("configure qdrant/tei endpoints and collection settings");
        if (blockers.Contains("not_configured", StringComparer.Ordinal))
            recommendations.Add("complete retrieval runtime configuration before requalifying");
        if (blockers.Contains("hardware_gate_failed", StringComparer.Ordinal))
            recommendations.Add("relax hardware gate thresholds or move qualification to a stronger host profile");
        if (blockers.Contains("runtime_gate_failed", StringComparer.Ordinal))
            recommendations.Add("align runtime-specific hardware thresholds with the selected profile or qualify on a host sized for qdrant/tei/rerank");
        if (blockers.Contains("stale_qualification", StringComparer.Ordinal))
            recommendations.Add("requalify this capability because its stored qualification no longer matches the active profile or runtime configuration");
        if (blockers.Contains("performance_budget_failed", StringComparer.Ordinal))
            recommendations.Add("use a less strict profile or improve runtime latency before selecting this capability");
        if (blockers.Contains("profile_policy_failed", StringComparer.Ordinal))
            recommendations.Add("pick a compatible profile or enable the required runtime features before requalifying");
        if (blockers.Contains("warmup_failed", StringComparer.Ordinal))
            recommendations.Add("inspect warmup check details and rerun qualification after runtime recovery");
        if (blockers.Contains("not_authorized", StringComparer.Ordinal))
            recommendations.Add("authorize the qualified capability before selecting it");
        if (blockers.Contains("not_selected", StringComparer.Ordinal))
            recommendations.Add("select the authorized capability if it should be active");
        if (!state.Implemented)
            recommendations.Add("keep this capability disabled until a real implementation exists");
        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, capabilityAKey, StringComparison.Ordinal))
        {
            var retrievalOffsetBackfillCount = 0;
            var exactOffsetBackfillCount = 0;
            if (operationalSummary?.ReasonCounts is not null)
            {
                operationalSummary.ReasonCounts.TryGetValue("retrieval_chunk_offsets_missing", out retrievalOffsetBackfillCount);
                operationalSummary.ReasonCounts.TryGetValue("exact_match_offsets_missing", out exactOffsetBackfillCount);
            }

            if (retrievalOffsetBackfillCount > 0 || exactOffsetBackfillCount > 0)
            {
                recommendations.Add(
                    $"capability A backlog includes {retrievalOffsetBackfillCount} retrieval chunk offset backfill candidates and {exactOffsetBackfillCount} exact-match offset backfill candidates; enqueue governed reindex jobs to backfill legacy evidence-pack offsets");
            }

            if (operationalSummary is not null && operationalSummary.CandidateCount > 0)
            {
                recommendations.Add(
                    $"review capability A candidates: {operationalSummary.ReadyToEnqueueCount} ready to enqueue, {operationalSummary.BlockedByActiveJobCount} blocked by active ingestion jobs, {operationalSummary.BlockedByCooldownCount} blocked by admin policy");
            }

            recommendations.Add("review capability A semantic previews and enqueue controlled reindex jobs when appropriate");
        }

        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, capabilityBKey, StringComparison.Ordinal))
        {
            if (operationalSummary is not null && operationalSummary.CandidateCount > 0)
            {
                recommendations.Add(
                    $"review capability B candidates: {operationalSummary.ReadyToEnqueueCount} ready to enqueue, {operationalSummary.BlockedByActiveJobCount} blocked by active summary jobs, {operationalSummary.BlockedByCooldownCount} blocked by recent failure/cancellation cooldowns");
            }

            if (operationalSummary is not null && operationalSummary.ActiveCapabilityJobCount > 0)
            {
                recommendations.Add("monitor active capability B summary jobs and the latest campaign progress from diagnostics");
            }

            if (operationalSummary is not null
                && operationalSummary.CandidateCount == 0
                && operationalSummary.ActiveCapabilityJobCount == 0)
            {
                recommendations.Add("backoffice summary backlog is currently clear");
            }
        }

        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, capabilityBKey, StringComparison.Ordinal))
        {
            recommendations.Add("review capability B candidates and enqueue governed backoffice summary generation jobs when appropriate");
        }

        if (recommendations.Count == 0 && state.Selected)
            recommendations.Add("runtime is ready for the nominal path");

        return recommendations.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool ContainsError(AdminRuntimeCapabilityStateDto state, string fragment)
        => !string.IsNullOrWhiteSpace(state.LastError)
           && state.LastError.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildCapabilityAReasons(CapabilityAOperationalRow row)
    {
        var reasons = new List<string>();

        if (row.IndexedVersion <= 0)
            reasons.Add("never_indexed");
        if (row.IngestionVersion > row.IndexedVersion)
            reasons.Add("indexed_version_outdated");
        if (row.IndexedVersion > 0 && !row.HasRevision)
            reasons.Add("revision_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasRetrievalChunks)
            reasons.Add("retrieval_chunks_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && row.HasRetrievalChunks && row.HasRetrievalChunkOffsetsMissing)
            reasons.Add("retrieval_chunk_offsets_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasExactMatchEntries)
            reasons.Add("exact_match_entries_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && row.HasExactMatchEntries && row.HasExactMatchOffsetsMissing)
            reasons.Add("exact_match_offsets_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasContextualTextEntries)
            reasons.Add("contextual_text_entries_missing");
        if (row.AutoIngestPaused)
            reasons.Add("auto_ingest_paused");

        return reasons;
    }

    private sealed record CapabilityAOperationalRow(
        Guid DocId,
        string DocPath,
        string Status,
        int IngestionVersion,
        int IndexedVersion,
        bool AutoIngestPaused,
        bool HasRevision,
        bool HasRetrievalChunks,
        bool HasRetrievalChunkOffsetsMissing,
        bool HasExactMatchEntries,
        bool HasExactMatchOffsetsMissing,
        bool HasContextualTextEntries,
        bool HasActiveUpsertJob);

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

    private sealed record CapabilityACampaignOperationalRow(
        Guid CampaignId,
        string EventType,
        DateTime OccurredAt);

    private sealed record CapabilityBCampaignOperationalRow(
        Guid CampaignId,
        string EventType,
        DateTime OccurredAt);

    private sealed record CapabilityBCampaignJobAggregateRow(
        Guid CampaignId,
        int ActiveJobCount,
        int TerminalJobCount,
        int StoredSummaryCount);

    private sealed record NamedCountRow(
        string Key,
        int Count);
}
