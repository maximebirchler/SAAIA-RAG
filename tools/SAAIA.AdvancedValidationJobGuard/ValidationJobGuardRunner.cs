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
}
