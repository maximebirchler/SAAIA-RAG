using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Text_inside_a_bounded_picture_is_preserved_without_borrowing_the_previous_page_heading()
    {
        var source = PictureTextDocument();
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = Assert.Single(canonical.Pages[1].Blocks);
        Assert.Equal("Observed label 42", block.Text.Canonical);
        Assert.Equal("not_asserted", block.Provenance.Attributes["sectionAssociation"]);
        Assert.All(canonical.SectionTree, section => Assert.DoesNotContain(block.BlockId, section.ContentBlockIds));
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1), c => c.PageStart == 2);
        Assert.Equal("Observed label 42", chunk.Text);
        Assert.Equal(RetrievalChunkProjector.PhysicalPageContextChunkType, chunk.ChunkType);
        Assert.Null(chunk.SectionOrdinal);
        Assert.Null(chunk.SectionTitle);
        Assert.Empty(chunk.CanonicalContextBlockIds!);
        Assert.Contains(block.BlockId, chunk.CanonicalBlockIds!);
        Assert.Null(Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source), u => u.PageStart == 2).SectionOrdinal);
        Assert.Null(Assert.Single(CanonicalProfileInputProjector.Project([chunk])).SectionOrdinal);
        Assert.Empty(CanonicalContractValidator.Validate(canonical));
        CanonicalFigureTextAssociationPolicy.Apply(canonical);
        Assert.Single(block.QualityFlags, f => f == CanonicalFigureTextAssociationPolicy.QualityFlag);
    }

    [Theory]
    [InlineData("full_page_scan")]
    [InlineData("partial_overlap")]
    [InlineData("explicit_caption")]
    [InlineData("caption_label")]
    public void A_scan_partial_overlap_or_explicit_caption_is_not_reclassified_as_incidental_picture_text(string mode)
    {
        var source = PictureTextDocument();
        var picture = source.Pictures[0];
        if (mode == "full_page_scan") picture.Provenance[0].BoundingBox = Box(0, 200, 100, 0, "BOTTOMLEFT");
        if (mode == "partial_overlap") picture.Provenance[0].BoundingBox = Box(50, 160, 95, 120, "BOTTOMLEFT");
        if (mode == "explicit_caption") picture.Captions.Add(new() { Ref = source.Texts[^1].SelfRef });
        if (mode == "caption_label") source.Texts[^1].Label = "caption";
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var block = Assert.Single(canonical.Pages[1].Blocks);
        Assert.False(CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(block));
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1), c => c.PageStart == 2);
        Assert.Contains("Observed label 42", chunk.Text, StringComparison.Ordinal);
        Assert.Equal("Operating instructions", chunk.SectionTitle);
    }

    [Fact]
    public void Native_embedded_image_inventory_is_not_a_picture_semantics_classifier()
    {
        var source = PictureTextDocument();
        source.Pictures.Clear();
        source.Body.Children.RemoveAll(child => child.Ref.StartsWith("#/pictures/", StringComparison.Ordinal));
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        canonical.Pages[1].Figures.Add(new() { FigureId = "native_image", FigureType = "embedded_raster",
            Polygon = new() { Points = [new() { X = .05, Y = .2 }, new() { X = .95, Y = .2 },
                new() { X = .95, Y = .4 }, new() { X = .05, Y = .4 }] } });
        CanonicalFigureTextAssociationPolicy.Apply(canonical);
        Assert.False(CanonicalSourceAssociationPolicy.IsUnassignedPhysicalContext(Assert.Single(canonical.Pages[1].Blocks)));
    }

    [Fact]
    public void A_heading_is_not_carried_across_an_empty_physical_page()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Operating instructions", "section_header");
        AddFidelityText(source, "An independently observed original paragraph.");
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        source.Pages["3"] = new() { PageNumber = 3, Size = new() { Width = 100, Height = 200 } };
        const string later = "Unrelated observed poster text";
        AddFidelityText(source, later, page: 3);
        var canonical = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(canonical, source, 100, 1), c => c.PageStart == 3);
        Assert.Equal(later, chunk.Text);
        Assert.Null(chunk.SectionTitle);
        Assert.Null(chunk.SectionOrdinal);
        Assert.Empty(chunk.CanonicalContextBlockIds!);
        Assert.Null(Assert.Single(CanonicalProfileInputProjector.Project([chunk])).SectionOrdinal);
        Assert.Null(Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(canonical, source), unit => unit.PageStart == 3).SectionOrdinal);
    }

    private static DoclingDocument PictureTextDocument()
    {
        var source = FidelityDocument();
        AddFidelityText(source, "Operating instructions", "section_header", top: 195, bottom: 185);
        AddFidelityText(source, "An independently observed original paragraph.");
        source.Pages["2"] = new() { PageNumber = 2, Size = new() { Width = 100, Height = 200 } };
        AddFidelityText(source, "Observed label 42", page: 2);
        var picture = new DoclingPictureItem { SelfRef = "#/pictures/0", Parent = new() { Ref = "#/body" },
            Label = "picture", ContentLayer = "body",
            Provenance = [Provenance(Box(5, 160, 95, 120, "BOTTOMLEFT"), 2)] };
        source.Pictures.Add(picture);
        source.Body.Children.Add(new() { Ref = picture.SelfRef });
        return source;
    }
}
