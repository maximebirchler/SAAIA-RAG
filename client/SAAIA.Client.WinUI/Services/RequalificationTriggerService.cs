using System;

namespace SAAIA.Client.WinUI.Services;

internal sealed record RequalificationDecision(
    bool Required,
    string Reason);

internal static class RequalificationTriggerService
{
    public static RequalificationDecision EvaluateProfileDrift(AppSettings settings)
    {
        if (settings.QualifiedProfile is null)
            return new RequalificationDecision(false, "qualified_profile_missing");

        var currentRuntime = DetectRuntimeKey(settings.LlamaExePath);
        if (!string.Equals(currentRuntime, settings.QualifiedProfile.Runtime, StringComparison.OrdinalIgnoreCase))
        {
            return new RequalificationDecision(
                true,
                $"runtime_changed:{settings.QualifiedProfile.Runtime}->{currentRuntime}");
        }

        var currentModelId = ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(settings.ModelPath is null ? null : System.IO.Path.GetFileName(settings.ModelPath))
            ?? settings.ModelId;

        if (!string.Equals(currentModelId, settings.QualifiedProfile.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return new RequalificationDecision(
                true,
                $"model_changed:{settings.QualifiedProfile.ModelId}->{currentModelId}");
        }

        return new RequalificationDecision(false, "profile_unchanged");
    }

    internal static string DetectRuntimeKey(string? exePath)
    {
        var path = (exePath ?? string.Empty).Trim();
        if (path.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || path.Contains("cu12", StringComparison.OrdinalIgnoreCase)
            || path.Contains("cublas", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp-cuda";
        }

        if (path.Contains("vulkan", StringComparison.OrdinalIgnoreCase))
            return "llama.cpp-vulkan";

        return "llama.cpp-cpu";
    }
}
