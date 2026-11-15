using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static object? DescribeSynthesisSelectionValidation(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult candidate,
        IReadOnlyList<PromptEvidenceItem> evidence)
    {
        // These are the same checks that would reject the final complete grid.
        // Partial findings do not assert complete coverage or require a selected name.
        if (candidate.Outcome != "answered" || !RequiresDistinctStructuredSelection(request.Handoff.Load))
            return null;

        var validation = AnalyzeDistinctSelectedItems(request.Handoff.Load, candidate, evidence);
        var duplicateNames = candidate.Claims.Where(c => !string.IsNullOrWhiteSpace(c.SelectedItem))
            .GroupBy(c => NormalizeClaimText(c.SelectedItem!), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return new
        {
            validation.IsValid,
            validation.MissingIdentityCount,
            validation.DuplicateIdentityCount,
            validation.UnsupportedIdentityCount,
            validation.UnsupportedIdentities,
            missingIdentityClaimIds = candidate.Claims.Where(c => string.IsNullOrWhiteSpace(c.SelectedItem))
                .Select(c => c.ClaimId).ToArray(),
            duplicateIdentityClaimIds = candidate.Claims.Where(c => duplicateNames.Contains(NormalizeClaimText(c.SelectedItem ?? "")))
                .Select(c => c.ClaimId).ToArray(),
            unsupportedIdentityClaimIds = candidate.Claims.Where(c => validation.UnsupportedIdentities.Contains(
                    (c.SelectedItem ?? "").Trim(), StringComparer.Ordinal))
                .Select(c => c.ClaimId).ToArray(),
            documentaryEvidence = false,
            suitabilityVerified = false,
            instruction = "These are deterministic selection diagnostics on the prior draft and its cited visible passages, not source facts or semantic verdicts. Repair the affected claims and their displayed selections together. Preserve unaffected supported findings. If a complete repair is not supported, keep an explicitly partial answer rather than repeating the rejected selection or discarding all useful findings."
        };
    }
}
