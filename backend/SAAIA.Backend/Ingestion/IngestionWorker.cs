using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Contracts.DocumentIntelligence;

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

sealed partial class IngestionWorker : BackgroundService
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
        if (!opt.WorkerEnabled)
        {
            _log.LogInformation("Ingestion worker is disabled");
            return;
        }

        var workerInstanceId = CreateWorkerInstanceId();
        var workerConcurrency = Math.Clamp(opt.WorkerConcurrency, 1, 16);
        var emptyDelayMs = IngestionOptions.ResolveWorkerEmptyDelayMs(
            opt.WorkerEmptyDelayMs);
        _log.LogInformation(
            "Ingestion worker pool starting instance={WorkerInstanceId} concurrency={WorkerConcurrency} empty_poll_delay_ms={EmptyPollDelayMs}",
            workerInstanceId,
            workerConcurrency,
            emptyDelayMs);

        var tasks = Enumerable.Range(0, workerConcurrency)
            .Select(i => RunLoopAsync(
                workerId: BuildWorkerId(workerInstanceId, i),
                emptyDelayMs,
                ct))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    internal static string CreateWorkerInstanceId()
    {
        var host = Regex.Replace(Environment.MachineName ?? string.Empty, @"[^\p{L}\p{N}_-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(host))
            host = "host";
        if (host.Length > 12)
            host = host[..12];

        var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        return $"{host}-{Environment.ProcessId}-{nonce}";
    }

    internal static string BuildWorkerId(string workerInstanceId, int workerIndex)
    {
        var normalizedInstance = string.IsNullOrWhiteSpace(workerInstanceId)
            ? "instance-unknown"
            : workerInstanceId.Trim();
        return $"{normalizedInstance}-w{Math.Max(0, workerIndex)}";
    }

    private async Task RunLoopAsync(
        string workerId,
        int emptyDelayMs,
        CancellationToken ct)
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
                var documentIntelligence = scope.ServiceProvider
                    .GetRequiredService<IOptions<DocumentIntelligenceOptions>>()
                    .Value;
                var doclingClient = scope.ServiceProvider.GetRequiredService<DoclingClient>();
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
                    await Task.Delay(emptyDelayMs, ct);
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
                                : ProcessUpsertAsync(
                                    ds,
                                    httpFactory,
                                    rag,
                                    ingest,
                                    documentIntelligence,
                                    doclingClient,
                                    job,
                                    workerId,
                                    operationCt),
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
                                if (string.Equals(bulkheadTimeout.BulkheadName, "OCR", StringComparison.OrdinalIgnoreCase))
                                {
                                    await JobRepo.UpdateProgressAsync(
                                        ds,
                                        job.JobId,
                                        "ocr_waiting_for_slot_timeout",
                                        null,
                                        null,
                                        new
                                        {
                                            bulkhead = bulkheadTimeout.BulkheadName,
                                            maxConcurrency = bulkheadTimeout.MaxConcurrency,
                                            waitTimeoutSeconds = Math.Max(0, (int)Math.Ceiling(bulkheadTimeout.WaitTimeout.TotalSeconds)),
                                            retryDelaySeconds = Math.Max(1, (int)Math.Ceiling(retryDelay.TotalSeconds)),
                                            deferred = true
                                        },
                                        ct);
                                }

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

        if (IsCanonicalDocumentIntelligenceChunk(chunk))
        {
            return string.IsNullOrWhiteSpace(chunk.Text)
                ? "empty_text"
                : null;
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

        if (OcrNoiseFilter.LooksLikeProbableNoiseText(chunk.Text)
            && !LooksLikePublishableRepairedContinuationChunk(chunk)
            && !LooksLikeClassifierConfirmedRetrievalContent(chunk))
        {
            return "ocr_noise";
        }

        if (LooksLikeLowSubstanceRetrievalChunk(chunk))
            return "low_substance";

        return null;
    }

    private static bool IsCanonicalDocumentIntelligenceChunk(
        ProjectedRetrievalChunk chunk)
        => string.Equals(
               chunk.ChunkType,
               DoclingCanonicalRetrievalProjector.ContentChunkType,
               StringComparison.Ordinal)
           || string.Equals(
               chunk.ChunkType,
               DoclingCanonicalRetrievalProjector.TableChunkType,
               StringComparison.Ordinal);

    private static bool LooksLikeLowSubstanceRetrievalChunk(ProjectedRetrievalChunk chunk)
    {
        if (chunk.TokenCount >= 8)
            return LooksLikeShortOcrLayoutFragment(chunk);
        if (string.IsNullOrWhiteSpace(chunk.Text))
            return true;

        var normalized = Regex.Replace(chunk.Text, @"\s+", " ").Trim();
        normalized = normalized.Trim(' ', '\t', '-', '\u2013', '\u2014', ',', ';', ':', '.', '|', '/', '\\', '(', ')', '[', ']');
        if (normalized.Length == 0)
            return true;

        if (LowSubstanceNumericRangeRegex().IsMatch(normalized))
            return true;

        if (LowSubstanceQuantityLeadRegex().IsMatch(normalized))
            return true;

        if (chunk.TokenCount <= 3)
            return true;

        if (normalized.Any(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022'))
            return false;

        var meaningfulWords = SubstantiveWordRegex().Matches(normalized).Count;
        return meaningfulWords < 3;
    }

    private static bool LooksLikeShortOcrLayoutFragment(ProjectedRetrievalChunk chunk)
    {
        if (chunk.TokenCount is < 8 or > 16 || string.IsNullOrWhiteSpace(chunk.Text))
            return false;
        if (chunk.ContentDensityScore > 0.45)
            return false;

        var tokens = OcrLayoutTokenRegex()
            .Matches(chunk.Text)
            .Select(static match => match.Value)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length < 6)
            return false;

        var meaningfulWords = SubstantiveWordRegex().Matches(chunk.Text).Count;
        if (meaningfulWords >= 4)
            return false;

        var shortOrSymbolicTokens = tokens.Count(static token =>
            token.Length <= 2
            || token.All(char.IsDigit)
            || token.Any(static ch => !char.IsLetterOrDigit(ch)));

        return shortOrSymbolicTokens >= Math.Max(4, (int)Math.Ceiling(tokens.Length * 0.55));
    }

    private static bool LooksLikePublishableRepairedContinuationChunk(ProjectedRetrievalChunk chunk)
    {
        if (!string.Equals(chunk.ChunkType, "unit_exact_v1", StringComparison.Ordinal)
            || chunk.SourceUnitCount is null or < 2
            || chunk.SourceUnitOrdinals is null || chunk.SourceUnitOrdinals.Count < 2)
        {
            return false;
        }

        if (RetrievalContentClassifier.IsPredominantlyNavigationContent(
                chunk.ContentRole,
                chunk.ChunkType,
                chunk.NavigationScore,
                chunk.ContentDensityScore))
        {
            return false;
        }

        if (chunk.ContentDensityScore < 0.55 || chunk.TokenCount < 30)
            return false;

        var text = chunk.Text ?? string.Empty;
        var sentenceOrBulletCount = text.Count(static ch => ch is '.' or '!' or '?' or '\u2026' or '\u2022');
        var structuredMeasureCount = Regex.Matches(
                text,
                @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|nm|bar|pa|kpa|mpa|v|kv|a|ma|w|kw|hz|rpm|pct|percent|pourcent|deg|degrees?|degres?|c|f|units?|unites?|items?|elements?|entries?|parts?|pieces?|pages?|s|sec|secs|secondes?|seconds?|min|mins?|minutes?|h|hr|hrs?|hours?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count;

        return sentenceOrBulletCount >= 3 && structuredMeasureCount >= 2;
    }

    private static bool LooksLikeClassifierConfirmedRetrievalContent(ProjectedRetrievalChunk chunk)
        => string.Equals(chunk.ContentRole, RetrievalContentClassifier.ContentRole, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(chunk.NavigationReason)
            && chunk.TokenCount >= 20
            && chunk.ContentDensityScore >= 0.35;

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
        TimeSpan? heartbeatInterval = null,
        Func<int, TimeSpan, CancellationToken, Task>? onHeartbeatAsync = null)
    {
        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(20);
        var elapsed = Stopwatch.StartNew();
        try
        {
            await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Failed to refresh initial ingestion heartbeat job={JobId}", job.JobId);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatCount = 0;
        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, heartbeatCts.Token);
                    await TouchJobLockAsync(ds, job.JobId, workerId, heartbeatCts.Token);
                    heartbeatCount++;
                    if (onHeartbeatAsync is not null)
                        await onHeartbeatAsync(heartbeatCount, elapsed.Elapsed, heartbeatCts.Token);
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

    private static async Task ReportOcrWaitingForSlotHeartbeatAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string workerId,
        bool fullDocumentOcrRecommended,
        bool imagePageOcrRecommended,
        int pageCount,
        bool forceFullDocumentOcr,
        int heartbeatCount,
        TimeSpan elapsed,
        CancellationToken ct)
    {
        var details = new
        {
            heartbeat = Math.Max(0, heartbeatCount),
            elapsedSeconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero)),
            fullDocumentOcrRecommended,
            imagePageOcrRecommended,
            pageCount = Math.Max(0, pageCount),
            forceFullDocumentOcr,
            state = "waiting_for_ocr_bulkhead"
        };
        await JobRepo.UpdateProgressAsync(ds, jobId, "ocr_waiting_for_slot", Math.Max(0, heartbeatCount), null, details, ct);
        await TouchJobLockAsync(ds, jobId, workerId, ct);
    }

    private static async Task ReportFullDocumentOcrHeartbeatAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string workerId,
        int pageCount,
        string? languages,
        bool forceOcr,
        int heartbeatCount,
        TimeSpan elapsed,
        CancellationToken ct)
    {
        var details = new
        {
            heartbeat = Math.Max(0, heartbeatCount),
            elapsedSeconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero)),
            pageCount = Math.Max(0, pageCount),
            languages = string.IsNullOrWhiteSpace(languages) ? null : languages.Trim(),
            forceOcr
        };
        await JobRepo.UpdateProgressAsync(ds, jobId, "ocr_full_document", Math.Max(0, heartbeatCount), null, details, ct);
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
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        return await RunWithJobHeartbeatAsync(
            ds,
            job,
            workerId,
            async operationCt =>
            {
                using (await _bulkheads.AcquireHeavyComputeAsync(operationCt))
                using (await _teiGovernor.AcquireIngestionAsync(
                    ingest.TeiInteractiveQuietPeriodMs,
                    operationCt))
                using (await _bulkheads.AcquireTeiAsync(operationCt))
                {
                    using var bTeiCts = CreateTimeoutCts(
                        operationCt,
                        ingest.TeiTimeoutSeconds);
                    var bTeiToken = bTeiCts?.Token ?? operationCt;
                    return await TeiClient.EmbedAsync(
                        tei,
                        model,
                        inputs,
                        bTeiToken);
                }
            },
            ct);
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
        DocumentIntelligenceOptions documentIntelligence,
        DoclingClient doclingClient,
        IngestionJob job,
        string workerId,
        CancellationToken ct)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;
        var canonicalCodeRevision = ingest.CanonicalArtifactsEnabled
            ? IngestionProvenanceConfiguration.Validate(
                rag,
                Environment.GetEnvironmentVariable("SAAIA_CODE_REVISION"))
            : null;
        var swTotal = Stopwatch.StartNew();
        long hashMs = 0;
        long extractMs = 0;
        long ocrMs = 0;
        long sectionMs = 0;
        long unitMs = 0;
        long canonicalProjectionMs = 0;
        long nativeTextExtractionMs = 0;
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
        var hashHex = Convert.ToHexString(hash).ToLowerInvariant();
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);

        // PDF -> tokens -> chunks
        if (!hasSavedProgress)
            await JobRepo.UpdateProgressAsync(ds, job.JobId, "extracting", null, null, ct);
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        var useDocling = ResolveUseDocling(documentIntelligence);
        DoclingConvertResponse? doclingConversion = null;
        PdfExtractionResult? nativeTextLayerExtraction = null;
        var doclingForceOcr = false;
        var swExtract = Stopwatch.StartNew();
        PdfExtractionResult extraction;
        if (useDocling)
        {
            await JobRepo.UpdateProgressAsync(
                ds,
                job.JobId,
                "document_intelligence",
                null,
                null,
                ct);
            if (documentIntelligence
                    .NativeTextCoverageReconciliationEnabled
                || documentIntelligence.NativePdfImageInventoryEnabled)
            {
                var nativeTextStopwatch =
                    Stopwatch.StartNew();
                try
                {
                    nativeTextLayerExtraction =
                        PdfExtractor.Extract(absPath, ct);
                }
                catch (OperationCanceledException)
                    when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Native PDF text coverage audit failed open job={JobId} doc={DocPath}",
                        job.JobId,
                        relDocPath);
                }
                finally
                {
                    nativeTextStopwatch.Stop();
                    nativeTextExtractionMs =
                        nativeTextStopwatch.ElapsedMilliseconds;
                }
            }
            doclingForceOcr = ResolveDoclingForceOcr(
                documentIntelligence,
                nativeTextLayerExtraction);
            if (doclingForceOcr && !documentIntelligence.ForceOcr)
            {
                _log.LogInformation(
                    "Docling adaptive force OCR enabled job={JobId} doc={DocPath} native_signals={NativeSignals}",
                    job.JobId,
                    relDocPath,
                    string.Join(
                        ',',
                        nativeTextLayerExtraction?.Quality.Signals
                        ?? Array.Empty<string>()));
            }
            doclingConversion = await RunWithJobHeartbeatAsync(
                ds,
                job,
                workerId,
                async operationCt =>
                {
                    using (await _bulkheads.AcquireOcrAsync(operationCt))
                        return await doclingClient.ConvertPdfAsync(
                            absPath,
                            operationCt,
                            doclingForceOcr);
                },
                ct);
            extraction = DoclingLegacyExtractionAdapter.Project(doclingConversion);
        }
        else
        {
            extraction = PdfExtractor.Extract(absPath, ct);
        }
        swExtract.Stop();
        extractMs = swExtract.ElapsedMilliseconds;
        var nativeExtractionQuality =
            nativeTextLayerExtraction?.Quality
            ?? extraction.Quality;
        var ocrAttempted = useDocling && documentIntelligence.DoOcr;
        var ocrApplied = useDocling
            && documentIntelligence.DoOcr
            && ResolveDoclingTimingCount(doclingConversion, "ocr") > 0;
        string? ocrLanguages = null;
        PdfOcrDiagnostics? ocrDiagnostics = useDocling
            ? BuildDoclingOcrDiagnostics(
                documentIntelligence,
                doclingConversion!,
                extraction,
                doclingForceOcr)
            : null;
        if (useDocling)
            ocrMs = ResolveDoclingTimingMs(doclingConversion!, "ocr");
        var nativeExtraction = nativeTextLayerExtraction ?? extraction;
        var fullDocumentOcrRecommended =
            !useDocling && nativeExtraction.Quality.OcrRecommended;
        var imagePageOcrRecommended =
            !useDocling && ShouldAttemptImagePageOcr(ingest, nativeExtraction);
        var ocrRequiredButDisabled = useDocling
            ? nativeExtraction.Quality.OcrRecommended && !documentIntelligence.DoOcr
            : IsOcrRequiredButDisabled(
                ingest,
                fullDocumentOcrRecommended,
                imagePageOcrRecommended);
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
        if (ocrRequiredButDisabled && !useDocling)
        {
            ocrDiagnostics = PdfOcrTextExtractor.BuildOcrDisabledDiagnostics(
                ingest,
                nativeExtraction,
                imagePageOcrRecommended);
        }

        if (!useDocling
            && ingest.OcrEnabled
            && (fullDocumentOcrRecommended || imagePageOcrRecommended))
        {
            ocrAttempted = true;
            var forceFullDocumentOcr = fullDocumentOcrRecommended && PdfOcrTextExtractor.ShouldForceOcrNativeText(nativeExtraction);
            await ReportOcrWaitingForSlotHeartbeatAsync(
                ds,
                job.JobId,
                workerId,
                fullDocumentOcrRecommended,
                imagePageOcrRecommended,
                nativeExtraction.Pages.Count,
                forceFullDocumentOcr,
                0,
                TimeSpan.Zero,
                ct);
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
                ct,
                onHeartbeatAsync: (heartbeat, elapsed, operationCt) => ReportOcrWaitingForSlotHeartbeatAsync(
                    ds,
                    job.JobId,
                    workerId,
                    fullDocumentOcrRecommended,
                    imagePageOcrRecommended,
                    nativeExtraction.Pages.Count,
                    forceFullDocumentOcr,
                    heartbeat,
                    elapsed,
                    operationCt)))
            {
                ocrLanguages = PdfOcrTextExtractor.ResolveLanguagesForDocument(absPath, ingest, nativeExtraction);
                if (fullDocumentOcrRecommended)
                {
                    await ReportFullDocumentOcrHeartbeatAsync(
                        ds,
                        job.JobId,
                        workerId,
                        nativeExtraction.Pages.Count,
                        ocrLanguages,
                        forceFullDocumentOcr,
                        0,
                        TimeSpan.Zero,
                        ct);
                    var fullOcrResult = await RunWithJobHeartbeatAsync(
                        ds,
                        job,
                        workerId,
                        operationCt => PdfOcrTextExtractor.TryExtractWithDiagnosticsAsync(absPath, ingest, operationCt, ocrLanguages, forceFullDocumentOcr),
                        ct,
                        onHeartbeatAsync: (heartbeat, elapsed, operationCt) => ReportFullDocumentOcrHeartbeatAsync(
                            ds,
                            job.JobId,
                            workerId,
                            nativeExtraction.Pages.Count,
                            ocrLanguages,
                            forceFullDocumentOcr,
                            heartbeat,
                            elapsed,
                            operationCt));
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

                    var imageMergeBase = ResolveImageOcrMergeBase(nativeExtraction, ocrExtraction, fullOcrApplied);
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
        CanonicalDocument? canonicalRetrievalDocument = null;
        NativeTextCoverageReconciliationSummary?
            nativeTextReconciliation = null;
        NativePdfImageInventorySummary?
            nativePdfImageInventory = null;
        if (doclingConversion?.Document.JsonContent is { } doclingDocument)
        {
            var swCanonicalProjection = Stopwatch.StartNew();
            canonicalRetrievalDocument =
                DoclingCanonicalDocumentAdapter.Project(
                    new(
                        docId,
                        DocumentFoundationRepo.BuildStableRevisionId(
                            tenantId,
                            docId,
                            job.Version),
                        hashHex,
                        size,
                        Path.GetFileName(
                            relDocPath.Replace('\\', '/')),
                        hashHex,
                        "canonical_projection",
                        documentIntelligence.EngineVersion,
                        documentIntelligence.DeploymentRevision,
                        doclingConversion.Confidence?.MeanScore),
                    doclingDocument);
            nativeTextReconciliation =
                CanonicalNativeTextCoverageReconciler.Apply(
                    canonicalRetrievalDocument,
                    nativeTextLayerExtraction,
                    documentIntelligence
                        .NativeTextCoverageReconciliationEnabled,
                    documentIntelligence
                        .NativeTextCoverageMinimumLineCoverage);
            nativePdfImageInventory =
                CanonicalNativePdfImageInventoryReconciler.Apply(
                    canonicalRetrievalDocument,
                    nativeTextLayerExtraction,
                    documentIntelligence.NativePdfImageInventoryEnabled);
            swCanonicalProjection.Stop();
            canonicalProjectionMs =
                swCanonicalProjection.ElapsedMilliseconds;
            _log.LogInformation(
                "Native text coverage reconciliation job={JobId} doc={DocPath} enabled={Enabled} native_pages={NativePages} audited_pages={AuditedPages} candidate_lines={CandidateLines} recovered_lines={RecoveredLines} recovered_blocks={RecoveredBlocks} recovered_chars={RecoveredChars} skipped_low_quality_pages={SkippedLowQualityPages} minimum_coverage={MinimumCoverage} native_extract_ms={NativeExtractMs} reconcile_ms={ReconcileMs}",
                job.JobId,
                relDocPath,
                nativeTextReconciliation.Enabled,
                nativeTextReconciliation.NativePageCount,
                nativeTextReconciliation.AuditedPageCount,
                nativeTextReconciliation.CandidateLineCount,
                nativeTextReconciliation.RecoveredLineCount,
                nativeTextReconciliation.RecoveredBlockCount,
                nativeTextReconciliation.RecoveredCharacterCount,
                nativeTextReconciliation.SkippedLowQualityPageCount,
                nativeTextReconciliation.MinimumLineCoverage,
                nativeTextExtractionMs,
                nativeTextReconciliation.DurationMs);
            _log.LogInformation(
                "Native PDF image inventory job={JobId} doc={DocPath} enabled={Enabled} native_pages={NativePages} candidate_images={CandidateImages} added_figures={AddedFigures} enriched_pages={EnrichedPages} duration_ms={DurationMs}",
                job.JobId,
                relDocPath,
                nativePdfImageInventory.Enabled,
                nativePdfImageInventory.NativePageCount,
                nativePdfImageInventory.CandidateImageCount,
                nativePdfImageInventory.AddedFigureCount,
                nativePdfImageInventory.EnrichedPageCount,
                nativePdfImageInventory.DurationMs);
        }
        var swSections = Stopwatch.StartNew();
        var sections = canonicalRetrievalDocument is null
            ? DocumentSectionExtractor.Extract(pages)
            : CanonicalDocumentSectionProjector.Project(
                canonicalRetrievalDocument);
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
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks;
        if (doclingConversion?.Document.JsonContent is { } canonicalSource
            && canonicalRetrievalDocument is not null)
        {
            retrievalChunks = DoclingCanonicalRetrievalProjector.Project(
                canonicalRetrievalDocument,
                canonicalSource,
                ingest.ChunkMaxWords,
                ingest.ChunkMinWords);
        }
        else
        {
            retrievalChunks = RetrievalChunkProjector.ProjectStructureAware(
                sections,
                units,
                ingest.ChunkMaxWords,
                ingest.ChunkOverlapWords,
                ingest.ChunkMinWords);
        }
        IReadOnlyList<ExtractedDocumentUnit>? structuredProfileUnits =
            doclingConversion?.Document.JsonContent is not null
            && canonicalRetrievalDocument is not null
                ? CanonicalProfileInputProjector.Project(
                    retrievalChunks
                        .Where(ShouldPublishRetrievalChunk)
                        .ToArray())
                : null;
        var retrievalChunkQuality = BuildRetrievalChunkQualitySummary(retrievalChunks);
        var chunks = retrievalChunks
            .Where(ShouldEmbedRetrievalChunk)
            .Select(chunk => new Chunk(chunk.ChunkIndex, chunk.PageStart, chunk.PageEnd, chunk.Text))
            .ToList();
        swChunking.Stop();
        chunkingMs = swChunking.ElapsedMilliseconds;
        _log.LogInformation(
            "Ingestion chunking summary job={JobId} doc={DocPath} retrieval_chunks={RetrievalChunks} searchable_chunks={SearchableChunks} embedding_chunks={EmbeddingChunks} rejected_chunks={RejectedChunks} navigation_chunks={NavigationChunks} sparse_rejected={SparseRejected} replacement_rejected={ReplacementRejected} empty_rejected={EmptyRejected} ocr_noise_rejected={OcrNoiseRejected} other_rejected={OtherRejected} manual_review={ManualReview} canonical_projection_ms={CanonicalProjectionMs} chunking_ms={ChunkingMs}",
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
            canonicalProjectionMs,
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
        var exactMatchEntries = ExactMatchEntryExtractor.Extract(
            structuredProfileUnits ?? units);
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

        int dim;
        var swTeiWarmup = Stopwatch.StartNew();
        await TouchJobLockAsync(ds, job.JobId, workerId, ct);
        await ThrowIfJobCanceledAsync(ds, job, ct);
        dim = await RunWithJobHeartbeatAsync(
            ds,
            job,
            workerId,
            async operationCt =>
            {
                using (await _bulkheads.AcquireHeavyComputeAsync(operationCt))
                using (await _teiGovernor.AcquireIngestionAsync(
                    ingest.TeiInteractiveQuietPeriodMs,
                    operationCt))
                using (await _bulkheads.AcquireTeiAsync(operationCt))
                {
                    using var teiCts = CreateTimeoutCts(
                        operationCt,
                        ingest.TeiTimeoutSeconds);
                    var teiToken = teiCts?.Token ?? operationCt;
                    return await TeiClient.GetVectorDimAsync(
                        tei,
                        rag.EmbeddingsModel,
                        teiToken);
                }
            },
            ct);
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
                    var embeddingText = CleanTextForIndexing(ResolveEmbeddingText(projectedChunk.ChunkIndex, projectedChunk.Text, contextualTextByChunkIndex));
                    var sectionTitle = CleanOptionalTextForIndexing(
                        projectedChunk.SectionTitle
                        ?? ResolveSectionTitle(
                            projectedChunk.SectionOrdinal,
                            sectionTitleByOrdinal));
                    var headingPath = CleanOptionalTextForIndexing(
                        projectedChunk.HeadingPath
                        ?? ContextualTextProjector.ResolveHeadingPath(
                            projectedChunk.SectionOrdinal,
                            headingPathBySectionOrdinal));
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
        CanonicalIngestionBundle? canonicalBundle = null;
        if (ingest.CanonicalArtifactsEnabled)
        {
            var codeRevision = canonicalCodeRevision!;

            var revisionId = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, job.Version);
            if (doclingConversion is not null)
            {
                var stages = DoclingIngestionStageManifestFactory.Create(
                    documentIntelligence,
                    ingest,
                    rag,
                    doclingConversion,
                    new(
                        canonicalProjectionMs,
                        chunkingMs,
                        embeddingTotalMs,
                        qdrantUpsertTotalMs),
                    codeRevision,
                    nativeTextReconciliation,
                    nativePdfImageInventory);
                canonicalBundle = DoclingCanonicalBundleFactory.Create(new(
                    docId,
                    revisionId,
                    DocumentFoundationRepo.BuildStableProcessingRunId(job.JobId),
                    job.Version,
                    hashHex,
                    size,
                    Path.GetFileName(relDocPath.Replace('\\', '/')),
                    codeRevision,
                    DateTimeOffset.UtcNow,
                    IngestionHardwareProfiler.Capture(),
                    documentIntelligence,
                    stages,
                    canonicalRetrievalDocument
                    ?? throw new InvalidOperationException(
                        "The reconciled canonical document is unavailable."),
                    doclingConversion,
                    retrievalChunks.Where(ShouldPublishRetrievalChunk).ToArray()));
            }
            else
            {
                var stages = LegacyIngestionStageManifestFactory.Create(
                    ingest,
                    rag,
                    extraction,
                    ocrAttempted,
                    ocrApplied,
                    ocrLanguages,
                    ocrDiagnostics,
                    new(
                        extractMs,
                        ocrMs,
                        sectionMs,
                        unitMs,
                        chunkingMs,
                        embeddingTotalMs,
                        qdrantUpsertTotalMs),
                    codeRevision);
                canonicalBundle = LegacyCanonicalBundleFactory.Create(new(
                    docId,
                    revisionId,
                    DocumentFoundationRepo.BuildStableProcessingRunId(job.JobId),
                    job.Version,
                    hashHex,
                    size,
                    Path.GetFileName(relDocPath.Replace('\\', '/')),
                    codeRevision,
                    DateTimeOffset.UtcNow,
                    IngestionHardwareProfiler.Capture(),
                    stages,
                    extraction,
                    sections,
                    retrievalChunks.Where(ShouldPublishRetrievalChunk).ToArray()));
            }
        }
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
            capabilityAProfileSeed: job.CapabilityAProfileSeed,
            canonicalBundle: canonicalBundle,
            structuredProfileUnits: structuredProfileUnits);
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
            "Ingestion stage timings job={JobId} doc={DocPath} total_ms={TotalMs} hash_ms={HashMs} extract_ms={ExtractMs} native_text_extract_ms={NativeTextExtractMs} ocr_ms={OcrMs} extraction_source={ExtractionSource} sections_ms={SectionsMs} units_ms={UnitsMs} chunking_ms={ChunkingMs} exact_ms={ExactMs} contextual_ms={ContextualMs} tei_warmup_ms={TeiWarmupMs} qdrant_ensure_ms={QdrantEnsureMs} embedding_ms={EmbeddingMs} qdrant_upsert_ms={QdrantUpsertMs} publish_ms={PublishMs} cleanup_ms={CleanupMs} pages={Pages} sections={Sections} units={Units} chunks={Chunks} exact_entries={ExactEntries} contextual_entries={ContextualEntries} extraction_quality={ExtractionQuality} ocr_recommended={OcrRecommended}",
            job.JobId,
            relDocPath,
            swTotal.ElapsedMilliseconds,
            hashMs,
            extractMs,
            nativeTextExtractionMs,
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

    internal static PdfExtractionResult ResolveImageOcrMergeBase(
        PdfExtractionResult nativeExtraction,
        PdfExtractionResult? currentOcrExtraction,
        bool fullOcrApplied)
    {
        if (!fullOcrApplied || currentOcrExtraction is null)
            return nativeExtraction;

        return ShouldPreferImageOcrFromNativeBase(nativeExtraction)
            ? nativeExtraction
            : currentOcrExtraction;
    }

    private static bool ShouldPreferImageOcrFromNativeBase(PdfExtractionResult nativeExtraction)
    {
        var quality = nativeExtraction.Quality;
        return string.Equals(quality.TextStatus, "empty_text", StringComparison.Ordinal)
               || quality.TotalWordCount <= 0
               || nativeExtraction.Pages.All(static page => string.IsNullOrWhiteSpace(page.Text));
    }

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
        var text = CleanTextForIndexing(projectedChunk.Text);
        var cleanEmbeddingText = CleanTextForIndexing(embeddingText);
        var cleanSectionTitle = CleanOptionalTextForIndexing(sectionTitle);
        var cleanHeadingPath = CleanOptionalTextForIndexing(headingPath);
        var textCleaningChanged = !string.Equals(projectedChunk.Text, text, StringComparison.Ordinal);
        var usesContextualText = !string.Equals(text, cleanEmbeddingText, StringComparison.Ordinal);
        var sourceUnitOrdinals = projectedChunk.SourceUnitOrdinals ?? Array.Empty<int>();
        var sourceUnitCount = projectedChunk.SourceUnitCount ?? sourceUnitOrdinals.Count;
        var pageSpan = projectedChunk.PageStart == projectedChunk.PageEnd
            ? projectedChunk.PageStart.ToString(CultureInfo.InvariantCulture)
            : $"{projectedChunk.PageStart.ToString(CultureInfo.InvariantCulture)}-{projectedChunk.PageEnd.ToString(CultureInfo.InvariantCulture)}";
        var sourceUnitSpan = projectedChunk.SourceUnitStartOrdinal.HasValue && projectedChunk.SourceUnitEndOrdinal.HasValue
            ? projectedChunk.SourceUnitStartOrdinal.Value == projectedChunk.SourceUnitEndOrdinal.Value
                ? projectedChunk.SourceUnitStartOrdinal.Value.ToString(CultureInfo.InvariantCulture)
                : $"{projectedChunk.SourceUnitStartOrdinal.Value.ToString(CultureInfo.InvariantCulture)}-{projectedChunk.SourceUnitEndOrdinal.Value.ToString(CultureInfo.InvariantCulture)}"
            : null;

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
            ["page_span"] = pageSpan,
            ["offset_start"] = projectedChunk.OffsetStart,
            ["offset_end"] = projectedChunk.OffsetEnd,
            ["hash_doc"] = hashHex,
            ["created_at"] = nowIso,
            ["updated_at"] = nowIso,
            ["text"] = text,
            ["embed_text"] = cleanEmbeddingText,
            ["text_cleaning_version"] = "mojibake_repair_v2",
            ["text_cleaning_changed"] = textCleaningChanged,
            ["text_mojibake_after"] = LooksLikeMojibakeForDiagnostics(text)
                || LooksLikeMojibakeForDiagnostics(cleanEmbeddingText)
                || LooksLikeMojibakeForDiagnostics(cleanSectionTitle)
                || LooksLikeMojibakeForDiagnostics(cleanHeadingPath),
            ["embedding_basis"] = usesContextualText ? ContextualTextProjector.SchemaVersion : "chunk_text",
            ["context_schema_version"] = ContextualTextProjector.SchemaVersion,
            ["embedding_model"] = embeddingModel,
            ["embedding_input_format"] = embeddingInputFormat,
            ["section_ordinal"] = projectedChunk.SectionOrdinal,
            ["unit_ordinal"] = projectedChunk.UnitOrdinal,
            ["source_unit_ordinals"] = sourceUnitOrdinals,
            ["source_unit_start_ordinal"] = projectedChunk.SourceUnitStartOrdinal,
            ["source_unit_end_ordinal"] = projectedChunk.SourceUnitEndOrdinal,
            ["source_unit_span"] = sourceUnitSpan,
            ["source_unit_count"] = sourceUnitCount,
            ["chunk_composition"] = projectedChunk.ChunkComposition,
            ["canonical_block_ids"] = projectedChunk.CanonicalBlockIds
                ?? Array.Empty<string>(),
            ["canonical_span_ids"] = projectedChunk.CanonicalSpanIds
                ?? Array.Empty<string>(),
            ["canonical_table_cell_ids"] =
                projectedChunk.CanonicalTableCellIds
                ?? Array.Empty<string>(),
            ["canonical_context_block_ids"] =
                projectedChunk.CanonicalContextBlockIds
                ?? Array.Empty<string>(),
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
            ["section_title"] = cleanSectionTitle,
            ["heading_path"] = cleanHeadingPath ?? cleanSectionTitle,
            ["prev_chunk_id"] = chunkLinks?.PreviousChunkId?.ToString(),
            ["next_chunk_id"] = chunkLinks?.NextChunkId?.ToString(),
            ["same_section_chunk_id"] = chunkLinks?.SameSectionChunkId?.ToString(),
            ["ingestion_version"] = ingestionVersion
        };
    }

    private static string CleanTextForIndexing(string? text)
        => PdfTextSanitizer.ForStorage(text ?? string.Empty).Trim();

    private static string? CleanOptionalTextForIndexing(string? text)
    {
        var cleaned = CleanTextForIndexing(text);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static bool LooksLikeMojibakeForDiagnostics(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.Contains('\ufffd', StringComparison.Ordinal))
        {
            return true;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is >= '\u0080' and <= '\u009f')
                return true;

            if (i + 1 >= text.Length)
                continue;

            var next = text[i + 1];
            if ((ch is '\u00c2' or '\u00c3' or '\u00c5') && next is >= '\u0080' and <= '\u00bf')
                return true;

            if (ch == '\u00e2' && (next is '\u0080' or '\u20ac'))
                return true;
        }

        return false;
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

    [GeneratedRegex(@"^\d{1,4}(?:\s*(?:[-\u2013\u2014\u2022\u00b7/]|to|a|and|et|und)\s*\d{1,4}|\s+\d{1,4})+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowSubstanceNumericRangeRegex();

    [GeneratedRegex(@"^\d{1,4}(?:[,.]\d+)?\s+(?:\p{Ll}|g|kg|mg|ml|cl|l|oz|lb|mm|cm|m|km|v|a|w|hz|rpm|s|sec|secs|seconds?|secondes?|min|mins?|minutes?|h|hr|hrs?|hours?|heures?|units?|unites?|items?|elements?|entries?|parts?|pieces?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LowSubstanceQuantityLeadRegex();

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}'\u2019\-]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SubstantiveWordRegex();

    [GeneratedRegex(@"[\p{L}\p{N}%]+", RegexOptions.CultureInvariant)]
    private static partial Regex OcrLayoutTokenRegex();

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
