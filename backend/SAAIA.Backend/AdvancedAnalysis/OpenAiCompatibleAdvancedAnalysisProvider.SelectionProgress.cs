using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    internal sealed record SelectionProgress(
        int ClaimCount,
        int NamedClaimCount,
        int DistinctNamedItemCount,
        int ClaimsWithoutSelectedItem,
        int RequiredCount,
        int RemainingSelectionCount,
        bool SuitabilityVerified = false);

    internal static SelectionProgress? DescribeSelectionProgress(
        AdvancedAnalysisLoadDescriptor load,
        IReadOnlyList<AdvancedAnalysisResultClaim> claims)
    {
        var mode = ResolveSelectionMode(load);
        if (mode is not ("distinct_named_items" or "repeatable_named_items"))
            return null;
        var identities = claims.Select(claim => NormalizeClaimText(claim.SelectedItem ?? string.Empty))
            .Where(identity => identity.Length > 0).ToArray();
        var distinct = identities.Distinct(StringComparer.Ordinal).Count();
        // This is arithmetic over a proposal, not documentary or semantic approval.
        var count = mode == "distinct_named_items" ? distinct : identities.Length;
        return new(claims.Count, identities.Length, distinct, claims.Count - identities.Length,
            load.AnswerUnitCount, Math.Max(0, load.AnswerUnitCount - count));
    }

}
