namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal bool TryApplyStructuredCandidateWriterEvidenceRequest(
        StructuredCandidateWriterExecution execution,
        int observedCandidateCount,
        int turn,
        ref int candidatePoolTargetCount,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool directWriterRequested,
        ref string? semanticReviewFeedback,
        ref int maximumRunTurns,
        int maximumRunTurnCeiling,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence)
    {
        if (!execution.RequiresMoreEvidence)
            return false;

        var requestedAdditionalCandidateCount = Math.Max(
            1,
            execution.RequestedAdditionalCandidateCount);
        var target = Math.Min(
            _options.MaximumWorkingEvidenceItems,
            observedCandidateCount + requestedAdditionalCandidateCount);
        if (target <= observedCandidateCount)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.structured_candidate_writer.more_evidence_unavailable",
                ("turn", turn),
                ("observed_candidates", observedCandidateCount),
                ("maximum_working_evidence", _options.MaximumWorkingEvidenceItems),
                ("research_need", execution.ResearchNeed),
                ("decision_source", "mechanical_capacity_contract")));
            return false;
        }

        candidatePoolTargetCount = Math.Max(candidatePoolTargetCount, target);
        candidateCollectionOpen = true;
        selectionOnlyNextTurn = false;
        directWriterRequested = false;
        semanticReviewFeedback = $"""
            LE LLM REDACTEUR DEMANDE DE NOUVELLES PREUVES AVANT AFFECTATION.
            CANDIDATS APPROUVES ACTUELS: {observedCandidateCount}
            NOUVELLES ALTERNATIVES DEMANDEES: {requestedAdditionalCandidateCount}
            BESOIN SEMANTIQUE FORMULE PAR LE LLM: {execution.ResearchNeed}

            Reprends la recherche avec les outils exposes. Choisis toi-meme la capacite,
            la requete et la navigation les plus utiles d'apres ce besoin, les preuves deja
            observees et les actions deja consommees. Ne redige pas encore la grille.
            """;
        maximumRunTurns = Math.Min(
            maximumRunTurnCeiling,
            Math.Max(maximumRunTurns, turn + 3));
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.structured_candidate_writer.more_evidence_requested",
            ("turn", turn),
            ("observed_candidates", observedCandidateCount),
            ("requested_additional_candidates", requestedAdditionalCandidateCount),
            ("candidate_pool_target", candidatePoolTargetCount),
            ("research_need", execution.ResearchNeed),
            ("writer_attempts", execution.Attempts),
            ("decision_source", "llm_orchestrator_writer")));
        return true;
    }
}
