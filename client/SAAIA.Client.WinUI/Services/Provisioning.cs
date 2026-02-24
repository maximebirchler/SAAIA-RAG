using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Provisioning: the installer / IT can drop a provisioning.json so the end user has
/// nothing to configure.
///
/// Paths (first match wins):
///  - %ProgramData%\SAAIA\provisioning.json
///  - %LOCALAPPDATA%\SAAIA\provisioning.json
///
/// Sensitive values (apiKey) are stored via SecureLocalStore (DPAPI), never in AppSettings.
/// </summary>
internal static class Provisioning
{
    private const string FileName = "provisioning.json";

    private static string ProgramDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SAAIA", FileName);

    private static string LocalAppDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", FileName);

    private static string[] CandidatePaths => new[] { ProgramDataPath, LocalAppDataPath };

    private sealed record UiDto(bool? ShowAdvanced, bool? AutoConnect);

    private sealed record LlmDto(
        string? Mode,
        string? BaseUrl,
        string? ModelId,
        bool? Enabled,
        bool? ManageProcess,
        bool? StrictMode,
        string? RagQualityPreset,
        double? Temperature,
        int? MaxOutputTokens,
        bool? AutoInstallOnFirstRun,
        string? InstallScriptPath);

    private sealed record DownloadAssetDto(
        string? Id,
        string? Url,
        string? Sha256,
        string? Target);

    private sealed record DownloadsDto(
        bool? AutoInstallOnFirstRun,
        List<DownloadAssetDto>? Assets);

    private sealed record ProvisioningDto(
        string? BackendUrl,
        string? ApiKey,
        UiDto? Ui,
        LlmDto? Llm,
        DownloadsDto? Downloads);

    public static string? FindProvisioningPath()
    {
        foreach (var p in CandidatePaths)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool TryApplyIfPresent(out string message)
    {
        message = "";

        var path = FindProvisioningPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var hash = ComputeSha256Hex(json);

            var settings = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(settings.ProvisioningHash) &&
                string.Equals(settings.ProvisioningHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                message = $"Provisioning already applied ({Path.GetFileName(path)}).";
                return false;
            }

            var dto = JsonSerializer.Deserialize<ProvisioningDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (dto is null)
            {
                message = "Provisioning file invalid (empty).";
                return false;
            }

            // Apply non-sensitive settings
            if (!string.IsNullOrWhiteSpace(dto.BackendUrl))
                settings.BackendUrl = dto.BackendUrl.Trim().TrimEnd('/');

            if (dto.Ui?.ShowAdvanced is bool adv)
                settings.ShowAdvancedUi = adv;

            if (dto.Ui?.AutoConnect is bool ac)
                settings.AutoConnect = ac;

            // LLM
            if (!string.IsNullOrWhiteSpace(dto.Llm?.Mode))
                settings.LlmMode = (dto.Llm!.Mode ?? "embedded").Trim();

            if (dto.Llm?.Enabled is bool llmEnabled)
                settings.UseLocalLlm = llmEnabled;

            // For safety, Mode implies a default manageProcess behavior.
            // - embedded => client manages llama.cpp process
            // - docker/external => process managed elsewhere
            var mode = (settings.LlmMode ?? "embedded").Trim().ToLowerInvariant();
            if (mode == "embedded")
                settings.ManageLocalLlmProcess = true;
            else
                settings.ManageLocalLlmProcess = false;

            // Allow explicit override (advanced deployments)
            if (dto.Llm?.ManageProcess is bool mp)
                settings.ManageLocalLlmProcess = mp;

            if (!string.IsNullOrWhiteSpace(dto.Llm?.BaseUrl) && TryParseBaseUrl(dto.Llm!.BaseUrl!, out var host, out var port))
            {
                settings.Host = host;
                settings.Port = port;
            }

            if (!string.IsNullOrWhiteSpace(dto.Llm?.ModelId))
                settings.ModelId = dto.Llm!.ModelId!.Trim();

            if (dto.Llm?.Temperature is double t)
                settings.LlmTemperature = t;

            if (dto.Llm?.MaxOutputTokens is int mt)
                settings.LlmMaxOutputTokens = mt;

            if (dto.Llm?.StrictMode is bool sm)
                settings.StrictMode = sm;

            if (!string.IsNullOrWhiteSpace(dto.Llm?.RagQualityPreset))
                settings.RagQualityPreset = dto.Llm!.RagQualityPreset!.Trim();

            // Sensitive
            if (!string.IsNullOrWhiteSpace(dto.ApiKey))
                SecureLocalStore.SetServerApiKey(dto.ApiKey.Trim());

            settings.ProvisioningHash = hash;
            settings.Save();

            message = $"Provisioning applied from {path}.";
            return true;
        }
        catch (Exception ex)
        {
            message = "Provisioning apply failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads (but does not apply) the optional Downloads plan from provisioning.json.
    /// This enables auto-download of heavy assets (models, binaries).
    /// </summary>
    public static bool TryGetDownloadAssets(out IReadOnlyList<DownloadManager.AssetSpec> assets, out bool autoInstallOnFirstRun)
    {
        assets = Array.Empty<DownloadManager.AssetSpec>();
        autoInstallOnFirstRun = false;

        var path = FindProvisioningPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ProvisioningDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var dl = dto?.Downloads;
            if (dl?.Assets is null || dl.Assets.Count == 0)
                return false;

            autoInstallOnFirstRun = dl.AutoInstallOnFirstRun ?? false;

            var list = new List<DownloadManager.AssetSpec>();
            foreach (var a in dl.Assets)
            {
                var id = (a.Id ?? "").Trim();
                var url = (a.Url ?? "").Trim();
                var target = (a.Target ?? "").Trim();
                var sha = string.IsNullOrWhiteSpace(a.Sha256) ? null : a.Sha256.Trim();

                if (id.Length == 0 || url.Length == 0 || target.Length == 0)
                    continue;

                list.Add(new DownloadManager.AssetSpec(id, url, target, sha));
            }

            if (list.Count == 0) return false;
            assets = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Option B (dev/test): returns true when provisioning says mode=docker and autoInstallOnFirstRun=true.
    /// </summary>
    public static bool TryGetLlmAutoInstall(out bool autoInstallOnFirstRun, out string? installScriptPath)
    {
        autoInstallOnFirstRun = false;
        installScriptPath = null;

        var path = FindProvisioningPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ProvisioningDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var mode = (dto?.Llm?.Mode ?? "").Trim().ToLowerInvariant();
            if (mode != "docker")
                return false;

            autoInstallOnFirstRun = dto?.Llm?.AutoInstallOnFirstRun ?? false;
            installScriptPath = dto?.Llm?.InstallScriptPath;

            // Sensible default: script deployed by infra install.
            if (string.IsNullOrWhiteSpace(installScriptPath))
                installScriptPath = Path.Combine(@"C:\SAAIA", "deploy", "install-llm.ps1");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool TryParseBaseUrl(string baseUrl, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 1234;

        try
        {
            var u = new Uri(baseUrl.Trim(), UriKind.Absolute);
            host = string.IsNullOrWhiteSpace(u.Host) ? host : u.Host;
            port = u.Port > 0 ? u.Port : port;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
