using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed partial class DoclingClientTests
{
    [Theory]
    [InlineData("100.00000000000000001")]
    [InlineData("1.0000000000000000001e2")]
    public async Task Input_geometry_retains_exact_entity_tokens_lost_by_primary_double(string width)
    {
        var body = JsonSerializer.Serialize(BuildResponse()).Replace("\"width\":100", "\"width\":" + width, StringComparison.Ordinal);
        var response = await GeometryResponse(body, true, true, true);
        Assert.Equal(100d, response.Document.JsonContent!.Pages["1"].Size.Width);
        using var snapshot = GeometrySnapshot(response);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), snapshot.RootElement.GetProperty("responseSha256").GetString());
        Assert.Contains("\"" + width + "\"", snapshot.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("observed", snapshot.RootElement.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Input_geometry_response_without_upload_consumption_never_invents_source_hash()
    {
        var response = await GeometryResponse(JsonSerializer.Serialize(BuildResponse()), true, true, false);
        Assert.Equal("success", response.Status);
        using var snapshot = GeometrySnapshot(response);
        Assert.Equal("unavailable", snapshot.RootElement.GetProperty("availability").GetString());
        Assert.Equal("upload_not_serialized", snapshot.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("serializedPdfSha256").ValueKind);
    }

    [Fact]
    public async Task Input_geometry_duplicate_decoded_key_refuses_channel_but_keeps_primary_last_value()
    {
        var body = JsonSerializer.Serialize(BuildResponse()).Replace("\"width\":100", "\"width\":100,\"w\\u0069dth\":101", StringComparison.Ordinal);
        var response = await GeometryResponse(body, true, true, true);
        Assert.Equal(101, response.Document.JsonContent!.Pages["1"].Size.Width);
        using var snapshot = GeometrySnapshot(response);
        Assert.Equal("refused", snapshot.RootElement.GetProperty("availability").GetString());
        Assert.Equal("duplicate_geometry_key", snapshot.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Input_geometry_off_keeps_legacy_response_and_has_no_capture()
    {
        var response = await GeometryResponse(JsonSerializer.Serialize(BuildResponse()), false, true, true);
        Assert.Equal(100, response.Document.JsonContent!.Pages["1"].Size.Width);
        Assert.Null(response.Document.JsonContent.GetType().GetProperty("InputGeometryCapture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(response.Document.JsonContent));
    }

    [Fact]
    public async Task Input_geometry_requested_without_canonical_sink_keeps_primary_success()
    {
        var response = await GeometryResponse(JsonSerializer.Serialize(BuildResponse()), true, false, true);
        Assert.Equal("success", response.Status);
        // The refusal is observable even though this mode has no canonical manifest to persist.
        using var snapshot = GeometrySnapshot(response);
        Assert.Equal("canonical_artifacts_disabled", snapshot.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("responseSha256").ValueKind);
    }

    [Fact]
    public async Task Input_geometry_retry_binds_only_successful_attempt_and_entity()
    {
        var count = 0;
        var body = JsonSerializer.Serialize(BuildResponse());
        var factory = new StubHttpClientFactory(async request =>
        {
            await request.Content!.ReadAsByteArrayAsync();
            count++;
            return new(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(count == 1 ? "{\"private-failed-body\":true}" : body, Encoding.UTF8, "application/json")
            };
        });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var result = await new DoclingClient(factory, new() { DoclingInputGeometryCaptureEnabled = true })
                .ConvertPdfAsync(path, CancellationToken.None, inputGeometryPublicationAllowed: true);
            using var snapshot = GeometrySnapshot(result);
            Assert.Equal(2, count);
            Assert.Equal(2, snapshot.RootElement.GetProperty("attempt").GetInt32());
            Assert.Equal(1, snapshot.RootElement.GetProperty("serializations").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), snapshot.RootElement.GetProperty("responseSha256").GetString());
            Assert.DoesNotContain("private-failed-body", snapshot.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Input_geometry_no_sink_refusal_is_observable_in_operation_log()
    {
        var log = new RetryCaptureLogger();
        var body = JsonSerializer.Serialize(BuildResponse());
        var factory = new StubHttpClientFactory(async request =>
        {
            await request.Content!.ReadAsByteArrayAsync();
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var result = await new DoclingClient(factory, new() { DoclingInputGeometryCaptureEnabled = true }, log)
                .ConvertPdfAsync(path, CancellationToken.None);
            Assert.Equal("success", result.Status);
            Assert.Contains(log.Events, e => e.Contains("canonical_artifacts_disabled", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Events, e => e.Contains("json_content", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Input_geometry_disabled_option_is_omitted_from_legacy_options_json()
    {
        Assert.DoesNotContain("DoclingInputGeometryCaptureEnabled", JsonSerializer.Serialize(new DocumentIntelligenceOptions()), StringComparison.Ordinal);
        Assert.Contains("DoclingInputGeometryCaptureEnabled", JsonSerializer.Serialize(new DocumentIntelligenceOptions { DoclingInputGeometryCaptureEnabled = true }), StringComparison.Ordinal);
    }

    private static JsonDocument GeometrySnapshot(DoclingConvertResponse response)
    {
        var source = Assert.IsType<DoclingDocument>(response.Document.JsonContent);
        var capture = source.GetType().GetProperty("InputGeometryCapture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        Assert.NotNull(capture); // Real client consumed the entity; the diagnostic must survive that seam.
        var json = capture.GetType().GetProperty("DebugSnapshotJson", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(capture) as string;
        Assert.NotNull(json);
        return JsonDocument.Parse(json);
    }

    private static async Task<DoclingConvertResponse> GeometryResponse(string body, bool enabled, bool sink, bool consume)
    {
        var options = new DocumentIntelligenceOptions();
        options.GetType().GetProperty("DoclingInputGeometryCaptureEnabled")?.SetValue(options, enabled);
        var factory = new StubHttpClientFactory(async request =>
        {
            if (consume) await request.Content!.ReadAsByteArrayAsync();
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var client = new DoclingClient(factory, options);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [37, 80, 68, 70, 10]);
            var method = typeof(DoclingClient).GetMethod(nameof(DoclingClient.ConvertPdfAsync))!;
            var args = method.GetParameters().Length == 4
                ? new object?[] { path, CancellationToken.None, null, sink }
                : new object?[] { path, CancellationToken.None, null };
            return await (Task<DoclingConvertResponse>)method.Invoke(client, args)!;
        }
        finally { File.Delete(path); }
    }
}
