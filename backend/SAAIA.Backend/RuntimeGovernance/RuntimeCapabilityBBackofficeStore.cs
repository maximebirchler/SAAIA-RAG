using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBBackofficeStore
{
    internal static async Task<AdminRuntimeCapabilityBBackofficeCandidateDto[]> LoadCandidatesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? category,
        int? limit,
        RuntimeGovernanceOptions options,
        bool includeFresh,
        CancellationToken ct)
    {
        var categoryPath = await Endpoints.SummaryCategoryScopeResolver.ResolveScopeAsync(
            conn,
            tenantId,
            Endpoints.SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(category),
            Endpoints.SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(category),
            ct);

        var rows = await LoadCandidateRowsAsync(
            conn,
            tenantId,
            categoryPath,
            limit.HasValue ? Math.Clamp(limit.Value, 1, 500) : null,
            includeFresh,
            ct);

        return rows
            .Select(row => MapCandidate(row, options))
            .OrderByDescending(candidate => candidate.PriorityScore)
            .ThenBy(candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static async Task<IReadOnlyDictionary<Guid, AdminRuntimeCapabilityBBackofficeCandidateDto>> LoadCandidateLookupAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var rows = await LoadCandidateRowsAsync(
            conn,
            tenantId,
            categoryPath,
            limit: null,
            includeFresh: false,
            ct);

        return rows
            .Select(row => MapCandidate(row, options))
            .GroupBy(candidate => candidate.DocId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    internal static async Task<AdminRuntimeCapabilityBBackofficeCandidateDto[]> LoadCandidatesForDiagnosticsAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var rows = await LoadCandidateRowsAsync(
            conn,
            tenantId: null,
            categoryPath: null,
            limit: null,
            includeFresh: false,
            ct);

        return rows
            .Select(row => MapCandidate(row, options))
            .OrderByDescending(candidate => candidate.PriorityScore)
            .ThenBy(candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<CapabilityBBackofficeCandidateRow[]> LoadCandidateRowsAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        string? categoryPath,
        int? limit,
        bool includeFresh,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityBBackofficeCandidateRow>(new CommandDefinition(
            $"""
SELECT
  d.doc_id AS "DocId",
  d.doc_path AS "DocPath",
  d.doc_name AS "DocName",
  d.category AS "Category",
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState",
  EXISTS (
    SELECT 1
    FROM admin_jobs a
    WHERE a.tenant_id = d.tenant_id
      AND a.doc_id = d.doc_id
      AND a.job_type IN ('summary.request','summary.generate')
      AND a.status IN ('queued','running','paused')
  ) AS "HasActiveJob",
  latest.status AS "LastJobStatus",
  latest.last_job_finished_at AS "LastJobFinishedAt",
  latest.last_error AS "LastJobError"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
LEFT JOIN LATERAL (
  SELECT
    a.status,
    COALESCE(a.finished_at, a.canceled_at, a.created_at) AS last_job_finished_at,
    a.last_error
  FROM admin_jobs a
  WHERE a.tenant_id = d.tenant_id
    AND a.doc_id = d.doc_id
    AND a.job_type IN ('summary.request','summary.generate')
  ORDER BY COALESCE(a.finished_at, a.canceled_at, a.created_at) DESC, a.created_at DESC
  LIMIT 1
) latest ON TRUE
WHERE (@tenant IS NULL OR d.tenant_id = @tenant)
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    @includeFresh = true
    OR
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )
ORDER BY d.updated_at DESC
{(limit.HasValue ? "LIMIT @limit;" : ";")}
""",
            new { tenant = tenantId, categoryPath, limit, includeFresh },
            cancellationToken: ct))).ToArray();

    private static AdminRuntimeCapabilityBBackofficeCandidateDto MapCandidate(
        CapabilityBBackofficeCandidateRow row,
        RuntimeGovernanceOptions options)
    {
        var reasons = new List<string>();
        if (string.Equals(row.SummaryState, "missing", StringComparison.OrdinalIgnoreCase))
            reasons.Add("summary_missing");
        if (string.Equals(row.SummaryState, "stale", StringComparison.OrdinalIgnoreCase))
            reasons.Add("summary_stale");
        if (string.Equals(row.SummaryState, "fresh", StringComparison.OrdinalIgnoreCase))
            reasons.Add("summary_force_refresh");
        if (row.HasActiveJob)
            reasons.Add("summary_job_active");

        var cooldownReason = ResolveCooldownReason(row, options);
        if (string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal))
            reasons.Add("recent_summary_failure");
        else if (string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal))
            reasons.Add("recent_summary_cancellation");

        var recommendedAction = row.HasActiveJob
            ? "review_active_summary_job"
            : string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal)
                ? "inspect_recent_summary_failure"
                : string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal)
                    ? "review_recent_summary_cancellation"
                    : "enqueue_summary_generation";

        return new AdminRuntimeCapabilityBBackofficeCandidateDto(
            row.DocId,
            row.DocPath,
            row.DocName,
            row.Category,
            row.SummaryState,
            row.HasActiveJob,
            recommendedAction,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PriorityScore: BuildPriorityScore(row, cooldownReason),
            PolicyBlocked: row.HasActiveJob || !string.IsNullOrWhiteSpace(cooldownReason),
            PolicyBlockReason: row.HasActiveJob ? "active_summary_job_exists" : cooldownReason,
            LastJobStatus: row.LastJobStatus,
            LastJobFinishedAt: row.LastJobFinishedAt,
            LastJobError: row.LastJobError);
    }

    private static string? ResolveCooldownReason(
        CapabilityBBackofficeCandidateRow row,
        RuntimeGovernanceOptions options)
    {
        if (!row.LastJobFinishedAt.HasValue || string.IsNullOrWhiteSpace(row.LastJobStatus))
            return null;

        var status = row.LastJobStatus.Trim();
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            && row.LastJobFinishedAt.Value >= DateTimeOffset.UtcNow.AddHours(-Math.Abs(options.CapabilityBRecentFailureCooldownHours)))
        {
            return "recent_summary_job_failure";
        }

        if ((string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
             || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            && row.LastJobFinishedAt.Value >= DateTimeOffset.UtcNow.AddHours(-Math.Abs(options.CapabilityBRecentCancellationCooldownHours)))
        {
            return "recent_summary_job_cancellation";
        }

        return null;
    }

    private static int BuildPriorityScore(
        CapabilityBBackofficeCandidateRow row,
        string? cooldownReason)
    {
        var score = string.Equals(row.SummaryState, "missing", StringComparison.OrdinalIgnoreCase) ? 200 : 120;

        if (row.HasActiveJob)
            score -= 180;

        if (string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal))
            score -= 120;
        else if (string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal))
            score -= 80;

        return score;
    }

    private sealed record CapabilityBBackofficeCandidateRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string Category,
        string SummaryState,
        bool HasActiveJob,
        string? LastJobStatus,
        DateTimeOffset? LastJobFinishedAt,
        string? LastJobError);
}
