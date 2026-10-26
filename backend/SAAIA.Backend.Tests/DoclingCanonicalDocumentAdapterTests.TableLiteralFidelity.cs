using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("ALPHA", "-5 °C", "Ne pas ouvrir")]
    [InlineData("BETA", "1,5 bar", "")]
    [InlineData("GAMMA", "+5 °C", "Après consignation")]
    [InlineData("DELTA", "15 bar", "Contrôle requis")]
    public void Table_literal_fidelity_preserves_contiguous_row_facts_without_invented_pipe(
        string module, string limit, string observation)
    {
        var source = TableLiteralSource("MATRICE DES LIMITES - PREMIÈRE PAGE");
        var cells = source.Tables[0].Data.Cells;
        cells.Single(cell => cell.RowIndex == 2 && cell.ColumnIndex == 0).Text = module;
        cells.Single(cell => cell.RowIndex == 2 && cell.ColumnIndex == 1).Text = limit;
        cells.Single(cell => cell.RowIndex == 2 && cell.ColumnIndex == 2).Text = observation;
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunks = DoclingCanonicalRetrievalProjector.Project(canonical, source, 220, 1);
        var table = Assert.Single(chunks, chunk => chunk.ChunkType == DoclingCanonicalRetrievalProjector.TableChunkType);
        var fact = string.Join(" ", new[] { module, limit, observation }.Where(value => value.Length > 0));

        Assert.Contains(fact, Regex.Replace(table.Text, @"\s+", " "), StringComparison.Ordinal);
        Assert.DoesNotContain(" | ", table.Text, StringComparison.Ordinal);
        Assert.Contains(module + "\t" + limit, table.Text, StringComparison.Ordinal);
        Assert.Equal(canonical.Pages[0].Tables[0].Cells.Count, table.CanonicalTableCellIds!.Count);
        Assert.Empty(CanonicalContractValidator.Validate(canonical));
    }

    [Theory]
    [InlineData("MATRICE DES LIMITES - PREMIÈRE PAGE", "MATRICE DES LIMITES — PREMIÈRE PAGE")]
    [InlineData("MATRICE DES LIMITES - SUITE", "MATRICE DES LIMITES — SUITE")]
    public void Table_literal_fidelity_selects_unique_spatial_native_title_and_retains_docling_observation(
        string converted, string literal)
    {
        var source = TableLiteralSource(converted);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = canonical.Pages[0].Tables[0].Cells[0];
        var rawHash = before.Text.RawSha256;

        var native = TableLiteralNative(literal,
            new ExtractedPdfLayoutBlock(0, literal, 46, 275.2060529, 657.361328125, 647.5, "nearest_neighbour+recursive_xy_cut"));
        var summary = CanonicalNativeTextCoverageReconciler.Apply(canonical, native);
        Assert.Equal(1, summary.ReconciledTableCellCount);
        Assert.Equal(0, CanonicalNativeTextCoverageReconciler.Apply(canonical, native).ReconciledTableCellCount);

        var cell = canonical.Pages[0].Tables[0].Cells[0];
        Assert.Equal(converted, cell.Text.Raw);
        Assert.Equal(rawHash, cell.Text.RawSha256);
        Assert.Equal(literal, cell.Text.Canonical);
        Assert.Equal(literal, cell.Text.Retrieval);
        Assert.Equal(literal, cell.Text.Display);
        Assert.Contains(cell.BlockIds, id => canonical.Pages[0].Blocks.Any(block => block.BlockId == id
            && block.Text.Canonical == literal && block.Provenance.Engine == "PdfPig"));
        Assert.Empty(CanonicalContractValidator.Validate(canonical));
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("outside_cell")]
    [InlineData("changed_value")]
    [InlineData("numeric_dash")]
    [InlineData("changed_negation")]
    public void Table_literal_fidelity_does_not_replace_cell_from_uncertain_or_factually_different_native_observation(string kind)
    {
        var converted = kind switch
        {
            "changed_value" or "numeric_dash" => "Operating limit -5 °C",
            "changed_negation" => "Opening is permitted",
            _ => "MATRICE DES LIMITES - PREMIÈRE PAGE"
        };
        var literal = kind switch
        {
            "changed_value" => "Operating limit +5 °C",
            "numeric_dash" => "Operating limit —5 °C",
            "changed_negation" => "Opening is not permitted",
            _ => "MATRICE DES LIMITES — PREMIÈRE PAGE"
        };
        var source = TableLiteralSource(converted);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var blocks = new List<ExtractedPdfLayoutBlock>
        {
            new(0, literal, kind == "outside_cell" ? 331 : 46,
                kind == "outside_cell" ? 550 : 275.2060529, 657.361328125, 647.5, "native_test")
        };
        if (kind == "ambiguous")
            blocks.Add(new(1, "MATRICE DES LIMITES – PREMIÈRE PAGE", 46, 275.2060529,
                657.361328125, 647.5, "native_test"));

        CanonicalNativeTextCoverageReconciler.Apply(canonical, TableLiteralNative(literal, blocks.ToArray()));

        Assert.Equal(converted, canonical.Pages[0].Tables[0].Cells[0].Text.Canonical);
        Assert.Equal(converted, canonical.Pages[0].Tables[0].Cells[0].Text.Raw);
    }

    [Fact]
    public void Table_literal_fidelity_preserves_a_literal_pipe_inside_a_source_cell()
    {
        var source = TableLiteralSource("MATRICE DES LIMITES - PREMIÈRE PAGE");
        source.Tables[0].Data.Cells.Single(cell => cell.RowIndex == 2 && cell.ColumnIndex == 0).Text = "AC|DC";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var table = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 220, 1),
            chunk => chunk.ChunkType == DoclingCanonicalRetrievalProjector.TableChunkType);
        Assert.Contains("AC|DC", table.Text, StringComparison.Ordinal);
        Assert.Equal("AC|DC", canonical.Pages[0].Tables[0].Cells.Single(cell => cell.RowIndex == 2 && cell.ColumnIndex == 0).Text.Raw);
    }

    private static DoclingDocument TableLiteralSource(string title)
    {
        var source = BuildDocument();
        source.Pages["1"].Size = new() { Width = 595.2756, Height = 841.8898 };
        var table = source.Tables[0];
        table.Provenance = [Provenance(Box(40, 675, 535, 480, "BOTTOMLEFT"))];
        table.Data.RowCount = 3;
        table.Data.ColumnCount = 3;
        var titleCell = Cell(0, 0, title, Box(46, 655.3774, 275.2061, 645.2194, "BOTTOMLEFT"));
        titleCell.ColumnSpan = 3;
        table.Data.Cells =
        [
            titleCell,
            Cell(1, 0, "Module", Box(46, 613, 83, 604, "BOTTOMLEFT"), true),
            Cell(1, 1, "Limite", Box(221, 613, 251, 604, "BOTTOMLEFT"), true),
            Cell(1, 2, "Observation", Box(331, 613, 391, 604, "BOTTOMLEFT"), true),
            Cell(2, 0, "ALPHA", Box(46, 566, 83, 557, "BOTTOMLEFT")),
            Cell(2, 1, "-5 °C", Box(221, 566, 247, 557, "BOTTOMLEFT")),
            Cell(2, 2, "Ne pas ouvrir", Box(331, 566, 397, 557, "BOTTOMLEFT"))
        ];
        return source;
    }

    private static PdfExtractionResult TableLiteralNative(string literal, params ExtractedPdfLayoutBlock[] blocks)
    {
        var text = literal + "\nModule Limite Observation\nALPHA -5 °C Ne pas ouvrir\nThe physical source records its verified operating restriction and maintenance procedure.";
        var native = FidelityNative((1, text));
        native.Pages[0] = native.Pages[0] with
        {
            WidthPoints = 595.2756, HeightPoints = 841.8898, NativeLayoutBlocks = blocks
        };
        return native;
    }
}
