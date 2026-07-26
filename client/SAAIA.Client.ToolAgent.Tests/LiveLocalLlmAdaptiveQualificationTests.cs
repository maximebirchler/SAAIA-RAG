using System.Collections.Concurrent;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class LiveLocalLlmAdaptiveQualificationTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Requalify_all_applicable_backends_and_promote_the_measured_winner()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_LOCAL_LLM_ADAPTIVE_QUALIFICATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var settings = AppSettings.Load();
        var preflight = await LocalLlmAdaptiveQualificationService.InspectAsync(
            settings,
            force: false,
            root: null,
            runtimeStateResolver: null,
            ct: CancellationToken.None);
        var provisioning = await LocalLlmRuntimeProvisioningService.ProvisionApplicableAsync(
            settings,
            preflight.Hardware.Gpus,
            progress: null,
            installers: null,
            runtimeTracker: null,
            ct: CancellationToken.None);
        Assert.True(provisioning.Succeeded, string.Join(", ", provisioning.Reasons));

        var progressItems = new ConcurrentQueue<LocalLlmAdaptiveQualificationProgress>();
        var qualification = await LocalLlmAdaptiveQualificationService.QualifyIfRequiredAsync(
            settings,
            LocalLlmAdaptiveQualificationOptions.CreateInitial(Environment.ProcessorCount),
            force: true,
            root: null,
            progress: new Progress<LocalLlmAdaptiveQualificationProgress>(progressItems.Enqueue),
            runtimeStateResolver: null,
            ct: CancellationToken.None);

        var artifactPath = Environment.GetEnvironmentVariable(
            "SAAIA_LOCAL_LLM_ADAPTIVE_QUALIFICATION_ARTIFACT");
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
                        Preflight = preflight,
                        Provisioning = provisioning,
                        Progress = progressItems.ToArray(),
                        Qualification = qualification,
                        EffectiveServerArguments = qualification.Promotion?.Succeeded == true
                            ? LlamaCppProcessManager.BuildArgs(AppSettings.Load())
                            : null
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }

        Assert.True(qualification.Succeeded, string.Join(", ", qualification.Reasons));
        Assert.False(qualification.Skipped);
        Assert.NotEmpty(qualification.Candidates);
        Assert.NotNull(qualification.Screening);
        Assert.NotNull(qualification.Refinement);
        Assert.NotNull(qualification.FinalValidation);
        Assert.True(qualification.Promotion?.Succeeded);
        Assert.NotNull(qualification.Promotion?.Winner);
        Assert.Contains(
            LocalLlmAdaptiveQualificationService.QualificationContractRef,
            qualification.Promotion!.Profiles[0].Profile.HardGateRefs);
    }

    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Current_qualified_machine_reuses_measured_profile_without_benchmarking()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_LOCAL_LLM_ADAPTIVE_CACHE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var settings = AppSettings.Load();
        var preflight = await LocalLlmAdaptiveQualificationService.InspectAsync(
            settings,
            force: false,
            root: null,
            runtimeStateResolver: null,
            ct: CancellationToken.None);

        Assert.False(
            preflight.Decision.Required,
            string.Join(", ", preflight.Decision.Reasons));
        Assert.True(preflight.Decision.CanRun);

        var outcome = await LocalLlmAdaptiveQualificationService.QualifyIfRequiredAsync(
            settings,
            LocalLlmAdaptiveQualificationOptions.CreateInitial(Environment.ProcessorCount),
            force: false,
            root: null,
            progress: null,
            runtimeStateResolver: null,
            ct: CancellationToken.None);

        Assert.True(outcome.Succeeded, string.Join(", ", outcome.Reasons));
        Assert.True(outcome.Skipped);
        Assert.Empty(outcome.Candidates);
        Assert.Null(outcome.Screening);
        Assert.Null(outcome.Refinement);
        Assert.Null(outcome.FinalValidation);
        Assert.Null(outcome.Promotion);
        Assert.Contains("qualification_cache_valid", outcome.Reasons);

        var artifactPath = Environment.GetEnvironmentVariable(
            "SAAIA_LOCAL_LLM_ADAPTIVE_CACHE_ARTIFACT");
        if (string.IsNullOrWhiteSpace(artifactPath))
            return;

        var fullPath = Path.GetFullPath(artifactPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(
                new
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    Preflight = preflight,
                    Outcome = outcome,
                    Settings = new
                    {
                        settings.UseLocalLlm,
                        settings.ManageLocalLlmProcess,
                        settings.LlamaExePath,
                        settings.ModelPath,
                        settings.ModelId,
                        settings.QualifiedProfile
                    }
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                }));
    }
}
