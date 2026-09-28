using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private IReadOnlyList<CandidateSupportCorrection> FindCandidateBindingCorrections(string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> candidateInventory,
        bool candidateDossierAvailable)
    {
        AdvancedAnalysisProviderResult proposal;
        try { proposal = ParseResult(raw, evidence, request); }
        catch (AdvancedAnalysisProviderException) { return []; }
        var corrections = FindIdentityOnlyCandidateClaims(proposal, evidence, request).ToList();
        var visible = observations.Select(e => e.EvidenceId).ToHashSet(StringComparer.Ordinal);
        foreach (var claim in proposal.Claims.Where(c => c.EvidenceIds.Any(id => !visible.Contains(id))))
            corrections.Add(new(claim.ClaimId, claim.SelectedItem ?? string.Empty, claim.EvidenceIds,
                "One or more cited IDs are absent from current evidence. An older observation or canonical job membership does not authorize a current citation. Use a current passage, research within remaining limits or revise the result yourself.",
                "candidate_evidence_not_visible"));
        if (!_options.CandidateBindingFeedbackEnabled || proposal.Outcome != "answered"
            || !RequiresDistinctStructuredSelection(request.Handoff.Load))
            return corrections;
        var duplicates = proposal.Claims.Where(c => !string.IsNullOrWhiteSpace(c.SelectedItem))
            .GroupBy(c => NormalizeClaimText(c.SelectedItem!), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var coordinates = BuildStructuredClaimCoordinates(request.Handoff.Load)
            .ToDictionary(item => item.ClaimId, item => item.ColumnLabel, StringComparer.Ordinal);
        foreach (var claim in proposal.Claims)
        {
            var name = claim.SelectedItem ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "This requested distinct atomic selection lacks its documentary item identity. Choose and name a concrete supported item yourself.", "candidate_identity_missing"));
            else if (duplicates.Contains(NormalizeClaimText(name)))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "This item identity is used for more than one requested distinct selection. Preserve distinct supported choices and correct the duplicate selection yourself.", "candidate_identity_duplicate"));
            else if (!claim.EvidenceIds.Any(id => observations.Any(e => e.EvidenceId == id
                         && EvidenceSupportsSelectedIdentity(name, e))))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "The selected identity does not exactly match its cited current excerpts under the final identity contract. Preserve internal words and source variants; correct the wording or association yourself. This lexical check does not establish a semantic contradiction or corpus absence.",
                    "candidate_identity_not_supported"));
            else if (candidateDossierAvailable
                     && coordinates.TryGetValue(claim.ClaimId, out var role)
                     && !candidateInventory.Any(candidate =>
                         candidate.Status is "body_verified" or "selected"
                         && candidate.TargetRoles.Contains(role, StringComparer.Ordinal)
                         && CandidateRoleSupportsClaimIdentity(
                             candidate,
                             claim,
                             name,
                             observations)))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "This item is not a body-verified Candidate Explorer choice for the requested coordinate role. Choose a distinct candidate whose targetRoles contains this column, or research and save a newly verified candidate with that role before selecting it.",
                    "candidate_role_not_verified"));
        }
        return corrections;
    }

    private static bool CandidateRoleSupportsClaimIdentity(
        CandidateInventoryItem candidate,
        AdvancedAnalysisResultClaim claim,
        string selectedItem,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        if (NormalizeClaimText(candidate.ExactTitle) == NormalizeClaimText(selectedItem)
            && candidate.BodyEvidenceIds.Any(id =>
                claim.EvidenceIds.Contains(id, StringComparer.Ordinal)))
        {
            return true;
        }

        // A grounded content card can expose a subordinate heading as its exact
        // card title while the same canonical body starts with the complete item
        // identity. The Explorer qualified that body for the role; allow the
        // Writer to refine only to an exact whole line from that same body/source.
        // This does not authorize a different excerpt, a partial phrase or a
        // role that the Explorer did not assign.
        var normalizedSelected = NormalizeClaimText(selectedItem);
        return normalizedSelected.Length > 0
               && candidate.BodyEvidenceIds.Any(id =>
                   claim.EvidenceIds.Contains(id, StringComparer.Ordinal)
                   && observations.Any(observation =>
                       string.Equals(observation.EvidenceId, id, StringComparison.Ordinal)
                       && string.Equals(observation.SourceKey, candidate.SourceKey, StringComparison.Ordinal)
                       && EvidenceContainsExactIdentityLine(
                           normalizedSelected,
                           observation.Content)));
    }

    private static bool EvidenceContainsExactIdentityLine(
        string normalizedSelected,
        string? content)
        => !string.IsNullOrWhiteSpace(content)
           && content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
               .Select(line => line.Trim(' ', '\t', '-', ':', ';', '.', ',', '|', '/', '\\', '(', ')', '*', '\u2022'))
               .Any(line => NormalizeClaimText(line) == normalizedSelected);

    internal static void EnsureVisibleClaimCitations(AdvancedAnalysisProviderResult result,
        IReadOnlyCollection<string> visibleEvidenceIds)
    {
        var visible = visibleEvidenceIds.ToHashSet(StringComparer.Ordinal);
        if (result.Claims.Any(c => c.EvidenceIds.Any(id => !visible.Contains(id))))
            throw new AdvancedAnalysisProviderException("advanced_final_evidence_not_visible");
    }
}
