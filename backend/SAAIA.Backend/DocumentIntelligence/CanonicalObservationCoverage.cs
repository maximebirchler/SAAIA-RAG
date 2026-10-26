using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>
/// Matches complete primary observations to source occurrences. Matching an
/// unordered complete observation selects the existing primary; it never proves
/// reading order. A word subset cannot stand in for a complete observation.
/// </summary>
internal static partial class CanonicalObservationCoverage
{
    internal sealed record SourceLine(int Start, int End, string Text, string CoverageText, string FlowGroup = "");

    internal sealed class Inventory
    {
        private readonly string[][] observations;

        internal Inventory(IEnumerable<string> values)
            => observations = values.Select(value => Tokens(value).ToArray())
                .Where(tokens => tokens.Length > 0).OrderByDescending(tokens => tokens.Length).ToArray();

        internal IReadOnlySet<int> RepresentedLines(IReadOnlyList<SourceLine> lines)
        {
            var source = BuildCoverageTokens(lines, observations).ToArray();
            var used = new bool[source.Length];
            foreach (var observation in observations)
            {
                var match = FindOrdered(observation);
                if (match is null)
                {
                    // A complete observation may be split by another column in
                    // the native page reading. Consume all of its word/value
                    // occurrences, never a partial subset of that observation.
                    var available = source.Select((token, index) => (token, index))
                        .Where(item => !used[item.index]).GroupBy(item => item.token.Value, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(item => item.index)), StringComparer.Ordinal);
                    var proposed = new List<int>();
                    foreach (var value in observation)
                    {
                        if (!available.TryGetValue(value, out var positions) || positions.Count == 0)
                        {
                            proposed.Clear();
                            break;
                        }
                        proposed.Add(positions.Dequeue());
                    }
                    if (proposed.Count == observation.Length)
                        match = proposed.ToArray();
                }
                if (match is not null)
                    foreach (var index in match)
                        used[index] = true;
            }

            var represented = new HashSet<int>();
            for (var line = 0; line < lines.Count; line++)
            {
                var indices = Enumerable.Range(0, source.Length)
                    .Where(index => source[index].SourceLines.Contains(line)).ToArray();
                if (indices.Length == 0 || indices.Any(index => !used[index]))
                    continue;
                represented.Add(line);
            }
            PreserveUnrepresentedContinuationLines(source, lines.Count, represented);
            return represented;

            int[]? FindOrdered(string[] value)
            {
                for (var start = 0; start <= source.Length - value.Length; start++)
                {
                    var equal = true;
                    for (var offset = 0; offset < value.Length; offset++)
                        if (used[start + offset] || source[start + offset].Value != value[offset])
                        {
                            equal = false;
                            break;
                        }
                    if (equal)
                        return Enumerable.Range(start, value.Length).ToArray();
                }
                return null;
            }
        }
    }

    internal static IEnumerable<string> PrimaryTexts(CanonicalPage page)
        => page.Blocks.Where(block => !CanonicalObservationSelector.IsNativeObservation(block)
                && !CanonicalObservationSelector.IsRedundantTableCellObservation(page, block)
                && !string.IsNullOrWhiteSpace(block.Text.Retrieval))
            .Select(block => block.Text.Canonical)
            .Concat(page.Tables.SelectMany(table => table.Cells).Select(cell => cell.Text.Canonical));

    internal static IReadOnlyList<SourceLine> Lines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select((value, index) => new SourceLine(index, index, value.Trim(), value.Trim()))
            .Where(line => line.Text.Length > 0).ToArray();

    internal static IReadOnlySet<int> RepresentedSourceLines(string text, CanonicalPage page)
    {
        var lines = Lines(text);
        var inventory = new Inventory(PrimaryTexts(page));
        return inventory.RepresentedLines(lines).SelectMany(index => Enumerable.Range(
            lines[index].Start, lines[index].End - lines[index].Start + 1)).ToHashSet();
    }

    private static IEnumerable<string> Tokens(string value)
    {
        var normalized = NormalizeText(value);
        foreach (Match match in InventoryToken().Matches(normalized))
            yield return NormalizeToken(match.Value);
    }

    private static string NormalizeText(string value)
        => value.Normalize(NormalizationForm.FormC).Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static string NormalizeToken(string token)
    {
        if (char.IsNumber(token[0]) || token[0] is '+' or '-' or '−' && token.Length > 1 && char.IsNumber(token[1]))
            return token.Replace('–', '-'); // Internal range/identifier separator; leading negative sign remains distinct.
        // A narrative dash remains an explicit separator token. In particular,
        // it is never discarded or combined with a following numeric sign.
        if (token is "–" or "—")
            return "-";
        return NormalizeWord(token.Contains('\'')
            ? string.Concat(token.Where(character => !char.IsWhiteSpace(character))) : token);
    }

    private static string NormalizeWord(string value)
    {
        // Initial sentence capitalization is presentation. All-uppercase and
        // mixed-case technical symbols remain distinct (AC/DC, Co/CO, etc.).
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length > 2 && char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower)
            ? value.ToLowerInvariant() : value;
    }

    [GeneratedRegex(@"[+\-−]?\p{N}+(?:[.,]\p{N}+)*(?:[–\-]\p{N}+(?:[.,]\p{N}+)*)*|[\p{L}\p{M}]+(?:'\s*[\p{L}\p{M}]+)*|[°%±=<>/·×÷!&|~^_\\:+−\-–—]", RegexOptions.CultureInvariant)]
    private static partial Regex InventoryToken();
}
