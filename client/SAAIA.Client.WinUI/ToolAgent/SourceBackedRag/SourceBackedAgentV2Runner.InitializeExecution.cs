using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task InitializeExecutionAsync(RunExecutionState runState, CancellationToken ct)
    {
        runState.traceSequence = 0;
        runState.traces = new List<SourceBackedTraceEvent>();
        var initialization = await PrepareRunInitializationAsync(
                runState.intake, runState.traces, runState.traceId, runState.traceSequence, ct)
            .ConfigureAwait(false);
        runState.intake = initialization.Intake;
        runState.traceSequence = initialization.TraceSequence;
        var semanticPlanPreparation = initialization.SemanticPlanPreparation;
        var planningCompletion = initialization.PlanningCompletion;
        runState.semanticPlan = initialization.SemanticPlan;
        runState.requiredAtomicEvidenceCount = initialization.RequiredAtomicEvidenceCount;
        runState.requireEvidenceSelection = initialization.RequireEvidenceSelection;
        runState.semanticLayoutDimensions = initialization.SemanticLayoutDimensions;
        runState.enableWorkspaceWriterHandoff = initialization.EnableWorkspaceWriterHandoff;
        runState.semanticColumnRoles = initialization.SemanticColumnRoles;
        var semanticColumnRoleOutcome = initialization.SemanticColumnRoleOutcome;
        runState.semanticColumnLabels = initialization.SemanticColumnLabels;
        runState.semanticRowHeader = initialization.SemanticRowHeader;
        runState.semanticRowLabels = initialization.SemanticRowLabels;
        runState.canonicalWorkspaceSelectionLayout = initialization.CanonicalWorkspaceSelectionLayout;
        runState.semanticCandidateStrategyOutcome = initialization.SemanticCandidateStrategyOutcome;
        var semanticCandidateDefinitionOutcome = initialization.SemanticCandidateDefinitionOutcome;
        runState.semanticCandidateObjectType = initialization.SemanticCandidateObjectType;
        runState.semanticCandidateEligibilityRule = initialization.SemanticCandidateEligibilityRule;
        var candidateProjectedEvidenceContract = initialization.CandidateProjectedEvidenceContract;
        runState.messages = initialization.Messages;
        runState.evidenceWorkspace = initialization.EvidenceWorkspace;
        runState.tools = initialization.Tools;
        var namedDocumentActionPreparation = initialization.NamedDocumentActionPreparation;
        var namedDocumentObservationDecision = initialization.NamedDocumentObservationDecision;
        runState.namedDocumentTerminalCompletion = initialization.NamedDocumentTerminalCompletion;
        var initialResearchActionOutcome = initialization.InitialResearchActionOutcome;
        runState.routerInitialActions = initialization.RouterInitialActions;
        runState.plannedFirstAction = initialization.PlannedFirstAction;
        runState.initialActionDecisionSource = initialization.InitialActionDecisionSource;
        runState.evidenceState = new SourceBackedEvidenceExecutionState();
        runState.totalToolCalls = 0;
        runState.repaired = false;
        runState.firstDraft = null;
        runState.latestDraft = null;
        runState.verification = null;
        runState.bundle = EvidenceBundle.Empty(runState.intake.UserQuestion);
        runState.semanticAccepted = false;
        runState.semanticReviewFeedback = null;
        runState.transientProtocolFeedback = null;
        runState.semanticReviewDecision = null;
        runState.semanticReviewReasons = Array.Empty<string>();
        runState.activeSemanticSelectionIds = null;
        runState.activeSemanticSelectionLayout = null;
        runState.directWriterRevisionInstruction = null;
        runState.atomicEvidenceMode = initialization.AtomicEvidenceMode;
        runState.boundedNamedDocumentExtraction = IsBoundedNamedDocumentExtraction(runState.intake);
        runState.useGlobalStructuredCandidateSelection = initialization.UseGlobalStructuredCandidateSelection;
        runState.useFlatContentClaimAdequacy = initialization.UseFlatContentClaimAdequacy;
        runState.candidateAuditEnabled = initialization.CandidateAuditEnabled;
        runState.candidateCollectionEnabled = initialization.CandidateCollectionEnabled;
        // The LLM-authored plan owns the number of atomic source objects needed
        // by the deliverable. Code must not silently add a semantic surplus quota:
        // once that many independently audited candidates exist, the LLM can make
        // the final suitability and layout selection itself.
        runState.candidatePoolTargetCount = initialization.CandidatePoolTargetCount;
        runState.candidateCollectionOpen = runState.candidateCollectionEnabled;
        runState.directWriterRequested = false;
        runState.commitNextVerifiedReviewDirectedRevision = false;
        runState.reviewDirectedRevisionRequiredClaimCount = null;
        runState.explicitSemanticSelectionActive = false;
        runState.inlineFastAnswerRequiresIndependentSemanticReview = false; runState.inlineFastAnswerDirectRevisionSignatures = new HashSet<string>(StringComparer.Ordinal); runState.semanticResolutionWriterReviewActive = false;
        runState.noCitableActionDecisionProgress = new NoCitableActionDecisionProgress();
        runState.lastFastEvidenceReviewSignature = string.Empty;
        runState.fastEvidenceReviewCarryForwardCandidateIds = Array.Empty<string>();
        runState.consecutiveFastEvidenceResearchZeroYieldReviews = 0;
        runState.adequacyCheckpoint = new EvidenceAdequacyCheckpoint<SourceBackedAgentCompletion>();
        runState.flatEvidenceGapTerminalDecisionNextTurn = false;
        runState.semanticallyRejectedDraftSignatures = new HashSet<string>(StringComparer.Ordinal);
        runState.contentClaimDirectRevisionSelectionSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        runState.pendingStructuredAssignmentReview = null;
        runState.pendingStructuredAssignmentDraft = null;
        runState.consecutiveDocumentaryNoProgressTurns = 0;
        runState.selectionOnlyNextTurn = false;
        runState.decisionOnlyNextTurn = false;
        runState.compactSingleFollowUpNextTurn = false;
        runState.yieldResolutionNextTurn = false;
        runState.semanticYieldResolutionPending = false;
        runState.semanticYieldResolutionContinuationCount = 0;
        runState.semanticYieldResolutionContinuationLimit = MaximumSemanticYieldResolutionContinuations;
        runState.semanticYieldTerminalProtocolAttemptCount = 0;
        runState.compactFollowUpLeadEvidenceIds = Array.Empty<string>();
        runState.maximumRunTurns = _options.SeparateActionAndWriter
            ? _options.MaximumTurns + _options.MaximumSemanticCorrectionTurns
            : _options.MaximumTurns;
        runState.maximumRunTurnCeiling = runState.maximumRunTurns
                                    + _options.MaximumSelectionProtocolRepairTurns;
        runState.selectionProtocolRepairTurns = 0;
        runState.lastInvalidSelectionProtocolKey = null;
        runState.consecutiveIdenticalInvalidSelections = 0;
        runState.lastMechanicalFailureSignature = null;
        runState.structuredRejectedEvidenceByCell = new Dictionary<int, HashSet<string>>();
        AddNamedDocumentPreparationTraces(
            runState.traces, runState.traceId, ref runState.traceSequence,
            namedDocumentActionPreparation,
            namedDocumentObservationDecision);
        AddRunStartupTraces(
            runState.traces,
            runState.traceId,
            ref runState.traceSequence,
            semanticPlanPreparation,
            planningCompletion,
            runState.semanticPlan,
            runState.requiredAtomicEvidenceCount,
            runState.semanticLayoutDimensions,
            runState.semanticColumnRoles,
            semanticColumnRoleOutcome,
            semanticCandidateDefinitionOutcome,
            runState.semanticCandidateStrategyOutcome,
            candidateProjectedEvidenceContract,
            initialResearchActionOutcome,
            runState.maximumRunTurns,
            runState.tools,
            runState.routerInitialActions,
            runState.plannedFirstAction);

    }
}
