using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

// Replays two exact, already captured publications in an owned loopback fixture.
// This is explicitly SQL seeding, not another ingestion or a PDF fidelity claim.
if (args.FirstOrDefault() == "inspect-job")
{
    await WireJobInspector.RunAsync(args.Skip(1).ToArray());
    return;
}
var argsMap = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)
        || !argsMap.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Invalid arguments.");
}
if (!argsMap.Keys.Order().SequenceEqual(new[] { "--capture", "--capture-sha256", "--output", "--owner" }.Order()))
    throw new ArgumentException("Expected owner, capture, capture-sha256 and output.");
string Required(string name) => Path.GetFullPath(argsMap[name]);
var ownerPath = Required("--owner");
var root = Path.GetDirectoryName(ownerPath)!;
var output = Required("--output");
if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || File.Exists(output)) throw new InvalidOperationException("Evidence output must be new and inside the owned runtime.");
using var ownerJson = JsonDocument.Parse(File.ReadAllText(ownerPath));
var owner = ownerJson.RootElement;
if (owner.GetProperty("schemaVersion").GetString() != "saaia-winui-local-runtime.v1"
    || !string.Equals(Path.GetFullPath(owner.GetProperty("outputDirectory").GetString()!), root, StringComparison.OrdinalIgnoreCase)
    || owner.GetProperty("state").GetString() != "ready") throw new InvalidOperationException("Not a ready owned runtime.");
var pgPort = owner.GetProperty("pgPort").GetInt32();
if (pgPort < 1024 || pgPort == 5432) throw new InvalidOperationException("Refusing ordinary PostgreSQL port.");
var capturePath = Required("--capture");
var captureBytes = File.ReadAllBytes(capturePath);
var captureHash = Convert.ToHexString(SHA256.HashData(captureBytes)).ToLowerInvariant();
if (!string.Equals(captureHash, argsMap["--capture-sha256"], StringComparison.Ordinal))
    throw new InvalidDataException("Captured publication SHA256 differs.");
using var captureJson = JsonDocument.Parse(captureBytes);
var rows = captureJson.RootElement.GetProperty("documents").EnumerateArray()
    .Where(row => row.GetProperty("docPath").GetString() is "nist-complex.pdf" or "recipe-real-v1.pdf").ToArray();
if (rows.Length != 2 || rows.Select(row => row.GetProperty("docPath").GetString()).Distinct().Count() != 2)
    throw new InvalidDataException("The exact two captured publications are required.");
using var configJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime", "deployment.config.json")));
var connectionString = configJson.RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString()!;
var builder = new NpgsqlConnectionStringBuilder(connectionString);
if (builder.Host != "127.0.0.1" || builder.Port != pgPort || builder.Database != "postgres")
    throw new InvalidOperationException("Database is not the owned loopback fixture.");
var tenant = configJson.RootElement.GetProperty("Bootstrap").GetProperty("TenantId").GetGuid();
// Original source27 qualification used this same tenant; no document/revision ID mapping.
if (tenant != Guid.Parse("77777777-7777-4777-8777-777777777777"))
    throw new InvalidDataException("Qualification tenant differs from the captured cohort.");
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
var actualDirectory = await connection.ExecuteScalarAsync<string>("SHOW data_directory");
if (!string.Equals(Path.GetFullPath(actualDirectory!),
        Path.GetFullPath(owner.GetProperty("postgresDataDirectory").GetString()!), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("PostgreSQL data directory ownership differs.");
if (await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM documents") != 0
    || await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM advanced_analysis_jobs") != 0)
    throw new InvalidOperationException("Import requires an empty owned corpus and job queue.");
var summary = new List<object>();
await using var transaction = await connection.BeginTransactionAsync();
foreach (var row in rows)
{
    var doc = row.GetProperty("docId").GetGuid();
    var revision = row.GetProperty("revisionId").GetGuid();
    var path = row.GetProperty("docPath").GetString()!;
    var sourceHash = Convert.FromHexString(row.GetProperty("sourceSha256").GetString()!);
    var artifacts = row.GetProperty("binaryArtifacts").EnumerateArray().ToArray();
    if (artifacts.Length != 1) throw new InvalidDataException("One canonical artifact required.");
    var sourceArtifact = artifacts[0];
    var artifact = new CanonicalCompressedArtifact(sourceArtifact.GetProperty("artifactType").GetString()!,
        sourceArtifact.GetProperty("schemaVersion").GetString()!,
        Convert.FromHexString(sourceArtifact.GetProperty("contentHash").GetString()!),
        sourceArtifact.GetProperty("byteSize").GetInt64(),
        Convert.FromBase64String(sourceArtifact.GetProperty("payloadBase64").GetString()!));
    if (sourceArtifact.GetProperty("compression").GetString() != "gzip"
        || artifact.Payload.LongLength != sourceArtifact.GetProperty("storedSize").GetInt64())
        throw new InvalidDataException("Stored canonical payload differs.");
    var bundle = CanonicalArtifactRepo.ReadBundleArtifact(artifact);
    var rebuilt = CanonicalArtifactRepo.BuildBundleArtifact(bundle);
    if (bundle.Document.DocumentId != doc || bundle.Document.RevisionId != revision
        || bundle.Document.Source.Sha256 != row.GetProperty("sourceSha256").GetString()
        || !rebuilt.ContentHash.SequenceEqual(artifact.ContentHash) || rebuilt.ByteSize != artifact.ByteSize)
        throw new InvalidDataException("Canonical reserialization or captured identities differ.");
    await connection.ExecuteAsync("""
        INSERT INTO documents(tenant_id,doc_id,doc_path,doc_name,category,content_hash,file_size,mime_type,
          page_count,status,ingestion_version,indexed_version,last_ingested_at)
        VALUES(@tenant,@doc,@path,@path,'wire-qualification',@sourceHash,@size,'application/pdf',
          @pages,'indexed',1,1,now());
        INSERT INTO document_revisions(revision_id,tenant_id,doc_id,doc_path,source_hash,source_size,
          ingestion_version,indexed_version) VALUES(@revision,@tenant,@doc,@path,@sourceHash,@size,1,1);
        """, new { tenant, doc, path, sourceHash, revision, size = bundle.Document.Source.SizeBytes,
            pages = bundle.Document.PageCount }, transaction);
    var chunks = row.GetProperty("chunks").EnumerateArray().ToArray();
    foreach (var chunk in chunks)
    {
        var text = chunk.GetProperty("text").GetString()!;
        var checksum = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        if (Convert.ToHexString(checksum).ToLowerInvariant() != chunk.GetProperty("checksumSha256").GetString())
            throw new InvalidDataException("Captured chunk UTF8 checksum differs.");
        await connection.ExecuteAsync("""
            INSERT INTO retrieval_chunks(retrieval_chunk_id,tenant_id,revision_id,chunk_index,page_start,page_end,
                text_content,token_count,checksum,metadata)
            VALUES(@chunkId,@tenant,@revision,@ordinal,@first,@last,@text,@tokens,@checksum,CAST(@metadata AS jsonb));
            """, new { chunkId = chunk.GetProperty("retrievalChunkId").GetGuid(), tenant, revision,
            ordinal = chunk.GetProperty("chunkIndex").GetInt32(), first = chunk.GetProperty("pageStart").GetInt32(),
            last = chunk.GetProperty("pageEnd").GetInt32(), text, tokens = Math.Max(1, text.Length / 4), checksum,
            metadata = chunk.GetProperty("metadata").GetRawText() }, transaction);
    }
    foreach (var page in bundle.Document.Pages)
    {
        var text = string.Join("\n", chunks.Where(chunk => chunk.GetProperty("pageStart").GetInt32() <= page.PageNumber
            && chunk.GetProperty("pageEnd").GetInt32() >= page.PageNumber).Select(chunk => chunk.GetProperty("text").GetString()));
        await connection.ExecuteAsync("""
            INSERT INTO document_page_index(tenant_id,revision_id,page_number,char_count,checksum)
            VALUES(@tenant,@revision,@page,@count,@checksum);
            """, new { tenant, revision, page = page.PageNumber, count = text.Length,
                checksum = SHA256.HashData(Encoding.UTF8.GetBytes(text)) }, transaction);
    }
    await CanonicalArtifactRepo.UpsertBundleAsync(connection, transaction, tenant, revision, bundle, CancellationToken.None);
    var storedHash = await connection.ExecuteScalarAsync<byte[]>("""
        SELECT content_hash FROM document_revision_binary_artifacts
        WHERE tenant_id=@tenant AND revision_id=@revision AND artifact_type='canonical_ingestion_bundle'
        """, new { tenant, revision }, transaction);
    if (storedHash is null || !storedHash.SequenceEqual(artifact.ContentHash))
        throw new InvalidDataException("Replayed canonical hash differs after publication.");
    var readback = (await connection.QueryAsync<(Guid Id, string Text, byte[] Hash)>("""
        SELECT retrieval_chunk_id AS Id,text_content AS Text,checksum AS Hash FROM retrieval_chunks
        WHERE tenant_id=@tenant AND revision_id=@revision ORDER BY chunk_index
        """, new { tenant, revision }, transaction)).ToArray();
    if (readback.Length != chunks.Length || readback.Where((item, index) =>
        item.Id != chunks[index].GetProperty("retrievalChunkId").GetGuid()
        || item.Text != chunks[index].GetProperty("text").GetString()
        || !item.Hash.SequenceEqual(Convert.FromHexString(chunks[index].GetProperty("checksumSha256").GetString()!))).Any())
        throw new InvalidDataException("Replayed SQL chunk identity or content differs.");
    summary.Add(new { docPath = path, docId = doc, revisionId = revision,
        sourceSha256 = Convert.ToHexString(sourceHash).ToLowerInvariant(), chunks = chunks.Length,
        anchors = bundle.SourceAnchors.Count, canonicalHash = Convert.ToHexString(storedHash).ToLowerInvariant(),
        canonicalBytes = artifact.ByteSize, exactPrimaryChunkReadback = true, exactCanonicalBytes = true,
        pageIndexDerivedForFixture = true, tokenCountsEstimatedForFixture = true });
}
await transaction.CommitAsync();
var corpusDigest = await connection.ExecuteScalarAsync<string>("""
    SELECT md5(string_agg(revision_id::text||':'||retrieval_chunk_id::text||':'||
        encode(checksum,'hex')||':'||text_content||':'||COALESCE(metadata::text,''),','
        ORDER BY revision_id,chunk_index)) FROM retrieval_chunks WHERE tenant_id=@tenant
    """, new { tenant });
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { schemaVersion = "saaia-wire-corpus-replay-v1",
    capturedAtUtc = DateTimeOffset.UtcNow, sourceCaptureSha256 = captureHash, documents = summary,
    corpusDigest,
    tenantId = tenant, documentAndRevisionIdsMapped = false, canonicalRowsSeeded = true,
    realIngestionExecuted = false, source27IngestionProofRemainsSeparate = true,
    existingDatabaseModified = false, productStatus = "TESTE_NON_APPROUVE" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Replayed two exact captured publications into owned loopback PostgreSQL.");
