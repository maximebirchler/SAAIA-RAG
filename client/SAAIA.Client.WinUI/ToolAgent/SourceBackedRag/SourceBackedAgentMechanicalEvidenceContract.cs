namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string[] FindSetMembers(
        IEnumerable<string> candidates,
        ISet<string> set)
        => candidates.Where(set.Contains).ToArray();

    private static string[] FindMissingSetMembers(
        IEnumerable<string> candidates,
        ISet<string> set)
        => candidates.Where(candidate => !set.Contains(candidate)).ToArray();

    private static HashSet<string> BuildVisibleCitableEvidenceIdSet(
        EvidenceBundle bundle,
        IEnumerable<string> evidenceIds)
        => evidenceIds
            .Where(id => bundle.ById.TryGetValue(id, out var item)
                         && IsMechanicallyCitableCandidate(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DescribeDuplicateVisibleSourceSelections(
        IReadOnlyList<DuplicateVisibleSourceSelection> duplicates)
        => duplicates
            .Select(static duplicate =>
                $"{duplicate.DuplicateEvidenceId}=meme_source_que_"
                + $"{duplicate.KeptEvidenceId}({duplicate.SourceLabel})")
            .ToArray();

    private static IReadOnlyList<string> DescribeDuplicateDisplayValueSelections(
        IReadOnlyList<DuplicateDisplayValueSelection> duplicates)
        => duplicates
            .Select(static duplicate =>
                $"{duplicate.DuplicateEvidenceId}=meme_valeur_que_"
                + $"{duplicate.KeptEvidenceId}({duplicate.DisplayValue})")
            .ToArray();

    private static string[] FindNonRenderableSelectionEvidenceIds(
        EvidenceBundle bundle,
        IEnumerable<string> evidenceIds)
        => evidenceIds
            .Where(id => bundle.ById.TryGetValue(id, out var item)
                         && !HasRenderableEvidenceValue(item))
            .ToArray();

    private static IReadOnlySet<string> FindMechanicallyNonRenderableEvidenceIds(
        IEnumerable<EvidenceItem> evidence)
        => evidence
            .Where(IsMechanicallyCitableCandidate)
            .Where(static item => !HasRenderableEvidenceValue(item))
            .Select(static item => item.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void TraceMechanicallySuppressedEvidence(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        IReadOnlySet<string> evidenceIds)
    {
        if (evidenceIds.Count == 0)
            return;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.SourceVerifier,
            "source_backed_agent_v2.evidence.non_renderable_suppressed",
            ("turn", turn),
            ("evidence_ids", evidenceIds),
            ("count", evidenceIds.Count),
            ("decision_source", "mechanical_contract")));
    }

    private static void SuppressNonRenderableSelectionEvidence(
        IReadOnlyList<string> evidenceIds,
        ISet<string> observedEvidenceIdSet,
        List<string> observedEvidenceIds,
        ISet<string> pendingSemanticCandidateIds,
        LlmEvidenceWorkspace evidenceWorkspace)
    {
        foreach (var evidenceId in evidenceIds)
        {
            observedEvidenceIdSet.Remove(evidenceId);
            observedEvidenceIds.RemoveAll(id => string.Equals(
                id,
                evidenceId,
                StringComparison.OrdinalIgnoreCase));
            pendingSemanticCandidateIds.Remove(evidenceId);
            evidenceWorkspace.Reject(
                evidenceId,
                "Valeur textuelle absente: preuve mecaniquement non rendable.");
        }
    }
}
