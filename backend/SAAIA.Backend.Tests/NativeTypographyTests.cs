using System.Reflection;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeTypographyTests
{
    [Theory]
    [InlineData("observed", "[]", "0")]
    [InlineData("unavailable", "null", "null")]
    [InlineData("refused", "null", "null")]
    public void Native_typography_wire_preserves_observed_empty_and_unknown_states(string state, string runs, string count)
    {
        var json = "{\"pageNumber\":1,\"nativeTypographyObservations\":{\"schemaVersion\":\"native_typography_observation_set_v1\",\"runAvailability\":\""
            + state + "\",\"runs\":" + runs + ",\"observedGlyphCount\":" + count + ",\"renderedStyleVerified\":false}}";
        var page = CanonicalContractJson.Deserialize<CanonicalPage>(json);
        using var restored = JsonDocument.Parse(CanonicalContractJson.Serialize(page));
        Assert.True(restored.RootElement.TryGetProperty("nativeTypographyObservations", out var typography));
        Assert.Equal(state, typography.GetProperty("runAvailability").GetString());
        if (state == "observed") Assert.Equal(0, typography.GetProperty("observedGlyphCount").GetInt32());
        else Assert.False(typography.TryGetProperty("observedGlyphCount", out _));
    }

    [Fact]
    public void Native_typography_extraction_retains_separate_observation_without_changing_primary()
    {
        var method = typeof(PdfExtractor).GetMethod("Extract", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(Stream), typeof(CancellationToken), typeof(bool), typeof(bool)], modifiers: null);
        Assert.NotNull(method);
        var bytes = PdfNativeVectorGeometryTests.BuildPdf("");
        using var enabled = new MemoryStream(bytes);
        using var disabled = new MemoryStream(bytes);
        var result = Assert.IsType<PdfExtractionResult>(method.Invoke(null, [enabled, CancellationToken.None, false, true]));
        var page = Assert.Single(result.Pages);
        var observation = page.GetType().GetProperty("NativeTypographyObservation")?.GetValue(page);
        Assert.NotNull(observation);
        var original = Assert.Single(PdfExtractor.Extract(disabled, default).Pages);
        Assert.Equal(original.Text, page.Text);
        Assert.Equal(original.Checksum, page.Checksum);
        Assert.True(enabled.CanRead);
    }

    [Fact]
    public void Native_typography_default_off_keeps_legacy_json_unchanged()
        => Assert.DoesNotContain("nativeTypographyObservations", CanonicalContractJson.Serialize(new CanonicalPage { PageNumber = 1 }));
}
