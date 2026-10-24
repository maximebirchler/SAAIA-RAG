namespace SAAIA.Client.Core.Execution;

public enum AgentToolMenu { None, DedicatedAudit, SemanticYield, CompactFollowUp, CandidateCollection, WorkspaceSelection, EvidenceSelection, Interactive }
public sealed record AgentToolMenuInput(bool DedicatedCandidateAudit, bool SemanticYieldTerminal,
    bool CompactFollowUp, bool CandidateCollectionOpen, bool SemanticSelectionDecision,
    bool WorkspaceWriterDecision, bool DecisionOnly, bool Finalization, bool AllowAdaptiveFlatSelection,
    bool HasCitableEvidence, int RequiredEvidenceCount, int ObservedCitableSources,
    string SelectionToolName, string EvidenceWorkspaceToolName);
public sealed record AgentToolMenuResult<TTool>(AgentToolMenu Menu, IReadOnlyList<TTool> Tools);

/// <summary>Owns the exposed tool menu for a turn, independently from prompt wording or model providers.</summary>
public static class AgentToolMenuPlanner
{
    public static AgentToolMenuResult<TTool> Select<TTool>(AgentToolMenuInput input,
        IReadOnlyList<TTool> turnTools, Func<TTool, string> name,
        Func<AgentToolMenu, IReadOnlyList<TTool>> buildMenu,
        Func<TTool> buildProvisionalSelection, Func<TTool> buildClarification)
    {
        var menu = input.DedicatedCandidateAudit ? AgentToolMenu.DedicatedAudit
            : input.SemanticYieldTerminal ? AgentToolMenu.SemanticYield
            : input.CompactFollowUp ? AgentToolMenu.CompactFollowUp
            : input.CandidateCollectionOpen ? AgentToolMenu.CandidateCollection
            : input.SemanticSelectionDecision ? input.WorkspaceWriterDecision ? AgentToolMenu.WorkspaceSelection : AgentToolMenu.EvidenceSelection
            : input.DecisionOnly || input.Finalization ? AgentToolMenu.None : AgentToolMenu.Interactive;
        var selected = menu is AgentToolMenu.None or AgentToolMenu.DedicatedAudit ? Array.Empty<TTool>()
            : menu == AgentToolMenu.Interactive ? turnTools.Where(tool =>
                (!string.Equals(name(tool), input.SelectionToolName, StringComparison.OrdinalIgnoreCase)
                    || input.RequiredEvidenceCount > 0 && input.ObservedCitableSources >= input.RequiredEvidenceCount)
                && (input.HasCitableEvidence || !string.Equals(name(tool), input.EvidenceWorkspaceToolName, StringComparison.OrdinalIgnoreCase))).ToArray()
            : buildMenu(menu).ToArray();
        if (input.AllowAdaptiveFlatSelection && !input.CompactFollowUp && !input.SemanticYieldTerminal)
            selected = selected.Append(buildProvisionalSelection()).Append(buildClarification()).ToArray();
        return new(menu, Array.AsReadOnly(selected));
    }
}
