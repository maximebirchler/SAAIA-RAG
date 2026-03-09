using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Minimal export service (spec v2.8.x): create a downloadable file.
///
/// Minimum guaranteed format: TXT.
/// We also support MD and CSV as plain text.
/// </summary>
internal static class ExportService
{
    public static string ExportsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "exports");

    public static string Create(string format, string fileName, string content)
    {
        Directory.CreateDirectory(ExportsDir);

        var fmt = (format ?? "txt").Trim().ToLowerInvariant();
        if (fmt is not ("txt" or "md" or "csv"))
            fmt = "txt"; // spec: minimum guaranteed

        var safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(fileName) ? "export" : fileName.Trim());
        if (!safeName.EndsWith("." + fmt, StringComparison.OrdinalIgnoreCase))
            safeName += "." + fmt;

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(ExportsDir, $"{Path.GetFileNameWithoutExtension(safeName)}_{stamp}.{fmt}");

        File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
        return path;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        cleaned = cleaned.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "export";
        return cleaned;
    }
}
