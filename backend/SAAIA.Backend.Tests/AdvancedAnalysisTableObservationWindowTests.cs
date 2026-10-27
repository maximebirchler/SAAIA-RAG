using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisTableObservationWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Raw_and_band_windows_keep_distinct_exact_orders_and_unassigned_lines()
    {
        var observation = Observation();
        var raw = Build(observation, "raw", limit: 2);
        Assert.Equal(new[] { "line-0", "line-1" }, raw.Lines!.Select(line => line.LineId));
        Assert.Equal(2, raw.NextLineOffset);
        Assert.True(raw.WindowComplete); Assert.False(raw.EntireViewReturned);
        var band = Build(observation, "physical_band", band: 0);
        Assert.Equal(new[] { "line-2", "line-0" }, band.Lines!.Select(line => line.LineId));
        Assert.Equal(new[] { 2, 0 }, band.Lines!.Select(line => line.RawOrdinal));
        Assert.Equal(new[] { 0, 1 }, band.Lines!.Select(line => line.ViewOrdinal));
        var unassigned = Build(observation, "unassigned");
        Assert.Equal("line-1", Assert.Single(unassigned.Lines!).LineId);
        Assert.False(raw.PdfCoverageVerified); Assert.False(band.CanonicalGridRepaired);
        Assert.Equal("not_verified", band.SemanticAccuracy);
    }

    [Fact]
    public void Pagination_keeps_whole_lines_and_never_restarts_raw_ordinals_for_band_offsets()
    {
        var observation = Observation();
        var second = Build(observation, "physical_band", band: 0, offset: 1, limit: 1);
        var line = Assert.Single(second.Lines!);
        Assert.Equal("line-0", line.LineId); Assert.Equal(1, line.ViewOrdinal);
        Assert.Equal(observation.Lines[0].Text, line.Text);
        Assert.Null(second.NextLineOffset); Assert.False(second.EntireViewReturned);
    }

    [Fact]
    public void Serializer_escaping_and_metadata_are_counted_before_returning_a_whole_window()
    {
        var observation = Observation();
        observation.Lines[2].Text = "Exact \"quoted\" \\ path\n-20,5 °C — ≤ 18";
        observation.Lines[2].TextSha256 = Hash(observation.Lines[2].Text);
        var full = Build(observation, "raw");
        var exactSize = JsonSerializer.Serialize(full, Web).Length;
        var fitsExactly = Build(observation, "raw", budget: exactSize);
        Assert.Equal(3, fitsExactly.ReturnedLineCount);
        var smaller = Build(observation, "raw", budget: exactSize - 1);
        Assert.True(JsonSerializer.Serialize(smaller, Web).Length <= exactSize - 1);
        Assert.True(smaller.ReturnedLineCount < 3);
        Assert.All(smaller.Lines!, line => Assert.Equal(observation.Lines[line.RawOrdinal].Text, line.Text));
        Assert.Equal("partial_budget", smaller.Status); Assert.False(smaller.WindowComplete);
    }

    [Fact]
    public void First_indivisible_line_too_large_returns_refusal_and_unchanged_offset_without_a_text_prefix()
    {
        var observation = Observation(); observation.Lines[0].Text = new string('x', 8_000);
        observation.Lines[0].TextSha256 = Hash(observation.Lines[0].Text);
        var result = Build(observation, "raw", budget: 1_500);
        Assert.Equal("line_does_not_fit", result.Status);
        Assert.Empty(result.Lines!); Assert.Equal(0, result.NextLineOffset);
        Assert.False(result.WindowComplete); Assert.False(result.EntireViewReturned);
        Assert.DoesNotContain(new string('x', 50), JsonSerializer.Serialize(result, Web));
    }

    [Fact]
    public void Observed_empty_raw_view_is_distinct_from_unavailable_physical_reading()
    {
        var observation = Observation(); observation.Lines.Clear();
        var empty = Build(observation, "raw");
        Assert.Empty(empty.Lines!); Assert.Equal(0, empty.RawLineCount);
        Assert.Equal(0, empty.ViewLineCount); Assert.Equal(0, empty.ReturnedLineCount);
        Assert.True(empty.WindowComplete); Assert.True(empty.EntireViewReturned);
        observation.PhysicalReading.Availability = "unavailable";
        observation.PhysicalReading.Bands = null;
        var unknown = Build(observation, "physical_band", band: 0);
        Assert.Null(unknown.Lines); Assert.Null(unknown.ViewLineCount); Assert.Null(unknown.ReturnedLineCount);
        Assert.False(unknown.WindowComplete); Assert.Equal("physical_reading_unavailable", unknown.Status);
    }

    [Theory]
    [InlineData("raw", 0, 0, 1)]
    [InlineData("physical_band", null, 0, 1)]
    [InlineData("raw", null, -1, 1)]
    [InlineData("raw", null, 0, 65)]
    [InlineData("raw", null, 4, 1)]
    public void Invalid_view_or_offset_is_refused_without_fallback(string view, int? band, int offset, int limit)
        => Assert.Throws<AdvancedAnalysisToolException>(() => Build(Observation(), view, band, offset, limit));

    [Fact]
    public void Unknown_band_or_line_or_unanchored_line_is_not_replaced_by_another_observation()
    {
        var observation = Observation();
        Assert.Throws<AdvancedAnalysisToolException>(() => Build(observation, "physical_band", band: 1));
        observation.PhysicalReading.Bands![0].OrderedLineIds.Add("missing");
        Assert.Throws<AdvancedAnalysisToolException>(() => Build(observation, "physical_band", band: 0));
        Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisTableObservationWindows.Build(
            Observation(), "raw", null, 0, 1, new Dictionary<string, string>(), 10_000));
    }

    private static AdvancedAnalysisTableObservationView Build(CanonicalTableRegionObservation observation,
        string view, int? band = null, int offset = 0, int limit = 64, int budget = 20_000)
        => AdvancedAnalysisTableObservationWindows.Build(observation, view, band, offset, limit,
            observation.Lines.ToDictionary(line => line.LineId, line => "anchor-" + line.LineId), budget);

    private static CanonicalTableRegionObservation Observation()
        => new()
        {
            ObservationId = "observation-1", TableId = "table-1", PhysicalPage = 3,
            RawObservationSha256 = new string('a', 64),
            QualitySignals = ["alternative_observation", "cell_membership_not_proven", "semantic_accuracy_not_verified"],
            Lines = Enumerable.Range(0, 3).Select(index => new CanonicalTableRegionObservationLine
            {
                LineId = "line-" + index, RawOrdinal = index, Text = "Literal line " + index,
                TextSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Literal line " + index))).ToLowerInvariant(),
                Confidence = index == 1 ? null : 0.99
            }).ToList(),
            PhysicalReading = new()
            {
                Availability = "candidate_only",
                Bands = [new() { BandOrdinal = 0, OrderedLineIds = ["line-2", "line-0"] }],
                UnassignedLineIds = ["line-1"]
            }
        };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
