namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<string> SelectGroupDiverseEvidenceIds(
        IEnumerable<string> evidenceIds,
        EvidenceBundle bundle,
        int capacity)
    {
        if (capacity <= 0)
            return Array.Empty<string>();

        var candidates = evidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .ToArray();
        return SelectGroupDiverseEvidenceItems(candidates, capacity)
            .Select(static item => item.EvidenceId)
            .ToArray();
    }

    private static IReadOnlyList<EvidenceItem> SelectCompactEvidenceItems(
        IReadOnlyList<EvidenceItem> retainedEvidence,
        IReadOnlyList<EvidenceItem> recentEvidence,
        int capacity)
    {
        if (capacity <= 0)
            return Array.Empty<EvidenceItem>();

        var selected = new List<EvidenceItem>(capacity);
        var selectedIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var retained in retainedEvidence)
        {
            if (selected.Count >= capacity)
                return selected;
            if (selectedIds.Add(retained.EvidenceId))
                selected.Add(retained);
        }

        foreach (var item in SelectGroupDiverseEvidenceItems(
                     recentEvidence.Where(item =>
                             !selectedIds.Contains(item.EvidenceId))
                         .ToArray(),
                     capacity - selected.Count))
        {
            if (selectedIds.Add(item.EvidenceId))
                selected.Add(item);
        }

        return selected;
    }

    private static IReadOnlyList<EvidenceItem> SelectGroupDiverseEvidenceItems(
        IReadOnlyList<EvidenceItem> evidence,
        int capacity)
    {
        if (capacity <= 0)
            return Array.Empty<EvidenceItem>();

        var selected = new List<EvidenceItem>(capacity);
        var selectedIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var sourceGroups = evidence
            .Where(item => !selectedIds.Contains(item.EvidenceId))
            .GroupBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.ToArray())
            .ToArray();

        // A compact state must first expose the breadth of mechanically
        // distinct visible sources. Further alternatives from the same source
        // are then added in rounds. This does not rank semantic suitability:
        // ordering remains the observation recency authored by the tool loop.
        for (var round = 0; selected.Count < capacity; round++)
        {
            var addedInRound = false;
            foreach (var sourceGroup in sourceGroups)
            {
                if (round >= sourceGroup.Length)
                    continue;

                var candidate = sourceGroup[round];
                if (!selectedIds.Add(candidate.EvidenceId))
                    continue;

                selected.Add(candidate);
                addedInRound = true;
                if (selected.Count >= capacity)
                    break;
            }

            if (!addedInRound)
                break;
        }

        return selected;
    }
}
