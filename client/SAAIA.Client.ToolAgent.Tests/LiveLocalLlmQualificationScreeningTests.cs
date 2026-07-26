using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveLocalLlmQualificationScreeningTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Screen_installed_runtime_topologies_with_the_governed_local_model()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_LOCAL_LLM_SCREENING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var modelPath = Environment.GetEnvironmentVariable("SAAIA_LIVE_MODEL_PATH");
        Assert.False(string.IsNullOrWhiteSpace(modelPath));
        Assert.True(File.Exists(modelPath), $"Model not found: {modelPath}");
        var modelId = Environment.GetEnvironmentVariable("SAAIA_LIVE_MODEL_ID")
                      ?? Path.GetFileNameWithoutExtension(modelPath);
        var runtimeProbes = await LocalLlmRuntimeCapabilityProbe.CaptureInstalledAsync(
            ct: CancellationToken.None);
        Assert.Contains(runtimeProbes, static probe => probe.Succeeded);

        var candidates = LocalLlmQualificationCandidateFactory.Build(
            modelId,
            modelPath,
            runtimeProbes,
            Environment.ProcessorCount,
            maxCandidates: 64);
        Assert.NotEmpty(candidates);

        var hardware = await HardwareProbeService.CaptureAsync(
            refresh: true,
            CancellationToken.None);
        var availableSystemRamMiB = Convert.ToInt32(hardware.Hardware["availableRamMiB"]);
        var scenario = new LocalLlmQualificationBenchmarkScenario(
            "live-screen-p128-n32",
            PromptTokens: 128,
            GenerationTokens: 32);
        var outcome = await LocalLlmQualificationScreening.RunAsync(
            candidates,
            runtimeProbes,
            modelPath!,
            scenario,
            availableSystemRamMiB,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096,
            ct: CancellationToken.None);

        Assert.NotEmpty(outcome.Entries);
        Assert.Contains(outcome.RankedRepresentatives, static score => score.Eligible);

        var artifactPath = Environment.GetEnvironmentVariable("SAAIA_LOCAL_LLM_SCREENING_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            var fullPath = Path.GetFullPath(artifactPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(
                    new
                    {
                        CapturedAt = DateTimeOffset.UtcNow,
                        ModelId = modelId,
                        ModelPath = Path.GetFullPath(modelPath!),
                        AvailableSystemRamMiB = availableSystemRamMiB,
                        RuntimeProbes = runtimeProbes,
                        CandidateCount = candidates.Count,
                        outcome.Ladders,
                        outcome.Entries,
                        outcome.RankedRepresentatives
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }
    }
}
