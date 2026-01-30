using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Bootstrap;
using System.Collections.Concurrent;

sealed class FileWatcherService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<FileWatcherService> _log;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingUpserts
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDeletes
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    public FileWatcherService(IServiceProvider sp, ILogger<FileWatcherService> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        IngestionOptions opt;
        BootstrapOptions bootstrap;
        NpgsqlDataSource ds;

        using (var scope = _sp.CreateScope())
        {
            opt = scope.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;
            bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
            ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        }

        if (!opt.WatcherEnabled)
        {
            _log.LogInformation("FileWatcher: WatcherEnabled=false (désactivé via config).");
            return;
        }

        var root = opt.DocumentsRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            _log.LogWarning("FileWatcher: DocumentsRoot is empty (Ingestion:DocumentsRoot)");
            return;
        }

        if (!Directory.Exists(root))
        {
            _log.LogWarning("FileWatcher: DocumentsRoot does not exist: {Root}", root);
            return;
        }

        var missingGrace = TimeSpan.FromSeconds(Math.Clamp(opt.MissingGraceSeconds, 5, 24 * 3600));

        Guid tenantId;
        try
        {
            tenantId = await ResolveSingleTenantIdAsync(ds, bootstrap, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FileWatcher: cannot resolve tenant id");
            return;
        }

        using var watcher = new FileSystemWatcher(root)
        {
            Filter = "*.pdf",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024, // max recommandé (64KB)
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "created", Debounce, ct);
        watcher.Changed += (_, e) => ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "changed", Debounce, ct);
        watcher.Renamed += (_, e) =>
        {
            ScheduleDelete(ds, tenantId, root, e.OldFullPath, "renamed(old)", missingGrace, ct);
            ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "renamed(new)", Debounce, ct);
        };
        watcher.Deleted += (_, e) => ScheduleDelete(ds, tenantId, root, e.FullPath, "deleted", missingGrace, ct);
        watcher.Error += (_, e) => _log.LogWarning(e.GetException(), "FileWatcher: watcher error (buffer overflow possible); scanner will resync.");

        _log.LogInformation("FileWatcher: enabled on {Root} (MissingGrace={Grace}s)", root, (int)missingGrace.TotalSeconds);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void ScheduleUpsert(
        NpgsqlDataSource ds,
        Guid tenantId,
        string root,
        IngestionOptions opt,
        string fullPath,
        string reason,
        TimeSpan debounce,
        CancellationToken appCt)
    {
        if (!fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return;

        var rel = IngestionPathFilter.SafeRelPath(root, fullPath);
        if (rel is null || IngestionPathFilter.ShouldIgnoreRel(rel))
            return;

        // Si un delete était planifié pour ce doc, on l’annule (upsert prend le dessus)
        CancelPending(_pendingDeletes, rel);

        var cts = new CancellationTokenSource();
        _pendingUpserts.AddOrUpdate(rel, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { }
            // IMPORTANT: ne pas Dispose ici, sinon ObjectDisposedException dans le Task
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(appCt, cts.Token);

                await Task.Delay(debounce, linked.Token);

                if (!File.Exists(fullPath))
                    return;

                FileInfo fi;
                try { fi = new FileInfo(fullPath); }
                catch { return; }

                // Evite de traiter un fichier encore en cours de copie
                var now = DateTime.UtcNow;
                var ageSeconds = (now - fi.LastWriteTimeUtc).TotalSeconds;
                var minAge = Math.Max(0, opt.MinFileAgeSeconds);

                if (ageSeconds < minAge)
                {
                    var wait = TimeSpan.FromSeconds(minAge - ageSeconds);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, linked.Token);
                }

                if (!File.Exists(fullPath))
                    return;

                var category = DeriveCategory(rel, opt);

                await using var conn = await ds.OpenConnectionAsync(linked.Token);
                await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, new FileInfo(fullPath), linked.Token);

                _log.LogInformation("FileWatcher: upsert enqueued ({Reason}) for {Doc}", reason, rel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "FileWatcher: error scheduling upsert for {Path}", fullPath);
            }
            finally
            {
                // On retire seulement si c’est encore le même CTS
                if (_pendingUpserts.TryGetValue(rel, out var current) && ReferenceEquals(current, cts))
                    _pendingUpserts.TryRemove(rel, out _);

                try { cts.Dispose(); } catch { }
            }
        });
    }

    private void ScheduleDelete(
        NpgsqlDataSource ds,
        Guid tenantId,
        string root,
        string fullPath,
        string reason,
        TimeSpan missingGrace,
        CancellationToken appCt)
    {
        if (!fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return;

        var rel = IngestionPathFilter.SafeRelPath(root, fullPath);
        if (rel is null || IngestionPathFilter.ShouldIgnoreRel(rel))
            return;

        // Si un upsert est planifié, on l’annule (delete prend le dessus)
        CancelPending(_pendingUpserts, rel);

        var cts = new CancellationTokenSource();
        _pendingDeletes.AddOrUpdate(rel, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { }
            // IMPORTANT: ne pas Dispose ici, sinon ObjectDisposedException dans le Task
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(appCt, cts.Token);

                await Task.Delay(missingGrace, linked.Token);

                // Si le fichier est revenu, on ne fait rien
                if (File.Exists(fullPath))
                    return;

                // P0: le watcher ne fait PAS de delete final => il marque "missing".
                await using var conn = await ds.OpenConnectionAsync(linked.Token);
                await IngestionEnqueue.MarkMissingAsync(conn, tenantId, rel, linked.Token);

                _log.LogInformation("FileWatcher: marked missing ({Reason}) for {Doc}", reason, rel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "FileWatcher: error scheduling delete/missing for {Path}", fullPath);
            }
            finally
            {
                if (_pendingDeletes.TryGetValue(rel, out var current) && ReferenceEquals(current, cts))
                    _pendingDeletes.TryRemove(rel, out _);

                try { cts.Dispose(); } catch { }
            }
        });
    }

    private static void CancelPending(ConcurrentDictionary<string, CancellationTokenSource> map, string key)
    {
        if (map.TryRemove(key, out var old))
        {
            try { old.Cancel(); } catch { }
            // IMPORTANT: Dispose dans le Task qui l’utilise
        }
    }

    private static string DeriveCategory(string docPath, IngestionOptions opt)
    {
        if (!opt.CategoryFromFirstFolder)
            return (opt.DefaultCategory ?? "general").Trim().ToLowerInvariant();

        var parts = docPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return parts[0].Trim().ToLowerInvariant();

        return (opt.DefaultCategory ?? "general").Trim().ToLowerInvariant();
    }

    private static async Task<Guid> ResolveSingleTenantIdAsync(NpgsqlDataSource ds, BootstrapOptions bootstrap, CancellationToken ct)
    {
        if (bootstrap.TenantId != Guid.Empty)
            return bootstrap.TenantId;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var tid = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC LIMIT 1;"
        );
        if (tid is null || tid == Guid.Empty)
            throw new Exception("No tenant found. Enable Bootstrap or create a tenant in DB.");

        return tid.Value;
    }
}
