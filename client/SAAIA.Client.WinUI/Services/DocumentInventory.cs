using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Small local cache of PDF files under the documents root.
///
/// Goal (M7 robustness): when the backend index returns a stale docPath after a move/rename,
/// the client can still resolve it to the real file on disk (and avoid listing "ghost" paths).
///
/// Notes:
/// - This is best-effort and only used for client-side UX (listing/opening sources).
/// - Source of truth for "indexed or not" remains the backend; we only sanitize paths.
/// </summary>
internal static class DocumentInventory
{
    private static readonly object _gate = new();
    private static DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private static List<Entry> _entries = new();

    internal sealed record Entry(string FullPath, string RelPath, string FileNameLower);

    public static IReadOnlyList<Entry> GetAll(bool forceRefresh = false, TimeSpan? maxAge = null)
    {
        maxAge ??= TimeSpan.FromSeconds(5);

        lock (_gate)
        {
            if (!forceRefresh && (DateTimeOffset.UtcNow - _lastScan) <= maxAge.Value && _entries.Count > 0)
                return _entries;

            var root = DocumentPathResolver.GetDocumentsRoot();
            var list = new List<Entry>();
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories))
                {
                    try
                    {
                        var full = Path.GetFullPath(file);
                        var rel = DocumentPathResolver.ToDisplayPath(full);
                        if (string.IsNullOrWhiteSpace(rel))
                            continue;
                        var fn = Path.GetFileName(full).ToLowerInvariant();
                        list.Add(new Entry(full, rel!, fn));
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            _entries = list
                .OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _lastScan = DateTimeOffset.UtcNow;
            return _entries;
        }
    }

    /// <summary>
    /// Find all PDFs matching a file name (case-insensitive). Returns absolute paths.
    /// </summary>
    public static List<string> FindByFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return new List<string>();

        var key = Path.GetFileName(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
            return new List<string>();

        var all = GetAll(forceRefresh: false);
        return all
            .Where(e => e.FileNameLower == key)
            .Select(e => e.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
