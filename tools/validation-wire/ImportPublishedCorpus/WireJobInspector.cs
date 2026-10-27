using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;

internal static class WireJobInspector
{
    internal static async Task RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Invalid inspect arguments.");
        if (!values.Keys.Order().SequenceEqual(new[] { "--owner", "--output", "--job-id" }.Order()))
            throw new ArgumentException("Expected owned runtime, new output and job-id.");
        var ownerPath = Path.GetFullPath(values["--owner"]);
        var root = Path.GetDirectoryName(ownerPath)!;
        var output = Path.GetFullPath(values["--output"]);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new InvalidOperationException("Inspection output must be new and inside the owned fixture.");
        using var ownerJson = JsonDocument.Parse(File.ReadAllText(ownerPath));
        var owner = ownerJson.RootElement;
        if (owner.GetProperty("schemaVersion").GetString() != "saaia-winui-local-runtime.v1"
            || Path.GetFullPath(owner.GetProperty("outputDirectory").GetString()!) != root
            || owner.GetProperty("state").GetString() != "ready") throw new InvalidOperationException("Unknown runtime ownership.");
        using var configJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime", "deployment.config.json")));
        var config = configJson.RootElement;
        var builder = new NpgsqlConnectionStringBuilder(config.GetProperty("Database").GetProperty("ConnectionString").GetString());
        if (builder.Host != "127.0.0.1" || builder.Database != "postgres" || builder.Port == 5432
            || builder.Port < 1024 || builder.Port != owner.GetProperty("pgPort").GetInt32())
            throw new InvalidOperationException("Unknown SQL fixture.");
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        if (!string.Equals(Path.GetFullPath((await connection.ExecuteScalarAsync<string>("SHOW data_directory"))!),
                Path.GetFullPath(owner.GetProperty("postgresDataDirectory").GetString()!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected PostgreSQL data directory.");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);
        var tenant = config.GetProperty("Bootstrap").GetProperty("TenantId").GetGuid();
        var jobId = Guid.Parse(values["--job-id"]);
        var json = await connection.QuerySingleAsync<string>("""
            SELECT jsonb_build_object('schemaVersion','saaia-wire-job-sql-v1','jobId',j.job_id,
              'status',j.status,'attemptCount',j.attempt_count,'handoff',j.handoff,'result',j.result,
              'lastErrorCode',j.last_error_code,'providerKey',j.provider_key,
              'events',COALESCE((SELECT jsonb_agg(jsonb_build_object('sequence',e.event_sequence,
                  'status',e.status,'request',e.request,'evidence',e.evidence_references,
                  'errorCode',e.error_code) ORDER BY e.event_sequence)
                FROM advanced_analysis_tool_events e WHERE e.tenant_id=j.tenant_id AND e.job_id=j.job_id),'[]'::jsonb),
              'corpusDigest',(SELECT md5(string_agg(revision_id::text||':'||retrieval_chunk_id::text||':'||
                  encode(checksum,'hex')||':'||text_content||':'||COALESCE(metadata::text,''),','
                  ORDER BY revision_id,chunk_index)) FROM retrieval_chunks WHERE tenant_id=j.tenant_id),
              'canonicalHashes',(SELECT jsonb_agg(jsonb_build_object('revisionId',revision_id,
                  'sha256',encode(content_hash,'hex')) ORDER BY revision_id)
                  FROM document_revision_binary_artifacts WHERE tenant_id=j.tenant_id))::text
            FROM advanced_analysis_jobs j WHERE j.tenant_id=@tenant AND j.job_id=@jobId
            """, new { tenant, jobId }, transaction);
        await transaction.CommitAsync();
        await File.WriteAllTextAsync(output, json);
        Console.WriteLine("Read-only snapshot captured for the exact owned job.");
    }
}
