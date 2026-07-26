using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveLocalLlmQualificationFinalValidationTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Validate_refined_finalists_with_rotated_server_and_structured_rounds()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_LOCAL_LLM_FINAL_VALIDATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var refinementArtifactPath =
            Environment.GetEnvironmentVariable("SAAIA_LOCAL_LLM_REFINEMENT_INPUT_ARTIFACT");
        Assert.False(string.IsNullOrWhiteSpace(refinementArtifactPath));
        Assert.True(
            File.Exists(refinementArtifactPath),
            $"Refinement artifact not found: {refinementArtifactPath}");

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(refinementArtifactPath!));
        var root = document.RootElement;
        var modelPath = root.GetProperty("modelPath").GetString();
        var modelId = root.GetProperty("modelId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(modelPath));
        Assert.False(string.IsNullOrWhiteSpace(modelId));
        Assert.True(File.Exists(modelPath), $"Model not found: {modelPath}");
        var refinement = root.GetProperty("refinement")
            .Deserialize<LocalLlmQualificationRefinementOutcome>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(refinement);

        var outcome = await LocalLlmQualificationFinalValidation.RunAsync(
            refinement!,
            modelPath!,
            modelId!,
            LocalLlmQualificationFinalValidationOptions.Default,
            ct: CancellationToken.None);

        var artifactPath = Environment.GetEnvironmentVariable(
            "SAAIA_LOCAL_LLM_FINAL_VALIDATION_ARTIFACT");
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
                        RefinementArtifact = Path.GetFullPath(refinementArtifactPath!),
                        Options = LocalLlmQualificationFinalValidationOptions.Default,
                        Outcome = outcome
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }

        Assert.Equal(
            LocalLlmQualificationFinalValidationOptions.Default.MaxFinalists,
            outcome.Finalists.Count);
        Assert.Equal(
            outcome.Finalists.Count
            * LocalLlmQualificationFinalValidationOptions.Default.RoundCount,
            outcome.Rounds.Count);
        Assert.Contains(outcome.RankedCandidates, static score => score.Eligible);
    }
}
