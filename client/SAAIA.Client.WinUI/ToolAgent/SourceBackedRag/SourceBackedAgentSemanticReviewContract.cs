namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static SemanticReview EnforceSemanticReviewContract(
        SemanticReview review)
    {
        if (string.Equals(
                review.Decision,
                "revise",
                StringComparison.OrdinalIgnoreCase)
            && review.RejectedEvidenceIds.Count == 0
            && review.PreferredAlternativeEvidenceIds.Count > 0)
        {
            review = review with
            {
                PreferredAlternativeEvidenceIds = Array.Empty<string>(),
                ContractAdjusted = true
            };
        }

        if (!string.Equals(
                review.Decision,
                "revise",
                StringComparison.OrdinalIgnoreCase)
            || review.RejectedEvidenceIds.Count == 0
            || review.PreferredAlternativeEvidenceIds.Count > 0)
        {
            return review;
        }

        const string contractReason =
            "Revision non soutenue: aucun remplacement visible n'est nomme.";
        return review with
        {
            Decision = "need_more_evidence",
            Reasons = new[] { contractReason }
                .Concat(review.Reasons)
                .Take(4)
                .ToArray(),
            ContractAdjusted = true
        };
    }
}
