using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

internal sealed record CanonicalTableCellBinding(
    [property: JsonPropertyName("cellId")] string CellId,
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("rowSpan")] int RowSpan,
    [property: JsonPropertyName("columnSpan")] int ColumnSpan,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("literalSha256")] string LiteralSha256);

internal sealed record CanonicalTableCellMap(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("tableOrdinal")] int TableOrdinal,
    [property: JsonPropertyName("totalRows")] int TotalRows,
    [property: JsonPropertyName("totalColumns")] int TotalColumns,
    [property: JsonPropertyName("cells")] IReadOnlyList<CanonicalTableCellBinding> Cells)
{
    internal const string CurrentSchemaVersion = "canonical_table_cell_map_v1";

    // The ordered ID set is not a coordinate map: source Cells may be in any
    // order. Persist an explicit binding from the actual structural projection.
    // Only hashes duplicate literal values; source text is not copied here.
    internal static CanonicalTableCellMap? Build(ProjectedRetrievalChunk chunk, Func<string, string> normalizeLiteral)
    {
        if (chunk.StructuredTableProjection is not { HasOverlappingCells: false } plan)
            return null;

        var cells = plan.HeaderGroups.Concat(plan.ContentGroups)
            .SelectMany(group => group.Cells)
            .DistinctBy(cell => cell.CellId)
            .OrderBy(cell => cell.RowIndex)
            .ThenBy(cell => cell.ColumnIndex)
            .Select(cell => new CanonicalTableCellBinding(cell.CellId, cell.RowIndex, cell.ColumnIndex,
                cell.RowSpan, cell.ColumnSpan, cell.CellRole, Hash(normalizeLiteral(cell.Text))))
            .ToArray();
        return new(CurrentSchemaVersion, plan.TableOrdinal, plan.RowCount, plan.ColumnCount, cells);
    }

    internal static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
