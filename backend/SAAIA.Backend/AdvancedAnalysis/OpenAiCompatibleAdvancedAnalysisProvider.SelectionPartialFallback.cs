using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static AdvancedAnalysisProviderResult? PreserveSupportedDistinctSelections(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult candidate,
        IReadOnlyList<PromptEvidenceItem> evidence)
    {
        // The complete proposal already passed result parsing. A failed final
        // identity check must not erase its independently supported selections.
        // Keep the original claims and coordinates; never reconstruct the
        // rejected identity or reuse the complete answer's success wording.
        var identityOnly = FindIdentityOnlyCandidateClaims(candidate, evidence, request)
            .Select(c => c.ClaimId).ToHashSet(StringComparer.Ordinal);
        var visible = evidence.Where(e => e.EvidenceId is not null
                && GetVisibleEvidenceContents(e).Any(c => !string.IsNullOrWhiteSpace(c)))
            .Select(e => e.EvidenceId!).ToHashSet(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var retained = candidate.Claims.Where(c => !string.IsNullOrWhiteSpace(c.SelectedItem)
                && !identityOnly.Contains(c.ClaimId)
                && c.EvidenceIds.Count > 0 && c.EvidenceIds.All(visible.Contains)
                && evidence.Any(e => c.EvidenceIds.Contains(e.EvidenceId!, StringComparer.Ordinal)
                    && EvidenceSupportsSelectedIdentity(c.SelectedItem!, e)))
            .Where(c => identities.Add(NormalizeClaimText(c.SelectedItem!))).ToList();
        using var probe = BackendDiagnostics.Start("llm.selection_partial_fallback");
        probe.Set("job_id", request.JobId);
        probe.Set("candidate_claim_count", candidate.Claims.Count);
        probe.Set("retained_claim_count", retained.Count);
        probe.Set("removed_claim_count", candidate.Claims.Count - retained.Count);
        probe.Complete(retained.Count > 0 ? "partial_preserved" : "no_supported_selection",
            "identity_and_citation_checks_not_semantic_approval");
        if (retained.Count == 0)
            return null;

        var remaining = Math.Max(0, request.Handoff.Load.AnswerUnitCount - retained.Count);
        var introduction = request.Handoff.Language.Trim().ToLowerInvariant() switch
        {
            "en" => $"Partial answer: I could not verify the complete selection. Here are {retained.Count} proposals retained with their sources; {remaining} choices remain to be checked.",
            "de" => $"Teilantwort: Ich konnte die gesamte Auswahl nicht bestätigen. Hier sind {retained.Count} beibehaltene Vorschläge mit ihren Quellen; {remaining} Auswahlen bleiben zu prüfen.",
            _ => $"Réponse partielle : je n'ai pas pu vérifier toute la sélection. Propositions conservées avec leurs sources : {retained.Count}. Il reste {remaining} choix à vérifier."
        };
        var coordinates = BuildStructuredClaimCoordinates(request.Handoff.Load)
            .ToDictionary(c => c.ClaimId, StringComparer.Ordinal);
        var lines = retained.Select(c =>
        {
            var placement = coordinates.TryGetValue(c.ClaimId, out var coordinate)
                ? $"{EscapePartialSelectionLabel(coordinate.RowLabel)} / {EscapePartialSelectionLabel(coordinate.ColumnLabel)} : "
                : string.Empty;
            return $"- {placement}{EscapePartialSelectionLabel(c.SelectedItem!)} [{c.ClaimId}]";
        });
        return new AdvancedAnalysisProviderResult
        {
            Outcome = "insufficient_documentation",
            AnswerText = introduction + "\n\n" + string.Join("\n", lines),
            Claims = retained
        };
    }

    private static string EscapePartialSelectionLabel(string value)
    {
        var escaped = value.Replace("\r", " ").Replace("\n", " ");
        foreach (var character in new[] { '\\', '`', '*', '_', '{', '}', '[', ']', '<', '>', '|' })
            escaped = escaped.Replace(character.ToString(), "\\" + character);
        return escaped;
    }
}
