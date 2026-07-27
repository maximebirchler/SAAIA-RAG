using System.Security.Cryptography;

public sealed record IngestionDuplicateFileCandidate(
    string RelativePath,
    string AbsolutePath,
    long FileSize,
    DateTime LastWriteTimeUtc = default);

public sealed record IngestionDuplicateFileDecision(
    string DuplicatePath,
    string CanonicalPath,
    string ContentHashHex,
    long FileSize);

public static class IngestionDuplicateFilePlanner
{
    public static async Task<IReadOnlyDictionary<string, IngestionDuplicateFileDecision>> FindDuplicatesByContentAsync(
        IEnumerable<IngestionDuplicateFileCandidate> candidates,
        CancellationToken ct,
        Func<IngestionDuplicateFileCandidate, CancellationToken, Task<string>>? hashResolver = null)
    {
        var duplicateMap = new Dictionary<string, IngestionDuplicateFileDecision>(StringComparer.OrdinalIgnoreCase);

        var sameSizeGroups = candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.RelativePath)
                                       && !string.IsNullOrWhiteSpace(candidate.AbsolutePath)
                                       && candidate.FileSize >= 0)
            .GroupBy(static candidate => candidate.FileSize)
            .Where(static group => group.Count() > 1);

        foreach (var sizeGroup in sameSizeGroups)
        {
            var byHash = new Dictionary<string, List<IngestionDuplicateFileCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();
                var hash = hashResolver is null
                    ? await ComputeSha256HexAsync(candidate.AbsolutePath, ct).ConfigureAwait(false)
                    : await hashResolver(candidate, ct).ConfigureAwait(false);
                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = new List<IngestionDuplicateFileCandidate>();
                    byHash[hash] = list;
                }

                list.Add(candidate);
            }

            foreach (var hashGroup in byHash)
            {
                if (hashGroup.Value.Count <= 1)
                    continue;

                var canonical = hashGroup.Value
                    .OrderBy(static candidate => CanonicalPathScore(candidate.RelativePath))
                    .ThenBy(static candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .First();

                foreach (var duplicate in hashGroup.Value)
                {
                    if (string.Equals(duplicate.RelativePath, canonical.RelativePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    duplicateMap[duplicate.RelativePath] = new IngestionDuplicateFileDecision(
                        duplicate.RelativePath,
                        canonical.RelativePath,
                        hashGroup.Key,
                        duplicate.FileSize);
                }
            }
        }

        return duplicateMap;
    }

    internal static async Task<string> ComputeSha256HexAsync(
        string absolutePath,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(absolutePath);
        var bytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int CanonicalPathScore(string relativePath)
    {
        var normalized = PathUtil.NormalizeRelativePath(relativePath);
        var folderDepth = normalized.Count(static ch => ch == '/');
        return checked(folderDepth * 10_000 + normalized.Length);
    }
}
