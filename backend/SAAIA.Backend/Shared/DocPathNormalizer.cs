using System.Runtime.InteropServices;

static class DocPathNormalizer
{
    public static string NormalizeToRelative(string docPath, string documentsRoot)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            throw new InvalidOperationException("docPath is required");
        if (string.IsNullOrWhiteSpace(documentsRoot))
            throw new InvalidOperationException("DocumentsRoot is required");

        docPath = docPath.Trim();

        var rootFull = Path.GetFullPath(documentsRoot);

        string rel;
        if (Path.IsPathRooted(docPath))
        {
            var full = Path.GetFullPath(docPath);

            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!full.StartsWith(rootPrefix, cmp) && !string.Equals(full, rootFull, cmp))
                throw new InvalidOperationException($"docPath must be inside DocumentsRoot. docPath='{full}', root='{rootFull}'");

            rel = Path.GetRelativePath(rootFull, full);
        }
        else
        {
            rel = docPath;
        }

        rel = rel.Replace('\\', '/').TrimStart('/');

        if (rel.Contains(".."))
            throw new InvalidOperationException("docPath must be a relative path without '..'");

        var combined = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));

        var cmp2 = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootPrefix2 = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix2, cmp2) && !string.Equals(combined, rootFull, cmp2))
            throw new InvalidOperationException("Invalid docPath (path traversal).");

        return Path.GetRelativePath(rootFull, combined).Replace('\\', '/').TrimStart('/');
    }

    public static string ToAbsoluteFromRelative(string relDocPath, string documentsRoot)
    {
        var rootFull = Path.GetFullPath(documentsRoot);
        return Path.GetFullPath(Path.Combine(rootFull, relDocPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
