using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Npgsql;

/// <summary>Immutable manifests and irreversible retirement; no HTTP and no source text.</summary>
internal static class IngestionVectorRegistry
{
    public static async Task<Guid> RegisterAsync(NpgsqlDataSource ds, IngestionJob job, string owner,
        RagOptions targetOptions, string fingerprint, IReadOnlyList<IngestionVectorPoint> points, CancellationToken ct)
    {
        if (job.Action != "upsert") throw new InvalidOperationException("vector_plan_requires_upsert_job");
        if (fingerprint.Length != 64 || fingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("vector_plan_fingerprint_invalid");
        if (points.Select(p => p.PointId).Distinct().Count() != points.Count
            || points.Any(p => p.PointId != IngestionVectorPlan.PointId(p.ChunkId, fingerprint)))
            throw new InvalidOperationException("vector_manifest_identity_invalid");
        var target = VectorTargetIdentity.From(targetOptions);
        var manifestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(points))).ToLowerInvariant();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await IngestionLeaseGuard.LockOwnedAsync(conn, tx, job.JobId, owner, ct, job.TenantId, job.DocPath))
            throw new IngestionLeaseLostException();
        var current = await conn.ExecuteScalarAsync<int?>(new CommandDefinition("""
SELECT ingestion_version FROM documents WHERE tenant_id=@tenant AND doc_id=@doc FOR UPDATE;
""", new { tenant = job.TenantId, doc = job.DocId }, transaction: tx, cancellationToken: ct));
        if (current != job.Version || job.Version <= 0)
            throw new InvalidOperationException("vector_plan_generation_superseded");
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_vector_plans WHERE tenant_id=@tenant AND doc_id=@doc
AND plan_fingerprint=@fingerprint AND (retired_at IS NOT NULL OR generation<>@version));
""", new { tenant = job.TenantId, doc = job.DocId, version = job.Version, fingerprint }, transaction: tx, cancellationToken: ct)))
            throw new InvalidOperationException("vector_plan_retired_or_generation_mismatch");
        var existing = await conn.QueryFirstOrDefaultAsync<ManifestState>(new CommandDefinition("""
SELECT plan_id AS "Id", target_key AS "Target", manifest_sha256 AS "Manifest", point_count AS "Count", retired_at AS "RetiredAt", manifest_sealed AS "Sealed"
FROM ingestion_vector_plans WHERE tenant_id=@tenant AND doc_id=@doc AND generation=@version AND plan_fingerprint=@fingerprint FOR UPDATE;
""", new { tenant = job.TenantId, doc = job.DocId, version = job.Version, fingerprint }, transaction: tx, cancellationToken: ct));
        if (existing is not null)
        {
            if (existing.RetiredAt is not null) throw new InvalidOperationException("vector_plan_retired");
            if (!existing.Sealed || existing.Target != target.Key || existing.Manifest != manifestHash || existing.Count != points.Count)
                throw new InvalidOperationException("vector_manifest_conflict");
            await tx.CommitAsync(ct);
            return existing.Id;
        }

        var planId = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition("""
INSERT INTO ingestion_generation_watermarks(tenant_id,doc_id,highest_generation) VALUES(@tenant,@doc,@version)
ON CONFLICT (tenant_id,doc_id) DO UPDATE SET highest_generation=GREATEST(ingestion_generation_watermarks.highest_generation,EXCLUDED.highest_generation);
INSERT INTO ingestion_vector_plans(plan_id,tenant_id,doc_id,generation,plan_fingerprint,target_key,target_endpoint,target_collection,target_revision,point_count,manifest_sha256)
VALUES(@plan,@tenant,@doc,@version,@fingerprint,@target,@endpoint,@collection,@revision,@count,@manifest);
INSERT INTO ingestion_vector_plan_points(plan_id,ordinal,point_id,canonical_chunk_id)
SELECT @plan,ordinal,point_id,chunk_id FROM unnest(CAST(@ordinals AS int[]),CAST(@point_ids AS uuid[]),CAST(@chunk_ids AS uuid[])) AS p(ordinal,point_id,chunk_id);
UPDATE ingestion_vector_plans SET manifest_sealed=true WHERE plan_id=@plan;
""", new
        {
            plan = planId, tenant = job.TenantId, doc = job.DocId, version = job.Version, fingerprint,
            target = target.Key, endpoint = target.Endpoint, collection = target.Collection, revision = target.Revision,
            count = points.Count, manifest = manifestHash, ordinals = Enumerable.Range(0, points.Count).ToArray(),
            point_ids = points.Select(p => p.PointId).ToArray(), chunk_ids = points.Select(p => p.ChunkId).ToArray()
        }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return planId;
    }

    public static async Task AssertWritableAsync(NpgsqlDataSource ds, Guid planId, IngestionJob job, string owner, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var allowed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_vector_plans p JOIN documents d ON d.tenant_id=p.tenant_id AND d.doc_id=p.doc_id
JOIN ingestion_jobs j ON j.job_id=@job AND j.tenant_id=p.tenant_id AND j.doc_path=d.doc_path
WHERE p.plan_id=@plan AND p.retired_at IS NULL AND p.manifest_sealed AND p.generation=d.ingestion_version
AND j.action='upsert' AND j.status='running' AND j.locked_by=@owner AND NOT COALESCE((j.payload #>> '{control,cancelRequested}')='true',false));
""", new { plan = planId, job = job.JobId, owner }, cancellationToken: ct));
        if (!allowed) throw new IngestionLeaseLostException();
    }

    // Missing manifests are historical/unmanaged and cannot be GC candidates. A known tombstone
    // must never be bypassed by replaying an old CompleteUpsert call.
    public static async Task<bool> CanPublishAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tenant, Guid doc, int version, string? fingerprint, CancellationToken ct)
        => fingerprint is null || !await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ingestion_vector_plans WHERE tenant_id=@tenant AND doc_id=@doc
AND plan_fingerprint=@fingerprint AND (retired_at IS NOT NULL OR generation<>@version OR NOT manifest_sealed));
""", new { tenant, doc, version, fingerprint }, transaction: tx, cancellationToken: ct));

    public static async Task<int> RetireEligibleAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int limit,
        CancellationToken ct, Guid? tenant = null, Guid? doc = null)
        => await conn.ExecuteAsync(new CommandDefinition("""
WITH eligible AS (
  SELECT p.plan_id FROM ingestion_vector_plans p
  JOIN documents d ON d.tenant_id=p.tenant_id AND d.doc_id=p.doc_id
  JOIN ingestion_generation_watermarks w ON w.tenant_id=p.tenant_id AND w.doc_id=p.doc_id AND w.highest_generation>=d.ingestion_version
  WHERE p.retired_at IS NULL AND p.manifest_sealed AND p.generation<d.ingestion_version
    AND (CAST(@tenant AS uuid) IS NULL OR p.tenant_id=@tenant) AND (CAST(@doc AS uuid) IS NULL OR p.doc_id=@doc)
    AND NOT (d.indexed_version=p.generation AND (d.ingestion_vector_plan_fingerprint IS NULL OR d.ingestion_vector_plan_fingerprint=p.plan_fingerprint))
  ORDER BY p.plan_id FOR UPDATE OF p SKIP LOCKED LIMIT @limit
)
UPDATE ingestion_vector_plans p SET retired_at=now(),retirement_reason='generation_superseded',available_at=now()
FROM eligible e WHERE p.plan_id=e.plan_id;
""", new { tenant, doc, limit = Math.Clamp(limit, 1, 1024) }, transaction: tx, cancellationToken: ct));

    private sealed class ManifestState
    {
        public Guid Id { get; set; }
        public string Target { get; set; } = "";
        public string Manifest { get; set; } = "";
        public int Count { get; set; }
        public DateTime? RetiredAt { get; set; }
        public bool Sealed { get; set; }
    }
}
