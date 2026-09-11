namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed class NoCitableActionDecisionProgress
    {
        public string LastSignature { get; set; } = string.Empty;

        public int ConsecutiveIdenticalDecisions { get; set; }
    }

    private bool HandleNoCitableActionDecision(
        string? decision,
        EvidenceBundle bundle,
        int consecutiveDocumentaryNoProgressTurns,
        NoCitableActionDecisionProgress progress,
        ICollection<SourceBackedAgentMessage> messages,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        string? language,
        out string terminalNote)
    {
        var signature = BuildNoCitableActionDecisionSignature(
            decision,
            bundle);
        if (string.Equals(
                signature,
                progress.LastSignature,
                StringComparison.Ordinal))
        {
            progress.ConsecutiveIdenticalDecisions++;
        }
        else
        {
            progress.LastSignature = signature;
            progress.ConsecutiveIdenticalDecisions = 1;
        }

        if (consecutiveDocumentaryNoProgressTurns >= 2
            && progress.ConsecutiveIdenticalDecisions >= 2)
        {
            terminalNote = BuildRepeatedNoCitableDecisionNote(language);
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.action.no_citable_evidence_repeat.stopped",
                ("turn", turn),
                ("identical_decisions",
                    progress.ConsecutiveIdenticalDecisions),
                ("consecutive_no_progress_turns",
                    consecutiveDocumentaryNoProgressTurns),
                ("decision_characters", decision?.Length ?? 0),
                ("observable_evidence_items", bundle.Items.Count),
                ("decision_source", "mechanical_no_progress_contract")));
            return true;
        }

        terminalNote = string.Empty;
        messages.Add(SourceBackedAgentMessage.User(
            BuildNoCitableEvidenceActionRepairMessage()));
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.SourceVerifier,
            "source_backed_agent_v2.action.no_citable_evidence_rejected",
            ("turn", turn),
            ("decision_characters", decision?.Length ?? 0)));
        return false;
    }

    private static string BuildNoCitableActionDecisionSignature(
        string? decision,
        EvidenceBundle bundle)
    {
        var normalizedDecision = CollapseDocumentFocusValue(decision)
            .ToUpperInvariant();
        var observableState = string.Join(
            ";",
            bundle.Items
                .OrderBy(static item => item.EvidenceId, StringComparer.OrdinalIgnoreCase)
                .Select(static item => string.Join(
                    ":",
                    item.EvidenceId,
                    item.SourceKind,
                    item.DocId ?? string.Empty,
                    item.DocPath ?? string.Empty,
                    item.ChunkId ?? string.Empty,
                    item.PageStart?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty,
                    item.PageEnd?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty)));
        return normalizedDecision + "|" + observableState;
    }

    private static string BuildRepeatedNoCitableDecisionNote(
        string? language)
        => string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase)
            ? "Arrêt mécanique après deux décisions d'action identiques sans nouvelle preuve citable."
            : "Mechanical stop after two identical action decisions without new citable evidence.";
}
