using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

internal sealed class VectorGcInventory
{
    public long RegisteredPlans { get; set; }
    public long RetiredPlans { get; set; }
    public long ProtectedPlans { get; set; }
    public long DueRechecks { get; set; }
    public long BlockedTargets { get; set; }
    public long TransportFailures { get; set; }
    public long ProtocolFailures { get; set; }
    public long QuarantinedPlans { get; set; }
    public long UnmanagedActivePublications { get; set; }

    internal const string ReadOnlySql = """
SELECT
 (SELECT count(*) FROM ingestion_vector_plans) AS "RegisteredPlans",
 (SELECT count(*) FROM ingestion_vector_plans WHERE retired_at IS NOT NULL) AS "RetiredPlans",
 (SELECT count(*) FROM ingestion_vector_plans p JOIN documents d ON d.tenant_id=p.tenant_id AND d.doc_id=p.doc_id
   WHERE p.generation>=d.ingestion_version OR (p.generation=d.indexed_version AND
     (d.ingestion_vector_plan_fingerprint IS NULL OR d.ingestion_vector_plan_fingerprint=p.plan_fingerprint))) AS "ProtectedPlans",
 (SELECT count(*) FROM ingestion_vector_plans WHERE retired_at IS NOT NULL AND available_at<=now()) AS "DueRechecks",
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code LIKE 'blocked_target_%') AS "BlockedTargets",
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code LIKE 'read_http_%' OR last_gc_code LIKE 'delete_http_%' OR last_gc_code IN ('operation_failed','operation_timeout')) AS "TransportFailures",
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code IN ('read_response_invalid','response_json_invalid','response_or_manifest_invalid','delete_not_completed','absence_not_verified','reappearance_not_verified')) AS "ProtocolFailures",
 (SELECT count(*) FROM ingestion_vector_plans WHERE NOT manifest_sealed OR last_gc_code IN ('manifest_incomplete','manifest_identity_mismatch','point_identity_mismatch')) AS "QuarantinedPlans",
 (SELECT count(*) FROM documents d WHERE d.indexed_version>0 AND NOT EXISTS (
   SELECT 1 FROM ingestion_vector_plans p WHERE p.tenant_id=d.tenant_id AND p.doc_id=d.doc_id
     AND p.generation=d.indexed_version AND p.plan_fingerprint=d.ingestion_vector_plan_fingerprint)) AS "UnmanagedActivePublications";
""";

    public static async Task<VectorGcInventory> ReadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<VectorGcInventory>(new CommandDefinition(ReadOnlySql, commandTimeout: 5, cancellationToken: ct));
    }
}

internal sealed record VectorGcStateSnapshot(bool WorkerStarted, bool Running, DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastFinishedAt, string LastCode, VectorGcRoundResult? LastRound, VectorGcInventory? Inventory);

internal sealed class VectorGcRuntimeState
{
    private readonly object _gate = new();
    private VectorGcStateSnapshot _snapshot = new(false, false, null, null, "not_started", null, null);
    public VectorGcStateSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public void WorkerStarted(bool enabled) { lock (_gate) _snapshot = _snapshot with { WorkerStarted = true, LastCode = enabled ? "idle" : "disabled" }; }
    public void Begin() { lock (_gate) _snapshot = _snapshot with { Running = true, LastStartedAt = DateTimeOffset.UtcNow, LastCode = "running" }; }
    public void Complete(VectorGcRoundResult result, VectorGcInventory inventory)
    {
        lock (_gate) _snapshot = _snapshot with { Running = false, LastFinishedAt = DateTimeOffset.UtcNow,
            LastCode = result.DeferredPlans > 0 ? "completed_with_deferred" : "completed", LastRound = result, Inventory = inventory };
    }
    public void Fail() { lock (_gate) _snapshot = _snapshot with { Running = false, LastFinishedAt = DateTimeOffset.UtcNow, LastCode = "round_incomplete" }; }
}

internal static class VectorGcReadiness
{
    public static void Apply(IDictionary<string, object?> details, IServiceProvider services)
    {
        details["vector_gc_enabled"] = services.GetService<IOptions<VectorGcOptions>>()?.Value.Enabled ?? false;
        details["vector_gc_state"] = services.GetService<VectorGcRuntimeState>()?.Snapshot;
        details["vector_gc_legacy_inventory_complete"] = false;
        details["vector_gc_scope"] = "registered_exact_manifests_older_generation";
    }
}
