namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static void AnnotateRejectedBudget(
        BackendDiagnostics.Operation probe,
        AdvancedAnalysisProviderException error)
    {
        if (error.ErrorCode is not ("advanced_external_budget_job_call_limit"
            or "advanced_external_budget_job_cost_limit"
            or "advanced_external_budget_campaign_hard_limit"))
            return;

        // Never enumerate exception Data: only these content-free numeric scalars
        // and the exact false marker may enter the provider journal.
        string[] fields =
        [
            "budget_attempted_input_token_bound", "budget_attempted_maximum_output_tokens",
            "budget_attempted_reserve_usd", "budget_job_calls", "budget_job_call_limit",
            "budget_job_charged_usd", "budget_job_reserved_usd", "budget_job_cap_usd",
            "budget_job_remaining_usd", "budget_campaign_charged_usd", "budget_campaign_reserved_usd",
            "budget_campaign_cap_usd", "budget_campaign_remaining_usd"
        ];
        foreach (var field in fields)
        {
            if (error.Data[field] is int count) probe.Set(field, count);
            else if (error.Data[field] is decimal amount) probe.Set(field, amount);
        }
        if (error.Data["budget_reservation_created"] is false)
            probe.Set("budget_reservation_created", false);
    }
}
