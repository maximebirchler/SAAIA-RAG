using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
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
    private readonly IngestionBulkheads _bulkheads;
    private readonly IngestionJobCancellationRegistry _cancelRegistry;

    public IngestionWorker(
        IServiceProvider sp,
        ILogger<IngestionWorker> log,
        IngestionBulkheads bulkheads,
        IngestionJobCancellationRegistry cancelRegistry)
    {
        _sp = sp;
        _log = log;
        _bulkheads = bulkheads;
        _cancelRegistry = cancelRegistry;
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

                using (_log.BeginScope(new Dictionary<string, object>
                {
                    { "tenant_id", job.TenantId },
                    { "job_id", job.JobId },
                    { "doc_id", job.DocId },
                    { "doc_path", job.DocPath },
                    { "version", job.Version },
                    { "action", job.Action },
                    { "worker_id", workerId }
                }))
                {
                    _log.LogInformation("Ingestion start job={JobId} action={Action} doc={DocPath} v={Version}",
                        job.JobId, job.Action, job.DocPath, job.Version);

                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var _reg = _cancelRegistry.Register(job.JobId, job.TenantId, job.DocPath, jobCts);
                    var jobCt = jobCts.Token;

                    try
                    {
                        bool completed;
                        if (job.Action == "delete")
                            completed = await ProcessDeleteAsync(ds, httpFactory, rag, ingest, job, workerId, jobCt);
                        else
                            completed = await ProcessUpsertAsync(ds, httpFactory, rag, ingest, job, workerId, jobCt);

                        if (completed)
                            _log.LogInformation("Ingestion done job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                        else
                            _log.LogInformation("Ingestion canceled at commit job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                    }
                    catch (JobCanceledException jc)
                    {
                        _log.LogInformation("Job canceled job={JobId} action={Action} doc={DocPath} reason={Reason}",
                            job.JobId, job.Action, job.DocPath, jc.Reason);
                        await JobRepo.MarkCanceledAsync(ds, job.JobId, jc.Reason, ct);
                        // Safety net: stabilize document to prevent scanner from recreating the job.
                        // Uses COALESCE so it won't overwrite if cancel endpoint already set the pause.
                        try
                        {
                            await JobRepo.StabilizeDocumentAfterCancelAsync(ds, job.TenantId, job.DocPath, ct);
                        }
                        catch (Exception stabEx)
                        {
                            _log.LogWarning(stabEx, "Failed to stabilize document after cancel job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        var canceledByAdmin = await JobRepo.IsCanceledAsync(ds, job.JobId, CancellationToken.None);
                        if (canceledByAdmin)
                        {
                            _log.LogInformation("Job canceled via token job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkCanceledAsync(ds, job.JobId, "canceled_by_admin_token", ct);
                            try
                            {
                                await JobRepo.StabilizeDocumentAfterCancelAsync(ds, job.TenantId, job.DocPath, ct);
                            }
                            catch (Exception stabEx)
                            {
                                _log.LogWarning(stabEx, "Failed to stabilize document after token cancel job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                            }
                        }
                        else
                        {
                            _log.LogWarning("Job timed out/canceled job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkFailedAsync(ds, job.JobId, "timeout_or_canceled", ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Job failed job={JobId} action={Action} doc={DocPath}",
                            job.JobId, job.Action, job.DocPath);
                        await JobRepo.MarkFailedAsync(ds, job.JobId, ex.Message, ct);
                    }
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

    private static async Task ThrowIfJobCanceledAsync(NpgsqlDataSource ds, IngestionJob job, CancellationToken ct)
    {
        if (await JobRepo.IsCancellationRequestedAsync(ds, job.TenantId, job.DocPath, job.JobId, job.Action, ct).ConfigureAwait(false))
            throw new JobCanceledException("canceled_by_admin");
    }

    private async Task<bool> ProcessDeleteAsync(
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

        await ThrowIfJobCanceledAsync(ds, job, ct);

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
                throw new JobCanceledException("superseded_version");
        }

        using var qdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
        var qct = qdrantCts?.Token ?? ct;

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "deleting", null, null, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        var committed = await JobRepo.CompleteDeleteAsync(
            ds, tenantId, job.JobId, relDocPath, job.Version, ct);

        if (!committed)
            return false;

        try
        {
            using (await _bulkheads.AcquireQdrantAsync(qct))
            {
                await QdrantClient.DeleteByDocAsync(qdrant, rag.QdrantCollection, tenantId, docId, qct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Qdrant cleanup failed after delete commit job={JobId} doc={DocPath}. Points are orphaned but filtered by RAG.",
                job.JobId, job.DocPath);
        }

        return true;
    }

    private async Task<bool> ProcessUpsertAsync(
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

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "preparing", null, null, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
                throw new JobCanceledException("superseded_version");
        }

        if (!File.Exists(absPath))
            throw new Exception("file_missing");

        await ThrowIfJobCanceledAsync(ds, job, ct);

        // Hash + size
        byte[] hash;
        long size;
        await using (var fs = File.OpenRead(absPath))
        {
            size = fs.Length;
            hash = await SHA256.HashDataAsync(fs, ct);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);

        // PDF -> tokens -> chunks
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "extracting", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        var tokens = PdfExtractor.ExtractWordTokens(absPath, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        if (tokens.Count == 0)
            throw new Exception("No text extracted from PDF");

        var chunks = Chunker.MakeChunks(tokens, ingest.ChunkMaxWords, ingest.ChunkOverlapWords, ingest.ChunkMinWords, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", 0, chunks.Count, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        // TEI
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        using var teiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
        var teiToken = teiCts?.Token ?? ct;

        int dim;
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        using (await _bulkheads.AcquireTeiAsync(teiToken))
        {
            dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, teiToken);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        // Qdrant
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        using var qdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
        var qdrantToken = qdrantCts?.Token ?? ct;

        // ✅ EnsureCollection sous bulkhead Qdrant
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        using (await _bulkheads.AcquireQdrantAsync(qdrantToken))
        {
            await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, dim, qdrantToken);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        // embed + upsert by batches
        var batchSize = Math.Clamp(ingest.EmbeddingsBatchSize, 1, 256);
        static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var hashHex = ToHex(hash);
        var category = (job.Category ?? ingest.DefaultCategory).Trim().ToLowerInvariant();

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            await ThrowIfJobCanceledAsync(ds, job, ct);
            if (!File.Exists(absPath))
                throw new Exception("source_removed_during_ingestion");
            var slice = chunks.Skip(i).Take(batchSize).ToList();
            var inputs = slice.Select(c => c.Text).ToArray();

            // TEI embeddings
            var swTei = Stopwatch.StartNew();
            using var bTeiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
            var bTeiToken = bTeiCts?.Token ?? ct;

            await TouchJobLockAsync(ds, job.JobId, workerId, ct);

            float[][] vectors;
            await ThrowIfJobCanceledAsync(ds, job, ct);
            using (await _bulkheads.AcquireTeiAsync(bTeiToken))
            {
                vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, inputs, bTeiToken);
            }
            swTei.Stop();
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);

            var points = new List<object>(slice.Count);
            for (int j = 0; j < slice.Count; j++)
            {
                var c = slice[j];
                var chunkId = IdUtil.DeterministicGuid($"{docId}:{job.Version}:{c.ChunkIndex}");

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
                        ["embed_text"] = c.Text,
                        ["ingestion_version"] = job.Version
                    }
                });
            }

            // Qdrant upsert
            var swQ = Stopwatch.StartNew();
            using var bQdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
            var bQToken = bQdrantCts?.Token ?? ct;

            await ThrowIfJobCanceledAsync(ds, job, ct);
            using (await _bulkheads.AcquireQdrantAsync(bQToken))
            {
                await QdrantClient.UpsertPointsAsync(qdrant, rag.QdrantCollection, points, bQToken);
            }
            swQ.Stop();

            // Heartbeat + progression
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);

            var done = Math.Min(i + slice.Count, chunks.Count);
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", done, chunks.Count, ct);
            _log.LogInformation(
                "Ingestion progress job={JobId} doc={DocPath} chunks={Done}/{Total} tei_ms={TeiMs} qdrant_ms={QdrantMs}",
                job.JobId, relDocPath, done, chunks.Count, swTei.ElapsedMilliseconds, swQ.ElapsedMilliseconds
            );
        }

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "finalizing", chunks.Count, chunks.Count, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        if (!File.Exists(absPath))
            throw new Exception("source_removed_during_ingestion");

        var mtime = File.GetLastWriteTimeUtc(absPath);
        var committed = await JobRepo.CompleteUpsertAsync(
            ds, tenantId, job.JobId, relDocPath,
            hash, size, mtime, job.Version, ct);

        if (!committed)
            return false;

        try
        {
            using (await _bulkheads.AcquireQdrantAsync(qdrantToken))
            {
                await QdrantClient.DeleteOtherVersionsByDocAsync(
                    qdrant, rag.QdrantCollection,
                    tenantId, docId, job.Version, qdrantToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to cleanup old Qdrant versions job={JobId} doc={DocPath}. Old points filtered by RAG version check.",
                job.JobId, job.DocPath);
        }

        return true;
    }
}
