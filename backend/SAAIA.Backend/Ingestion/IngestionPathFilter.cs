static class IngestionPathFilter
{
    private static readonly string[] IgnoredFileNames =
    {
        "thumbs.db",
        ".ds_store"
    };

    private static readonly string[] IgnoredDirNames =
    {
        "__macosx"
    };

    public static bool ShouldIgnoreRel(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return true;

        var norm = relPath.Replace('\\', '/').Trim('/');

        if (norm.StartsWith(".", StringComparison.Ordinal)) return true;
        if (norm.StartsWith("~", StringComparison.Ordinal)) return true;

        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        foreach (var seg in parts)
        {
            var s = seg.Trim();
            if (s.Length == 0) return true;

            if (s.StartsWith(".", StringComparison.Ordinal)) return true;
            if (s.StartsWith("~", StringComparison.Ordinal)) return true;

            foreach (var d in IgnoredDirNames)
                if (string.Equals(s, d, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        var name = parts[^1];

        foreach (var n in IgnoredFileNames)
            if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase))
                return true;

        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;

        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;

        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static string? SafeRelPath(string root, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(root, fullPath);
            rel = rel.Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
            return PathUtil.NormalizeRelativePath(rel);
        }
        catch
        {
            return null;
        }
    }
}
