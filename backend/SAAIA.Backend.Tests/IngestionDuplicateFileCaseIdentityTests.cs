using System.Security.Cryptography;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionDuplicateFileCaseIdentityTests
{
    private static readonly DateTime FixedMtime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string HashX = Convert.ToHexString(SHA256.HashData("value-X"u8)).ToLowerInvariant();
    private static readonly string HashY = Convert.ToHexString(SHA256.HashData("value-Y"u8)).ToLowerInvariant();

    [Fact]
    public async Task Linux_case_distinct_paths_do_not_share_cached_hash_for_same_size_and_mtime()
    {
        var candidates = VirtualCandidates();
        var cache = new IngestionFileContentHashCache(windowsFileSystem: false, hashResolver: VirtualHash);

        var lower = await cache.ResolveAsync(candidates[0], CancellationToken.None);
        var upper = await cache.ResolveAsync(candidates[1], CancellationToken.None);

        Assert.Equal(HashX, lower.HashHex);
        Assert.Equal(HashY, upper.HashHex);
        Assert.False(upper.CacheHit);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Linux_retain_only_does_not_keep_a_different_case_path_cache_entry()
    {
        var candidates = VirtualCandidates();
        var cache = new IngestionFileContentHashCache(windowsFileSystem: false, hashResolver: VirtualHash);
        await cache.ResolveAsync(candidates[0], CancellationToken.None);

        cache.RetainOnly([candidates[1].AbsolutePath]);

        Assert.Equal(0, cache.Count);
        Assert.False((await cache.ResolveAsync(candidates[0], CancellationToken.None)).CacheHit);
    }

    [Fact]
    public async Task Linux_cached_duplicate_chain_never_suppresses_distinct_case_file_with_different_content()
    {
        var candidates = VirtualCandidates();
        var cache = new IngestionFileContentHashCache(windowsFileSystem: false, hashResolver: VirtualHash);

        var duplicates = await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(
            candidates, CancellationToken.None,
            async (candidate, ct) => (await cache.ResolveAsync(candidate, ct)).HashHex,
            windowsFileSystem: false);

        var onlyDuplicate = Assert.Single(duplicates);
        Assert.Equal("deep/a.pdf", onlyDuplicate.Key);
        Assert.Equal("base.pdf", onlyDuplicate.Value.CanonicalPath);
        Assert.False(duplicates.ContainsKey("deep/A.PDF"));
    }

    [Fact]
    public async Task Linux_identical_case_distinct_files_keep_one_canonical_and_one_duplicate()
    {
        var candidates = VirtualCandidates()[..2];
        var duplicates = await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(
            candidates, CancellationToken.None, (_, _) => Task.FromResult(HashX), windowsFileSystem: false);

        var onlyDuplicate = Assert.Single(duplicates);
        Assert.Equal("deep/A.PDF", onlyDuplicate.Value.CanonicalPath);
        Assert.Equal("deep/a.pdf", onlyDuplicate.Key);
    }

    [Fact]
    public async Task Windows_case_aliases_keep_the_same_cached_identity_and_canonical_path()
    {
        var candidates = VirtualCandidates()[..2];
        var cache = new IngestionFileContentHashCache(windowsFileSystem: true, hashResolver: VirtualHash);
        await cache.ResolveAsync(candidates[0], CancellationToken.None);
        var upper = await cache.ResolveAsync(candidates[1], CancellationToken.None);
        Assert.True(upper.CacheHit);
        Assert.Equal(HashX, upper.HashHex);
        cache.RetainOnly([candidates[1].AbsolutePath]);
        Assert.Equal(1, cache.Count);
        var duplicates = await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(
            candidates, CancellationToken.None, (_, _) => Task.FromResult(HashX), windowsFileSystem: true);
        Assert.Empty(duplicates);
    }

    [LinuxFileSystemFact]
    [Trait("Profile", "LinuxFileSystem")]
    public async Task Real_Linux_case_distinct_files_same_size_and_mtime_keep_their_distinct_hashes_and_publication_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"saaia-case-paths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "deep"));
        try
        {
            var lower = Path.Combine(root, "deep", "a.pdf");
            var upper = Path.Combine(root, "deep", "A.PDF");
            var canonical = Path.Combine(root, "base.pdf");
            await File.WriteAllTextAsync(lower, "%PDF synthetic X");
            await File.WriteAllTextAsync(upper, "%PDF synthetic Y");
            await File.WriteAllTextAsync(canonical, "%PDF synthetic X");
            foreach (var path in new[] { lower, upper, canonical }) File.SetLastWriteTimeUtc(path, FixedMtime);
            Assert.Equal(3, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
            var candidates = new[] { lower, upper, canonical }.Select(path =>
                new IngestionDuplicateFileCandidate(Path.GetRelativePath(root, path).Replace('\\', '/'), path,
                    new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc)).ToArray();
            var cache = new IngestionFileContentHashCache();
            var first = await cache.ResolveAsync(candidates[0], CancellationToken.None);
            var second = await cache.ResolveAsync(candidates[1], CancellationToken.None);
            Assert.NotEqual(first.HashHex, second.HashHex);
            Assert.False(second.CacheHit);
            Assert.Equal(2, cache.Count);
            cache.RetainOnly([upper]);
            Assert.Equal(1, cache.Count);
            Assert.False((await cache.ResolveAsync(candidates[0], CancellationToken.None)).CacheHit);

            var duplicates = await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(
                candidates, CancellationToken.None, async (candidate, ct) => (await cache.ResolveAsync(candidate, ct)).HashHex);
            var onlyDuplicate = Assert.Single(duplicates);
            Assert.Equal("deep/a.pdf", onlyDuplicate.Key);
            Assert.Equal("base.pdf", onlyDuplicate.Value.CanonicalPath);
            Assert.False(duplicates.ContainsKey("deep/A.PDF"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IngestionDuplicateFileCandidate[] VirtualCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-virtual-linux-case");
        return [new("deep/a.pdf", Path.Combine(root, "deep", "a.pdf"), 7, FixedMtime),
                new("deep/A.PDF", Path.Combine(root, "deep", "A.PDF"), 7, FixedMtime),
                new("base.pdf", Path.Combine(root, "base.pdf"), 7, FixedMtime)];
    }

    private static Task<string> VirtualHash(IngestionDuplicateFileCandidate candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(candidate.RelativePath == "deep/A.PDF" ? HashY : HashX);
    }

    private sealed class LinuxFileSystemFactAttribute : FactAttribute
    {
        public LinuxFileSystemFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "Requires real Linux case-distinct files; virtual platform protocol tests remain runnable elsewhere.";
        }
    }
}
