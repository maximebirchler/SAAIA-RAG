using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Contracts.Tests;

public sealed class CanonicalTableBoundsOverflowTests
{
    [Theory]
    [InlineData("row")]
    [InlineData("column")]
    public void Canonical_table_bounds_reject_intmax_span_overflow(string axis)
    {
        var document = CanonicalContractFixture.CreateDocument();
        var table = document.Pages[0].Tables[0];
        table.RowCount = table.ColumnCount = 2;
        var cell = table.Cells[0];
        if (axis == "row") { cell.RowIndex = 1; cell.RowSpan = int.MaxValue; }
        else { cell.ColumnIndex = 1; cell.ColumnSpan = int.MaxValue; }
        Assert.Contains(CanonicalContractValidator.Validate(document), issue => issue.Code == "table_bounds");
    }

    [Fact]
    public void Canonical_table_bounds_allow_exact_positive_edge_without_overflow()
    {
        var document = CanonicalContractFixture.CreateDocument();
        var table = document.Pages[0].Tables[0];
        table.RowCount = table.ColumnCount = 2;
        table.Cells[0].RowSpan = table.Cells[0].ColumnSpan = 2;
        Assert.DoesNotContain(CanonicalContractValidator.Validate(document), issue => issue.Code == "table_bounds");
    }
}
