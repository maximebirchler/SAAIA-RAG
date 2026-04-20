using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityDiagnosticsBuilder
{
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

    internal static AdminRuntimeDiagnosticsSummaryDto BuildDiagnosticsSummary(IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> items)
        => new(
            TotalCapabilities: items.Count,
            ImplementedCapabilities: items.Count(item => item.Implemented),
            QualifiedCapabilities: items.Count(item => item.Qualified && !item.Stale),
            SelectedCapabilities: items.Count(item => item.Selected && !item.Stale),
            PersistedSelectedCapabilities: items.Count(item => item.PersistedSelected),
            BlockedCapabilities: items.Count(item => item.Blockers.Count > 0),
            StaleCapabilities: items.Count(item => item.Stale));

    internal static async Task<AdminRuntimeCapabilityOperationalSummaryDto> LoadCapabilityBOperationalSummaryAsync(
        NpgsqlConnection conn,
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
WHERE payload ->> 'source' = 'capability_b'
  AND job_type = 'summary.generate'
  AND status IN ('queued', 'running', 'paused');
""",
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
ORDER BY occurred_at DESC;
""",
            new { capabilityKey = capabilityBKey },
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
WHERE a.payload ->> 'source' = 'capability_b'
  AND a.payload ? 'campaignId'
GROUP BY CAST(a.payload ->> 'campaignId' AS uuid);
""",
                cancellationToken: ct))).ToArray();

        var activeCampaignCount = campaignStates.Count(static row => row.ActiveJobCount > 0);
        var terminalCapabilityJobCount = campaignStates.Sum(static row => row.TerminalJobCount);
        var storedSummaryCount = campaignStates.Sum(static row => row.StoredSummaryCount);

        int? latestCampaignProgressPercent = null;
        string? latestCampaignStatus = null;
        Guid? latestCampaignId = latestCampaignRow?.CampaignId;
        DateTimeOffset? latestCampaignOccurredAt = latestCampaignRow?.OccurredAt;
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
            LatestCampaignProgressPercent: latestCampaignProgressPercent,
            LatestCampaignId: latestCampaignId,
            LatestCampaignStatus: latestCampaignStatus,
            LatestCampaignOccurredAt: latestCampaignOccurredAt);
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

    private sealed record CapabilityBCampaignOperationalRow(
        Guid CampaignId,
        string EventType,
        DateTimeOffset OccurredAt);

    private sealed record CapabilityBCampaignJobAggregateRow(
        Guid CampaignId,
        int ActiveJobCount,
        int TerminalJobCount,
        int StoredSummaryCount);
}
