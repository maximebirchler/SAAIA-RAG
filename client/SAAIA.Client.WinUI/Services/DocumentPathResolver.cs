using System;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Resolves backend docPath (relative, e.g. "ATEX/...pdf") to a local file path.
/// Also provides robust helpers to produce user-facing display paths.
///
/// Design intent (prod/dev):
/// - The canonical host install root is defined by SAAIA_INSTALL_ROOT (same value as infra/.env).
/// - Documents live under: <installRoot>\documents
/// - If SAAIA_INSTALL_ROOT is not available, fall back to a sensible default (C:\SAAIA\documents),
///   then finally to the repo/dev "documents" folder.
///
/// NOTE: opening a source is a *client-only* operation (no backend download), so we must resolve
/// to a local host path.
/// </summary>
internal static class DocumentPathResolver
{
    private const string EnvDocumentsRoot = "SAAIA_DOCUMENTS_ROOT"; // optional override
    private const string EnvInstallRoot = "SAAIA_INSTALL_ROOT";     // canonical install root

    private static string? NormalizeEnvPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        return p.Trim().Trim('"', '\'');
    }

    private static string GetSystemDriveRoot()
        => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    private static string DefaultInstallRoot
        => Path.Combine(GetSystemDriveRoot(), "SAAIA");

    private static string DefaultDocumentsRoot
        => Path.Combine(DefaultInstallRoot, "documents");

    /// <summary>
    /// Best effort documents root.
    /// </summary>
    public static string GetDocumentsRoot()
    {
        // 1) Explicit documents root override (only if it exists)
        var envDocs = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvDocumentsRoot));
        if (!string.IsNullOrWhiteSpace(envDocs) && Directory.Exists(envDocs))
            return Path.GetFullPath(envDocs);

        // 2) Canonical install root (from infra/.env) => <installRoot>/documents
        // IMPORTANT: only accept candidate if the documents folder exists.
        // Also accept when EnvInstallRoot already points to the documents folder.
        var envInstall = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvInstallRoot));
        if (!string.IsNullOrWhiteSpace(envInstall))
        {
            try
            {
                var installFull = Path.GetFullPath(envInstall);
                var installName = Path.GetFileName(installFull.TrimEnd(Path.DirectorySeparatorChar, '/'));

                // If it already points to ...\documents, use it directly.
                if (string.Equals(installName, "documents", StringComparison.OrdinalIgnoreCase) && Directory.Exists(installFull))
                    return installFull;

                var candidate = Path.GetFullPath(Path.Combine(installFull, "documents"));
                if (Directory.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore and continue
            }
        }

        // 3) Default prod location (works for the common Windows install)
        try
        {
            var prodDocs = Path.GetFullPath(DefaultDocumentsRoot);
            if (Directory.Exists(prodDocs))
                return prodDocs;
        }
        catch
        {
            // ignore
        }

        // 4) Dev heuristic: walk up from exe and look for a "documents" folder
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "documents");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            dir = dir.Parent;
        }

        // 5) Fallback
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "documents"));
    }

    /// <summary>
    /// Resolve a docPath (relative or absolute-ish) to an existing absolute path.
    /// </summary>
    public static string? Resolve(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        // normalize separators + trim leading slashes
        var rel = docPath
            .Trim()
            .TrimStart('\\', '/')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        // already absolute?
        try
        {
            if (Path.IsPathRooted(rel) && File.Exists(rel))
                return Path.GetFullPath(rel);
        }
        catch
        {
            // ignore
        }

        // Try under the selected root
        var root = GetDocumentsRoot();
        var resolved = TryResolveUnderRoot(root, rel);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        // Safety net: also try the default prod root if different.
        try
        {
            var prod = Path.GetFullPath(DefaultDocumentsRoot);
            if (!string.Equals(Path.GetFullPath(root), prod, StringComparison.OrdinalIgnoreCase))
            {
                resolved = TryResolveUnderRoot(prod, rel);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// Convert an absolute path (or something that looks like it) to a user-facing relative display path.
    /// The returned path:
    /// - never contains ".."
    /// - never contains the absolute prefix "C:\\SAAIA\\documents" (or any absolute root)
    /// - uses '/' separators
    /// </summary>
    public static string? ToDisplayPath(string? docPathOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(docPathOrAbsolute))
            return null;

        var s = docPathOrAbsolute.Trim();

        // If it contains parent segments, attempt to strip the "documents" prefix.
        if (s.StartsWith("..", StringComparison.Ordinal) || s.Contains("..\\") || s.Contains("../"))
        {
            var stripped = TryStripDocumentsSegment(s);
            if (!string.IsNullOrWhiteSpace(stripped))
                return NormalizeRelString(stripped);
        }

        // If it's rooted or contains a drive, convert from absolute.
        try
        {
            if (Path.IsPathRooted(s) || s.Contains(":\\") || s.Contains(":/"))
            {
                var abs = Path.GetFullPath(s);
                return ToDisplayPathFromAbsolute(abs);
            }
        }
        catch
        {
            // ignore
        }

        // If it starts with documents/, strip it.
        s = s.Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) s = s.Replace("//", "/", StringComparison.Ordinal);
        s = s.TrimStart('/');
        if (s.StartsWith("documents/", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("documents/".Length);

        return NormalizeRelString(s);
    }

    /// <summary>
    /// Convert an existing absolute path to a relative display path.
    /// </summary>
    private static string ToDisplayPathFromAbsolute(string absFullPath)
    {
        var abs = Path.GetFullPath(absFullPath);

        // Prefer the currently selected documents root if the file is under it.
        var primaryRoot = Path.GetFullPath(GetDocumentsRoot());
        var prodRoot = Path.GetFullPath(DefaultDocumentsRoot);

        string rel;
        if (IsUnder(primaryRoot, abs))
        {
            rel = Path.GetRelativePath(primaryRoot, abs);
        }
        else if (IsUnder(prodRoot, abs))
        {
            rel = Path.GetRelativePath(prodRoot, abs);
        }
        else
        {
            // Last resort: strip the first occurrence of a "documents" folder in the absolute path.
            var stripped = TryStripDocumentsSegment(abs);
            rel = !string.IsNullOrWhiteSpace(stripped) ? stripped : Path.GetFileName(abs);
        }

        rel = rel.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
        rel = rel.TrimStart('/');

        return NormalizeRelString(rel);
    }

    private static bool IsUnder(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, '/');
        path = Path.GetFullPath(path);

        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryStripDocumentsSegment(string anyPath)
    {
        var s = (anyPath ?? "").Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) s = s.Replace("//", "/", StringComparison.Ordinal);

        var idx = s.IndexOf("/documents/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return s.Substring(idx + "/documents/".Length).TrimStart('/');

        // Also handle when the string ends with "/documents" (rare)
        idx = s.IndexOf("/documents", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + "/documents".Length < s.Length)
        {
            var after = s.Substring(idx + "/documents".Length);
            return after.TrimStart('/');
        }

        return null;
    }

    private static string NormalizeRelString(string rel)
    {
        var s = (rel ?? "").Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) s = s.Replace("//", "/", StringComparison.Ordinal);
        s = s.Trim();
        s = s.TrimStart('/');

        // Remove any parent segments defensively.
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = parts.Where(p => !string.Equals(p, "..", StringComparison.Ordinal)).ToArray();
        return string.Join("/", clean);
    }

    private static string? TryResolveUnderRoot(string root, string rel)
    {
        try
        {
            // root + relative path
            var candidate = Path.GetFullPath(Path.Combine(root, rel));
            if (File.Exists(candidate))
                return candidate;

            // root + filename
            var fileName = Path.GetFileName(rel);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var candidate2 = Path.GetFullPath(Path.Combine(root, fileName));
                if (File.Exists(candidate2))
                    return candidate2;

                // best-effort search (exact filename)
                if (Directory.Exists(root))
                {
                    var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                        return found;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
