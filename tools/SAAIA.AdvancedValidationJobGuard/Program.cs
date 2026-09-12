using System.Text.Json;
using Npgsql;

const string ConnectionEnvironmentVariable =
    "SAAIA_ADVANCED_VALIDATION_JOB_GUARD_CONNECTION";

var options = ParseArguments(args);
var connectionString = Environment.GetEnvironmentVariable(
    ConnectionEnvironmentVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        $"{ConnectionEnvironmentVariable} is required.");
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await using var transaction = await connection.BeginTransactionAsync();

var observed = await ReadNonterminalJobsAsync(
    connection,
    transaction,
    options.UserId);
var canceledCount = 0;
if (options.Cancel && observed.Count > 0)
{
    const string cancelSql = """
        UPDATE advanced_analysis_jobs
        SET status='canceled',
            cancel_requested_at=COALESCE(cancel_requested_at, now()),
            finished_at=COALESCE(finished_at, now()),
            revision=revision+1,
            updated_at=now(),
            lease_owner=NULL,
            lease_expires_at=NULL
        WHERE user_id=@user_id
          AND status IN ('queued', 'running');
        """;
    await using var cancel = new NpgsqlCommand(
        cancelSql,
        connection,
        transaction);
    cancel.Parameters.AddWithValue("user_id", options.UserId);
    canceledCount = await cancel.ExecuteNonQueryAsync();
}

var remaining = await ReadNonterminalJobsAsync(
    connection,
    transaction,
    options.UserId);
await transaction.CommitAsync();

var report = new
{
    schemaVersion = "saaia-advanced-validation-job-guard-v1",
    checkedAtUtc = DateTimeOffset.UtcNow,
    userId = options.UserId,
    cancelRequested = options.Cancel,
    observedNonterminalCount = observed.Count,
    observed,
    canceledCount,
    remainingNonterminalCount = remaining.Count,
    remaining
};
var outputPath = Path.GetFullPath(options.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var temporaryPath = outputPath + ".tmp";
await File.WriteAllTextAsync(
    temporaryPath,
    JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions { WriteIndented = true }));
File.Move(temporaryPath, outputPath, overwrite: true);

Console.WriteLine(JsonSerializer.Serialize(new
{
    outputPath,
    observedNonterminalCount = observed.Count,
    canceledCount,
    remainingNonterminalCount = remaining.Count
}));
if (remaining.Count > 0)
    Environment.ExitCode = 2;

static async Task<List<object>> ReadNonterminalJobsAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    string userId)
{
    const string sql = """
        SELECT job_id, tenant_id, status, attempt_count, available_at,
               provider_key, provider_model, last_error_code
        FROM advanced_analysis_jobs
        WHERE user_id=@user_id
          AND status IN ('queued', 'running')
        ORDER BY available_at, created_at, job_id;
        """;
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    command.Parameters.AddWithValue("user_id", userId);
    await using var reader = await command.ExecuteReaderAsync();
    var jobs = new List<object>();
    while (await reader.ReadAsync())
    {
        jobs.Add(new
        {
            jobId = reader.GetGuid(0),
            tenantId = reader.GetGuid(1),
            status = reader.GetString(2),
            attemptCount = reader.GetInt32(3),
            availableAtUtc = reader.GetFieldValue<DateTimeOffset>(4),
            providerKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            providerModel = reader.IsDBNull(6) ? null : reader.GetString(6),
            lastErrorCode = reader.IsDBNull(7) ? null : reader.GetString(7)
        });
    }
    return jobs;
}

static (string UserId, string OutputPath, bool Cancel) ParseArguments(
    string[] values)
{
    string? userId = null;
    string? outputPath = null;
    var cancel = false;
    for (var index = 0; index < values.Length; index++)
    {
        if (values[index] == "--user-id" && index + 1 < values.Length)
            userId = values[++index];
        else if (values[index] == "--output" && index + 1 < values.Length)
            outputPath = values[++index];
        else if (values[index] == "--cancel")
            cancel = true;
        else
        {
            throw new InvalidOperationException(
                $"Unknown or incomplete argument: {values[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(userId)
        || string.IsNullOrWhiteSpace(outputPath))
    {
        throw new InvalidOperationException(
            "Usage: --user-id <test-user> --output <json-file> [--cancel]");
    }
    if (userId.Length > 200)
        throw new InvalidOperationException("The validation user id is too long.");
    return (userId.Trim(), Path.GetFullPath(outputPath), cancel);
}
