namespace SAAIA.Client.WinUI.Services;

internal static class ClientRoutingTracePolicy
{
    internal static bool Allows(string key, object? value)
    {
        if (value is not string text) return false;
        return key switch
        {
            "decision" => text is "advanced_analysis_required" or "advanced_provider_execution" or "local_eligible",
            "plan_kind" => text is "comparison" or "structured_layout" or "multi_item" or "single_item" or "user_instance_context" or "user_reference_context",
            "provider_mode" => text is "Local" or "OpenAiDev" or "RunPodBench" or "legacy_local",
            _ => false
        };
    }
}
