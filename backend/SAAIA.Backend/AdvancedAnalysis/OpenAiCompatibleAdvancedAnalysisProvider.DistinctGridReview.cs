using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record DistinctSelectionReview(
        int RequiredSlots, int PriorDraftSupportedDistinctNames,
        bool CoordinateCoverageKnown, string Instruction);

    private static DistinctSelectionReview? BuildDistinctSelectionReviewHint(
        string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request)
    {
        var load = request.Handoff.Load;
        if (!RequiresDistinctStructuredSelection(load))
            return null;

        try
        {
            var draft = ParseResult(raw, evidence, request);
            var claims = draft.Claims.GroupBy(claim => claim.ClaimId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var visible = observations.Where(item => item.EvidenceId is not null)
                .ToLookup(item => item.EvidenceId!, StringComparer.Ordinal);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claim in claims.Values)
            {
                if (string.IsNullOrWhiteSpace(claim.SelectedItem)
                    || !claim.EvidenceIds.Any(id => visible[id].Any(item =>
                        EvidenceSupportsSelectedIdentity(claim.SelectedItem, item)
                        && GetVisibleEvidenceContents(item).Any(content =>
                            !string.IsNullOrWhiteSpace(content)
                            && !RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(
                                content, item.CandidateTitle, claim.SelectedItem)))))
                    continue;
                counted.Add(NormalizeClaimText(claim.SelectedItem));
            }
            return new(load.AnswerUnitCount, counted.Count, false,
                "Citation numbers do not establish occupied slots in a partial answer. "
                + "This count records distinct named items with visible supporting bodies only; "
                + "it does not establish suitability, placement or completeness. Inspect the actual draft and evidence to identify gaps.");
        }
        catch (AdvancedAnalysisProviderException)
        {
            // The regular parser still owns failure handling. An invalid prior
            // draft supplies no coverage hint and cannot create a citation.
        }

        return null;
    }
}
