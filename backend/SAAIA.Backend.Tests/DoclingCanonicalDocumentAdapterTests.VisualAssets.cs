using System.Security.Cryptography;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Project_resolves_exported_pixels_from_a_zero_page_number_and_a_zero_padded_page_key()
    {
        var source = BuildDocument();
        var page = source.Pages["1"];
        page.PageNumber = 0;
        source.Pages.Clear(); source.Pages["01"] = page;
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Equal(1, Assert.Single(document.Assets).PageNumber);
    }

    [Fact]
    public void Project_preserves_actual_embedded_pixels_bound_to_page_figure_and_source()
    {
        var source = BuildDocument();
        source.Pages["1"].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Equal(2, document.Assets.Count);
        var figure = Assert.Single(document.Pages[0].Figures);
        var figureAsset = Assert.Single(document.Assets, asset => asset.FigureId == figure.FigureId);
        Assert.Contains(figureAsset.AssetId, figure.AssetIds);
        Assert.Equal(1, figureAsset.PageNumber);
        Assert.Equal(2, figureAsset.PixelWidth);
        Assert.Equal(2, figureAsset.PixelHeight);
        Assert.Equal(VisualAssetTestPng.Bytes, figureAsset.PublicationBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(VisualAssetTestPng.Bytes)).ToLowerInvariant(), figureAsset.Sha256);
        Assert.Equal("available", figure.Provenance.Attributes["visualAssetStatus"]);
        Assert.Equal("not_performed", figure.Provenance.Attributes["semanticExtractionStatus"]);
        Assert.DoesNotContain(Convert.ToBase64String(VisualAssetTestPng.Bytes), CanonicalContractJson.SerializeCanonical(document));
    }

    [Fact]
    public void Project_states_when_visual_pixels_were_not_actually_exported()
    {
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), BuildDocument());
        Assert.Empty(document.Assets);
        Assert.Equal("not_exported", document.Pages[0].Figures[0].Provenance.Attributes["visualAssetStatus"]);
    }

    [Theory]
    [InlineData("https://outside.invalid/private.png")]
    [InlineData("file:///private/source.png")]
    public void Project_does_not_fetch_an_external_or_file_image_URI(string uri)
    {
        var source = BuildDocument();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image!.Uri = uri;
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Fact]
    public void Project_rejects_declared_dimensions_that_do_not_match_actual_png_pixels()
    {
        var source = BuildDocument();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        source.Pictures[0].Image!.Size.Width = 999;
        Assert.Throws<InvalidDataException>(() => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));
    }

    [Fact]
    public void Project_visual_asset_identity_changes_with_the_source_revision()
    {
        var source = BuildDocument();
        source.Pictures[0].Image = VisualAssetTestPng.Image();
        var first = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var changed = DoclingCanonicalDocumentAdapter.Project(BuildContext() with { RevisionId = Guid.NewGuid() }, source);
        Assert.NotEmpty(first.Assets);
        Assert.NotEqual(first.Assets[0].AssetId, changed.Assets[0].AssetId);
    }
}
