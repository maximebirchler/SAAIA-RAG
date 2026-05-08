using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBIngestionIdleCoordinator
{
    internal static TimeSpan ResolveRequiredIdleDelay(RuntimeGovernanceOptions options)
        => TimeSpan.FromSeconds(Math.Max(0, options.CapabilityBIngestionIdleDelaySeconds));

    internal static CapabilityBIngestionIdleSnapshot Evaluate(
        int activeIngestionJobs,
        DateTimeOffset? lastIngestionActivityAt,
        TimeSpan requiredIdleDelay,
        DateTimeOffset now)
    {
        if (activeIngestionJobs > 0)
        {
            return new CapabilityBIngestionIdleSnapshot(
                IsIdle: false,
                ActiveIngestionJobs: activeIngestionJobs,
                LastIngestionActivityAt: lastIngestionActivityAt,
                RequiredIdleDelay: requiredIdleDelay,
                IdleFor: null,
                Reason: "ingestion_active");
        }

        if (!lastIngestionActivityAt.HasValue)
        {
            return new CapabilityBIngestionIdleSnapshot(
                IsIdle: true,
                ActiveIngestionJobs: 0,
                LastIngestionActivityAt: null,
                RequiredIdleDelay: requiredIdleDelay,
                IdleFor: null,
                Reason: "no_ingestion_activity");
        }

        var idleFor = now - lastIngestionActivityAt.Value;
        if (idleFor < TimeSpan.Zero)
            idleFor = TimeSpan.Zero;

        var isIdle = idleFor >= requiredIdleDelay;
        return new CapabilityBIngestionIdleSnapshot(
            IsIdle: isIdle,
            ActiveIngestionJobs: 0,
            LastIngestionActivityAt: lastIngestionActivityAt,
            RequiredIdleDelay: requiredIdleDelay,
            IdleFor: idleFor,
            Reason: isIdle ? "ingestion_idle" : "ingestion_recent");
    }

    internal static async Task<CapabilityBIngestionIdleSnapshot> LoadGlobalIngestionIdleSnapshotAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await LoadGlobalIngestionIdleSnapshotAsync(conn, options, now, ct);
    }

    internal static async Task<CapabilityBIngestionIdleSnapshot> LoadGlobalIngestionIdleSnapshotAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string sql = """
SELECT
  COUNT(*) FILTER (WHERE status IN ('queued','running'))::int AS "ActiveIngestionJobs",
  MAX(COALESCE(locked_at, started_at, finished_at, created_at)) AS "LastIngestionActivityAt"
FROM ingestion_jobs;
""";

        var row = await conn.QuerySingleAsync<CapabilityBIngestionActivityRow>(
            new CommandDefinition(sql, cancellationToken: ct));

        return Evaluate(
            row.ActiveIngestionJobs,
            ToUtcOffset(row.LastIngestionActivityAt),
            ResolveRequiredIdleDelay(options),
            now);
    }

    internal static async Task<CapabilityBIngestionIdleSnapshot> LoadTenantIngestionIdleSnapshotAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string sql = """
SELECT
  COUNT(*) FILTER (WHERE status IN ('queued','running'))::int AS "ActiveIngestionJobs",
  MAX(COALESCE(locked_at, started_at, finished_at, created_at)) AS "LastIngestionActivityAt"
FROM ingestion_jobs
WHERE tenant_id=@tenant;
""";

        var row = await conn.QuerySingleAsync<CapabilityBIngestionActivityRow>(
            new CommandDefinition(sql, new { tenant = tenantId }, cancellationToken: ct));

        return Evaluate(
            row.ActiveIngestionJobs,
            ToUtcOffset(row.LastIngestionActivityAt),
            ResolveRequiredIdleDelay(options),
            now);
    }

    internal static async Task<IReadOnlyList<Guid>> LoadTenantsWithSummaryBacklogAsync(
        NpgsqlConnection conn,
        int maxTenants,
        CancellationToken ct)
    {
        var limit = Math.Clamp(maxTenants, 1, 64);
        const string sql = """
SELECT d.tenant_id AS "TenantId"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
LEFT JOIN LATERAL (
  SELECT r.revision_id
  FROM document_revisions r
  WHERE r.tenant_id = d.tenant_id
    AND r.doc_id = d.doc_id
    AND r.indexed_version = COALESCE(d.indexed_version, 0)
  ORDER BY r.published_at DESC NULLS LAST, r.created_at DESC NULLS LAST
  LIMIT 1
) current_revision ON TRUE
LEFT JOIN document_profiles llm_profile
  ON llm_profile.tenant_id = d.tenant_id
 AND llm_profile.doc_id = d.doc_id
 AND llm_profile.revision_id = current_revision.revision_id
 AND llm_profile.profile_version = 'llm_backoffice_v1'
WHERE d.status='indexed'
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
    OR llm_profile.document_profile_id IS NULL
    OR COALESCE(
      CASE
        WHEN COALESCE(llm_profile.metadata ->> 'contentCardEvidenceSchemaVersion', '') ~ '^[0-9]+$'
          THEN (llm_profile.metadata ->> 'contentCardEvidenceSchemaVersion')::int
        ELSE 0
      END,
      0) < @contentCardEvidenceSchemaVersion
  )
  AND NOT EXISTS (
    SELECT 1
    FROM admin_jobs a
    WHERE a.tenant_id = d.tenant_id
      AND a.doc_id = d.doc_id
      AND a.job_type IN ('summary.request','summary.generate')
      AND a.status IN ('queued','running','paused')
  )
GROUP BY d.tenant_id
ORDER BY MAX(d.updated_at) DESC
LIMIT @limit;
""";

        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(
            sql,
            new { limit, contentCardEvidenceSchemaVersion = DocumentFoundationRepo.ContentCardEvidenceSchemaVersion },
            cancellationToken: ct));

        return rows.ToArray();
    }

    internal static async Task<bool> IsTenantIdleForCapabilityBAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!options.CapabilityBRequireIngestionIdleForExecution)
            return true;

        var snapshot = await LoadTenantIngestionIdleSnapshotAsync(conn, options, tenantId, now, ct);
        return snapshot.IsIdle;
    }

    internal static AdminRuntimeCapabilityBIdleSchedulerDto BuildSchedulerDto(
        RuntimeGovernanceOptions options,
        CapabilityBIngestionIdleSnapshot snapshot)
    {
        var requireIdle = options.CapabilityBRequireIngestionIdleForExecution;
        var enabled = options.CapabilityBWorkerEnabled;
        var autoEnqueueEnabled = options.CapabilityBAutoEnqueueWhenIngestionIdleEnabled;
        var state = !enabled
            ? "worker_disabled"
            : !requireIdle
                ? "unrestricted"
                : snapshot.IsIdle
                    ? "idle"
                    : "waiting_for_ingestion_idle";

        return new AdminRuntimeCapabilityBIdleSchedulerDto(
            Enabled: enabled,
            RequiresIngestionIdle: requireIdle,
            AutoEnqueueEnabled: autoEnqueueEnabled,
            IsIdle: !requireIdle || snapshot.IsIdle,
            State: state,
            Reason: snapshot.Reason,
            ActiveIngestionJobs: snapshot.ActiveIngestionJobs,
            LastIngestionActivityAt: snapshot.LastIngestionActivityAt,
            RequiredIdleSeconds: (int)Math.Ceiling(snapshot.RequiredIdleDelay.TotalSeconds),
            IdleForSeconds: snapshot.IdleFor.HasValue
                ? (int)Math.Floor(snapshot.IdleFor.Value.TotalSeconds)
                : null,
            AutoEnqueueBatchSize: Math.Clamp(options.CapabilityBAutoEnqueueBatchSize, 1, 500),
            RunningJobLeaseTimeoutSeconds: Math.Max(0, options.CapabilityBRunningJobLeaseTimeoutSeconds));
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

    private sealed class CapabilityBIngestionActivityRow
    {
        public int ActiveIngestionJobs { get; set; }
        public DateTime? LastIngestionActivityAt { get; set; }
    }
}

internal sealed record CapabilityBIngestionIdleSnapshot(
    bool IsIdle,
    int ActiveIngestionJobs,
    DateTimeOffset? LastIngestionActivityAt,
    TimeSpan RequiredIdleDelay,
    TimeSpan? IdleFor,
    string Reason);
