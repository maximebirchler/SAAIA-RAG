using System.Text.Json;
using System.Xml.Linq;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_table_split_recursively_keeps_complete_rows_headers_and_exact_cell_links(bool navigation)
    {
        var source = StructuredTableSource(9, 2,
            Enumerable.Range(0, 9).SelectMany(row => new[]
            {
                Cell(row, 0, row == 0 ? "Assembly" : $"Source {row}", Box(0, 0, 1, 1, "TOPLEFT"), row == 0),
                Cell(row, 1, row == 0 ? "Maximum" : $"{row} bar", Box(0, 0, 1, 1, "TOPLEFT"), row == 0)
            }).ToArray());
        source.Tables[0].Label = navigation ? "document_index" : "table";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 1000, 1),
            chunk => chunk.CanonicalTableCellIds is { Count: > 0 });
        var before = JsonSerializer.Serialize(canonical);
        var sourceText = chunk.Text;
        var leaves = SplitTableToIndivisibleGroups(chunk);

        Assert.Equal(8, leaves.Count);
        Assert.Equal(sourceText, chunk.Text);
        Assert.Equal(before, JsonSerializer.Serialize(canonical));
        Assert.Equal(canonical.Pages[0].Tables[0].Cells.Select(cell => cell.CellId).Order(),
            leaves.SelectMany(leaf => leaf.CanonicalTableCellIds!).Distinct().Order());
        var headerIds = canonical.Pages[0].Tables[0].Cells.Where(cell => cell.RowIndex == 0).Select(cell => cell.CellId).ToArray();
        foreach (var leaf in leaves)
        {
            var table = ReadStructuredTable(leaf.Text);
            Assert.Equal(2, table.Elements("tr").Count());
            Assert.Equal("true", (string?)table.Attribute("data-fragment"));
            Assert.Equal("9", (string?)table.Attribute("data-total-rows"));
            Assert.Equal(new[] { "Assembly", "Maximum" }, table.Elements("tr").First().Elements().Select(cell => cell.Value));
            Assert.All(headerIds, id => Assert.Contains(id, leaf.CanonicalTableCellIds!));
            var expectedIds = leaf.StructuredTableProjection!.HeaderGroups.Concat(leaf.StructuredTableProjection.ContentGroups)
                .SelectMany(group => group.Cells).Select(cell => cell.CellId).Distinct().Order();
            Assert.Equal(expectedIds, leaf.CanonicalTableCellIds!.Order());
            Assert.Equal(chunk.ContentRole, leaf.ContentRole);
            Assert.Equal(chunk.OriginalChunkType, leaf.OriginalChunkType);
            Assert.Equal(chunk.PageStart, leaf.PageStart);
            Assert.Contains("canonical_table_tokenizer_structural_split_v1", leaf.ExtractionQualitySignals!);
            Assert.DoesNotContain("structuredTableProjection", JsonSerializer.Serialize(leaf), StringComparison.OrdinalIgnoreCase);
            Assert.All(leaf.CanonicalTableCellIds!, id => Assert.DoesNotContain(id, leaf.Text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Structured_table_split_does_not_cut_a_cross_row_cell_or_fall_back_to_plain_text()
    {
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "Common restriction", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(0, 1, "First value", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 1, "Second value", Box(0, 0, 1, 1, "TOPLEFT")));
        source.Tables[0].Data.Cells[0].RowSpan = 2;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));

        Assert.True(DoclingCanonicalRetrievalProjector.IsStructuredChunk(chunk));
        Assert.Null(DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(chunk));
        Assert.Null(DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(chunk with { StructuredTableProjection = null }));
        Assert.Equal(2, ReadStructuredTable(chunk.Text).Elements("tr").Count());
    }

    [Fact]
    public void Structured_table_split_preserves_overlapping_observations_without_asserting_a_false_grid()
    {
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "Original observation", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(0, 0, "Conflicting observation", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 1, "Separate observation", Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));
        Assert.DoesNotContain("<table", chunk.Text, StringComparison.Ordinal);
        var ledger = XElement.Parse(chunk.Text[(chunk.Text.IndexOf("<canonical-table-observations", StringComparison.Ordinal))..]);
        var observed = ledger.Elements("cell-observation").ToArray();
        Assert.Equal(3, observed.Length);
        Assert.Equal(2, observed.Count(cell => (string?)cell.Attribute("row") == "0"
            && (string?)cell.Attribute("column") == "0"));
        Assert.Contains("canonical_table_overlapping_grid", chunk.ExtractionQualitySignals!);
        Assert.Contains("canonical_table_overlap_observations_v1", chunk.ExtractionQualitySignals!);
        Assert.Equal(3, SplitTableToIndivisibleGroups(chunk).Count);
        Assert.Equal(3, chunk.CanonicalTableCellIds!.Count);
    }

    [Fact]
    public void Structured_table_split_does_not_hoist_late_row_headers_or_repeat_real_data()
    {
        var source = StructuredTableSource(4, 2,
            Enumerable.Range(0, 4).SelectMany(row => new[]
            {
                Cell(row, 0, $"Row {row}", Box(0, 0, 1, 1, "TOPLEFT")),
                Cell(row, 1, $"Value {row}", Box(0, 0, 1, 1, "TOPLEFT"))
            }).ToArray());
        source.Tables[0].Data.Cells[4].IsRowHeader = true;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));
        var leaves = SplitTableToIndivisibleGroups(chunk);

        Assert.Equal(4, leaves.Count);
        Assert.All(leaves, leaf => Assert.Empty(leaf.StructuredTableProjection!.HeaderGroups));
        foreach (var row in Enumerable.Range(0, 4))
            Assert.Single(leaves, leaf => ReadStructuredTable(leaf.Text).Descendants()
                .Any(cell => cell.Name.LocalName is "td" or "th" && cell.Value == $"Row {row}"));
    }

    [Fact]
    public void Structured_table_split_keeps_only_the_block_and_span_links_of_retained_cells()
    {
        var source = StructuredTableSource(2, 1,
            Cell(0, 0, "First source", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 0, "Second source", Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var body = canonical.Pages[0].Blocks.Single(block => block.Text.Canonical == "Canonical body");
        var caption = canonical.Pages[0].Blocks.Single(block => block.Text.Canonical == "Table caption");
        var cells = canonical.Pages[0].Tables[0].Cells;
        cells[0].BlockIds = [body.BlockId];
        cells[0].SpanIds = body.Spans.Select(span => span.SpanId).ToList();
        cells[1].BlockIds = [caption.BlockId];
        cells[1].SpanIds = caption.Spans.Select(span => span.SpanId).ToList();
        var chunk = Assert.Single(TableChunks(canonical, source));
        var children = DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(chunk)!;

        Assert.Equal(2, children.Count);
        Assert.Equal(new[] { body.BlockId }, children[0].CanonicalBlockIds);
        Assert.Equal(body.Spans.Select(span => span.SpanId), children[0].CanonicalSpanIds!);
        Assert.Equal(new[] { caption.BlockId }, children[1].CanonicalBlockIds);
        Assert.Equal(caption.Spans.Select(span => span.SpanId), children[1].CanonicalSpanIds!);
        Assert.All(children, child => Assert.Equal(chunk.CanonicalContextBlockIds, child.CanonicalContextBlockIds));
    }

    [Fact]
    public void Structured_table_embedding_source_reduces_explicit_caption_context_without_removing_table_headers()
    {
        const string captionText = "Attached table calibration reference for source-only embedding reduction";
        var source = BuildDocument();
        source.Texts[2].Text = source.Texts[2].OriginalText = captionText;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));
        var before = chunk.Text;
        var input = DoclingCanonicalRetrievalProjector.ResolveEmbeddingSourceText(chunk);

        // The table's explicit caption replaces the formerly assumed preceding
        // heading. Admission may remove only this context from embedding input.
        Assert.Contains(captionText, before, StringComparison.Ordinal);
        Assert.DoesNotContain(captionText, input, StringComparison.Ordinal);
        Assert.Equal(new[] { "A", "B" }, ReadStructuredTable(input).Elements("tr").First().Elements().Select(cell => cell.Value));
        Assert.Equal(new[] { "A", "B", "C", "D" }, ReadStructuredTable(input).Descendants()
            .Where(cell => cell.Name.LocalName is "th" or "td").Select(cell => cell.Value));
        Assert.Equal(before, chunk.Text);
        Assert.Contains(captionText, DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk), StringComparison.Ordinal);
        var caption = canonical.Pages[0].Blocks.Single(block => block.Text.Retrieval == captionText);
        Assert.Contains(caption.BlockId, chunk.CanonicalContextBlockIds!);
    }

    [Fact]
    public void Structured_table_split_distinguishes_two_same_page_tables_with_identical_cells()
    {
        var source = StructuredTableSource(3, 2,
            Enumerable.Range(0, 3).SelectMany(row => new[]
            {
                Cell(row, 0, row == 0 ? "Assembly" : $"Source {row}", Box(0, 0, 1, 1, "TOPLEFT"), row == 0),
                Cell(row, 1, row == 0 ? "Maximum" : $"{row} bar", Box(0, 0, 1, 1, "TOPLEFT"), row == 0)
            }).ToArray());
        var second = JsonSerializer.Deserialize<DoclingTableItem>(JsonSerializer.Serialize(source.Tables[0]))!;
        second.SelfRef = "#/tables/1";
        second.Provenance[0].BoundingBox = Box(10, 50, 90, 10, "BOTTOMLEFT");
        source.Tables.Add(second);
        source.Body.Children.Add(new() { Ref = second.SelfRef });
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunks = TableChunks(canonical, source);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new[] { 0, 1 }, chunks.Select(chunk => chunk.StructuredTableProjection!.TableOrdinal));
        var fragments = chunks.SelectMany(SplitTableToIndivisibleGroups).ToArray();
        Assert.Equal(4, fragments.Length);
        foreach (var fragment in fragments)
        {
            var plan = fragment.StructuredTableProjection!;
            Assert.Equal(plan.TableOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (string?)ReadStructuredTable(fragment.Text).Attribute("data-source-table-ordinal"));
            var sourceTable = canonical.Pages[0].Tables.Single(table => table.Ordinal == plan.TableOrdinal);
            Assert.Equal(sourceTable.TableId, plan.TableId);
            Assert.All(fragment.CanonicalTableCellIds!, id => Assert.Contains(sourceTable.Cells, cell => cell.CellId == id));
        }
        Assert.Equal(canonical.Pages[0].Tables.SelectMany(table => table.Cells).Select(cell => cell.CellId).Order(),
            fragments.SelectMany(fragment => fragment.CanonicalTableCellIds!).Distinct().Order());
    }

    private static IReadOnlyList<ProjectedRetrievalChunk> SplitTableToIndivisibleGroups(ProjectedRetrievalChunk chunk)
    {
        var parts = DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(chunk);
        return parts is null ? [chunk] : parts.SelectMany(SplitTableToIndivisibleGroups).ToArray();
    }
}
