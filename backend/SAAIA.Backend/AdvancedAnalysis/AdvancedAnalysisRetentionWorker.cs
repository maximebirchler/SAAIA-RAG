using Microsoft.Extensions.Options;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed class AdvancedAnalysisRetentionWorker : BackgroundService
{
    private const int MaximumBatchesPerSweep = 10;
    private readonly AdvancedAnalysisJobStore _store;
    private readonly AdvancedAnalysisOptions _options;
    private readonly ILogger<AdvancedAnalysisRetentionWorker> _logger;

    public AdvancedAnalysisRetentionWorker(
        AdvancedAnalysisJobStore store,
        IOptions<AdvancedAnalysisOptions> options,
        ILogger<AdvancedAnalysisRetentionWorker> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = Math.Clamp(
            _options.RetentionSweepMilliseconds,
            60_000,
            86_400_000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await SweepOnceAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Purged {JobCount} expired advanced-analysis job(s) and their dependent tool traces",
                        deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Advanced-analysis retention sweep failed");
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(
            _options.RetentionDeleteBatchSize,
            1,
            10_000);
        var total = 0;
        for (var batch = 0; batch < MaximumBatchesPerSweep; batch++)
        {
            var deleted = await _store.PurgeExpiredAsync(
                batchSize,
                cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < batchSize)
                break;
        }
        return total;
    }
}
