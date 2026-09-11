namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static IReadOnlyList<string> FindCanonicalStructuredDiversityProblems(
        string? answer,
        SourceBackedIntake intake)
    {
        var policy = intake.CanonicalDiversityPolicy;
        if (policy is null)
            return Array.Empty<string>();

        var claims = ExtractRequestedStructuredCellClaims(answer, intake);
        if (claims.Count == 0)
            return Array.Empty<string>();

        var problems = new List<string>();
        foreach (var pair in policy.MinimumDistinctValuesPerColumn)
        {
            var actual = claims
                .Where(claim => string.Equals(
                    SourceBackedStructuredTableShapeBuilder.NormalizeLabel(claim.ColumnHeader),
                    SourceBackedStructuredTableShapeBuilder.NormalizeLabel(pair.Key),
                    StringComparison.OrdinalIgnoreCase))
                .Select(static claim => NormalizeCanonicalDisplayTitle(claim.ClaimText))
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (actual < pair.Value)
            {
                problems.Add(
                    $"column={pair.Key}; minimumDistinct={pair.Value}; actualDistinct={actual}");
            }
        }

        var overall = claims
            .Select(static claim => NormalizeCanonicalDisplayTitle(claim.ClaimText))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (overall < policy.MinimumDistinctValuesOverall)
        {
            problems.Add(
                $"overall minimumDistinct={policy.MinimumDistinctValuesOverall}; actualDistinct={overall}");
        }

        return problems;
    }
}
