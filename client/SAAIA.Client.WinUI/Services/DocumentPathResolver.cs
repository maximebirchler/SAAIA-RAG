using System;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Resolves backend docPath (relative, e.g. "ATEX/...pdf") to a local file path.
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

    public static string GetDocumentsRoot()
    {
        // 1) Explicit documents root override (only if it exists)
        var envDocs = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvDocumentsRoot));
        if (!string.IsNullOrWhiteSpace(envDocs) && Directory.Exists(envDocs))
            return Path.GetFullPath(envDocs);

        // 2) Canonical install root (from infra/.env) => <installRoot>/documents
        // Use it if it exists (install folder or documents folder).
        var envInstall = NormalizeEnvPath(Environment.GetEnvironmentVariable(EnvInstallRoot));
        if (!string.IsNullOrWhiteSpace(envInstall))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(envInstall, "documents"));
                if (Directory.Exists(envInstall) || Directory.Exists(candidate))
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
        if (Path.IsPathRooted(rel) && File.Exists(rel))
            return Path.GetFullPath(rel);

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
