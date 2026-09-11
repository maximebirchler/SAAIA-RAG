using System.Security.Cryptography;

namespace SAAIA.Client.WinUI.Services;

internal static partial class DocumentPathResolver
{
    private static string? TryResolveMovedSourceAlias(
        string relativePath,
        string? sourceHash,
        IEnumerable<string> documentRoots)
    {
        var fileName = Path.GetFileName(relativePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var candidates = EnumerateFilenameCandidates(documentRoots, fileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.Count(ch => ch is '\\' or '/'))
            .ThenBy(static path => path.Length)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var normalizedHash = NormalizeSha256(sourceHash);
        if (normalizedHash is not null)
        {
            var matching = candidates
                .Where(candidate => FileMatchesSha256(candidate, normalizedHash))
                .Take(2)
                .ToArray();

            // Several identical local copies are mechanically equivalent. Prefer the
            // shallowest stable path, matching the server's canonical-path policy.
            if (matching.Length > 0)
                return matching[0];

            return null;
        }

        // Without an immutable identity, a filename alias is safe only when unique.
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static IEnumerable<string> EnumerateFilenameCandidates(
        IEnumerable<string> documentRoots,
        string fileName)
    {
        foreach (var root in documentRoots)
        {
            string[] matches;
            try
            {
                matches = Directory.GetFiles(
                    root,
                    fileName,
                    SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(match);
                }
                catch
                {
                    continue;
                }

                if (IsUnder(root, candidate) && File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static bool MatchesSourceHashOrUnspecified(
        string absolutePath,
        string? sourceHash)
    {
        var normalizedHash = NormalizeSha256(sourceHash);
        return normalizedHash is null || FileMatchesSha256(absolutePath, normalizedHash);
    }

    private static string? NormalizeSha256(string? sourceHash)
    {
        if (string.IsNullOrWhiteSpace(sourceHash))
            return null;

        var normalized = sourceHash.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["sha256:".Length..];

        return normalized.Length == 64
               && normalized.All(static character =>
                   character is >= '0' and <= '9'
                       or >= 'a' and <= 'f'
                       or >= 'A' and <= 'F')
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static bool FileMatchesSha256(
        string absolutePath,
        string normalizedExpectedHash)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(normalizedExpectedHash));
        }
        catch
        {
            return false;
        }
    }
}
