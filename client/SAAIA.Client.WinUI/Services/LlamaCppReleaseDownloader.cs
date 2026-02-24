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
/// Downloads prebuilt llama.cpp release binaries from ggml-org/llama.cpp.
///
/// MVP:
/// - We can auto-download Windows CPU runtime.
/// - If NVIDIA is detected, we can auto-download Windows CUDA runtime (and optional cudart bundle) if present in the release assets.
///
/// Production note:
/// - Final installer can ship the runtime(s) directly.
/// </summary>
internal sealed class LlamaCppReleaseDownloader
{
    private const string GitHubApiLatest = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

    // Local runtime dirs
    public static string RuntimeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "llm", "runtime");

    public static string CpuRuntimeDir => Path.Combine(RuntimeRoot, "win-cpu-x64");
    public static string CudaRuntimeDir => Path.Combine(RuntimeRoot, "win-cuda-x64");

    public static string CpuServerExePath => Path.Combine(CpuRuntimeDir, "llama-server.exe");
    public static string CudaServerExePath => Path.Combine(CudaRuntimeDir, "llama-server.exe");

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

            // Common asset name: llama-bXXXX-bin-win-cpu-x64.zip
            var cpuZip = rel.assets
                .FirstOrDefault(a => a.name.Contains("-bin-win-cpu-", StringComparison.OrdinalIgnoreCase)
                                  && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                                  && a.name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase));

            if (cpuZip is null)
                return (false, "Release llama.cpp trouvée, mais aucun binaire Windows CPU x64 n'a été détecté.", null);

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

            return ExtractServerZip(zipPath, CpuRuntimeDir, rel.tag_name, progress);
        }
        catch (OperationCanceledException)
        {
            return (false, "Annulé.", null);
        }
        catch (Exception ex)
        {
            return (false, "Erreur: " + ex.Message, null);
        }
    }

    public async Task<(bool ok, string message, string? exePath)> EnsureWindowsCudaAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
    {
        try
        {
            if (File.Exists(CudaServerExePath))
                return (true, "OK", CudaServerExePath);

            Directory.CreateDirectory(CudaRuntimeDir);

            progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "resolve", 0, null, null));
            var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (rel is null)
                return (false, "Impossible de récupérer la release llama.cpp (GitHub API).", null);

            // Common asset name patterns seen in the wild:
            // llama-b7806-bin-win-cuda-13.1-x64.zip
            // llama-b5535-bin-win-cuda-12.4-x64.zip
            var cudaCandidates = rel.assets
                .Where(a => a.name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
                         && a.name.Contains("-bin-win-cuda-", StringComparison.OrdinalIgnoreCase)
                         && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                         && !a.name.StartsWith("cudart-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer CUDA 12.x builds when present (better compatibility with many driver stacks).
            var cudaZip = cudaCandidates.FirstOrDefault(a => a.name.Contains("-bin-win-cuda-12.", StringComparison.OrdinalIgnoreCase))
                      ?? cudaCandidates.FirstOrDefault(a => a.name.Contains("-bin-win-cuda-12", StringComparison.OrdinalIgnoreCase))
                      ?? cudaCandidates.FirstOrDefault();

            if (cudaZip is null)
                return (false, "Release llama.cpp trouvée, mais aucun binaire Windows CUDA x64 n'a été détecté.", null);

            // Optional: some releases also publish a cudart bundle that must be extracted alongside.
            var cudaSuffix = ExtractCudaSuffix(cudaZip.name); // e.g. "13.1"
            var cudartZip = rel.assets
                .FirstOrDefault(a => a.name.StartsWith("cudart-llama-", StringComparison.OrdinalIgnoreCase)
                                  && a.name.Contains("-bin-win-cuda-", StringComparison.OrdinalIgnoreCase)
                                  && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                                  && (string.IsNullOrWhiteSpace(cudaSuffix) || a.name.Contains(cudaSuffix, StringComparison.OrdinalIgnoreCase)));

            var dm = new DownloadManager();
            var specs = new List<DownloadManager.AssetSpec>
            {
                new DownloadManager.AssetSpec(
                    Id: $"llama.cpp_{rel.tag_name}_win-cuda-x64",
                    Url: cudaZip.browser_download_url,
                    TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/" + cudaZip.name,
                    Sha256Hex: null)
            };

            if (cudartZip is not null)
            {
                specs.Add(new DownloadManager.AssetSpec(
                    Id: $"llama.cpp_{rel.tag_name}_win-cuda-cudart-x64",
                    Url: cudartZip.browser_download_url,
                    TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/" + cudartZip.name,
                    Sha256Hex: null));
            }

            var downloaded = await dm.EnsureAssetsAsync(specs, progress, ct).ConfigureAwait(false);
            var mainZipPath = downloaded.FirstOrDefault(p => p.EndsWith(cudaZip.name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mainZipPath) || !File.Exists(mainZipPath))
                return (false, "Téléchargement llama.cpp CUDA échoué.", null);

            // Extract main zip (server + dlls)
            var res = ExtractServerZip(mainZipPath, CudaRuntimeDir, rel.tag_name, progress);

            // If cudart present, extract & copy dlls into runtime dir
            var cudartPath = (cudartZip is null) ? null : downloaded.FirstOrDefault(p => p.EndsWith(cudartZip.name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(cudartPath) && File.Exists(cudartPath))
                ExtractOverlayZip(cudartPath, CudaRuntimeDir);

            if (!File.Exists(CudaServerExePath))
                return (false, "Extraction CUDA échouée (llama-server.exe manquant).", null);

            return (true, $"OK ({rel.tag_name})", CudaServerExePath);
        }
        catch (OperationCanceledException)
        {
            return (false, "Annulé.", null);
        }
        catch (Exception ex)
        {
            return (false, "Erreur: " + ex.Message, null);
        }
    }

    private static (bool ok, string message, string? exePath) ExtractServerZip(
        string zipPath,
        string runtimeDir,
        string tag,
        IProgress<DownloadManager.ProgressInfo>? progress)
    {
        progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "extract", 0, null, null));

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

            // Clear runtime dir then copy folder contents
            SafeDeleteDirectory(runtimeDir);
            Directory.CreateDirectory(runtimeDir);

            foreach (var f in Directory.GetFiles(srcDir))
            {
                var dst = Path.Combine(runtimeDir, Path.GetFileName(f));
                File.Copy(f, dst, overwrite: true);
            }

            try
            {
                File.WriteAllText(Path.Combine(runtimeDir, "runtime.tag"), tag);
                File.WriteAllText(Path.Combine(runtimeDir, "runtime.asset"), Path.GetFileName(zipPath));
            }
            catch { /* ignore */ }

            var exePath = Path.Combine(runtimeDir, "llama-server.exe");
            if (!File.Exists(exePath))
                return (false, "Extraction échouée (llama-server.exe manquant après copie).", null);

            return (true, $"OK ({tag})", exePath);
        }
        finally
        {
            SafeDeleteDirectory(tmp);
        }
    }

    private static void ExtractOverlayZip(string zipPath, string runtimeDir)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "saaia_llama_overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);

            foreach (var f in Directory.GetFiles(tmp, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dst = Path.Combine(runtimeDir, Path.GetFileName(f));
                File.Copy(f, dst, overwrite: true);
            }
        }
        finally
        {
            SafeDeleteDirectory(tmp);
        }
    }

    private static string ExtractCudaSuffix(string assetName)
    {
        // Try to pull the token after "-bin-win-cuda-" and before "-x64.zip"
        var marker = "-bin-win-cuda-";
        var i = assetName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        i += marker.Length;
        var j = assetName.IndexOf("-x64.zip", i, StringComparison.OrdinalIgnoreCase);
        if (j < 0) return "";
        return assetName.Substring(i, j - i);
    }

    private static async Task<GhRelease?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SAAIA/1.0");

        using var resp = await http.GetAsync(GitHubApiLatest, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GhRelease>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static void SafeDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
