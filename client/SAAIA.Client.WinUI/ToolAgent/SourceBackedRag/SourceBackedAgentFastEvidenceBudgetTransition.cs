namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record CumulativeBudgetAwareFastEvidenceReview(
        FastEvidenceReview? Review,
        bool TerminalBudgetOnly);

    private async Task<CumulativeBudgetAwareFastEvidenceReview>
        ReviewInitialEvidenceWithinCumulativeBudgetAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            EvidenceBundle bundle,
            IReadOnlyList<string> candidateEvidenceIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds,
            int maximumCandidateCount,
            CancellationToken ct)
    {
        try
        {
            var review = await ReviewInitialEvidenceAsync(
                    intake,
                    semanticPlan,
                    bundle,
                    candidateEvidenceIds,
                    semanticallyRejectedEvidenceIds,
                    maximumCandidateCount,
                    ct)
                .ConfigureAwait(false);
            return new CumulativeBudgetAwareFastEvidenceReview(
                review,
                review.SingleSelectionScopeTerminalBudgetOnly);
        }
        catch (SourceBackedLlmBudgetExceededException ex) when (
            string.Equals(
                ex.Reason,
                "terminal_budget_only",
                StringComparison.Ordinal))
        {
            // Admission failed before I/O. The runner will preserve the current
            // EvidenceBundle and re-enter through the existing terminal state
            // machine; no semantic decision is inferred here.
            return new CumulativeBudgetAwareFastEvidenceReview(
                Review: null,
                TerminalBudgetOnly: true);
        }
    }

    private static EvidenceBundle PreserveTerminalFastReviewSemanticSelection(
        FastEvidenceReview review,
        EvidenceBundle bundle,
        ISet<string> semanticallyAuditedEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds)
    {
        if (!review.SingleSelectionScopeTerminalBudgetOnly
            || !review.Ready
            || !review.AnchorVerified
            || review.EvidenceIds.Count != 1)
        {
            return bundle;
        }

        var evidenceId = review.EvidenceIds[0];
        if (semanticallyRejectedEvidenceIds.Contains(evidenceId)
            || review.PresentedEvidenceIds is null
            || !review.PresentedEvidenceIds.Contains(
                evidenceId,
                StringComparer.OrdinalIgnoreCase)
            || !bundle.ById.TryGetValue(evidenceId, out var selectedItem))
        {
            return bundle;
        }

        var semanticDisplayValue = GetEvidenceDisplayValue(selectedItem);
        if (string.IsNullOrWhiteSpace(semanticDisplayValue))
            return bundle;

        semanticallyAuditedEvidenceIds.Add(evidenceId);
        var items = bundle.Items.Select(item =>
        {
            if (!string.Equals(
                    item.EvidenceId,
                    evidenceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var hints = new Dictionary<string, string>(
                item.SelectionHints,
                StringComparer.OrdinalIgnoreCase)
            {
                [SemanticDisplayValueHint] = semanticDisplayValue
            };
            return item with { SelectionHints = hints };
        }).ToArray();
        return bundle with { Items = items };
    }

    private bool HasCurrentMechanicallyFinalizableEvidence(
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds,
        bool candidateAuditEnabled,
        IReadOnlySet<string> semanticallyAuditedEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        int? requiredAtomicEvidenceCount,
        string atomicEvidenceMode)
    {
        var eligibleIds = observedEvidenceIds
            .Where(id => !candidateAuditEnabled
                         || semanticallyAuditedEvidenceIds.Contains(id))
            .Where(id => !semanticallyRejectedEvidenceIds.Contains(id))
            .ToArray();
        var hasCitableEvidence = eligibleIds.Any(id =>
            bundle.ById.TryGetValue(id, out var item)
            && !item.RiskFlags.Contains(
                "orientation_only",
                StringComparer.OrdinalIgnoreCase));
        if (!hasCitableEvidence)
            return false;

        var observedCitableSourceCount =
            CountObservedSelectableEvidenceUnits(
                bundle,
                eligibleIds,
                atomicEvidenceMode);
        return requiredAtomicEvidenceCount.GetValueOrDefault() <= 1
               || observedCitableSourceCount
               >= requiredAtomicEvidenceCount.GetValueOrDefault();
    }
}
