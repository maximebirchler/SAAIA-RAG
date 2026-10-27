using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisTableRow(int Index, string Xml, IReadOnlyList<string> CellIds,
    bool IsHeader, int EndExclusive);

// Process-local view of the published fragment. SQL text and its source anchors
// remain unchanged; this parser never retrieves a bundle or infers a cell value.
internal sealed record AdvancedAnalysisTableFragment(int Ordinal, int TotalRows, int TotalColumns,
    IReadOnlyList<AdvancedAnalysisTableRow> Rows, string? ErrorCode = null,
    IReadOnlyList<string>? QualitySignals = null)
{
    internal const int MaximumRows = 10_000;
    internal const int MaximumColumns = 128;
    public bool QualityMetadataMalformed { get; init; }
    public bool QualityMetadataTruncated { get; init; }
    public int QualitySignalsOmittedCount { get; init; }

    internal static AdvancedAnalysisTableFragment? Parse(string text, string? cellIdsJson, string? qualitySignalsJson = null,
        string? cellMapJson = null)
    {
        var start = text.IndexOf("<table", StringComparison.Ordinal);
        if (start<0) return null;
        var quality = ReadQualityMetadata(qualitySignalsJson);
        var ordinal = -1;
        try
        {
            var end = text.IndexOf("</table>", start, StringComparison.Ordinal);
            if (end<start || text.IndexOf("<table", start+1, StringComparison.Ordinal) >= 0)
                throw new FormatException();
            using var reader = XmlReader.Create(new StringReader(text[start..(end+8)]), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 200_000
            });
            var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            if (root.Name != "table"  || root.Descendants().Any(node => node.Name != "tr"  && node.Name != "th"  && node.Name != "td")
                 || !TryInt(root.Attribute("data-source-table-ordinal")?.Value, 0, int.MaxValue, out ordinal))
                throw new FormatException();
            var nodes = root.Elements().ToArray();
            if (nodes.Length == 0 || nodes.Any(row => row.Name != "tr")) throw new FormatException();
            var total = nodes.Length;
            var indices = Enumerable.Range(0, total).ToArray();
            if (root.Attribute("data-fragment")?.Value == "true")
            {
                if (!TryInt(root.Attribute("data-total-rows")?.Value, 1, MaximumRows, out total)) throw new FormatException();
                indices = ParseRanges(root.Attribute("data-source-row-indices")?.Value, total);
            }
            if (total>MaximumRows || indices.Length != nodes.Length) throw new FormatException();
            if (cellIdsJson?.Length > 4_000_000 || cellMapJson?.Length > 4_000_000)
            {
                throw new FormatException();
            }
            var ids = JsonSerializer.Deserialize<string[]>(cellIdsJson ?? "[]") ?? [];
            if (ids.Length == 0 || ids.Length>16_384 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length>200)
                 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new FormatException();
            if (string.IsNullOrEmpty(cellMapJson) || cellMapJson == "null")
                return WithQuality(new(ordinal, total, 0, [], "structured_table_cell_map_unavailable"));
            var map = JsonSerializer.Deserialize<CanonicalTableCellMap>(cellMapJson);
            if (map is null || map.SchemaVersion != CanonicalTableCellMap.CurrentSchemaVersion || map.TableOrdinal != ordinal
                || map.TotalRows != total || map.TotalColumns is <1 or >MaximumColumns || map.Cells is null || map.Cells.Count != ids.Length
                || map.Cells.Any(cell => cell is null)
                 || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(map.Cells.Select(cell => cell.CellId)))
                throw new FormatException();
            var bindings = new Dictionary<(int Row, int Column), CanonicalTableCellBinding>();
            foreach (var binding in map.Cells)
            {
                if (binding is null || binding.Row<0 || binding.Row >= total || binding.Column<0 || binding.Column >= map.TotalColumns
                     || binding.RowSpan<1 || binding.ColumnSpan<1 || binding.RowSpan>total-binding.Row
                     || binding.ColumnSpan>map.TotalColumns-binding.Column
                     || !bindings.TryAdd((binding.Row, binding.Column), binding)) throw new FormatException();
            }
            var rows = new List<AdvancedAnalysisTableRow>();
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            var width = 0;
            var occupied = new Dictionary<(int Row, int Column), bool>();
            for (var i = 0; i<nodes.Length; i++)
            {
                var index = indices[i];
                var columns = 0;
                var rowIds = new List<string>();
                var rowEnd = index+1;
                foreach (var cell in nodes[i].Elements())
                {
                    while (occupied.ContainsKey((index, columns))) columns++;
                    var rs = Span(cell, "rowspan"); var cs = Span(cell, "colspan");
                    if (columns+cs>MaximumColumns || index+rs>total || cell.HasElements) throw new FormatException();
                    var missing = cell.Attribute("data-canonical-missing")?.Value == "true";
                    if (missing && (cell.Value.Length>0 || rs != 1 || cs != 1)) throw new FormatException();
                    bindings.TryGetValue((index, columns), out var binding);
                    if (missing && binding is not null) throw new FormatException();
                    if (!missing)
                    {
                        if (binding is null || binding.RowSpan != rs || binding.ColumnSpan != cs
                             || binding.LiteralSha256 != CanonicalTableCellMap.Hash(cell.Value)
                             || !consumed.Add(binding.CellId)) throw new FormatException();
                        var expectedRole = cell.Attribute("data-cell-role")?.Value ??  "data";
                        if (binding.Role != expectedRole || (cell.Name == "th") != (expectedRole != "data")) throw new FormatException();
                        rowIds.Add(binding.CellId);
                    }
                    for (var r = index; r<index+rs; r++) for (var c = columns; c<columns+cs; c++)
                        if (!occupied.TryAdd((r, c), true)) throw new FormatException();
                    columns+= cs; rowEnd = Math.Max(rowEnd, index+rs);
                }
                while (occupied.ContainsKey((index, columns))) columns++;
                if (columns <= 0 || columns>MaximumColumns || width != 0 && width != columns) throw new FormatException();
                width = columns;
                rows.Add(new(index, nodes[i].ToString(SaveOptions.DisableFormatting), rowIds,
                    nodes[i].Elements().Any(cell => cell.Name == "th"
                         && cell.Attribute("data-cell-role")?.Value is "column_header" or "column_and_row_header" or "row_and_column_header"), rowEnd));
            }
            if (consumed.Count != ids.Length || width != map.TotalColumns) throw new FormatException();
            return WithQuality(new(ordinal, total, width, rows));
        }
        catch (Exception error) when (error is XmlException or FormatException or JsonException or OverflowException)
        {
            return WithQuality(new(ordinal, 0, 0, [], "structured_table_fragment_invalid"));
        }

        AdvancedAnalysisTableFragment WithQuality(AdvancedAnalysisTableFragment fragment) => fragment with
        {
            QualitySignals = quality.Signals,
            QualityMetadataMalformed = quality.MetadataMalformed,
            QualityMetadataTruncated = quality.MetadataTruncated,
            QualitySignalsOmittedCount = quality.SignalsOmittedCount
        };
    }

    private static AdvancedAnalysisSourceQuality ReadQualityMetadata(string? signalsJson)
    {
        var quality = AdvancedAnalysisSourceQuality.FromMetadata(null, null, signalsJson)
            ?? new(null, null, []);
        if (signalsJson is null || signalsJson.Length > 100_000)
            return quality;
        try
        {
            using var json = JsonDocument.Parse(signalsJson);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return quality;
            // Fragments are internal and never projected as documentary text.
            // Keep the complete, source-bounded set until the assembler unions
            // fragments; truncating each first would miscount shared omissions.
            // Use the same code syntax as AdvancedAnalysisSourceQuality.
            var signals = json.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => value is { Length: > 0 and <= 160 }
                    && value.All(character => char.IsAsciiLetterOrDigit(character)
                        || character is '_' or '-' or '.' or ':'))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal).ToArray();
            return quality with { Signals = signals, MetadataTruncated = false, SignalsOmittedCount = 0 };
        }
        catch (JsonException) { return quality; }
    }

    private static int Span(XElement cell, string name)
         => cell.Attribute(name) is not { } attribute ? 1
            : TryInt(attribute.Value, 1, MaximumRows, out var span) ? span : throw new FormatException();

    private static bool TryInt(string? value, int min, int max, out int result)
         => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= min && result <= max;

    private static int[] ParseRanges(string? value, int total)
    {
        if (string.IsNullOrEmpty(value) || value.Length>60_000) throw new FormatException();
        var indices = new List<int>();
        foreach (var range in value.Split(','))
        {
            var bounds = range.Split('-');
            if (bounds.Length is <1 or >2 || !TryInt(bounds[0], 0, total-1, out var start)
                 || !TryInt(bounds[^1], start, total-1, out var end)) throw new FormatException();
            for (var index = start; index <= end; index++)
            {
                if (indices.Count >= MaximumRows || indices.Count>0 && index <= indices[^1]) throw new FormatException();
                indices.Add(index);
            }
        }
        return indices.ToArray();
    }
}
