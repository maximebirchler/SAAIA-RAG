using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalObservationCellAliasTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Views_OnlyTheSamePhysicalTableCellCanReplaceAParallelPrimaryBlock(bool sameLocation)
    {
        var cellBox = Box(.70, .70, .72, .72);
        var blockBox = sameLocation ? Box(.70, .70, .732, .72) : Box(.10, .15, .132, .17);
        var document = new CanonicalDocument
        {
            DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Source = new() { Sha256 = new string('a', 64), MediaType = "application/pdf" },
            ManifestSha256 = new string('b', 64), PageCount = 1,
            Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" },
                Blocks = [new() { BlockId = "primary_35", BlockType = "text", Polygon = blockBox, Text = Text("35") }],
                Tables = [new() { TableId = "table", RowCount = 1, ColumnCount = 1,
                    Cells = [new() { CellId = "cell", RowIndex = 0, ColumnIndex = 0, Polygon = cellBox, Text = Text("35") }] }] }]
        };

        var units = CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument());

        Assert.Equal(sameLocation ? 1 : 2, units.Count);
        Assert.Single(document.Pages[0].Blocks);
        Assert.Equal("35", document.Pages[0].Blocks[0].Text.Raw);
    }

    private static CanonicalTextVariants Text(string value) => new()
    {
        Raw = value, Canonical = value, Display = value, Retrieval = value, Normalized = value,
        RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()
    };

    private static CanonicalPolygon Box(double left, double top, double right, double bottom) => new()
    {
        Points = [new() { X = left, Y = top }, new() { X = right, Y = top },
            new() { X = right, Y = bottom }, new() { X = left, Y = bottom }]
    };
}
