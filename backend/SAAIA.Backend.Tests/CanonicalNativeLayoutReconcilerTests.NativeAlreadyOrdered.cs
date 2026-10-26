using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class CanonicalNativeLayoutReconcilerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_flat_text_already_in_layout_order_does_not_hide_a_different_canonical_order(bool representedAsTable)
    {
        var values = new[] { "FIRST COLUMN HEADING", "FIRST ALPHA DETAIL", "FIRST BRAVO DETAIL",
            "SECOND COLUMN HEADING", "SECOND ECHO DETAIL", "SECOND FOXTROT DETAIL" };
        var rowOrder = new[] { values[0], values[3], values[1], values[4], values[2], values[5] };
        var document = Document(string.Join("\n", rowOrder));
        var native = NativePage(string.Join("\n", values), values.Select((value, index) =>
            Layout(index, value, index < 3 ? 40 : 330, 700 - (index % 3) * 30)).ToArray());
        if (representedAsTable)
        {
            document.Pages[0].Blocks.Clear();
            document.Pages[0].Tables.Add(new CanonicalTable
            {
                TableId = "table_observed_as_columns", RowCount = 3, ColumnCount = 2,
                Polygon = Box(0.01, 0.01, .99, .99),
                Cells = rowOrder.Select((value, index) => new CanonicalTableCell
                {
                    CellId = "cell_" + index, RowIndex = index / 2, ColumnIndex = index % 2,
                    Text = Text(value)
                }).ToList()
            });
        }

        var result = CanonicalNativeLayoutReconciler.Apply(document, document.Pages[0], native, false, "test");

        Assert.True(result.Applied, "A layout already faithful to native text can still challenge Docling's different order.");
        Assert.Equal(1d, result.OrderedAgreement, precision: 6);
        Assert.Equal(1d, result.BagAgreement, precision: 6);
        var alternatives = document.Pages[0].Blocks.Where(block => block.BlockType == CanonicalNativeLayoutReconciler.RecoveryBlockType)
            .OrderBy(block => block.ReadingOrder).ToArray();
        Assert.Equal(values, alternatives.Select(block => block.Text.Canonical));
        Assert.All(alternatives, block =>
        {
            Assert.NotNull(block.Polygon);
            Assert.Equal("llm_client", block.Provenance.Attributes["semanticDecisionOwner"]);
        });
        if (representedAsTable)
            Assert.Equal(rowOrder, Assert.Single(document.Pages[0].Tables).Cells.Select(cell => cell.Text.Canonical));
    }

    [Fact]
    public void Already_ordered_canonical_and_native_observations_do_not_need_a_duplicate_layout_alternative()
    {
        const string text = "FIRST ALPHA DETAIL\nFIRST BRAVO DETAIL\nSECOND ECHO DETAIL\nSECOND FOXTROT DETAIL";
        var document = Document(text);
        var native = NativePage(text, text.Split('\n').Select((value, index) => Layout(index, value, 40, 700-index*30)).ToArray());
        var result = CanonicalNativeLayoutReconciler.Apply(document, document.Pages[0], native, false, "test");
        Assert.False(result.Applied);
        Assert.Single(document.Pages[0].Blocks);
    }

    [Fact]
    public void A_geometric_candidate_with_unrelated_native_words_is_not_admitted_as_a_layout_alternative()
    {
        var document = Document("FIRST ALPHA DETAIL SECOND ECHO DETAIL FIRST BRAVO DETAIL SECOND FOXTROT DETAIL");
        var native = NativePage("Unrelated source inventory has no matching geometry observation vocabulary.",
            [Layout(0, "FIRST ALPHA DETAIL", 40, 700), Layout(1, "FIRST BRAVO DETAIL", 40, 670),
             Layout(2, "SECOND ECHO DETAIL", 330, 700), Layout(3, "SECOND FOXTROT DETAIL", 330, 670)]);
        var result = CanonicalNativeLayoutReconciler.Apply(document, document.Pages[0], native, false, "test");
        Assert.False(result.Applied);
        Assert.Single(document.Pages[0].Blocks);
    }
}
