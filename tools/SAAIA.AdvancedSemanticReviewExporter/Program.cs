using System.Data;
using System.Text.Json;
using Npgsql;

const string ConnectionEnvironmentVariable = "SAAIA_SEMANTIC_REVIEW_CONNECTION";

var options = ParseArguments(args);
var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");

var jobIdsJson = await File.ReadAllTextAsync(options.JobIdsPath);
var jobIdStrings = JsonSerializer.Deserialize<string[]>(jobIdsJson) ?? [];
var requestedJobIds = jobIdStrings
    .Select(value => Guid.TryParse(value, out var parsed)
        ? parsed
        : throw new InvalidOperationException("The job-id file contains an invalid UUID."))
    .Distinct()
    .Order()
    .ToArray();
if (requestedJobIds.Length == 0)
    throw new InvalidOperationException("The job-id file is empty.");

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
    await readOnly.ExecuteNonQueryAsync();

var jobs = new List<object>();
var tenantIds = new HashSet<Guid>();
var chunkIds = new HashSet<Guid>();
var contentCardIds = new HashSet<Guid>();

const string jobSql = """
SELECT job_id, tenant_id, status, result::text, provider_key, provider_model,
       last_error_code, revision, attempt_count, created_at, finished_at
FROM advanced_analysis_jobs
WHERE job_id=ANY(@ids)
ORDER BY job_id;
""";
await using (var command = new NpgsqlCommand(jobSql, connection, transaction))
{
    command.Parameters.AddWithValue("ids", requestedJobIds);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var jobId = reader.GetGuid(0);
        var tenantId = reader.GetGuid(1);
        var status = reader.GetString(2);
        if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Job {jobId:D} is not succeeded.");

        JsonElement result;
        if (reader.IsDBNull(3))
            throw new InvalidOperationException($"Job {jobId:D} has no result.");
        using (var document = JsonDocument.Parse(reader.GetString(3)))
        {
            result = document.RootElement.Clone();
            if (result.TryGetProperty("evidence", out var evidence)
                && evidence.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in evidence.EnumerateArray())
                {
                    AddUuid(item, "chunkId", chunkIds);
                    AddUuid(item, "contentCardId", contentCardIds);
                }
            }
        }

        tenantIds.Add(tenantId);
        jobs.Add(new
        {
            jobId,
            tenantId,
            status,
            result,
            providerKey = reader.IsDBNull(4) ? null : reader.GetString(4),
            providerModel = reader.IsDBNull(5) ? null : reader.GetString(5),
            lastErrorCode = reader.IsDBNull(6) ? null : reader.GetString(6),
            revision = reader.GetInt32(7),
            attemptCount = reader.GetInt32(8),
            createdAtUtc = reader.GetFieldValue<DateTimeOffset>(9),
            finishedAtUtc = reader.IsDBNull(10)
                ? (DateTimeOffset?)null
                : reader.GetFieldValue<DateTimeOffset>(10)
        });
    }
}

if (jobs.Count != requestedJobIds.Length)
    throw new InvalidOperationException(
        $"Requested {requestedJobIds.Length} jobs but found {jobs.Count}.");
if (tenantIds.Count != 1)
    throw new InvalidOperationException("All reviewed jobs must belong to one tenant.");

var chunks = new List<object>();
if (chunkIds.Count > 0)
{
    const string chunkSql = """
SELECT retrieval_chunk_id, revision_id, page_start, page_end, text_content,
       encode(COALESCE(checksum, ''::bytea), 'hex')
FROM retrieval_chunks
WHERE tenant_id=@tenant AND retrieval_chunk_id=ANY(@ids)
ORDER BY retrieval_chunk_id;
""";
    await using var command = new NpgsqlCommand(chunkSql, connection, transaction);
    command.Parameters.AddWithValue("tenant", tenantIds.Single());
    command.Parameters.AddWithValue("ids", chunkIds.Order().ToArray());
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        chunks.Add(new
        {
            chunkId = reader.GetGuid(0),
            revisionId = reader.GetGuid(1),
            pageStart = reader.GetInt32(2),
            pageEnd = reader.GetInt32(3),
            text = reader.GetString(4),
            checksum = reader.GetString(5)
        });
}

var contentCards = new List<object>();
if (contentCardIds.Count > 0)
{
    const string contentCardSql = """
SELECT content_card_id, revision_id, page_start, page_end, title, search_text,
       metadata::text, encode(checksum, 'hex')
FROM document_profile_content_cards
WHERE tenant_id=@tenant AND content_card_id=ANY(@ids)
ORDER BY content_card_id;
""";
    await using var command = new NpgsqlCommand(contentCardSql, connection, transaction);
    command.Parameters.AddWithValue("tenant", tenantIds.Single());
    command.Parameters.AddWithValue("ids", contentCardIds.Order().ToArray());
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        using var metadataDocument = JsonDocument.Parse(reader.GetString(6));
        contentCards.Add(new
        {
            contentCardId = reader.GetGuid(0),
            revisionId = reader.GetGuid(1),
            pageStart = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            pageEnd = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
            title = reader.GetString(4),
            searchText = reader.GetString(5),
            metadata = metadataDocument.RootElement.Clone(),
            checksum = reader.GetString(7)
        });
    }
}

var toolEvents = new List<object>();
const string eventSql = """
SELECT job_id, event_sequence, attempt_count, tool_name, status, request::text,
       evidence_references::text, degraded_retrievers, elapsed_milliseconds,
       error_code, created_at
FROM advanced_analysis_tool_events
WHERE tenant_id=@tenant AND job_id=ANY(@ids)
ORDER BY job_id, event_sequence;
""";
await using (var command = new NpgsqlCommand(eventSql, connection, transaction))
{
    command.Parameters.AddWithValue("tenant", tenantIds.Single());
    command.Parameters.AddWithValue("ids", requestedJobIds);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        using var requestDocument = JsonDocument.Parse(reader.GetString(5));
        using var evidenceDocument = JsonDocument.Parse(reader.GetString(6));
        toolEvents.Add(new
        {
            jobId = reader.GetGuid(0),
            sequence = reader.GetInt32(1),
            attemptCount = reader.GetInt32(2),
            toolName = reader.GetString(3),
            status = reader.GetString(4),
            request = requestDocument.RootElement.Clone(),
            evidence = evidenceDocument.RootElement.Clone(),
            degradedRetrievers = reader.GetFieldValue<string[]>(7),
            elapsedMilliseconds = reader.GetInt64(8),
            errorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
            createdAtUtc = reader.GetFieldValue<DateTimeOffset>(10)
        });
    }
}

var bundle = new
{
    schemaVersion = "saaia-advanced-semantic-review-private-bundle-v1",
    exportedAtUtc = DateTimeOffset.UtcNow,
    transaction = "REPEATABLE READ ONLY",
    requestedJobCount = requestedJobIds.Length,
    tenantCount = tenantIds.Count,
    jobs,
    chunks,
    contentCards,
    toolEvents
};

var outputPath = Path.GetFullPath(options.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var temporaryPath = outputPath + ".tmp";
await File.WriteAllTextAsync(
    temporaryPath,
    JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
File.Move(temporaryPath, outputPath, overwrite: true);
await transaction.RollbackAsync();
Console.WriteLine(JsonSerializer.Serialize(new
{
    outputPath,
    jobs = jobs.Count,
    chunks = chunks.Count,
    contentCards = contentCards.Count,
    toolEvents = toolEvents.Count,
    transaction = "REPEATABLE READ ONLY"
}));

static void AddUuid(JsonElement parent, string propertyName, HashSet<Guid> target)
{
    if (parent.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && Guid.TryParse(property.GetString(), out var value))
        target.Add(value);
}

static (string JobIdsPath, string OutputPath) ParseArguments(string[] values)
{
    string? jobIdsPath = null;
    string? outputPath = null;
    for (var i = 0; i < values.Length; i++)
    {
        if (values[i] == "--job-ids" && i + 1 < values.Length)
            jobIdsPath = values[++i];
        else if (values[i] == "--output" && i + 1 < values.Length)
            outputPath = values[++i];
        else
            throw new InvalidOperationException($"Unknown or incomplete argument: {values[i]}");
    }

    if (string.IsNullOrWhiteSpace(jobIdsPath) || string.IsNullOrWhiteSpace(outputPath))
        throw new InvalidOperationException("Usage: --job-ids <json-file> --output <json-file>");
    if (!File.Exists(jobIdsPath))
        throw new FileNotFoundException("Job-id file not found.", jobIdsPath);
    return (Path.GetFullPath(jobIdsPath), Path.GetFullPath(outputPath));
}
