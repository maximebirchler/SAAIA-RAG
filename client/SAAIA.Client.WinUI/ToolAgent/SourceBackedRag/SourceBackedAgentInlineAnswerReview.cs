namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private SourceBackedAgentCompletion BuildInlineFastAnswerCompletion(
        FastEvidenceReview fastReview,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        var completion = new SourceBackedAgentCompletion(
            fastReview.Answer,
            Array.Empty<SourceBackedAgentToolCall>(),
            "stop");
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Writer,
            "source_backed_agent_v2.writer.inline_from_evidence_review",
            ("turn", turn),
            ("decision_source", fastReview.SemanticAnswerTransactionAttempted
                ? "llm_semantic_answer_transaction"
                : "llm_evidence_judge"),
            ("content_characters", fastReview.Answer.Length),
            ("selected_evidence_ids", fastReview.EvidenceIds),
            ("shared_llm_call", true)));
        return completion;
    }

    private bool TryScheduleInlineFastAnswerRevision(
        bool independentReviewRequired,
        bool semanticResolutionWriterReviewActive,
        ISet<string> directRevisionSignatures,
        int maximumDirectRevisionAttempts,
        SemanticReview semanticReview,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        ref IReadOnlyList<string>? activeSemanticSelectionIds,
        SemanticSelectionLayout? activeSemanticSelectionLayout,
        ref string? semanticReviewFeedback,
        ref string? directWriterRevisionInstruction,
        ref bool directWriterRequested,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        var preferredAlternativeEvidenceIds =
            ResolveInlineFastAnswerPreferredAlternatives(
                semanticReview,
                bundle,
                observedEvidenceIds,
                semanticallyRejectedEvidenceIds);
        var usesPreferredAlternatives =
            semanticReview.RejectedEvidenceIds.Count > 0
            && preferredAlternativeEvidenceIds.Count > 0;
        var revisionEvidenceIds = usesPreferredAlternatives
            ? preferredAlternativeEvidenceIds
            : activeSemanticSelectionIds ?? Array.Empty<string>();
        var revisionSignature = BuildInlineFastAnswerRevisionSignature(
            revisionEvidenceIds);
        var boundedMaximumDirectRevisionAttempts =
            semanticResolutionWriterReviewActive
                ? 1
                : Math.Max(1, maximumDirectRevisionAttempts);
        var canReviseDirectly = independentReviewRequired
                                && directRevisionSignatures.Count
                                    < boundedMaximumDirectRevisionAttempts
                                && revisionSignature.Length > 0
                                && !directRevisionSignatures.Contains(
                                    revisionSignature)
                                && _options.SeparateActionAndWriter
                                && revisionEvidenceIds.Count > 0
                                && activeSemanticSelectionLayout is null
                                && string.Equals(
                                    semanticReview.Decision,
                                    "revise",
                                    StringComparison.OrdinalIgnoreCase)
                                && (semanticReview.RejectedEvidenceIds.Count == 0
                                    || usesPreferredAlternatives);
        if (!canReviseDirectly)
            return false;

        directRevisionSignatures.Add(revisionSignature);
        activeSemanticSelectionIds = revisionEvidenceIds;
        semanticReviewFeedback = BuildSemanticJudgeFeedback(semanticReview);
        directWriterRevisionInstruction = semanticReviewFeedback;
        directWriterRequested = true;
        candidateCollectionOpen = false;
        selectionOnlyNextTurn = false;
        decisionOnlyNextTurn = false;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            semanticResolutionWriterReviewActive
                ? "source_backed_agent_v2.semantic_resolution_writer_review.repair_scheduled"
                : usesPreferredAlternatives
                    ? "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer"
                    : "source_backed_agent_v2.inline_answer_revision.returned_to_writer",
            ("turn", turn),
            ("rejected_evidence", semanticReview.RejectedEvidenceIds),
            ("selected_evidence", revisionEvidenceIds),
            ("revision_attempt", directRevisionSignatures.Count),
            ("maximum_revision_attempts", boundedMaximumDirectRevisionAttempts),
            ("selection_signature", revisionSignature),
            ("reason_count", semanticReview.Reasons.Count),
            ("decision_source", "llm_semantic_review")));
        return true;
    }

    internal static string BuildInlineFastAnswerRevisionSignature(
        IReadOnlyList<string> evidenceIds) =>
        string.Join(
            ",",
            evidenceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal));

    private static bool CanCommitReviewDirectedRevisionAfterSourceVerification(
        SemanticReview semanticReview) =>
        string.Equals(
            semanticReview.Decision,
            "revise",
            StringComparison.OrdinalIgnoreCase)
        && semanticReview.RejectedEvidenceIds.Count == 0
        && semanticReview.PreferredAlternativeEvidenceIds.Count == 0;

    private static bool IsBoundedNamedDocumentExtraction(
        SourceBackedIntake intake)
    {
        if (intake.InitialSemanticMission is not { } mission
            || mission.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object
            || !mission.Arguments.TryGetProperty(
                "boundedNamedDocumentExtraction",
                out var marker))
        {
            return false;
        }

        return marker.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static IReadOnlyList<string> ResolveInlineFastAnswerPreferredAlternatives(
        SemanticReview semanticReview,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds)
    {
        if (semanticReview.PreferredAlternativeEvidenceIds.Count == 0)
            return Array.Empty<string>();

        var visibleCitableEvidenceIds = BuildVisibleCitableEvidenceIdSet(
            bundle,
            observedEvidenceIds);
        return semanticReview.PreferredAlternativeEvidenceIds
            .Where(visibleCitableEvidenceIds.Contains)
            .Where(id => !semanticallyRejectedEvidenceIds.Contains(id))
            .Where(id => bundle.ById.TryGetValue(id, out var item)
                         && HasRenderableEvidenceValue(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
