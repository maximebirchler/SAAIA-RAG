using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
public sealed partial class SourceBackedAgentV2Runner
{
    // These owners are allocated per RunAsync call. No execution state is stored on the reusable runner.
    // Nonnullable run references are assigned by InitializeExecutionAsync, which RunCore awaits before
    // invoking any phase. If initialization fails, no phase is entered. Nullable fields express absence.
    // Iteration references are assigned by preparation or acquisition before later phases may read them.
    private sealed class RunExecutionState(SourceBackedIntake initialIntake, AgentRunLifecycle runLifecycle)
    {
        public SourceBackedIntake intake = initialIntake;
        public readonly AgentRunLifecycle lifecycle = runLifecycle;
        public readonly AgentIterationObserverHealth phaseObserverHealth = new();
        public readonly SourceBackedTelemetryContext.CollectorHealth traceObserverHealth = new();
        public int turn;
        public string traceId = null !;
        public int traceSequence;
        public List<SourceBackedTraceEvent> traces = null !;
        public string semanticPlan = null !;
        public int? requiredAtomicEvidenceCount;
        public bool requireEvidenceSelection;
        public SemanticLayoutDimensions? semanticLayoutDimensions;
        public bool enableWorkspaceWriterHandoff;
        public IReadOnlyDictionary<string, string> semanticColumnRoles = null !;
        public string[] semanticColumnLabels = null !;
        public string semanticRowHeader = null !;
        public IReadOnlyList<string> semanticRowLabels = null !;
        public SemanticSelectionLayout? canonicalWorkspaceSelectionLayout;
        public SemanticCandidateStrategyOutcome semanticCandidateStrategyOutcome = null !;
        public string semanticCandidateObjectType = null !;
        public string semanticCandidateEligibilityRule = null !;
        public List<SourceBackedAgentMessage> messages = null !;
        public LlmEvidenceWorkspace evidenceWorkspace = null !;
        public IReadOnlyList<SourceBackedAgentToolDefinition> tools = null !;
        public SourceBackedAgentCompletion? namedDocumentTerminalCompletion;
        public SourceBackedAgentToolCall[] routerInitialActions = null !;
        public SourceBackedAgentToolCall? plannedFirstAction;
        public string? initialActionDecisionSource;
        public SourceBackedEvidenceExecutionState evidenceState = null !;
        public int totalToolCalls;
        public bool repaired;
        public WriterDraft? firstDraft;
        public WriterDraft? latestDraft;
        public SourceVerificationResult? verification;
        public EvidenceBundle bundle = null !;
        public bool semanticAccepted;
        public string? semanticReviewFeedback;
        public string? transientProtocolFeedback;
        public string? semanticReviewDecision;
        public IReadOnlyList<string> semanticReviewReasons = null !;
        public IReadOnlyList<string>? activeSemanticSelectionIds;
        public SemanticSelectionLayout? activeSemanticSelectionLayout;
        public string? directWriterRevisionInstruction;
        public string atomicEvidenceMode = null !;
        public bool boundedNamedDocumentExtraction;
        public bool useGlobalStructuredCandidateSelection;
        public bool useFlatContentClaimAdequacy;
        public bool candidateAuditEnabled;
        public bool candidateCollectionEnabled;
        public int candidatePoolTargetCount;
        public bool candidateCollectionOpen;
        public bool directWriterRequested;
        public bool commitNextVerifiedReviewDirectedRevision;
        public int? reviewDirectedRevisionRequiredClaimCount;
        public bool explicitSemanticSelectionActive;
        public bool inlineFastAnswerRequiresIndependentSemanticReview;
        public HashSet<string> inlineFastAnswerDirectRevisionSignatures = null !;
        public bool semanticResolutionWriterReviewActive;
        public NoCitableActionDecisionProgress noCitableActionDecisionProgress = null !;
        public string lastFastEvidenceReviewSignature = null !;
        public IReadOnlyList<string> fastEvidenceReviewCarryForwardCandidateIds = null !;
        public int consecutiveFastEvidenceResearchZeroYieldReviews;
        public EvidenceAdequacyCheckpoint<SourceBackedAgentCompletion> adequacyCheckpoint = null !;
        public bool flatEvidenceGapTerminalDecisionNextTurn;
        public HashSet<string> semanticallyRejectedDraftSignatures = null !;
        public HashSet<string> contentClaimDirectRevisionSelectionSignatures = null !;
        public SemanticReview? pendingStructuredAssignmentReview;
        public WriterDraft? pendingStructuredAssignmentDraft;
        public int consecutiveDocumentaryNoProgressTurns;
        public bool selectionOnlyNextTurn;
        public bool decisionOnlyNextTurn;
        public bool compactSingleFollowUpNextTurn;
        public bool yieldResolutionNextTurn;
        public bool semanticYieldResolutionPending;
        public int semanticYieldResolutionContinuationCount;
        public int semanticYieldResolutionContinuationLimit;
        public int semanticYieldTerminalProtocolAttemptCount;
        public IReadOnlyList<string> compactFollowUpLeadEvidenceIds = null !;
        public int maximumRunTurns;
        public int maximumRunTurnCeiling;
        public int selectionProtocolRepairTurns;
        public string? lastInvalidSelectionProtocolKey;
        public int consecutiveIdenticalInvalidSelections;
        public string? lastMechanicalFailureSignature;
        public Dictionary<int, HashSet<string>> structuredRejectedEvidenceByCell = null !;
    }

    private sealed class IterationExecutionState
    {
        public bool freshWriterOutputNeedsSemanticReview;
        public IReadOnlyList<EvidenceItem> collectionCandidates = null !;
        public bool dedicatedCandidateAuditTurn;
        public string[] selectionEligibleObservedEvidenceIds = null !;
        public bool hasCitableEvidence;
        public int observedCitableSourceCount;
        public bool hasMechanicallyFinalizableEvidence;
        public int requiredEvidenceCount;
        public bool semanticNeedsMoreEvidence;
        public bool finalizationTurn;
        public bool selectionDecisionTurn;
        public bool allowAdaptiveFlatSelection;
        public SourceBackedAgentCompletion? flatEvidenceAdequacyCompletion;
        public bool semanticSelectionDecisionTurn;
        public bool workspaceWriterDecisionTurn;
        public bool semanticYieldTerminalDecisionTurn;
        public SourceBackedAgentCompletion completion = null !;
        public bool structuredCandidateWriterTurn;
        public string? structuredCandidateWriterProtocolError;
        public bool compactSingleFollowUpTurn;
        public bool yieldResolutionTurn;
        public bool completedByFastEvidenceWriter;
        public bool reviewDirectedRevisionCompletedThisTurn;
        public bool reviewDirectedRevisionExactClaimCountSatisfied;
        public IReadOnlyList<string> selectableEvidenceIds = null !;
    }
}
