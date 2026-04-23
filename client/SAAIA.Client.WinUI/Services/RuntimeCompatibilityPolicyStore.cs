using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

internal sealed record RuntimeCompatibilityPolicyArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<RuntimeCompatibilityItem> Runtimes,
    IReadOnlyList<RuntimeModelRule> MinModelRules,
    IReadOnlyList<RuntimeCompatibilityOverride> KnownOverrides);

internal sealed record RuntimeCompatibilityItem(
    string RuntimeId,
    string Build,
    string Backend,
    IReadOnlyList<string> SupportsArchitectures,
    string SupportTier);

internal sealed record RuntimeModelRule(
    string ModelFamily,
    string RuntimeId,
    string MinBuild,
    string Reason);

internal sealed record RuntimeCompatibilityOverride(
    string ModelFamily,
    string RuntimeId,
    string? GpuArchitecture,
    bool? ForceFlashAttn,
    string Reason);

internal sealed record RuntimeCompatibilityDecision(
    bool Compatible,
    bool RequiresUpgrade,
    string Reason,
    string? CurrentBuild,
    string? RequiredBuild);

internal static class RuntimeCompatibilityPolicyStore
{
    public static RuntimeCompatibilityPolicyArtifact CreateDefaultPolicy() => new(
        GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
        "v3.1",
        Runtimes: new[]
        {
            new RuntimeCompatibilityItem(
                RuntimeId: "llama.cpp-cuda",
                Build: "b8149",
                Backend: "cuda",
                SupportsArchitectures: new[] { "llama", "mistral", "qwen2" },
                SupportTier: "legacy-current"),
            new RuntimeCompatibilityItem(
                RuntimeId: "llama.cpp-cuda",
                Build: "b8901",
                Backend: "cuda",
                SupportsArchitectures: new[] { "llama", "mistral", "qwen2", "qwen3", "gemma4" },
                SupportTier: "gemma4-compatible"),
            new RuntimeCompatibilityItem(
                RuntimeId: "llama.cpp-cpu",
                Build: "b8901",
                Backend: "cpu",
                SupportsArchitectures: new[] { "llama", "mistral", "qwen2", "qwen3", "gemma4" },
                SupportTier: "gemma4-compatible")
        },
        MinModelRules: new[]
        {
            new RuntimeModelRule(
                ModelFamily: "gemma4",
                RuntimeId: "llama.cpp-cuda",
                MinBuild: "b8901",
                Reason: "Gemma 4 GGUF uses general.architecture=gemma4, unsupported by the b8149 runtime."),
            new RuntimeModelRule(
                ModelFamily: "gemma4",
                RuntimeId: "llama.cpp-cpu",
                MinBuild: "b8901",
                Reason: "Gemma 4 GGUF uses general.architecture=gemma4, unsupported by the b8149 runtime.")
        },
        KnownOverrides: new[]
        {
            new RuntimeCompatibilityOverride(
                ModelFamily: "gemma4",
                RuntimeId: "llama.cpp-cuda",
                GpuArchitecture: "pascal",
                ForceFlashAttn: false,
                Reason: "Local b8901 bench on Quadro P520/Pascal showed flash-attn on crashes before warmup.")
        });

    public static RuntimeCompatibilityDecision Evaluate(
        string? runtimeId,
        string? runtimeBuild,
        ModelCatalogItem? model,
        RuntimeCompatibilityPolicyArtifact? policy = null)
    {
        if (model is null)
            return new RuntimeCompatibilityDecision(true, false, "model_unknown", runtimeBuild, null);

        if (string.IsNullOrWhiteSpace(runtimeId))
            return new RuntimeCompatibilityDecision(false, false, "runtime_unknown", runtimeBuild, null);

        policy ??= CreateDefaultPolicy();
        var family = NormalizeModelFamily(model);
        var architecture = model.Gguf.Architecture;
        var build = NormalizeBuild(runtimeBuild);

        var rule = policy.MinModelRules.FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase)
            && MatchesFamily(family, item.ModelFamily));

        if (rule is null)
            return new RuntimeCompatibilityDecision(true, false, "compatible_no_min_rule", runtimeBuild, null);

        if (string.IsNullOrWhiteSpace(build))
        {
            return new RuntimeCompatibilityDecision(
                false,
                true,
                $"runtime_build_unknown_requires:{rule.MinBuild}",
                runtimeBuild,
                rule.MinBuild);
        }

        if (!BuildAtLeast(build, rule.MinBuild))
        {
            return new RuntimeCompatibilityDecision(
                false,
                true,
                $"runtime_build_too_old:{build}<{rule.MinBuild}",
                runtimeBuild,
                rule.MinBuild);
        }

        var runtime = policy.Runtimes
            .Where(item => string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(build) || BuildAtLeast(build, item.Build))
            .OrderByDescending(item => ParseBuildNumber(item.Build))
            .FirstOrDefault();

        if (runtime is not null
            && !runtime.SupportsArchitectures.Any(item => string.Equals(item, architecture, StringComparison.OrdinalIgnoreCase)))
        {
            return new RuntimeCompatibilityDecision(
                false,
                false,
                $"architecture_not_supported:{architecture}",
                runtimeBuild,
                null);
        }

        return new RuntimeCompatibilityDecision(true, false, "compatible", runtimeBuild, rule.MinBuild);
    }

    public static bool? GetForcedFlashAttn(
        string? runtimeId,
        ModelCatalogItem? model,
        GpuInfo? gpu,
        RuntimeCompatibilityPolicyArtifact? policy = null)
    {
        if (model is null || string.IsNullOrWhiteSpace(runtimeId))
            return null;

        policy ??= CreateDefaultPolicy();
        var family = NormalizeModelFamily(model);
        var gpuArchitecture = DetectGpuArchitecture(gpu);
        var match = policy.KnownOverrides.FirstOrDefault(item =>
            string.Equals(item.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase)
            && MatchesFamily(family, item.ModelFamily)
            && (string.IsNullOrWhiteSpace(item.GpuArchitecture)
                || string.Equals(item.GpuArchitecture, gpuArchitecture, StringComparison.OrdinalIgnoreCase)));

        return match?.ForceFlashAttn;
    }

    public static string? ReadRuntimeBuild(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;

        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir))
                return null;

            var tagPath = Path.Combine(dir, "runtime.tag");
            return File.Exists(tagPath)
                ? File.ReadAllText(tagPath).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string DetectGpuArchitecture(GpuInfo? gpu)
    {
        var name = (gpu?.Name ?? string.Empty).ToLowerInvariant();
        if (name.Contains("p520", StringComparison.Ordinal)
            || name.Contains("p500", StringComparison.Ordinal)
            || name.Contains("p600", StringComparison.Ordinal)
            || name.Contains("p1000", StringComparison.Ordinal)
            || name.Contains("pascal", StringComparison.Ordinal))
        {
            return "pascal";
        }

        return "unknown";
    }

    private static string NormalizeModelFamily(ModelCatalogItem model)
    {
        if (!string.IsNullOrWhiteSpace(model.Gguf.Architecture))
            return model.Gguf.Architecture.Trim().ToLowerInvariant();

        return (model.Family ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static bool MatchesFamily(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
           || actual.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBuild(string? build)
        => (build ?? string.Empty).Trim().ToLowerInvariant();

    private static bool BuildAtLeast(string actual, string required)
        => ParseBuildNumber(actual) >= ParseBuildNumber(required);

    private static int ParseBuildNumber(string? build)
    {
        var value = (build ?? string.Empty).Trim();
        if (value.StartsWith("b", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }
}
