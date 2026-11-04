using System.Text;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.Core.Presentation;

public sealed record MarkdownTable(IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows, int ConsumedLineCount);

/// <summary>Reads a complete bounded pipe table; malformed blocks remain plain text.</summary>
public static class MarkdownTableParser
{
    public static IReadOnlyDictionary<int, MarkdownTable> ReadTables(IReadOnlyList<string> lines)
    {
        var tables = new Dictionary<int, MarkdownTable>();
        string? fence = null;
        for (var index = 0; index < lines.Count; index++)
        {
            if (UpdateCodeFence(lines[index], ref fence)) continue;
            if (fence is not null || !TryParse(lines, index, out var table)) continue;
            tables.Add(index, table!);
            index += table!.ConsumedLineCount - 1;
        }
        return tables;
    }

    public static bool UpdateCodeFence(string line, ref string? fence)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return false;
        var length = 1;
        while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;
        if (length < 3) return false;
        if (fence is null)
        {
            fence = trimmed[..length];
            return true;
        }
        if (fence[0] != trimmed[0] || length < fence.Length || !string.IsNullOrWhiteSpace(trimmed[length..]))
            return false;
        fence = null;
        return true;
    }

    public static bool TryParse(IReadOnlyList<string> lines, int start, out MarkdownTable? table)
    {
        table = null;
        if (start < 0 || start + 2 >= lines.Count || !lines[start].Contains('|')) return false;
        var headers = Split(lines[start]);
        var separator = Split(lines[start + 1]);
        if (headers.Count is < 2 or > AdvancedAnalysisClientLayoutLimits.MaximumColumns + 1
            || separator.Count != headers.Count || separator.Any(cell => !Regex.IsMatch(
                cell.Replace(" ", string.Empty), @"^:?-{3,}:?$", RegexOptions.CultureInvariant))) return false;
        var rows = new List<IReadOnlyList<string>>();
        var position = start + 2;
        for (; position < lines.Count && !string.IsNullOrWhiteSpace(lines[position]) && lines[position].Contains('|'); position++)
        {
            var cells = Split(lines[position]);
            if (cells.Count != headers.Count || rows.Count >= AdvancedAnalysisClientLayoutLimits.MaximumRows)
                return false;
            rows.Add(cells);
        }
        if (rows.Count == 0) return false;
        table = new(headers, rows, position - start);
        return true;
    }

    private static IReadOnlyList<string> Split(string line)
    {
        var value = line.Trim();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var slashes = 0;
        var ownedLink = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!ownedLink && value.AsSpan(index).StartsWith("[[open|", StringComparison.OrdinalIgnoreCase))
                ownedLink = true;
            var character = value[index];
            if (character == '|' && !ownedLink && slashes % 2 == 0)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else if (character == '|' && !ownedLink && slashes % 2 == 1)
            {
                cell.Length--;
                cell.Append(character);
            }
            else cell.Append(character);
            if (ownedLink && character == ']' && index > 0 && value[index - 1] == ']') ownedLink = false;
            slashes = character == '\\' ? slashes + 1 : 0;
        }
        cells.Add(cell.ToString().Trim());
        if (value.StartsWith('|') && cells.Count > 0) cells.RemoveAt(0);
        if (value.EndsWith('|') && cells.Count > 0 && cells[^1].Length == 0 && !ownedLink)
            cells.RemoveAt(cells.Count - 1);
        return cells;
    }
}
