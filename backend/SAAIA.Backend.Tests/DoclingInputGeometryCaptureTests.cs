using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class DoclingInputGeometryCaptureTests
{
    [Fact]
    public void Input_geometry_budget_checks_current_scope_and_never_double_counts()
    {
        long clock = 0;
        var budget = new DoclingInputGeometryBudget(() => clock, 100);
        using (budget.Work(CancellationToken.None))
        {
            clock = 101;
            Assert.Throws<DoclingInputGeometryRefusal>(() => budget.Check(CancellationToken.None));
        }
        Assert.Equal(101, budget.UsedTicks);
        Assert.Throws<DoclingInputGeometryRefusal>(() => budget.Work(CancellationToken.None));
    }

    [Fact]
    public async Task Input_geometry_expired_hash_budget_continues_exact_upload_without_claim()
    {
        long clock = 0;
        var budget = new DoclingInputGeometryBudget(() => clock++, 1);
        var capture = new DoclingInputGeometryCapture(1, budget, true);
        var path = Path.GetTempFileName();
        byte[] expected = Encoding.UTF8.GetBytes("%PDF synthetic exact bytes");
        try
        {
            await File.WriteAllBytesAsync(path, expected);
            await using var file = File.OpenRead(path);
            using var output = new MemoryStream();
            await capture.SerializePdfAsync(file, output, CancellationToken.None);
            Assert.Equal(expected, output.ToArray());
            Assert.Equal("observer_budget_exceeded", capture.Data.Reason);
            Assert.Null(capture.Data.SerializedPdfSha256);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Input_geometry_multiple_serializations_are_not_claimed_as_one_upload()
    {
        var capture = new DoclingInputGeometryCapture(1, new(), true);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await using var file = File.OpenRead(path);
            await capture.SerializePdfAsync(file, Stream.Null, CancellationToken.None);
            Assert.NotNull(capture.Data.SerializedPdfSha256);
            await capture.SerializePdfAsync(file, Stream.Null, CancellationToken.None);
            Assert.Null(capture.Data.SerializedPdfSha256);
            Assert.Equal("ambiguous_upload_serialization", capture.Data.Reason);
            Assert.Equal(2, capture.Data.Serializations);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"document\":{\"json_content\":{\"pages\":{\"1\":{\"page_no\":2}},\"tables\":[]}}}", "page_binding_inconsistent")]
    [InlineData("{\"document\":{\"json_content\":{\"pages\":{\"1\":{},\"\\u0031\":{}},\"tables\":[]}}}", "duplicate_geometry_key")]
    [InlineData("{\"document\":{\"json_content\":{\"pages\":{},\"tables\":[],\"tables\":[]}}}", "duplicate_geometry_key")]
    [InlineData("{\"document\":{\"json_content\":{\"pages\":{}}}}", "geometry_inventory_unavailable")]
    public async Task Input_geometry_ambiguous_or_absent_inventory_is_not_observed_empty(string body, string reason)
    {
        var capture = await Capture();
        capture.ObserveResponse(Encoding.UTF8.GetBytes(body), CancellationToken.None);
        Assert.Equal(reason, capture.Data.Reason);
        Assert.False(capture.Data.SourceInventoryComplete);
        Assert.Null(capture.Data.OmittedCount);
    }

    [Fact]
    public async Task Input_geometry_skips_large_text_and_pixels_without_copying_them()
    {
        var capture = await Capture();
        var body = "{\"document\":{\"json_content\":{\"pages\":{},\"texts\":[{\"text\":\"PRIVATE_WORDS\"}],\"pictures\":[{\"image\":\"data:image/png;base64," + new string('A', 100000) + "\"}],\"tables\":[]}}}";
        capture.ObserveResponse(Encoding.UTF8.GetBytes(body), CancellationToken.None);
        Assert.Equal("observed", capture.Data.Availability);
        Assert.DoesNotContain("PRIVATE_WORDS", capture.DebugSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", capture.DebugSnapshotJson, StringComparison.Ordinal);
        Assert.Empty(capture.Data.Tokens);
    }

    [Fact]
    public async Task Input_geometry_observes_exact_tokens_after_more_than_64_mib_of_ignored_pixels()
    {
        var capture = await Capture();
        var prefix = Encoding.UTF8.GetBytes("{\"document\":{\"json_content\":{\"pages\":{\"1\":{\"image\":{\"uri\":\"data:image/png;base64,");
        const string width = "100.00000000000000001";
        var suffix = Encoding.UTF8.GetBytes("\"},\"page_no\":1,\"size\":{\"width\":" + width + ",\"height\":200}},\"2\":{\"page_no\":2,\"size\":{\"width\":100,\"height\":200}}},\"tables\":[]}}}");
        const int imageBytes = 64 * 1024 * 1024 + 1024;
        var body = new byte[prefix.Length + imageBytes + suffix.Length];
        prefix.CopyTo(body, 0);
        body.AsSpan(prefix.Length, imageBytes).Fill((byte)'A');
        suffix.CopyTo(body, prefix.Length + imageBytes);

        capture.ObserveResponse(body, CancellationToken.None);

        Assert.Equal("observed", capture.Data.Availability);
        Assert.Null(capture.Data.Reason);
        Assert.True(capture.Data.SourceInventoryComplete);
        Assert.Equal(2, capture.Data.PageCount);
        Assert.Equal(0, capture.Data.TableCount);
        Assert.Equal(body.LongLength, capture.Data.ResponseBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), capture.Data.ResponseSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), capture.Data.SerializedPdfSha256);
        var token = capture.Data.Pages["1"].Fields["size.width"];
        Assert.Equal(width, token.RawToken);
        Assert.Equal(width, Encoding.UTF8.GetString(body.AsSpan(checked((int)token.Offset), token.Length)));
        Assert.True(token.Offset > 64 * 1024 * 1024);
        Assert.Equal((long)prefix.Length + imageBytes + Encoding.UTF8.GetString(suffix).IndexOf(width, StringComparison.Ordinal), token.Offset);
        Assert.DoesNotContain("data:image", capture.DebugSnapshotJson, StringComparison.Ordinal);
        Assert.Equal(8 * 1024 * 1024, capture.Data.Limits.PayloadJsonBytes);
        Assert.Equal(4 * 1024 * 1024, capture.Data.Limits.PayloadGzipBytes);
        Assert.Equal(5000, capture.Data.Limits.OwnWorkMilliseconds);
    }

    [Fact]
    public async Task Input_geometry_caller_cancellation_is_not_a_diagnostic_refusal()
    {
        var capture = await Capture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => capture.ObserveResponse(Encoding.UTF8.GetBytes("{}"), cancelled.Token));
        Assert.Null(capture.Data.Reason);
    }

    [Fact]
    public async Task Input_geometry_entity_cap_retains_known_size_and_full_effective_limits()
    {
        const int limit = 1024 * 1024;
        var capture = await Capture(limit);
        capture.ObserveResponse(new byte[limit + 1], CancellationToken.None);
        Assert.Equal("geometry_entity_limit", capture.Data.Reason);
        Assert.Equal(limit + 1L, capture.Data.ResponseBytes);
        Assert.Equal(limit, capture.Data.Limits.ResponseEntityBytes);
        Assert.Null(capture.Data.ResponseSha256);
        var stage = capture.CreateStage();
        Assert.Equal((limit + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture), stage.Attributes["responseBytes"]);
        Assert.Equal(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), stage.Attributes["entityLimitBytes"]);
        Assert.Equal(SAAIA.Contracts.DocumentIntelligence.CanonicalContractJson.ComputeSha256(capture.Data.Limits), stage.OptionsSha256);
    }

    [Fact]
    public void Input_geometry_entity_limit_cannot_exceed_the_primary_transport_bounds()
    {
        Assert.Equal(DocumentIntelligenceOptions.DefaultMaxResponseBytes, DoclingInputGeometryCapture.MaximumEntityBytes);
        foreach (var limit in new[] { 0L, 1024 * 1024 - 1L, 2L * 1024 * 1024 * 1024 + 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new DoclingInputGeometryCapture(1, new(), true, limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(int.MaxValue - 56)]
    [InlineData(int.MaxValue)]
    public void Input_geometry_hash_cursor_reaches_large_entity_end_without_integer_wrap(int length)
    {
        var offset = 0;
        var blocks = 0;
        while (offset < length)
        {
            var next = DoclingInputGeometryCapture.NextHashOffset(offset, length);
            Assert.InRange(next, offset + 1, length);
            Assert.InRange(next - offset, 1, 64 * 1024);
            offset = next;
            blocks++;
        }
        Assert.Equal(length, offset);
        Assert.Equal((length + 65535L) / 65536L, blocks);
        Assert.Equal(length, DoclingInputGeometryCapture.NextHashOffset(length, length));
    }

    [Fact]
    public void Input_geometry_hash_cursor_rejects_out_of_bounds_offsets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DoclingInputGeometryCapture.NextHashOffset(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DoclingInputGeometryCapture.NextHashOffset(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => DoclingInputGeometryCapture.NextHashOffset(11, 10));
    }

    private static async Task<DoclingInputGeometryCapture> Capture(long maximumEntityBytes = DoclingInputGeometryCapture.MaximumEntityBytes)
    {
        var capture = new DoclingInputGeometryCapture(1, new(), true, maximumEntityBytes);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await using var input = File.OpenRead(path);
            await capture.SerializePdfAsync(input, Stream.Null, CancellationToken.None);
            return capture;
        }
        finally { File.Delete(path); }
    }
}
