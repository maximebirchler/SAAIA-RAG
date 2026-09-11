namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void ApplyGlobalStructuredCandidateMinimumGate(
        bool useGlobalStructuredCandidateSelection,
        bool candidateAuditEnabled,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        ISet<string> semanticallyAuditedEvidenceIds,
        ISet<string> semanticallyRejectedEvidenceIds,
        int? requiredAtomicEvidenceCount,
        int candidatePoolTargetCount,
        IReadOnlyList<string> semanticRowLabels,
        IReadOnlyList<string> semanticColumnLabels,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        if (!useGlobalStructuredCandidateSelection
            || !candidateCollectionOpen
            || requiredAtomicEvidenceCount is not > 0)
        {
            return;
        }

        var availableCandidates = CountObservedCitableSources(
            bundle,
            observedEvidenceIds.Where(id =>
                (!candidateAuditEnabled
                 || semanticallyAuditedEvidenceIds.Contains(id))
                && !semanticallyRejectedEvidenceIds.Contains(id)));
        var structuredColumnCoverage = EvaluateStructuredColumnCoverage(
            bundle,
            observedEvidenceIds,
            semanticallyAuditedEvidenceIds,
            semanticallyRejectedEvidenceIds,
            semanticRowLabels,
            semanticColumnLabels);
        if (structuredColumnCoverage.Applied
            && !structuredColumnCoverage.HasRequiredCoverage)
        {
            return;
        }
        if (availableCandidates < candidatePoolTargetCount)
            return;

        candidateCollectionOpen = false;
        selectionOnlyNextTurn = true;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.global_structured_assignment.requested",
            ("turn", turn),
            ("available_candidates", availableCandidates),
            ("required", requiredAtomicEvidenceCount.Value),
            ("candidate_pool_target", candidatePoolTargetCount),
            ("structured_column_coverage_applied",
                structuredColumnCoverage.Applied),
            ("structured_column_coverage_matched_cells",
                structuredColumnCoverage.MatchedCellCount),
            ("structured_column_coverage_required_cells",
                structuredColumnCoverage.RequiredCellCount),
            ("decision_source", "llm_orchestrator_writer"),
            ("mechanical_trigger", "minimum_citable_pool_reached")));
    }

    private sealed record StructuredAssignmentCollectionDecision(
        int RejectedCellCount,
        int UnselectedCandidateCount,
        int MissingAlternativeCount,
        int RepeatedRejectedCellCount,
        int CandidatePoolTargetCount,
        bool RequiresCollection,
        string DecisionSource,
        string? ResearchNeed = null);

    private StructuredAssignmentCollectionDecision
        EvaluateStructuredAssignmentCollectionNeed(
            bool structuredAssignmentRevision,
            SemanticReview currentReview,
            SemanticReview? previousReview,
            int unselectedCandidateCount,
            int observedCandidateCount)
    {
        if (!structuredAssignmentRevision)
        {
            return new StructuredAssignmentCollectionDecision(
                0,
                0,
                0,
                0,
                observedCandidateCount,
                RequiresCollection: false,
                DecisionSource: "not_a_structured_assignment_revision");
        }

        var currentRejectedCells =
            GetStructuredAssignmentRejectedCellIndexes(currentReview)
            ?? new HashSet<int>();
        var previousRejectedCells = previousReview is null
            ? null
            : GetStructuredAssignmentRejectedCellIndexes(previousReview);
        var repeatedRejectedCellCount = previousRejectedCells is null
            ? 0
            : currentRejectedCells.Count(previousRejectedCells.Contains);
        var missingAlternativeCount = Math.Max(
            0,
            currentRejectedCells.Count - unselectedCandidateCount);

        // A rejected placement proves that the assignment is wrong, not that the
        // entire audited pool is insufficient. When enough unused candidates are
        // already present, let the LLM reassign them and remember every per-cell
        // rejection. Reopen retrieval only for the mechanically missing number of
        // distinct alternatives; code never decides which candidate fits a role.
        var additionalCandidateCount = missingAlternativeCount;
        var target = Math.Min(
            _options.MaximumWorkingEvidenceItems,
            observedCandidateCount + additionalCandidateCount);
        var requiresCollection = additionalCandidateCount > 0
            && target > observedCandidateCount;
        var decisionSource = additionalCandidateCount == 0
            ? currentRejectedCells.Count == 0
                ? "llm_assignments_accepted"
                : "llm_rejected_assignments_have_local_alternatives"
            : "llm_rejected_assignments_lack_local_alternatives";

        return new StructuredAssignmentCollectionDecision(
            currentRejectedCells.Count,
            unselectedCandidateCount,
            missingAlternativeCount,
            repeatedRejectedCellCount,
            target,
            requiresCollection,
            decisionSource);
    }

    private void TraceStructuredAssignmentCollectionDecision(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        StructuredAssignmentCollectionDecision decision,
        bool freshEvidenceLifecycleAvailable)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.structured_assignment_revision.path_decided",
            ("turn", turn),
            ("rejected_cells", decision.RejectedCellCount),
            ("unselected_candidates", decision.UnselectedCandidateCount),
            ("missing_alternatives", decision.MissingAlternativeCount),
            ("repeated_rejected_cells", decision.RepeatedRejectedCellCount),
            ("candidate_pool_target", decision.CandidatePoolTargetCount),
            ("fresh_evidence_lifecycle_available",
                freshEvidenceLifecycleAvailable),
            ("requires_collection", decision.RequiresCollection),
            ("decision_source", decision.DecisionSource),
            ("research_need", decision.ResearchNeed)));
}
