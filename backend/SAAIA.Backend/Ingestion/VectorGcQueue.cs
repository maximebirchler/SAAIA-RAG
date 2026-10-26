using Dapper;
using Npgsql;

internal static class VectorGcQueue
{
    public static async Task<IReadOnlyList<VectorGcPlan>> ClaimAsync(NpgsqlDataSource ds, int limit, int leaseSeconds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<VectorGcPlan>(new CommandDefinition("""
WITH candidates AS (
  SELECT p.plan_id FROM ingestion_vector_plans p
  WHERE p.retired_at IS NOT NULL AND p.manifest_sealed AND p.available_at<=now() AND (p.gc_expires_at IS NULL OR p.gc_expires_at<=now())
    AND NOT EXISTS (SELECT 1 FROM documents d WHERE d.tenant_id=p.tenant_id AND d.doc_id=p.doc_id AND (
      d.ingestion_version<=p.generation OR (d.indexed_version=p.generation AND
        (d.ingestion_vector_plan_fingerprint IS NULL OR d.ingestion_vector_plan_fingerprint=p.plan_fingerprint))))
  ORDER BY p.available_at,p.plan_id FOR UPDATE OF p SKIP LOCKED LIMIT @limit
)
UPDATE ingestion_vector_plans p SET gc_owner=@owner,gc_expires_at=now()+(@lease * interval '1 second'),gc_attempts=gc_attempts+1
FROM candidates c WHERE p.plan_id=c.plan_id
RETURNING p.plan_id AS "PlanId",p.tenant_id AS "TenantId",p.doc_id AS "DocId",p.generation AS "Generation",
 p.plan_fingerprint AS "Fingerprint",p.target_key AS "TargetKey",p.target_endpoint AS "TargetEndpoint",
 p.target_collection AS "TargetCollection",p.target_revision AS "TargetRevision",p.point_count AS "PointCount",
 p.gc_cursor AS "Cursor",p.gc_owner AS "Owner",(p.last_sweep_completed_at IS NOT NULL) AS "PreviouslySwept";
""", new { owner = Guid.NewGuid(), limit = Math.Clamp(limit, 1, 16), lease = Math.Clamp(leaseSeconds, 60, 600) }, cancellationToken: ct))).AsList();
    }

    public static async Task<IReadOnlyList<IngestionVectorPoint>> ReadBatchAsync(NpgsqlDataSource ds, VectorGcPlan plan, int limit, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<PointRow>(new CommandDefinition("""
SELECT ordinal AS "Ordinal",point_id AS "PointId",canonical_chunk_id AS "ChunkId" FROM ingestion_vector_plan_points
WHERE plan_id=@plan AND ordinal>=@cursor ORDER BY ordinal LIMIT @limit;
""", new { plan = plan.PlanId, cursor = plan.Cursor, limit = Math.Clamp(limit, 1, 256) }, cancellationToken: ct))).AsList();
        if (rows.Count != Math.Min(Math.Clamp(limit, 1, 256), plan.PointCount - plan.Cursor)
            || rows.Where((row, index) => row.Ordinal != plan.Cursor + index).Any())
            throw new InvalidDataException("vector_gc_manifest_incomplete");
        return rows.Select(row => new IngestionVectorPoint(row.PointId, row.ChunkId)).ToArray();
    }

    public static async Task<bool> CheckpointAsync(NpgsqlDataSource ds, VectorGcPlan plan, int batchCount,
        VectorGcBatchResult result, int recheckSeconds, int failureBackoffSeconds, CancellationToken ct)
    {
        if (batchCount < 0 || batchCount > plan.PointCount - plan.Cursor
            || (result.AbsenceVerified && result.AbsentCount != batchCount))
            throw new InvalidDataException("vector_gc_batch_observation_incomplete");
        var next = result.AbsenceVerified ? plan.Cursor + batchCount : plan.Cursor;
        var completed = result.AbsenceVerified && next == plan.PointCount;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
UPDATE ingestion_vector_plans SET gc_cursor=@cursor,gc_owner=NULL,gc_expires_at=NULL,
 last_gc_code=@code,last_batch_absence_at=CASE WHEN @verified THEN now() ELSE last_batch_absence_at END,
 last_sweep_completed_at=CASE WHEN @completed THEN now() ELSE last_sweep_completed_at END,
 available_at=now()+(@delay * interval '1 second')
WHERE plan_id=@plan AND retired_at IS NOT NULL AND gc_owner=@owner AND gc_expires_at>now();
""", new
        {
            plan = plan.PlanId, owner = plan.Owner, cursor = completed ? 0 : next, code = result.Code,
            verified = result.AbsenceVerified, completed,
            delay = result.AbsenceVerified ? (completed ? Math.Clamp(recheckSeconds, 1, 86400) : 0)
                : Math.Clamp(failureBackoffSeconds, 1, 86400)
        }, cancellationToken: ct));
        return changed == 1;
    }

    private sealed class PointRow
    {
        public int Ordinal { get; set; }
        public Guid PointId { get; set; }
        public Guid ChunkId { get; set; }
    }
}
