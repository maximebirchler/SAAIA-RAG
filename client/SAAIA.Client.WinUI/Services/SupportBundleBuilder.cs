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
using SAAIA.Client.Core.Diagnostics;
using System.Threading;

namespace SAAIA.Client.WinUI.Services;

internal static class SupportBundleBuilder
{
    private sealed record BundlePiece(string Path, string Status, string? Reason = null);
    private sealed record BundleCollection(string Root, List<BundlePiece> Pieces);
    private static readonly AsyncLocal<BundleCollection?> Collection = new();

    public static string SupportDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "support");

    public static async Task<string> BuildAsync(
        AppSettings settings,
        object? agentRuntimeSnapshot = null,
        IReadOnlyCollection<string>? include = null,
        string? localAppDataRoot = null,
        Func<string, Uri, Task>? probeWriter = null)
    {
        Directory.CreateDirectory(SupportDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(SupportDir, $"support-bundle_{stamp}_{Guid.NewGuid():N}.zip");

        var staging = Path.Combine(SupportDir, $"staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var previousCollection = Collection.Value;
        var pieces = new List<BundlePiece>();
        Collection.Value = new(staging, pieces);

        var includes = new HashSet<string>((include ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var writeProbe = probeWriter ?? WriteProbeAsync;
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
                "- Legacy log content is omitted unless SAAIA_DIAGNOSTIC_CONTENT=true.\r\n" +
                "- Content-free turn records, collection status, settings and readiness probes are included.\r\n" +
                "- A successful Windows launch does not prove PDF page rendering.\r\n");

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
                    Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "llm", "runtime", "active-runtime.json.sha256"),
                    Path.Combine(llmDir, "active-runtime.json.sha256"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeEventLogFile, localAppDataRoot),
                    Path.Combine(llmDir, "runtime_event_log.json"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeEventLogFile, localAppDataRoot) + ".sha256",
                    Path.Combine(llmDir, "runtime_event_log.json.sha256"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeCompatibilityPolicyFile, localAppDataRoot),
                    Path.Combine(llmDir, "runtime_compatibility_policy.json"));
                CopyIfExists(
                    ResolveGovernancePath(GovernanceArtifactStore.RuntimeCompatibilityPolicyFile, localAppDataRoot) + ".sha256",
                    Path.Combine(llmDir, "runtime_compatibility_policy.json.sha256"));
                try
                {
                    var runtimeEvents = await RuntimeEventLogStore.ReadLatestAsync(10).ConfigureAwait(false);
                    if (runtimeEvents.Count > 0)
                    {
                        var lines = runtimeEvents.Select(item =>
                            $"{item.At.ToLocalTime():g} | {item.RuntimeId} | {item.EventKind} | build={item.Build ?? "-"} | previous={item.PreviousBuild ?? "-"} | model={item.ModelId ?? "-"} | detail={(ClientDiagnostics.IncludeContent ? item.Detail ?? "-" : "[REDACTED]")}");
                        File.WriteAllLines(Path.Combine(llmDir, "runtime_event_log.txt"), lines, Encoding.UTF8);
                    }
                }
                catch (Exception exception)
                {
                    pieces.Add(new("llm/runtime_event_log.txt", "failed", exception.GetType().Name));
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
            catch (Exception exception)
            {
                pieces.Add(new("llm/embedded.runtime.json", "failed", exception.GetType().Name));
            }


            // 6) Logs (last 40)
            var logsDir = Path.Combine(GetLocalAppDataRoot(localAppDataRoot), "SAAIA", "logs");
            if (Directory.Exists(logsDir))
            {
                var outLogs = Path.Combine(staging, "logs");
                Directory.CreateDirectory(outLogs);

                foreach (var f in GetLatestFiles(logsDir, 40))
                {
                    if (ClientDiagnostics.IncludeContent)
                        CopyIfExists(f, Path.Combine(outLogs, Path.GetFileName(f)));
                    else
                        WriteMetadataLog(f, Path.Combine(outLogs, Path.GetFileName(f)));
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
            await WriteCollectedProbeAsync(Path.Combine(staging, "backend_ready.json"),
                () => new Uri(new Uri(settings.BackendUrl.TrimEnd('/')), "/ready"), writeProbe,
                injected: probeWriter is not null).ConfigureAwait(false);

            await WriteCollectedProbeAsync(Path.Combine(staging, "llm_models.json"),
                () => new Uri(new Uri(settings.LlmBaseUrl.TrimEnd('/')), "models"), writeProbe,
                injected: probeWriter is not null).ConfigureAwait(false);

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

            // Every entry point exports the same bounded production history, even without an orchestrator instance.
            var commonDiagnostics = Path.Combine(staging, "diagnostics");
            Directory.CreateDirectory(commonDiagnostics);
            File.WriteAllText(Path.Combine(commonDiagnostics, "client-turns.json"), JsonSerializer.Serialize(
                ClientDiagnostics.Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
            pieces.Add(new("diagnostics/client-turns.json", "present"));
            if (!includeAgentRuntime || agentRuntimeSnapshot is null)
                pieces.Add(new("diagnostics/agent-runtime.json", "unavailable", "no_live_orchestrator_snapshot"));
            RedactStructuredContent(staging);
            // 9) Create zip
            RedactExternalLlmSecrets(staging);
            File.WriteAllText(Path.Combine(staging, "collection-manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = "saaia.support-collection.v1", capturedAtUtc = DateTimeOffset.UtcNow,
                probeImplementation = probeWriter is null ? "http" : "injected",
                contentMode = ClientDiagnostics.IncludeContent ? "explicit_content" : "metadata_only",
                collectorFailures = ClientDiagnostics.Journal.CollectorFailures,
                collectionComplete = pieces.All(piece => piece.Status != "failed")
                    && ClientDiagnostics.Journal.CollectorFailures == 0 && ClientLog.FailedWrites == 0,
                pieces,
                files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(staging, path).Replace('\\', '/')).OrderBy(path => path).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            return zipPath;
        }
        finally
        {
            Collection.Value = previousCollection;
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static void WriteMetadataLog(string source, string destination)
    {
        try
        {
            var structured = File.ReadLines(source).Where(line => line.Contains("CLIENT_PROBE ", StringComparison.Ordinal)).ToList();
            structured.Insert(0, "[REDACTED] Legacy log content omitted by metadata-only export policy.");
            File.WriteAllLines(destination, structured, Encoding.UTF8);
            RecordPiece(destination, "present", "metadata_filtered");
        }
        catch (Exception exception)
        {
            RecordPiece(destination, "failed", exception.GetType().Name);
        }
    }

    private static void RedactStructuredContent(string staging)
    {
        foreach (var path in Directory.EnumerateFiles(staging, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var original = File.ReadAllText(path);
                var redacted = SupportDiagnosticRedactor.RedactJson(original, ClientDiagnostics.IncludeContent);
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(original),
                        System.Text.Json.Nodes.JsonNode.Parse(redacted)))
                {
                    File.WriteAllText(path, redacted);
                    RefreshExportChecksum(path);
                    RecordPiece(path, "present", "redacted_copy");
                }
            }
            catch (Exception exception)
            {
                // Never ship an unreadable JSON file which could contain unredacted secrets or text.
                File.WriteAllText(path, "{\"status\":\"unavailable\",\"reason\":\"redaction_failed\"}");
                RefreshExportChecksum(path);
                RecordPiece(path, "failed", exception.GetType().Name);
            }
        }
    }

    private static void RefreshExportChecksum(string path)
    {
        var sidecar = path + ".sha256";
        if (!File.Exists(sidecar)) return;
        if (!File.Exists(sidecar + ".source")) File.Copy(sidecar, sidecar + ".source");
        File.WriteAllText(sidecar, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        RecordPiece(sidecar, "present", "checksum_of_redacted_export_original_in_source_sidecar");
    }

    private static void RedactExternalLlmSecrets(string stagingDirectory)
    {
        var secrets = new[]
            {
                Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable),
                Environment.GetEnvironmentVariable(LlmProviderConfiguration.RunPodKeyEnvironmentVariable)
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".jsonl", ".log", ".txt", ".yaml", ".yml"
        };
        foreach (var path in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            if (!textExtensions.Contains(Path.GetExtension(path)))
                continue;
            var original = File.ReadAllText(path);
            var content = SupportDiagnosticRedactor.RedactText(original);
            foreach (var secret in secrets)
                content = content.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            if (!string.Equals(original, content, StringComparison.Ordinal))
            {
                File.WriteAllText(path, content, Encoding.UTF8);
                RefreshExportChecksum(path);
            }
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
                     GovernanceArtifactStore.ModelCatalogFile,
                     GovernanceArtifactStore.ModelCollectionsFile,
                     GovernanceArtifactStore.ModelPolicyFile,
                     GovernanceArtifactStore.ModelSourcesFile,
                     GovernanceArtifactStore.WarmupProfilesFile,
                     GovernanceArtifactStore.HardwareProbeFile,
                     GovernanceArtifactStore.WarmupResultsFile,
                     GovernanceArtifactStore.CapabilityStateFile,
                     GovernanceArtifactStore.LastKnownGoodProfileFile,
                     GovernanceArtifactStore.BlacklistFile,
                     GovernanceArtifactStore.RollbackLogFile,
                     GovernanceArtifactStore.BlacklistAppliedFile,
                     GovernanceArtifactStore.AcquisitionLogFile,
                     GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
                     GovernanceArtifactStore.RuntimeEventLogFile,
                     GovernanceArtifactStore.BatteryPoliciesFile
                 })
        {
            var sourcePath = ResolveGovernancePath(fileName, localAppDataRoot);
            var destinationPath = Path.Combine(governanceDir, fileName);
            CopyIfExists(sourcePath, destinationPath);
            CopyIfExists(sourcePath + ".sha256", destinationPath + ".sha256");
        }
    }

    private static void CopyIfExists(string src, string dst)
    {
        try
        {
            if (!File.Exists(src))
            {
                RecordPiece(dst, "unavailable", "optional_file_missing");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (!ClientDiagnostics.IncludeContent && Path.GetExtension(src) is ".log" or ".txt")
            {
                WriteMetadataLog(src, dst);
                return;
            }
            File.Copy(src, dst, overwrite: true);
            RecordPiece(dst, "present");
        }
        catch (Exception exception)
        {
            RecordPiece(dst, "failed", exception.GetType().Name);
        }
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
        catch (Exception exception)
        {
            if (Collection.Value is { } collection)
                RecordPiece(Path.Combine(collection.Root, "logs"), "failed", exception.GetType().Name);
            return Array.Empty<string>();
        }
    }

    private static void RecordPiece(string destination, string status, string? reason = null)
    {
        if (Collection.Value is not { } collection) return;
        collection.Pieces.Add(new(Path.GetRelativePath(collection.Root, destination).Replace('\\', '/'), status, reason));
    }

    private static async Task WriteCollectedProbeAsync(string outputPath, Func<Uri> resolveUri,
        Func<string, Uri, Task> writeProbe, bool injected)
    {
        try
        {
            await writeProbe(outputPath, resolveUri()).ConfigureAwait(false);
            if (injected) RecordPiece(outputPath, File.Exists(outputPath) ? "present" : "failed",
                File.Exists(outputPath) ? "injected_probe" : "probe_output_missing");
        }
        catch (Exception exception)
        {
            RecordPiece(outputPath, "failed", exception.GetType().Name);
            // A failed optional readiness capture must not discard the rest of the support archive.
            try { File.WriteAllText(outputPath, "{\"status\":\"unavailable\",\"reason\":\"probe_collection_failed\"}"); }
            catch (Exception writeException) { RecordPiece(outputPath, "failed", "fallback_" + writeException.GetType().Name); }
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
                ["body"] = TryParseJson(body) ?? (ClientDiagnostics.IncludeContent ? body : "[REDACTED non-JSON response]")
            };

            File.WriteAllText(outputPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            RecordPiece(outputPath, resp.IsSuccessStatusCode ? "present" : "failed", resp.IsSuccessStatusCode ? null : "http_unsuccessful");
        }
        catch (Exception ex)
        {
            var obj = new Dictionary<string, object?>
            {
                ["url"] = uri.ToString(),
                ["ok"] = false,
                ["error"] = ex.GetType().Name
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            RecordPiece(outputPath, "failed", ex.GetType().Name);
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
