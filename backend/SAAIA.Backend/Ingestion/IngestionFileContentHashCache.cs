sealed record IngestionFileContentHashResolution(string HashHex, bool CacheHit);

sealed class IngestionFileContentHashCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IngestionFileContentHashResolution> ResolveAsync(
        IngestionDuplicateFileCandidate candidate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var key = NormalizePath(candidate.AbsolutePath);
        var lastWriteTicks = NormalizeLastWriteTime(candidate.LastWriteTimeUtc).Ticks;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached)
                && cached.FileSize == candidate.FileSize
                && cached.LastWriteUtcTicks == lastWriteTicks)
            {
                return new(cached.HashHex, CacheHit: true);
            }
        }

        var hash = await IngestionDuplicateFilePlanner.ComputeSha256HexAsync(
            candidate.AbsolutePath,
            ct).ConfigureAwait(false);

        lock (_gate)
        {
            _entries[key] = new(candidate.FileSize, lastWriteTicks, hash);
        }

        return new(hash, CacheHit: false);
    }

    public void RetainOnly(IEnumerable<string> absolutePaths)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        var retained = absolutePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var key in _entries.Keys
                         .Where(key => !retained.Contains(key))
                         .ToArray())
            {
                _entries.Remove(key);
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);

    private static DateTime NormalizeLastWriteTime(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record Entry(
        long FileSize,
        long LastWriteUtcTicks,
        string HashHex);
}
