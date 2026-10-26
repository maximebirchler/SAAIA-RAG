using Dapper;
using Npgsql;

/// <summary>A durable floor independent of ordinary document/tenant retention.</summary>
internal static class IngestionGenerationRegistry
{
    public static async Task<Guid> ResolveDocumentIdAsync(NpgsqlConnection connection, Guid tenantId,
        string path, Guid fallback, CancellationToken ct)
        => await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT doc_id FROM documents WHERE tenant_id=@tenant AND doc_path=@path",
            new { tenant = tenantId, path }, cancellationToken: ct)) ?? fallback;

    public static async Task<int> ReserveAsync(NpgsqlConnection connection, Guid tenantId, Guid docId, CancellationToken ct)
    {
        // This single short statement commits before document/job locks. Gaps are safe; reusing a
        // generation is not. The document admission must CAS against this reservation and retry
        // with a new reservation if another action has already advanced the document.
        const string sql = """
INSERT INTO ingestion_generation_watermarks(tenant_id,doc_id,highest_generation)
VALUES(@tenant,@doc,1::bigint+COALESCE((SELECT GREATEST(COALESCE(ingestion_version,0),COALESCE(indexed_version,0))
  FROM documents WHERE tenant_id=@tenant AND doc_id=@doc),0))
ON CONFLICT (tenant_id,doc_id) DO UPDATE
SET highest_generation=GREATEST(ingestion_generation_watermarks.highest_generation,
  EXCLUDED.highest_generation-1)+1, updated_at=now()
WHERE GREATEST(ingestion_generation_watermarks.highest_generation,EXCLUDED.highest_generation-1)<2147483647
RETURNING highest_generation;
""";
        var generation = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(sql,
            new { tenant = tenantId, doc = docId }, cancellationToken: ct));
        if (!generation.HasValue || generation.Value > int.MaxValue) throw new InvalidOperationException("ingestion_generation_exhausted");
        return (int)generation.Value;
    }
}
