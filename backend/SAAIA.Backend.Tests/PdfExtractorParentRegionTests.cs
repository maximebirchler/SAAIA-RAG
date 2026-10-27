using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using SAAIA.Backend;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractorParentRegionTests
{
    [Theory]
    [InlineData(1200, 840)]
    [InlineData(1080, 760)]
    public void A_real_spread_keeps_each_observed_parent_heading_before_its_three_body_columns(double width, double height)
    {
        var source = SpreadWords();
        using var parent = new Activity("parent-region-test").Start();
        var decisions = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = item => item.Name == BackendDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = item =>
            {
                if (item.TraceId == parent.TraceId) decisions.Enqueue(string.Join(",", item.TagObjects.Select(tag => tag.Key + "=" + tag.Value)));
            }
        };
        ActivitySource.AddActivityListener(listener);
        var text = Extract(source, width, height);

        AssertTokens(source, text);
        AssertBefore(text, "ASSEMBLY ALPHA", "alpha_inventory_0_240", string.Join(";", decisions));
        AssertBefore(text, "ASSEMBLY BETA", "beta_inventory_0_340");
        AssertBefore(text, "alpha_check_4_240", "ASSEMBLY BETA");
        Assert.Contains("Do not exceed 240 bar.", text, StringComparison.Ordinal);
        Assert.Contains(decisions, item => item.Contains("two_observed_parent_regions_candidate", StringComparison.Ordinal));
        Assert.DoesNotContain(decisions, item => item.Contains("ASSEMBLY", StringComparison.Ordinal)
            || item.Contains("inventory", StringComparison.Ordinal) || item.Contains("240", StringComparison.Ordinal));
    }

    [Fact]
    public void An_observed_source_fragment_crossing_the_center_prevents_parent_partition()
    {
        var words = SpreadWords();
        words.Add(new("SHARED SAFETY LIMIT 999", 550, 700));
        var (text, legacy) = ExtractResult(words);
        Assert.Equal(legacy, text);
        AssertTokens(words, text);
        Assert.Contains("SHARED SAFETY LIMIT 999", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_portrait_page_keeps_the_existing_column_reading_order()
    {
        var words = SpreadWords();
        var (text, legacy) = ExtractResult(words, 600, 840);
        Assert.Equal(legacy, text);
        AssertTokens(words, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gutter_does_not_invent_a_parent_without_both_observed_heading_prefixes(bool removeBoth)
    {
        var words = SpreadWords();
        words.RemoveAll(word => word.Text == "ASSEMBLY BETA" || removeBoth && word.Text == "ASSEMBLY ALPHA");
        var (text, legacy) = ExtractResult(words);
        Assert.Equal(legacy, text);
        AssertTokens(words, text);
    }

    [Fact]
    public void A_single_body_column_on_one_side_does_not_establish_two_nested_parents()
    {
        var words = SpreadWords();
        words.RemoveAll(word => word.Text.StartsWith("beta_method_", StringComparison.Ordinal)
            || word.Text.StartsWith("beta_check_", StringComparison.Ordinal));
        words.Add(new("beta_inventory_5_340", 650, 500));
        words.Add(new("beta_inventory_6_340", 650, 480));
        var (text, legacy) = ExtractResult(words);
        Assert.Equal(legacy, text);
        AssertTokens(words, text);
    }

    [Fact]
    public void Three_ordinary_columns_with_a_spanning_heading_are_not_split_into_two_parents()
    {
        List<SourceWord> words = [new("ONE SPANNING DOCUMENT HEADING", 350, 740, 18)];
        for (var row = 0; row < 5; row++)
            foreach (var column in new[] { 60, 500, 940 })
                words.Add(new($"source_{column}_{row}_240", column, 680 - row * 26));
        var (text, legacy) = ExtractResult(words);
        Assert.Equal(legacy, text);
        AssertTokens(words, text);
    }

    [Theory]
    [InlineData(double.NaN, 840)]
    [InlineData(1200, double.PositiveInfinity)]
    [InlineData(0, 840)]
    public void Invalid_or_unknown_dimensions_do_not_enable_parent_inference(double width, double height)
    {
        PdfLayoutWord[] words = [new("SOURCE ONE", 50, 100, 740, 730), new("SOURCE TWO", 850, 900, 740, 730)];
        const string fallback = "SOURCE ONE SOURCE TWO";
        var expected = PdfExtractor.BuildLayoutAwareText(words, fallback);
        Assert.Equal(expected, PdfExtractor.BuildLayoutAwareText(words, fallback, width, height));
        Assert.Equal(expected, PdfExtractor.BuildLayoutAwareText(words, fallback, null, null));
    }

    private static List<SourceWord> SpreadWords()
    {
        var words = new List<SourceWord>
        {
            new("ASSEMBLY ALPHA", 250, 740, 16), new("ASSEMBLY BETA", 850, 740, 16)
        };
        for (var row = 0; row < 5; row++)
        {
            var y = 680 - row * 26;
            words.Add(new($"alpha_inventory_{row}_240", 50, y));
            words.Add(new($"alpha_method_{row}_240", 250, y));
            words.Add(new($"alpha_check_{row}_240", 430, y));
            words.Add(new($"beta_inventory_{row}_340", 650, y));
            words.Add(new($"beta_method_{row}_340", 850, y));
            words.Add(new($"beta_check_{row}_340", 1030, y));
        }
        words.Add(new("Do not exceed 240 bar.", 250, 520));
        return words;
    }
    private static string Extract(IReadOnlyList<SourceWord> words, double width = 1200, double height = 840)
        => ExtractResult(words, width, height).Text;
    private static (string Text, string Legacy) ExtractResult(IReadOnlyList<SourceWord> words, double width = 1200, double height = 840)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(width, height);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var word in words)
            page.AddText(word.Text, word.Size * width / 1200, new PdfPoint(word.X * width / 1200, word.Y * height / 840), font);
        var bytes = builder.Build();
        using var source = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var original = source.GetPage(1).GetWords().Select(word =>
        {
            var first = word.Letters.FirstOrDefault(letter => !string.IsNullOrWhiteSpace(letter.Value));
            return new PdfLayoutWord(word.Text, word.BoundingBox.Left, word.BoundingBox.Right,
                word.BoundingBox.Top, word.BoundingBox.Bottom, first?.StartBaseLine.X, first?.StartBaseLine.Y,
                first?.EndBaseLine.X, first?.EndBaseLine.Y);
        }).ToArray();
        var legacy = PdfExtractor.BuildLayoutAwareText(original, source.GetPage(1).Text);
        using var input = new MemoryStream(bytes);
        return (Assert.Single(PdfExtractor.Extract(input, default).Pages).Text, legacy);
    }
    private static void AssertTokens(IEnumerable<SourceWord> words, string actual)
        => Assert.Equal(words.SelectMany(word => Tokens(word.Text)).Order(StringComparer.Ordinal), Tokens(actual).Order(StringComparer.Ordinal));
    private static IEnumerable<string> Tokens(string value) => Regex.Matches(value, @"\S+").Select(match => match.Value);
    private static void AssertBefore(string text, string first, string second, string diagnostics = "")
        => Assert.True(text.IndexOf(first, StringComparison.Ordinal) >= 0
            && text.IndexOf(first, StringComparison.Ordinal) < text.IndexOf(second, StringComparison.Ordinal), diagnostics + "\n" + text);
    private sealed record SourceWord(string Text, double X, double Y, double Size = 10);
}
