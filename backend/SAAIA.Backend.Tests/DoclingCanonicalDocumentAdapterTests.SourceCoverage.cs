using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("table")]
    [InlineData("picture")]
    public void Nonempty_unlocated_source_item_is_rejected_instead_of_silently_disappearing(string kind)
    {
        var source = BuildDocument();
        switch (kind)
        {
            case "text": source.Texts[1].Provenance.Clear(); break;
            case "table": source.Tables[0].Provenance.Clear(); break;
            case "picture": source.Pictures[0].Provenance.Clear(); break;
        }
        var canonicalError = Assert.Throws<InvalidDataException>(
            () => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
        var legacyError = Assert.Throws<InvalidDataException>(() => DoclingLegacyExtractionAdapter.Project(
            new() { Document = new() { JsonContent = source } }));
        Assert.StartsWith("docling_unlocated_source_items", canonicalError.Message);
        Assert.Equal(canonicalError.Message, legacyError.Message);
        Assert.DoesNotContain("Raw body", canonicalError.Message);
        Assert.DoesNotContain("Canonical body", canonicalError.Message);
    }

    [Fact]
    public void Empty_unlocated_text_placeholder_does_not_block_located_content()
    {
        var source = BuildDocument();
        source.Texts.Add(new() { SelfRef = "#/texts/4", Text = "  ", OriginalText = "\n" });
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Equal(4, Assert.Single(document.Pages).Blocks.Count);
    }

    [Fact]
    public void Known_page_without_geometry_is_preserved_without_fabricating_a_box()
    {
        var source = BuildDocument();
        source.Texts[1].Provenance[0].BoundingBox = null;
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = Assert.Single(Assert.Single(document.Pages).Blocks, b => b.Text.Canonical == "Canonical body");
        Assert.Null(block.Polygon);
        Assert.Contains("spatial_anchor_unavailable", block.QualityFlags);
    }
}
