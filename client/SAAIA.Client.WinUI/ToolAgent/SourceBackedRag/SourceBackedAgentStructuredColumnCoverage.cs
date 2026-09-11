using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record StructuredColumnCoverage(
        bool Applied,
        bool HasRequiredCoverage,
        int RequiredCellCount,
        int MatchedCellCount,
        int AnnotatedCandidateCount,
        int UnannotatedCandidateCount,
        IReadOnlyDictionary<string, int> CompatibleCandidateCountByColumn);

    private static StructuredColumnCoverage
        EvaluateStructuredColumnCoverage(
            EvidenceBundle bundle,
            IEnumerable<string> observedEvidenceIds,
            ISet<string> semanticallyAuditedEvidenceIds,
            ISet<string> semanticallyRejectedEvidenceIds,
            IReadOnlyList<string> rowLabels,
            IReadOnlyList<string> columnLabels)
    {
        if (rowLabels.Count == 0 || columnLabels.Count <= 1)
            return NoStructuredColumnCoverage();

        var candidates = observedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => semanticallyAuditedEvidenceIds.Contains(id)
                         && !semanticallyRejectedEvidenceIds.Contains(id))
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null
                                  && IsMechanicallyCitableCandidate(item))
            .Cast<EvidenceItem>()
            .DistinctBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .DistinctBy(
                static item => GetEvidenceDisplayValue(item),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentColumns = columnLabels.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var compatibleColumnsByCandidate = new List<HashSet<string>>();
        var unannotatedCandidateCount = 0;
        foreach (var candidate in candidates)
        {
            if (!TryReadCompatibleColumnLabels(
                    candidate,
                    out var compatibleColumns))
            {
                unannotatedCandidateCount++;
                continue;
            }
            compatibleColumnsByCandidate.Add(compatibleColumns
                .Where(currentColumns.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        if (compatibleColumnsByCandidate.Count == 0)
            return NoStructuredColumnCoverage();

        var requiredCellCount = checked(rowLabels.Count * columnLabels.Count);
        var slots = columnLabels
            .SelectMany(column => Enumerable.Repeat(column, rowLabels.Count))
            .ToArray();
        var slotOwners = Enumerable.Repeat(-1, slots.Length).ToArray();
        var orderedCandidates = compatibleColumnsByCandidate
            .Select((columns, index) => (columns, index))
            .OrderBy(static candidate => candidate.columns.Count)
            .ToArray();
        var matchedCellCount = 0;
        foreach (var candidate in orderedCandidates)
        {
            var visitedSlots = new bool[slots.Length];
            if (TryMatchStructuredColumnCandidate(
                    candidate.index,
                    compatibleColumnsByCandidate,
                    slots,
                    slotOwners,
                    visitedSlots))
            {
                matchedCellCount++;
            }
        }

        var capacities = columnLabels.ToDictionary(
            static label => label,
            label => compatibleColumnsByCandidate.Count(columns =>
                columns.Contains(label)),
            StringComparer.OrdinalIgnoreCase);
        return new StructuredColumnCoverage(
            Applied: true,
            HasRequiredCoverage: matchedCellCount >= requiredCellCount,
            requiredCellCount,
            matchedCellCount,
            compatibleColumnsByCandidate.Count,
            unannotatedCandidateCount,
            capacities);
    }

    private static bool TryMatchStructuredColumnCandidate(
        int candidateIndex,
        IReadOnlyList<HashSet<string>> compatibleColumnsByCandidate,
        IReadOnlyList<string> slots,
        int[] slotOwners,
        bool[] visitedSlots)
    {
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            if (visitedSlots[slotIndex]
                || !compatibleColumnsByCandidate[candidateIndex]
                    .Contains(slots[slotIndex]))
            {
                continue;
            }
            visitedSlots[slotIndex] = true;
            var previousOwner = slotOwners[slotIndex];
            if (previousOwner < 0
                || TryMatchStructuredColumnCandidate(
                    previousOwner,
                    compatibleColumnsByCandidate,
                    slots,
                    slotOwners,
                    visitedSlots))
            {
                slotOwners[slotIndex] = candidateIndex;
                return true;
            }
        }
        return false;
    }

    private static bool TryReadCompatibleColumnLabels(
        EvidenceItem item,
        out IReadOnlyList<string> columnLabels)
    {
        columnLabels = Array.Empty<string>();
        if (!item.SelectionHints.TryGetValue(
                SemanticCompatibleColumnLabelsHint,
                out var serializedLabels))
        {
            return false;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(
                serializedLabels,
                ClientJson.CamelCase);
            columnLabels = (parsed ?? Array.Empty<string>())
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .Select(static label => label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildStructuredColumnCoverageFeedback(
        StructuredColumnCoverage coverage,
        IReadOnlyList<string> columnLabels,
        int requiredRowsPerColumn)
        => "COUVERTURE SEMANTIQUE DU VIVIER INSUFFISANTE: les compatibilites "
           + "decidees par le LLM permettent un appariement distinct de "
           + coverage.MatchedCellCount
           + " cellule(s) sur "
           + coverage.RequiredCellCount
           + ". Capacites documentaires par colonne: "
           + string.Join(
               " | ",
               columnLabels.Select(label =>
                   label
                   + "="
                   + coverage.CompatibleCandidateCountByColumn.GetValueOrDefault(
                       label)
                   + "/"
                   + requiredRowsPerColumn))
           + ". Poursuis la collecte en ciblant librement les lacunes semantiques; "
           + "ne lance pas encore la redaction.";

    private static StructuredColumnCoverage NoStructuredColumnCoverage()
        => new(
            Applied: false,
            HasRequiredCoverage: false,
            0,
            0,
            0,
            0,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}
