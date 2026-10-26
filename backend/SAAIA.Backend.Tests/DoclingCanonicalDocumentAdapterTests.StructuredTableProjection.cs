using System.Text.Json;
using System.Xml.Linq;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Structured_table_projection_keeps_sparse_columns_and_explicit_empty_cells()
    {
        var source = StructuredTableSource(3, 3,
            Cell(0, 0, "Parameter", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 1, "Minimum", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 2, "Maximum", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(1, 0, "Alpha", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 2, "+5 °C", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(2, 0, "Beta", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(2, 1, "", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(2, 2, "15 bar", Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));
        var rows = ReadStructuredTable(chunk.Text).Elements("tr").ToArray();

        Assert.Equal(3, rows.Length);
        var sparse = rows[1].Elements().ToArray();
        Assert.Equal(3, sparse.Length);
        Assert.Equal("Alpha", sparse[0].Value);
        Assert.Equal("", sparse[1].Value);
        Assert.Equal("true", (string?)sparse[1].Attribute("data-canonical-missing"));
        Assert.Null(sparse[1].Attribute("data-cell-id"));
        Assert.Equal("+5 °C", sparse[2].Value);
        var observedEmpty = rows[2].Elements().ElementAt(1);
        Assert.Null(observedEmpty.Attribute("data-canonical-missing"));
        Assert.Equal("", observedEmpty.Value);
        Assert.Contains(chunk.StructuredTableProjection!.ContentGroups.SelectMany(group => group.Cells),
            cell => cell.RowIndex == 2 && cell.ColumnIndex == 1 && cell.Text == ""
                && canonical.Pages[0].Tables[0].Cells.Any(sourceCell => sourceCell.CellId == cell.CellId));
        Assert.Equal(canonical.Pages[0].Tables[0].Cells.Select(cell => cell.CellId).Order(),
            chunk.CanonicalTableCellIds!.Order());
        Assert.Equal(chunk.Text, PdfTextSanitizer.ForStorage(chunk.Text));
    }

    [Fact]
    public void Structured_table_projection_preserves_spans_header_roles_and_literal_markup()
    {
        var source = StructuredTableSource(3, 3,
            Cell(0, 0, "District", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 1, "Average temperature", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(1, 1, "Minimum", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(1, 2, "Maximum", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(2, 0, "AC|DC <limit> & exact", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(2, 1, "-5 °C", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(2, 2, "+5 °C", Box(0, 0, 1, 1, "TOPLEFT")));
        source.Tables[0].Data.Cells[0].RowSpan = 2;
        source.Tables[0].Data.Cells[0].IsRowHeader = true;
        source.Tables[0].Data.Cells[1].ColumnSpan = 2;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = JsonSerializer.Serialize(canonical);
        var table = ReadStructuredTable(Assert.Single(TableChunks(canonical, source)).Text);
        var district = table.Descendants("th").Single(cell => cell.Value == "District");

        Assert.Equal("2", (string?)district.Attribute("rowspan"));
        Assert.Equal("column_and_row_header", (string?)district.Attribute("data-cell-role"));
        Assert.Equal("2", (string?)table.Descendants("th").Single(cell => cell.Value == "Average temperature").Attribute("colspan"));
        Assert.Equal("AC|DC <limit> & exact", table.Descendants("td").Single(cell => cell.Value.StartsWith("AC|DC", StringComparison.Ordinal)).Value);
        Assert.Equal(before, JsonSerializer.Serialize(canonical));
    }

    [Fact]
    public void Structured_table_projection_fragments_repeat_headers_without_losing_source_cells()
    {
        var source = StructuredTableSource(7, 2,
            Enumerable.Range(0, 7).SelectMany(row => new[]
            {
                Cell(row, 0, row == 0 ? "Parameter" : $"Assembly {row} inspected", Box(0, 0, 1, 1, "TOPLEFT"), row == 0),
                Cell(row, 1, row == 0 ? "Limit" : $"Never exceed {row} bar", Box(0, 0, 1, 1, "TOPLEFT"), row == 0)
            }).ToArray());
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunks = TableChunks(canonical, source, 16);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var table = ReadStructuredTable(chunk.Text);
            Assert.Equal("Parameter", table.Elements("tr").First().Elements().First().Value);
            Assert.Contains("Limit", table.Elements("tr").First().Value, StringComparison.Ordinal);
        }
        Assert.Equal(canonical.Pages[0].Tables[0].Cells.Select(cell => cell.CellId).Order(),
            chunks.SelectMany(chunk => chunk.CanonicalTableCellIds!).Distinct().Order());
        foreach (var row in Enumerable.Range(1, 6))
            Assert.Single(chunks, chunk => ReadStructuredTable(chunk.Text).Descendants("td")
                .Any(cell => cell.Value == $"Never exceed {row} bar"));
    }

    [Fact]
    public void Structured_table_projection_keeps_cross_row_span_together_despite_small_budget()
    {
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "Shared source restriction", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(0, 1, "First observed value", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 1, "Second observed value", Box(0, 0, 1, 1, "TOPLEFT")));
        source.Tables[0].Data.Cells[0].RowSpan = 2;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source, 2));
        var table = ReadStructuredTable(chunk.Text);

        Assert.Equal(2, table.Elements("tr").Count());
        Assert.Equal("2", (string?)table.Descendants("td").First().Attribute("rowspan"));
        Assert.Equal(3, table.Descendants("td").Count());
    }

    [Fact]
    public void Structured_table_projection_does_not_split_an_oversized_literal_cell()
    {
        const string literal = "Disconnect electrical power before opening the equipment enclosure. Never remove the safety interlock.";
        var source = StructuredTableSource(1, 1, Cell(0, 0, literal, Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var table = ReadStructuredTable(Assert.Single(TableChunks(canonical, source, 2)).Text);
        Assert.Equal(literal, Assert.Single(table.Descendants("td")).Value);
    }

    [Fact]
    public void Structured_table_projection_keeps_a_header_only_table()
    {
        var source = StructuredTableSource(1, 2,
            Cell(0, 0, "Parameter", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 1, "Limit", Box(0, 0, 1, 1, "TOPLEFT"), true));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var table = ReadStructuredTable(Assert.Single(TableChunks(canonical, source)).Text);
        Assert.Equal(2, table.Descendants("th").Count());
    }

    [Fact]
    public void Structured_table_projection_publishes_document_index_with_title_destination_pairs()
    {
        var source = StructuredTableSource(2, 2,
            Cell(0, 0, "First source topic", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(0, 1, "12", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 0, "Second source topic", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 1, "35", Box(0, 0, 1, 1, "TOPLEFT")));
        source.Tables[0].Label = "document_index";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1),
            chunk => chunk.CanonicalTableCellIds is { Count: > 0 });
        var table = ReadStructuredTable(chunk.Text);

        Assert.Equal(new[] { "First source topic", "12" }, table.Elements("tr").First().Elements().Select(cell => cell.Value));
        Assert.Equal(new[] { "Second source topic", "35" }, table.Elements("tr").Last().Elements().Select(cell => cell.Value));
        Assert.Equal(RetrievalContentClassifier.NavigationRole, chunk.ContentRole);
        Assert.Equal(DoclingCanonicalRetrievalProjector.TableChunkType, chunk.OriginalChunkType);
        Assert.True(IngestionWorker.ShouldPublishRetrievalChunk(chunk));
    }

    [Fact]
    public void Structured_table_projection_search_surface_keeps_literal_row_facts_without_markup()
    {
        var source = StructuredTableSource(2, 3,
            Cell(0, 0, "Assembly", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 1, "Limit", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(0, 2, "Restriction", Box(0, 0, 1, 1, "TOPLEFT"), true),
            Cell(1, 0, "AC|DC <limit>", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 1, "-5 °C", Box(0, 0, 1, 1, "TOPLEFT")),
            Cell(1, 2, "Do not open & remove interlock", Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(TableChunks(canonical, source));
        var search = DoclingCanonicalRetrievalProjector.ResolveLiteralSearchText(chunk);

        Assert.Contains("Assembly\tLimit\tRestriction", search, StringComparison.Ordinal);
        Assert.Contains("AC|DC <limit>\t-5 °C\tDo not open & remove interlock", search, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", search, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", search, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", search, StringComparison.Ordinal);
        Assert.Contains("&lt;limit&gt;", chunk.Text, StringComparison.Ordinal);
        var unit = Assert.Single(CanonicalProfileInputProjector.Project([chunk]));
        Assert.Equal(search, unit.Text);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(search)), unit.Checksum);
        Assert.Equal(search.Length, unit.CharCount);
        Assert.Contains("&lt;limit&gt;", chunk.Text, StringComparison.Ordinal);
        var pieces = DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk(chunk);
        Assert.Null(pieces);
    }

    [Fact]
    public void Structured_table_projection_cleans_storage_control_characters_before_XML_serialization()
    {
        var source = StructuredTableSource(1, 1,
            Cell(0, 0, "Never\0open\u0001equipment", Box(0, 0, 1, 1, "TOPLEFT")));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var raw = canonical.Pages[0].Tables[0].Cells[0].Text.Raw;
        var chunk = Assert.Single(TableChunks(canonical, source));

        Assert.Equal("Never open equipment", Assert.Single(ReadStructuredTable(chunk.Text).Descendants("td")).Value);
        Assert.Equal(raw, canonical.Pages[0].Tables[0].Cells[0].Text.Raw);
        Assert.Contains("canonical_table_storage_controls_normalized", chunk.ExtractionQualitySignals!);
    }

    private static IReadOnlyList<ProjectedRetrievalChunk> TableChunks(CanonicalDocument canonical, DoclingDocument source, int maxWords = 220)
        => DoclingCanonicalRetrievalProjector.Project(canonical, source, maxWords, 1)
            .Where(chunk => chunk.ChunkType == DoclingCanonicalRetrievalProjector.TableChunkType).ToArray();

    private static XElement ReadStructuredTable(string text)
    {
        var start = text.IndexOf("<table", StringComparison.Ordinal);
        var end = text.IndexOf("</table>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= start, "A structured table must reach the published chunk.");
        return XElement.Parse(text[start..(end + "</table>".Length)]);
    }

    private static DoclingDocument StructuredTableSource(int rows, int columns, params DoclingTableCell[] cells)
    {
        var source = BuildDocument();
        source.Tables[0].Data.RowCount = rows;
        source.Tables[0].Data.ColumnCount = columns;
        source.Tables[0].Data.Cells = cells.ToList();
        return source;
    }
}
