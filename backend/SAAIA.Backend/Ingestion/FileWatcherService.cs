using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
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

    public FileWatcherService(IServiceProvider sp, ILogger<FileWatcherService> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // ⚠️ IMPORTANT: ne pas récupérer NpgsqlDataSource dans un scope qui pourrait le disposer.
        var opt = _sp.GetRequiredService<IOptions<IngestionOptions>>().Value;
        var bootstrap = _sp.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        var ds = _sp.GetRequiredService<NpgsqlDataSource>();

        var debounce = TimeSpan.FromMilliseconds(Math.Clamp(opt.WatcherDebounceMs, 50, 10_000));

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

        // ✅ Normalise en chemin absolu (robuste en service Windows / docker)
        root = Path.GetFullPath(root);

        try
        {
            Directory.CreateDirectory(root); // ✅ évite un stop bête si le dossier n’existe pas
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FileWatcher: cannot create/open DocumentsRoot: {Root}", root);
            return;
        }

        var missingGrace = TimeSpan.FromSeconds(Math.Clamp(opt.MissingGraceSeconds, 5, 24 * 3600));

        Guid tenantId;
        try
        {
            tenantId = await ResolveSingleTenantIdAsync(ds, bootstrap, ct);
            _log.LogInformation("FileWatcher: tenant resolved to {TenantId}", tenantId);
        }
        
        catch (Exception ex)
        {
            _log.LogError(ex, "FileWatcher: cannot resolve tenant id");
            return;
        }

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                Filter = "*.pdf",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FileWatcher: cannot start watcher on {Root}", root);
            return;
        }

        using (watcher)
        {
            watcher.Created += (_, e) => ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "created", debounce, ct);
            watcher.Changed += (_, e) => ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "changed", debounce, ct);
            watcher.Renamed += (_, e) =>
            {
                // Renames/moves can take a little time on some filesystems (Docker Desktop mounts, network shares…).
                // But we also want the UI catalog to reflect the move quickly.
                // => Use a shorter grace for the old path on rename, while keeping the global grace for true deletes.
                var renameGrace = TimeSpan.FromSeconds(Math.Min(missingGrace.TotalSeconds, 3));
                ScheduleDelete(ds, tenantId, root, e.OldFullPath, "renamed(old)", renameGrace, ct);
                ScheduleUpsert(ds, tenantId, root, opt, e.FullPath, "renamed(new)", debounce, ct);
            };
            watcher.Deleted += (_, e) => ScheduleDelete(ds, tenantId, root, e.FullPath, "deleted", missingGrace, ct);

            watcher.Error += (_, e) =>
                _log.LogWarning(e.GetException(),
                    "FileWatcher: watcher error (buffer overflow possible); scanner will resync.");

            _log.LogInformation("FileWatcher: enabled on {Root} (MissingGrace={Grace}s, DebounceMs={Debounce})",
                root, (int)missingGrace.TotalSeconds, (int)debounce.TotalMilliseconds);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
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

        // Upsert prend le dessus
        CancelPending(_pendingDeletes, rel);

        var cts = new CancellationTokenSource();
        _pendingUpserts.AddOrUpdate(rel, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { }
            return cts; // pas de Dispose ici
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(appCt, cts.Token);

                await Task.Delay(debounce, linked.Token);

                if (!File.Exists(fullPath))
                    return;

                // 1) Age minimal
                var minAge = Math.Max(0, opt.MinFileAgeSeconds);
                if (minAge > 0)
                    await WaitMinAgeAsync(fullPath, minAge, linked.Token);

                if (!File.Exists(fullPath))
                    return;

                // 2) Fichier prêt (taille stable + ouverture possible)
                var ready = await WaitForFileReadyAsync(fullPath, maxAttempts: 8, delayMs: 250, linked.Token);
                if (!ready)
                    return;

                var fi = new FileInfo(fullPath);
                var category = DeriveCategory(rel, opt);

                await using var conn = await ds.OpenConnectionAsync(linked.Token);
                var restored = await IngestionEnqueue.TryRestoreMissingIndexedDocumentAsync(conn, tenantId, rel, fi, linked.Token);
                if (restored == IngestionEnqueue.ReturnedMissingIndexedDocumentOutcome.RestoredWithoutReingestion)
                {
                    _log.LogInformation(
                        "FileWatcher: restored indexed document without reingestion ({Reason}) for {Doc}",
                        reason, rel);
                    return;
                }
                if (restored == IngestionEnqueue.ReturnedMissingIndexedDocumentOutcome.ReactivatedForReindex)
                {
                    _log.LogInformation(
                        "FileWatcher: reactivated indexed document and will enqueue reindex ({Reason}) for changed source {Doc}",
                        reason, rel);
                }

                var state = await IngestionAutoUpsertGuard.LoadAsync(conn, tenantId, rel, linked.Token);
                if (IngestionAutoUpsertGuard.ShouldSuppressAutoUpsert(state, fi.Length, fi.LastWriteTimeUtc))
                {
                    _log.LogInformation(
                        "FileWatcher: auto-upsert suppressed after admin cancel ({Reason}) for {Doc}",
                        reason, rel);
                    return;
                }

                await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, linked.Token, isAutomatic: true, enqueueSource: "watcher");

                _log.LogInformation("FileWatcher: upsert enqueued ({Reason}) for {Doc}", reason, rel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "FileWatcher: error scheduling upsert for {Path}", fullPath);
            }
            finally
            {
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

        // Delete/missing prend le dessus
        CancelPending(_pendingUpserts, rel);

        var cts = new CancellationTokenSource();
        _pendingDeletes.AddOrUpdate(rel, cts, (_, old) =>
        {
            try { old.Cancel(); } catch { }
            return cts; // pas de Dispose ici
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

                // P0: le watcher ne fait PAS de delete final => il marque missing.
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
            // Dispose dans le Task qui l’utilise
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
        await using var conn = await ds.OpenConnectionAsync(ct);

        // 1) Si un tenant est fourni via config, ne l'accepter QUE s'il existe réellement.
        //    (Sinon: FK violations sur documents/api_keys, et Qdrant peut paraître "vide" car filtré par tenant.)
        if (bootstrap.TenantId != Guid.Empty)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM tenants WHERE tenant_id=@id LIMIT 1;",
                new { id = bootstrap.TenantId },
                cancellationToken: ct));

            if (exists.HasValue)
                return bootstrap.TenantId;
        }

        // 2) Fallback: premier tenant actif (profil single-tenant dev/prod).
        var tid = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC LIMIT 1;",
            cancellationToken: ct));

        if (tid is null || tid == Guid.Empty)
            throw new Exception("No tenant found. Enable Bootstrap or create a tenant in DB.");

        return tid.Value;
    }


    private static async Task WaitMinAgeAsync(string fullPath, int minAgeSeconds, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            var now = DateTime.UtcNow;
            var ageSeconds = (now - fi.LastWriteTimeUtc).TotalSeconds;

            if (ageSeconds < minAgeSeconds)
            {
                var wait = TimeSpan.FromSeconds(minAgeSeconds - ageSeconds);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
        }
        catch { /* ignore */ }
    }

    private static async Task<bool> WaitForFileReadyAsync(string fullPath, int maxAttempts, int delayMs, CancellationToken ct)
    {
        long? lastSize = null;

        for (var i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fi = new FileInfo(fullPath);
                if (!fi.Exists) return false;

                var size = fi.Length;

                // taille stable sur 2 ticks consécutifs
                if (lastSize.HasValue && lastSize.Value == size)
                {
                    // tentons une ouverture lecture (si verrou exclusif, ça échoue)
                    using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }

                lastSize = size;
            }
            catch
            {
                // ignore -> retry
            }

            await Task.Delay(delayMs, ct);
        }

        return false;
    }
}
