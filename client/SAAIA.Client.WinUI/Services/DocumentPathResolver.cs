using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Resolves backend docPath (relative, e.g. "Category/...pdf") to a local file path.
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
        foreach (var root in EnumerateCandidateDocumentRoots())
            return root;

        // Fallback used only for diagnostics when no known document root exists locally.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "documents"));
    }

    /// <summary>
    /// Resolve a docPath (relative or absolute-ish) to an existing absolute path.
    /// </summary>
    public static string? Resolve(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var raw = docPath.Trim().Trim('"', '\'');

        // already absolute?
        try
        {
            var absoluteCandidate = raw.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(absoluteCandidate) && File.Exists(absoluteCandidate))
                return Path.GetFullPath(absoluteCandidate);
        }
        catch
        {
            // ignore
        }

        // normalize separators + trim leading slashes only after checking rooted paths.
        var rel = raw
            .TrimStart('\\', '/')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        foreach (var root in EnumerateCandidateDocumentRoots())
        {
            var resolved = TryResolveUnderRoot(root, rel);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
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

        string rel;
        var matchingRoot = EnumerateCandidateDocumentRoots().FirstOrDefault(root => IsUnder(root, abs));
        if (!string.IsNullOrWhiteSpace(matchingRoot))
        {
            rel = Path.GetRelativePath(matchingRoot, abs);
        }
        else if (IsUnder(Path.GetFullPath(DefaultDocumentsRoot), abs))
        {
            rel = Path.GetRelativePath(Path.GetFullPath(DefaultDocumentsRoot), abs);
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

    private static IEnumerable<string> EnumerateCandidateDocumentRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateCandidateDocumentRootStrings())
        {
            var existing = NormalizeExistingDirectory(candidate);
            if (string.IsNullOrWhiteSpace(existing))
                continue;

            if (seen.Add(existing))
                yield return existing;
        }
    }

    private static IEnumerable<string?> EnumerateCandidateDocumentRootStrings()
    {
        // 1) Explicit documents root override.
        yield return NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvDocumentsRoot));

        // 2) Canonical install root (from infra/.env) => <installRoot>/documents.
        // Also accept when EnvInstallRoot already points to a documents/docs folder.
        var envInstall = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvInstallRoot));
        if (!string.IsNullOrWhiteSpace(envInstall))
        {
            var installName = Path.GetFileName(envInstall.TrimEnd(Path.DirectorySeparatorChar, '/'));
            if (string.Equals(installName, "documents", StringComparison.OrdinalIgnoreCase)
                || string.Equals(installName, "docs", StringComparison.OrdinalIgnoreCase))
            {
                yield return envInstall;
            }

            yield return Path.Combine(envInstall, "documents");
            yield return Path.Combine(envInstall, "docs");
        }

        // 3) Default prod location.
        yield return DefaultDocumentsRoot;

        // 4) Dev roots near the executable/current directory. Some local workspaces use
        // "docs" while production installs use "documents".
        foreach (var root in EnumerateDevDocumentRootStrings(new DirectoryInfo(AppContext.BaseDirectory)))
            yield return root;

        DirectoryInfo? currentDirectory = null;
        try
        {
            currentDirectory = new DirectoryInfo(Environment.CurrentDirectory);
        }
        catch
        {
            // ignore
        }

        foreach (var root in EnumerateDevDocumentRootStrings(currentDirectory))
            yield return root;

        // 5) Network shares derived from configured backend URLs. This lets a Windows
        // client open sources stored on a server share without hard-coding a machine IP.
        foreach (var root in EnumerateBackendShareRootStrings())
            yield return root;
    }

    private static IEnumerable<string> EnumerateDevDocumentRootStrings(DirectoryInfo? start)
    {
        var dir = start;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, "documents");
            yield return Path.Combine(dir.FullName, "docs");
            yield return Path.Combine(dir.FullName, "saaia-repo", "documents");
            yield return Path.Combine(dir.FullName, "saaia-repo", "docs");

            dir = dir.Parent;
        }
    }

    private static IEnumerable<string> EnumerateBackendShareRootStrings()
    {
        IEnumerable<string> urls;
        try
        {
            urls = AppSettings.Load().AllBackendUrlCandidates().ToArray();
        }
        catch
        {
            yield break;
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in urls)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                continue;

            var host = uri.Host?.Trim();
            if (string.IsNullOrWhiteSpace(host) || IsLocalLoopbackHost(host))
                continue;

            if (!hosts.Add(host))
                continue;

            yield return $@"\\{host}\saaia-repo\documents";
            yield return $@"\\{host}\saaia-repo\docs";
            yield return $@"\\{host}\documents";
            yield return $@"\\{host}\docs";
        }
    }

    private static bool IsLocalLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeExistingDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        try
        {
            var full = Path.GetFullPath(candidate.Trim().Trim('"', '\''));
            return Directory.Exists(full) ? full.TrimEnd(Path.DirectorySeparatorChar, '/') : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveUnderRoot(string root, string rel)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);

            // root + relative path
            var candidate = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (IsUnder(rootFull, candidate) && File.Exists(candidate))
                return candidate;

            // root + filename
            var fileName = Path.GetFileName(rel);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var candidate2 = Path.GetFullPath(Path.Combine(rootFull, fileName));
                if (IsUnder(rootFull, candidate2) && File.Exists(candidate2))
                    return candidate2;

                // best-effort search (exact filename)
                if (Directory.Exists(rootFull))
                {
                    var found = Directory.EnumerateFiles(rootFull, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(found) && IsUnder(rootFull, found) && File.Exists(found))
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
