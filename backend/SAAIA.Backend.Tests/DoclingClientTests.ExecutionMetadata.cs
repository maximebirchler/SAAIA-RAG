using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed partial class DoclingClientTests
{
    [Theory]
    [InlineData("package")]
    [InlineData("force")]
    [InlineData("repair")]
    public async Task ConvertPdfAsync_rejects_observed_execution_mismatch_without_publishing_declared_provenance(string mismatch)
    {
        var response = BuildResponse();
        response.Execution = new() { ForceOcr = true, DoOcr = true, TableRepairEnabled = true,
            TableRepairScale = 2.5, TableRepairPaddingPoints = 12, TableRepairMinimumScore = 0.5,
            PackageVersions = new() { ["docling-serve"] = mismatch == "package" ? "9.0.0" : "1.27.0" } };
        if (mismatch == "force") response.Execution.ForceOcr = false;
        if (mismatch == "repair") response.Execution.TableRepairEnabled = false;
        var bytes = JsonSerializer.Serialize(response);
        var factory = new StubHttpClientFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(bytes, Encoding.UTF8, "application/json") }));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new DoclingClient(factory, new())
                .ConvertPdfAsync(path, CancellationToken.None, forceOcrOverride: true));
            Assert.Contains("docling_execution_", error.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConvertPdfAsync_retains_actual_force_override_and_observed_package_versions()
    {
        var response = BuildResponse();
        response.Execution = new() { ForceOcr = true, DoOcr = true,
            PackageVersions = new() { ["rapidocr"] = "3.9.1" } };
        var factory = new StubHttpClientFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") }));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var result = await new DoclingClient(factory, new()).ConvertPdfAsync(path, CancellationToken.None, true);
            Assert.True(result.RequestedForceOcr);
            Assert.Equal("3.9.1", result.Execution!.PackageVersions["rapidocr"]);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("http")]
    [InlineData("status")]
    [InlineData("schema")]
    public async Task ConvertPdfAsync_error_diagnostics_do_not_publish_upstream_document_content(string kind)
    {
        const string privateContent = "PRIVATE_DOCUMENT_SENTINEL_517293";
        var response = BuildResponse();
        if (kind == "status") response.Status = privateContent;
        if (kind == "schema") response.Document.JsonContent!.SchemaName = privateContent;
        var factory = new StubHttpClientFactory(_ => Task.FromResult(new HttpResponseMessage(
            kind == "http" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            { Content = new StringContent(kind == "http" ? privateContent : JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") }));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var error = await Record.ExceptionAsync(() => new DoclingClient(factory, new()).ConvertPdfAsync(path, CancellationToken.None));
            Assert.NotNull(error);
            Assert.DoesNotContain(privateContent, error.ToString(), StringComparison.Ordinal);
            Assert.Contains("docling_", error.Message, StringComparison.Ordinal);
            if (kind == "http") Assert.Equal(HttpStatusCode.InternalServerError, Assert.IsType<HttpRequestException>(error).StatusCode);
        }
        finally { File.Delete(path); }
    }

}
