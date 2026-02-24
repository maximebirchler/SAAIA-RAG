using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Option B: downloads heavy assets (LLM binaries, models, etc.) from the internet.
/// Designed to be safe: assets are verified with SHA256 when provided and written atomically.
/// </summary>
internal sealed class DownloadManager
{
    internal sealed record AssetSpec(
        string Id,
        string Url,
        string TargetRelativePath,
        string? Sha256Hex = null);

    internal sealed record ProgressInfo(
        string AssetId,
        string Stage,
        long DownloadedBytes,
        long? TotalBytes,
        double? BytesPerSecond)
    {
        // Back-compat aliases used by some UI code
        public string Id => AssetId;
        public long BytesDownloaded => DownloadedBytes;
        public long? BytesTotal => TotalBytes;
    }

    private static string SaaiaRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA");

    private static string DownloadsDir => Path.Combine(SaaiaRoot, "downloads");

    private static string ManifestPath => Path.Combine(DownloadsDir, "manifest.json");

    private sealed record ManifestItem(
        string Id,
        string Url,
        string LocalPath,
        string? Sha256Hex,
        long Size,
        string InstalledAtUtc);

    public async Task<IReadOnlyList<string>> EnsureAssetsAsync(
        IReadOnlyList<AssetSpec> assets,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        if (assets is null || assets.Count == 0) return Array.Empty<string>();

        Directory.CreateDirectory(SaaiaRoot);
        Directory.CreateDirectory(DownloadsDir);

        var installedPaths = new List<string>();

        foreach (var a in assets)
        {
            ct.ThrowIfCancellationRequested();

            var targetAbs = Path.Combine(SaaiaRoot, a.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetAbs)!);

            // If already present and hash matches, keep.
            if (File.Exists(targetAbs))
            {
                if (string.IsNullOrWhiteSpace(a.Sha256Hex))
                {
                    installedPaths.Add(targetAbs);
                    continue;
                }

                progress?.Report(new ProgressInfo(a.Id, "verify", 0, null, null));
                var current = await ComputeSha256HexAsync(targetAbs, ct).ConfigureAwait(false);
                if (string.Equals(NormHex(current), NormHex(a.Sha256Hex), StringComparison.OrdinalIgnoreCase))
                {
                    installedPaths.Add(targetAbs);
                    continue;
                }

                // hash mismatch => replace
                try { File.Delete(targetAbs); } catch { }
            }

            progress?.Report(new ProgressInfo(a.Id, "download", 0, null, null));

            var part = targetAbs + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            var (sha, size) = await DownloadToFileWithHashAsync(a.Url, part, progress, a.Id, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(a.Sha256Hex) &&
                !string.Equals(NormHex(sha), NormHex(a.Sha256Hex), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(part); } catch { }
                throw new InvalidOperationException($"SHA256 mismatch for {a.Id}. Expected {a.Sha256Hex}, got {sha}.");
            }

            // Atomic replace
            try
            {
                if (File.Exists(targetAbs)) File.Delete(targetAbs);
                File.Move(part, targetAbs);
            }
            catch
            {
                // best effort cleanup
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }

            installedPaths.Add(targetAbs);

            // Update manifest
            try
            {
                var item = new ManifestItem(
                    a.Id,
                    a.Url,
                    targetAbs,
                    string.IsNullOrWhiteSpace(a.Sha256Hex) ? sha : a.Sha256Hex,
                    size,
                    DateTimeOffset.UtcNow.ToString("o"));

                await UpsertManifestAsync(item, ct).ConfigureAwait(false);
            }
            catch
            {
                // non-fatal
            }
        }

        return installedPaths;
    }

    private static async Task<(string sha256Hex, long size)> DownloadToFileWithHashAsync(
        string url,
        string dstPath,
        IProgress<ProgressInfo>? progress,
        string assetId,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;

        await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1024 * 128];
        long done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var n = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;

            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, n);
            done += n;

            if (sw.ElapsedMilliseconds > 350)
            {
                var bps = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                progress?.Report(new ProgressInfo(assetId, "download", done, total, bps));
                sw.Restart();
            }
        }

        var sha = Convert.ToHexString(hasher.GetHashAndReset());
        progress?.Report(new ProgressInfo(assetId, "done", done, total, null));
        return (sha, done);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[1024 * 128];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;
            hasher.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static string NormHex(string hex)
        => (hex ?? "").Trim().Replace(" ", "").ToUpperInvariant();

    private static async Task UpsertManifestAsync(ManifestItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);

        List<ManifestItem> items;
        try
        {
            if (!File.Exists(ManifestPath))
            {
                items = new List<ManifestItem>();
            }
            else
            {
                var json = await File.ReadAllTextAsync(ManifestPath, ct).ConfigureAwait(false);
                items = JsonSerializer.Deserialize<List<ManifestItem>>(json) ?? new List<ManifestItem>();
            }
        }
        catch
        {
            items = new List<ManifestItem>();
        }

        // Remove any previous entry by id
        items = items.Where(x => !string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Insert(0, item);

        var outJson = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ManifestPath, outJson, ct).ConfigureAwait(false);
    }

    // Back-compat: older UI code expects InstallAsync
    public Task<IReadOnlyList<string>> InstallAsync(
        IReadOnlyList<AssetSpec> assets,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
        => EnsureAssetsAsync(assets, progress, ct);

    // Convenience: install from provisioning downloads plan (if present)
    public async Task<IReadOnlyList<string>> InstallAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (!Provisioning.TryGetDownloadAssets(out var assets, out _))
            return Array.Empty<string>();
        return await EnsureAssetsAsync(assets, progress, ct).ConfigureAwait(false);
    }

}
