using System.Security.Cryptography;

/// <summary>One immutable input for every extractor in an ingestion attempt.</summary>
internal sealed class IngestionSourceSnapshot : IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly string _directory;
    public string Path { get; }
    public byte[] Hash { get; }
    public long Length { get; }
    public DateTime SourceLastWriteTimeUtc { get; }

    private IngestionSourceSnapshot(string path, string directory, FileStream lease,
        byte[] hash, long length, DateTime lastWrite)
        => (Path, _directory, _lease, Hash, Length, SourceLastWriteTimeUtc)
            = (path, directory, lease, hash, length, lastWrite);

    public static async Task<IngestionSourceSnapshot> CreateAsync(string sourcePath, CancellationToken ct)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SAAIA", "ingestion-snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(sourcePath));
        FileStream? lease = null;
        try
        {
            DateTime lastWrite;
            // Deny writes/deletes on Windows while copying; all engines subsequently read the copy.
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                lastWrite = File.GetLastWriteTimeUtc(sourcePath);
                await source.CopyToAsync(target, ct);
            }
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(lease, ct);
            lease.Position = 0;
            return new(path, directory, lease, hash, lease.Length, lastWrite);
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            File.Delete(path);
            Directory.Delete(directory);
            throw;
        }
    }

    public async Task AssertSourceUnchangedAsync(string sourcePath, CancellationToken ct)
    {
        if (!File.Exists(sourcePath)) throw new InvalidDataException("source_removed_during_ingestion");
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var currentHash = await SHA256.HashDataAsync(source, ct);
        if (source.Length != Length || !CryptographicOperations.FixedTimeEquals(Hash, currentHash))
            throw new InvalidDataException("source_changed_during_ingestion");
    }

    public async ValueTask DisposeAsync()
    {
        await _lease.DisposeAsync();
        File.Delete(Path);
        Directory.Delete(_directory); // Only the known file and the now-empty owned directory.
    }
}
