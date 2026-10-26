using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Whole observed line ranges are matched to complete canonical observations, without similarity or value fragments.</summary>
internal static partial class CanonicalLiteralTextCoverage
{
    internal static IReadOnlySet<int> FindRepresentedLineIndices(IReadOnlyList<string> sourceLines, CanonicalPage page)
    {
        var lines = sourceLines.Select(Normalize).ToArray();
        var represented = new HashSet<int>();
        // Reconciled blocks have an explicit source-line anchor. Reapplying reconciliation
        // may overlap original blocks; exact text and that anchor make the overlap provable.
        foreach (var block in page.Blocks.Where(block => block.Provenance.Method == "native_text_coverage_reconciliation"))
        {
            if (block.Provenance.Attributes.TryGetValue("sourceLineStart", out var from)
                && block.Provenance.Attributes.TryGetValue("sourceLineEnd", out var to)
                && int.TryParse(from, out var start) && int.TryParse(to, out var end)
                && start >= 0 && end >= start && end < lines.Length
                && string.Equals(Normalize(block.Text.Canonical), Join(start, end), StringComparison.Ordinal))
                for (var index = start; index <= end; index++) represented.Add(index);
        }
        var observations = page.Blocks.OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal)
            .Select(block => block.Text.Canonical)
            .Concat(page.Tables.OrderBy(table => table.Ordinal).SelectMany(table => table.Cells
                .GroupBy(cell => cell.RowIndex).OrderBy(row => row.Key)
                .Select(row => string.Join(" ", row.OrderBy(cell => cell.ColumnIndex).Select(cell => cell.Text.Canonical)))));
        foreach (var value in observations)
        {
            var candidate = Normalize(value);
            if (candidate.Length == 0) continue;
            for (var start = 0; start < lines.Length; start++)
            {
                if (lines[start].Length == 0 || represented.Contains(start)
                    || !candidate.StartsWith(lines[start], StringComparison.Ordinal)) continue;
                var joined = "";
                for (var end = start; end < lines.Length; end++)
                {
                    if (represented.Contains(end)) break;
                    if (lines[end].Length > 0) joined += (joined.Length == 0 ? "" : " ") + lines[end];
                    if (!candidate.StartsWith(joined, StringComparison.Ordinal)) break;
                    if (!string.Equals(candidate, joined, StringComparison.Ordinal)) continue;
                    for (var index = start; index <= end; index++) represented.Add(index);
                    start = lines.Length; // One canonical occurrence does not prove two source occurrences.
                    break;
                }
            }
        }
        return represented;

        string Join(int start, int end) => string.Join(" ", lines.Skip(start).Take(end - start + 1)
            .Where(line => line.Length > 0));
    }

    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
