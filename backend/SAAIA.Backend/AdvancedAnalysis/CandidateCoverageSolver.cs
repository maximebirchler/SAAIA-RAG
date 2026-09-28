namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record CandidateCoverageSlot(
    string SlotId,
    string TargetRole);

internal sealed record CandidateCoverageOption(
    string CandidateKey,
    IReadOnlyList<string> TargetRoles);

internal sealed record CandidateCoverageAssignment(
    string SlotId,
    string TargetRole,
    string CandidateKey);

internal sealed record CandidateCoverageSolution(
    int RequiredSlotCount,
    int MaximumAssignableCount,
    IReadOnlyList<CandidateCoverageAssignment> Assignments,
    IReadOnlyDictionary<string, int> MissingByRole)
{
    public bool Complete => MaximumAssignableCount == RequiredSlotCount;
}

internal static class CandidateCoverageSolver
{
    public static CandidateCoverageSolution Solve(
        IReadOnlyList<CandidateCoverageSlot> slots,
        IReadOnlyList<CandidateCoverageOption> candidates)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(candidates);

        if (slots.Any(slot => string.IsNullOrWhiteSpace(slot.SlotId)
                              || string.IsNullOrWhiteSpace(slot.TargetRole))
            || slots.Select(slot => slot.SlotId)
                .Distinct(StringComparer.Ordinal)
                .Count() != slots.Count)
        {
            throw new ArgumentException(
                "Coverage slots must have unique non-empty identifiers and roles.",
                nameof(slots));
        }

        var normalizedCandidates = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateKey))
            .GroupBy(candidate => candidate.CandidateKey, StringComparer.Ordinal)
            .Select(group => new CandidateCoverageOption(
                group.Key,
                group.SelectMany(candidate => candidate.TargetRoles)
                    .Where(role => !string.IsNullOrWhiteSpace(role))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(role => role, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(candidate => candidate.CandidateKey, StringComparer.Ordinal)
            .ToArray();
        var candidateRoles = normalizedCandidates
            .Select(candidate => candidate.TargetRoles.ToHashSet(StringComparer.Ordinal))
            .ToArray();
        var candidateToSlot = Enumerable.Repeat(-1, normalizedCandidates.Length).ToArray();
        var slotToCandidate = Enumerable.Repeat(-1, slots.Count).ToArray();

        bool TryAssign(int slotIndex, bool[] visitedCandidates)
        {
            for (var candidateIndex = 0;
                 candidateIndex < normalizedCandidates.Length;
                 candidateIndex++)
            {
                if (visitedCandidates[candidateIndex]
                    || !candidateRoles[candidateIndex].Contains(slots[slotIndex].TargetRole))
                {
                    continue;
                }

                visitedCandidates[candidateIndex] = true;
                var previousSlot = candidateToSlot[candidateIndex];
                if (previousSlot >= 0
                    && !TryAssign(previousSlot, visitedCandidates))
                {
                    continue;
                }

                candidateToSlot[candidateIndex] = slotIndex;
                slotToCandidate[slotIndex] = candidateIndex;
                return true;
            }

            return false;
        }

        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
            TryAssign(slotIndex, new bool[normalizedCandidates.Length]);

        var assignments = slots.Select((slot, slotIndex) => new
            {
                Slot = slot,
                CandidateIndex = slotToCandidate[slotIndex]
            })
            .Where(item => item.CandidateIndex >= 0)
            .Select(item => new CandidateCoverageAssignment(
                item.Slot.SlotId,
                item.Slot.TargetRole,
                normalizedCandidates[item.CandidateIndex].CandidateKey))
            .ToArray();
        var assignedSlots = assignments.Select(assignment => assignment.SlotId)
            .ToHashSet(StringComparer.Ordinal);
        var missingByRole = slots.Where(slot => !assignedSlots.Contains(slot.SlotId))
            .GroupBy(slot => slot.TargetRole, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new CandidateCoverageSolution(
            slots.Count,
            assignments.Length,
            assignments,
            missingByRole);
    }
}
