using Xunit;
using System.Text.Json;

public sealed class CanonicalPngAssetTests
{
    [Fact]
    public void Docling_image_dimensions_can_use_integral_json_floating_numbers()
    {
        var image = JsonSerializer.Deserialize<DoclingImageReference>("{\"mimetype\":\"image/png\",\"uri\":\"data:image/png;base64,"
            + Convert.ToBase64String(VisualAssetTestPng.Bytes) + "\",\"size\":{\"width\":2.0,\"height\":2.0}}");
        Assert.Equal(2, CanonicalPngAsset.ReadEmbedded(image!).Width);
    }
    [Fact]
    public void Png_pixels_are_checked_without_fetching_or_interpreting_them()
    {
        var result = CanonicalPngAsset.ReadEmbedded(VisualAssetTestPng.Image());
        Assert.Equal(VisualAssetTestPng.Bytes, result.Bytes);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Fact]
    public void Corrupt_png_crc_is_rejected_instead_of_becoming_an_immutable_false_asset()
    {
        var bytes = VisualAssetTestPng.Bytes.ToArray();
        bytes[45] ^= 1;
        var error = Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.Validate(bytes));
        Assert.DoesNotContain(Convert.ToBase64String(bytes), error.Message);
    }

    [Fact]
    public void Truncated_png_and_trailing_bytes_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.Validate(VisualAssetTestPng.Bytes[..^4]));
        Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.Validate([.. VisualAssetTestPng.Bytes, 0]));
    }

    [Theory]
    [InlineData("data:image/jpeg;base64,AA==")]
    [InlineData("data:image/png;base64,not-base64!")]
    [InlineData("data:text/html;base64,AA==")]
    public void A_data_URI_is_not_accepted_merely_because_it_is_embedded(string uri)
    {
        var image = VisualAssetTestPng.Image();
        image.Uri = uri;
        Assert.Throws<InvalidDataException>(() => CanonicalPngAsset.ReadEmbedded(image));
    }
}
