namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static IReadOnlyList<string> FindNonCanonicalStructuredCellClaims(
        string? answer,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string>? allowedEvidenceIds,
        out bool hasCanonicalCards)
    {
        var cards = SourceBackedCanonicalContentCardInventory.Build(
            intake,
            bundle,
            allowedEvidenceIds);
        hasCanonicalCards = cards.Count > 0;
        if (!hasCanonicalCards)
            return Array.Empty<string>();

        var claims = ExtractRequestedStructuredCellClaims(answer, intake);
        var invalid = new List<string>();
        foreach (var claim in claims)
        {
            var claimTitle = NormalizeCanonicalDisplayTitle(claim.ClaimText);
            var matches = cards.Any(card =>
                string.Equals(
                    NormalizeCanonicalDisplayTitle(card.Title),
                    claimTitle,
                    StringComparison.OrdinalIgnoreCase)
                && claim.EvidenceIds.Contains(card.EvidenceId, StringComparer.OrdinalIgnoreCase)
                && SourceBackedCanonicalContentCardInventory.IsValueRefAllowedForColumn(
                    intake,
                    claim.ColumnHeader,
                    card.ValueRef));
            if (!matches)
                invalid.Add($"{claim.RowLabel}/{claim.ColumnHeader}: {claim.ClaimText}");
        }

        return invalid;
    }

    private static string NormalizeCanonicalDisplayTitle(string? value)
        => SourceBackedCanonicalContentCardInventory.CollapseWhitespace(
            (value ?? string.Empty).Replace('|', ' '));
}
