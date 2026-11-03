using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record DistinctGridReviewColumn(
        string ColumnLabel, int RequiredSlots, int PriorDraftSupportedNames);

    private static IReadOnlyList<DistinctGridReviewColumn>? BuildDistinctGridReviewHint(
        string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request)
    {
        var load = request.Handoff.Load;
        if (!RequiresDistinctStructuredSelection(load))
            return null;

        var coordinates = BuildStructuredClaimCoordinates(load);
        if (coordinates.Count != load.AnswerUnitCount || coordinates.Count == 0)
            return null;

        var supportedByColumn = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var draft = ParseResult(raw, evidence, request);
            var claims = draft.Claims.GroupBy(claim => claim.ClaimId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var visible = observations.Where(item => item.EvidenceId is not null)
                .ToLookup(item => item.EvidenceId!, StringComparer.Ordinal);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var coordinate in coordinates)
            {
                if (!claims.TryGetValue(coordinate.ClaimId, out var claim)
                    || string.IsNullOrWhiteSpace(claim.SelectedItem)
                    || !claim.EvidenceIds.Any(id => visible[id].Any(item =>
                        EvidenceSupportsSelectedIdentity(claim.SelectedItem, item)
                        && GetVisibleEvidenceContents(item).Any(content =>
                            !string.IsNullOrWhiteSpace(content)
                            && !RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(
                                content, item.CandidateTitle, claim.SelectedItem))))
                    || !counted.Add(NormalizeClaimText(claim.SelectedItem)))
                    continue;

                supportedByColumn[coordinate.ColumnLabel] =
                    supportedByColumn.GetValueOrDefault(coordinate.ColumnLabel) + 1;
            }
        }
        catch (AdvancedAnalysisProviderException)
        {
            // The regular parser still owns failure handling. An invalid prior
            // draft supplies no coverage hint and cannot create a citation.
        }

        return coordinates.GroupBy(coordinate => coordinate.ColumnLabel, StringComparer.Ordinal)
            .Select(group => new DistinctGridReviewColumn(
                group.Key, group.Count(), supportedByColumn.GetValueOrDefault(group.Key)))
            .ToArray();
    }
}
