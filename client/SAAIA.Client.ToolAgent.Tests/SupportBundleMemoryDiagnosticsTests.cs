using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SupportBundleMemoryDiagnosticsTests
{
    [Fact]
    public async Task Support_bundle_includes_human_readable_memory_summary_when_runtime_snapshot_is_present()
    {
        var settings = new AppSettings
        {
            BackendUrl = "http://127.0.0.1:9",
            Host = "127.0.0.1",
            Port = 9
        };

        var runtimeSnapshot = new Dictionary<string, object?>
        {
            ["routerMs"] = 11,
            ["toolsMs"] = 22,
            ["writerMs"] = 7,
            ["totalMs"] = 40,
            ["memorySummary"] = new Dictionary<string, object?>
            {
                ["profile"] = "cdc-v3-m1lite-m3-m6",
                ["schemaVersion"] = 1,
                ["cdcAlignment"] = "v3.1",
                ["workspace"] = new Dictionary<string, object?>
                {
                    ["catalogCategoriesCount"] = 2,
                    ["knownDocumentsCount"] = 3,
                    ["hasCapabilitiesSnapshot"] = true
                },
                ["session"] = new Dictionary<string, object?>
                {
                    ["hasFocusedDocument"] = false,
                    ["lastListedDocumentsCount"] = 1,
                    ["hasResolvedCategory"] = true,
                    ["hasPendingClarification"] = false
                },
                ["execution"] = new Dictionary<string, object?>
                {
                    ["mode"] = "auto",
                    ["hasRouterIntent"] = true,
                    ["toolNamesCount"] = 2,
                    ["hasAdminOperation"] = false
                },
                ["persistence"] = new Dictionary<string, object?>
                {
                    ["language"] = true,
                    ["style"] = true,
                    ["mode"] = false,
                    ["focusedDocument"] = false,
                    ["resolvedCategory"] = false
                },
                ["resetPolicy"] = new Dictionary<string, object?>
                {
                    ["preservesM1Lite"] = true,
                    ["preservesPreferences"] = true,
                    ["clearsM3"] = true,
                    ["clearsM6"] = true,
                    ["resetsModeToAuto"] = true
                }
            }
        };

        var zipPath = await SupportBundleBuilder.BuildAsync(settings, runtimeSnapshot, new[] { "diagnostics/agent-runtime" });

        try
        {
            Assert.True(File.Exists(zipPath));

            using var archive = ZipFile.OpenRead(zipPath);
            var textEntry = archive.GetEntry("diagnostics/agent-memory-summary.txt");
            Assert.NotNull(textEntry);

            using var reader = new StreamReader(textEntry!.Open());
            var text = await reader.ReadToEndAsync();

            Assert.Contains("Memory and performance diagnostics:", text);
            Assert.Contains("router 11 ms", text);
            Assert.Contains("2 canonical category(ies)", text);
            Assert.Contains("3 known document(s)", text);

            var jsonEntry = archive.GetEntry("diagnostics/agent-memory-summary.json");
            Assert.NotNull(jsonEntry);

            using var jsonReader = new StreamReader(jsonEntry!.Open());
            var jsonText = await jsonReader.ReadToEndAsync();
            using var jsonDoc = JsonDocument.Parse(jsonText);
            Assert.Equal("cdc-v3-m1lite-m3-m6", jsonDoc.RootElement.GetProperty("profile").GetString());
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Support_bundle_includes_local_governance_artifacts_when_present()
    {
        var tempLocalAppData = Path.Combine(Path.GetTempPath(), "saaia-support-localappdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempLocalAppData, "SAAIA", "governance"));
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
            var path = Path.Combine(tempLocalAppData, "SAAIA", "governance", fileName);
            await File.WriteAllTextAsync(path, $"{{\"artifact\":\"{fileName}\"}}");
            await File.WriteAllTextAsync(path + ".sha256", "deadbeef");
        }
        Directory.CreateDirectory(Path.Combine(tempLocalAppData, "SAAIA", "llm", "runtime"));
        await File.WriteAllTextAsync(
            Path.Combine(tempLocalAppData, "SAAIA", "llm", "runtime", "active-runtime.json"),
            "{\"items\":[]}");
        await File.WriteAllTextAsync(
            Path.Combine(tempLocalAppData, "SAAIA", "llm", "runtime", "active-runtime.json.sha256"),
            "cafebabe");

        var settings = new AppSettings
        {
            BackendUrl = "http://127.0.0.1:9",
            Host = "127.0.0.1",
            Port = 9
        };

        var zipPath = await SupportBundleBuilder.BuildAsync(settings, null, null, tempLocalAppData);

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            Assert.NotNull(archive.GetEntry("governance/model_catalog.json"));
            Assert.NotNull(archive.GetEntry("governance/model_catalog.json.sha256"));
            Assert.NotNull(archive.GetEntry("governance/model_collections.json"));
            Assert.NotNull(archive.GetEntry("governance/model_policy.json"));
            Assert.NotNull(archive.GetEntry("governance/model_sources.json"));
            Assert.NotNull(archive.GetEntry("governance/warmup_profiles.json"));
            Assert.NotNull(archive.GetEntry("governance/hardware_probe.json"));
            Assert.NotNull(archive.GetEntry("governance/warmup_results.json"));
            Assert.NotNull(archive.GetEntry("governance/capability_state.json"));
            Assert.NotNull(archive.GetEntry("governance/last_known_good_profile.json"));
            Assert.NotNull(archive.GetEntry("governance/blacklist.json"));
            Assert.NotNull(archive.GetEntry("governance/rollback_log.json"));
            Assert.NotNull(archive.GetEntry("governance/blacklist_applied.json"));
            Assert.NotNull(archive.GetEntry("governance/acquisition_log.json"));
            Assert.NotNull(archive.GetEntry("governance/runtime_compatibility_policy.json"));
            Assert.NotNull(archive.GetEntry("governance/runtime_event_log.json"));
            Assert.NotNull(archive.GetEntry("governance/battery_policies.json"));
            Assert.NotNull(archive.GetEntry("llm/active-runtime.json"));
            Assert.NotNull(archive.GetEntry("llm/active-runtime.json.sha256"));
            Assert.NotNull(archive.GetEntry("llm/runtime_compatibility_policy.json"));
            Assert.NotNull(archive.GetEntry("llm/runtime_event_log.json.sha256"));
            Assert.NotNull(archive.GetEntry("llm/runtime_compatibility_policy.json.sha256"));
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            if (Directory.Exists(tempLocalAppData))
                Directory.Delete(tempLocalAppData, recursive: true);
        }
    }
}
