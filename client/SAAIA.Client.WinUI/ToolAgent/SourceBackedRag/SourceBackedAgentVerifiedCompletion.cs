namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private bool TryCompleteVerifiedAnswerWithoutFreshSemanticReview(
        bool reviewDirectedRevisionCompleted,
        bool exactClaimCountSatisfied,
        bool explicitSemanticSelectionActive,
        bool freshWriterOutputNeedsSemanticReview,
        bool inlineFastAnswerRequiresIndependentSemanticReview,
        bool citedContextEvidenceBeyondSelection,
        IReadOnlyList<string>? activeSelectionIds,
        SemanticSelectionLayout? activeSelectionLayout,
        SourceBackedIntake intake,
        IReadOnlyList<RetrievalRequest> executedRequests,
        EvidenceBundle bundle,
        WriterDraft? firstDraft,
        WriterDraft latestDraft,
        SourceVerificationResult verification,
        bool repaired,
        IReadOnlySet<string> rejectedEvidenceIds,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        out SourceBackedPipelineResult result)
    {
        result = null!;
        IReadOnlyList<string>? reasons = null;
        if (reviewDirectedRevisionCompleted
            && exactClaimCountSatisfied
            && activeSelectionIds is { Count: > 0 }
            && activeSelectionLayout is null
            && !citedContextEvidenceBeyondSelection)
        {
            reasons =
            [
                "La revision demandee par le juge conserve la selection et passe le contrat source."
            ];
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.AnswerAdequacyJudge,
                "source_backed_agent_v2.review_directed_revision.committed_after_source_verification",
                ("turn", turn),
                ("selected_evidence", activeSelectionIds),
                ("decision_source", "prior_llm_semantic_review_plus_source_verifier")));
        }
        else if (explicitSemanticSelectionActive
                 && activeSelectionLayout is null
                 && !freshWriterOutputNeedsSemanticReview
                 && !inlineFastAnswerRequiresIndependentSemanticReview
                 && !citedContextEvidenceBeyondSelection)
        {
            reasons =
            [
                "Audit et selection semantiques explicites valides par le contrat source."
            ];
            TraceReusedSelectionReview(
                traces,
                traceId,
                ref traceSequence,
                turn,
                activeSelectionIds);
        }

        if (reasons is null)
            return false;

        result = BuildResult(
            intake,
            executedRequests,
            bundle,
            firstDraft,
            latestDraft,
            verification,
            repaired,
            semanticAccepted: true,
            rejectedEvidenceIds,
            reasons,
            traces);
        return true;
    }
}
