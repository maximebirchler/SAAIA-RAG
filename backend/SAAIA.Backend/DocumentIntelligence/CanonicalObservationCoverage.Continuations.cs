using System.Text.RegularExpressions;

internal static partial class CanonicalObservationCoverage
{
    private sealed record CoverageToken(string Value, string Raw, int Start, int Length, int[] SourceLines);
    private sealed record Continuation(CoverageToken Prefix, CoverageToken Mark, CoverageToken Suffix, string Combined);

    private static IEnumerable<CoverageToken> BuildCoverageTokens(
        IReadOnlyList<SourceLine> lines, IReadOnlyList<string[]> observations)
    {
        var knownWords = observations.SelectMany(value => value).ToHashSet(StringComparer.Ordinal);
        var normalized = lines.Select(line => NormalizeText(line.CoverageText)).ToArray();
        var tokens = normalized.Select((text, line) => InventoryToken().Matches(text).Cast<Match>()
            .Select(match => new CoverageToken(NormalizeToken(match.Value), match.Value,
                match.Index, match.Length, [line])).ToList()).ToArray();

        // First resolve only lexical bijections. Their complete words may be
        // needed when proving an entire observation in the contextual pass.
        for (var line = 0; line + 1 < lines.Count; line++)
            ResolveLine(line, allowContext: false);
        bool changed;
        do
        {
            changed = false;
            for (var line = 0; line + 1 < lines.Count; line++)
                changed |= ResolveLine(line, allowContext: true);
        } while (changed); // Each accepted join removes two tokens; this is finite.

        // A joined token belongs to both physical lines. Neither line can be
        // marked represented unless this exact reconstructed occurrence was
        // consumed by a complete primary observation; a same-word occurrence
        // elsewhere cannot stand in for it. Canonical source text is unchanged.
        return tokens.SelectMany(line => line);

        bool ResolveLine(int line, bool allowContext)
        {
            if (lines[line].FlowGroup != lines[line + 1].FlowGroup)
                return false;
            var current = tokens[line];
            var following = tokens[line + 1];
            var candidates = new List<Continuation>();
            for (var index = 0; index + 1 < current.Count; index++)
            {
                var prefix = current[index];
                var mark = current[index + 1];
                if (prefix.Raw.Length < 2 || !ContinuationWord().IsMatch(prefix.Raw)
                    || mark.Raw != "-" || prefix.Start + prefix.Length != mark.Start
                    || mark.Start + 1 < normalized[line].Length && !char.IsWhiteSpace(normalized[line][mark.Start + 1]))
                    continue;
                foreach (var suffix in following.Where(token => ContinuationWord().IsMatch(token.Raw)))
                {
                    var combined = prefix.Raw + suffix.Raw;
                    if (knownWords.Contains(NormalizeWord(combined)))
                        candidates.Add(new(prefix, mark, suffix, combined));
                }
            }

            var supported = candidates.Where(candidate => IsUniquePair(candidate, candidates)
                || allowContext && HasCompleteOrderedContext(candidate, current, following,
                    tokens.Where((_, index) => lines[index].FlowGroup == lines[line].FlowGroup)
                        .SelectMany(value => value), observations)).ToArray();
            // Context can remove lexical ambiguity, never replace source
            // occurrence uniqueness. A remaining ambiguous pair is untouched.
            var accepted = supported.Where(candidate => IsUniquePair(candidate, supported)).ToArray();
            foreach (var candidate in accepted)
            {
                var index = current.IndexOf(candidate.Prefix);
                current[index] = new(NormalizeWord(candidate.Combined), candidate.Combined,
                    candidate.Prefix.Start, candidate.Prefix.Length,
                    candidate.Prefix.SourceLines.Concat(candidate.Suffix.SourceLines).Distinct().ToArray());
                current.Remove(candidate.Mark);
                following.Remove(candidate.Suffix);
            }
            return accepted.Length > 0;
        }
    }

    private static bool IsUniquePair(Continuation candidate, IReadOnlyList<Continuation> candidates)
        => candidates.Count(other => ReferenceEquals(other.Prefix, candidate.Prefix)) == 1
            && candidates.Count(other => ReferenceEquals(other.Suffix, candidate.Suffix)) == 1;

    private static void PreserveUnrepresentedContinuationLines(
        IReadOnlyList<CoverageToken> source, int lineCount, HashSet<int> represented)
    {
        var neighbors = Enumerable.Range(0, lineCount).Select(_ => new HashSet<int>()).ToArray();
        foreach (var token in source.Where(token => token.SourceLines.Length > 1))
            foreach (var line in token.SourceLines)
                neighbors[line].UnionWith(token.SourceLines.Where(other => other != line));
        var pending = new Queue<int>(Enumerable.Range(0, lineCount).Where(line => !represented.Contains(line)));
        while (pending.TryDequeue(out var line))
            foreach (var neighbor in neighbors[line])
                if (represented.Remove(neighbor))
                    pending.Enqueue(neighbor);
        // A surviving unique fact on either half keeps both physical lines.
        // Otherwise selecting only the prefix could publish a broken word while
        // hiding its suffix, despite retaining the canonical raw observation.
    }

    [GeneratedRegex(@"\A[\p{L}\p{M}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationWord();
}
