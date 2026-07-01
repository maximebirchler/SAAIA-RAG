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
