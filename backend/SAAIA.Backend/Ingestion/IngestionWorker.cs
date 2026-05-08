using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
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

sealed record IngestionJob(
    Guid JobId,
    Guid TenantId,
    string Action,
    string DocPath,
    string? Category,
    Guid DocId,
    int Version,
    IngestionCapabilityAProfileSeed? CapabilityAProfileSeed);

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
                        if (ShouldStabilizeDocumentAfterCancel(jc.Reason))
                        {
                            // Safety net: stabilize document to prevent scanner from recreating admin/user-canceled jobs.
                            // Uses COALESCE so it won't overwrite if cancel endpoint already set the pause.
                            try
                            {
                                await JobRepo.StabilizeDocumentAfterCancelAsync(ds, job.TenantId, job.DocPath, ct);
                                await JobRepo.FreezeTerminalSnapshotAsync(ds, job.JobId, ct);
                            }
                            catch (Exception stabEx)
                            {
                                _log.LogWarning(stabEx, "Failed to stabilize document after cancel job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        var canceledByAdmin = jobCt.IsCancellationRequested
                            || await JobRepo.IsCanceledAsync(ds, job.JobId, CancellationToken.None);
                        if (canceledByAdmin)
                        {
                            _log.LogInformation("Job canceled via token job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkCanceledAsync(ds, job.JobId, "canceled_by_admin_token", ct);
                            try
                            {
                                await JobRepo.StabilizeDocumentAfterCancelAsync(ds, job.TenantId, job.DocPath, ct);
                                await JobRepo.FreezeTerminalSnapshotAsync(ds, job.JobId, ct);
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
                        var canceledByAdmin = jobCt.IsCancellationRequested
                            || await JobRepo.IsCanceledAsync(ds, job.JobId, CancellationToken.None);
                        if (canceledByAdmin)
                        {
                            _log.LogInformation(ex, "Job canceled after exception job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkCanceledAsync(ds, job.JobId, "canceled_by_admin_exception", ct);
                            try
                            {
                                await JobRepo.StabilizeDocumentAfterCancelAsync(ds, job.TenantId, job.DocPath, ct);
                                await JobRepo.FreezeTerminalSnapshotAsync(ds, job.JobId, ct);
                            }
                            catch (Exception stabEx)
                            {
                                _log.LogWarning(stabEx, "Failed to stabilize document after exception cancel job={JobId} doc={DocPath}", job.JobId, job.DocPath);
                            }
                        }
                        else
                        {
                            _log.LogError(ex, "Job failed job={JobId} action={Action} doc={DocPath}",
                                job.JobId, job.Action, job.DocPath);
                            await JobRepo.MarkFailedAsync(ds, job.JobId, ex.Message, ct);
                        }
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

    private static async Task<int?> GetIndexedDocVersionAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = "SELECT indexed_version FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
    }

    internal static bool ShouldPurgeQdrantVersionBeforeFullEmbedding(int resumeFromChunk, int? indexedVersion, int jobVersion)
        => resumeFromChunk == 0
           && jobVersion > 0
           && (!indexedVersion.HasValue || indexedVersion.Value != jobVersion);

    internal static bool ShouldStabilizeDocumentAfterCancel(string? reason)
        => !string.Equals(reason, "superseded_version", StringComparison.OrdinalIgnoreCase);

    // Heartbeat: refresh locked_at so long jobs are not considered stale while they are still running.
    private async Task<T> RunWithJobHeartbeatAsync<T>(
        NpgsqlDataSource ds,
        IngestionJob job,
        string workerId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), heartbeatCts.Token);
                    await TouchJobLockAsync(ds, job.JobId, workerId, heartbeatCts.Token);
                }
                catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Failed to refresh ingestion heartbeat job={JobId}", job.JobId);
                }
            }
        }, CancellationToken.None);

        try
        {
            return await operation(ct);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
            {
                // Expected when the operation finishes before the next heartbeat tick.
            }
        }
    }

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

    private async Task<float[][]> EmbedBatchWithAdaptiveRetryAsync(
        NpgsqlDataSource ds,
        IngestionJob job,
        string workerId,
        HttpClient tei,
        string model,
        string[] inputs,
        IngestionOptions ingest,
        int configuredBatchSize,
        CancellationToken ct)
        => await EmbedBatchSegmentWithAdaptiveRetryAsync(
            ds,
            job,
            workerId,
            tei,
            model,
            inputs,
            offset: 0,
            count: inputs.Length,
            ingest,
            configuredBatchSize,
            ct);

    private async Task<float[][]> EmbedBatchSegmentWithAdaptiveRetryAsync(
        NpgsqlDataSource ds,
        IngestionJob job,
        string workerId,
        HttpClient tei,
        string model,
        string[] inputs,
        int offset,
        int count,
        IngestionOptions ingest,
        int configuredBatchSize,
        CancellationToken ct)
    {
        try
        {
            return await EmbedBatchOnceAsync(
                ds,
                job,
                workerId,
                tei,
                model,
                SliceEmbeddingInputs(inputs, offset, count),
                ingest,
                ct);
        }
        catch (Exception ex) when (ShouldRetryEmbeddingBatchWithSmallerBatch(
            ex,
            ct,
            ingest.EmbeddingsBatchAdaptiveRetryEnabled,
            count))
        {
            var leftCount = Math.Max(1, count / 2);
            var rightCount = count - leftCount;
            _log.LogWarning(
                ex,
                "TEI embedding batch failed; retrying with smaller batches job={JobId} doc={DocPath} configured_batch={ConfiguredBatch} failed_batch={FailedBatch} next_batches={LeftBatch}+{RightBatch}",
                job.JobId,
                job.DocPath,
                configuredBatchSize,
                count,
                leftCount,
                rightCount);

            var left = await EmbedBatchSegmentWithAdaptiveRetryAsync(
                ds,
                job,
                workerId,
                tei,
                model,
                inputs,
                offset,
                leftCount,
                ingest,
                configuredBatchSize,
                ct);
            var right = await EmbedBatchSegmentWithAdaptiveRetryAsync(
                ds,
                job,
                workerId,
                tei,
                model,
                inputs,
                offset + leftCount,
                rightCount,
                ingest,
                configuredBatchSize,
                ct);

            return left.Concat(right).ToArray();
        }
    }

    private async Task<float[][]> EmbedBatchOnceAsync(
        NpgsqlDataSource ds,
        IngestionJob job,
        string workerId,
        HttpClient tei,
        string model,
        string[] inputs,
        IngestionOptions ingest,
        CancellationToken ct)
    {
        using var bTeiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
        var bTeiToken = bTeiCts?.Token ?? ct;

        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        using (await _bulkheads.AcquireTeiAsync(bTeiToken))
        {
            return await TeiClient.EmbedAsync(tei, model, inputs, bTeiToken);
        }
    }

    private static string[] SliceEmbeddingInputs(string[] inputs, int offset, int count)
    {
        if (offset == 0 && count == inputs.Length)
            return inputs;

        var slice = new string[count];
        Array.Copy(inputs, offset, slice, 0, count);
        return slice;
    }

    internal static bool ShouldRetryEmbeddingBatchWithSmallerBatch(
        Exception ex,
        CancellationToken rootToken,
        bool adaptiveRetryEnabled,
        int batchItemCount)
    {
        if (!adaptiveRetryEnabled || batchItemCount <= 1 || rootToken.IsCancellationRequested || ex is JobCanceledException)
            return false;

        if (ex is OperationCanceledException)
            return true;

        var message = ex.Message ?? string.Empty;
        if (message.Contains("bulkhead", StringComparison.OrdinalIgnoreCase))
            return false;

        return message.Contains("413", StringComparison.OrdinalIgnoreCase)
            || message.Contains("payload too large", StringComparison.OrdinalIgnoreCase)
            || message.Contains("request entity too large", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too large", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum request", StringComparison.OrdinalIgnoreCase)
            || message.Contains("max batch", StringComparison.OrdinalIgnoreCase)
            || message.Contains("batch size", StringComparison.OrdinalIgnoreCase)
            || message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("oom", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
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
            {
                await JobRepo.MarkSupersededAndQueueCurrentAsync(ds, tenantId, job.JobId, relDocPath, job.Version, ct);
                return false;
            }
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
        var swTotal = Stopwatch.StartNew();
        long hashMs = 0;
        long extractMs = 0;
        long ocrMs = 0;
        long sectionMs = 0;
        long unitMs = 0;
        long chunkingMs = 0;
        long exactMatchMs = 0;
        long contextualMs = 0;
        long teiWarmupMs = 0;
        long qdrantEnsureMs = 0;
        long embeddingTotalMs = 0;
        long qdrantUpsertTotalMs = 0;
        long publishMs = 0;
        long cleanupMs = 0;

        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);
        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);
        var checkpoint = await JobRepo.GetResumeCheckpointAsync(ds, job.JobId, ct);
        var savedProgressCurrent = checkpoint?.ProgressCurrent;
        var savedProgressTotal = checkpoint?.ProgressTotal ?? checkpoint?.ChunkTotal;
        var hasSavedProgress =
            savedProgressCurrent.HasValue
            && savedProgressCurrent.Value > 0
            && savedProgressTotal.HasValue
            && savedProgressTotal.Value > 0;

        if (hasSavedProgress)
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "resuming", savedProgressCurrent, savedProgressTotal, ct);
        else
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "preparing", null, null, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkSupersededAndQueueCurrentAsync(ds, tenantId, job.JobId, relDocPath, job.Version, ct);
                return false;
            }
        }

        if (!File.Exists(absPath))
            throw new Exception("file_missing");

        await ThrowIfJobCanceledAsync(ds, job, ct);

        // Hash + size
        byte[] hash;
        long size;
        var swHash = Stopwatch.StartNew();
        await using (var fs = File.OpenRead(absPath))
        {
            size = fs.Length;
            hash = await SHA256.HashDataAsync(fs, ct);
        }
        swHash.Stop();
        hashMs = swHash.ElapsedMilliseconds;
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);

        // PDF -> tokens -> chunks
        if (!hasSavedProgress)
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "extracting", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        var swExtract = Stopwatch.StartNew();
        var extraction = PdfExtractor.Extract(absPath, ct);
        swExtract.Stop();
        extractMs = swExtract.ElapsedMilliseconds;
        var nativeExtractionQuality = extraction.Quality;
        var ocrAttempted = false;
        var ocrApplied = false;
        string? ocrLanguages = null;
        PdfOcrDiagnostics? ocrDiagnostics = null;
        var nativeExtraction = extraction;
        var fullDocumentOcrRecommended = nativeExtraction.Quality.OcrRecommended;
        var imagePageOcrRecommended = ShouldAttemptImagePageOcr(ingest, nativeExtraction);
        var ocrRequiredButDisabled = IsOcrRequiredButDisabled(ingest, fullDocumentOcrRecommended, imagePageOcrRecommended);
        if (ocrRequiredButDisabled)
        {
            ocrDiagnostics = PdfOcrTextExtractor.BuildOcrDisabledDiagnostics(
                ingest,
                nativeExtraction,
                imagePageOcrRecommended);
        }

        if (ingest.OcrEnabled && (fullDocumentOcrRecommended || imagePageOcrRecommended))
        {
            ocrAttempted = true;
            var forceFullDocumentOcr = fullDocumentOcrRecommended && PdfOcrTextExtractor.ShouldForceOcrNativeText(nativeExtraction);
            await JobRepo.UpdateProgressAsync(ds, job.JobId, fullDocumentOcrRecommended ? "ocr" : "image_ocr", null, null, ct);
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);
            var swOcr = Stopwatch.StartNew();
            PdfExtractionResult? fullOcrExtraction = null;
            PdfExtractionResult? imageOcrExtraction = null;
            PdfExtractionResult? ocrExtraction = null;
            PdfOcrDiagnostics? fullOcrDiagnostics = null;
            PdfOcrDiagnostics? imageOcrDiagnostics = null;
            var fullOcrApplied = false;
            var imageOcrApplied = false;
            using (await RunWithJobHeartbeatAsync(
                ds,
                job,
                workerId,
                operationCt => _bulkheads.AcquireOcrAsync(operationCt),
                ct))
            {
                ocrLanguages = PdfOcrTextExtractor.ResolveLanguagesForDocument(absPath, ingest, nativeExtraction);
                if (fullDocumentOcrRecommended)
                {
                    var fullOcrResult = await RunWithJobHeartbeatAsync(
                        ds,
                        job,
                        workerId,
                        operationCt => PdfOcrTextExtractor.TryExtractWithDiagnosticsAsync(absPath, ingest, operationCt, ocrLanguages, forceFullDocumentOcr),
                        ct);
                    fullOcrDiagnostics = fullOcrResult?.Diagnostics;
                    fullOcrExtraction = fullOcrResult?.Extraction;
                    fullOcrApplied = fullOcrExtraction is not null
                        && PdfOcrTextExtractor.ShouldApplyOcrExtraction(
                            nativeExtraction,
                            fullOcrExtraction,
                            fullDocumentOcrRecommended,
                            forceFullDocumentOcr);
                    if (fullOcrApplied)
                        ocrExtraction = PdfOcrTextExtractor.WithImageCountsFromNative(fullOcrExtraction!, nativeExtraction);
                }

                if (imagePageOcrRecommended)
                {
                    if (fullDocumentOcrRecommended)
                    {
                        await JobRepo.UpdateProgressAsync(ds, job.JobId, "image_ocr", null, null, ct);
                        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
                    }

                    var imageMergeBase = ocrExtraction ?? nativeExtraction;
                    var imageOcrResult = await RunWithJobHeartbeatAsync(
                        ds,
                        job,
                        workerId,
                        operationCt => PdfOcrTextExtractor.TryMergeImagePageOcrAsync(absPath, ingest, imageMergeBase, operationCt, ocrLanguages),
                        ct);
                    imageOcrDiagnostics = imageOcrResult?.Diagnostics;
                    imageOcrExtraction = imageOcrResult?.Extraction;
                    imageOcrApplied = imageOcrExtraction is not null
                        && PdfOcrTextExtractor.ShouldApplyOcrExtraction(
                            imageMergeBase,
                            imageOcrExtraction,
                            fullDocumentOcrRecommended: false,
                            forceFullDocumentOcr: false);
                    if (imageOcrApplied)
                        ocrExtraction = imageOcrExtraction;
                }
            }
            swOcr.Stop();
            ocrMs = swOcr.ElapsedMilliseconds;
            var shouldApplyOcrExtraction = fullOcrApplied || imageOcrApplied;
            var appliedReason = ResolveOcrAppliedReason(
                fullDocumentOcrRecommended,
                forceFullDocumentOcr,
                fullDocumentOcrRecommended ? fullOcrExtraction : imageOcrExtraction,
                shouldApplyOcrExtraction,
                imagePageOcrRecommended,
                imageOcrApplied,
                fullOcrApplied);
            ocrDiagnostics = PdfOcrTextExtractor.CombineOcrDiagnostics(fullOcrDiagnostics, imageOcrDiagnostics);
            ocrDiagnostics = PdfOcrTextExtractor.WithAppliedReason(ocrDiagnostics, appliedReason);
            if (shouldApplyOcrExtraction && ocrExtraction is not null)
            {
                extraction = ocrExtraction with { OcrDiagnostics = ocrDiagnostics };
                ocrApplied = true;
            }
        }
        var tokens = extraction.Tokens;
        var pages = extraction.Pages;
        var extractionQuality = extraction.Quality;
        var swSections = Stopwatch.StartNew();
        var sections = DocumentSectionExtractor.Extract(pages);
        swSections.Stop();
        sectionMs = swSections.ElapsedMilliseconds;
        var swUnits = Stopwatch.StartNew();
        var units = DocumentUnitExtractor.Extract(pages, sections);
        swUnits.Stop();
        unitMs = swUnits.ElapsedMilliseconds;
        await ThrowIfJobCanceledAsync(ds, job, ct);
        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkSupersededAndQueueCurrentAsync(ds, tenantId, job.JobId, relDocPath, job.Version, ct);
                return false;
            }
        }

        if (tokens.Count == 0)
        {
            var failureReason = ResolveNoTextFailureReason(ocrAttempted, ocrRequiredButDisabled);
            try
            {
                await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
                    ds,
                    tenantId,
                    docId,
                    job.JobId,
                    relDocPath,
                    hash,
                    size,
                    File.GetLastWriteTimeUtc(absPath),
                    job.Version,
                    extraction.Source,
                    ocrAttempted,
                    ocrLanguages,
                    ocrAttempted ? ocrMs : null,
                    ocrDiagnostics,
                    extractionQuality,
                    nativeExtractionQuality,
                    failureReason,
                    ct);
            }
            catch (Exception persistEx)
            {
                _log.LogWarning(persistEx, "Failed to persist OCR failure diagnostics job={JobId} doc={DocPath}", job.JobId, relDocPath);
            }

            throw new Exception($"No text extracted from PDF; text_status={extractionQuality.TextStatus}; ocr_recommended={extractionQuality.OcrRecommended}; failure_reason={failureReason}; ocr_failure={ocrDiagnostics?.FailureReason ?? "none"}");
        }

        var swChunking = Stopwatch.StartNew();
        var retrievalChunks = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            ingest.ChunkMaxWords,
            ingest.ChunkOverlapWords,
            ingest.ChunkMinWords);
        var chunks = retrievalChunks.Count > 0
            ? retrievalChunks
                .Select(chunk => new Chunk(chunk.ChunkIndex, chunk.PageStart, chunk.PageEnd, chunk.Text))
                .ToList()
            : Chunker.MakeChunks(tokens, ingest.ChunkMaxWords, ingest.ChunkOverlapWords, ingest.ChunkMinWords, ct);
        if (retrievalChunks.Count == 0)
            retrievalChunks = RetrievalChunkProjector.Project(chunks, sections, units);
        swChunking.Stop();
        chunkingMs = swChunking.ElapsedMilliseconds;
        var swExact = Stopwatch.StartNew();
        var exactMatchEntries = ExactMatchEntryExtractor.Extract(units);
        swExact.Stop();
        exactMatchMs = swExact.ElapsedMilliseconds;
        var swContextual = Stopwatch.StartNew();
        var contextualTextEntries = ContextualTextProjector.Project(relDocPath, sections, units, retrievalChunks);
        swContextual.Stop();
        contextualMs = swContextual.ElapsedMilliseconds;
        var retrievalChunksByIndex = retrievalChunks.ToDictionary(chunk => chunk.ChunkIndex);
        var contextualTextByChunkIndex = BuildContextualTextMap(contextualTextEntries);
        var sectionTitleByOrdinal = sections.ToDictionary(section => section.Ordinal, section => section.Title);
        var headingPathBySectionOrdinal = ContextualTextProjector.BuildHeadingPathMap(sections);
        var chunkLinkMap = BuildChunkLinkMap(docId, job.Version, retrievalChunks);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        var hashHex = ToHex(hash);
        var canResumeFromCheckpoint =
            checkpoint is not null
            && checkpoint.ProgressCurrent.HasValue
            && checkpoint.ProgressCurrent.Value > 0
            && string.Equals(checkpoint.SourceHash, hashHex, StringComparison.OrdinalIgnoreCase)
            && checkpoint.ChunkTotal == chunks.Count;
        var resumeFromChunk = canResumeFromCheckpoint
            ? Math.Clamp(checkpoint!.ProgressCurrent!.Value, 0, chunks.Count)
            : 0;

        await JobRepo.StoreResumeCheckpointAsync(ds, job.JobId, hashHex, size, chunks.Count, ct);
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", resumeFromChunk, chunks.Count, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        if (resumeFromChunk > 0)
        {
            _log.LogInformation(
                "Resuming ingestion job={JobId} doc={DocPath} from chunk {ResumeFrom}/{Total}",
                job.JobId, relDocPath, resumeFromChunk, chunks.Count);
        }
        else if (checkpoint?.ProgressCurrent is > 0)
        {
            _log.LogWarning(
                "Resume checkpoint mismatch job={JobId} doc={DocPath}; restarting from chunk 0 (saved={Saved}, total={Total})",
                job.JobId, relDocPath, checkpoint.ProgressCurrent, chunks.Count);
        }

        // TEI
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        using var teiCts = CreateTimeoutCts(ct, ingest.TeiTimeoutSeconds);
        var teiToken = teiCts?.Token ?? ct;

        int dim;
        var swTeiWarmup = Stopwatch.StartNew();
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        using (await _bulkheads.AcquireTeiAsync(teiToken))
        {
            dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, teiToken);
        }
        swTeiWarmup.Stop();
        teiWarmupMs = swTeiWarmup.ElapsedMilliseconds;
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        // Qdrant
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        using var qdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
        var qdrantToken = qdrantCts?.Token ?? ct;

        // ✅ EnsureCollection sous bulkhead Qdrant
        var swEnsureCollection = Stopwatch.StartNew();
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        using (await _bulkheads.AcquireQdrantAsync(qdrantToken))
        {
            await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, dim, qdrantToken);
        }
        swEnsureCollection.Stop();
        qdrantEnsureMs = swEnsureCollection.ElapsedMilliseconds;
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);

        var indexedVersionBeforeEmbedding = await GetIndexedDocVersionAsync(ds, tenantId, relDocPath, ct);
        if (ShouldPurgeQdrantVersionBeforeFullEmbedding(resumeFromChunk, indexedVersionBeforeEmbedding, job.Version))
        {
            await ThrowIfJobCanceledAsync(ds, job, ct);
            using (await _bulkheads.AcquireQdrantAsync(qdrantToken))
            {
                await QdrantClient.DeleteVersionByDocAsync(
                    qdrant,
                    rag.QdrantCollection,
                    tenantId,
                    docId,
                    job.Version,
                    qdrantToken);
            }
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        }
        else if (resumeFromChunk == 0 && indexedVersionBeforeEmbedding == job.Version)
        {
            _log.LogWarning(
                "Skipping Qdrant version pre-cleanup for already published version job={JobId} doc={DocPath} version={Version}",
                job.JobId,
                relDocPath,
                job.Version);
        }

        // embed + upsert by batches
        var batchSize = IngestionOptions.ResolveEmbeddingsBatchSize(ingest.EmbeddingsBatchSize);

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var category = IngestionCategoryResolver.Normalize(job.Category ?? ingest.DefaultCategory);

        for (int i = resumeFromChunk; i < chunks.Count; i += batchSize)
        {
            await ThrowIfJobCanceledAsync(ds, job, ct);
            if (!File.Exists(absPath))
                throw new Exception("source_removed_during_ingestion");
            var slice = chunks.Skip(i).Take(batchSize).ToList();
            var workItems = slice
                .Select(chunk =>
                {
                    var projectedChunk = ResolveProjectedRetrievalChunk(chunk, retrievalChunksByIndex);
                    var embeddingText = ResolveEmbeddingText(projectedChunk.ChunkIndex, projectedChunk.Text, contextualTextByChunkIndex);
                    var sectionTitle = ResolveSectionTitle(projectedChunk.SectionOrdinal, sectionTitleByOrdinal);
                    var headingPath = ContextualTextProjector.ResolveHeadingPath(projectedChunk.SectionOrdinal, headingPathBySectionOrdinal);
                    chunkLinkMap.TryGetValue(projectedChunk.ChunkIndex, out var chunkLinks);
                    return new EmbeddingChunkWorkItem(projectedChunk, embeddingText, sectionTitle, headingPath, chunkLinks);
                })
                .ToList();
            var inputs = workItems.Select(item => item.EmbeddingText).ToArray();

            var swTei = Stopwatch.StartNew();
            var vectors = await EmbedBatchWithAdaptiveRetryAsync(
                ds,
                job,
                workerId,
                tei,
                rag.EmbeddingsModel,
                inputs,
                ingest,
                configuredBatchSize: batchSize,
                ct);
            swTei.Stop();
            embeddingTotalMs += swTei.ElapsedMilliseconds;
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);

            var points = new List<object>(slice.Count);
            for (int j = 0; j < slice.Count; j++)
            {
                var item = workItems[j];
                var chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, job.Version, item.ProjectedChunk.ChunkIndex);

                points.Add(new
                {
                    id = chunkId.ToString(),
                    vector = vectors[j],
                    payload = BuildQdrantChunkPayload(
                        tenantId,
                        docId,
                        relDocPath,
                        category,
                        hashHex,
                        nowIso,
                        job.Version,
                        item.ProjectedChunk,
                        item.EmbeddingText,
                        item.SectionTitle,
                        item.HeadingPath,
                        item.ChunkLinks)
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
            qdrantUpsertTotalMs += swQ.ElapsedMilliseconds;

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
        var swPublish = Stopwatch.StartNew();
        var committed = await JobRepo.CompleteUpsertAsync(
            ds, tenantId, job.JobId, relDocPath,
            hash, size, mtime, job.Version, pages, sections, units, retrievalChunks, exactMatchEntries, contextualTextEntries, ct,
            extractionSource: extraction.Source,
            ocrAttempted: ocrAttempted,
            ocrApplied: ocrApplied,
            ocrLanguages: ocrLanguages,
            ocrDurationMs: ocrAttempted ? ocrMs : null,
            ocrDiagnostics: ocrDiagnostics,
            nativeExtractionQuality: nativeExtractionQuality,
            capabilityAProfileSeed: job.CapabilityAProfileSeed);
        swPublish.Stop();
        publishMs = swPublish.ElapsedMilliseconds;


        if (!committed)
            return false;

        try
        {
            var swCleanup = Stopwatch.StartNew();
            using var cleanupQdrantCts = CreateTimeoutCts(ct, ingest.QdrantTimeoutSeconds);
            var cleanupQdrantToken = cleanupQdrantCts?.Token ?? ct;
            using (await _bulkheads.AcquireQdrantAsync(cleanupQdrantToken))
            {
                await QdrantClient.DeleteOtherVersionsByDocAsync(
                    qdrant, rag.QdrantCollection,
                    tenantId, docId, job.Version, cleanupQdrantToken);
            }
            swCleanup.Stop();
            cleanupMs = swCleanup.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to cleanup old Qdrant versions job={JobId} doc={DocPath}. Old points filtered by RAG version check.",
                job.JobId, job.DocPath);
        }

        swTotal.Stop();
        _log.LogInformation(
            "Ingestion stage timings job={JobId} doc={DocPath} total_ms={TotalMs} hash_ms={HashMs} extract_ms={ExtractMs} ocr_ms={OcrMs} extraction_source={ExtractionSource} sections_ms={SectionsMs} units_ms={UnitsMs} chunking_ms={ChunkingMs} exact_ms={ExactMs} contextual_ms={ContextualMs} tei_warmup_ms={TeiWarmupMs} qdrant_ensure_ms={QdrantEnsureMs} embedding_ms={EmbeddingMs} qdrant_upsert_ms={QdrantUpsertMs} publish_ms={PublishMs} cleanup_ms={CleanupMs} pages={Pages} sections={Sections} units={Units} chunks={Chunks} exact_entries={ExactEntries} contextual_entries={ContextualEntries} extraction_quality={ExtractionQuality} ocr_recommended={OcrRecommended}",
            job.JobId,
            relDocPath,
            swTotal.ElapsedMilliseconds,
            hashMs,
            extractMs,
            ocrMs,
            extraction.Source,
            sectionMs,
            unitMs,
            chunkingMs,
            exactMatchMs,
            contextualMs,
            teiWarmupMs,
            qdrantEnsureMs,
            embeddingTotalMs,
            qdrantUpsertTotalMs,
            publishMs,
            cleanupMs,
            pages.Count,
            sections.Count,
            units.Count,
            retrievalChunks.Count,
            exactMatchEntries.Count,
            contextualTextEntries.Count,
            extractionQuality.TextStatus,
            extractionQuality.OcrRecommended);

        return true;
    }

    internal static IReadOnlyDictionary<int, ProjectedContextualTextEntry> BuildContextualTextMap(
        IReadOnlyList<ProjectedContextualTextEntry> contextualTextEntries)
        => contextualTextEntries
            .GroupBy(entry => entry.ChunkIndex)
            .ToDictionary(group => group.Key, group => group.Last());

    internal static bool ShouldAttemptImagePageOcr(IngestionOptions options, PdfExtractionResult extraction)
        => options.OcrImagePageEnabled
           && extraction.Pages.Any(static page =>
               page.ImageCount > 0 || PdfOcrTextExtractor.HasReplacementCharacters(page.Text));

    internal static bool IsOcrRequiredButDisabled(
        IngestionOptions options,
        bool fullDocumentOcrRecommended,
        bool imagePageOcrRecommended)
        => !options.OcrEnabled && (fullDocumentOcrRecommended || imagePageOcrRecommended);

    internal static string ResolveNoTextFailureReason(bool ocrAttempted, bool ocrRequiredButDisabled)
    {
        if (ocrRequiredButDisabled)
            return "ocr_required_but_disabled";

        return ocrAttempted
            ? "scanned_pdf_not_indexable"
            : "no_indexable_text";
    }

    internal static string ResolveOcrAppliedReason(
        bool fullDocumentOcrRecommended,
        bool forceFullDocumentOcr,
        PdfExtractionResult? ocrExtraction,
        bool ocrApplied,
        bool imagePageOcrRecommended = false,
        bool imageOcrApplied = false,
        bool fullOcrApplied = false)
    {
        if (imagePageOcrRecommended && imageOcrApplied)
        {
            return fullOcrApplied
                ? "ocr_replaced_native_text_and_image_ocr_merged"
                : "image_ocr_merged_native_text";
        }

        if (ocrExtraction is null)
            return fullDocumentOcrRecommended
                ? "ocr_extraction_failed"
                : "image_ocr_no_novel_text";

        if (ocrApplied)
        {
            if (!fullDocumentOcrRecommended)
                return "image_ocr_merged_native_text";

            return forceFullDocumentOcr
                ? "force_ocr_replaced_native_text"
                : "ocr_replaced_native_text";
        }

        return fullDocumentOcrRecommended
            ? "ocr_not_better_than_native_text"
            : "image_ocr_not_better_than_native_text";
    }

    internal static string ResolveEmbeddingText(
        int chunkIndex,
        string chunkText,
        IReadOnlyDictionary<int, ProjectedContextualTextEntry> contextualTextByChunkIndex)
    {
        if (contextualTextByChunkIndex.TryGetValue(chunkIndex, out var contextual)
            && !string.IsNullOrWhiteSpace(contextual.Text))
        {
            return contextual.Text;
        }

        return chunkText;
    }

    internal static Dictionary<string, object?> BuildQdrantChunkPayload(
        Guid tenantId,
        Guid docId,
        string relDocPath,
        string? category,
        string hashHex,
        string nowIso,
        int ingestionVersion,
        ProjectedRetrievalChunk projectedChunk,
        string embeddingText,
        string? sectionTitle,
        string? headingPath,
        ChunkLinkInfo? chunkLinks)
    {
        var chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, projectedChunk.ChunkIndex);
        var usesContextualText = !string.Equals(projectedChunk.Text, embeddingText, StringComparison.Ordinal);

        return new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId.ToString(),
            ["doc_id"] = docId.ToString(),
            ["doc_path"] = relDocPath,
            ["doc_name"] = Path.GetFileName(relDocPath),
            ["category"] = category,
            ["chunk_id"] = chunkId.ToString(),
            ["chunk_index"] = projectedChunk.ChunkIndex,
            ["page_start"] = projectedChunk.PageStart,
            ["page_end"] = projectedChunk.PageEnd,
            ["offset_start"] = projectedChunk.OffsetStart,
            ["offset_end"] = projectedChunk.OffsetEnd,
            ["hash_doc"] = hashHex,
            ["created_at"] = nowIso,
            ["updated_at"] = nowIso,
            ["text"] = projectedChunk.Text,
            ["embed_text"] = embeddingText,
            ["embedding_basis"] = usesContextualText ? "contextual_text_v1" : "chunk_text",
            ["section_ordinal"] = projectedChunk.SectionOrdinal,
            ["unit_ordinal"] = projectedChunk.UnitOrdinal,
            ["chunk_type"] = projectedChunk.ChunkType,
            ["content_role"] = projectedChunk.ContentRole,
            ["navigation_reason"] = projectedChunk.NavigationReason,
            ["original_chunk_type"] = projectedChunk.OriginalChunkType,
            ["section_title"] = sectionTitle,
            ["heading_path"] = headingPath ?? sectionTitle,
            ["prev_chunk_id"] = chunkLinks?.PreviousChunkId?.ToString(),
            ["next_chunk_id"] = chunkLinks?.NextChunkId?.ToString(),
            ["same_section_chunk_id"] = chunkLinks?.SameSectionChunkId?.ToString(),
            ["ingestion_version"] = ingestionVersion
        };
    }

    internal static IReadOnlyDictionary<int, ChunkLinkInfo> BuildChunkLinkMap(
        Guid docId,
        int ingestionVersion,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        if (retrievalChunks.Count == 0)
            return new Dictionary<int, ChunkLinkInfo>();

        var ordered = retrievalChunks.OrderBy(chunk => chunk.ChunkIndex).ToList();
        var map = new Dictionary<int, ChunkLinkInfo>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            Guid? prev = i > 0
                ? DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, ordered[i - 1].ChunkIndex)
                : null;
            Guid? next = i + 1 < ordered.Count
                ? DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, ordered[i + 1].ChunkIndex)
                : null;

            Guid? sameSection = null;
            if (current.SectionOrdinal.HasValue)
            {
                var match = ordered
                    .Skip(i + 1)
                    .FirstOrDefault(candidate => candidate.SectionOrdinal == current.SectionOrdinal);
                if (match is not null)
                    sameSection = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, match.ChunkIndex);
            }

            map[current.ChunkIndex] = new ChunkLinkInfo(prev, next, sameSection);
        }

        return map;
    }

    internal static ProjectedRetrievalChunk ResolveProjectedRetrievalChunk(
        Chunk chunk,
        IReadOnlyDictionary<int, ProjectedRetrievalChunk> retrievalChunksByIndex)
    {
        if (retrievalChunksByIndex.TryGetValue(chunk.ChunkIndex, out var projectedChunk))
            return projectedChunk;

        return new ProjectedRetrievalChunk(
            ChunkIndex: chunk.ChunkIndex,
            SectionOrdinal: null,
            UnitOrdinal: null,
            PageStart: chunk.PageStart,
            PageEnd: chunk.PageEnd,
            Text: chunk.Text,
            TokenCount: CountWords(chunk.Text),
            Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text)),
            ChunkType: "legacy_word_window_v1",
            OffsetStart: null,
            OffsetEnd: null,
            ContentRole: RetrievalContentClassifier.ContentRole,
            NavigationReason: null,
            OriginalChunkType: null);
    }

    internal static string? ResolveSectionTitle(
        int? sectionOrdinal,
        IReadOnlyDictionary<int, string> sectionTitleByOrdinal)
    {
        if (!sectionOrdinal.HasValue)
            return null;

        return sectionTitleByOrdinal.TryGetValue(sectionOrdinal.Value, out var title)
            ? title
            : null;
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed record EmbeddingChunkWorkItem(
        ProjectedRetrievalChunk ProjectedChunk,
        string EmbeddingText,
        string? SectionTitle,
        string? HeadingPath,
        ChunkLinkInfo? ChunkLinks);

    internal sealed record ChunkLinkInfo(
        Guid? PreviousChunkId,
        Guid? NextChunkId,
        Guid? SameSectionChunkId);
}
