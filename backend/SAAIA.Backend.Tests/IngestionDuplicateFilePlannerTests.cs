using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionDuplicateFilePlannerTests
{
    [Fact]
    public async Task FindDuplicatesByContentAsync_keeps_shallow_canonical_path_for_identical_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"saaia-duplicate-planner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Alpha", "PDF"));

        try
        {
            var canonicalPath = Path.Combine(root, "Alpha", "guide.pdf");
            var duplicatePath = Path.Combine(root, "Alpha", "PDF", "guide.pdf");
            var otherSameSizePath = Path.Combine(root, "Alpha", "other.pdf");

            await File.WriteAllBytesAsync(canonicalPath, "same-pdf-content"u8.ToArray());
            await File.WriteAllBytesAsync(duplicatePath, "same-pdf-content"u8.ToArray());
            await File.WriteAllBytesAsync(otherSameSizePath, "same-pdf-contents"u8.ToArray());

            var candidates = new[]
            {
                new IngestionDuplicateFileCandidate("Alpha/guide.pdf", canonicalPath, new FileInfo(canonicalPath).Length),
                new IngestionDuplicateFileCandidate("Alpha/PDF/guide.pdf", duplicatePath, new FileInfo(duplicatePath).Length),
                new IngestionDuplicateFileCandidate("Alpha/other.pdf", otherSameSizePath, new FileInfo(otherSameSizePath).Length)
            };

            var duplicates = await IngestionDuplicateFilePlanner.FindDuplicatesByContentAsync(candidates, CancellationToken.None);

            var decision = Assert.Single(duplicates);
            Assert.Equal("Alpha/PDF/guide.pdf", decision.Key);
            Assert.Equal("Alpha/guide.pdf", decision.Value.CanonicalPath);
            Assert.Equal(new FileInfo(duplicatePath).Length, decision.Value.FileSize);
            Assert.False(string.IsNullOrWhiteSpace(decision.Value.ContentHashHex));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task File_hash_cache_reuses_unchanged_content_and_invalidates_on_mtime_change()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"saaia-duplicate-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var path = Path.Combine(root, "fixture.pdf");
            await File.WriteAllBytesAsync(path, "content-a"u8.ToArray());
            var firstMtime = DateTime.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(path, firstMtime);
            var firstInfo = new FileInfo(path);
            var cache = new IngestionFileContentHashCache();
            var firstCandidate = new IngestionDuplicateFileCandidate(
                "fixture.pdf",
                path,
                firstInfo.Length,
                firstInfo.LastWriteTimeUtc);

            var first = await cache.ResolveAsync(
                firstCandidate,
                CancellationToken.None);
            var second = await cache.ResolveAsync(
                firstCandidate,
                CancellationToken.None);

            Assert.False(first.CacheHit);
            Assert.True(second.CacheHit);
            Assert.Equal(first.HashHex, second.HashHex);
            Assert.Equal(1, cache.Count);

            await File.WriteAllBytesAsync(path, "content-b"u8.ToArray());
            File.SetLastWriteTimeUtc(path, firstMtime.AddSeconds(10));
            var changedInfo = new FileInfo(path);
            var changed = await cache.ResolveAsync(
                firstCandidate with
                {
                    FileSize = changedInfo.Length,
                    LastWriteTimeUtc = changedInfo.LastWriteTimeUtc
                },
                CancellationToken.None);

            Assert.False(changed.CacheHit);
            Assert.NotEqual(first.HashHex, changed.HashHex);

            cache.RetainOnly([]);
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp files created by this test.
        }
    }
}
