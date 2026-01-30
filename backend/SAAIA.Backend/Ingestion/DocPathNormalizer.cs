using System.Runtime.InteropServices;

namespace SAAIA.Backend.Shared;

public static class DocPathNormalizer
{
    public static string NormalizeToRelative(string docPath, string documentsRoot)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            throw new ArgumentException("docPath is required", nameof(docPath));
        if (string.IsNullOrWhiteSpace(documentsRoot))
            throw new ArgumentException("DocumentsRoot is required", nameof(documentsRoot));

        docPath = docPath.Trim();

        // Full path of root (canonical)
        var rootFull = Path.GetFullPath(documentsRoot);

        // Normalize input: if rooted -> canonical full path, then relativize
        string rel;
        if (Path.IsPathRooted(docPath))
        {
            var full = Path.GetFullPath(docPath);

            // Ensure inside DocumentsRoot (case-insensitive on Windows)
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
                && !string.Equals(full, rootFull, comparison))
            {
                throw new InvalidOperationException($"docPath must be inside DocumentsRoot. docPath='{full}', root='{rootFull}'");
            }

            rel = Path.GetRelativePath(rootFull, full);
        }
        else
        {
            rel = docPath;
        }

        // Normalize separators & trim leading slashes
        rel = rel.Replace('\\', '/').TrimStart('/');

        // Prevent path traversal: re-canonicalize and re-relativize
        var combined = Path.GetFullPath(Path.Combine(rootFull, rel));
        var comparison2 = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combined.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison2)
            && !string.Equals(combined, rootFull, comparison2))
        {
            throw new InvalidOperationException($"Invalid docPath (path traversal). docPath='{docPath}'");
        }

        rel = Path.GetRelativePath(rootFull, combined).Replace('\\', '/').TrimStart('/');

        return rel;
    }

    public static string ToAbsoluteFromRelative(string relDocPath, string documentsRoot)
    {
        // relDocPath must already be normalized
        return Path.GetFullPath(Path.Combine(Path.GetFullPath(documentsRoot), relDocPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
