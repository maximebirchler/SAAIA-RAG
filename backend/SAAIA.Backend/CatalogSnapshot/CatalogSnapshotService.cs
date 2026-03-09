using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SAAIA.Backend.CatalogSnapshot;

public sealed class CatalogSnapshotService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<CatalogSnapshotService> _log;
    private bool _didInitialDelay;

    public CatalogSnapshotService(IServiceProvider sp, ILogger<CatalogSnapshotService> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CatalogSnapshotOptions opt;
            NpgsqlDataSource ds;

            using (var scope = _sp.CreateScope())
            {
                opt = scope.ServiceProvider.GetRequiredService<IOptions<CatalogSnapshotOptions>>().Value;
                ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            }

            if (!opt.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            // Startup delay (only once)
            if (!_didInitialDelay && opt.StartupDelaySeconds > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(opt.StartupDelaySeconds, 0, 600)), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

                _didInitialDelay = true;
            }

            try
            {
                await CatalogSnapshotBuilder.BuildAllActiveTenantsAsync(ds, opt, _log, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "CatalogSnapshotService error");
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(opt.IntervalSeconds, 30, 24 * 3600));
            await Task.Delay(delay, ct);
        }
    }
}
