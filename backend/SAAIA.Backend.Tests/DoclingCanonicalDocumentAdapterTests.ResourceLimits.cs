using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData(1_000_001, 1)]
    [InlineData(1, 1_000_001)]
    public void Project_RefusesOversizedGridBeforeAllocatingOccupancy(int rows, int columns)
    {
        var source = BuildDocument();
        source.Tables[0].Data.RowCount = rows;
        source.Tables[0].Data.ColumnCount = columns;
        source.Tables[0].Data.Cells.Clear();
        var exception = Assert.Throws<InvalidDataException>(
            () => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        Assert.Equal("canonical_table_grid_projection_limit", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Project_RefusesOverflowingCellRangeBeforeProjection(bool rowAxis)
    {
        var source = BuildDocument();
        var cell = source.Tables[0].Data.Cells[0];
        if (rowAxis)
        {
            cell.RowIndex = int.MaxValue;
            cell.RowSpan = 2;
        }
        else
        {
            cell.ColumnIndex = int.MaxValue;
            cell.ColumnSpan = 2;
        }
        var exception = Assert.Throws<InvalidDataException>(
            () => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        Assert.Contains("exceeds its declared dimensions", exception.Message);
    }
}
