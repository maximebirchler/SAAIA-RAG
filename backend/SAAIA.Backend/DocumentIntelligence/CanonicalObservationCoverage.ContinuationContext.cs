internal static partial class CanonicalObservationCoverage
{
    private static bool HasCompleteOrderedContext(Continuation candidate,
        IReadOnlyList<CoverageToken> current, IReadOnlyList<CoverageToken> following,
        IEnumerable<CoverageToken> flowTokens, IReadOnlyList<string[]> observations)
    {
        var prefix = Enumerable.Range(0, current.Count).Single(index => ReferenceEquals(current[index], candidate.Prefix));
        var suffix = Enumerable.Range(0, following.Count).Single(index => ReferenceEquals(following[index], candidate.Suffix));
        // A boundary fragment supplies no two-sided local order proof. Do not
        // invent a neighboring word from another line or another observation.
        if (prefix == 0 || suffix + 1 >= following.Count)
            return false;
        var before = current[prefix - 1].Value;
        var after = following[suffix + 1].Value;
        var joined = NormalizeWord(candidate.Combined);
        var available = flowTokens.Where(token => !ReferenceEquals(token, candidate.Prefix)
                && !ReferenceEquals(token, candidate.Mark) && !ReferenceEquals(token, candidate.Suffix))
            .GroupBy(token => token.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var observation in observations)
        {
            var orderedPosition = Enumerable.Range(1, Math.Max(0, observation.Length - 2))
                .Any(index => observation[index] == joined && observation[index - 1] == before
                    && observation[index + 1] == after);
            if (!orderedPosition)
                continue;
            var remaining = new Dictionary<string, int>(available, StringComparer.Ordinal);
            var forcedJoinConsumed = false;
            var complete = true;
            foreach (var value in observation)
            {
                // This particular joined occurrence must participate in the
                // proof; an identical word elsewhere cannot stand in for it.
                if (!forcedJoinConsumed && value == joined)
                {
                    forcedJoinConsumed = true;
                    continue;
                }
                if (!remaining.TryGetValue(value, out var count) || count == 0)
                {
                    complete = false;
                    break;
                }
                remaining[value] = count - 1;
            }
            if (complete && forcedJoinConsumed)
                return true;
        }
        return false;
    }
}
