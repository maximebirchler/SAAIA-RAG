namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedNamedDocumentIdentity
{
    internal const string MismatchRiskFlag =
        "requested_document_identity_mismatch";

    internal static bool TryGetResolvedRequestedIdentity(
        SourceBackedIntake intake,
        out SourceBackedDocumentResolutionCandidate candidate)
    {
        candidate = default!;
        var observation = intake.RequestedDocumentResolution;
        if (intake.DocumentScope != SourceBackedDocumentScope.RequestedDocument
            || observation is null
            || observation.Status != SourceBackedDocumentResolutionStatus.Resolved
            || !observation.CatalogObservationComplete
            || observation.Candidates.Count != 1)
        {
            return false;
        }
        candidate = observation.Candidates[0];
        return true;
    }

    internal static bool Matches(
        SourceBackedDocumentResolutionCandidate expected,
        EvidenceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.DocId))
        {
            if (!string.Equals(
                    expected.DocId,
                    item.DocId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!SourceBackedNamedDocumentResolver.IsExactMatch(
                     item.DocPath ?? item.DocName ?? string.Empty,
                     expected))
        {
            return false;
        }

        if (!MatchesOptionalCurrentIdentity(
                expected.RevisionId,
                item.RevisionId))
        {
            return false;
        }
        return MatchesOptionalCurrentIdentity(
            expected.SourceHash,
            item.SourceHash);
    }

    private static bool MatchesOptionalCurrentIdentity(
        string? expected,
        string? actual)
        => string.IsNullOrWhiteSpace(expected)
           || string.Equals(
               expected.Trim(),
               actual?.Trim(),
               StringComparison.OrdinalIgnoreCase);
}
