using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalObservationSelectionTests
{
    [Fact]
    public void Views_PublishOnePrimaryInsteadOfThreeObservationsOfTheSameQuantity()
    {
        var document = Document(
            Block("primary", "text", "1,2 kg de matière sèche"),
            Block("flat", "native_text_recovery", "• 1,2 kg de matière sèche"),
            Block("layout", "native_layout_recovery", "1,2 kg de matière sèche"));

        var units = Units(document);

        Assert.Single(units);
        Assert.Equal("1,2 kg de matière sèche", units[0].Text);
        Assert.Equal(3, document.Pages[0].Blocks.Count);
        Assert.Equal("• 1,2 kg de matière sèche", document.Pages[0].Blocks[1].Text.Raw);
    }

    [Fact]
    public void Views_SelectOnlyTheRealMissingLineFromAPartiallyCoveredRecovery()
    {
        var document = Document(
            Block("primary", "text", "Inspect the enclosure before maintenance."),
            Block("flat", "native_text_recovery", "Inspect the enclosure before maintenance.\nTorque limit: 12,5 N·m."));

        var units = Units(document);

        Assert.Equal(2, units.Count);
        Assert.Equal("Torque limit: 12,5 N·m.", units[1].Text);
        Assert.Contains("Inspect the enclosure", document.Pages[0].Blocks[1].Text.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Views_KeepScrambledNativeOrderForAuditInsteadOfPublishingItAgain()
    {
        const string primary = "Carefully inspect the assembly before opening the valve.";
        var document = Document(
            Block("primary", "text", primary),
            Block("layout", "native_layout_recovery", "valve.\nthe\nopening\nbefore\nassembly\nthe\ninspect\nCarefully"));

        Assert.Equal(primary, Assert.Single(Units(document)).Text);
        Assert.Equal(2, document.Pages[0].Blocks.Count);
    }

    [Fact]
    public void Views_PreserveTwoLegitimatePrimaryOccurrencesWithoutPublishingEachNativeCopy()
    {
        var document = Document(
            Block("primary_a", "text", "1 dose de produit"),
            Block("primary_b", "text", "1 dose de produit"),
            Block("flat", "native_text_recovery", "• 1 dose de produit\n• 1 dose de produit"),
            Block("layout", "native_layout_recovery", "1 dose de produit\n1 dose de produit"));

        var units = Units(document);

        Assert.Equal(2, units.Count);
        Assert.All(units, unit => Assert.Equal("1 dose de produit", unit.Text));
    }

    [Fact]
    public void Views_PreserveAnAdditionalPhysicalOccurrenceMissingFromPrimary()
    {
        var document = Document(
            Block("primary", "text", "Approved"),
            Block("flat", "native_text_recovery", "Approved\nApproved"),
            Block("layout", "native_layout_recovery", "Approved\nApproved"));

        var units = Units(document);

        Assert.Equal(2, units.Count);
        Assert.All(units, unit => Assert.Equal("Approved", unit.Text));
    }

    [Theory]
    [InlineData("20", "-20")]
    [InlineData("20", "20,5")]
    [InlineData("20", "20.5")]
    [InlineData("Approved", "Not Approved")]
    [InlineData("1 5", "15")]
    [InlineData("not able", "notable")]
    [InlineData("A B", "A-B")]
    [InlineData("<5", ">5")]
    public void Views_DoNotAssimilateDifferentValuesSignsOrNegation(string primary, string native)
    {
        var document = Document(
            Block("primary", "text", primary),
            Block("flat", "native_text_recovery", native));

        var texts = Units(document).Select(unit => unit.Text).ToArray();

        Assert.Contains(primary, texts);
        Assert.Contains(native, texts);
    }

    [Fact]
    public void Views_DoNotRemoveARepeatedPrimarySentenceOnAnotherPage()
    {
        var document = Document(Block("first", "text", "Disconnect power before maintenance."));
        document.Pages.Add(new() { PageNumber = 2, Blocks = [Block("second", "text", "Disconnect power before maintenance.")] });
        document.PageCount = 2;

        var units = Units(document);

        Assert.Equal(2, units.Count);
        Assert.Equal(new[] { 1, 2 }, units.Select(unit => unit.PageStart));
    }

    [Fact]
    public void Views_KeepStructuredRowsInsteadOfAParallelColumnMajorNativeCopy()
    {
        var document = Document(Block("flat", "native_text_recovery", "Module\nALPHA\nBETA\nLimit\n-5 °C\n+5 °C"));
        document.Pages[0].Tables.Add(new()
        {
            TableId = "table_limits", RowCount = 3, ColumnCount = 2,
            Cells =
            [
                Cell("h0", 0, 0, "Module"), Cell("h1", 0, 1, "Limit"),
                Cell("a0", 1, 0, "ALPHA"), Cell("a1", 1, 1, "-5 °C"),
                Cell("b0", 2, 0, "BETA"), Cell("b1", 2, 1, "+5 °C")
            ]
        });

        var texts = Units(document).Select(unit => unit.Text).ToArray();

        Assert.Equal(new[] { "Module\tLimit", "ALPHA\t-5 °C", "BETA\t+5 °C" }, texts);
        Assert.Single(document.Pages[0].Tables);
        Assert.Single(document.Pages[0].Blocks);
    }

    [Fact]
    public void Views_FullWidthTableSeparatesAllUpperColumnsFromTheLowerNote()
    {
        var document = Document(
            Block("left_a", "text", "LEFT upper first", Box(.08, .15, .38, .19)),
            Block("left_b", "text", "LEFT upper second", Box(.08, .22, .38, .26)),
            Block("note", "text", "NOTE below the complete table", Box(.08, .76, .90, .80)),
            Block("right_a", "text", "RIGHT upper first", Box(.55, .15, .88, .19)),
            Block("right_b", "text", "RIGHT upper second", Box(.55, .22, .88, .26)));
        document.Pages[0].Tables.Add(new()
        {
            TableId = "table_barrier", RowCount = 1, ColumnCount = 1,
            Polygon = Box(.06, .40, .92, .68), Cells = [Cell("cell", 0, 0, "TABLE content")]
        });

        var text = string.Join("\n", Units(document).Select(unit => unit.Text));

        Assert.True(text.IndexOf("RIGHT upper second", StringComparison.Ordinal)
            < text.IndexOf("TABLE content", StringComparison.Ordinal));
        Assert.True(text.IndexOf("TABLE content", StringComparison.Ordinal)
            < text.IndexOf("NOTE below", StringComparison.Ordinal));
    }

    [Fact]
    public void Views_TerminalHeadingBelowBothColumnsIsReadAfterThemBeforeTheTable()
    {
        var document = Document(
            Block("left", "text", "Left paragraph", Box(.08, .15, .38, .26)),
            Block("terminal_heading", "section_header", "Observed lower heading", Box(.08, .33, .38, .35)),
            Block("right", "text", "Right paragraph", Box(.55, .15, .88, .26)));
        document.Pages[0].Tables.Add(new()
        {
            TableId = "table", RowCount = 1, ColumnCount = 1,
            Polygon = Box(.06, .40, .92, .68), Cells = [Cell("cell", 0, 0, "Table body")]
        });

        var text = string.Join("\n", Units(document).Select(unit => unit.Text));

        Assert.True(text.IndexOf("Right paragraph", StringComparison.Ordinal)
            < text.IndexOf("Observed lower heading", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Observed lower heading", StringComparison.Ordinal)
            < text.IndexOf("Table body", StringComparison.Ordinal));
        Assert.Empty(document.Pages[0].Tables[0].CaptionBlockIds);
        Assert.Equal(1, document.Pages[0].Blocks[1].ReadingOrder);
    }

    [Fact]
    public void Views_HeadingInsideOneColumnDoesNotBecomeATerminalHeading()
    {
        var document = Document(
            Block("left", "text", "Left paragraph", Box(.08, .15, .38, .19)),
            Block("heading", "section_header", "Left subsection", Box(.08, .22, .38, .24)),
            Block("left_continuation", "text", "Left continuation", Box(.08, .27, .38, .30)),
            Block("right", "text", "Right paragraph", Box(.55, .15, .88, .30)));
        document.Pages[0].Tables.Add(new()
        {
            TableId = "table", RowCount = 1, ColumnCount = 1,
            Polygon = Box(.06, .40, .92, .68), Cells = [Cell("cell", 0, 0, "Table body")]
        });

        var text = string.Join("\n", Units(document).Select(unit => unit.Text));

        Assert.True(text.IndexOf("Left subsection", StringComparison.Ordinal)
            < text.IndexOf("Left continuation", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Left continuation", StringComparison.Ordinal)
            < text.IndexOf("Right paragraph", StringComparison.Ordinal));
    }

    [Fact]
    public void Reconciliation_CompleteSourceInventoryPreservesARealExtraInstructionWithTheSameWords()
    {
        const string first = "Open A then B.";
        const string second = "Open B then A.";
        var document = Document(Block("primary", "text", first));
        var text = first + "\n" + second;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var native = new ExtractedPdfPage(1, text, words, text.Length,
            SHA256.HashData(Encoding.UTF8.GetBytes(text)), PdfPageExtractionQuality.FromText(text, words, text.Length));

        CanonicalNativeTextCoverageReconciler.Apply(document,
            new PdfExtractionResult([], [native], PdfExtractionQualitySummary.FromPages([native])));

        Assert.Equal(new[] { first, second }, Units(document).Select(unit => unit.Text));
        var recovery = Assert.Single(document.Pages[0].Blocks, block => block.BlockType == "native_text_recovery");
        // Coverage retained a second source occurrence. Its semantic novelty is
        // deliberately not inferred by the ingestion stage from that fact.
        Assert.Equal("literal_native_coverage_unresolved", recovery.Provenance.Attributes["selectionRole"]);
        Assert.Equal("not_asserted", recovery.Provenance.Attributes["semanticNovelty"]);
    }

    [Fact]
    public void Views_SelectedPageRawViewDoesNotReintroduceTheDiscardedContext()
    {
        var document = Document(
            Block("primary", "text", "Inspect the enclosure."),
            Block("flat", "native_text_recovery", "Inspect the enclosure.\nTorque limit: 12,5 N·m."));

        var page = Assert.Single(CanonicalExtractionViewProjector.Project(document, [], new DoclingDocument()).Pages);

        Assert.Equal(page.Text, page.RawText);
        Assert.Equal("Inspect the enclosure.\nTorque limit: 12,5 N·m.", document.Pages[0].Blocks[1].Text.Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Views_DoNotOverrideOrderWhenTheTableIsNarrowOrTheGeometryOverlaps(bool overlap)
    {
        var document = Document(
            Block("first", "text", "Observed first", Box(.08, .70, .35, .80)),
            Block("second", "text", "Observed second", overlap ? Box(.55, .50, .88, .60) : Box(.55, .15, .88, .20)));
        document.Pages[0].Tables.Add(new()
        {
            TableId = "ambiguous_table", RowCount = 1, ColumnCount = 1,
            Polygon = overlap ? Box(.06, .40, .92, .68) : Box(.06, .40, .40, .68),
            Cells = [Cell("cell", 0, 0, "table content")]
        });

        Assert.False(CanonicalObservationSelector.SelectPage(document.Pages[0]).HasPhysicalOrderOverride);
    }

    private static IReadOnlyList<ExtractedDocumentUnit> Units(CanonicalDocument document)
        => CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument());

    private static CanonicalDocument Document(params CanonicalBlock[] blocks)
    {
        for (var index = 0; index < blocks.Length; index++)
        {
            blocks[index].Ordinal = index;
            blocks[index].ReadingOrder = index;
        }
        return new()
        {
            DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Source = new() { Sha256 = new string('a', 64), MediaType = "application/pdf" },
            ManifestSha256 = new string('b', 64), PageCount = 1,
            Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" }, Blocks = blocks.ToList() }]
        };
    }

    private static CanonicalBlock Block(string id, string type, string text, CanonicalPolygon? polygon = null)
        => new()
        {
            BlockId = id, BlockType = type, Text = Text(text), Polygon = polygon,
            Provenance = new() { Engine = type.StartsWith("native_", StringComparison.Ordinal) ? "PdfPig" : "docling" }
        };

    private static CanonicalTableCell Cell(string id, int row, int column, string text)
        => new() { CellId = id, RowIndex = row, ColumnIndex = column, Text = Text(text) };

    private static CanonicalTextVariants Text(string text) => new()
    {
        Raw = text, Canonical = text, Display = text, Retrieval = text,
        Normalized = text.ToLowerInvariant(),
        RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
    };

    private static CanonicalPolygon Box(double left, double top, double right, double bottom) => new()
    {
        Points = [new() { X = left, Y = top }, new() { X = right, Y = top },
            new() { X = right, Y = bottom }, new() { X = left, Y = bottom }]
    };
}
