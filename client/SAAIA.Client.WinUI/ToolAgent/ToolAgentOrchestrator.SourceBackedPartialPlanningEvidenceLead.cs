namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record PartialPlanningEvidenceLead(
        string Title,
        string Guidance,
        string SourceLabel,
        int Page,
        bool IsPlanningFrame,
        bool IsConcreteOption);
}
