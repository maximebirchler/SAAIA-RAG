namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private bool TryScheduleFlatNamedItemRevision(
        bool useFlatContentClaimAdequacy,
        IReadOnlyList<string>? activeSemanticSelectionIds,
        SemanticSelectionLayout? activeSemanticSelectionLayout,
        SemanticReview semanticReview,
        ISet<string> attemptedSelectionSignatures,
        ref string? semanticReviewFeedback,
        ref string? directWriterRevisionInstruction,
        ref bool directWriterRequested,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        if (useFlatContentClaimAdequacy
            || !_options.SeparateActionAndWriter
            || activeSemanticSelectionIds is not { Count: > 0 }
            || activeSemanticSelectionLayout is not null
            || !string.Equals(
                semanticReview.Decision,
                "revise",
                StringComparison.OrdinalIgnoreCase)
            || semanticReview.RejectedEvidenceIds.Count > 0
            || semanticReview.PreferredAlternativeEvidenceIds.Count > 0)
        {
            return false;
        }

        var signature = BuildEvidenceSelectionSignature(
            activeSemanticSelectionIds);
        if (signature.Length == 0
            || !attemptedSelectionSignatures.Add(signature))
        {
            return false;
        }

        semanticReviewFeedback = BuildFlatNamedItemRevisionFeedback(
            semanticReview);
        directWriterRevisionInstruction = semanticReviewFeedback;
        directWriterRequested = true;
        candidateCollectionOpen = false;
        selectionOnlyNextTurn = false;
        decisionOnlyNextTurn = false;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.flat_selection_revision.returned_to_writer",
            ("turn", turn),
            ("selected_evidence", activeSemanticSelectionIds),
            ("selection_signature", signature),
            ("direct_revision_budget", 1),
            ("reason_count", semanticReview.Reasons.Count),
            ("decision_source", "llm_semantic_review")));
        return true;
    }

    private static string BuildFlatNamedItemRevisionFeedback(
        SemanticReview review)
        => $"""
           REVISION SEMANTIQUE — MEME UNITE ET MEME FENETRE DE PREUVES:
           {string.Join(
               Environment.NewLine,
               review.Reasons.Select(static reason => "- " + reason))}

           Les preuves citees n'ont pas ete rejetees. Reecris directement le
           livrable avec la meme unite selectionnee et ses preuves citables,
           sans nouvelle recherche ni remplacement. Corrige seulement les
           formulations signalees. Chaque claim documentaire reste localement
           associe a l'EvidenceId dont l'extrait le soutient. Ne mentionne ni
           cette revision, ni le juge, ni les outils.
           """;
}
