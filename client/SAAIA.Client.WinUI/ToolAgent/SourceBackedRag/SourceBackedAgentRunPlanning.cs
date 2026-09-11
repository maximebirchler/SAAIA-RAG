namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SourceBackedRunPlanning(
        SourceBackedIntake Intake,
        SemanticPlanPreparation SemanticPlanPreparation,
        string SemanticPlan,
        int? RequiredAtomicEvidenceCount,
        bool RequireEvidenceSelection,
        SemanticLayoutDimensions? SemanticLayoutDimensions,
        bool EnableWorkspaceWriterHandoff,
        IReadOnlyDictionary<string, string> SemanticColumnRoles,
        SemanticColumnRoleOutcome? SemanticColumnRoleOutcome,
        string[] SemanticColumnLabels,
        string SemanticRowHeader,
        IReadOnlyList<string> SemanticRowLabels,
        SemanticCandidateDefinitionOutcome SemanticCandidateDefinitionOutcome,
        SemanticCandidateStrategyOutcome SemanticCandidateStrategyOutcome,
        CandidateProjectedEvidenceContractOutcome CandidateProjectedEvidenceContract);

    private async Task<SourceBackedRunPlanning> PrepareRunPlanningAsync(
        SourceBackedIntake intake,
        CancellationToken ct)
    {
        var semanticPlanPreparation = await PrepareSemanticPlanAsync(intake, ct)
            .ConfigureAwait(false);
        if (intake.InitialToolCalls is not { Count: > 0 }
            && semanticPlanPreparation.InitialToolCall is { } plannedInitialCall)
        {
            intake = intake with
            {
                InitialToolCalls = new[] { plannedInitialCall }
            };
        }

        var semanticPlan = semanticPlanPreparation.Plan;
        var requiredAtomicEvidenceCount = ReadRequiredAtomicEvidenceCount(semanticPlan);
        var requireEvidenceSelection =
            _options.RequireEvidenceSelectionBeforeWriter
            && _options.SeparateActionAndWriter
            && requiredAtomicEvidenceCount is > 0;
        var semanticLayoutDimensions = requiredAtomicEvidenceCount is > 1
            ? ReadSemanticLayoutDimensions(
                semanticPlan,
                requiredAtomicEvidenceCount.Value)
            : null;
        var enableWorkspaceWriterHandoff = CanUseWorkspaceWriterHandoff(
            requireEvidenceSelection,
            semanticLayoutDimensions,
            requiredAtomicEvidenceCount);
        var hasRouterInitialObservation = intake.InitialToolCalls?.Any(call =>
            call.DecisionSource.StartsWith(
                "llm_router",
                StringComparison.OrdinalIgnoreCase)) == true;
        var semanticColumnRolePreparation = await PrepareSemanticColumnRolesAsync(
                intake,
                semanticPlan,
                semanticLayoutDimensions,
                semanticPlanPreparation.ColumnRoles,
                ct,
                allowPreObservationLlmReview: !hasRouterInitialObservation)
            .ConfigureAwait(false);
        intake = semanticColumnRolePreparation.Intake;
        var semanticColumnRoles = semanticColumnRolePreparation.Roles;
        intake = ApplySemanticLayoutContractToIntake(
            intake,
            semanticPlanPreparation.RowHeader,
            semanticPlanPreparation.RowLabels,
            semanticColumnRoles.Keys.ToArray());
        var semanticCandidateDefinitionOutcome =
            _options.SemanticCandidateDefinitionEnabled
            && !hasRouterInitialObservation
            && !_options.SemanticCandidateStrategyEnabled
            && requireEvidenceSelection
            && semanticPlanPreparation.RowLabels.Count > 0
            && semanticColumnRoles.Count > 0
                ? await ReviewSemanticCandidateDefinitionAsync(
                        intake,
                        semanticPlan,
                        semanticPlanPreparation.RowHeader,
                        semanticPlanPreparation.RowLabels,
                        semanticColumnRoles,
                        ct)
                    .ConfigureAwait(false)
                : CreateUnusedSemanticCandidateDefinitionOutcome();
        var semanticCandidateStrategyOutcome =
            _options.SemanticCandidateStrategyEnabled
            && !hasRouterInitialObservation
            && semanticPlanPreparation.RowLabels.Count > 0
            && semanticPlanPreparation.ColumnRoles.Count > 0
                ? await ReviewSemanticCandidateStrategyAsync(
                        intake,
                        semanticPlan,
                        semanticPlanPreparation.RowHeader,
                        semanticPlanPreparation.RowLabels,
                        semanticPlanPreparation.ColumnRoles.Keys.ToArray(),
                        SourceBackedRetrievalScope.GetAvailableCategoryPaths(intake),
                        requiredAtomicEvidenceCount.GetValueOrDefault(1),
                        ct)
                    .ConfigureAwait(false)
                : CreateUnusedSemanticCandidateStrategyOutcome();
        semanticCandidateStrategyOutcome = PreserveRouterChosenCandidateScope(
            semanticCandidateStrategyOutcome,
            intake.InitialSemanticMission,
            intake.CatalogHints);
        var candidateProjectedEvidenceContract =
            ApplyCandidateProjectedEvidenceContract(
            semanticPlan,
            semanticPlanPreparation,
            semanticCandidateStrategyOutcome,
            requiredAtomicEvidenceCount);

        return new SourceBackedRunPlanning(
            intake,
            semanticPlanPreparation,
            candidateProjectedEvidenceContract.SemanticPlan,
            requiredAtomicEvidenceCount,
            requireEvidenceSelection,
            semanticLayoutDimensions,
            enableWorkspaceWriterHandoff,
            semanticColumnRoles,
            semanticColumnRolePreparation.Outcome,
            semanticColumnRoles.Keys.ToArray(),
            semanticPlanPreparation.RowHeader,
            semanticPlanPreparation.RowLabels,
            semanticCandidateDefinitionOutcome,
            semanticCandidateStrategyOutcome,
            candidateProjectedEvidenceContract);
    }
}
