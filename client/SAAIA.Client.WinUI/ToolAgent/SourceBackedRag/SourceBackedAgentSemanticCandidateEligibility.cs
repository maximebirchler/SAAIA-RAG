namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool IsMechanicallyCitableCandidate(EvidenceItem item)
        => !item.RiskFlags.Contains(
               "orientation_only",
               StringComparer.OrdinalIgnoreCase)
           && !SourceBackedContentCardEvidenceContract
               .IsProoflessCanonicalContentCard(item)
           && IsRequestedDocumentIdentityEligible(item)
           && !string.Equals(
               item.SourceKind,
               "navigation_map",
               StringComparison.OrdinalIgnoreCase)
           && !string.Equals(
               item.ToolName,
               "documents.navigation",
               StringComparison.OrdinalIgnoreCase)
           && (!string.IsNullOrWhiteSpace(item.DocPath)
               || !string.IsNullOrWhiteSpace(item.DocName))
           && item.PageStart is > 0;
}
