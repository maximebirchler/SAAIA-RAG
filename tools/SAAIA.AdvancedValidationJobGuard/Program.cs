using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace SAAIA.ValidationTools;

internal static class JobGuardCli
{
    private static async Task<int> Main(string[] args)
    {
        string? legacyUser = null, manifest = null, output = null, auditOutput = null;
        var cancel = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--user-id" && i + 1 < args.Length) legacyUser = args[++i];
            else if (args[i] == "--owner-ids-path" && i + 1 < args.Length) manifest = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else if (args[i] == "--audit-output" && i + 1 < args.Length) auditOutput = args[++i];
            else if (args[i] == "--cancel") cancel = true;
            else throw new InvalidOperationException("Unknown or incomplete job-guard argument.");
        }
        if (string.IsNullOrWhiteSpace(output) || (string.IsNullOrWhiteSpace(legacyUser) == string.IsNullOrWhiteSpace(manifest)))
            throw new InvalidOperationException("Usage: (--owner-ids-path <absolute JSONL file> | --user-id <legacy owner>) --output <JSON file> [--cancel] [--audit-output <private JSON file>]");
        if (legacyUser?.Length > 200) throw new InvalidOperationException("The validation user id is too long.");
        if (cancel && manifest is null) throw new InvalidOperationException("Canceling validation jobs requires the exact owner manifest; legacy user lookup is read-only.");
        if (auditOutput is not null && manifest is null) throw new InvalidOperationException("Auditing validation jobs requires the exact owner manifest; legacy user lookup is not accepted.");
        var owners = manifest is not null ? ValidationJobGuardRunner.ReadOwnerManifest(manifest) : [legacyUser!.Trim()];
        var connectionString = Environment.GetEnvironmentVariable("SAAIA_ADVANCED_VALIDATION_JOB_GUARD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("SAAIA_ADVANCED_VALIDATION_JOB_GUARD_CONNECTION is required.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var result = await ValidationJobGuardRunner.RunAsync(connection, owners, cancel);
        ValidationJobAuditResult? audit = null;
        string? auditPath = null, auditSha256 = null;
        if (auditOutput is not null)
        {
            audit = await ValidationJobGuardRunner.ReadAuditAsync(connection, owners);
            auditPath = Path.GetFullPath(auditOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            var privateAudit = new
            {
                schemaVersion = "saaia-advanced-validation-private-job-audit-v1",
                capturedAtUtc = DateTimeOffset.UtcNow,
                containsPrivateCorpusMetadata = true,
                mustNotCommit = true,
                ownerManifestPath = manifest,
                emptyOwnerScope = audit.EmptyOwnerScope,
                userIds = audit.UserIds,
                jobs = audit.Jobs
            };
            var auditTemporary = auditPath + ".tmp";
            await File.WriteAllTextAsync(
                auditTemporary,
                JsonSerializer.Serialize(privateAudit, JsonOptions));
            File.Move(auditTemporary, auditPath, overwrite: true);
            auditSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(auditPath)));
        }
        var report = new
        {
            schemaVersion = "saaia-advanced-validation-job-guard-v2",
            checkedAtUtc = DateTimeOffset.UtcNow,
            userId = legacyUser,
            userIds = result.UserIds,
            ownerManifestPath = manifest,
            emptyOwnerScope = result.EmptyOwnerScope,
            globalQueueAudit = false,
            cancelRequested = cancel,
            observedNonterminalCount = result.Observed.Count,
            observed = result.Observed,
            canceledCount = result.CanceledCount,
            remainingNonterminalCount = result.Remaining.Count,
            remaining = result.Remaining,
            privateAuditCaptured = audit is not null,
            privateAuditPath = auditPath,
            privateAuditSha256 = auditSha256,
            auditedJobCount = audit?.Jobs.Count ?? 0,
            auditedToolEventCount = audit?.ToolEventCount ?? 0,
            auditedResearchCheckpointCount = audit?.ResearchCheckpointCount ?? 0
        };
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporary, outputPath, overwrite: true);
        Console.WriteLine(JsonSerializer.Serialize(new { outputPath, ownerCount = owners.Length, emptyOwnerScope = result.EmptyOwnerScope, observedNonterminalCount = result.Observed.Count, canceledCount = result.CanceledCount, remainingNonterminalCount = result.Remaining.Count, privateAuditCaptured = audit is not null, auditedJobCount = audit?.Jobs.Count ?? 0, auditedToolEventCount = audit?.ToolEventCount ?? 0, auditedResearchCheckpointCount = audit?.ResearchCheckpointCount ?? 0 }));
        return result.Remaining.Count > 0 ? 2 : 0;
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
