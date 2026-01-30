using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

sealed class JobCanceledException : Exception
{
    public string Reason { get; }
    public JobCanceledException(string reason) : base(reason) => Reason = reason;
}

sealed record IngestionJob(Guid JobId, Guid TenantId, string Action, string DocPath, string? Category, Guid DocId, int Version);

sealed class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<IngestionWorker> _log;

    public IngestionWorker(IServiceProvider sp, ILogger<IngestionWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunConsumersAsync(stoppingToken);

    private async Task RunConsumersAsync(CancellationToken ct)
    {
        using var scope0 = _sp.CreateScope();
        var opt = scope0.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;

        var tasks = Enumerable.Range(0, Math.Clamp(opt.WorkerConcurrency, 1, 16))
            .Select(i => RunLoopAsync(workerId: $"w{i}", ct))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task RunLoopAsync(string workerId, CancellationToken ct)
    {
        // Évite de requeue les jobs "running" à CHAQUE itération (sinon spam + risque de duplicats)
        var lastRequeueUtc = DateTimeOffset.MinValue;
        var requeueEvery = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                var rag = scope.ServiceProvider.GetRequiredService<IOptions<RagOptions>>().Value;
                var ingest = scope.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;
                var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                var now = DateTimeOffset.UtcNow;
                if (now - lastRequeueUtc >= requeueEvery)
                {
                    lastRequeueUtc = now;

                    var requeued = await JobRepo.RequeueStaleRunningAsync(ds, TimeSpan.FromMinutes(ingest.StaleRunningMinutes), ct);
                    if (requeued > 0)
                        _log.LogWarning("Requeued {Count} stale running ingestion jobs", requeued);
                }

                var job = await JobRepo.TryDequeueAsync(ds, workerId, ct);
                if (job is null)
                {
                    await Task.Delay(ingest.WorkerEmptyDelayMs, ct);
                    continue;
                }

                _log.LogInformation("Ingestion start job={JobId} action={Action} doc={DocPath} v={Version}", job.JobId, job.Action, job.DocPath, job.Version);

                try
                {
                    if (job.Action == "delete")
                        await ProcessDeleteAsync(ds, httpFactory, rag, ingest, job, workerId, ct);
                    else
                        await ProcessUpsertAsync(ds, httpFactory, rag, ingest, job, workerId, ct);

                    await JobRepo.MarkDoneAsync(ds, job.JobId, ct);
                    _log.LogInformation("Ingestion done job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                }
                catch (JobCanceledException jc)
                {
                    _log.LogInformation("Job canceled job={JobId} action={Action} doc={DocPath} reason={Reason}", job.JobId, job.Action, job.DocPath, jc.Reason);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning("Job timed out/canceled job={JobId} action={Action} doc={DocPath}", job.JobId, job.Action, job.DocPath);
                    await JobRepo.MarkFailedAsync(ds, job.JobId, "timeout_or_canceled", ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Job failed job={JobId} action={Action} doc={DocPath}", job.JobId, job.Action, job.DocPath);
                    await JobRepo.MarkFailedAsync(ds, job.JobId, ex.Message, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error");
                await Task.Delay(1000, ct);
            }
        }
    }

    private static async Task<bool> IsCurrentDocVersionAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, int version, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        var v = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
        return v.HasValue && v.Value == version;
    }

    // Heartbeat: rafraîchit locked_at pour éviter qu’un job long soit considéré "stale" alors qu’il tourne.
    private static async Task TouchJobLockAsync(NpgsqlDataSource ds, Guid jobId, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET locked_at=now()
WHERE job_id=@job_id
  AND status='running'
  AND locked_by=@worker;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, worker = workerId }, cancellationToken: ct));
    }

    private static CancellationTokenSource? CreateTimeoutCts(CancellationToken ct, int seconds)
    {
        if (seconds <= 0) return null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private async Task ProcessDeleteAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        IngestionOptions ingest,
        IngestionJob job,
        string workerId,
        CancellationToken ct)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkCanceledAsync(ds, job.JobId, "superseded_version", ct);
                throw new JobCanceledException("superseded_version");
            }
        }

        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } }
            }
        };
        var body = new { filter };

        using var qdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
        var qct = qdrantCts?.Token ?? ct;

        var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/delete?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            qct);

        if (resp.StatusCode != HttpStatusCode.NotFound && !resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant delete failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
SET status='deleted', updated_at=now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = relDocPath }, cancellationToken: ct));

        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
    }

    private async Task ProcessUpsertAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        IngestionOptions ingest,
        IngestionJob job,
        string workerId,
        CancellationToken ct)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);
        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkCanceledAsync(ds, job.JobId, "superseded_version", ct);
                throw new JobCanceledException("superseded_version");
            }
        }

        if (!File.Exists(absPath))
        {
            await JobRepo.MarkCanceledAsync(ds, job.JobId, "file_missing", ct);
            throw new JobCanceledException("file_missing");
        }

        // Hash + size
        byte[] hash;
        long size;
        await using (var fs = File.OpenRead(absPath))
        {
            size = fs.Length;
            hash = await SHA256.HashDataAsync(fs, ct);
        }

        // PDF -> tokens -> chunks
        var tokens = PdfExtractor.ExtractWordTokens(absPath);
        if (tokens.Count == 0)
            throw new Exception("No text extracted from PDF");

        var chunks = Chunker.MakeChunks(tokens, ingest.ChunkMaxWords, ingest.ChunkOverlapWords, ingest.ChunkMinWords);

        // TEI
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        using var teiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
        var teiToken = teiCts?.Token ?? ct;
        var dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, teiToken);

        // Qdrant
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        using var qdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
        var qdrantToken = qdrantCts?.Token ?? ct;

        await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, dim, qdrantToken);

        // delete previous points
        await QdrantClient.DeleteByDocAsync(qdrant, rag.QdrantCollection, tenantId, docId, qdrantToken);

        // embed + upsert by batches
        var batchSize = Math.Clamp(ingest.EmbeddingsBatchSize, 1, 256);
        static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var hashHex = ToHex(hash);
        var category = (job.Category ?? ingest.DefaultCategory).Trim().ToLowerInvariant();

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var slice = chunks.Skip(i).Take(batchSize).ToList();
            var inputs = slice.Select(c => c.Text).ToArray();

            // TEI embeddings
            var swTei = Stopwatch.StartNew();
            using var bTeiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
            var bTeiToken = bTeiCts?.Token ?? ct;

            var vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, inputs, bTeiToken);
            swTei.Stop();

            var points = new List<object>(slice.Count);
            for (int j = 0; j < slice.Count; j++)
            {
                var c = slice[j];
                var chunkId = IdUtil.DeterministicGuid($"{docId}:{c.ChunkIndex}");

                points.Add(new
                {
                    id = chunkId.ToString(),
                    vector = vectors[j],
                    payload = new Dictionary<string, object?>
                    {
                        ["tenant_id"] = tenantId.ToString(),
                        ["doc_id"] = docId.ToString(),
                        ["doc_path"] = relDocPath,
                        ["doc_name"] = Path.GetFileName(relDocPath),
                        ["category"] = category,
                        ["chunk_id"] = chunkId.ToString(),
                        ["chunk_index"] = c.ChunkIndex,
                        ["page_start"] = c.PageStart,
                        ["page_end"] = c.PageEnd,
                        ["hash_doc"] = hashHex,
                        ["created_at"] = nowIso,
                        ["updated_at"] = nowIso,
                        ["text"] = c.Text,
                        ["embed_text"] = c.Text
                    }
                });
            }

            // Qdrant upsert
            var swQ = Stopwatch.StartNew();
            using var bQdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
            var bQToken = bQdrantCts?.Token ?? ct;

            await QdrantClient.UpsertPointsAsync(qdrant, rag.QdrantCollection, points, bQToken);
            swQ.Stop();

            // Heartbeat + log progression
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);

            var done = Math.Min(i + slice.Count, chunks.Count);
            _log.LogInformation(
                "Ingestion progress job={JobId} doc={DocPath} chunks={Done}/{Total} tei_ms={TeiMs} qdrant_ms={QdrantMs}",
                job.JobId, relDocPath, done, chunks.Count, swTei.ElapsedMilliseconds, swQ.ElapsedMilliseconds
            );
        }

        // Update documents row
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
SET content_hash=@hash,
    file_size=@size,
    file_mtime=@mtime,
    status='indexed',
    last_ingested_at=now(),
    updated_at=now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        var mtime = File.GetLastWriteTimeUtc(absPath);

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_path = relDocPath,
            hash,
            size,
            mtime = DateTime.SpecifyKind(mtime, DateTimeKind.Utc)
        }, cancellationToken: ct));

        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
    }
}
