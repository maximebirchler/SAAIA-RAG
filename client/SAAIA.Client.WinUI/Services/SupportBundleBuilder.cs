using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI.Services;

internal static class SupportBundleBuilder
{
    public static string SupportDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "support");

    public static async Task<string> BuildAsync(
        AppSettings settings,
        object? agentRuntimeSnapshot = null,
        IReadOnlyCollection<string>? include = null,
        string? localAppDataRoot = null)
    {
        Directory.CreateDirectory(SupportDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(SupportDir, $"support-bundle_{stamp}_{Guid.NewGuid():N}.zip");

        var staging = Path.Combine(SupportDir, $"staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        var includes = new HashSet<string>((include ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var includeAgentRuntime = includes.Count == 0
            || includes.Contains("diagnostics")
            || includes.Contains("diagnostics/agent-runtime")
            || includes.Contains("agent-runtime");

        try
        {
            // 1) README
            File.WriteAllText(Path.Combine(staging, "README.txt"),
                "SAAIA support bundle (redacted)\r\n" +
                "- No API key is included.\r\n" +
                "- Contains logs, settings (non-sensitive), environment info, and readiness probes.\r\n");

            // 2) Settings (non-sensitive)
            CopyIfExists(Path.Combine(LocalClientDir(localAppDataRoot), "settings.json"), Path.Combine(staging, "settings.json"));

            // 3) Provisioning (redacted)
            var provPath = Provisioning.FindProvisioningPath();
            if (!string.IsNullOrWhiteSpace(provPath) && File.Exists(provPath))
            {
                var redacted = RedactJsonFile(provPath, new[] { "apiKey" });
                File.WriteAllText(Path.Combine(staging, "provisioning.redacted.json"), redacted);
            }

            // 4) Models manifest if exists
            var modelsJson = Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "Models", "models.json");
            CopyIfExists(modelsJson, Path.Combine(staging, "models.json"));

            // 5) Downloads manifest if exists
            var dlManifest = Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "downloads", "manifest.json");
            CopyIfExists(dlManifest, Path.Combine(staging, "downloads.manifest.json"));

            // 5a) Local governance artifacts when present
            CopyOptionalGovernanceArtifacts(staging, localAppDataRoot);

            // 5b) LLM install artifacts (optional)
            var llmDir = Path.Combine(staging, "llm");
            Directory.CreateDirectory(llmDir);

            // Produced by infra/scripts/llm/install-llm.ps1 (Option B)
            CopyIfExists(Path.Combine(@"C:\SAAIA", "deploy", "docker-compose.llm.yml"), Path.Combine(llmDir, "docker-compose.llm.yml"));
            CopyIfExists(Path.Combine(@"C:\SAAIA", "deploy", "llm.install.json"), Path.Combine(llmDir, "llm.install.json"));
            CopyIfExists(Path.Combine(@"C:\SAAIA", "deploy", "install-llm.log"), Path.Combine(llmDir, "install-llm.log"));

            // Embedded runtime (M6): include lightweight marker files (not the whole runtime folder)
            try
            {
                CopyIfExists(
                    Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "llm", "runtime", "active-runtime.json"),
                    Path.Combine(llmDir, "active-runtime.json"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeEventLogFile, localAppDataRoot),
                    Path.Combine(llmDir, "runtime_event_log.json"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeEventLogFile, localAppDataRoot) + ".sha256",
                    Path.Combine(llmDir, "runtime_event_log.json.sha256"));
                try
                {
                    var runtimeEvents = await RuntimeEventLogStore.ReadLatestAsync(10).ConfigureAwait(false);
                    if (runtimeEvents.Count > 0)
                    {
                        var lines = runtimeEvents.Select(item =>
                            $"{item.At.ToLocalTime():g} | {item.RuntimeId} | {item.EventKind} | build={item.Build ?? "-"} | previous={item.PreviousBuild ?? "-"} | model={item.ModelId ?? "-"} | detail={item.Detail ?? "-"}");
                        File.WriteAllLines(Path.Combine(llmDir, "runtime_event_log.txt"), lines, Encoding.UTF8);
                    }
                }
                catch
                {
                    // ignore
                }
                var rtTag = Path.Combine(LlamaCppReleaseDownloader.CpuRuntimeDir, "runtime.tag");
                CopyIfExists(rtTag, Path.Combine(llmDir, "embedded.runtime.tag"));
                CopyIfExists(Path.Combine(LlamaCppReleaseDownloader.CudaRuntimeDir, "runtime.tag"), Path.Combine(llmDir, "embedded.cuda.runtime.tag"));
                CopyIfExists(Path.Combine(LlamaCppReleaseDownloader.VulkanRuntimeDir, "runtime.tag"), Path.Combine(llmDir, "embedded.vulkan.runtime.tag"));

                var rtInfo = new Dictionary<string, object?>
                {
                    ["cpuExeExists"] = File.Exists(LlamaCppReleaseDownloader.CpuServerExePath),
                    ["cpuExePath"] = LlamaCppReleaseDownloader.CpuServerExePath,
                    ["cudaExeExists"] = File.Exists(LlamaCppReleaseDownloader.CudaServerExePath),
                    ["cudaExePath"] = LlamaCppReleaseDownloader.CudaServerExePath,
                    ["vulkanExeExists"] = File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath),
                    ["vulkanExePath"] = LlamaCppReleaseDownloader.VulkanServerExePath,
                    ["activeRuntimeManifestPath"] = LlamaCppReleaseDownloader.ActiveRuntimeManifestPath,
                };
                File.WriteAllText(Path.Combine(llmDir, "embedded.runtime.json"),
                    JsonSerializer.Serialize(rtInfo, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ignore */ }


// 6) Logs (last 40)
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "logs");
            if (Directory.Exists(logsDir))
            {
                var outLogs = Path.Combine(staging, "logs");
                Directory.CreateDirectory(outLogs);

                foreach (var f in GetLatestFiles(logsDir, 40))
                {
                    CopyIfExists(f, Path.Combine(outLogs, Path.GetFileName(f)));
                }
            }

            // 7) Runtime info (redacted)
            var runtimeInfo = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("o"),
                ["os"] = RuntimeInformation.OSDescription,
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["processArch"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["dotnet"] = Environment.Version.ToString(),
                ["appBaseDir"] = AppContext.BaseDirectory,
                ["userId"] = SafeGetUserId(),
                ["hasApiKey"] = SafeHasApiKey(),
                ["backendUrl"] = settings.BackendUrl,
                ["llmBaseUrl"] = settings.LlmBaseUrl,
                ["modelId"] = settings.ModelId,
                ["safeSettings"] = new Dictionary<string, object?>
                {
                    ["assistantEnabled"] = settings.UseLocalLlm,
                    ["ragQualityPreset"] = settings.RagQualityPreset,
                    ["answerLengthTokens"] = settings.LlmMaxOutputTokens,
                    ["styleTemperature"] = settings.LlmTemperature
                }
            };

            File.WriteAllText(Path.Combine(staging, "runtime.json"),
                JsonSerializer.Serialize(runtimeInfo, new JsonSerializerOptions { WriteIndented = true }));

            // 8) Probes (no auth)
            await WriteProbeAsync(Path.Combine(staging, "backend_ready.json"),
                new Uri(new Uri(settings.BackendUrl.TrimEnd('/')), "/ready")).ConfigureAwait(false);

            await WriteProbeAsync(Path.Combine(staging, "llm_models.json"),
                new Uri(new Uri(settings.LlmBaseUrl.TrimEnd('/')), "models")).ConfigureAwait(false);

            // 8b) Agent runtime diagnostics (optional but enabled by default)
            if (includeAgentRuntime && agentRuntimeSnapshot is not null)
            {
                var diagnosticsDir = Path.Combine(staging, "diagnostics");
                Directory.CreateDirectory(diagnosticsDir);
                File.WriteAllText(Path.Combine(diagnosticsDir, "agent-runtime.json"),
                    JsonSerializer.Serialize(agentRuntimeSnapshot, new JsonSerializerOptions { WriteIndented = true }));

                if (agentRuntimeSnapshot is IReadOnlyDictionary<string, object?> runtimeSnapshot
                    && runtimeSnapshot.TryGetValue("memorySummary", out var memorySummary)
                    && memorySummary is not null)
                {
                    File.WriteAllText(Path.Combine(diagnosticsDir, "agent-memory-summary.json"),
                        JsonSerializer.Serialize(memorySummary, new JsonSerializerOptions { WriteIndented = true }));

                    var renderedSummary = TryRenderMemorySummaryText(runtimeSnapshot);
                    if (!string.IsNullOrWhiteSpace(renderedSummary))
                    {
                        File.WriteAllText(Path.Combine(diagnosticsDir, "agent-memory-summary.txt"), renderedSummary, Encoding.UTF8);
                    }
                }
            }

            // 9) Create zip
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            return zipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string LocalClientDir(string? localAppDataRoot = null) =>
        Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "client");

    private static string GetLocalAppDataRoot(string? localAppDataRoot = null)
        => string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

    private static string ResolveGovernancePath(string fileName, string? localAppDataRoot = null)
        => Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "governance", fileName);

    private static void CopyOptionalGovernanceArtifacts(string staging, string? localAppDataRoot = null)
    {
        var governanceDir = Path.Combine(staging, "governance");
        Directory.CreateDirectory(governanceDir);

        foreach (var fileName in new[]
                 {
                     GovernanceArtifactStore.HardwareProbeFile,
                     GovernanceArtifactStore.WarmupResultsFile,
                     GovernanceArtifactStore.CapabilityStateFile,
                     GovernanceArtifactStore.LastKnownGoodProfileFile,
                     GovernanceArtifactStore.RollbackLogFile,
                     GovernanceArtifactStore.BlacklistAppliedFile,
                     GovernanceArtifactStore.AcquisitionLogFile,
                     GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
                     GovernanceArtifactStore.RuntimeEventLogFile,
                     GovernanceArtifactStore.BatteryPoliciesFile
                 })
        {
            CopyIfExists(ResolveGovernancePath(fileName, localAppDataRoot), Path.Combine(governanceDir, fileName));
        }
    }

    private static void CopyIfExists(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        catch { }
    }

    private static IEnumerable<string> GetLatestFiles(string dir, int max)
    {
        try
        {
            var files = Directory.GetFiles(dir);
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

            var take = Math.Min(files.Length, Math.Max(0, max));
            if (take <= 0) return Array.Empty<string>();

            var list = new List<string>(take);
            for (int i = 0; i < take; i++)
                list.Add(files[i]);
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task WriteProbeAsync(string outputPath, Uri uri)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var resp = await http.GetAsync(uri).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var obj = new Dictionary<string, object?>
            {
                ["url"] = uri.ToString(),
                ["status"] = (int)resp.StatusCode,
                ["ok"] = resp.IsSuccessStatusCode,
                ["body"] = TryParseJson(body) ?? body
            };

            File.WriteAllText(outputPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            var obj = new Dictionary<string, object?>
            {
                ["url"] = uri.ToString(),
                ["ok"] = false,
                ["error"] = ex.GetType().Name + ": " + ex.Message
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static object? TryParseJson(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        }
        catch { return null; }
    }

    private static string RedactJsonFile(string path, IEnumerable<string> keysToRedact)
    {
        try
        {
            var txt = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(txt);

            var redacted = RedactElement(doc.RootElement, new HashSet<string>(keysToRedact, StringComparer.OrdinalIgnoreCase));
            return JsonSerializer.Serialize(redacted, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "{\n  \"error\": \"failed to read/parse provisioning\"\n}";
        }
    }

    private static object? RedactElement(JsonElement el, HashSet<string> keysToRedact)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>();
                foreach (var p in el.EnumerateObject())
                {
                    if (keysToRedact.Contains(p.Name))
                        dict[p.Name] = "***REDACTED***";
                    else
                        dict[p.Name] = RedactElement(p.Value, keysToRedact);
                }
                return dict;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var v in el.EnumerateArray())
                    list.Add(RedactElement(v, keysToRedact));
                return list;
            }
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                if (el.TryGetDouble(out var d)) return d;
                return el.GetRawText();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: return el.GetRawText();
        }
    }

    private static string? SafeGetUserId()
    {
        try { return SecureLocalStore.GetOrCreateUserId(); } catch { return null; }
    }

    private static bool SafeHasApiKey()
    {
        try { return !string.IsNullOrWhiteSpace(SecureLocalStore.GetServerApiKey()); } catch { return false; }
    }

    private static string? TryRenderMemorySummaryText(IReadOnlyDictionary<string, object?> runtimeSnapshot)
    {
        try
        {
            if (!runtimeSnapshot.TryGetValue("memorySummary", out var memorySummary) || memorySummary is null)
                return null;

            var payload = new Dictionary<string, object?>
            {
                ["memorySummary"] = memorySummary
            };

            if (runtimeSnapshot.TryGetValue("routerMs", out var routerMs))
                payload["routerMs"] = routerMs;
            if (runtimeSnapshot.TryGetValue("toolsMs", out var toolsMs))
                payload["toolsMs"] = toolsMs;
            if (runtimeSnapshot.TryGetValue("writerMs", out var writerMs))
                payload["writerMs"] = writerMs;
            if (runtimeSnapshot.TryGetValue("totalMs", out var totalMs))
                payload["totalMs"] = totalMs;

            if (memorySummary is IReadOnlyDictionary<string, object?> summaryMap)
            {
                foreach (var key in new[] { "profile", "schemaVersion", "cdcAlignment", "workspace", "session", "execution", "persistence", "resetPolicy" })
                {
                    if (summaryMap.TryGetValue(key, out var value))
                        payload[key] = value;
                }
            }

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("diagnostic_performance", doc.RootElement, "en");
            return string.IsNullOrWhiteSpace(rendered) ? null : rendered.Trim();
        }
        catch
        {
            return null;
        }
    }
}
