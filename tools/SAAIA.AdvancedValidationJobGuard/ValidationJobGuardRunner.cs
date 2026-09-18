using System.Text.Json;
using Npgsql;

namespace SAAIA.ValidationTools;

public sealed record OwnedValidationJob(Guid JobId, Guid TenantId, string UserId, string Status,
    int AttemptCount, DateTimeOffset AvailableAtUtc, string? ProviderKey, string? ProviderModel, string? LastErrorCode);

public sealed record ValidationJobGuardResult(IReadOnlyList<string> UserIds,
    IReadOnlyList<OwnedValidationJob> Observed, int CanceledCount, IReadOnlyList<OwnedValidationJob> Remaining)
{
    public bool EmptyOwnerScope => UserIds.Count == 0;
}

public sealed record OwnedValidationToolEventAudit(
    int EventSequence,
    int AttemptCount,
    string ToolName,
    string Status,
    JsonElement Request,
    JsonElement EvidenceReferences,
    IReadOnlyList<string> DegradedRetrievers,
    long ElapsedMilliseconds,
    string? ErrorCode,
    DateTimeOffset CreatedAtUtc);

public sealed record OwnedValidationJobAudit(
    Guid JobId,
    Guid TenantId,
    string UserId,
    string Status,
    int AttemptCount,
    string? ProviderKey,
    string? ProviderModel,
    string? LastErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    JsonElement Handoff,
    JsonElement? Result,
    JsonElement? ResearchCheckpoint,
    int ToolEventCount,
    IReadOnlyList<OwnedValidationToolEventAudit> ToolEvents);

public sealed record ValidationJobAuditResult(
    IReadOnlyList<string> UserIds,
    IReadOnlyList<OwnedValidationJobAudit> Jobs)
{
    public bool EmptyOwnerScope => UserIds.Count == 0;
    public int ToolEventCount => Jobs.Sum(job => job.ToolEvents.Count);
    public int ResearchCheckpointCount => Jobs.Count(job => job.ResearchCheckpoint.HasValue);
}

public static class ValidationJobGuardRunner
{
    public static string[] ReadOwnerManifest(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Owner manifest path must be absolute.");
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (++lines > 10000) throw new InvalidOperationException("Owner manifest exceeds its bound.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonSerializer.Deserialize<string>(line);
            if (!Guid.TryParseExact(raw, "D", out var owner) || owner == Guid.Empty)
                throw new InvalidOperationException("Owner manifest must contain only non-empty GUID JSON strings.");
            owners.Add(owner.ToString("D"));
        }
        return owners.Order(StringComparer.Ordinal).ToArray();
    }

    public static async Task<ValidationJobGuardResult> RunAsync(NpgsqlConnection connection,
        IReadOnlyList<string> userIds, bool cancel)
    {
        var owners = userIds.Distinct(StringComparer.Ordinal).ToArray();
        if (cancel && owners.Any(owner => !Guid.TryParseExact(owner, "D", out var id) || id == Guid.Empty))
            throw new InvalidOperationException("Cancellation requires recorded non-empty GUID owners.");
        if (owners.Length == 0) return new(owners, [], 0, []); // Empty scope is never a global queue assertion.
        await using var transaction = await connection.BeginTransactionAsync();
        if (!cancel)
        {
            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync();
        }
        var observed = await ReadAsync(connection, transaction, owners, cancel);
        var canceled = 0;
        if (cancel && observed.Count > 0)
        {
            const string sql = """
                UPDATE advanced_analysis_jobs
                SET status='canceled', cancel_requested_at=COALESCE(cancel_requested_at,now()),
                    finished_at=COALESCE(finished_at,now()), revision=revision+1, updated_at=now(),
                    lease_owner=NULL, lease_expires_at=NULL
                WHERE job_id=ANY(@job_ids) AND user_id=ANY(@user_ids)
                  AND status IN ('queued','running');
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("job_ids", observed.Select(j => j.JobId).ToArray());
            command.Parameters.AddWithValue("user_ids", owners);
            canceled = await command.ExecuteNonQueryAsync();
        }
        var remaining = await ReadAsync(connection, transaction, owners, false);
        await transaction.CommitAsync();
        return new(owners, observed, canceled, remaining);
    }

    public static async Task<ValidationJobAuditResult> ReadAuditAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> userIds)
    {
        var owners = userIds.Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Any(owner =>
                !Guid.TryParseExact(owner, "D", out var id) || id == Guid.Empty))
        {
            throw new InvalidOperationException(
                "Validation job audit requires recorded non-empty GUID owners.");
        }
        if (owners.Length == 0)
            return new(owners, []);

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var readOnly = new NpgsqlCommand(
                         "SET TRANSACTION READ ONLY",
                         connection,
                         transaction))
        {
            await readOnly.ExecuteNonQueryAsync();
        }

        const string jobsSql = """
            SELECT job_id, tenant_id, user_id, status, attempt_count,
                   provider_key, provider_model, last_error_code,
                   created_at, finished_at, handoff::text, result::text,
                   research_checkpoint::text, tool_event_count
            FROM advanced_analysis_jobs
            WHERE user_id=ANY(@user_ids)
            ORDER BY created_at, job_id
            LIMIT 1001;
            """;
        var jobRows = new List<AuditJobRow>();
        await using (var command = new NpgsqlCommand(jobsSql, connection, transaction))
        {
            command.Parameters.AddWithValue("user_ids", owners);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                jobRows.Add(new AuditJobRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.IsDBNull(9)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetInt32(13)));
            }
        }
        if (jobRows.Count > 1000)
            throw new InvalidOperationException(
                "Validation job audit exceeds its job bound.");

        var events = new Dictionary<Guid, List<OwnedValidationToolEventAudit>>();
        var jobIds = jobRows.Select(row => row.JobId).ToArray();
        if (jobIds.Length > 0)
        {
            const string eventsSql = """
                SELECT job_id, event_sequence, attempt_count, tool_name, status,
                       request::text, evidence_references::text,
                       degraded_retrievers, elapsed_milliseconds, error_code,
                       created_at
                FROM advanced_analysis_tool_events
                WHERE job_id=ANY(@job_ids)
                ORDER BY job_id, event_sequence
                LIMIT 32769;
                """;
            var eventCount = 0;
            await using var command = new NpgsqlCommand(
                eventsSql,
                connection,
                transaction);
            command.Parameters.AddWithValue("job_ids", jobIds);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (++eventCount > 32768)
                    throw new InvalidOperationException(
                        "Validation job audit exceeds its tool-event bound.");
                var jobId = reader.GetGuid(0);
                if (!events.TryGetValue(jobId, out var jobEvents))
                {
                    jobEvents = [];
                    events.Add(jobId, jobEvents);
                }
                jobEvents.Add(new OwnedValidationToolEventAudit(
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseJson(reader.GetString(5)),
                    ParseJson(reader.GetString(6)),
                    reader.GetFieldValue<string[]>(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        await transaction.CommitAsync();
        return new ValidationJobAuditResult(
            owners,
            jobRows.Select(row => new OwnedValidationJobAudit(
                row.JobId,
                row.TenantId,
                row.UserId,
                row.Status,
                row.AttemptCount,
                row.ProviderKey,
                row.ProviderModel,
                row.LastErrorCode,
                row.CreatedAtUtc,
                row.FinishedAtUtc,
                ParseJson(row.HandoffJson),
                ParseOptionalJson(row.ResultJson),
                ParseOptionalJson(row.ResearchCheckpointJson),
                row.ToolEventCount,
                events.TryGetValue(row.JobId, out var jobEvents)
                    ? jobEvents
                    : [])).ToArray());
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement? ParseOptionalJson(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : ParseJson(json);

    private static async Task<List<OwnedValidationJob>> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string[] owners, bool lockRows)
    {
        const string sql = """
            SELECT job_id,tenant_id,user_id,status,attempt_count,available_at,
                   provider_key,provider_model,last_error_code
            FROM advanced_analysis_jobs WHERE user_id=ANY(@user_ids)
              AND status IN ('queued','running')
            ORDER BY available_at,created_at,job_id
            """;
        await using var command = new NpgsqlCommand(sql + (lockRows ? " FOR UPDATE;" : ";"), connection, transaction);
        command.Parameters.AddWithValue("user_ids", owners);
        await using var reader = await command.ExecuteReaderAsync();
        var jobs = new List<OwnedValidationJob>();
        while (await reader.ReadAsync()) jobs.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return jobs;
    }

    private sealed record AuditJobRow(
        Guid JobId,
        Guid TenantId,
        string UserId,
        string Status,
        int AttemptCount,
        string? ProviderKey,
        string? ProviderModel,
        string? LastErrorCode,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? FinishedAtUtc,
        string HandoffJson,
        string? ResultJson,
        string? ResearchCheckpointJson,
        int ToolEventCount);
}
