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
/// The runtime is versioned on disk so we can:
/// - keep the legacy runtime available for rollback,
/// - activate a newer build when a model family requires it,
/// - expose the active runtime explicitly instead of relying on "exe present == OK".
/// </summary>
internal sealed class LlamaCppReleaseDownloader
{
    private const string GitHubApiLatest = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";
    private const string ActiveRuntimeArtifactName = "active-runtime.json";
    private const string RuntimeStatusQualified = "qualified";
    private const string RuntimeStatusPendingQualification = "pending_qualification";

    public static string RuntimeRoot =>
        RuntimeRootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "llm", "runtime");

    internal static string? RuntimeRootOverride { get; set; }

    public static string ActiveRuntimeManifestPath => Path.Combine(RuntimeRoot, ActiveRuntimeArtifactName);

    public static string CpuRuntimeBaseDir => Path.Combine(RuntimeRoot, "win-cpu-x64");
    public static string CudaRuntimeBaseDir => Path.Combine(RuntimeRoot, "win-cuda-x64");
    public static string VulkanRuntimeBaseDir => Path.Combine(RuntimeRoot, "win-vulkan-x64");

    public static string CpuRuntimeDir => ResolveRuntimeDir("llama.cpp-cpu", CpuRuntimeBaseDir);
    public static string CudaRuntimeDir => ResolveRuntimeDir("llama.cpp-cuda", CudaRuntimeBaseDir);
    public static string VulkanRuntimeDir => ResolveRuntimeDir("llama.cpp-vulkan", VulkanRuntimeBaseDir);

    public static string CpuServerExePath => ResolveServerExePath("llama.cpp-cpu", CpuRuntimeBaseDir);
    public static string CudaServerExePath => ResolveServerExePath("llama.cpp-cuda", CudaRuntimeBaseDir);
    public static string VulkanServerExePath => ResolveServerExePath("llama.cpp-vulkan", VulkanRuntimeBaseDir);

    private sealed record GhAsset(string name, string browser_download_url, long? size);
    private sealed record GhRelease(string tag_name, List<GhAsset> assets);

    private sealed record RuntimeRollbackCandidate(
        string Build,
        string DirectoryPath,
        string ExePath,
        string? AssetName);

    private sealed record ActiveRuntimeArtifact(
        string Artifact,
        string CdcAlignment,
        IReadOnlyList<ActiveRuntimeItem> Items);

    private sealed record ActiveRuntimeItem(
        string RuntimeId,
        string Backend,
        string Build,
        string DirectoryPath,
        string ExePath,
        string AssetName,
        DateTimeOffset ActivatedAtUtc,
        string? Status = null,
        DateTimeOffset? QualifiedAtUtc = null,
        RuntimeRollbackCandidate? Previous = null);

    internal sealed record ActiveRuntimeState(
        string RuntimeId,
        string Build,
        string ExePath,
        string? Status,
        string? PreviousBuild,
        DateTimeOffset ActivatedAtUtc,
        DateTimeOffset? QualifiedAtUtc);

    public Task<(bool ok, string message, string? exePath)> EnsureWindowsCpuAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
        => EnsureWindowsCpuAsync(progress, minBuild: null, ct);

    public async Task<(bool ok, string message, string? exePath)> EnsureWindowsCpuAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        string? minBuild,
        CancellationToken ct)
    {
        try
        {
            if (TryResolveInstalledRuntime("llama.cpp-cpu", CpuRuntimeBaseDir, minBuild, out var existingExe))
                return (true, "OK", existingExe);

            Directory.CreateDirectory(CpuRuntimeBaseDir);

            progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "resolve", 0, null, null));
            var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (rel is null)
                return (false, "Impossible de recuperer la release llama.cpp (GitHub API).", null);

            var cpuZip = rel.assets
                .FirstOrDefault(a => a.name.Contains("-bin-win-cpu-", StringComparison.OrdinalIgnoreCase)
                                  && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                                  && a.name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase));

            if (cpuZip is null)
                return (false, "Release llama.cpp trouvee, mais aucun binaire Windows CPU x64 n'a ete detecte.", null);

            var dm = new DownloadManager();
            var zipSpec = new DownloadManager.AssetSpec(
                Id: $"llama.cpp_{rel.tag_name}_win-cpu-x64",
                Url: cpuZip.browser_download_url,
                TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/{cpuZip.name}",
                Sha256Hex: null);

            var downloaded = await dm.EnsureAssetsAsync(new[] { zipSpec }, progress, ct).ConfigureAwait(false);
            var zipPath = downloaded.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return (false, "Telechargement llama.cpp echoue.", null);

            return InstallRuntimeFromZip("llama.cpp-cpu", "cpu", CpuRuntimeBaseDir, zipPath, rel.tag_name, cpuZip.name, progress);
        }
        catch (OperationCanceledException)
        {
            return (false, "Annule.", null);
        }
        catch (Exception ex)
        {
            return (false, "Erreur: " + ex.Message, null);
        }
    }

    public Task<(bool ok, string message, string? exePath)> EnsureWindowsCudaAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
        => EnsureWindowsCudaAsync(progress, minBuild: null, ct);

    public async Task<(bool ok, string message, string? exePath)> EnsureWindowsCudaAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        string? minBuild,
        CancellationToken ct)
    {
        try
        {
            if (TryResolveInstalledRuntime("llama.cpp-cuda", CudaRuntimeBaseDir, minBuild, out var existingExe))
                return (true, "OK", existingExe);

            Directory.CreateDirectory(CudaRuntimeBaseDir);

            progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "resolve", 0, null, null));
            var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (rel is null)
                return (false, "Impossible de recuperer la release llama.cpp (GitHub API).", null);

            var cudaCandidates = rel.assets
                .Where(a => a.name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
                         && a.name.Contains("-bin-win-cuda-", StringComparison.OrdinalIgnoreCase)
                         && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                         && !a.name.StartsWith("cudart-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var cudaZip = cudaCandidates.FirstOrDefault(a => a.name.Contains("-bin-win-cuda-12.", StringComparison.OrdinalIgnoreCase))
                      ?? cudaCandidates.FirstOrDefault(a => a.name.Contains("-bin-win-cuda-12", StringComparison.OrdinalIgnoreCase))
                      ?? cudaCandidates.FirstOrDefault();

            if (cudaZip is null)
                return (false, "Release llama.cpp trouvee, mais aucun binaire Windows CUDA x64 n'a ete detecte.", null);

            var cudaSuffix = ExtractCudaSuffix(cudaZip.name);
            var cudartZip = rel.assets.FirstOrDefault(a =>
                a.name.StartsWith("cudart-llama-", StringComparison.OrdinalIgnoreCase)
                && a.name.Contains("-bin-win-cuda-", StringComparison.OrdinalIgnoreCase)
                && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(cudaSuffix) || a.name.Contains(cudaSuffix, StringComparison.OrdinalIgnoreCase)));

            var dm = new DownloadManager();
            var specs = new List<DownloadManager.AssetSpec>
            {
                new(
                    Id: $"llama.cpp_{rel.tag_name}_win-cuda-x64",
                    Url: cudaZip.browser_download_url,
                    TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/{cudaZip.name}",
                    Sha256Hex: null)
            };

            if (cudartZip is not null)
            {
                specs.Add(new DownloadManager.AssetSpec(
                    Id: $"llama.cpp_{rel.tag_name}_win-cuda-cudart-x64",
                    Url: cudartZip.browser_download_url,
                    TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/{cudartZip.name}",
                    Sha256Hex: null));
            }

            var downloaded = await dm.EnsureAssetsAsync(specs, progress, ct).ConfigureAwait(false);
            var mainZipPath = downloaded.FirstOrDefault(p => p.EndsWith(cudaZip.name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(mainZipPath) || !File.Exists(mainZipPath))
                return (false, "Telechargement llama.cpp CUDA echoue.", null);

            var install = InstallRuntimeFromZip("llama.cpp-cuda", "cuda", CudaRuntimeBaseDir, mainZipPath, rel.tag_name, cudaZip.name, progress);
            if (!install.ok || string.IsNullOrWhiteSpace(install.exePath))
                return install;

            var cudartPath = cudartZip is null
                ? null
                : downloaded.FirstOrDefault(p => p.EndsWith(cudartZip.name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(cudartPath) && File.Exists(cudartPath))
                ExtractOverlayZip(cudartPath, Path.Combine(CudaRuntimeBaseDir, rel.tag_name));

            if (!File.Exists(CudaServerExePath))
                return (false, "Extraction CUDA echouee (llama-server.exe manquant).", null);

            return (true, $"OK ({rel.tag_name})", CudaServerExePath);
        }
        catch (OperationCanceledException)
        {
            return (false, "Annule.", null);
        }
        catch (Exception ex)
        {
            return (false, "Erreur: " + ex.Message, null);
        }
    }

    public Task<(bool ok, string message, string? exePath)> EnsureWindowsVulkanAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
        => EnsureWindowsVulkanAsync(progress, minBuild: null, ct);

    public async Task<(bool ok, string message, string? exePath)> EnsureWindowsVulkanAsync(
        IProgress<DownloadManager.ProgressInfo>? progress,
        string? minBuild,
        CancellationToken ct)
    {
        try
        {
            if (TryResolveInstalledRuntime("llama.cpp-vulkan", VulkanRuntimeBaseDir, minBuild, out var existingExe))
                return (true, "OK", existingExe);

            Directory.CreateDirectory(VulkanRuntimeBaseDir);

            progress?.Report(new DownloadManager.ProgressInfo("llama.cpp", "resolve", 0, null, null));
            var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            if (rel is null)
                return (false, "Impossible de recuperer la release llama.cpp (GitHub API).", null);

            var vkZip = rel.assets.FirstOrDefault(a =>
                a.name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
                && a.name.Contains("-bin-win-vulkan-", StringComparison.OrdinalIgnoreCase)
                && a.name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase));

            if (vkZip is null)
                return (false, "Release llama.cpp trouvee, mais aucun binaire Windows Vulkan x64 n'a ete detecte.", null);

            var dm = new DownloadManager();
            var zipSpec = new DownloadManager.AssetSpec(
                Id: $"llama.cpp_{rel.tag_name}_win-vulkan-x64",
                Url: vkZip.browser_download_url,
                TargetRelativePath: $"downloads/llama.cpp/{rel.tag_name}/{vkZip.name}",
                Sha256Hex: null);

            var downloaded = await dm.EnsureAssetsAsync(new[] { zipSpec }, progress, ct).ConfigureAwait(false);
            var zipPath = downloaded.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return (false, "Telechargement llama.cpp Vulkan echoue.", null);

            return InstallRuntimeFromZip("llama.cpp-vulkan", "vulkan", VulkanRuntimeBaseDir, zipPath, rel.tag_name, vkZip.name, progress);
        }
        catch (OperationCanceledException)
        {
            return (false, "Annule.", null);
        }
        catch (Exception ex)
        {
            return (false, "Erreur: " + ex.Message, null);
        }
    }

    private static (bool ok, string message, string? exePath) InstallRuntimeFromZip(
        string runtimeId,
        string backend,
        string runtimeBaseDir,
        string zipPath,
        string tag,
        string assetName,
        IProgress<DownloadManager.ProgressInfo>? progress)
    {
        var runtimeDir = Path.Combine(runtimeBaseDir, tag);
        var result = ExtractServerZip(zipPath, runtimeDir, tag, progress);
        if (!result.ok || string.IsNullOrWhiteSpace(result.exePath))
            return result;

        var previous = TryReadActiveRuntime(runtimeId);
        var hasRollbackCandidate =
            previous is not null
            && File.Exists(previous.ExePath)
            && !string.Equals(previous.Build, tag, StringComparison.OrdinalIgnoreCase);

        WriteActiveRuntime(new ActiveRuntimeItem(
            RuntimeId: runtimeId,
            Backend: backend,
            Build: tag,
            DirectoryPath: runtimeDir,
            ExePath: result.exePath,
            AssetName: assetName,
            ActivatedAtUtc: DateTimeOffset.UtcNow,
            Status: hasRollbackCandidate ? RuntimeStatusPendingQualification : RuntimeStatusQualified,
            QualifiedAtUtc: hasRollbackCandidate ? null : DateTimeOffset.UtcNow,
            Previous: hasRollbackCandidate
                ? new RuntimeRollbackCandidate(
                    previous!.Build,
                    previous.DirectoryPath,
                    previous.ExePath,
                    previous.AssetName)
                : null));

        return result;
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
            catch
            {
                // ignore
            }

            var exePath = Path.Combine(runtimeDir, "llama-server.exe");
            if (!File.Exists(exePath))
                return (false, "Extraction echouee (llama-server.exe manquant apres copie).", null);

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
        const string marker = "-bin-win-cuda-";
        var i = assetName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return string.Empty;

        i += marker.Length;
        var j = assetName.IndexOf("-x64.zip", i, StringComparison.OrdinalIgnoreCase);
        if (j < 0)
            return string.Empty;

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

    private static string ResolveRuntimeDir(string runtimeId, string runtimeBaseDir)
    {
        var active = TryReadActiveRuntime(runtimeId);
        if (active is not null && Directory.Exists(active.DirectoryPath))
            return active.DirectoryPath;

        return runtimeBaseDir;
    }

    private static string ResolveServerExePath(string runtimeId, string runtimeBaseDir)
    {
        var active = TryReadActiveRuntime(runtimeId);
        if (active is not null && File.Exists(active.ExePath))
            return active.ExePath;

        return Path.Combine(runtimeBaseDir, "llama-server.exe");
    }

    private static bool TryResolveInstalledRuntime(
        string runtimeId,
        string runtimeBaseDir,
        string? minBuild,
        out string exePath)
    {
        var active = TryReadActiveRuntime(runtimeId);
        if (active is not null
            && File.Exists(active.ExePath)
            && BuildSatisfies(active.Build, minBuild))
        {
            exePath = active.ExePath;
            return true;
        }

        var legacyExe = Path.Combine(runtimeBaseDir, "llama-server.exe");
        var legacyBuild = RuntimeCompatibilityPolicyStore.ReadRuntimeBuild(legacyExe);
        if (File.Exists(legacyExe) && BuildSatisfies(legacyBuild, minBuild))
        {
            exePath = legacyExe;
            return true;
        }

        exePath = string.Empty;
        return false;
    }

    private static bool BuildSatisfies(string? build, string? minBuild)
    {
        if (string.IsNullOrWhiteSpace(minBuild))
            return true;

        if (string.IsNullOrWhiteSpace(build))
            return false;

        return ParseBuildNumber(build) >= ParseBuildNumber(minBuild);
    }

    private static int ParseBuildNumber(string? build)
    {
        var value = (build ?? string.Empty).Trim();
        if (value.StartsWith("b", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    private static ActiveRuntimeItem? TryReadActiveRuntime(string runtimeId)
        => TryReadActiveRuntimeArtifact()?.Items.FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

    internal static ActiveRuntimeState? TryGetActiveRuntimeState(string runtimeId)
    {
        var item = TryReadActiveRuntime(runtimeId);
        return item is null
            ? null
            : new ActiveRuntimeState(
                item.RuntimeId,
                item.Build,
                item.ExePath,
                item.Status,
                item.Previous?.Build,
                item.ActivatedAtUtc,
                item.QualifiedAtUtc);
    }

    internal static bool TryMarkRuntimeQualified(string runtimeId)
    {
        if (!TryReadActiveRuntimeArtifactForUpdate(out var artifact, out var items))
            return false;

        var index = items.FindIndex(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        var current = items[index];
        items[index] = current with
        {
            Status = RuntimeStatusQualified,
            QualifiedAtUtc = DateTimeOffset.UtcNow,
            Previous = null
        };

        WriteActiveRuntimeArtifact(artifact!, items);
        return true;
    }

    internal static bool TryRollbackPendingRuntime(
        string runtimeId,
        out string? exePath,
        out string? build)
    {
        exePath = null;
        build = null;

        if (!TryReadActiveRuntimeArtifactForUpdate(out var artifact, out var items))
            return false;

        var index = items.FindIndex(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        var current = items[index];
        if (!string.Equals(current.Status, RuntimeStatusPendingQualification, StringComparison.OrdinalIgnoreCase)
            || current.Previous is null
            || string.IsNullOrWhiteSpace(current.Previous.ExePath)
            || !File.Exists(current.Previous.ExePath))
        {
            return false;
        }

        var rollback = new ActiveRuntimeItem(
            RuntimeId: current.RuntimeId,
            Backend: current.Backend,
            Build: current.Previous.Build,
            DirectoryPath: current.Previous.DirectoryPath,
            ExePath: current.Previous.ExePath,
            AssetName: current.Previous.AssetName ?? current.AssetName,
            ActivatedAtUtc: DateTimeOffset.UtcNow,
            Status: RuntimeStatusQualified,
            QualifiedAtUtc: DateTimeOffset.UtcNow,
            Previous: null);

        items[index] = rollback;
        WriteActiveRuntimeArtifact(artifact!, items);

        exePath = rollback.ExePath;
        build = rollback.Build;
        return true;
    }

    private static ActiveRuntimeArtifact? TryReadActiveRuntimeArtifact()
    {
        try
        {
            if (!File.Exists(ActiveRuntimeManifestPath))
                return null;

            var json = File.ReadAllText(ActiveRuntimeManifestPath);
            return JsonSerializer.Deserialize<ActiveRuntimeArtifact>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static void WriteActiveRuntime(ActiveRuntimeItem item)
    {
        var items = TryReadActiveRuntimeArtifact()?.Items.ToList() ?? new List<ActiveRuntimeItem>();
        items.RemoveAll(current => string.Equals(current.RuntimeId, item.RuntimeId, StringComparison.OrdinalIgnoreCase));
        items.Add(item);

        WriteActiveRuntimeArtifact(
            new ActiveRuntimeArtifact(ActiveRuntimeArtifactName, "v3.1", Array.Empty<ActiveRuntimeItem>()),
            items);
    }

    private static bool TryReadActiveRuntimeArtifactForUpdate(
        out ActiveRuntimeArtifact? artifact,
        out List<ActiveRuntimeItem> items)
    {
        artifact = TryReadActiveRuntimeArtifact();
        items = artifact?.Items.ToList() ?? new List<ActiveRuntimeItem>();
        return artifact is not null && items.Count > 0;
    }

    private static void WriteActiveRuntimeArtifact(
        ActiveRuntimeArtifact artifact,
        List<ActiveRuntimeItem> items)
    {
        Directory.CreateDirectory(RuntimeRoot);

        var updated = artifact with
        {
            Artifact = ActiveRuntimeArtifactName,
            CdcAlignment = "v3.1",
            Items = items
                .OrderBy(current => current.RuntimeId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        WriteAllTextWithRetry(ActiveRuntimeManifestPath, json);
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

    private static void WriteAllTextWithRetry(string path, string content)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.WriteAllText(path, content);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }

        File.WriteAllText(path, content);
    }
}
