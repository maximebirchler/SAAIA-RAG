using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Downloads prebuilt llama.cpp release binaries (Windows x64 CPU) from ggml-org/llama.cpp.
///
/// Production note:
/// - In the final installer, the llama-server.exe should ideally be shipped directly.
/// - This downloader is here to make the MVP fully automatic without Docker.
///
/// Safety:
/// - We download the official release .zip and extract the folder containing llama-server.exe.
/// - If GitHub API is not reachable, we fail gracefully with a clear error.
/// </summary>
internal sealed class LlamaCppReleaseDownloader
{
    private const string GitHubApiLatest = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

    public static string RuntimeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "llm", "runtime");

    public static string CpuRuntimeDir => Path.Combine(RuntimeRoot, "win-cpu-x64");
    public static string CpuServerExePath => Path.Combine(CpuRuntimeDir, "llama-server.exe");

    private sealed record GhAsset(string name, string browser_download_url, long? size);
    private sealed record GhRelease(string tag_name, List<GhAsset> assets);

    public async Task<(bool ok, string message, string? exePath)> EnsureWindowsCpuAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
    {
        try
        {
            if (File.Exists(CpuServerExePath))
                return (true, "OK", CpuServerExePath);

            Directory.CreateDirectory(CpuRuntimeDir);

            progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "resolve", 0, null, null));
            var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (rel is null)
                return (false, "Impossible de récupérer la release llama.cpp (GitHub API).", null);

            // Example asset name: llama-b5473-bin-win-cpu-x64.zip
            var cpuZip = rel.assets
                .FirstOrDefault(a => a.name.EndsWith("-bin-win-cpu-x64.zip", StringComparison.OrdinalIgnoreCase));

            if (cpuZip is null)
                return (false, "Release llama.cpp trouvée, mais aucun binaire Windows CPU x64 n'a été détecté.", null);

            // Download zip to %LOCALAPPDATA%\SAAIA\downloads\...
            var dm = new DownloadManager();
            var zipSpec = new DownloadManager.AssetSpec(
                Id: $"llama.cpp_{rel.tag_name}_win-cpu-x64",
                Url: cpuZip.browser_download_url,
                TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/" + cpuZip.name,
                Sha256Hex: null);

            var downloaded = await dm.EnsureAssetsAsync(new[] { zipSpec }, progress, ct).ConfigureAwait(false);
            var zipPath = downloaded.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return (false, "Téléchargement llama.cpp échoué.", null);

            // Extract to temp, find llama-server.exe, copy its folder contents to CpuRuntimeDir
            var tmp = Path.Combine(Path.GetTempPath(), "saaia_llama_extract_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, tmp);

                var exe = Directory.GetFiles(tmp, "llama-server.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    return (false, "Archive llama.cpp invalide : llama-server.exe introuvable.", null);

                var srcDir = Path.GetDirectoryName(exe)!;

                // Clear runtime dir (safe) then copy
                SafeDeleteDirectory(CpuRuntimeDir);
                Directory.CreateDirectory(CpuRuntimeDir);

                foreach (var f in Directory.GetFiles(srcDir))
                {
                    var dst = Path.Combine(CpuRuntimeDir, Path.GetFileName(f));
                    File.Copy(f, dst, overwrite: true);
                }

                // Write marker
                try
                {
                    File.WriteAllText(Path.Combine(CpuRuntimeDir, "runtime.tag"), rel.tag_name);
                }
                catch { /* ignore */ }

                if (!File.Exists(CpuServerExePath))
                    return (false, "Extraction llama.cpp échouée (llama-server.exe manquant après copie).", null);

                return (true, $"OK ({rel.tag_name})", CpuServerExePath);
            }
            finally
            {
                SafeDeleteDirectory(tmp);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Annulé.", null);
        }
        catch (Exception ex)
        {
            return (false, "Téléchargement llama.cpp échoué: " + ex.Message, null);
        }
    }

    private static async Task<GhRelease?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatest);

        // GitHub API requires a User-Agent.
        req.Headers.UserAgent.ParseAdd("SAAIA/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GhRelease>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
