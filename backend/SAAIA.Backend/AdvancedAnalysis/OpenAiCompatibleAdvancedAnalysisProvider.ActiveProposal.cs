namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static void RememberActiveProposalEvidence(string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> observations,
        AdvancedAnalysisProviderRequest request, SynthesisResearchContext context)
    {
        AdvancedAnalysisProviderResult proposal;
        try { proposal = ParseResult(raw, evidence, request); }
        catch (AdvancedAnalysisProviderException) { return; }
        RememberActiveProposalEvidence(proposal, observations, context);
    }

    private static void RememberActiveProposalEvidence(AdvancedAnalysisProviderResult proposal,
        IReadOnlyList<PromptEvidenceItem> observations, SynthesisResearchContext context)
    {
        var visible = observations.Select(e => e.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var chosen = proposal.Claims.SelectMany(c => c.EvidenceIds)
            .Where(visible.Contains).Distinct(StringComparer.Ordinal).ToArray();
        context.ActiveProposalEvidenceIds = chosen.Take(256).ToArray();
        context.ActiveProposalOmittedEvidenceCount = Math.Max(0, chosen.Length - 256);
    }

    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeActiveProposalEvidence(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<string> selectedIds,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
    {
        var byId = evidence.ToDictionary(e => e.Reference.EvidenceId!, StringComparer.Ordinal);
        var selected = selectedIds.Distinct(StringComparer.Ordinal).Where(byId.ContainsKey).ToArray();
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        return selected.Select(id => byId[id])
            .Concat(focused.Where(e => !ids.Contains(e.Reference.EvidenceId!))).ToArray();
    }
}
