using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class LiveLocalLlmQualificationPromotionTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Promote_successful_final_validation_into_real_governance_and_settings()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_LOCAL_LLM_PROMOTION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var inputArtifactPath =
            Environment.GetEnvironmentVariable("SAAIA_LOCAL_LLM_FINAL_VALIDATION_INPUT_ARTIFACT");
        Assert.False(string.IsNullOrWhiteSpace(inputArtifactPath));
        Assert.True(
            File.Exists(inputArtifactPath),
            $"Final validation artifact not found: {inputArtifactPath}");

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifactPath!));
        var root = document.RootElement;
        var modelPath = root.GetProperty("modelPath").GetString();
        var modelId = root.GetProperty("modelId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(modelPath));
        Assert.False(string.IsNullOrWhiteSpace(modelId));
        Assert.True(File.Exists(modelPath), $"Model not found: {modelPath}");
        var finalValidation = root.GetProperty("outcome")
            .Deserialize<LocalLlmQualificationFinalValidationOutcome>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(finalValidation);
        Assert.Contains(finalValidation!.RankedCandidates, static score => score.Eligible);

        var hardware = await HardwareProbeService.CaptureAsync(
            refresh: true,
            CancellationToken.None);
        var driverVersion = TryGetString(hardware.Hardware, "gpuDriverVersion");
        var settingsBefore = AppSettings.Load();
        var eligibleRuntimeIds = finalValidation.RankedCandidates
            .Where(static score => score.Eligible)
            .Select(static score => score.Candidate.Profile.Runtime)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runtimeStatesBefore = eligibleRuntimeIds
            .Select(runtimeId => LlamaCppReleaseDownloader.TryGetActiveRuntimeState(runtimeId))
            .Where(static state => state is not null)
            .Cast<LlamaCppReleaseDownloader.ActiveRuntimeState>()
            .ToArray();

        var outcome = await LocalLlmQualificationPromotion.RunAsync(
            finalValidation,
            modelId!,
            modelPath!,
            settingsBefore,
            persistSettings: true,
            root: null,
            driverVersion: driverVersion,
            hardwareFingerprint: hardware.MachineFingerprint,
            hardwareProbe: hardware,
            runtimeQualifier: null,
            ct: CancellationToken.None);

        Assert.True(outcome.Succeeded, string.Join(", ", outcome.Reasons));
        Assert.NotNull(outcome.Winner);
        Assert.All(outcome.Profiles, static profile =>
        {
            Assert.True(
                profile.Gate.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded,
                $"{profile.Profile.ProfileId}: {string.Join(", ", profile.Gate.Reasons)}");
            Assert.True(profile.RuntimeMarkedQualified, profile.Profile.Runtime);
        });

        var registeredProfiles = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
            GovernanceArtifactStore.WarmupProfilesFile);
        var warmupResults = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
            GovernanceArtifactStore.WarmupResultsFile);
        var lastKnownGood = await RollbackManager.ReadLastKnownGoodAsync();
        var settingsAfter = AppSettings.Load();
        var runtimeStatusAfter = await LocalLlmRuntimeStatusService.EvaluateAsync(settingsAfter);
        Assert.Equal(GovernanceArtifactReadStatus.Ok, registeredProfiles.Status);
        Assert.Equal(GovernanceArtifactReadStatus.Ok, warmupResults.Status);
        Assert.Null(runtimeStatusAfter);
        Assert.Equal(outcome.Winner!.ProfileId, lastKnownGood?.ProfileId);
        Assert.Equal(outcome.Winner.ProfileId, settingsAfter.QualifiedProfile?.ProfileId);
        Assert.Equal(outcome.Winner.Runtime, RequalificationTriggerService.DetectRuntimeKey(
            settingsAfter.LlamaExePath));
        Assert.Equal(Path.GetFullPath(modelPath!), Path.GetFullPath(settingsAfter.ModelPath));
        Assert.All(outcome.Profiles, promoted =>
        {
            Assert.Contains(
                registeredProfiles.Value!.Items,
                profile => string.Equals(
                    profile.ProfileId,
                    promoted.Profile.ProfileId,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                warmupResults.Value!.Items,
                result => string.Equals(
                              result.ProfileId,
                              promoted.Profile.ProfileId,
                              StringComparison.OrdinalIgnoreCase)
                          && result.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded);
        });

        var runtimeStatesAfter = eligibleRuntimeIds
            .Select(runtimeId => LlamaCppReleaseDownloader.TryGetActiveRuntimeState(runtimeId))
            .ToArray();
        Assert.All(runtimeStatesAfter, static state =>
        {
            Assert.NotNull(state);
            Assert.Equal("qualified", state!.Status);
            Assert.NotNull(state.QualifiedAtUtc);
        });

        var outputArtifactPath =
            Environment.GetEnvironmentVariable("SAAIA_LOCAL_LLM_PROMOTION_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(outputArtifactPath))
        {
            var fullPath = Path.GetFullPath(outputArtifactPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(
                    new
                    {
                        CapturedAt = DateTimeOffset.UtcNow,
                        InputArtifact = Path.GetFullPath(inputArtifactPath!),
                        ModelId = modelId,
                        ModelPath = Path.GetFullPath(modelPath!),
                        HardwareFingerprint = hardware.MachineFingerprint,
                        DriverVersion = driverVersion,
                        Winner = outcome.Winner,
                        Promotion = outcome,
                        RuntimeStatesBefore = runtimeStatesBefore,
                        RuntimeStatesAfter = runtimeStatesAfter,
                        RuntimeStatusAfter = runtimeStatusAfter,
                        EffectiveServerArguments = LlamaCppProcessManager.BuildArgs(settingsAfter),
                        SettingsAfter = new
                        {
                            settingsAfter.UseLocalLlm,
                            settingsAfter.ManageLocalLlmProcess,
                            settingsAfter.LlamaExePath,
                            settingsAfter.ModelPath,
                            settingsAfter.ModelId,
                            settingsAfter.QualifiedProfile
                        },
                        RegisteredProfileCount = registeredProfiles.Value!.Items.Count,
                        WarmupResultCount = warmupResults.Value!.Items.Count,
                        LastKnownGood = lastKnownGood
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }
    }

    private static string? TryGetString(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement { ValueKind: JsonValueKind.String } json)
            return json.GetString();

        return Convert.ToString(value);
    }
}
