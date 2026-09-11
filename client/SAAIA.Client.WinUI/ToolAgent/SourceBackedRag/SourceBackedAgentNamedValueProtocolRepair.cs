namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string NamedValueNotExtractiveProtocolError =
        "structured_flat_writer_named_value_not_extractive";

    private async Task<DirectWriterHandoff>
        CompleteDirectWriterHandoffWithNamedValueRepairAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            SourceVerificationResult? verification,
            string? directRevisionInstruction,
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<string>? activeSelectionIds,
            SemanticSelectionLayout? activeSelectionLayout,
            string atomicEvidenceMode,
            int? requiredStructuredClaimCount,
            bool repairEligible,
            CancellationToken ct)
    {
        var first = await CompleteDirectWriterHandoffAsync(
                messages,
                verification,
                directRevisionInstruction,
                intake,
                bundle,
                activeSelectionIds,
                activeSelectionLayout,
                atomicEvidenceMode,
                requiredStructuredClaimCount,
                ct)
            .ConfigureAwait(false);
        if (!repairEligible
            || !string.Equals(
                first.ProtocolError,
                NamedValueNotExtractiveProtocolError,
                StringComparison.Ordinal)
            || requiredStructuredClaimCount is not > 0)
        {
            return first;
        }

        var repaired = await CompleteDirectWriterHandoffAsync(
                messages,
                verification,
                BuildNamedValueProtocolRepairInstruction(
                    requiredStructuredClaimCount.Value),
                intake,
                bundle,
                activeSelectionIds,
                activeSelectionLayout,
                atomicEvidenceMode,
                requiredStructuredClaimCount,
                ct)
            .ConfigureAwait(false);
        return repaired with
        {
            NamedValueProtocolRepairAttempted = true,
            OriginalCompletion = first.Completion,
            OriginalProtocolError = first.ProtocolError
        };
    }

    private static string BuildNamedValueProtocolRepairInstruction(
        int requiredClaimCount)
        => $"""
           REPARATION MECANIQUE UNIQUE — EXTRACTION DE VALEURS NOMMEES:
           La sortie précédente est invalide et doit être entièrement abandonnée.
           Produis exactement {requiredClaimCount} claims. Dans chaque text, recopie
           uniquement une valeur ou exigence demandée telle qu'elle apparaît dans la
           FENETRE_SOURCE citée. N'écris ni phrase d'introduction, ni catégorie, ni rôle,
           ni définition, ni explication, ni interprétation, ni conséquence. Conserve
           l'ordre de la source. Utilise exactement les mêmes EvidenceIds autorisés.
           """;

    private void TraceNamedValueProtocolRepair(
        DirectWriterHandoff handoff,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        if (!handoff.NamedValueProtocolRepairAttempted
            || handoff.OriginalCompletion is null)
        {
            return;
        }

        AddWriterTrace(
            traces,
            traceId,
            ref traceSequence,
            turn,
            handoff.OriginalCompletion,
            directRevision: true);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.named_value_extraction.protocol_repair.requested",
            ("turn", turn),
            ("protocol_error", handoff.OriginalProtocolError),
            ("decision_source", "bounded_named_value_mechanical_contract")));
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Writer,
            "source_backed_agent_v2.named_value_extraction.protocol_repair.completed",
            ("turn", turn),
            ("protocol_valid", string.IsNullOrWhiteSpace(handoff.ProtocolError)),
            ("protocol_error", handoff.ProtocolError),
            ("claim_count", handoff.StructuredClaimCount.GetValueOrDefault()),
            ("decision_source", "bounded_named_value_mechanical_contract")));
    }
}
