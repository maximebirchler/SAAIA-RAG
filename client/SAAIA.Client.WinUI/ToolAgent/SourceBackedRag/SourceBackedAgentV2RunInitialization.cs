namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record RunInitialization(
        SourceBackedIntake Intake,
        int TraceSequence,
        SemanticPlanPreparation SemanticPlanPreparation,
        SourceBackedAgentCompletion PlanningCompletion,
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
        SemanticSelectionLayout? CanonicalWorkspaceSelectionLayout,
        SemanticCandidateStrategyOutcome SemanticCandidateStrategyOutcome,
        SemanticCandidateDefinitionOutcome SemanticCandidateDefinitionOutcome,
        string SemanticCandidateObjectType,
        string SemanticCandidateEligibilityRule,
        CandidateProjectedEvidenceContractOutcome CandidateProjectedEvidenceContract,
        List<SourceBackedAgentMessage> Messages,
        LlmEvidenceWorkspace EvidenceWorkspace,
        IReadOnlyList<SourceBackedAgentToolDefinition> Tools,
        NamedDocumentInitialActionPreparation NamedDocumentActionPreparation,
        NamedDocumentObservationDecisionOutcome NamedDocumentObservationDecision,
        SourceBackedAgentCompletion? NamedDocumentTerminalCompletion,
        InitialResearchActionOutcome InitialResearchActionOutcome,
        SourceBackedAgentToolCall[] RouterInitialActions,
        SourceBackedAgentToolCall? PlannedFirstAction,
        string? InitialActionDecisionSource,
        string AtomicEvidenceMode,
        bool UseGlobalStructuredCandidateSelection,
        bool UseFlatContentClaimAdequacy,
        bool CandidateAuditEnabled,
        bool CandidateCollectionEnabled,
        int CandidatePoolTargetCount);

    private async Task<RunInitialization> PrepareRunInitializationAsync(
        SourceBackedIntake intake,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        int initialTraceSequence,
        CancellationToken ct)
    {
        var traceSequence = initialTraceSequence;
        intake = ApplyNamedDocumentResolution(
            intake,
            await ResolveRequestedDocumentAsync(intake, ct).ConfigureAwait(false),
            traces,
            traceId,
            ref traceSequence);
        var runPreparation = await PrepareRunPlanningAsync(intake, ct)
            .ConfigureAwait(false);
        intake = runPreparation.Intake;
        var actionPreparation = PrepareNamedDocumentInitialActions(intake);
        intake = actionPreparation.Intake;
        var planPreparation = runPreparation.SemanticPlanPreparation;
        var semanticPlan = runPreparation.SemanticPlan;
        var layout = runPreparation.SemanticLayoutDimensions;
        var candidateStrategy = runPreparation.SemanticCandidateStrategyOutcome;
        var candidateDefinition = runPreparation.SemanticCandidateDefinitionOutcome;
        var candidateObjectType = candidateDefinition.ProtocolValid
                                  && candidateDefinition.Attempted
            ? candidateDefinition.CandidateObjectType
            : candidateStrategy.CandidateObjectType;
        var candidateEligibilityRule = candidateDefinition.ProtocolValid
                                       && candidateDefinition.Attempted
            ? BuildCandidateDefinitionAuditRule(candidateDefinition)
            : candidateStrategy.CandidateEligibilityRule;
        var canonicalLayout = BuildCanonicalWorkspaceSelectionLayout(
            layout,
            runPreparation.SemanticRowHeader,
            runPreparation.SemanticColumnLabels,
            runPreparation.SemanticRowLabels);
        var messages = new List<SourceBackedAgentMessage>
        {
            SourceBackedAgentMessage.System(BuildSystemPrompt()),
            SourceBackedAgentMessage.User(BuildUserContext(
                intake,
                _options.MaximumContextTokens)),
            SourceBackedAgentMessage.User(BuildPlanExecutionMessage(semanticPlan))
        };
        var evidenceWorkspace = new LlmEvidenceWorkspace();
        var compactSingleSelection =
            runPreparation.RequireEvidenceSelection
            && runPreparation.RequiredAtomicEvidenceCount == 1
            && layout is null;
        var tools = BuildAvailableEvidenceTools(
            intake,
            semanticPlan,
            compactSingleSelection,
            runPreparation.RequireEvidenceSelection,
            runPreparation.EnableWorkspaceWriterHandoff,
            runPreparation.RequiredAtomicEvidenceCount,
            layout,
            runPreparation.SemanticColumnLabels,
            runPreparation.SemanticRowHeader,
            runPreparation.SemanticRowLabels);
        var observationDecision =
            await PrepareNamedDocumentObservationDecisionAsync(
                    intake,
                    actionPreparation,
                    tools,
                    ct)
                .ConfigureAwait(false);
        intake = observationDecision.Intake;
        var suppressInitialResearch =
            actionPreparation.SuppressPlannedFirstAction
            && intake.InitialToolCalls is not { Count: > 0 };
        var initialResearch = await PrepareMissingInitialResearchActionAsync(
                intake,
                planPreparation,
                semanticPlan,
                layout,
                candidateDefinition,
                candidateStrategy,
                runPreparation.CandidateProjectedEvidenceContract,
                runPreparation.SemanticColumnRoles,
                tools,
                suppressInitialResearch,
                ct)
            .ConfigureAwait(false);
        if (intake.InitialToolCalls is not { Count: > 0 }
            && initialResearch.Actions.Count > 0)
        {
            intake = intake with { InitialToolCalls = initialResearch.Actions };
        }
        candidateStrategy = PreserveLlmChosenCandidateScope(
            candidateStrategy,
            intake.InitialToolCalls);
        var (routerInitialActions, plannedFirstAction) = PrepareInitialActions(
            intake,
            planPreparation.InitialToolCall,
            semanticPlan,
            tools,
            suppressInitialResearch);
        var initialActionDecisionSource = intake.InitialToolCalls?
            .FirstOrDefault()?.DecisionSource
            ?? planPreparation.InitialToolCall?.DecisionSource;
        var atomicEvidenceMode = ReadAtomicEvidenceMode(semanticPlan);
        var useGlobalStructuredCandidateSelection =
            _options.SeparateActionAndWriter
            && layout is not null
            && runPreparation.SemanticRowLabels.Count > 0
            && runPreparation.SemanticColumnLabels.Length > 0
            && runPreparation.RequiredAtomicEvidenceCount.GetValueOrDefault()
            > _options.MaximumFlatStructuredSelectionItems;
        var useFlatContentClaimAdequacy =
            _options.SeparateActionAndWriter
            && layout is null
            && runPreparation.RequiredAtomicEvidenceCount is > 1
            && string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.OrdinalIgnoreCase);
        var candidateAuditEnabled =
            _options.SemanticCandidateAuditEnabled
            && runPreparation.RequiredAtomicEvidenceCount is > 1
            && !useFlatContentClaimAdequacy;
        var candidateCollectionEnabled = candidateAuditEnabled
                                         || useGlobalStructuredCandidateSelection
                                         || useFlatContentClaimAdequacy;

        return new RunInitialization(
            intake,
            traceSequence,
            planPreparation,
            planPreparation.Completion,
            semanticPlan,
            runPreparation.RequiredAtomicEvidenceCount,
            runPreparation.RequireEvidenceSelection,
            layout,
            runPreparation.EnableWorkspaceWriterHandoff,
            runPreparation.SemanticColumnRoles,
            runPreparation.SemanticColumnRoleOutcome,
            runPreparation.SemanticColumnLabels,
            runPreparation.SemanticRowHeader,
            runPreparation.SemanticRowLabels,
            canonicalLayout,
            candidateStrategy,
            candidateDefinition,
            candidateObjectType,
            candidateEligibilityRule,
            runPreparation.CandidateProjectedEvidenceContract,
            messages,
            evidenceWorkspace,
            tools,
            actionPreparation,
            observationDecision,
            observationDecision.TerminalCompletion,
            initialResearch,
            routerInitialActions,
            plannedFirstAction,
            initialActionDecisionSource,
            atomicEvidenceMode,
            useGlobalStructuredCandidateSelection,
            useFlatContentClaimAdequacy,
            candidateAuditEnabled,
            candidateCollectionEnabled,
            runPreparation.RequiredAtomicEvidenceCount.GetValueOrDefault());
    }
}
