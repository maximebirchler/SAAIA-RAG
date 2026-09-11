namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private bool TryScheduleContentClaimDirectRevision(
        bool useFlatContentClaimAdequacy,
        IReadOnlyList<string>? activeSelectionIds,
        SemanticSelectionLayout? activeSelectionLayout,
        SemanticReview semanticReview,
        ISet<string> attemptedSelectionSignatures,
        bool boundedNamedDocumentExtraction,
        int? requiredAtomicEvidenceCount,
        ref string? semanticReviewFeedback,
        ref string? directWriterRevisionInstruction,
        ref bool directWriterRequested,
        ref bool commitNextVerifiedReviewDirectedRevision,
        ref int? reviewDirectedRevisionRequiredClaimCount,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        out bool isRevisionCandidate,
        out string selectionSignature)
    {
        isRevisionCandidate = useFlatContentClaimAdequacy
                              && _options.SeparateActionAndWriter
                              && activeSelectionIds is { Count: > 0 }
                              && activeSelectionLayout is null
                              && string.Equals(
                                  semanticReview.Decision,
                                  "revise",
                                  StringComparison.OrdinalIgnoreCase)
                              && semanticReview.RejectedEvidenceIds.Count == 0;
        selectionSignature = isRevisionCandidate
            ? BuildEvidenceSelectionSignature(activeSelectionIds!)
            : string.Empty;
        if (!isRevisionCandidate
            || !attemptedSelectionSignatures.Add(selectionSignature))
        {
            return false;
        }

        semanticReviewFeedback = BuildContentClaimDirectRevisionFeedback(
            semanticReview);
        directWriterRevisionInstruction = semanticReviewFeedback;
        directWriterRequested = true;
        commitNextVerifiedReviewDirectedRevision =
            boundedNamedDocumentExtraction
            && requiredAtomicEvidenceCount is > 0
            && CanCommitReviewDirectedRevisionAfterSourceVerification(
                semanticReview);
        reviewDirectedRevisionRequiredClaimCount =
            commitNextVerifiedReviewDirectedRevision
                ? requiredAtomicEvidenceCount
                : null;
        candidateCollectionOpen = false;
        selectionOnlyNextTurn = false;
        decisionOnlyNextTurn = false;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.content_claim_revision.returned_to_writer",
            ("turn", turn),
            ("selected_evidence", activeSelectionIds),
            ("reason_count", semanticReview.Reasons.Count),
            ("decision_source", "llm_semantic_review")));
        return true;
    }

    private static string BuildContentClaimDirectRevisionFeedback(
        SemanticReview review)
        => $"""
           REVISION SEMANTIQUE CONTENT_CLAIM — PREUVES INCHANGEES:
           {string.Join(
               Environment.NewLine,
               review.Reasons.Select(static reason => "- " + reason))}

           Les EvidenceId selectionnes restent valides. Reecris directement le
           livrable avec exactement la meme selection, sans rechercher, remplacer ni
           ajouter de preuve. Corrige chaque formulation, relation, ordre ou distinction
           de variante signalee par le juge. N'utilise aucune connaissance externe
           contenue ou suggeree dans les motifs: seules les FENETRE_SOURCE autorisees
           prouvent les claims. Garde un claim distinct et localement cite par EvidenceId.
           Ne mentionne ni cette revision, ni le juge, ni les outils.
           """;
}
