using System.Globalization;
using System.Text.Json;
namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
public sealed partial class SourceBackedAgentV2Runner
{
    public async Task<SourceBackedPipelineResult> RunAsync(
        SourceBackedIntake intake, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var traceId = "source-backed-agent-v2-" + Guid.NewGuid().ToString("N")[..10];
        using var telemetryScope = SourceBackedTelemetryContext.Push(traceId);
        var traceSequence = 0;
        var traces = new List<SourceBackedTraceEvent>();
        var initialization = await PrepareRunInitializationAsync(
                intake, traces, traceId, traceSequence, ct)
            .ConfigureAwait(false);
        intake = initialization.Intake;
        traceSequence = initialization.TraceSequence;
        var semanticPlanPreparation = initialization.SemanticPlanPreparation;
        var planningCompletion = initialization.PlanningCompletion;
        var semanticPlan = initialization.SemanticPlan;
        var requiredAtomicEvidenceCount = initialization.RequiredAtomicEvidenceCount;
        var requireEvidenceSelection = initialization.RequireEvidenceSelection;
        var semanticLayoutDimensions = initialization.SemanticLayoutDimensions;
        var enableWorkspaceWriterHandoff = initialization.EnableWorkspaceWriterHandoff;
        var semanticColumnRoles = initialization.SemanticColumnRoles;
        var semanticColumnRoleOutcome = initialization.SemanticColumnRoleOutcome;
        var semanticColumnLabels = initialization.SemanticColumnLabels;
        var semanticRowHeader = initialization.SemanticRowHeader;
        var semanticRowLabels = initialization.SemanticRowLabels;
        var canonicalWorkspaceSelectionLayout = initialization.CanonicalWorkspaceSelectionLayout;
        var semanticCandidateStrategyOutcome = initialization.SemanticCandidateStrategyOutcome;
        var semanticCandidateDefinitionOutcome = initialization.SemanticCandidateDefinitionOutcome;
        var semanticCandidateObjectType = initialization.SemanticCandidateObjectType;
        var semanticCandidateEligibilityRule = initialization.SemanticCandidateEligibilityRule;
        var candidateProjectedEvidenceContract = initialization.CandidateProjectedEvidenceContract;
        var messages = initialization.Messages;
        var evidenceWorkspace = initialization.EvidenceWorkspace;
        var tools = initialization.Tools;
        var namedDocumentActionPreparation = initialization.NamedDocumentActionPreparation;
        var namedDocumentObservationDecision = initialization.NamedDocumentObservationDecision;
        var namedDocumentTerminalCompletion = initialization.NamedDocumentTerminalCompletion;
        var initialResearchActionOutcome = initialization.InitialResearchActionOutcome;
        var routerInitialActions = initialization.RouterInitialActions;
        var plannedFirstAction = initialization.PlannedFirstAction;
        var initialActionDecisionSource = initialization.InitialActionDecisionSource;
        var cumulativeResults = new ToolResults();
        var executedRequests = new List<RetrievalRequest>();
        var executedCallKeys = new HashSet<string>(StringComparer.Ordinal);
        var resolvedNavigationEvidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var auditedNavigationEvidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedEvidenceIds = new List<string>();
        var observedEvidenceIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalToolCalls = 0;
        var repaired = false;
        WriterDraft? firstDraft = null;
        WriterDraft? latestDraft = null;
        SourceVerificationResult? verification = null;
        var bundle = EvidenceBundle.Empty(intake.UserQuestion);
        var semanticAccepted = false;
        string? semanticReviewFeedback = null;
        string? transientProtocolFeedback = null;
        string? semanticReviewDecision = null;
        IReadOnlyList<string> semanticReviewReasons = Array.Empty<string>();
        IReadOnlyList<string>? activeSemanticSelectionIds = null;
        SemanticSelectionLayout? activeSemanticSelectionLayout = null;
        string? directWriterRevisionInstruction = null;
        var semanticallyRejectedEvidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var semanticallyAuditedEvidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var atomicEvidenceMode = initialization.AtomicEvidenceMode;
        var boundedNamedDocumentExtraction =
            IsBoundedNamedDocumentExtraction(intake);
        var useGlobalStructuredCandidateSelection =
            initialization.UseGlobalStructuredCandidateSelection;
        var useFlatContentClaimAdequacy = initialization.UseFlatContentClaimAdequacy;
        var candidateAuditEnabled = initialization.CandidateAuditEnabled;
        var candidateCollectionEnabled = initialization.CandidateCollectionEnabled;
        // The LLM-authored plan owns the number of atomic source objects needed
        // by the deliverable. Code must not silently add a semantic surplus quota:
        // once that many independently audited candidates exist, the LLM can make
        // the final suitability and layout selection itself.
        var candidatePoolTargetCount = initialization.CandidatePoolTargetCount;
        var candidateCollectionOpen = candidateCollectionEnabled;
        var pendingSemanticCandidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directWriterRequested = false;
        var commitNextVerifiedReviewDirectedRevision = false;
        int? reviewDirectedRevisionRequiredClaimCount = null;
        var explicitSemanticSelectionActive = false;
        var inlineFastAnswerRequiresIndependentSemanticReview = false; var inlineFastAnswerDirectRevisionSignatures = new HashSet<string>(StringComparer.Ordinal); var semanticResolutionWriterReviewActive = false;
        var noCitableActionDecisionProgress = new NoCitableActionDecisionProgress();
        var lastFastEvidenceReviewSignature = string.Empty;
        IReadOnlyList<string> fastEvidenceReviewCarryForwardCandidateIds = Array.Empty<string>();
        var consecutiveFastEvidenceResearchZeroYieldReviews = 0;
        var lastFlatEvidenceAdequacySignature = string.Empty;
        IReadOnlyList<string> flatEvidenceAdequacyIncumbentEvidenceIds = Array.Empty<string>();
        var flatEvidenceAdequacyReviewedEvidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flatEvidenceAdequacyGapActive = false;
        var flatEvidenceGapTerminalDecisionNextTurn = false;
        var semanticallyRejectedDraftSignatures = new HashSet<string>(StringComparer.Ordinal);
        var contentClaimDirectRevisionSelectionSignatures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SemanticReview? pendingStructuredAssignmentReview = null;
        WriterDraft? pendingStructuredAssignmentDraft = null;
        var consecutiveDocumentaryNoProgressTurns = 0;
        var selectionOnlyNextTurn = false;
        var decisionOnlyNextTurn = false;
        var compactSingleFollowUpNextTurn = false;
        var yieldResolutionNextTurn = false;
        var semanticYieldResolutionPending = false;
        var semanticYieldResolutionContinuationCount = 0;
        var semanticYieldResolutionContinuationLimit =
            MaximumSemanticYieldResolutionContinuations;
        var semanticYieldTerminalProtocolAttemptCount = 0;
        IReadOnlyList<string> compactFollowUpLeadEvidenceIds = Array.Empty<string>();
        var maximumRunTurns = _options.SeparateActionAndWriter
            ? _options.MaximumTurns + _options.MaximumSemanticCorrectionTurns
            : _options.MaximumTurns;
        var maximumRunTurnCeiling = maximumRunTurns
                                    + _options.MaximumSelectionProtocolRepairTurns;
        var selectionProtocolRepairTurns = 0;
        string? lastInvalidSelectionProtocolKey = null;
        var consecutiveIdenticalInvalidSelections = 0;
        string? lastMechanicalFailureSignature = null;
        var structuredRejectedEvidenceByCell = new Dictionary<int, HashSet<string>>();
        AddNamedDocumentPreparationTraces(
            traces, traceId, ref traceSequence,
            namedDocumentActionPreparation,
            namedDocumentObservationDecision);
        AddRunStartupTraces(
            traces,
            traceId,
            ref traceSequence,
            semanticPlanPreparation,
            planningCompletion,
            semanticPlan,
            requiredAtomicEvidenceCount,
            semanticLayoutDimensions,
            semanticColumnRoles,
            semanticColumnRoleOutcome,
            semanticCandidateDefinitionOutcome,
            semanticCandidateStrategyOutcome,
            candidateProjectedEvidenceContract,
            initialResearchActionOutcome,
            maximumRunTurns,
            tools,
            routerInitialActions,
            plannedFirstAction);
        for (var turn = 1; turn <= maximumRunTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var freshWriterOutputNeedsSemanticReview = false;
            ApplyGlobalStructuredCandidateMinimumGate(
                useGlobalStructuredCandidateSelection, candidateAuditEnabled,
                bundle, observedEvidenceIds, semanticallyAuditedEvidenceIds,
                semanticallyRejectedEvidenceIds, requiredAtomicEvidenceCount,
                candidatePoolTargetCount, semanticRowLabels,
                semanticColumnLabels, ref candidateCollectionOpen,
                ref selectionOnlyNextTurn, traces, traceId,
                ref traceSequence, turn);
            var candidateAuditTurn = PrepareCandidateAuditTurn(
                candidateAuditEnabled, candidateCollectionOpen,
                bundle, observedEvidenceIds,
                pendingSemanticCandidateIds, semanticallyRejectedEvidenceIds);
            var collectionCandidates = candidateAuditTurn.Candidates;
            var dedicatedCandidateAuditTurn = candidateAuditTurn.IsDedicated;
            var selectionEligibleObservedEvidenceIds = observedEvidenceIds
                .Where(id => !candidateAuditEnabled
                             || semanticallyAuditedEvidenceIds.Contains(id))
                .ToArray();
            var hasCitableEvidence = selectionEligibleObservedEvidenceIds.Any(id =>
                bundle.ById.TryGetValue(id, out var item)
                && !item.RiskFlags.Contains(
                    "orientation_only",
                    StringComparer.OrdinalIgnoreCase));
            var observedCitableSourceCount = CountObservedSelectableEvidenceUnits(
                bundle,
                selectionEligibleObservedEvidenceIds.Where(id =>
                    !semanticallyRejectedEvidenceIds.Contains(id)),
                atomicEvidenceMode);
            var hasMechanicallyFinalizableEvidence = hasCitableEvidence
                && (requiredAtomicEvidenceCount.GetValueOrDefault() <= 1
                    || observedCitableSourceCount
                    >= requiredAtomicEvidenceCount.GetValueOrDefault());
            if (TryActivateCumulativeBudgetFinalization(
                    hasMechanicallyFinalizableEvidence,
                    executedRequests.Count,
                    turn,
                    pendingSemanticCandidateIds,
                    ref candidateCollectionOpen,
                    ref selectionOnlyNextTurn,
                    ref decisionOnlyNextTurn,
                    ref flatEvidenceGapTerminalDecisionNextTurn,
                    traces,
                    traceId,
                    ref traceSequence))
            {
                dedicatedCandidateAuditTurn = false;
            }
            if (candidateCollectionEnabled)
                AddCandidateCollectionStateTrace(
                    traces, traceId, ref traceSequence, turn, candidateCollectionOpen,
                    observedEvidenceIds.Count, collectionCandidates.Count,
                    pendingSemanticCandidateIds.Count, semanticallyAuditedEvidenceIds.Count,
                    semanticallyRejectedEvidenceIds.Count, observedCitableSourceCount);
            var semanticNeedsMoreEvidence = string.Equals(
                semanticReviewDecision,
                "need_more_evidence",
                StringComparison.OrdinalIgnoreCase);
            var structuredSelectionRun = _options.SeparateActionAndWriter
                                         && requiredAtomicEvidenceCount is > 1;
            var finalizationTurn = structuredSelectionRun
                ? turn >= maximumRunTurns
                  || (turn >= _options.MaximumTurns
                      && !semanticNeedsMoreEvidence
                      && observedCitableSourceCount
                      >= requiredAtomicEvidenceCount.GetValueOrDefault())
                : turn >= _options.MaximumTurns
                  || (!_options.SeparateActionAndWriter
                      && turn >= _options.MaximumTurns - 2
                      && hasCitableEvidence
                      && !semanticNeedsMoreEvidence);
            var renderingReservationTurn = _options.SeparateActionAndWriter
                                           && requiredAtomicEvidenceCount is > 1
                                           && observedCitableSourceCount
                                           >= requiredAtomicEvidenceCount.Value
                                           && turn >= _options.MaximumTurns - 2
                                           && !semanticNeedsMoreEvidence;
            var selectionDecisionTurn = !candidateCollectionOpen
                                        && (finalizationTurn
                                            || selectionOnlyNextTurn
                                            || decisionOnlyNextTurn
                                            || renderingReservationTurn);
            var requiredEvidenceCount =
                requiredAtomicEvidenceCount.GetValueOrDefault();
            var allowAdaptiveFlatSelection =
                candidateCollectionOpen
                && (candidateAuditEnabled || useFlatContentClaimAdequacy)
                && semanticLayoutDimensions is null
                && requiredEvidenceCount > 1
                && observedCitableSourceCount > 0;
            SourceBackedAgentCompletion? flatEvidenceAdequacyCompletion = null;
            if (allowAdaptiveFlatSelection)
            {
                var flatEvidenceAdequacySignature = string.Join(
                    ",",
                    selectionEligibleObservedEvidenceIds
                        .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));
                if (flatEvidenceAdequacySignature.Length > 0
                    && !string.Equals(
                        flatEvidenceAdequacySignature,
                        lastFlatEvidenceAdequacySignature,
                        StringComparison.Ordinal))
                {
                    lastFlatEvidenceAdequacySignature =
                        flatEvidenceAdequacySignature;
                    FlatEvidenceAdequacyOutcome flatAdequacy;
                    try
                    {
                        flatAdequacy = await ReviewFlatEvidenceAdequacyAsync(
                                intake,
                                semanticPlan,
                                bundle,
                                selectionEligibleObservedEvidenceIds,
                                requiredEvidenceCount,
                                atomicEvidenceMode,
                                flatEvidenceAdequacyIncumbentEvidenceIds,
                                flatEvidenceAdequacyReviewedEvidenceIds,
                                semanticReviewFeedback ?? string.Empty,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (SourceBackedLlmBudgetExceededException ex) when (
                        string.Equals(
                            ex.Reason,
                            "terminal_budget_only",
                            StringComparison.Ordinal))
                    {
                        TryActivateCumulativeBudgetFinalization(
                            hasMechanicallyFinalizableEvidence,
                            executedRequests.Count,
                            turn,
                            pendingSemanticCandidateIds,
                            ref candidateCollectionOpen,
                            ref selectionOnlyNextTurn,
                            ref decisionOnlyNextTurn,
                            ref flatEvidenceGapTerminalDecisionNextTurn,
                            traces,
                            traceId,
                            ref traceSequence,
                            force: true);
                        turn--;
                        continue;
                    }
                    // A truncated or otherwise protocol-invalid checkpoint is not
                    // a semantic decision. Keep the last valid tournament state
                    // and gap verdict until the LLM successfully replaces them.
                    if (flatAdequacy.ProtocolValid
                        && flatAdequacy.TournamentSelectionEvidenceIds is not null)
                    {
                        flatEvidenceAdequacyIncumbentEvidenceIds =
                            flatAdequacy.TournamentSelectionEvidenceIds;
                    }
                    if (flatAdequacy.ProtocolValid)
                    {
                        flatEvidenceAdequacyGapActive =
                            flatAdequacy.Decision is "continue" or "expand_context";
                    }
                    TraceFlatEvidenceAdequacy(
                        traces, traceId, ref traceSequence, turn,
                        flatAdequacy, selectionEligibleObservedEvidenceIds,
                        requiredEvidenceCount);
                    flatEvidenceAdequacyCompletion =
                        flatAdequacy.RoutedCompletion;
                    if (flatEvidenceAdequacyCompletion is null
                        && (flatAdequacy.ProtocolValid
                            || !flatEvidenceAdequacyGapActive))
                    {
                        semanticReviewFeedback =
                            "CHECKPOINT D'ADEQUATION: continuer. Manque precise par "
                            + "le juge LLM: "
                            + flatAdequacy.Reason;
                    }
                }
            }
            var semanticSelectionDecisionTurn =
                !candidateCollectionOpen
                && _options.SeparateActionAndWriter
                && requiredEvidenceCount > 0
                && (requiredEvidenceCount > 1
                    || requireEvidenceSelection || selectionOnlyNextTurn)
                && (selectionOnlyNextTurn
                    || renderingReservationTurn
                    || finalizationTurn
                    && observedCitableSourceCount >= requiredEvidenceCount);
            var workspaceWriterDecisionTurn =
                semanticSelectionDecisionTurn
                && enableWorkspaceWriterHandoff;
            var semanticYieldTerminalDecisionTurn =
                ShouldRequestSemanticYieldTerminalDecision(
                    flatEvidenceGapTerminalDecisionNextTurn,
                    semanticYieldResolutionPending,
                    semanticYieldResolutionContinuationCount,
                    semanticYieldResolutionContinuationLimit,
                    candidateAuditEnabled,
                    pendingSemanticCandidateIds.Count,
                    dedicatedCandidateAuditTurn);
            compactSingleFollowUpNextTurn &= !(semanticSelectionDecisionTurn || semanticYieldTerminalDecisionTurn);
            if (semanticYieldTerminalDecisionTurn)
            {
                AddSemanticYieldTerminalDecisionRequestedTrace(
                    traces, traceId, ref traceSequence,
                    flatEvidenceGapTerminalDecisionNextTurn, turn,
                    semanticYieldResolutionContinuationCount,
                    semanticYieldResolutionContinuationLimit,
                    candidateAuditEnabled, pendingSemanticCandidateIds.Count,
                    consecutiveDocumentaryNoProgressTurns);
            }
            if (turn == _options.MaximumTurns - 3)
                messages.Add(SourceBackedAgentMessage.User(BuildLastExplorationTurnMessage()));
            if (finalizationTurn)
                messages.Add(SourceBackedAgentMessage.User(BuildBudgetFinalizationMessage(
                    _options.SeparateActionAndWriter)));
            else if (selectionOnlyNextTurn)
                messages.Add(SourceBackedAgentMessage.User(
                    BuildNoProgressSelectionMessage(requiredAtomicEvidenceCount)));
            else if (renderingReservationTurn)
                messages.Add(SourceBackedAgentMessage.User(
                    BuildRenderingBudgetSelectionMessage(requiredAtomicEvidenceCount)));
            SourceBackedAgentCompletion completion;
            CandidateAuditExecutionResult? candidateAuditExecution = null;
            ResearchTransitionExecution? researchTransitionExecution = null;
            var structuredCandidateWriterTurn = false;
            string? structuredCandidateWriterProtocolError = null;
            var compactSingleFollowUpTurn = false;
            var yieldResolutionTurn = yieldResolutionNextTurn
                                      || semanticYieldResolutionPending;
            var completedByFastEvidenceWriter = false;
            var reviewDirectedRevisionCompletedThisTurn = false;
            var reviewDirectedRevisionExactClaimCountSatisfied = true;
            IReadOnlyList<string> selectableEvidenceIds = Array.Empty<string>();
            if (flatEvidenceAdequacyCompletion is not null)
            {
                completion = flatEvidenceAdequacyCompletion;
            }
            else if (namedDocumentTerminalCompletion is not null)
            {
                completion = namedDocumentTerminalCompletion;
                namedDocumentTerminalCompletion = null;
            }
            else if (TryApplyInitialAction(
                    ref routerInitialActions,
                    ref plannedFirstAction,
                    initialActionDecisionSource,
                    traces,
                    traceId,
                    ref traceSequence,
                    turn,
                    out completion))
            {
            }
            else if (_options.SeparateActionAndWriter
                && directWriterRequested)
            {
                reviewDirectedRevisionCompletedThisTurn =
                    commitNextVerifiedReviewDirectedRevision;
                commitNextVerifiedReviewDirectedRevision = false;
                var requiredClaimCountForThisRevision =
                    reviewDirectedRevisionRequiredClaimCount;
                reviewDirectedRevisionRequiredClaimCount = null;
                var writerHandoff =
                    await CompleteDirectWriterHandoffWithNamedValueRepairAsync(
                            messages,
                            verification,
                            directWriterRevisionInstruction,
                            intake,
                            bundle,
                            activeSemanticSelectionIds,
                            activeSemanticSelectionLayout,
                            atomicEvidenceMode,
                            boundedNamedDocumentExtraction
                                ? requiredAtomicEvidenceCount
                                : null,
                            reviewDirectedRevisionCompletedThisTurn
                            && boundedNamedDocumentExtraction
                            && requiredClaimCountForThisRevision is > 0,
                            ct)
                        .ConfigureAwait(false);
                completion = writerHandoff.Completion;
                TraceNamedValueProtocolRepair(
                    writerHandoff, traces, traceId, ref traceSequence, turn);
                reviewDirectedRevisionExactClaimCountSatisfied =
                    requiredClaimCountForThisRevision is null
                    || writerHandoff.StructuredClaimCount
                        == requiredClaimCountForThisRevision;
                freshWriterOutputNeedsSemanticReview = true;
                structuredCandidateWriterProtocolError = writerHandoff.ProtocolError;
                directWriterRequested = false;
                directWriterRevisionInstruction = null;
                if (writerHandoff.Layout is { } renderedLayout)
                {
                    TraceStructuredRendererCompletion(renderedLayout, activeSemanticSelectionIds, completion, traces, traceId, ref traceSequence, turn);
                }
                else
                {
                    AddWriterTrace(
                        traces,
                        traceId,
                        ref traceSequence,
                        turn,
                        completion,
                        directRevision: true);
                }
            }
            else
            {
                selectableEvidenceIds = semanticSelectionDecisionTurn
                    ? SelectEligibleWorkspaceEvidenceIds(
                        bundle,
                        selectionEligibleObservedEvidenceIds,
                        semanticallyRejectedEvidenceIds,
                        Math.Min(
                            80,
                            Math.Max(
                                12,
                                Math.Max(
                                    requiredEvidenceCount + 8,
                                    candidatePoolTargetCount))))
                    : Array.Empty<string>();
                NavigationAnchorEligibilityAuditExecution navigationAnchorAudit;
                try
                {
                    navigationAnchorAudit = candidateCollectionOpen
                                            && candidateAuditEnabled
                                            && !dedicatedCandidateAuditTurn
                        ? await CompleteNavigationAnchorEligibilityAuditAsync(
                            intake, semanticPlan, semanticCandidateObjectType, semanticCandidateEligibilityRule,
                            bundle, resolvedNavigationEvidenceIds,
                            auditedNavigationEvidenceIds, ct).ConfigureAwait(false)
                        : NoNavigationAnchorEligibilityAudit(bundle);
                }
                catch (SourceBackedLlmBudgetExceededException ex) when (
                    string.Equals(
                        ex.Reason,
                        "terminal_budget_only",
                        StringComparison.Ordinal))
                {
                    TryActivateCumulativeBudgetFinalization(
                        hasMechanicallyFinalizableEvidence,
                        executedRequests.Count,
                        turn,
                        pendingSemanticCandidateIds,
                        ref candidateCollectionOpen,
                        ref selectionOnlyNextTurn,
                        ref decisionOnlyNextTurn,
                        ref flatEvidenceGapTerminalDecisionNextTurn,
                        traces,
                        traceId,
                        ref traceSequence,
                        force: true);
                    turn--;
                    continue;
                }
                bundle = navigationAnchorAudit.Bundle;
                TraceNavigationAnchorEligibilityAudit(traces, traceId, ref traceSequence, turn, navigationAnchorAudit);
                var turnTools = BuildTurnEvidenceTools(
                    tools,
                    bundle,
                    selectionEligibleObservedEvidenceIds,
                    semanticallyRejectedEvidenceIds,
                    enableWorkspaceWriterHandoff,
                    requiredAtomicEvidenceCount,
                    canonicalWorkspaceSelectionLayout is not null);
                var actionTools = dedicatedCandidateAuditTurn
                    ? Array.Empty<SourceBackedAgentToolDefinition>()
                    : semanticYieldTerminalDecisionTurn
                    ? BuildSemanticYieldTerminalDecisionTools()
                    : compactSingleFollowUpNextTurn
                    ? BuildCompactSingleFollowUpTools(
                        turnTools,
                        yieldResolutionTurn)
                    : candidateCollectionOpen
                    ? BuildCandidateCollectionTools(
                        turnTools,
                        semanticCandidateStrategyOutcome.CandidateScopePaths,
                        bundle,
                        resolvedNavigationEvidenceIds,
                        executedRequests,
                        _options.MaximumWorkingEvidenceItems, intake)
                    : semanticSelectionDecisionTurn
                    ? workspaceWriterDecisionTurn
                        ? new[]
                        {
                            BuildEvidenceWorkspaceTool(
                                enableWriterHandoff: true,
                                requiredAtomicEvidenceCount,
                                canonicalWorkspaceSelectionLayout is not null,
                                selectableEvidenceIds)
                        }
                        : new[]
                        {
                            BuildSemanticSelectionTool(
                                requiredEvidenceCount,
                                semanticLayoutDimensions,
                                selectableEvidenceIds,
                                semanticColumnLabels,
                                semanticRowHeader,
                                semanticRowLabels)
                        }
                    : decisionOnlyNextTurn
                      || finalizationTurn
                        ? Array.Empty<SourceBackedAgentToolDefinition>()
                    : compactSingleFollowUpNextTurn
                        ? BuildCompactSingleFollowUpTools(
                            tools,
                            yieldResolutionTurn)
                    : finalizationTurn
                        ? turnTools
                            .Where(tool =>
                                string.Equals(
                                    tool.Name,
                                    SemanticSelectionToolName,
                                    StringComparison.OrdinalIgnoreCase)
                                || enableWorkspaceWriterHandoff
                                && string.Equals(
                                    tool.Name,
                                    EvidenceWorkspaceToolName,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToArray()
                        : turnTools
                            .Where(tool =>
                                (!string.Equals(
                                     tool.Name,
                                     SemanticSelectionToolName,
                                     StringComparison.OrdinalIgnoreCase)
                                 || requiredEvidenceCount > 0
                                 && observedCitableSourceCount
                                 >= requiredEvidenceCount)
                                && (hasCitableEvidence
                                    || !string.Equals(
                                        tool.Name,
                                        EvidenceWorkspaceToolName,
                                        StringComparison.OrdinalIgnoreCase)))
                            .ToArray();
                if (allowAdaptiveFlatSelection
                    && !compactSingleFollowUpNextTurn
                    && !semanticYieldTerminalDecisionTurn)
                {
                    actionTools = actionTools
                        .Append(BuildSemanticSelectionTool(
                            requiredEvidenceCount,
                            dimensions: null,
                            allowedEvidenceIds:
                                selectionEligibleObservedEvidenceIds,
                            canonicalColumnLabels: null,
                            minimumCount: 1,
                            provisionalFlatTarget: true))
                        .Append(BuildSourceBackedClarificationTool())
                        .ToArray();
                }
                var compactCandidateCollectionForContextRecovery = false;
                var actionFeedback = MergeAgentFeedback(semanticReviewFeedback, transientProtocolFeedback);
                IReadOnlyList<SourceBackedAgentMessage> BuildActionMessages()
                    => dedicatedCandidateAuditTurn
                        ? Array.Empty<SourceBackedAgentMessage>()
                        : semanticYieldTerminalDecisionTurn
                        ? BuildSemanticYieldTerminalDecisionMessages(
                            intake,
                            semanticPlan,
                            executedRequests,
                            actionFeedback,
                            semanticYieldResolutionContinuationCount)
                        : candidateCollectionOpen
                          && UsesResearchTransitionTools(actionTools)
                        ? BuildResearchTransitionDecisionMessages(
                            intake,
                            semanticPlan,
                            semanticCandidateObjectType,
                            semanticCandidateEligibilityRule,
                            bundle,
                            resolvedNavigationEvidenceIds,
                            executedRequests,
                            semanticCandidateStrategyOutcome.CandidateScopePaths,
                            observedCitableSourceCount,
                            candidatePoolTargetCount,
                            actionFeedback,
                            _options.MaximumWorkingEvidenceItems,
                            compactCandidateCollectionForContextRecovery)
                        : compactSingleFollowUpNextTurn
                        ? BuildCompactSingleFollowUpMessages(
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            semanticallyRejectedEvidenceIds,
                            compactFollowUpLeadEvidenceIds,
                            executedRequests,
                            actionFeedback)
                        : candidateCollectionOpen
                          && compactCandidateCollectionForContextRecovery
                        ? BuildCompactCandidateCollectionRetrievalMessages(
                            intake, semanticPlan,
                            semanticCandidateStrategyOutcome, bundle,
                            resolvedNavigationEvidenceIds, executedRequests,
                            selectionEligibleObservedEvidenceIds,
                            observedCitableSourceCount, candidatePoolTargetCount,
                            actionFeedback, _options.MaximumWorkingEvidenceItems,
                            allowAdaptiveFlatSelection)
                        : candidateCollectionOpen
                        ? BuildCandidateCollectionRetrievalMessages(
                            messages, semanticCandidateStrategyOutcome,
                            bundle, selectionEligibleObservedEvidenceIds,
                            observedCitableSourceCount, candidatePoolTargetCount,
                            actionFeedback,
                            allowAdaptiveFlatSelection)
                        : semanticSelectionDecisionTurn
                        ? BuildSemanticSelectionDecisionMessages(
                            intake,
                            semanticPlan,
                            bundle,
                            selectableEvidenceIds,
                             semanticReviewFeedback,
                             requiredEvidenceCount,
                             semanticLayoutDimensions,
                             semanticRowHeader,
                             semanticRowLabels,
                             semanticColumnLabels,
                             canonicalLayoutAlreadyDefined:
                                semanticLayoutDimensions is not null
                                && !string.IsNullOrWhiteSpace(semanticRowHeader)
                                && semanticColumnLabels.Length > 0
                                && semanticRowLabels.Any(),
                            useWorkspaceWriterHandoff:
                                workspaceWriterDecisionTurn)
                        : _options.SeparateActionAndWriter
                          && compactSingleFollowUpNextTurn
                            ? BuildCompactSingleFollowUpMessages(
                                intake,
                                semanticPlan,
                                bundle,
                                observedEvidenceIds,
                                semanticallyRejectedEvidenceIds,
                                compactFollowUpLeadEvidenceIds,
                                executedRequests,
                                semanticReviewFeedback)
                        : _options.SeparateActionAndWriter
                            ? messages
                                .Concat(new[]
                                {
                                    SourceBackedAgentMessage.User(BuildActionDecisionMessage(
                                        requiredAtomicEvidenceCount,
                                        semanticLayoutDimensions,
                                        requireEvidenceSelection,
                                        enableWorkspaceWriterHandoff))
                                })
                                .ToArray()
                            : messages.ToArray();
                var actionMaxTokens = _options.SeparateActionAndWriter
                    ? semanticSelectionDecisionTurn
                        ? Math.Clamp(
                            96 + requiredEvidenceCount * 12,
                            128,
                            512)
                        : semanticYieldTerminalDecisionTurn
                            ? Math.Min(_options.MaximumActionTokens, 128)
                        : compactSingleFollowUpNextTurn
                            ? Math.Min(_options.MaximumActionTokens, 192)
                        : candidateCollectionOpen
                            ? Math.Min(_options.MaximumActionTokens, 256)
                        : selectionDecisionTurn
                            ? Math.Min(_options.MaximumActionTokens, 192)
                        : compactSingleFollowUpNextTurn
                            ? Math.Min(_options.MaximumActionTokens, 192)
                        : _options.MaximumActionTokens
                    : _options.MaximumOutputTokens;
                var requireActionToolCall = candidateCollectionOpen
                                            || semanticSelectionDecisionTurn
                                             || compactSingleFollowUpNextTurn
                                             || semanticYieldTerminalDecisionTurn;
                if (!requireActionToolCall
                    && !dedicatedCandidateAuditTurn
                    && !decisionOnlyNextTurn
                    && !finalizationTurn
                    && observedEvidenceIds.Count > 0)
                {
                    actionTools = actionTools
                        .Append(BuildSourceBackedClarificationTool())
                        .ToArray();
                }
                var useStructuredCandidateWriter = ShouldUseStructuredCandidateWriter(
                    semanticSelectionDecisionTurn, semanticLayoutDimensions,
                    semanticRowLabels, semanticColumnLabels, requiredEvidenceCount);
                compactSingleFollowUpTurn =
                    compactSingleFollowUpNextTurn
                    || semanticYieldTerminalDecisionTurn;
                var contextRecoveryAttempted = false;
                var effectiveActionMaxTokens = actionMaxTokens;
                int? exactInputTokens = null;
                var cumulativeBudgetRestartTurn = false;
                while (true)
                {
                    var actionMessages = BuildActionMessages();
                    exactInputTokens = useStructuredCandidateWriter
                        ? null
                        : await CountInputTokensAsync(
                                actionMessages,
                                actionTools,
                                requireActionToolCall,
                                ct)
                            .ConfigureAwait(false);
                    if (!contextRecoveryAttempted
                        && ExceedsContextBudget(
                            exactInputTokens,
                            effectiveActionMaxTokens))
                    {
                        contextRecoveryAttempted = true;
                        compactCandidateCollectionForContextRecovery |= candidateCollectionOpen;
                        effectiveActionMaxTokens = Math.Min(
                            effectiveActionMaxTokens,
                            192);
                        if (!semanticSelectionDecisionTurn)
                        {
                            CompactWorkingMessages(
                                messages,
                                intake,
                                semanticPlan,
                                bundle,
                                observedEvidenceIds,
                                executedRequests,
                                evidenceWorkspace,
                                MergeAgentFeedback(
                                    semanticReviewFeedback,
                                    transientProtocolFeedback),
                                emergencyContextRecovery: true,
                                includeEvidenceDetails: !candidateCollectionOpen);
                            messages.Add(SourceBackedAgentMessage.User(
                                BuildContextRecoveryActionMessage()));
                        }
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.context.recovery_requested",
                            ("turn", turn),
                            ("reason", "preflight_exact_token_budget"),
                            ("selection_decision",
                                semanticSelectionDecisionTurn),
                            ("input_tokens", exactInputTokens),
                            ("reserved_output_tokens", effectiveActionMaxTokens),
                            ("safety_reserve_tokens", ContextSafetyReserveTokens),
                            ("maximum_context_tokens", _options.MaximumContextTokens)));
                        continue;
                    }
                    if (contextRecoveryAttempted
                        && exactInputTokens is { } measuredInputTokens
                        && ExceedsContextBudget(
                            exactInputTokens,
                            effectiveActionMaxTokens))
                    {
                        var availableOutputTokens =
                            _options.MaximumContextTokens
                            - measuredInputTokens
                            - ContextSafetyReserveTokens;
                        if (availableOutputTokens >= 64)
                        {
                            effectiveActionMaxTokens = Math.Min(
                                effectiveActionMaxTokens,
                                availableOutputTokens);
                            AddTrace(traces, Trace(
                                traceId,
                                ref traceSequence,
                                SourceBackedPipelineStep.IterationController,
                                "source_backed_agent_v2.context.output_budget_adjusted",
                                ("turn", turn),
                                ("input_tokens", measuredInputTokens),
                                ("reserved_output_tokens", effectiveActionMaxTokens),
                                ("safety_reserve_tokens", ContextSafetyReserveTokens),
                                ("maximum_context_tokens", _options.MaximumContextTokens)));
                        }
                        else
                        {
                            AddTrace(traces, Trace(
                                traceId,
                                ref traceSequence,
                                SourceBackedPipelineStep.IterationController,
                                "source_backed_agent_v2.context.preflight_blocked",
                                ("turn", turn),
                                ("input_tokens", measuredInputTokens),
                                ("minimum_output_tokens", 64),
                                ("safety_reserve_tokens", ContextSafetyReserveTokens),
                                ("maximum_context_tokens", _options.MaximumContextTokens),
                                ("tool_names", actionTools.Select(static tool => tool.Name))));
                            throw new InvalidOperationException(
                                "Source-backed action request remains outside the measured "
                                + $"context budget after emergency compaction: input={measuredInputTokens}, "
                                + $"context={_options.MaximumContextTokens}.");
                        }
                    }
                    try
                    {
                        if (useStructuredCandidateWriter)
                        {
                            freshWriterOutputNeedsSemanticReview = true;
                            structuredCandidateWriterTurn = true;
                            StructuredAssignmentRevisionExecution? revision = null;
                            StructuredCandidateWriterExecution? writerExecution = null;
                            if (pendingStructuredAssignmentReview is not null
                                && pendingStructuredAssignmentDraft is not null)
                            {
                                revision =
                                    await CompleteStructuredAssignmentRevisionAsync(
                                        intake,
                                        bundle,
                                        pendingStructuredAssignmentReview,
                                        pendingStructuredAssignmentDraft,
                                        selectableEvidenceIds,
                                        structuredRejectedEvidenceByCell,
                                        ct)
                                    .ConfigureAwait(false);
                                completion = revision.Completion;
                                AddTrace(traces, Trace(
                                    traceId,
                                    ref traceSequence,
                                    SourceBackedPipelineStep.Writer,
                                    "source_backed_agent_v2.structured_assignment_revision.patch_completed",
                                    ("turn", turn),
                                    ("protocol_valid", revision.ProtocolValid),
                                    ("revised_cells", revision.RevisedCellCount),
                                    ("available_candidates", revision.AvailableCandidateCount),
                                    ("raw_output", revision.RawOutput),
                                    ("failure_reason", revision.FailureReason),
                                    ("attempts", revision.Attempts),
                                    ("context_recovery_used", revision.ContextRecoveryUsed),
                                    ("input_tokens_exact", revision.ExactInputTokens),
                                    ("decision_source", "llm_semantic_assignment_patch")));
                            }
                            else
                            {
                                writerExecution =
                                    await CompleteStructuredCandidateWriterAsync(
                                            intake,
                                            bundle,
                                            selectableEvidenceIds,
                                            semanticRowHeader,
                                            semanticRowLabels,
                                            semanticColumnLabels,
                                            semanticColumnRoles,
                                            MergeAgentFeedback(
                                                semanticReviewFeedback,
                                                transientProtocolFeedback),
                                            ct)
                                        .ConfigureAwait(false);
                                completion = writerExecution.Completion;
                                if (TryApplyStructuredCandidateWriterEvidenceRequest(
                                        writerExecution, observedCitableSourceCount, turn,
                                        ref candidatePoolTargetCount,
                                        ref candidateCollectionOpen, ref selectionOnlyNextTurn,
                                        ref directWriterRequested, ref semanticReviewFeedback,
                                        ref maximumRunTurns, maximumRunTurnCeiling,
                                        traces, traceId, ref traceSequence))
                                    continue;
                            }
                            IReadOnlyList<string> structuredWriterSelectionIds =
                                Array.Empty<string>();
                            var structuredWriterFailureReason =
                                revision?.FailureReason
                                ?? writerExecution?.FailureReason
                                ?? string.Empty;
                            var structuredWriterProtocolValid = revision?.ProtocolValid
                                                                ?? writerExecution?.ProtocolValid
                                                                ?? true;
                            if (structuredWriterProtocolValid)
                            {
                                structuredWriterProtocolValid =
                                    TryValidateStructuredCandidateWriterCompletion(
                                        completion,
                                        bundle,
                                        selectableEvidenceIds,
                                        requiredEvidenceCount,
                                        out structuredWriterSelectionIds,
                                        out structuredWriterFailureReason);
                            }
                            structuredCandidateWriterProtocolError =
                                structuredWriterProtocolValid
                                    ? null
                                    : revision?.FailureReason
                                      ?? structuredWriterFailureReason;
                            if (structuredWriterProtocolValid)
                            {
                                activeSemanticSelectionIds =
                                    structuredWriterSelectionIds;
                                activeSemanticSelectionLayout =
                                    canonicalWorkspaceSelectionLayout;
                                explicitSemanticSelectionActive = true;
                                completion = completion with
                                {
                                    Content = NormalizeStructuredCandidateWriterLabels(
                                        completion.Content,
                                        bundle,
                                        structuredWriterSelectionIds.ToHashSet(
                                            StringComparer.OrdinalIgnoreCase),
                                        out var normalizedStructuredWriterCells)
                                };
                                AddTrace(traces, Trace(
                                    traceId,
                                    ref traceSequence,
                                    SourceBackedPipelineStep.SourceVerifier,
                                    "source_backed_agent_v2.structured_candidate_writer.labels_normalized",
                                    ("turn", turn),
                                    ("normalized_cells", normalizedStructuredWriterCells),
                                    ("decision_source", "mechanical_source_contract")));
                            }
                            completedByFastEvidenceWriter = true;
                            TraceStructuredCandidateWriterCompletion(
                                traces, traceId, ref traceSequence, turn,
                                structuredWriterProtocolValid,
                                structuredWriterFailureReason,
                                semanticRowLabels.Count, semanticColumnLabels.Length,
                                structuredWriterSelectionIds, completion, writerExecution);
                        }
                        else
                        {
                            (completion, candidateAuditExecution) =
                                await CompleteActionOrCandidateAuditAsync(
                                    dedicatedCandidateAuditTurn,
                                    intake,
                                    semanticPlan,
                                    semanticCandidateObjectType,
                                    semanticCandidateEligibilityRule,
                                    semanticRowHeader,
                                    semanticRowLabels,
                                    semanticColumnLabels,
                                    collectionCandidates, actionMessages,
                                    actionTools, effectiveActionMaxTokens,
                                    requireActionToolCall, ct)
                                    .ConfigureAwait(false);
                            if (!dedicatedCandidateAuditTurn
                                && candidateCollectionOpen)
                            {
                                researchTransitionExecution =
                                    CompleteResearchTransition(completion);
                                completion = researchTransitionExecution.Completion;
                                if (researchTransitionExecution.Attempted)
                                {
                                    AddTrace(traces, Trace(
                                        traceId,
                                        ref traceSequence,
                                        SourceBackedPipelineStep.Planner,
                                        "source_backed_agent_v2.research_transition.strategy_completed",
                                        ("turn", turn),
                                        ("action_id", researchTransitionExecution.ActionId),
                                        ("protocol_valid", researchTransitionExecution.ProtocolValid),
                                        ("llm_calls", researchTransitionExecution.LlmCallCount),
                                        ("failure_reason", researchTransitionExecution.FailureReason),
                                        ("decision_source", "llm_semantic_strategy")));
                                }
                            }
                        }
                        if (compactSingleFollowUpTurn)
                        {
                            completion = ApplyCompactSingleFollowUpAdjustment(
                                completion, intake, bundle, traces, traceId,
                                ref traceSequence, turn);
                        }
                    }
                    catch (SourceBackedLlmBudgetExceededException ex) when (
                        string.Equals(
                            ex.Reason,
                            "terminal_budget_only",
                            StringComparison.Ordinal)
                        && !semanticSelectionDecisionTurn
                        && !semanticYieldTerminalDecisionTurn)
                    {
                        if (candidateCollectionOpen
                            && UsesResearchTransitionTools(actionTools)
                            && !compactCandidateCollectionForContextRecovery
                            && !ex.Snapshot.NormalBudgetClosed)
                        {
                            // A large request can fail admission while a smaller
                            // one still fits. Keep the same tools and output limit;
                            // a second refusal uses the existing terminal path.
                            compactCandidateCollectionForContextRecovery = true;
                            AddTrace(traces, Trace(
                                traceId, ref traceSequence,
                                SourceBackedPipelineStep.IterationController,
                                "source_backed_agent_v2.cumulative_budget.research_compaction_requested",
                                ("turn", turn),
                                ("input_tokens", exactInputTokens),
                                ("reserved_output_tokens", effectiveActionMaxTokens),
                                ("charged_tokens", ex.Snapshot.ChargedTokens),
                                ("decision_source", "mechanical_budget_contract")));
                            continue;
                        }
                        TryActivateCumulativeBudgetFinalization(
                            hasMechanicallyFinalizableEvidence,
                            executedRequests.Count,
                            turn,
                            pendingSemanticCandidateIds,
                            ref candidateCollectionOpen,
                            ref selectionOnlyNextTurn,
                            ref decisionOnlyNextTurn,
                            ref flatEvidenceGapTerminalDecisionNextTurn,
                            traces,
                            traceId,
                            ref traceSequence,
                            force: true);
                        cumulativeBudgetRestartTurn = true;
                        break;
                    }
                    catch (HttpRequestException ex) when (
                        !contextRecoveryAttempted
                        && !semanticSelectionDecisionTurn
                        && IsContextWindowExceeded(ex))
                    {
                        contextRecoveryAttempted = true;
                        compactCandidateCollectionForContextRecovery |= candidateCollectionOpen;
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            MergeAgentFeedback(
                                semanticReviewFeedback,
                                transientProtocolFeedback),
                            emergencyContextRecovery: true,
                            includeEvidenceDetails: !candidateCollectionOpen);
                        messages.Add(SourceBackedAgentMessage.User(
                            BuildContextRecoveryActionMessage()));
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.context.recovery_requested",
                            ("turn", turn),
                            ("reason", "request_context_overflow"),
                            ("maximum_context_tokens", _options.MaximumContextTokens)));
                        continue;
                    }
                    if (!contextRecoveryAttempted
                        && !semanticSelectionDecisionTurn
                        && IsTruncatedToolProtocolCompletion(completion))
                    {
                        contextRecoveryAttempted = true;
                        compactCandidateCollectionForContextRecovery |= candidateCollectionOpen;
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            MergeAgentFeedback(
                                semanticReviewFeedback,
                                transientProtocolFeedback),
                            emergencyContextRecovery: true,
                            includeEvidenceDetails: !candidateCollectionOpen);
                        messages.Add(SourceBackedAgentMessage.User(
                            BuildContextRecoveryActionMessage()));
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.context.recovery_requested",
                            ("turn", turn),
                            ("reason", "truncated_tool_protocol"),
                            ("prompt_tokens", completion.PromptTokens),
                            ("completion_tokens", completion.CompletionTokens),
                            ("maximum_context_tokens", _options.MaximumContextTokens)));
                        continue;
                    }
                    break;
                }
                if (cumulativeBudgetRestartTurn)
                {
                    turn--;
                    continue;
                }
                if (compactSingleFollowUpTurn)
                {
                    compactSingleFollowUpNextTurn = false;
                    yieldResolutionNextTurn = false;
                }
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.turn.completed",
                    ("turn", turn),
                    ("finish_reason", completion.FinishReason),
                    ("tool_calls", completion.ToolCalls.Count),
                    ("content_characters", completion.Content?.Length ?? 0),
                    ("content_evidence_ids", SourceContractVerifier.ExtractEvidenceIds(
                        completion.Content ?? string.Empty).Count),
                     ("input_tokens_exact", exactInputTokens),
                     ("prompt_tokens", completion.PromptTokens),
                     ("completion_tokens", completion.CompletionTokens),
                     ("server_cache_tokens", completion.ServerCacheTokens),
                     ("server_prompt_evaluated_tokens",
                         completion.ServerPromptTokensEvaluated),
                     ("server_prompt_ms", completion.ServerPromptMilliseconds),
                     ("server_predicted_tokens", completion.ServerPredictedTokens),
                      ("server_predicted_ms",
                           completion.ServerPredictedMilliseconds),
                      ("compact_follow_up", compactSingleFollowUpTurn)));
                AddCandidateAuditPerformanceTrace(
                    candidateAuditExecution,
                    traces,
                    traceId,
                    ref traceSequence,
                    turn);
                if (TryApplyCandidateCollectionAudit(
                        completion, collectionCandidates,
                        pendingSemanticCandidateIds, ref bundle, evidenceWorkspace,
                        semanticallyAuditedEvidenceIds,
                        semanticallyRejectedEvidenceIds,
                        observedEvidenceIdSet, observedEvidenceIds,
                         candidatePoolTargetCount,
                         semanticLayoutDimensions is null,
                         messages, intake, semanticPlan,
                         semanticRowLabels, semanticColumnLabels,
                        executedRequests, traces, traceId, ref traceSequence, turn,
                        ref candidateCollectionOpen, ref selectionOnlyNextTurn,
                        ref compactSingleFollowUpNextTurn,
                        ref yieldResolutionNextTurn,
                        ref semanticYieldResolutionPending,
                        ref semanticYieldResolutionContinuationCount,
                        ref semanticYieldResolutionContinuationLimit,
                        maximumRunTurnCeiling, ref maximumRunTurns,
                        ref semanticReviewFeedback,
                        candidateAuditExecution?.LlmCallCount ?? 1,
                        candidateAuditExecution?.CandidateDecisionCount ?? 0,
                        candidateAuditExecution?.ProtocolRepairCount ?? 0,
                        candidateAuditExecution?.LabelReviewLlmCallCount ?? 0,
                        candidateAuditExecution?.ElapsedMilliseconds,
                        candidateAuditExecution?.Decision))
                    continue;
                if (_options.SeparateActionAndWriter
                    && !structuredCandidateWriterTurn
                    && completion.ToolCalls.Count == 0)
                {
                    decisionOnlyNextTurn = false;
                    if (!hasCitableEvidence && !finalizationTurn)
                    {
                        if (HandleNoCitableActionDecision(
                                completion.Content, bundle, consecutiveDocumentaryNoProgressTurns,
                                noCitableActionDecisionProgress, messages, traces, traceId,
                                ref traceSequence, turn, intake.Language,
                                out var terminalNote))
                        {
                            semanticReviewReasons = new[] { terminalNote };
                            break;
                        }
                        continue;
                    }
                    if (requiredAtomicEvidenceCount is > 0
                        && observedCitableSourceCount < requiredAtomicEvidenceCount.Value
                        && !finalizationTurn)
                    {
                        messages.Add(SourceBackedAgentMessage.User(
                            BuildAtomicEvidenceCountActionRepairMessage(
                                requiredAtomicEvidenceCount.Value,
                                observedCitableSourceCount)));
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.SourceVerifier,
                            "source_backed_agent_v2.action.atomic_evidence_count_rejected",
                            ("turn", turn),
                            ("required", requiredAtomicEvidenceCount.Value),
                            ("observed", observedCitableSourceCount)));
                        continue;
                    }
                    if (requireEvidenceSelection
                        && requiredAtomicEvidenceCount is > 0)
                    {
                        if (observedCitableSourceCount
                            < requiredAtomicEvidenceCount.Value)
                        {
                            if (finalizationTurn)
                            {
                                semanticReviewReasons = new[]
                                {
                                    "Décision finale de l'orchestrateur LLM: "
                                    + TrimPromptValue(
                                        completion.Content,
                                        300),
                                    "Le contrat mécanique exige "
                                    + requiredAtomicEvidenceCount.Value
                                    + " sources visibles distinctes, mais "
                                    + observedCitableSourceCount
                                    + " seulement ont été observées."
                                };
                                AddTrace(traces, Trace(
                                    traceId,
                                    ref traceSequence,
                                    SourceBackedPipelineStep.EvidenceJudge,
                                    "source_backed_agent_v2.insufficient_evidence.declared",
                                    ("turn", turn),
                                    ("required",
                                        requiredAtomicEvidenceCount.Value),
                                    ("observed",
                                        observedCitableSourceCount),
                                    ("decision",
                                        TrimPromptValue(
                                            completion.Content,
                                            300))));
                                break;
                            }
                            continue;
                        }
                        selectionOnlyNextTurn = true;
                        transientProtocolFeedback =
                            BuildEvidenceSelectionRequiredMessage(
                                requiredAtomicEvidenceCount.Value,
                                enableWorkspaceWriterHandoff,
                                canonicalWorkspaceSelectionLayout is not null);
                        messages.Add(SourceBackedAgentMessage.User(
                            transientProtocolFeedback));
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.action.evidence_selection_requested",
                            ("turn", turn),
                            ("required", requiredAtomicEvidenceCount.Value),
                            ("observed", observedCitableSourceCount),
                            ("reason", "writer_handoff_requires_explicit_llm_selection")));
                        continue;
                    }
                    var actionDecision = completion.Content;
                    if (requiredAtomicEvidenceCount is > 1)
                    {
                        selectionOnlyNextTurn = true;
                        semanticReviewFeedback = BuildSemanticSelectionContractRepairMessage(
                            requiredAtomicEvidenceCount.Value,
                            0,
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            "selection_tool_required");
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            semanticReviewFeedback);
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.SourceVerifier,
                            "source_backed_agent_v2.action.semantic_selection_rejected",
                            ("turn", turn),
                            ("required", requiredAtomicEvidenceCount.Value),
                            ("declared", 0),
                            ("unknown_evidence_ids", Array.Empty<string>())));
                        continue;
                    }
                    completion = await CompleteDedicatedWriterAsync(
                            messages, actionDecision, intake, bundle,
                            activeSemanticSelectionIds, atomicEvidenceMode, ct)
                        .ConfigureAwait(false);
                    freshWriterOutputNeedsSemanticReview = true;
                    AddWriterTrace(
                        traces,
                        traceId,
                        ref traceSequence,
                        turn,
                        completion,
                        directRevision: false);
                }
            }
            var semanticYieldTerminalSubmission =
                EvaluateSemanticYieldTerminalDecision(
                    completion,
                    semanticYieldTerminalDecisionTurn,
                    intake,
                    executedRequests,
                    bundle,
                    firstDraft,
                    latestDraft,
                    verification,
                    repaired,
                    semanticallyRejectedEvidenceIds,
                    traces,
                    traceId,
                    ref traceSequence,
                    turn,
                    consecutiveDocumentaryNoProgressTurns,
                    observedCitableSourceCount);
            if (semanticYieldTerminalSubmission.Submitted)
            {
                if (semanticYieldTerminalSubmission.Result is not null)
                    return semanticYieldTerminalSubmission.Result;
                semanticYieldTerminalProtocolAttemptCount++;
                messages.Add(SourceBackedAgentMessage.Assistant(
                    completion.Content,
                    completion.ToolCalls));
                foreach (var call in completion.ToolCalls)
                {
                    messages.Add(SourceBackedAgentMessage.Tool(
                        call.Id,
                        call.Name,
                        SourceBackedAgentObservationCompactor.BuildProtocolError(
                            call.Name,
                            semanticYieldTerminalSubmission.FailureReason,
                            "Choisis clarification ou insufficiency et fournis un message concis.")));
                }
                transientProtocolFeedback =
                    "La décision terminale ne respecte pas son contrat; corrige uniquement ses deux champs.";
                compactSingleFollowUpNextTurn = true;
                yieldResolutionNextTurn = true;
                maximumRunTurns = Math.Min(
                    maximumRunTurns,
                    turn + (semanticYieldTerminalProtocolAttemptCount < 2 ? 1 : 0));
                if (semanticYieldTerminalProtocolAttemptCount >= 2)
                {
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.semantic_audit_zero_yield.terminal_protocol_exhausted",
                        ("turn", turn),
                        ("attempts", semanticYieldTerminalProtocolAttemptCount),
                        ("decision_source", "mechanical_protocol_budget")));
                }
                continue;
            }
            var insufficiencySubmission =
                EvaluateSourceInsufficiencySubmission(
                    completion,
                    yieldResolutionTurn,
                    intake,
                    executedRequests,
                    bundle,
                    firstDraft,
                    latestDraft,
                    verification,
                    repaired,
                    semanticallyRejectedEvidenceIds,
                    traces,
                    traceId,
                    ref traceSequence,
                    turn,
                    consecutiveDocumentaryNoProgressTurns,
                    observedCitableSourceCount);
            if (insufficiencySubmission.Submitted)
            {
                if (insufficiencySubmission.Result is not null)
                    return insufficiencySubmission.Result;
                messages.Add(SourceBackedAgentMessage.Assistant(
                    completion.Content,
                    completion.ToolCalls));
                foreach (var call in completion.ToolCalls)
                {
                    messages.Add(SourceBackedAgentMessage.Tool(
                        call.Id,
                        call.Name,
                        SourceBackedAgentObservationCompactor.BuildProtocolError(
                            call.Name,
                            insufficiencySubmission.FailureReason,
                            "Déclare l'insuffisance uniquement pendant une résolution de rendement, ou choisis une action documentaire autorisée.")));
                }
                transientProtocolFeedback =
                    "La déclaration d'insuffisance ne respecte pas son contrat; corrige-la ou poursuis la recherche.";
                if (yieldResolutionTurn)
                {
                    compactSingleFollowUpNextTurn = true;
                    yieldResolutionNextTurn = true;
                }
                continue;
            }
            var clarificationSubmission =
                EvaluateSourceBackedClarificationSubmission(
                    completion,
                    intake,
                    executedRequests,
                    bundle,
                    firstDraft,
                    latestDraft,
                    verification,
                    repaired,
                    semanticallyRejectedEvidenceIds,
                    traces,
                    traceId,
                    ref traceSequence,
                    turn,
                    observedEvidenceIds.Count);
            if (clarificationSubmission.Submitted)
            {
                if (clarificationSubmission.Result is not null)
                    return clarificationSubmission.Result;
                messages.Add(SourceBackedAgentMessage.Assistant(
                    completion.Content,
                    completion.ToolCalls));
                foreach (var call in completion.ToolCalls)
                {
                    messages.Add(SourceBackedAgentMessage.Tool(
                        call.Id,
                        call.Name,
                        SourceBackedAgentObservationCompactor.BuildProtocolError(
                            call.Name,
                            clarificationSubmission.FailureReason,
                            "La clarification doit etre l'unique action et n'est disponible qu'apres une observation du corpus. Corrige l'appel ou poursuis avec un outil documentaire.")));
                }
                transientProtocolFeedback =
                    "La clarification soumise ne respecte pas son contrat; corrige-la ou poursuis la recherche.";
                continue;
            }

            var semanticSelectionHandling =
                await ProcessSemanticSelectionSubmissionAsync(
                        completion, requiredAtomicEvidenceCount, requireEvidenceSelection,
                        allowAdaptiveFlatSelection, atomicEvidenceMode,
                        semanticColumnLabels, semanticRowHeader, semanticRowLabels, bundle,
                        selectionEligibleObservedEvidenceIds, semanticallyRejectedEvidenceIds,
                        observedEvidenceIdSet, observedEvidenceIds, pendingSemanticCandidateIds,
                        evidenceWorkspace, selectionOnlyNextTurn, semanticReviewFeedback,
                        traces, traceId, traceSequence, turn, lastInvalidSelectionProtocolKey,
                        consecutiveIdenticalInvalidSelections, maximumRunTurns,
                        maximumRunTurnCeiling, selectionProtocolRepairTurns, messages, intake,
                        semanticPlan, executedRequests, semanticallyAuditedEvidenceIds,
                        activeSemanticSelectionIds, activeSemanticSelectionLayout,
                        explicitSemanticSelectionActive, consecutiveDocumentaryNoProgressTurns, ct)
                    .ConfigureAwait(false);
            completion = semanticSelectionHandling.Completion;
            freshWriterOutputNeedsSemanticReview |= semanticSelectionHandling.FreshWriterOutputGenerated;
            activeSemanticSelectionIds = semanticSelectionHandling.ActiveSemanticSelectionIds;
            activeSemanticSelectionLayout = semanticSelectionHandling.ActiveSemanticSelectionLayout;
            explicitSemanticSelectionActive = semanticSelectionHandling.ExplicitSemanticSelectionActive;
            selectionOnlyNextTurn = semanticSelectionHandling.SelectionOnlyNextTurn;
            consecutiveDocumentaryNoProgressTurns =
                semanticSelectionHandling.ConsecutiveDocumentaryNoProgressTurns;
            semanticReviewFeedback = semanticSelectionHandling.SemanticReviewFeedback;
            traceSequence = semanticSelectionHandling.TraceSequence;
            lastInvalidSelectionProtocolKey = semanticSelectionHandling.LastInvalidSelectionProtocolKey;
            consecutiveIdenticalInvalidSelections =
                semanticSelectionHandling.ConsecutiveIdenticalInvalidSelections;
            maximumRunTurns = semanticSelectionHandling.MaximumRunTurns;
            selectionProtocolRepairTurns = semanticSelectionHandling.SelectionProtocolRepairTurns;
            if (semanticSelectionHandling.AdaptiveFlatSelectionAccepted)
                candidateCollectionOpen = false;
            if (semanticSelectionHandling.ContinueRun)
                continue;

            if (completion.ToolCalls.Count > 0)
            {
                var toolBatchExecution =
                    await ExecuteDocumentaryToolBatchAsync(
                            completion, messages, intake, cumulativeResults, bundle,
                            evidenceWorkspace, observedEvidenceIdSet, observedEvidenceIds,
                            semanticallyRejectedEvidenceIds, traces, traceId, traceSequence,
                            turn, enableWorkspaceWriterHandoff, requiredAtomicEvidenceCount,
                            activeSemanticSelectionIds, activeSemanticSelectionLayout,
                            canonicalWorkspaceSelectionLayout, directWriterRequested,
                            selectionOnlyNextTurn, decisionOnlyNextTurn,
                            consecutiveDocumentaryNoProgressTurns,
                            resolvedNavigationEvidenceIds, executedRequests, executedCallKeys,
                            totalToolCalls, candidateCollectionOpen,
                            semanticCandidateStrategyOutcome, hasCitableEvidence,
                            transientProtocolFeedback, semanticallyAuditedEvidenceIds,
                            pendingSemanticCandidateIds, ct)
                        .ConfigureAwait(false);
                bundle = toolBatchExecution.Bundle;
                activeSemanticSelectionIds = toolBatchExecution.ActiveSemanticSelectionIds;
                activeSemanticSelectionLayout = toolBatchExecution.ActiveSemanticSelectionLayout;
                directWriterRequested = toolBatchExecution.DirectWriterRequested;
                selectionOnlyNextTurn = toolBatchExecution.SelectionOnlyNextTurn;
                decisionOnlyNextTurn = toolBatchExecution.DecisionOnlyNextTurn;
                consecutiveDocumentaryNoProgressTurns =
                    toolBatchExecution.ConsecutiveDocumentaryNoProgressTurns;
                transientProtocolFeedback = toolBatchExecution.TransientProtocolFeedback;
                totalToolCalls = toolBatchExecution.TotalToolCalls;
                traceSequence = toolBatchExecution.TraceSequence;
                var completedUsefulToolCall = toolBatchExecution.CompletedUsefulToolCall;
                var duplicateCallCount = toolBatchExecution.DuplicateCallCount;
                var zeroNewEvidenceCallCount = toolBatchExecution.ZeroNewEvidenceCallCount;
                var workspaceReadyForWriter = toolBatchExecution.WorkspaceReadyForWriter;
                var documentaryToolCallCount = toolBatchExecution.DocumentaryToolCallCount;
                var workspaceNoOpCallCount = toolBatchExecution.WorkspaceNoOpCallCount;
                var newlyObservedEvidenceIds = toolBatchExecution.NewlyObservedEvidenceIds;

                if (compactSingleFollowUpTurn
                    && semanticYieldResolutionPending
                    && documentaryToolCallCount > 0)
                {
                    semanticYieldResolutionContinuationCount++;
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.semantic_audit_zero_yield.continuation_recorded",
                        ("turn", turn),
                        ("continuations", semanticYieldResolutionContinuationCount),
                        ("maximum_continuations",
                            semanticYieldResolutionContinuationLimit),
                        ("decision_source", "llm_orchestrator")));
                }

                maximumRunTurns = ReserveProductiveRetrievalFollowUp(
                    traces, traceId, ref traceSequence, turn,
                    candidateCollectionOpen, pendingSemanticCandidateIds.Count,
                    newlyObservedEvidenceIds, maximumRunTurns,
                    maximumRunTurnCeiling);

                if (completedUsefulToolCall)
                {
                    transientProtocolFeedback = null;
                    consecutiveDocumentaryNoProgressTurns = 0;
                    selectionOnlyNextTurn = false;
                    decisionOnlyNextTurn = false;
                    yieldResolutionNextTurn = false;
                    if (!workspaceReadyForWriter)
                    {
                        activeSemanticSelectionIds = null;
                        activeSemanticSelectionLayout = null;
                    }
                    if (semanticNeedsMoreEvidence)
                        semanticReviewDecision = null;
                    CompactWorkingMessages(
                        messages,
                        intake,
                        semanticPlan,
                        bundle,
                        observedEvidenceIds,
                        executedRequests,
                        evidenceWorkspace,
                        semanticReviewFeedback,
                        includeEvidenceDetails: !candidateCollectionOpen);
                    var fastEvidenceReviewCandidateIds =
                        BuildFastEvidenceReviewCandidateInput(
                            bundle,
                            newlyObservedEvidenceIds,
                            fastEvidenceReviewCarryForwardCandidateIds,
                            semanticallyRejectedEvidenceIds);
                    var maximumFastEvidenceReviewCandidates =
                        ResolveFastEvidenceReviewCandidateLimit(
                            intake,
                            fastEvidenceReviewCarryForwardCandidateIds.Count);
                    var carriedCandidateIdsBeforeReview =
                        fastEvidenceReviewCarryForwardCandidateIds
                            .Where(id =>
                                fastEvidenceReviewCandidateIds.Contains(
                                    id,
                                    StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    var fastEvidenceReviewSignature =
                        BuildFastEvidenceReviewSignature(
                            bundle,
                            fastEvidenceReviewCandidateIds,
                            semanticallyRejectedEvidenceIds,
                            maximumFastEvidenceReviewCandidates);
                    if (compactSingleFollowUpTurn
                        && fastEvidenceReviewSignature.Length == 0)
                    {
                        compactSingleFollowUpNextTurn = true;
                    }
                    if (compactSingleFollowUpTurn
                        && semanticYieldResolutionPending
                        && pendingSemanticCandidateIds.Count == 0)
                    {
                        compactSingleFollowUpNextTurn = true;
                        yieldResolutionNextTurn = true;
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.semantic_audit_zero_yield.resolution_persisted",
                            ("turn", turn),
                            ("new_evidence", newlyObservedEvidenceIds.Count),
                            ("pending_candidates", pendingSemanticCandidateIds.Count),
                            ("decision_source", "mechanical_state_contract")));
                    }
                    if (fastEvidenceReviewSignature.Length > 0
                        && !string.Equals(
                            fastEvidenceReviewSignature,
                            lastFastEvidenceReviewSignature,
                            StringComparison.Ordinal)
                        && CanAttemptFastEvidenceReview(
                            intake,
                            requireEvidenceSelection,
                            requiredAtomicEvidenceCount,
                            semanticLayoutDimensions)
                        && observedEvidenceIds.Count > 0)
                    {
                        lastFastEvidenceReviewSignature =
                            fastEvidenceReviewSignature;
                        var fastReviewAttempt = await ReviewInitialEvidenceWithinCumulativeBudgetAsync(intake, semanticPlan, bundle, fastEvidenceReviewCandidateIds,
                            semanticallyRejectedEvidenceIds, maximumFastEvidenceReviewCandidates, ct).ConfigureAwait(false);
                        if (fastReviewAttempt.TerminalBudgetOnly)
                        {
                            if (fastReviewAttempt.Review is not null) bundle = PreserveTerminalFastReviewSemanticSelection(fastReviewAttempt.Review, bundle, semanticallyAuditedEvidenceIds, semanticallyRejectedEvidenceIds);
                            var hasCurrentFinalizableEvidence = HasCurrentMechanicallyFinalizableEvidence(
                                bundle, observedEvidenceIds, candidateAuditEnabled, semanticallyAuditedEvidenceIds,
                                semanticallyRejectedEvidenceIds, requiredAtomicEvidenceCount, atomicEvidenceMode);
                            TryActivateCumulativeBudgetFinalization(
                                hasCurrentFinalizableEvidence, executedRequests.Count, turn,
                                pendingSemanticCandidateIds, ref candidateCollectionOpen, ref selectionOnlyNextTurn,
                                ref decisionOnlyNextTurn, ref flatEvidenceGapTerminalDecisionNextTurn,
                                traces, traceId, ref traceSequence, force: true);
                            turn--;
                            continue;
                        }
                        var fastReview = fastReviewAttempt.Review!;
                        fastEvidenceReviewCarryForwardCandidateIds =
                            fastReview.ProtocolValid
                                ? Array.Empty<string>()
                                : (fastReview.PresentedCandidateIds
                                   ?? Array.Empty<string>())
                                .Where(id =>
                                    !semanticallyRejectedEvidenceIds.Contains(
                                        id))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Take(6)
                                .ToArray();
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.AnswerAdequacyJudge,
                            "source_backed_agent_v2.fast_evidence_review.completed",
                            ("turn", turn),
                            ("decision", fastReview.Decision),
                            ("next_capability",
                                fastReview.NextCapability),
                            ("protocol_valid", fastReview.ProtocolValid),
                            ("anchor_verified",
                                fastReview.AnchorVerified),
                            ("selected_evidence_ids", fastReview.EvidenceIds),
                            ("lead_evidence_ids",
                                fastReview.LeadEvidenceIds),
                            ("anchor_excerpt",
                                TrimPromptValue(
                                    fastReview.AnchorExcerpt,
                                    180)),
                            ("missing", fastReview.Missing),
                            ("assessment", fastReview.Assessment),
                            ("prompt_characters", fastReview.PromptCharacters),
                            ("evidence_context_characters", fastReview.EvidenceContextCharacters),
                            ("candidate_count", fastReview.CandidateCount),
                            ("candidate_input_count",
                                fastEvidenceReviewCandidateIds.Count),
                            ("maximum_candidate_count",
                                maximumFastEvidenceReviewCandidates),
                            ("presented_candidate_ids", fastReview.PresentedCandidateIds ?? Array.Empty<string>()),
                            ("presented_evidence_ids",
                                fastReview.PresentedEvidenceIds
                                ?? Array.Empty<string>()),
                            ("carried_candidate_ids_before_review",
                                carriedCandidateIdsBeforeReview),
                            ("carried_candidate_ids_after_review",
                                fastEvidenceReviewCarryForwardCandidateIds),
                            ("answer_redirected_to_writer", fastReview.AnswerMechanicallyRedirectedToWriter),
                            ("source_window_item_count", fastReview.SourceWindowItemCount),
                            ("evidence_pool_budget", fastReview.EvidencePoolBudget),
                            ("evidence_pool_eligible_item_count", fastReview.EvidencePoolEligibleItemCount),
                            ("evidence_pool_truncated_item_count", fastReview.EvidencePoolTruncatedItemCount),
                            ("answer_adequacy", fastReview.AnswerAdequacy),
                            ("requested_deliverable_complete", fastReview.RequestedDeliverableComplete),
                            ("missing_user_input_prevents_unique_result", fastReview.MissingUserInputPreventsUniqueResult),
                            ("visible_context_evidence_id", fastReview.VisibleContextEvidenceId),
                            ("ms", fastReview.ElapsedMilliseconds),
                            ("single_selection_scope_decision",
                                fastReview.SingleSelectionScopeDecision),
                            ("single_selection_scope_basis", fastReview.SingleSelectionScopeBasis),
                            ("single_selection_scope_reason", fastReview.SingleSelectionScopeReason),
                            ("single_selection_scope_protocol_valid",
                                fastReview.SingleSelectionScopeProtocolValid),
                            ("single_selection_scope_ms",
                                fastReview.SingleSelectionScopeElapsedMilliseconds),
                            ("single_selection_scope_normalized",
                                fastReview.SingleSelectionScopeMechanicallyNormalized),
                            ("protocol_error", fastReview.Completion.ProtocolError),
                            ("raw_content",
                                fastReview.ProtocolValid ? null
                                    : TrimPromptValue(
                                        fastReview.Completion.ProtocolRawOutput ?? fastReview.Completion.Content,
                                        700)),
                            ("finish_reason",
                                fastReview.Completion.FinishReason),
                            ("prompt_tokens", fastReview.Completion.PromptTokens),
                            ("completion_tokens", fastReview.Completion.CompletionTokens),
                            ("server_cache_tokens",
                                fastReview.Completion.ServerCacheTokens),
                            ("server_prompt_evaluated_tokens",
                                fastReview.Completion.ServerPromptTokensEvaluated),
                            ("server_prompt_ms",
                                fastReview.Completion.ServerPromptMilliseconds),
                            ("server_predicted_tokens",
                                fastReview.Completion.ServerPredictedTokens),
                            ("server_predicted_ms",
                                    fastReview.Completion.ServerPredictedMilliseconds)));
                        if (fastReview.SemanticResolutionWriterReviewAttempted) AddSemanticResolutionTrace(traces, traceId, ref traceSequence, turn, fastReview);
                        if ((fastReview.SemanticAnswerTransactionAttempted || fastReview.SemanticResolutionWriterReviewAttempted) && !fastReview.ProtocolValid)
                        {
                            completion = fastReview.Completion;
                            break;
                        }
                        if (!string.IsNullOrWhiteSpace(
                                fastReview.ClarificationQuestion))
                        {
                            var clarification =
                                new SourceBackedClarificationDecision(
                                    fastReview.ClarificationQuestion,
                                    Array.Empty<string>(),
                                    fastReview.ClarificationQuestion,
                                    "other");
                            AddTrace(traces, Trace(
                                traceId,
                                ref traceSequence,
                                SourceBackedPipelineStep.EvidenceJudge,
                                "source_backed_agent_v2.clarification.requested",
                                ("turn", turn),
                                ("observed_candidates",
                                    fastReview.CandidateCount),
                                ("ambiguity_kind", "other"),
                                ("option_count", 0),
                                ("execution_impact",
                                    fastReview.ClarificationQuestion),
                                ("decision_source",
                                    "llm_evidence_judge")));
                            return BuildResult(
                                intake,
                                executedRequests,
                                bundle,
                                firstDraft,
                                latestDraft,
                                verification,
                                repaired,
                                semanticAccepted: false,
                                semanticallyRejectedEvidenceIds,
                                new[]
                                {
                                    fastReview.ClarificationQuestion
                                },
                                traces.ToArray(),
                                clarification);
                        }
                        if (fastReview.Ready)
                        {
                            consecutiveFastEvidenceResearchZeroYieldReviews = 0;
                            explicitSemanticSelectionActive = true;
                            compactFollowUpLeadEvidenceIds = Array.Empty<string>();
                            activeSemanticSelectionIds = fastReview.EvidenceIds;
                            activeSemanticSelectionLayout = null;
                            if (fastReview.SemanticResolutionWriterReviewAttempted)
                            {
                                semanticResolutionWriterReviewActive = true;
                                inlineFastAnswerRequiresIndependentSemanticReview = true;
                                inlineFastAnswerDirectRevisionSignatures.Clear();
                            }
                            if (!fastReview.SemanticResolutionWriterReviewAttempted && !string.IsNullOrWhiteSpace(fastReview.Answer))
                            {
                                inlineFastAnswerRequiresIndependentSemanticReview = !fastReview.AnswerSemanticallyFinal;
                                inlineFastAnswerDirectRevisionSignatures.Clear();
                            }
                            completion = await CompleteFastEvidenceWriterAsync(
                                    fastReview, messages, intake, bundle, activeSemanticSelectionIds, atomicEvidenceMode,
                                    traces, traceId, ref traceSequence, turn, ct)
                                .ConfigureAwait(false);
                            freshWriterOutputNeedsSemanticReview |= string.IsNullOrWhiteSpace(fastReview.Answer);
                            AddWriterTrace(traces, traceId, ref traceSequence,
                                turn, completion, directRevision: false);
                            completedByFastEvidenceWriter = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(fastReview.Missing))
                        {
                            if (fastReview.ProtocolValid)
                            {
                                if (TryBlockSemanticResolutionContinuation(fastReview, totalToolCalls, traces, traceId, ref traceSequence, turn)) break;
                                var fastReviewRequestsResearch = string.Equals(
                                    fastReview.NextCapability,
                                    "research",
                                    StringComparison.Ordinal);
                                if (fastReviewRequestsResearch)
                                {
                                    consecutiveFastEvidenceResearchZeroYieldReviews++;
                                    AddTrace(traces, Trace(
                                        traceId,
                                        ref traceSequence,
                                        SourceBackedPipelineStep.IterationController,
                                        "source_backed_agent_v2.fast_evidence_review.zero_yield.observed",
                                        ("turn", turn),
                                        ("consecutive_research_reviews",
                                            consecutiveFastEvidenceResearchZeroYieldReviews),
                                        ("new_evidence",
                                            newlyObservedEvidenceIds.Count),
                                        ("decision_source",
                                            "llm_evidence_judge")));
                                }
                                else
                                {
                                    consecutiveFastEvidenceResearchZeroYieldReviews = 0;
                                }
                                SourceBackedAgentToolCall?
                                    leadContextAction = null;
                                if (string.Equals(
                                        fastReview.NextCapability,
                                        "documents_context",
                                        StringComparison.Ordinal)
                                    && TryBuildLeadContextAction(
                                        bundle,
                                        fastReview.LeadEvidenceIds,
                                        out var resolvedLeadContextAction))
                                {
                                    leadContextAction =
                                        resolvedLeadContextAction;
                                }
                                if (leadContextAction is not null)
                                {
                                    routerInitialActions =
                                        new[] { leadContextAction };
                                    plannedFirstAction = null;
                                    initialActionDecisionSource =
                                        "llm_evidence_judge";
                                    compactFollowUpLeadEvidenceIds =
                                        Array.Empty<string>();
                                    compactSingleFollowUpNextTurn = false;
                                    AddTrace(traces, Trace(
                                        traceId,
                                        ref traceSequence,
                                        SourceBackedPipelineStep.IterationController,
                                        "source_backed_agent_v2.fast_evidence_review.next_action_scheduled",
                                        ("turn", turn),
                                        ("decision_source",
                                            "llm_evidence_judge"),
                                        ("capability",
                                            fastReview.NextCapability),
                                        ("lead_evidence_ids",
                                            fastReview.LeadEvidenceIds),
                                        ("tool",
                                            leadContextAction!.Name),
                                        ("arguments",
                                            TrimPromptValue(
                                                leadContextAction.Arguments
                                                    .GetRawText(),
                                                800))));
                                }
                                else
                                {
                                    compactFollowUpLeadEvidenceIds =
                                        fastReview.LeadEvidenceIds;
                                    compactSingleFollowUpNextTurn = true;
                                }
                                if (!string.Equals(
                                        fastReview.NextCapability,
                                        "documents_context",
                                        StringComparison.Ordinal))
                                {
                                    foreach (var rejectedEvidenceId
                                             in fastReview.PresentedEvidenceIds
                                                ?? fastEvidenceReviewCandidateIds)
                                    {
                                        semanticallyRejectedEvidenceIds.Add(
                                            rejectedEvidenceId);
                                        pendingSemanticCandidateIds.Remove(
                                            rejectedEvidenceId);
                                    }
                                }
                                if (fastReviewRequestsResearch
                                    && consecutiveFastEvidenceResearchZeroYieldReviews >= 2)
                                {
                                    semanticYieldResolutionPending = true;
                                    yieldResolutionNextTurn = true;
                                    compactSingleFollowUpNextTurn = true;
                                    semanticYieldResolutionContinuationCount = 0;
                                    semanticYieldResolutionContinuationLimit = 0;
                                    compactFollowUpLeadEvidenceIds =
                                        Array.Empty<string>();
                                    AddTrace(traces, Trace(
                                        traceId,
                                        ref traceSequence,
                                        SourceBackedPipelineStep.IterationController,
                                        "source_backed_agent_v2.fast_evidence_review.zero_yield.resolution_requested",
                                        ("turn", turn),
                                        ("consecutive_research_reviews",
                                            consecutiveFastEvidenceResearchZeroYieldReviews),
                                        ("executed_requests",
                                            executedRequests.Count),
                                        ("rejected_evidence",
                                            semanticallyRejectedEvidenceIds.Count),
                                        ("rejected_evidence_ids",
                                            semanticallyRejectedEvidenceIds),
                                        ("decision_source",
                                            "llm_evidence_judge+mechanical_budget_contract")));
                                }
                            }
                            else if (fastReview.LeadEvidenceIds.Count > 0)
                            {
                                compactFollowUpLeadEvidenceIds =
                                    fastReview.LeadEvidenceIds;
                                compactSingleFollowUpNextTurn = true;
                            }
                            semanticReviewFeedback =
                                "REVUE DE SUFFISANCE DU PREMIER LOT: "
                                + TrimPromptValue(
                                    fastReview.Missing,
                                    240);
                            messages.Add(SourceBackedAgentMessage.User(
                                semanticReviewFeedback));
                        }
                    }
                }
                else if (documentaryToolCallCount
                             + workspaceNoOpCallCount
                         > 0
                         && !completedUsefulToolCall
                         && duplicateCallCount + zeroNewEvidenceCallCount
                         == documentaryToolCallCount
                            + workspaceNoOpCallCount)
                {
                    consecutiveDocumentaryNoProgressTurns++;
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.documentary_progress.none",
                        ("turn", turn),
                        ("attempted_calls",
                            documentaryToolCallCount
                            + workspaceNoOpCallCount),
                        ("duplicate_calls", duplicateCallCount),
                        ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                        ("consecutive_no_progress_turns",
                            consecutiveDocumentaryNoProgressTurns),
                        ("observed", observedCitableSourceCount),
                        ("required",
                            requiredAtomicEvidenceCount.GetValueOrDefault())));
                    if (compactSingleFollowUpTurn
                        && semanticYieldResolutionPending)
                    {
                        compactSingleFollowUpNextTurn = true;
                        yieldResolutionNextTurn = true;
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.semantic_audit_zero_yield.resolution_persisted",
                            ("turn", turn),
                            ("new_evidence", 0),
                            ("pending_candidates", pendingSemanticCandidateIds.Count),
                            ("decision_source", "mechanical_state_contract")));
                    }
                    ApplyDocumentaryNoProgressTransition(
                        intake, semanticPlan, bundle, observedEvidenceIds,
                        executedRequests, evidenceWorkspace, messages,
                        traces, traceId, ref traceSequence, turn,
                        requiredAtomicEvidenceCount, requireEvidenceSelection,
                        hasCitableEvidence, observedCitableSourceCount,
                        consecutiveDocumentaryNoProgressTurns,
                        duplicateCallCount, zeroNewEvidenceCallCount,
                        flatEvidenceAdequacyGapActive,
                        flatEvidenceAdequacyIncumbentEvidenceIds,
                        ref candidateCollectionOpen,
                        ref selectionOnlyNextTurn,
                        ref decisionOnlyNextTurn,
                        ref compactSingleFollowUpNextTurn,
                        ref yieldResolutionNextTurn,
                        ref compactFollowUpLeadEvidenceIds,
                        ref semanticReviewFeedback,
                        ref transientProtocolFeedback,
                        ref flatEvidenceGapTerminalDecisionNextTurn);
                }
                if (!completedByFastEvidenceWriter)
                    continue;
            }
            latestDraft = BuildDraft(completion.Content);
            firstDraft ??= latestDraft;
            var selectedWriterVerification = VerifySelectedWriterDraft(
                latestDraft,
                bundle,
                intake,
                activeSemanticSelectionIds,
                atomicEvidenceMode,
                structuredCandidateWriterTurn);
            var writerCitedContextEvidenceBeyondSemanticSelection =
                selectedWriterVerification.CitedContextEvidenceBeyondSemanticSelection;
            verification = selectedWriterVerification.Verification;
            if (!string.IsNullOrWhiteSpace(
                    structuredCandidateWriterProtocolError ??= completion.ProtocolError))
            {
                verification = verification with
                {
                    IsValid = false,
                    Errors = verification.Errors.Concat(new[]
                    {
                        new SourceVerificationError(
                            structuredCandidateWriterProtocolError!,
                            "Le writer structure n'a pas respecte le contrat mecanique "
                            + "de cardinalite, d'identifiants ou d'unicite des sources.",
                            null,
                            SourceBackedPipelineStep.SourceVerifier)
                    }).ToArray()
                };
            }
            TraceVerifiedAnswer(verification, selectedWriterVerification, writerCitedContextEvidenceBeyondSemanticSelection, traces, traceId, ref traceSequence, turn);
            if (!string.IsNullOrWhiteSpace(structuredCandidateWriterProtocolError))
            {
                break;
            }
            if (verification.IsValid)
            {
                if (TryCompleteVerifiedAnswerWithoutFreshSemanticReview(
                        reviewDirectedRevisionCompletedThisTurn,
                        reviewDirectedRevisionExactClaimCountSatisfied,
                        explicitSemanticSelectionActive,
                        freshWriterOutputNeedsSemanticReview,
                        inlineFastAnswerRequiresIndependentSemanticReview,
                        writerCitedContextEvidenceBeyondSemanticSelection,
                        activeSemanticSelectionIds,
                        activeSemanticSelectionLayout,
                        intake, executedRequests, bundle, firstDraft, latestDraft,
                        verification, repaired, semanticallyRejectedEvidenceIds,
                        traces, traceId, ref traceSequence, turn,
                        out var verifiedResult))
                    return verifiedResult;

                using var cumulativeBudgetTerminalReviewScope =
                    SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
                var semanticReview = activeSemanticSelectionLayout is not null
                    ? await ReviewStructuredAssignmentsAsync(
                            intake,
                            semanticPlan,
                            latestDraft,
                            bundle,
                            observedEvidenceIds,
                            GetStructuredAssignmentRejectedCellIndexes(
                                pendingStructuredAssignmentReview),
                            ct)
                        .ConfigureAwait(false)
                    : await ReviewSemanticsAsync(
                            intake,
                            semanticPlan,
                            latestDraft,
                            bundle,
                            observedEvidenceIds,
                            ct)
                        .ConfigureAwait(false);
                semanticReviewReasons = semanticReview.Reasons;
                semanticReviewDecision = semanticReview.Decision;
                var accumulatedStructuredRejections =
                    AccumulateStructuredAssignmentRejections(
                        intake,
                        latestDraft,
                        semanticReview,
                        structuredRejectedEvidenceByCell);
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.AnswerAdequacyJudge,
                    "source_backed_agent_v2.semantic_review.completed",
                    ("turn", turn),
                    ("decision", semanticReview.Decision),
                    ("reasons", semanticReview.Reasons),
                    ("contract_adjusted", semanticReview.ContractAdjusted),
                    ("rejected_evidence_count", semanticReview.RejectedEvidenceIds.Count),
                    ("rejected_evidence_ids", semanticReview.RejectedEvidenceIds),
                    ("preferred_alternative_count",
                        semanticReview.PreferredAlternativeEvidenceIds.Count),
                    ("preferred_alternative_evidence_ids",
                        semanticReview.PreferredAlternativeEvidenceIds),
                    ("prompt_tokens", semanticReview.PromptTokens),
                    ("completion_tokens", semanticReview.CompletionTokens),
                    ("finish_reason", semanticReview.FinishReason),
                    ("attempts", semanticReview.Attempts),
                    ("truncation_retry_exhausted",
                        semanticReview.TruncationRetryExhausted),
                    ("context_recovery_used",
                        semanticReview.ContextRecoveryUsed),
                    ("evidence_context_mode", semanticReview.EvidenceContextMode),
                    ("exact_input_tokens",
                        semanticReview.ExactInputTokens),
                    ("cited_evidence_characters",
                        semanticReview.CitedEvidenceCharacters),
                    ("accumulated_structured_assignment_rejections",
                        accumulatedStructuredRejections)));
                if (semanticReview.TruncationRetryExhausted)
                {
                    if (semanticResolutionWriterReviewActive)
                        AddSemanticResolutionPublicationBlockedTrace(traces, traceId, ref traceSequence, turn,
                            "semantic_review_protocol_failed",
                            new[] { semanticReview.Decision });
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.AnswerAdequacyJudge,
                        "source_backed_agent_v2.semantic_review.protocol_failed",
                        ("turn", turn),
                        ("attempts", semanticReview.Attempts),
                        ("finish_reason", semanticReview.FinishReason)));
                    break;
                }
                if (!semanticReview.IsAccepted)
                {
                    repaired = true;
                    foreach (var rejectedEvidenceId in semanticReview.RejectedEvidenceIds)
                    {
                        evidenceWorkspace.Reject(
                            rejectedEvidenceId,
                            string.Join(" ", semanticReview.Reasons.Take(2)));
                        semanticallyRejectedEvidenceIds.Add(rejectedEvidenceId);
                        observedEvidenceIdSet.Remove(rejectedEvidenceId);
                        observedEvidenceIds.RemoveAll(id => string.Equals(
                            id,
                            rejectedEvidenceId,
                            StringComparison.OrdinalIgnoreCase));
                    }
                    if (TryScheduleInlineFastAnswerRevision(
                            inlineFastAnswerRequiresIndependentSemanticReview, semanticResolutionWriterReviewActive,
                            inlineFastAnswerDirectRevisionSignatures, Math.Max(1, _options.MaximumSemanticCorrectionTurns), semanticReview,
                            bundle, observedEvidenceIds, semanticallyRejectedEvidenceIds, ref activeSemanticSelectionIds, activeSemanticSelectionLayout,
                            ref semanticReviewFeedback, ref directWriterRevisionInstruction, ref directWriterRequested, ref candidateCollectionOpen,
                            ref selectionOnlyNextTurn,
                            ref decisionOnlyNextTurn, traces, traceId, ref traceSequence, turn))
                    {
                        commitNextVerifiedReviewDirectedRevision =
                            !semanticResolutionWriterReviewActive
                            && requiredAtomicEvidenceCount.GetValueOrDefault() <= 1
                            && CanCommitReviewDirectedRevisionAfterSourceVerification(
                                semanticReview);
                        reviewDirectedRevisionRequiredClaimCount = null;
                        continue;
                    }
                    if (semanticResolutionWriterReviewActive)
                    {
                        AddSemanticResolutionPublicationBlockedTrace(traces, traceId, ref traceSequence, turn,
                            semanticReview.Decision, semanticReview.Reasons,
                            inlineFastAnswerDirectRevisionSignatures.Count);
                        break;
                    }
                    if (inlineFastAnswerRequiresIndependentSemanticReview)
                    {
                        inlineFastAnswerRequiresIndependentSemanticReview = false;
                        explicitSemanticSelectionActive = false;
                    }
                    if (TryScheduleFlatNamedItemRevision(useFlatContentClaimAdequacy, activeSemanticSelectionIds, activeSemanticSelectionLayout, semanticReview, contentClaimDirectRevisionSelectionSignatures, ref semanticReviewFeedback, ref directWriterRevisionInstruction, ref directWriterRequested, ref candidateCollectionOpen, ref selectionOnlyNextTurn, ref decisionOnlyNextTurn, traces, traceId, ref traceSequence, turn))
                    {
                        commitNextVerifiedReviewDirectedRevision = false;
                        reviewDirectedRevisionRequiredClaimCount = null;
                        continue;
                    }
                    var normalizedRejectedDraft = NormalizeDraftForProgressComparison(
                        latestDraft.Answer);
                    var revisionMadeNoProgress = string.Equals(
                        semanticReview.Decision,
                        "revise",
                        StringComparison.OrdinalIgnoreCase)
                        && !semanticallyRejectedDraftSignatures.Add(
                            normalizedRejectedDraft);
                    if (revisionMadeNoProgress)
                    {
                        semanticReviewDecision = "need_more_evidence";
                        semanticReviewFeedback = BuildSemanticRevisionNoProgressFeedback(
                            semanticReview);
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            semanticReviewFeedback);
                        directWriterRequested = false;
                        activeSemanticSelectionIds = null;
                        activeSemanticSelectionLayout = null;
                        pendingStructuredAssignmentReview = null;
                        pendingStructuredAssignmentDraft = null;
                        selectionOnlyNextTurn = false;
                        candidateCollectionOpen = candidateCollectionEnabled;
                        AddTrace(traces, Trace(
                            traceId,
                            ref traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.semantic_revision.no_progress_returned_to_orchestrator",
                            ("turn", turn),
                            ("answer_characters", latestDraft.Answer.Length),
                            ("seen_rejected_drafts",
                                semanticallyRejectedDraftSignatures.Count),
                            ("cited_evidence", verification.CitedEvidence.Count)));
                        continue;
                    }
                    if (TryScheduleContentClaimDirectRevision(
                            useFlatContentClaimAdequacy,
                            activeSemanticSelectionIds,
                            activeSemanticSelectionLayout,
                            semanticReview,
                            contentClaimDirectRevisionSelectionSignatures,
                            boundedNamedDocumentExtraction,
                            requiredAtomicEvidenceCount,
                            ref semanticReviewFeedback,
                            ref directWriterRevisionInstruction,
                            ref directWriterRequested,
                            ref commitNextVerifiedReviewDirectedRevision,
                            ref reviewDirectedRevisionRequiredClaimCount,
                            ref candidateCollectionOpen,
                            ref selectionOnlyNextTurn,
                            ref decisionOnlyNextTurn,
                            traces, traceId, ref traceSequence, turn,
                            out var flatContentClaimRevisionCandidate,
                            out var contentClaimSelectionSignature))
                        continue;
                    if (flatContentClaimRevisionCandidate)
                    {
                        semanticReviewFeedback = BuildContentClaimRevisionBudgetFeedback(
                            semanticReview);
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            semanticReviewFeedback);
                        directWriterRequested = false;
                        directWriterRevisionInstruction = null;
                        activeSemanticSelectionIds = null;
                        activeSemanticSelectionLayout = null;
                        pendingStructuredAssignmentReview = null;
                        pendingStructuredAssignmentDraft = null;
                        selectionOnlyNextTurn = false;
                        decisionOnlyNextTurn = false;
                        candidateCollectionOpen = candidateCollectionEnabled;
                        TraceContentClaimRevisionBudget(
                            traces, traceId, ref traceSequence, turn,
                            contentClaimSelectionSignature,
                            semanticReview.Reasons.Count);
                        continue;
                    }
                    var structuredAssignmentRevision =
                        structuredCandidateWriterTurn
                        && string.Equals(
                            semanticReview.FinishReason,
                            StructuredAssignmentBatchedReviewFinishReason,
                            StringComparison.OrdinalIgnoreCase);
                    var unselectedStructuredCandidateCount = structuredAssignmentRevision
                        ? selectableEvidenceIds.Count(id =>
                            !latestDraft.CitedEvidenceIds.Contains(
                                id,
                                StringComparer.OrdinalIgnoreCase))
                        : 0;
                    var freshEvidenceLifecycleAvailable =
                        turn + 3 <= maximumRunTurnCeiling;
                    var assignmentCollectionDecision =
                        EvaluateStructuredAssignmentCollectionNeed(
                            structuredAssignmentRevision,
                            semanticReview,
                            pendingStructuredAssignmentReview,
                            unselectedStructuredCandidateCount,
                            observedCitableSourceCount);
                    var assignmentContinuationResolution =
                        await ResolveRepeatedStructuredAssignmentContinuationAsync(
                            intake, bundle, latestDraft, semanticReview,
                            selectableEvidenceIds, structuredRejectedEvidenceByCell,
                            freshEvidenceLifecycleAvailable, observedCitableSourceCount,
                            assignmentCollectionDecision, traces, traceId,
                            traceSequence, turn, ct).ConfigureAwait(false);
                    assignmentCollectionDecision =
                        assignmentContinuationResolution.Decision;
                    traceSequence = assignmentContinuationResolution.TraceSequence;
                    TraceStructuredAssignmentCollectionDecision(
                        traces, traceId, ref traceSequence, turn,
                        assignmentCollectionDecision,
                        freshEvidenceLifecycleAvailable);
                    var rejectedStructuredCellCount = assignmentCollectionDecision.RejectedCellCount;
                    var missingStructuredAlternativeCount = assignmentCollectionDecision.MissingAlternativeCount;
                    var expandedCandidatePoolTarget = assignmentCollectionDecision.CandidatePoolTargetCount;
                    var structuredAssignmentRequiresCollection = assignmentCollectionDecision.RequiresCollection;
                    pendingStructuredAssignmentReview = structuredAssignmentRevision
                        ? semanticReview
                        : null;
                    pendingStructuredAssignmentDraft = structuredAssignmentRevision
                        ? latestDraft
                        : null;
                    semanticReviewFeedback = structuredAssignmentRequiresCollection
                        ? BuildStructuredAssignmentEvidenceGapFeedback(
                            intake,
                            latestDraft,
                            semanticReview,
                            rejectedStructuredCellCount,
                            unselectedStructuredCandidateCount,
                            assignmentCollectionDecision.RepeatedRejectedCellCount,
                            expandedCandidatePoolTarget,
                            assignmentCollectionDecision.ResearchNeed)
                        : structuredAssignmentRevision
                        ? BuildStructuredAssignmentRevisionFeedback(
                            intake,
                            semanticReview,
                            latestDraft,
                            activeSemanticSelectionIds
                            ?? latestDraft.CitedEvidenceIds)
                        : BuildSemanticJudgeFeedback(semanticReview);
                    if (_options.SeparateActionAndWriter)
                    {
                        var reviseStructuredAssignments =
                            structuredCandidateWriterTurn
                            && string.Equals(
                                semanticReview.Decision,
                                "revise",
                                StringComparison.OrdinalIgnoreCase)
                            && semanticReview.RejectedEvidenceIds.Count == 0
                            && !structuredAssignmentRequiresCollection;
                        if (structuredAssignmentRequiresCollection)
                        {
                            semanticReviewDecision = "need_more_evidence";
                            candidatePoolTargetCount = expandedCandidatePoolTarget;
                            candidateCollectionOpen = true;
                            selectionOnlyNextTurn = false;
                            maximumRunTurns = Math.Min(
                                maximumRunTurnCeiling,
                                Math.Max(maximumRunTurns, turn + 3));
                            AddTrace(traces, Trace(
                                traceId,
                                ref traceSequence,
                                SourceBackedPipelineStep.IterationController,
                                "source_backed_agent_v2.structured_assignment_revision.collection_reopened",
                                ("turn", turn),
                                ("rejected_cells", rejectedStructuredCellCount),
                                ("unselected_candidates", unselectedStructuredCandidateCount),
                                ("missing_alternatives", missingStructuredAlternativeCount),
                                ("repeated_rejected_cells",
                                    assignmentCollectionDecision
                                        .RepeatedRejectedCellCount),
                                ("observed_candidates", observedCitableSourceCount),
                                ("candidate_pool_target", candidatePoolTargetCount),
                                ("decision_source",
                                    assignmentCollectionDecision.DecisionSource)));
                        }
                        if (string.Equals(
                                semanticReview.Decision,
                                "need_more_evidence",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            candidateCollectionOpen = candidateCollectionEnabled;
                        }
                        CompactWorkingMessages(
                            messages,
                            intake,
                            semanticPlan,
                            bundle,
                            observedEvidenceIds,
                            executedRequests,
                            evidenceWorkspace,
                            semanticReviewFeedback);
                        directWriterRequested = false;
                        activeSemanticSelectionIds = null;
                        activeSemanticSelectionLayout = null;
                        selectionOnlyNextTurn = reviseStructuredAssignments;
                        if (reviseStructuredAssignments)
                        {
                            maximumRunTurns = Math.Min(
                                maximumRunTurnCeiling,
                                Math.Max(maximumRunTurns, turn + 2));
                        }
                        continue;
                    }

                    messages.Add(SourceBackedAgentMessage.Assistant(completion.Content));
                    messages.Add(SourceBackedAgentMessage.User(semanticReviewFeedback));
                    directWriterRequested = false;
                    continue;
                }

                semanticAccepted = true;
                return BuildResult(
                    intake,
                    executedRequests,
                    bundle,
                    firstDraft,
                    latestDraft,
                    verification,
                    repaired,
                    semanticAccepted,
                    semanticallyRejectedEvidenceIds,
                    semanticReviewReasons,
                    traces);
            }

            repaired = true;
            var mechanicalFailureSignature =
                BuildMechanicalFailureSignature(verification);
            if (string.Equals(
                    lastMechanicalFailureSignature,
                    mechanicalFailureSignature,
                    StringComparison.Ordinal))
            {
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.mechanical_repair.no_progress_stopped",
                    ("turn", turn),
                    ("failure_signature", mechanicalFailureSignature),
                    ("answer_characters", latestDraft.Answer.Length),
                    ("cited_evidence", verification.CitedEvidence.Count)));
                break;
            }
            lastMechanicalFailureSignature = mechanicalFailureSignature;
            if (structuredCandidateWriterTurn)
            {
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.structured_candidate_writer.mechanical_repair_exhausted",
                    ("turn", turn),
                    ("failure_signature", mechanicalFailureSignature),
                    ("answer_characters", latestDraft.Answer.Length),
                    ("cited_evidence", verification.CitedEvidence.Count),
                    ("decision", "terminal_insufficient_evidence")));
                break;
            }
            if (activeSemanticSelectionLayout is not null)
            {
                semanticReviewFeedback = BuildMechanicalRepairMessage(verification);
                CompactWorkingMessages(
                    messages,
                    intake,
                    semanticPlan,
                    bundle,
                    observedEvidenceIds,
                    executedRequests,
                    evidenceWorkspace,
                    semanticReviewFeedback);
                activeSemanticSelectionIds = null;
                activeSemanticSelectionLayout = null;
                directWriterRequested = false;
                continue;
            }

            messages.Add(SourceBackedAgentMessage.Assistant(completion.Content));
            messages.Add(SourceBackedAgentMessage.User(BuildMechanicalRepairMessage(verification)));
            directWriterRequested = _options.SeparateActionAndWriter;
        }

        return BuildResult(
            intake,
            executedRequests,
            bundle,
            firstDraft,
            latestDraft,
            verification,
            repaired,
            semanticAccepted,
            semanticallyRejectedEvidenceIds,
            semanticReviewReasons,
            traces);
    }
}
