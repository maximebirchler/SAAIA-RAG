using SAAIA.Contracts.DocumentIntelligence;

// This plan is process-local, excluded from JSON by ProjectedRetrievalChunk.
// Provenance stays out of the embedding text. Persisted canonical anchors and
// the immutable source bundle remain the authority after publication.
internal sealed record CanonicalTableChunkProjection(
    string TableId,
    int TableOrdinal,
    int RowCount,
    int ColumnCount,
    bool HasOverlappingCells,
    IReadOnlyList<CanonicalTableRowGroup> HeaderGroups,
    IReadOnlyList<CanonicalTableRowGroup> ContentGroups,
    string ContextPrefix);

internal sealed record CanonicalTableRowGroup(
    int RowStart,
    int RowEndExclusive,
    bool IsColumnHeaderGroup,
    IReadOnlyList<CanonicalTableProjectedCell> Cells);

internal sealed record CanonicalTableProjectedCell(
    string CellId,
    int RowIndex,
    int ColumnIndex,
    int RowSpan,
    int ColumnSpan,
    string CellRole,
    string Text,
    IReadOnlyList<string> BlockIds,
    IReadOnlyList<string> SpanIds)
{
    internal static CanonicalTableProjectedCell FromCanonical(CanonicalTableCell cell, string text)
        => new(cell.CellId, cell.RowIndex, cell.ColumnIndex, cell.RowSpan, cell.ColumnSpan,
            cell.CellRole, text, cell.BlockIds.ToArray(), cell.SpanIds.ToArray());
}
