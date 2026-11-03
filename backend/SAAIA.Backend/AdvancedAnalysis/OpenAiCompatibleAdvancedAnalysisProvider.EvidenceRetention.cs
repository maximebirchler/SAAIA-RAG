namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static void RememberPreviousCitedEvidence(string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request, SynthesisResearchContext context)
    {
        AdvancedAnalysisProviderResult proposal;
        try { proposal = ParseResult(raw, evidence, request); }
        catch (AdvancedAnalysisProviderException) { return; }
        var visible = observations.Where(item => GetVisibleEvidenceContents(item)
                .Any(content => !string.IsNullOrWhiteSpace(content)))
            .Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        // Independent of the optional active-proposal feature: only a bounded
        // ordering hint, never copied text or an extra working-set protection.
        context.PreviousCitedEvidenceIds = proposal.Claims.SelectMany(claim => claim.EvidenceIds)
            .Where(visible.Contains).Distinct(StringComparer.Ordinal).Take(256).ToArray();
    }

    private static IReadOnlyCollection<string> RetainedResearchEvidenceIds(
        SynthesisResearchContext context, IReadOnlyList<PromptEvidenceItem> observations)
    {
        RememberActiveExplicitReadIds(context, []);
        // Remember IDs from this synthesis-loop projection, also supplied again
        // before a research batch. Replace this hint; never add an extra pin.
        var active = context.Tools.Evidence.Select(item => item.Reference.EvidenceId).ToHashSet(StringComparer.Ordinal);
        context.PreviousProjectionEvidenceIds = observations.Select(item => item.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && active.Contains(id))
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        return observations.Select(item => item.EvidenceId)
            .Concat(context.ActiveProposalEvidenceIds)
            .Concat(context.Workspace.Where(item => item.Status != "rejected").SelectMany(item => item.EvidenceIds))
            .Concat(context.CandidateInventory.Where(item => item.Status != "rejected")
                .SelectMany(item => item.LocatorEvidenceIds.Concat(item.BodyEvidenceIds)))
            .Concat(context.EvidenceReadWindows.Keys)
            .Concat(context.LatestExplicitReadEvidenceIds)
            .Concat(context.CandidateExplorerDossier?.BodyEvidenceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void PruneRetiredEvidenceCopies(SynthesisResearchContext context)
    {
        if (context.Tools.Budget?.EvidenceRotationEnabled is not true) return;
        var activeIds = context.Tools.Evidence.Select(item => item.Reference.EvidenceId).ToHashSet(StringComparer.Ordinal);
        context.PreviousProjectionEvidenceIds = context.PreviousProjectionEvidenceIds.Where(activeIds.Contains).ToArray();
        if (context.DiscoveryBaselineEvidenceIds is { } baseline)
            context.DiscoveryBaselineEvidenceIds = baseline.Where(activeIds.Contains).ToArray();
        context.PreviousCitedEvidenceIds = context.PreviousCitedEvidenceIds.Where(activeIds.Contains).ToArray();
        for (var index = context.EvidenceGroups.Count - 1; index >= 0; index--)
        {
            var retained = context.EvidenceGroups[index]
                .Where(item => activeIds.Contains(item.Reference.EvidenceId)).ToArray();
            if (retained.Length == 0) context.EvidenceGroups.RemoveAt(index);
            else context.EvidenceGroups[index] = retained;
        }
        foreach (var id in context.RetrievalQueriesByEvidenceId.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            context.RetrievalQueriesByEvidenceId.Remove(id);
        context.ObservedCandidateEvidenceIds.IntersectWith(activeIds);
        context.CandidateInventory = context.CandidateInventory.Where(item => item.Status != "rejected"
            || item.LocatorEvidenceIds.Concat(item.BodyEvidenceIds).All(activeIds.Contains)).ToArray();
        context.Workspace = context.Workspace.Where(item => item.Status != "rejected"
            || item.EvidenceIds.All(activeIds.Contains)).ToArray();
        // NativeTurn holds only the last bounded batch. Its tool-history renderer
        // exposes documentary text solely through the current active projection.
        // Candidate, workspace and read-window references are pinned, not pruned.
    }
}
