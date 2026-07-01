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
    private readonly TeiWorkloadGovernor _teiGovernor;
    private readonly IngestionJobCancellationRegistry _cancelRegistry;

    public IngestionWorker(
        IServiceProvider sp,
        ILogger<IngestionWorker> log,
        IngestionBulkheads bulkheads,
        TeiWorkloadGovernor teiGovernor,
        IngestionJobCancellationRegistry cancelRegistry)
    {
        _sp = sp;
        _log = log;
        _bulkheads = bulkheads;
        _teiGovernor = teiGovernor;
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
                        var heartbeatInterval = ResolveJobHeartbeatInterval(ingest.StaleRunningMinutes);
                        var completed = await RunWithJobHeartbeatAsync(
                            ds,
                            job,
                            workerId,
                            operationCt => job.Action == "delete"
                                ? ProcessDeleteAsync(ds, httpFactory, rag, ingest, job, workerId, operationCt)
                                : ProcessUpsertAsync(ds, httpFactory, rag, ingest, job, workerId, operationCt),
                            jobCt,
                            heartbeatInterval);

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
                            if (ex is IngestionBulkheadTimeoutException bulkheadTimeout)
                            {
                                var retryDelay = ComputeBulkheadDeferralDelay(bulkheadTimeout);
                                _log.LogWarning(
                                    ex,
                                    "Job deferred because ingestion bulkhead is saturated job={JobId} action={Action} doc={DocPath} bulkhead={Bulkhead} delay_seconds={DelaySeconds}",
                                    job.JobId,
                                    job.Action,
                                    job.DocPath,
                                    bulkheadTimeout.BulkheadName,
                                    (int)Math.Ceiling(retryDelay.TotalSeconds));
                                await JobRepo.DeferTransientAsync(ds, job.JobId, ex.Message, retryDelay, ct);
                                continue;
                            }

                            if (ShouldDeferTransientInfrastructureFailure(ex))
                            {
                                var retryDelay = ComputeTransientInfrastructureDeferralDelay(ex);
                                _log.LogWarning(
                                    ex,
                                    "Job deferred because an external ingestion dependency returned a transient error job={JobId} action={Action} doc={DocPath} delay_seconds={DelaySeconds}",
                                    job.JobId,
                                    job.Action,
                                    job.DocPath,
                                    (int)Math.Ceiling(retryDelay.TotalSeconds));
                                await JobRepo.DeferTransientAsync(ds, job.JobId, ex.Message, retryDelay, ct);
                                continue;
                            }

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

    internal static TimeSpan ComputeBulkheadDeferralDelay(IngestionBulkheadTimeoutException ex)
    {
        var seconds = Math.Clamp((int)Math.Ceiling(ex.WaitTimeout.TotalSeconds / 2), 30, 900);
        return TimeSpan.FromSeconds(seconds);
    }

    internal static bool ShouldDeferTransientInfrastructureFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (message.Contains("Too many open files", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!message.StartsWith("Qdrant ", StringComparison.OrdinalIgnoreCase))
            return false;

        return message.Contains(": 500 ", StringComparison.OrdinalIgnoreCase)
               || message.Contains(": 502 ", StringComparison.OrdinalIgnoreCase)
               || message.Contains(": 503 ", StringComparison.OrdinalIgnoreCase)
               || message.Contains(": 504 ", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Service internal error", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Not recovered from previous error", StringComparison.OrdinalIgnoreCase)
               || message.Contains("RocksDB", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    internal static TimeSpan ComputeTransientInfrastructureDeferralDelay(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Contains("Too many open files", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Not recovered from previous error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("RocksDB", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMinutes(10);
        }

        return TimeSpan.FromMinutes(2);
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

    internal static string ResolveEmbeddingInputFormat(string? embeddingsModel)
        => TeiClient.RequiresE5InstructionPrefix(embeddingsModel)
            ? "e5_passage_v1"
            : "raw_passage_v1";

    internal static bool ShouldEmbedRetrievalChunk(ProjectedRetrievalChunk chunk)
        => ShouldPublishRetrievalChunk(chunk);

    internal static bool ShouldPublishRetrievalChunk(ProjectedRetrievalChunk chunk)
        => ResolveRetrievalChunkEmbeddingRejectionReason(chunk) is null;

    internal static string? ResolveRetrievalChunkEmbeddingRejectionReason(ProjectedRetrievalChunk chunk)
    {
        if (RetrievalContentClassifier.IsPredominantlyNavigationContent(
                chunk.ContentRole,
                chunk.ChunkType,
                chunk.NavigationScore,
                chunk.ContentDensityScore))
        {
            return "navigation_only";
        }

        if (string.Equals(chunk.ExtractionTextStatus, "empty_text", StringComparison.Ordinal))
            return "empty_text";

        if (chunk.ExtractionTextSparse && chunk.TokenCount < 20)
            return "sparse_text";

        if (chunk.ExtractionQualitySignals is { Count: > 0 }
            && chunk.ExtractionQualitySignals.Any(static signal =>
                string.Equals(signal, "replacement_chars_remaining", StringComparison.Ordinal)))
        {
            return "replacement_chars_remaining";
        }

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(chunk.Text))
            return "ocr_noise";

        return null;
    }

    internal static IngestionRetrievalChunkQualitySummary BuildRetrievalChunkQualitySummary(
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks)
    {
        var searchable = 0;
        var navigation = 0;
        var sparse = 0;
        var replacementChars = 0;
        var emptyText = 0;
        var ocrNoise = 0;
        var other = 0;

        foreach (var chunk in retrievalChunks)
        {
            switch (ResolveRetrievalChunkEmbeddingRejectionReason(chunk))
            {
                case null:
                    searchable++;
                    break;
                case "navigation_only":
                    navigation++;
                    break;
                case "sparse_text":
                    sparse++;
                    break;
                case "replacement_chars_remaining":
                    replacementChars++;
                    break;
                case "empty_text":
                    emptyText++;
                    break;
                case "ocr_noise":
                    ocrNoise++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new IngestionRetrievalChunkQualitySummary(
            TotalChunkCount: retrievalChunks.Count,
            SearchableChunkCount: searchable,
            NavigationChunkCount: navigation,
            SparseRejectedChunkCount: sparse,
            ReplacementCharRejectedChunkCount: replacementChars,
            EmptyTextRejectedChunkCount: emptyText,
            OcrNoiseRejectedChunkCount: ocrNoise,
            OtherRejectedChunkCount: other);
    }

    internal static bool IsResumeCheckpointCompatible(
        JobRepo.ResumeCheckpointState? checkpoint,
        string sourceHash,
        int chunkTotal,
        string? embeddingsModel,
        string embeddingInputFormat)
    {
        if (checkpoint is null
            || !checkpoint.ProgressCurrent.HasValue
            || checkpoint.ProgressCurrent.Value <= 0)
            return false;

        return string.Equals(checkpoint.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase)
            && checkpoint.ChunkTotal == chunkTotal
            && string.Equals(NormalizeEmbeddingModelMarker(checkpoint.EmbeddingModel), NormalizeEmbeddingModelMarker(embeddingsModel), StringComparison.OrdinalIgnoreCase)
            && string.Equals(checkpoint.EmbeddingInputFormat, embeddingInputFormat, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEmbeddingModelMarker(string? embeddingsModel)
        => string.IsNullOrWhiteSpace(embeddingsModel) ? "unspecified" : embeddingsModel.Trim();

    internal static bool ShouldStabilizeDocumentAfterCancel(string? reason)
        => !string.Equals(reason, "superseded_version", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(reason, "superseded_failed_ocr_publish", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(reason, "superseded_failed_before_commit", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan ResolveJobHeartbeatInterval(int staleRunningMinutes)
    {
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, staleRunningMinutes));
        var intervalSeconds = (int)Math.Floor(staleAfter.TotalSeconds / 4);
        return TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 10, 60));
    }

    // Heartbeat: refresh locked_at so long jobs are not considered stale while they are still running.
    internal async Task<T> RunWithJobHeartbeatAsync<T>(
        NpgsqlDataSource ds,
        IngestionJob job,
        string workerId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        TimeSpan? heartbeatInterval = null)
    {
        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(20);
        try
        {
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Failed to refresh initial ingestion heartbeat job={JobId}", job.JobId);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, heartbeatCts.Token);
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

    private static async Task<bool> IsJobCancellationRequestedAsync(NpgsqlDataSource ds, IngestionJob job, CancellationToken ct)
        => await JobRepo.IsCancellationRequestedAsync(ds, job.TenantId, job.DocPath, job.JobId, job.Action, ct).ConfigureAwait(false);

    private static async Task ReportImageOcrProgressAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string workerId,
        int current,
        int total,
        CancellationToken ct)
    {
        var progressTotal = total > 0 ? total : (int?)null;
        var progressCurrent = progressTotal.HasValue
            ? Math.Clamp(current, 0, progressTotal.Value)
            : Math.Max(0, current);

        await JobRepo.UpdateProgressAsync(ds, jobId, "image_ocr", progressCurrent, progressTotal, ct);
        await TouchJobLockAsync(ds, jobId, workerId, ct);
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
        using (await _teiGovernor.AcquireIngestionAsync(ingest.TeiInteractiveQuietPeriodMs, bTeiToken))
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
        _log.LogInformation(
            "Ingestion native extraction summary job={JobId} doc={DocPath} pages={Pages} tokens={Tokens} text_status={TextStatus} text_pages={TextPages} empty_pages={EmptyPages} sparse_pages={SparsePages} image_pages={ImagePages} ocr_recommended={OcrRecommended} image_ocr_recommended={ImageOcrRecommended} ocr_required_but_disabled={OcrRequiredButDisabled}",
            job.JobId,
            relDocPath,
            nativeExtraction.Pages.Count,
            nativeExtraction.Tokens.Count,
            nativeExtraction.Quality.TextStatus,
            nativeExtraction.Quality.TextPageCount,
            nativeExtraction.Quality.EmptyPageCount,
            nativeExtraction.Quality.SparsePageCount,
            nativeExtraction.Pages.Count(static page => page.ImageCount > 0),
            fullDocumentOcrRecommended,
            imagePageOcrRecommended,
            ocrRequiredButDisabled);
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
                    var imageOcrPlan = PdfOcrTextExtractor.BuildImagePageOcrPlan(imageMergeBase, ingest);
                    await JobRepo.UpdateProgressAsync(ds, job.JobId, "image_ocr", 0, imageOcrPlan.AttemptedPageCount, ct);
                    await TouchJobLockAsync(ds, job.JobId, workerId, ct);
                    var imageOcrCallbacks = new PdfImagePageOcrCallbacks(
                        ReportProgressAsync: (current, total, operationCt) => ReportImageOcrProgressAsync(ds, job.JobId, workerId, current, total, operationCt),
                        IsCancellationRequestedAsync: operationCt => IsJobCancellationRequestedAsync(ds, job, operationCt));
                    var imageOcrResult = await RunWithJobHeartbeatAsync(
                        ds,
                        job,
                        workerId,
                        operationCt => PdfOcrTextExtractor.TryMergeImagePageOcrAsync(absPath, ingest, imageMergeBase, operationCt, ocrLanguages, imageOcrCallbacks),
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
        _log.LogInformation(
            "Ingestion OCR decision summary job={JobId} doc={DocPath} source={ExtractionSource} attempted={OcrAttempted} applied={OcrApplied} languages={OcrLanguages} candidate_pages={OcrCandidatePages} attempted_pages={OcrAttemptedPages} pages_with_text={OcrPagesWithText} pages_with_novel_text={OcrPagesWithNovelText} coverage={OcrCoverage} failure={OcrFailure}",
            job.JobId,
            relDocPath,
            extraction.Source,
            ocrAttempted,
            ocrApplied,
            ocrLanguages ?? "",
            ocrDiagnostics?.CandidatePageCount ?? 0,
            ocrDiagnostics?.AttemptedPageCount ?? 0,
            ocrDiagnostics?.PagesWithOcrText.Length ?? 0,
            ocrDiagnostics?.PagesWithNovelText.Length ?? 0,
            ocrDiagnostics?.CoverageStatus ?? "",
            ocrDiagnostics?.FailureReason ?? "");
        await JobRepo.UpdateProgressAsync(ds, job.JobId, "structuring", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        var swSections = Stopwatch.StartNew();
        var sections = DocumentSectionExtractor.Extract(pages);
        swSections.Stop();
        sectionMs = swSections.ElapsedMilliseconds;
        var swUnits = Stopwatch.StartNew();
        var units = DocumentUnitExtractor.Extract(pages, sections);
        swUnits.Stop();
        unitMs = swUnits.ElapsedMilliseconds;
        _log.LogInformation(
            "Ingestion structure summary job={JobId} doc={DocPath} pages={Pages} sections={Sections} units={Units} section_ms={SectionMs} unit_ms={UnitMs} text_status={TextStatus}",
            job.JobId,
            relDocPath,
            pages.Count,
            sections.Count,
            units.Count,
            sectionMs,
            unitMs,
            extractionQuality.TextStatus);
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
                var failureDiagnosticsPublished = await DocumentFoundationRepo.PublishFailedOcrExtractionAsync(
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
                    ct,
                    extractedPages: pages);
                if (!failureDiagnosticsPublished)
                    return false;
            }
            catch (Exception persistEx)
            {
                _log.LogWarning(persistEx, "Failed to persist OCR failure diagnostics job={JobId} doc={DocPath}", job.JobId, relDocPath);
            }

            throw new Exception($"No text extracted from PDF; text_status={extractionQuality.TextStatus}; ocr_recommended={extractionQuality.OcrRecommended}; failure_reason={failureReason}; ocr_failure={ocrDiagnostics?.FailureReason ?? "none"}");
        }

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "chunking", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        var swChunking = Stopwatch.StartNew();
        var retrievalChunks = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            ingest.ChunkMaxWords,
            ingest.ChunkOverlapWords,
            ingest.ChunkMinWords);
        var retrievalChunkQuality = BuildRetrievalChunkQualitySummary(retrievalChunks);
        var chunks = retrievalChunks
            .Where(ShouldEmbedRetrievalChunk)
            .Select(chunk => new Chunk(chunk.ChunkIndex, chunk.PageStart, chunk.PageEnd, chunk.Text))
            .ToList();
        swChunking.Stop();
        chunkingMs = swChunking.ElapsedMilliseconds;
        _log.LogInformation(
            "Ingestion chunking summary job={JobId} doc={DocPath} retrieval_chunks={RetrievalChunks} searchable_chunks={SearchableChunks} embedding_chunks={EmbeddingChunks} rejected_chunks={RejectedChunks} navigation_chunks={NavigationChunks} sparse_rejected={SparseRejected} replacement_rejected={ReplacementRejected} empty_rejected={EmptyRejected} ocr_noise_rejected={OcrNoiseRejected} other_rejected={OtherRejected} manual_review={ManualReview} chunking_ms={ChunkingMs}",
            job.JobId,
            relDocPath,
            retrievalChunks.Count,
            retrievalChunkQuality.SearchableChunkCount,
            chunks.Count,
            retrievalChunkQuality.RejectedChunkCount,
            retrievalChunkQuality.NavigationChunkCount,
            retrievalChunkQuality.SparseRejectedChunkCount,
            retrievalChunkQuality.ReplacementCharRejectedChunkCount,
            retrievalChunkQuality.EmptyTextRejectedChunkCount,
            retrievalChunkQuality.OcrNoiseRejectedChunkCount,
            retrievalChunkQuality.OtherRejectedChunkCount,
            retrievalChunkQuality.ManualReviewRecommended,
            chunkingMs);
        if (retrievalChunkQuality.SearchableChunkCount == 0)
        {
            const string failureReason = "manual_review_no_searchable_chunks";
            var failureDiagnosticsPublished = await DocumentFoundationRepo.PublishUnsearchableRetrievalDiagnosticsAsync(
                ds,
                tenantId,
                docId,
                job.JobId,
                relDocPath,
                hash,
                size,
                File.GetLastWriteTimeUtc(absPath),
                job.Version,
                pages,
                units,
                retrievalChunks,
                retrievalChunkQuality,
                extraction.Source,
                ocrAttempted,
                ocrApplied,
                ocrLanguages,
                ocrAttempted ? ocrMs : null,
                ocrDiagnostics,
                extractionQuality,
                nativeExtractionQuality,
                failureReason,
                ct);
            if (!failureDiagnosticsPublished)
                return false;

            return false;
        }

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "projecting", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
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
        var embeddingInputFormat = ResolveEmbeddingInputFormat(rag.EmbeddingsModel);
        var canResumeFromCheckpoint = IsResumeCheckpointCompatible(
            checkpoint,
            hashHex,
            chunks.Count,
            rag.EmbeddingsModel,
            embeddingInputFormat);
        var resumeFromChunk = canResumeFromCheckpoint
            ? Math.Clamp(checkpoint!.ProgressCurrent!.Value, 0, chunks.Count)
            : 0;

        await JobRepo.UpdateProgressAsync(ds, job.JobId, "embedding", resumeFromChunk, chunks.Count, ct);
        await JobRepo.StoreResumeCheckpointAsync(ds, job.JobId, hashHex, size, chunks.Count, rag.EmbeddingsModel, embeddingInputFormat, ct);
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
        using (await _teiGovernor.AcquireIngestionAsync(ingest.TeiInteractiveQuietPeriodMs, teiToken))
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
            var inputs = workItems
                .Select(item => TeiClient.FormatEmbeddingInput(
                    rag.EmbeddingsModel,
                    item.EmbeddingText,
                    TeiClient.EmbeddingInputKind.Passage))
                .ToArray();

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
                        item.ChunkLinks,
                        rag.EmbeddingsModel,
                        embeddingInputFormat)
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
               page.ImageCount > 0
               || PdfOcrTextExtractor.HasReplacementSignal(page)
               || PdfOcrTextExtractor.HasTextRecoverySignal(page));

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
        ChunkLinkInfo? chunkLinks,
        string? embeddingModel = null,
        string? embeddingInputFormat = null)
    {
        var chunkId = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, projectedChunk.ChunkIndex);
        var usesContextualText = !string.Equals(projectedChunk.Text, embeddingText, StringComparison.Ordinal);
        var sourceUnitOrdinals = projectedChunk.SourceUnitOrdinals ?? Array.Empty<int>();
        var sourceUnitCount = projectedChunk.SourceUnitCount ?? sourceUnitOrdinals.Count;

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
            ["embedding_model"] = embeddingModel,
            ["embedding_input_format"] = embeddingInputFormat,
            ["section_ordinal"] = projectedChunk.SectionOrdinal,
            ["unit_ordinal"] = projectedChunk.UnitOrdinal,
            ["source_unit_ordinals"] = sourceUnitOrdinals,
            ["source_unit_start_ordinal"] = projectedChunk.SourceUnitStartOrdinal,
            ["source_unit_end_ordinal"] = projectedChunk.SourceUnitEndOrdinal,
            ["source_unit_count"] = sourceUnitCount,
            ["chunk_composition"] = projectedChunk.ChunkComposition,
            ["chunk_type"] = projectedChunk.ChunkType,
            ["content_role"] = projectedChunk.ContentRole,
            ["navigation_reason"] = projectedChunk.NavigationReason,
            ["original_chunk_type"] = projectedChunk.OriginalChunkType,
            ["navigation_score"] = Math.Round(projectedChunk.NavigationScore, 4),
            ["content_density_score"] = Math.Round(projectedChunk.ContentDensityScore, 4),
            ["extraction_text_status"] = projectedChunk.ExtractionTextStatus,
            ["extraction_text_sparse"] = projectedChunk.ExtractionTextSparse,
            ["extraction_ocr_candidate"] = projectedChunk.ExtractionOcrCandidate,
            ["extraction_quality_signals"] = projectedChunk.ExtractionQualitySignals ?? Array.Empty<string>(),
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
        var nextChunkInSameSectionByIndex = new Dictionary<int, Guid>();
        var nextChunkBySectionOrdinal = new Dictionary<int, ProjectedRetrievalChunk>();

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var current = ordered[i];
            if (!current.SectionOrdinal.HasValue)
                continue;

            var sectionOrdinal = current.SectionOrdinal.Value;
            if (nextChunkBySectionOrdinal.TryGetValue(sectionOrdinal, out var sameSectionNext))
            {
                nextChunkInSameSectionByIndex[current.ChunkIndex] =
                    DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, sameSectionNext.ChunkIndex);
            }

            nextChunkBySectionOrdinal[sectionOrdinal] = current;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            Guid? prev = i > 0
                ? DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, ordered[i - 1].ChunkIndex)
                : null;
            Guid? next = i + 1 < ordered.Count
                ? DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, ingestionVersion, ordered[i + 1].ChunkIndex)
                : null;

            Guid? sameSection = nextChunkInSameSectionByIndex.TryGetValue(current.ChunkIndex, out var sameSectionId)
                ? sameSectionId
                : null;

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
            OriginalChunkType: null,
            NavigationScore: 0.0,
            ContentDensityScore: 0.0,
            SourceUnitCount: 0,
            ChunkComposition: "legacy_word_window_unknown_units");
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
