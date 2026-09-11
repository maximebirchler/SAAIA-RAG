namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool SoftChoiceOptionKindContradictsHit(IReadOnlyList<string> requestedKindTerms, RagHitSummary hit)
    {
        if (requestedKindTerms.Count == 0)
            return false;

        var titles = ExtractSourceBackedTitleCandidates(hit)
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
        {
            var fallbackTitle = ExtractSourceBackedOptionTitle(hit, query: null);
            if (!string.IsNullOrWhiteSpace(fallbackTitle))
                titles.Add(fallbackTitle);
        }

        if (titles.Count == 0)
            return false;

        var candidates = titles
            .Select(title => new SourceBackedOptionCandidate(hit, title, Score: 0, VisibleMinutes: ExtractBestVisibleDurationMinutes(hit)))
            .ToList();
        if (candidates.Any(candidate => SoftChoiceOptionKindMatchesCandidate(requestedKindTerms, candidate)))
            return false;

        return candidates.Any(candidate => OptionKindContradictsCandidate(requestedKindTerms, candidate));
    }

    private static bool SoftChoiceOptionKindMatchesHit(IReadOnlyList<string> requestedKindTerms, RagHitSummary hit)
    {
        if (requestedKindTerms.Count == 0)
            return true;

        var titles = ExtractSourceBackedTitleCandidates(hit)
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsefulSourceBackedDisplayTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
        {
            var fallbackTitle = ExtractSourceBackedOptionTitle(hit, query: null);
            if (!string.IsNullOrWhiteSpace(fallbackTitle))
                titles.Add(fallbackTitle);
        }

        if (titles
            .Select(title => new SourceBackedOptionCandidate(hit, title, Score: 0, VisibleMinutes: ExtractBestVisibleDurationMinutes(hit)))
            .Any(candidate => SoftChoiceOptionKindMatchesCandidate(requestedKindTerms, candidate)))
        {
            return true;
        }

        var evidence = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        return requestedKindTerms.Any(term => OptionKindTermMatchesText(evidence, term)
                                             || RetrievalQueryNarrowlyTargetsOptionKind(hit.RetrievalQuery, term));
    }
}
