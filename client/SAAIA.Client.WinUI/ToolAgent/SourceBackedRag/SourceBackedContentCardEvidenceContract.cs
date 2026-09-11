using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedContentCardEvidenceContract
{
    internal const string MissingGroundedEvidenceRisk =
        "missing_grounded_content_card_evidence";

    internal static bool IsProoflessCanonicalContentCard(EvidenceItem item)
        => string.Equals(
               item.SourceKind,
               "canonical_content_card",
               StringComparison.OrdinalIgnoreCase)
           && !HasEffectiveGroundedEvidence(item);

    internal static bool HasEffectiveGroundedEvidence(EvidenceItem item)
    {
        if (!string.Equals(
                item.SourceKind,
                "canonical_content_card",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.RiskFlags.Contains(
                MissingGroundedEvidenceRisk,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.MatchedContentCards is not { } cards
            || cards.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind == JsonValueKind.Object
                && !string.IsNullOrWhiteSpace(
                    EvidenceBundleBuilder.ReadContentCardProof(card)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSha256Hex(string? value)
        => value?.Trim() is { Length: 64 } trimmed
           && trimmed.All(static character =>
               character is >= '0' and <= '9'
               or >= 'a' and <= 'f'
               or >= 'A' and <= 'F');
}
