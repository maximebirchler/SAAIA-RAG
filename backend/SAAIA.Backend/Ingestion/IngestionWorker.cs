using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
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
    private readonly IngestionJobCancellationRegistry _cancellationRegistry;

    public IngestionWorker(
        IServiceProvider sp,
        ILogger<IngestionWorker> log,
        IngestionBulkheads bulkheads,
        IngestionJobCancellationRegistry cancellationRegistry)
    {
        _sp = sp;
        _log = log;
        _bulkheads = bulkheads;
        _cancellationRegistry = cancellationRegistry;
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
        var lastStaleSweepUtc = DateTimeOffset.MinValue;
        var staleSweepEvery = TimeSpan.FromSeconds(60);

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
                if (now - lastStaleSweepUtc >= staleSweepEvery)
                {
                    lastStaleSweepUtc = now;

                    var finalized = await JobRepo.FinalizeStaleRunningAsync(ds, TimeSpan.FromMinutes(ingest.StaleRunningMinutes), ct);
                    if (finalized > 0)
                        _log.LogWarning("Finalized {Count} stale running ingestion jobs", finalized);
                }

                var job = await JobRepo.TryDequeueAsync(ds, workerId, ct);
                if (job is null)
                {
                    await Task.Delay(ingest.WorkerEmptyDelayMs, ct);
                    continue;
                }

                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var registration = _cancellationRegistry.Register(job.JobId, job.TenantId, job.DocPath, jobCts);
                var jobCt = jobCts.Token;

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

                    try
                    {
                        if (job.Action == "delete")
                            await ProcessDeleteAsync(ds, httpFactory, rag, ingest, job, workerId, jobCt);
                        else
                            await ProcessUpsertAsync(ds, httpFactory, rag, ingest, job, workerId, jobCt);

                        await JobRepo.MarkDoneAsync(ds, job.JobId, CancellationToken.None);
                        _log.LogInformation("Ingestion done job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                    }
                    catch (JobCanceledException jc)
                    {
                        _log.LogInformation("Job canceled job={JobId} action={Action} doc={DocPath} reason={Reason}",
                            job.JobId, job.Action, job.DocPath, jc.Reason);
                        await JobRepo.MarkCanceledAsync(ds, job.JobId, jc.Reason, CancellationToken.None);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        var canceledByAdmin = await JobRepo.IsCanceledAsync(ds, job.JobId, CancellationToken.None);
                        if (canceledByAdmin)
                        {
                            _log.LogInformation("Job canceled via token job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkCanceledAsync(ds, job.JobId, "canceled_by_admin", CancellationToken.None);
                        }
                        else
                        {
                            _log.LogWarning("Job timed out/canceled job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkFailedAsync(ds, job.JobId, "timeout_or_canceled", CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Job failed job={JobId} action={Action} doc={DocPath}",
                            job.JobId, job.Action, job.DocPath);
                        await JobRepo.MarkFailedAsync(ds, job.JobId, ex.Message, CancellationToken.None);
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

    private static async Task TouchJobLockAsync(NpgsqlDataSource ds, Guid jobId, string workerId, CancellationToken ct)
    {
        await JobRepo.TouchAsync(ds, jobId, workerId, ct);
    }

    private static CancellationTokenSource? CreateTimeoutCts(CancellationToken ct, int seconds)
    {
        if (seconds <= 0) return null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private static async Task ThrowIfJobCanceledAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken jobCt)
    {
        if (jobCt.IsCancellationRequested)
            throw new JobCanceledException("canceled_by_admin");

        if (await JobRepo.IsCanceledAsync(ds, jobId, CancellationToken.None).ConfigureAwait(false))
            throw new JobCanceledException("canceled_by_admin");
    }

    private async Task ProcessDeleteAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        IngestionOptions ingest,
        IngestionJob job,
        string workerId,
        CancellationToken jobCt)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;

        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, jobCt);
            if (!ok)
                throw new JobCanceledException("superseded_version");
        }

        using var qdrantCts = CreateTimeoutCts(jobCt, ingest.QdrantTimeoutSeconds);
        var qct = qdrantCts?.Token ?? jobCt;

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "deleting", null, null, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        using (await _bulkheads.AcquireQdrantAsync(qct))
        {
            await QdrantClient.DeleteByDocAsync(qdrant, rag.QdrantCollection, tenantId, docId, qct);
        }

        await using var conn = await ds.OpenConnectionAsync(jobCt);
        const string sql = """
UPDATE documents
SET status='deleted',
    indexed_version=0,
    updated_at=now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;
""";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = relDocPath }, cancellationToken: jobCt));

        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
    }

    private async Task ProcessUpsertAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        IngestionOptions ingest,
        IngestionJob job,
        string workerId,
        CancellationToken jobCt)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);
        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "preparing", null, null, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, jobCt);
            if (!ok)
                throw new JobCanceledException("superseded_version");
        }

        if (!File.Exists(absPath))
            throw new JobCanceledException("file_missing");

        // Hash + size
        byte[] hash;
        long size;
        await using (var fs = File.OpenRead(absPath))
        {
            size = fs.Length;
            hash = await SHA256.HashDataAsync(fs, jobCt);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);

        // PDF -> tokens -> chunks (cancel-aware)
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "extracting", null, null, CancellationToken.None);
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
        var tokens = PdfExtractor.ExtractWordTokens(absPath, jobCt);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        if (tokens.Count == 0)
            throw new Exception("No text extracted from PDF");

        var pageCount = tokens.Count == 0 ? 0 : tokens.Max(t => t.Page);
        var chunks = Chunker.MakeChunks(tokens, ingest.ChunkMaxWords, ingest.ChunkOverlapWords, ingest.ChunkMinWords);
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", 0, chunks.Count, CancellationToken.None);
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        using var teiCts = CreateTimeoutCts(jobCt, ingest.TeiTimeoutSeconds);
        var teiToken = teiCts?.Token ?? jobCt;

        int dim;
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        using (await _bulkheads.AcquireTeiAsync(teiToken))
        {
            dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, teiToken);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        using var qdrantCts = CreateTimeoutCts(jobCt, ingest.QdrantTimeoutSeconds);
        var qdrantToken = qdrantCts?.Token ?? jobCt;

        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        using (await _bulkheads.AcquireQdrantAsync(qdrantToken))
        {
            await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, dim, qdrantToken);
        }
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        var batchSize = Math.Clamp(ingest.EmbeddingsBatchSize, 1, 256);
        static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var hashHex = ToHex(hash);
        var category = (job.Category ?? ingest.DefaultCategory).Trim().ToLowerInvariant();

        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
            var slice = chunks.Skip(i).Take(batchSize).ToList();
            var inputs = slice.Select(c => c.Text).ToArray();

            var swTei = Stopwatch.StartNew();
            using var bTeiCts = CreateTimeoutCts(jobCt, ingest.TeiTimeoutSeconds);
            var bTeiToken = bTeiCts?.Token ?? jobCt;

            await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

            float[][] vectors;
            await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
            using (await _bulkheads.AcquireTeiAsync(bTeiToken))
            {
                vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, inputs, bTeiToken);
            }
            swTei.Stop();
            await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

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
                        ["ingestion_version"] = job.Version,
                        ["hash_doc"] = hashHex,
                        ["created_at"] = nowIso,
                        ["updated_at"] = nowIso,
                        ["text"] = c.Text,
                        ["embed_text"] = c.Text
                    }
                });
            }

            var swQ = Stopwatch.StartNew();
            using var bQdrantCts = CreateTimeoutCts(jobCt, ingest.QdrantTimeoutSeconds);
            var bQToken = bQdrantCts?.Token ?? jobCt;

            await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
            using (await _bulkheads.AcquireQdrantAsync(bQToken))
            {
                await QdrantClient.UpsertPointsAsync(qdrant, rag.QdrantCollection, points, bQToken);
            }
            swQ.Stop();

            await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

            var done = Math.Min(i + slice.Count, chunks.Count);
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", done, chunks.Count, CancellationToken.None);
            _log.LogInformation(
                "Ingestion progress job={JobId} doc={DocPath} chunks={Done}/{Total} tei_ms={TeiMs} qdrant_ms={QdrantMs}",
                job.JobId, relDocPath, done, chunks.Count, swTei.ElapsedMilliseconds, swQ.ElapsedMilliseconds
            );
        }

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "finalizing", chunks.Count, chunks.Count, CancellationToken.None);
        await ThrowIfJobCanceledAsync(ds, job.JobId, jobCt);
        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        await using (var conn = await ds.OpenConnectionAsync(jobCt))
        {
            const string sql = """
UPDATE documents
SET content_hash=@hash,
    file_size=@size,
    file_mtime=@mtime,
    page_count=@page_count,
    status='indexed',
    indexed_version=@version,
    last_ingested_at=now(),
    updated_at=now()
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND ingestion_version=@version;
""";
            var mtime = File.GetLastWriteTimeUtc(absPath);

            var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                doc_path = relDocPath,
                version = job.Version,
                hash,
                size,
                page_count = pageCount,
                mtime = DateTime.SpecifyKind(mtime, DateTimeKind.Utc)
            }, cancellationToken: jobCt));

            if (affected == 0)
                throw new JobCanceledException("superseded_version");
        }

        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);

        using var cleanupQdrantCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, ingest.QdrantTimeoutSeconds)));
        using (await _bulkheads.AcquireQdrantAsync(cleanupQdrantCts.Token))
        {
            await QdrantClient.DeleteOtherVersionsByDocAsync(qdrant, rag.QdrantCollection, tenantId, docId, job.Version, cleanupQdrantCts.Token);
        }

        await TouchJobLockAsync(ds, job.JobId, workerId, CancellationToken.None);
    }
}
