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
        var corrections = FindIdentityOnlyCandidateClaims(proposal, observations, request).ToList();
        var visible = observations.Where(e => GetVisibleEvidenceContents(e)
                .Any(content => !string.IsNullOrWhiteSpace(content)))
            .Select(e => e.EvidenceId).ToHashSet(StringComparer.Ordinal);
        foreach (var claim in proposal.Claims.Where(c => c.EvidenceIds.Any(id => !visible.Contains(id))))
            corrections.Add(new(claim.ClaimId, claim.SelectedItem ?? string.Empty, claim.EvidenceIds,
                "One or more cited IDs are absent from current evidence. An older observation or canonical job membership does not authorize a current citation. Use a current passage, research within remaining limits or revise the result yourself.",
                "candidate_evidence_not_visible"));
        var requiresDistinct = RequiresDistinctStructuredSelection(request.Handoff.Load);
        var namedSelection = request.Handoff.Load.AtomicEvidenceMode.Contains(
            "named_item", StringComparison.OrdinalIgnoreCase);
        var repeatable = UsesRepeatableGridIdentityCorrection(request.Handoff.Load);
        if ((!_options.CandidateBindingFeedbackEnabled && !repeatable) || proposal.Outcome != "answered"
            || !requiresDistinct && !namedSelection)
            return corrections;
        var duplicates = proposal.Claims.Where(c => requiresDistinct && !string.IsNullOrWhiteSpace(c.SelectedItem))
            .GroupBy(c => NormalizeClaimText(c.SelectedItem!), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var coordinates = BuildStructuredClaimCoordinates(request.Handoff.Load)
            .ToDictionary(item => item.ClaimId, item => item.ColumnLabel, StringComparer.Ordinal);
        foreach (var claim in proposal.Claims)
        {
            var name = claim.SelectedItem ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (requiresDistinct)
                    corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                        "This requested distinct atomic selection lacks its documentary item identity. Choose and name a concrete supported item yourself.", "candidate_identity_missing"));
            }
            else if (duplicates.Contains(NormalizeClaimText(name)))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "This item identity is used for more than one requested distinct selection. Preserve distinct supported choices and correct the duplicate selection yourself.", "candidate_identity_duplicate"));
            else if (!claim.EvidenceIds.Any(id => observations.Any(e => e.EvidenceId == id
                         && EvidenceSupportsSelectedIdentity(name, e))))
                corrections.Add(new(claim.ClaimId, name, claim.EvidenceIds,
                    "The selected identity does not exactly match its cited current excerpts under the final identity contract. Preserve internal words and source variants; correct the wording or association yourself. This lexical check does not establish a semantic contradiction or corpus absence.",
                    "candidate_identity_not_supported"));
            else if (_options.CandidateBindingFeedbackEnabled && candidateDossierAvailable
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

    private static bool UsesRepeatableGridIdentityCorrection(AdvancedAnalysisLoadDescriptor load)
        // The broad prompt mode can classify a single named selection as
        // repeatable. Recovery here requires an actual, complete slot grid.
        => ResolveSelectionMode(load) == "repeatable_named_items"
           && string.Equals(load.SelectionPolicy, "structured_layout", StringComparison.OrdinalIgnoreCase)
           && load.AnswerUnitCount > 1
           && load.RowCount > 0 && load.ColumnCount > 0
           && (long)load.RowCount * load.ColumnCount == load.AnswerUnitCount
           && load.RowLabels.Count >= load.RowCount && load.Columns.Count >= load.ColumnCount;

    private static bool HasUnsupportedNamedSelectionIdentity(
        AdvancedAnalysisProviderResult result,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        if (result.Outcome != "answered"
            || !request.Handoff.Load.AtomicEvidenceMode.Contains(
                "named_item", StringComparison.OrdinalIgnoreCase))
            return false;
        return result.Claims.Any(claim => !string.IsNullOrWhiteSpace(claim.SelectedItem)
            && !claim.EvidenceIds.Any(id => observations.Any(observation =>
                string.Equals(observation.EvidenceId, id, StringComparison.Ordinal)
                && EvidenceSupportsSelectedIdentity(claim.SelectedItem!, observation))));
    }

    private static AdvancedAnalysisProviderResult BuildUnverifiedSelectionIdentityResult(
        AdvancedAnalysisProviderRequest request)
        => new()
        {
            Outcome = "insufficient_documentation",
            AnswerText = request.Handoff.Language.Trim().ToLowerInvariant() switch
            {
                "en" => "I could not verify the exact identities of all required items against the passages examined.",
                "de" => "Ich konnte die genaue Identität nicht für alle erforderlichen Einträge anhand der geprüften Textstellen bestätigen.",
                _ => "Je n'ai pas pu vérifier l'identité exacte de tous les éléments requis dans les passages examinés."
            }
        };

    private static IReadOnlyList<CandidateSupportCorrection> FindIdentityOnlyCandidateClaims(
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request)
    {
        if (!request.Handoff.Load.StructuredLayout
            || !request.Handoff.Load.AtomicEvidenceMode.Contains("named_item", StringComparison.OrdinalIgnoreCase)
            || result.Outcome != "answered")
            return [];

        var visible = observations.Where(item => item.EvidenceId is not null
                && GetVisibleEvidenceContents(item).Any(content => !string.IsNullOrWhiteSpace(content)))
            .ToDictionary(item => item.EvidenceId!, StringComparer.Ordinal);
        return result.Claims.Where(claim => !string.IsNullOrWhiteSpace(claim.SelectedItem)
                && claim.EvidenceIds.All(id => !visible.TryGetValue(id, out var current)
                    || GetVisibleEvidenceContents(current).All(content =>
                        RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(
                            content, current.CandidateTitle, claim.SelectedItem!))))
            .Select(claim => new CandidateSupportCorrection(claim.ClaimId, claim.SelectedItem!,
                claim.EvidenceIds,
                "The currently visible passages only locate or name this selected item. Read its substantive canonical content with read_evidence, read_source or search_corpus within remaining limits. Hidden characters elsewhere in the same canonical chunk are not current proof. Retain a locator only as additional same-source scope; this correction does not establish corpus absence."))
            .ToArray();
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
                       && GetVisibleEvidenceContents(observation).Any(content =>
                           EvidenceContainsExactIdentityLine(normalizedSelected, content))));
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
