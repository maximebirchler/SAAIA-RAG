using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    internal static void EnsureAnsweredClaimCoordinates(
        string outcome,
        IReadOnlyList<AdvancedAnalysisResultClaim> claims,
        AdvancedAnalysisLoadDescriptor load)
    {
        if (outcome != "answered")
            return;

        // A claim ID identifies an answer unit. Reusing it can make several
        // selections share the same marker and bypass per-coordinate checks.
        var claimIds = claims.Select(claim => claim.ClaimId)
            .ToHashSet(StringComparer.Ordinal);
        if (claimIds.Count != claims.Count)
            throw new AdvancedAnalysisProviderException(
                "advanced_writer_claim_coordinates_invalid");

        if (!load.StructuredLayout)
            return;

        var requestedCount = load.AnswerUnitCount;
        var coordinates = BuildStructuredClaimCoordinates(load);
        var expectedIds = coordinates.Take(requestedCount)
            .Select(coordinate => coordinate.ClaimId)
            .ToHashSet(StringComparer.Ordinal);
        if (requestedCount <= 0
            || expectedIds.Count != requestedCount
            || !expectedIds.SetEquals(claimIds))
            throw new AdvancedAnalysisProviderException(
                "advanced_writer_claim_coordinates_invalid");
    }
}
