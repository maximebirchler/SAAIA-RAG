using Dapper;
using Npgsql;

internal static class IngestionAutoUpsertGuard
{
    internal sealed class AutoUpsertState
    {
        public string DocPath { get; set; } = "";
        public long? FileSize { get; set; }
        public DateTime? FileMtime { get; set; }
        public string Status { get; set; } = "";
        public int IndexedVersion { get; set; }
        public bool AutoIngestPaused { get; set; }
        public string? AutoIngestPauseReason { get; set; }
        public DateTime? AutoIngestPausedAt { get; set; }
    }

    public static async Task<AutoUpsertState?> LoadAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        CancellationToken ct)
    {
        const string sql = """
SELECT
  doc_path                           AS "DocPath",
  file_size                          AS "FileSize",
  file_mtime                         AS "FileMtime",
  status                             AS "Status",
  COALESCE(indexed_version, 0)       AS "IndexedVersion",
  COALESCE(auto_ingest_paused, false) AS "AutoIngestPaused",
  auto_ingest_pause_reason           AS "AutoIngestPauseReason",
  auto_ingest_paused_at              AS "AutoIngestPausedAt"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
LIMIT 1;
""";

        return await conn.QueryFirstOrDefaultAsync<AutoUpsertState>(
            new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                doc_path = PathUtil.NormalizeRelativePath(docPath)
            }, cancellationToken: ct));
    }

    public static bool ShouldSuppressAutoUpsert(AutoUpsertState? state, long currentFileSize, DateTime currentFileMtimeUtc)
    {
        if (state is null)
            return false;

        if (!state.AutoIngestPaused)
            return false;

        return SameFile(state.FileSize, state.FileMtime, currentFileSize, currentFileMtimeUtc);
    }

    public static bool SameFile(long? dbFileSize, DateTime? dbFileMtime, long currentFileSize, DateTime currentFileMtimeUtc)
    {
        return (dbFileSize ?? -1L) == currentFileSize
            && SameMtime(dbFileMtime, currentFileMtimeUtc);
    }

    private static bool SameMtime(DateTime? dbMtime, DateTime fsMtimeUtc)
    {
        if (dbMtime is null)
            return false;

        DateTime dbUtc;
        if (dbMtime.Value.Kind == DateTimeKind.Utc) dbUtc = dbMtime.Value;
        else if (dbMtime.Value.Kind == DateTimeKind.Local) dbUtc = dbMtime.Value.ToUniversalTime();
        else dbUtc = DateTime.SpecifyKind(dbMtime.Value, DateTimeKind.Utc);

        var fsUtc = DateTime.SpecifyKind(fsMtimeUtc, DateTimeKind.Utc);
        return Math.Abs((dbUtc - fsUtc).TotalSeconds) <= 2.0;
    }
}
