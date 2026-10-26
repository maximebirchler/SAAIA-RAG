using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed partial class DoclingClientTests
{
    [Theory]
    [InlineData(false, false, "placeholder")]
    [InlineData(true, false, "embedded")]
    [InlineData(false, true, "embedded")]
    [InlineData(true, true, "embedded")]
    public async Task Requested_images_select_an_explicit_export_mode(
        bool figures, bool pages, string expected)
    {
        string? mode = null;
        var factory = new StubHttpClientFactory(async request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var field = Assert.Single(form, part =>
                part.Headers.ContentDisposition?.Name?.Trim('"') == "image_export_mode");
            mode = await field.ReadAsStringAsync();
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(BuildResponse()),
                    Encoding.UTF8, "application/json")
            };
        });
        var client = new DoclingClient(factory, new()
        {
            IncludeImages = figures,
            IncludePageImages = pages
        });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.ConvertPdfAsync(path, CancellationToken.None);
            Assert.Equal(expected, mode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
