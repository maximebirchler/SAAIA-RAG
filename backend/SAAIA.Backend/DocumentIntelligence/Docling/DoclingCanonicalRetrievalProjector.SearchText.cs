internal static partial class DoclingCanonicalRetrievalProjector
{
    internal static string ResolveEmbeddingSourceText(ProjectedRetrievalChunk chunk)
        => chunk.StructuredTableProjection is { } plan
            ? RenderTableChunkText(plan with { ContextPrefix = "" })
            : chunk.Text;

    /// <summary>
    /// Literal lookup indexes source text, while read/context APIs retain the
    /// structured grid. This view is never appended to the model's context.
    /// </summary>
    internal static string ResolveLiteralSearchText(ProjectedRetrievalChunk chunk)
    {
        if (chunk.StructuredTableProjection is not { } plan) return chunk.Text;
        var rows = new List<string>();
        foreach (var group in plan.HeaderGroups.Concat(plan.ContentGroups))
        {
            if (plan.HasOverlappingCells)
            {
                rows.AddRange(group.Cells.Select(cell => cell.Text));
                continue;
            }
            for (var row = group.RowStart; row < group.RowEndExclusive; row++)
            {
                var columns = new string[plan.ColumnCount];
                Array.Fill(columns, "");
                foreach (var cell in group.Cells.Where(cell => cell.RowIndex == row))
                    columns[cell.ColumnIndex] = cell.Text;
                rows.Add(string.Join("\t", columns));
            }
        }
        var table = string.Join("\n", rows);
        return string.IsNullOrWhiteSpace(plan.ContextPrefix) ? table : plan.ContextPrefix + "\n\n" + table;
    }
}
