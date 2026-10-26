using System.Xml.Linq;
using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DoclingCanonicalRetrievalProjector
{
    private const string GridProjectionSignal = "canonical_table_grid_html_v1";
    private const string CellLedgerSignal = "canonical_table_overlap_observations_v1";

    private static IReadOnlyList<SourceAtom> BuildTableAtoms(
        CanonicalTable table, CanonicalPage page, bool navigation, int globalOrder)
    {
        // A sparse slot is an absence of an observation, not a value. Covered
        // slots belong to their spanning source cell and must not gain cells.
        if ((long)table.RowCount * table.ColumnCount > 1_000_000)
            throw new InvalidDataException("canonical_table_grid_projection_limit");
        var occupied = new HashSet<(int Row, int Column)>();
        var captions = ResolveTableCaptionContext(table, page, navigation);
        var contextSignal = captions.Count > 0
            ? TableCaptionContextSignal
            : TableContextUnassignedSignal;
        var unavailableCaption = table.CaptionBlockIds.Except(captions.SelectMany(caption => caption.BlockIds),
            StringComparer.Ordinal).Any();
        var overlaps = false;
        foreach (var cell in table.Cells)
        {
            for (var row = cell.RowIndex; row < cell.RowIndex + cell.RowSpan; row++)
            for (var column = cell.ColumnIndex; column < cell.ColumnIndex + cell.ColumnSpan; column++)
                overlaps |= !occupied.Add((row, column));
        }
        var groups = new List<CanonicalTableRowGroup>();
        var controlsNormalized = table.Cells.Any(cell => cell.Text.Retrieval.Any(character =>
            character < ' ' && character is not '\t' and not '\n' and not '\r'));
        var cells = table.Cells.Select(cell => CanonicalTableProjectedCell.FromCanonical(cell,
            NormalizeText(PdfTextSanitizer.ForStorage(cell.Text.Retrieval)))).ToArray();
        if (overlaps)
        {
            // No honest grid can put two observations in the same slot. Keep a
            // coordinate ledger instead of shifting one to a made-up column.
            groups.AddRange(cells.OrderBy(cell => cell.RowIndex).ThenBy(cell => cell.ColumnIndex)
                .ThenBy(cell => cell.CellId, StringComparer.Ordinal).Select(cell =>
                    new CanonicalTableRowGroup(cell.RowIndex, cell.RowIndex + cell.RowSpan, false, [cell])));
        }
        else
        {
            // Never cut at a row boundary crossed by an observed rowspan.
            for (var start = 0; start < table.RowCount;)
            {
                var end = start + 1;
                while (end < table.RowCount && cells.Any(cell => cell.RowIndex < end
                           && cell.RowIndex + cell.RowSpan > end)) end++;
                var groupCells = cells.Where(cell => cell.RowIndex >= start && cell.RowIndex < end).ToArray();
                var nonempty = groupCells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
                groups.Add(new(start, end, nonempty.Length > 0
                    && nonempty.All(cell => IsColumnHeaderCell(cell.CellRole)), groupCells));
                start = end;
            }
        }

        return groups.Select((group, ordinal) => new SourceAtom(
            globalOrder, ordinal, page.PageNumber,
            RenderTableGroup(group, table.ColumnCount, overlaps), "table_row", table.TableId,
            navigation, false, 1, null, group.IsColumnHeaderGroup,
            Distinct(group.Cells.SelectMany(cell => cell.BlockIds)),
            Distinct(group.Cells.SelectMany(cell => cell.SpanIds)),
            group.Cells.Select(cell => cell.CellId).ToArray(),
            Distinct(page.QualityFlags
                .Append(overlaps ? CellLedgerSignal : GridProjectionSignal)
                .Append(contextSignal)
                .Concat(unavailableCaption ? [TableCaptionUnavailableSignal] : Array.Empty<string>())
                .Concat(controlsNormalized ? ["canonical_table_storage_controls_normalized"] : Array.Empty<string>())),
            table.ColumnCount, table.RowCount, overlaps, group, table.Ordinal, captions)).ToArray();
    }

    private static string RenderTableGroup(CanonicalTableRowGroup group, int columns, bool ledger)
    {
        if (ledger)
            return string.Concat(group.Cells.Select(cell => new XElement("cell-observation",
                new XAttribute("row", cell.RowIndex), new XAttribute("column", cell.ColumnIndex),
                new XAttribute("rowspan", cell.RowSpan), new XAttribute("colspan", cell.ColumnSpan),
                new XAttribute("role", cell.CellRole), cell.Text).ToString(SaveOptions.DisableFormatting)));
        var occupied = new Dictionary<(int Row, int Column), CanonicalTableProjectedCell>();
        foreach (var cell in group.Cells)
            for (var row = cell.RowIndex; row < cell.RowIndex + cell.RowSpan; row++)
            for (var column = cell.ColumnIndex; column < cell.ColumnIndex + cell.ColumnSpan; column++)
                occupied.Add((row, column), cell);
        var rows = new List<XElement>();
        for (var row = group.RowStart; row < group.RowEndExclusive; row++)
        {
            var element = new XElement("tr");
            for (var column = 0; column < columns; column++)
            {
                if (!occupied.TryGetValue((row, column), out var cell))
                {
                    element.Add(new XElement("td", new XAttribute("data-canonical-missing", "true")));
                    continue;
                }
                if (cell.RowIndex != row || cell.ColumnIndex != column) continue;
                var node = new XElement(IsHeaderCell(cell.CellRole) ? "th" : "td");
                if (IsHeaderCell(cell.CellRole)) node.SetAttributeValue("data-cell-role", cell.CellRole);
                if (cell.RowSpan > 1) node.SetAttributeValue("rowspan", cell.RowSpan);
                if (cell.ColumnSpan > 1) node.SetAttributeValue("colspan", cell.ColumnSpan);
                node.Add(cell.Text);
                element.Add(node);
            }
            rows.Add(element);
        }
        return string.Concat(rows.Select(row => row.ToString(SaveOptions.DisableFormatting)));
    }

    private static bool IsHeaderCell(string role)
        => IsColumnHeaderCell(role) || string.Equals(role, "row_header", StringComparison.Ordinal)
           || string.Equals(role, "row_section", StringComparison.Ordinal);

    private static bool IsColumnHeaderCell(string role)
        => string.Equals(role, "column_header", StringComparison.Ordinal)
           || string.Equals(role, "column_and_row_header", StringComparison.Ordinal)
           || string.Equals(role, "row_and_column_header", StringComparison.Ordinal);

    private static void EmitTableChunks(IReadOnlyList<SourceAtom> rows,
        int maxWords,
        ICollection<ProjectedRetrievalChunk> projected)
    {
        if (rows.Count == 0) return;
        // Only leading column-header groups repeat. Later sections stay in
        // source order and cannot become a fabricated global table header.
        var headers = rows.TakeWhile(row => row.IsTableHeader).ToArray();
        var data = rows.Skip(headers.Length).ToArray();
        if (data.Length == 0) data = rows.ToArray();
        ChunkBuffer Start(SourceAtom row)
        {
            var next = new ChunkBuffer(row.IsNavigation, TableChunkType);
            // A preceding heading does not prove table membership. Only an
            // explicit, same-page canonical caption supplies local context;
            // it is repeated as context without inventing a section.
            foreach (var caption in row.TableCaptions ?? []) next.Add(caption, isContextHeading: true);
            if (headers.Length < rows.Count)
                foreach (var header in headers) next.Add(header, isContextHeading: true);
            return next;
        }

        ChunkBuffer? buffer = null;
        foreach (var row in data)
        {
            buffer ??= Start(row);
            if (!buffer.CanAdd(row, maxWords))
            {
                projected.Add(CreateChunk(projected.Count, buffer));
                buffer = Start(row);
            }
            // Never split a cell or XML for a heuristic word budget. The real
            // tokenizer re-counts structural subdivisions before publication.
            buffer.Add(row);
        }
        if (buffer is { HasContent: true }) projected.Add(CreateChunk(projected.Count, buffer));
    }

    private static CanonicalTableChunkProjection CreateTableProjection(IReadOnlyList<SourceAtom> atoms)
    {
        var rows = atoms.Where(atom => atom.Kind == "table_row").ToArray();
        var headerCount = rows.TakeWhile(row => row.IsTableHeader).Count();
        if (headerCount == rows.Length) headerCount = 0;
        var context = string.Join("\n\n", atoms.Where(atom => atom.Kind != "table_row").Select(atom => atom.Text));
        return new(rows[0].GroupKey, rows[0].TableOrdinal, rows[0].TableRowCount, rows[0].TableColumnCount,
            rows[0].TableHasOverlaps,
            rows.Take(headerCount).Select(row => row.TableGroup!).ToArray(),
            rows.Skip(headerCount).Select(row => row.TableGroup!).ToArray(), context);
    }

    private static string RenderTableChunkText(CanonicalTableChunkProjection projection)
    {
        var root = new XElement(projection.HasOverlappingCells ? "canonical-table-observations" : "table");
        root.SetAttributeValue("data-source-table-ordinal", projection.TableOrdinal);
        var groups = projection.HeaderGroups.Concat(projection.ContentGroups).ToArray();
        var coveredRows = groups.SelectMany(group => Enumerable.Range(group.RowStart,
            group.RowEndExclusive - group.RowStart)).Distinct().Order().ToArray();
        if (coveredRows.Length != projection.RowCount)
        {
            root.SetAttributeValue("data-fragment", "true");
            root.SetAttributeValue("data-total-rows", projection.RowCount);
            root.SetAttributeValue("data-source-row-indices", RowRanges(coveredRows));
        }
        foreach (var group in groups)
        {
            var wrapper = XElement.Parse("<rows>" + RenderTableGroup(group, projection.ColumnCount,
                projection.HasOverlappingCells) + "</rows>");
            root.Add(wrapper.Elements());
        }
        var text = root.ToString(SaveOptions.DisableFormatting);
        return string.IsNullOrWhiteSpace(projection.ContextPrefix) ? text : projection.ContextPrefix + "\n\n" + text;
    }

    private static string RowRanges(IReadOnlyList<int> indices)
    {
        var ranges = new List<string>();
        for (var index = 0; index < indices.Count;)
        {
            var start = indices[index];
            var end = start;
            while (++index < indices.Count && indices[index] == end + 1) end++;
            ranges.Add(start == end ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
                  + end.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(",", ranges);
    }
}
