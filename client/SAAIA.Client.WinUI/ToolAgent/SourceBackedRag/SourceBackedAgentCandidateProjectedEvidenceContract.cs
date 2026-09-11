namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CandidateProjectedEvidenceContractOutcome(
        string SemanticPlan,
        string EvidenceContract,
        bool Applied,
        string FailureReason);

    private static CandidateProjectedEvidenceContractOutcome
        ApplyCandidateProjectedEvidenceContract(
            string semanticPlan,
            SemanticPlanPreparation plan,
            SemanticCandidateStrategyOutcome candidateStrategy,
            int? requiredAtomicEvidenceCount)
    {
        if (plan.RowLabels.Count == 0 || plan.ColumnRoles.Count == 0)
        {
            return new CandidateProjectedEvidenceContractOutcome(
                semanticPlan,
                string.Empty,
                Applied: false,
                FailureReason: string.Empty);
        }
        if (!candidateStrategy.Attempted)
        {
            return new CandidateProjectedEvidenceContractOutcome(
                semanticPlan,
                string.Empty,
                Applied: false,
                FailureReason: "candidate_strategy_not_attempted");
        }
        if (!candidateStrategy.ProtocolValid)
        {
            return new CandidateProjectedEvidenceContractOutcome(
                semanticPlan,
                string.Empty,
                Applied: false,
                FailureReason: "candidate_strategy_invalid");
        }
        if (requiredAtomicEvidenceCount is not > 0)
        {
            return new CandidateProjectedEvidenceContractOutcome(
                semanticPlan,
                string.Empty,
                Applied: false,
                FailureReason: "candidate_count_contract_invalid");
        }

        var lines = semanticPlan.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        var replaced = false;
        var evidenceContract = string.Empty;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(
                    "PREUVES_ATOMIQUES:",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            evidenceContract = requiredAtomicEvidenceCount.Value
                               .ToString(
                                   System.Globalization.CultureInfo.InvariantCulture)
                               + " instances source distinctes, nommees et citables; "
                               + "chaque instance doit etre jugee par le LLM comme directement "
                               + "adequate a au moins une position finale visible; "
                               + "mode de decouverte decide par le LLM="
                               + candidateStrategy.CandidatePoolRelation;
            lines[index] = "PREUVES_ATOMIQUES: " + evidenceContract;
            replaced = true;
            break;
        }

        return new CandidateProjectedEvidenceContractOutcome(
            replaced ? string.Join(Environment.NewLine, lines) : semanticPlan,
            replaced ? evidenceContract : string.Empty,
            Applied: replaced,
            FailureReason: replaced
                ? string.Empty
                : "atomic_evidence_line_missing");
    }

    private void AddCandidateProjectedEvidenceContractTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        CandidateProjectedEvidenceContractOutcome outcome)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.candidate_projected_evidence_contract.completed",
            ("applied", outcome.Applied),
            ("evidence_contract", outcome.EvidenceContract),
            ("failure_reason", outcome.FailureReason)));
}
