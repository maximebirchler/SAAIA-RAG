namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const int MaximumSemanticYieldResolutionContinuations = 1;

    private static int DetermineSemanticYieldResolutionContinuationLimit(
        int rejectedCount,
        int requiredCount,
        int executedRequestCount)
        => executedRequestCount >= 3
           || rejectedCount >= (long)Math.Max(1, requiredCount) * 2
            ? 0
            : MaximumSemanticYieldResolutionContinuations;

    private static bool ShouldRequestSemanticAuditZeroYieldResolution(
        int approvedCount,
        int rejectedCount,
        int requiredCount,
        int executedRequestCount,
        bool hasRequiredEvidence)
        => approvedCount == 0
           && rejectedCount > 0
           && executedRequestCount >= 2
           && !hasRequiredEvidence;

    private static void ApplyCandidateAuditYieldToRetrievalRequests(
        IList<RetrievalRequest> executedRequests,
        CandidateCollectionAuditDecision decision)
    {
        if (!decision.ProtocolValid || executedRequests.Count == 0)
            return;

        var approvedById = decision.ApprovedCandidates.ToDictionary(
            static candidate => candidate.EvidenceId,
            static candidate => candidate,
            StringComparer.OrdinalIgnoreCase);
        var rejectedIds = decision.RejectedEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (approvedById.Count == 0 && rejectedIds.Count == 0)
            return;

        for (var index = 0; index < executedRequests.Count; index++)
        {
            var request = executedRequests[index];
            if (request.NewEvidenceIds is not { Count: > 0 })
                continue;
            var approvedIds = request.NewEvidenceIds
                .Where(approvedById.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rejectedCount = request.NewEvidenceIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(rejectedIds.Contains);
            if (approvedIds.Length == 0 && rejectedCount == 0)
                continue;

            var compatibilityCounts = new Dictionary<string, int>(
                request.SemanticCompatibilityCounts
                ?? new Dictionary<string, int>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var label in approvedIds
                         .SelectMany(id => approvedById[id]
                             .CompatibleColumnLabels
                             ?? Array.Empty<string>())
                         .Where(static label =>
                             !string.IsNullOrWhiteSpace(label)))
            {
                compatibilityCounts[label] =
                    compatibilityCounts.GetValueOrDefault(label) + 1;
            }

            executedRequests[index] = request with
            {
                SemanticAuditApprovedCount =
                    request.SemanticAuditApprovedCount.GetValueOrDefault()
                    + approvedIds.Length,
                SemanticAuditRejectedCount =
                    request.SemanticAuditRejectedCount.GetValueOrDefault()
                    + rejectedCount,
                SemanticCompatibilityCounts = compatibilityCounts
            };
        }
    }

#if DEBUG
    internal static bool
        ShouldRequestSemanticAuditZeroYieldResolutionForTests(
            int approvedCount,
            int rejectedCount,
            int requiredCount,
            int executedRequestCount,
            bool hasRequiredEvidence = false)
        => ShouldRequestSemanticAuditZeroYieldResolution(
            approvedCount,
            rejectedCount,
            requiredCount,
            executedRequestCount,
            hasRequiredEvidence);
#endif
}
