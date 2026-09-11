using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed record SourceBackedBudgetHandoffContract(
    string Intent,
    string ReasonCode,
    string Answer,
    bool HasVisibleSources)
{
    internal const string AdvancedAnalysisIntent =
        "advanced_analysis_required";
    internal const string LocalBudgetExhaustedReason =
        "local_model_budget_exhausted";

    internal static SourceBackedBudgetHandoffContract Create(
        string? language)
        => new(
            AdvancedAnalysisIntent,
            LocalBudgetExhaustedReason,
            DeterministicAgentText
                .AdvancedAnalysisRequiredAfterLocalBudget(language),
            HasVisibleSources: false);
}
