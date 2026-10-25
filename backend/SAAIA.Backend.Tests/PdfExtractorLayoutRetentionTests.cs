using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractorLayoutRetentionTests
{
    [Fact]
    public void A_longer_neighboring_body_does_not_disable_observed_stable_columns()
    {
        var words = new List<PdfLayoutWord>();
        for (var line = 0; line < 30; line++)
        {
            words.Add(Word($"right_{line:D2}", 275, 800 - line * 12));
            if (line % 3 == 0 && line <= 12)
                words.Add(Word($"left_{line:D2}", 84, 800 - line * 12));
        }

        var text = Reconstruct(words);

        AssertTokenMultiset(words, text);
        Assert.True(text.IndexOf("left_12", StringComparison.Ordinal)
            < text.IndexOf("right_00", StringComparison.Ordinal), text);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-4)]
    public void Native_inclined_text_preserves_complete_sentence_order(double angleDegrees)
    {
        string[] lines =
        [
            "Always isolate the supply before opening the safety enclosure.",
            "Never operate the machine while the access panel remains open.",
            "Record the measured pressure before restarting the equipment."
        ];
        var path = Path.Combine(Path.GetTempPath(), $"saaia-native-inclined-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BuildInclinedPdf(lines, angleDegrees));

            var page = Assert.Single(PdfExtractor.Extract(path).Pages);
            var text = Regex.Replace(page.Text, @"\s+", " ").Trim();

            foreach (var line in lines)
                Assert.Contains(line, text, StringComparison.Ordinal);
            Assert.True(text.IndexOf(lines[0], StringComparison.Ordinal)
                < text.IndexOf(lines[1], StringComparison.Ordinal), text);
            Assert.True(text.IndexOf(lines[1], StringComparison.Ordinal)
                < text.IndexOf(lines[2], StringComparison.Ordinal), text);
            Assert.Equal(lines.SelectMany(Tokens).Order(StringComparer.Ordinal),
                Tokens(text).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Complete_native_word_segmentation_preserves_a_fragmented_negative_instruction()
    {
        var fragments = FragmentedInstruction();
        PdfLayoutWord[] assembled = [Word("Not", 10, 800), Word("allowed", 50, 800)];

        var selected = PdfExtractor.SelectCompleteLayoutWords(fragments, assembled, "Not allowed");
        var text = Reconstruct(selected);

        Assert.Equal(new[] { "Not", "allowed" }, selected.Select(word => word.Text));
        Assert.Equal("Not allowed", text);
        AssertTokenMultiset(assembled, text);
    }

    [Theory]
    [InlineData("not", "allowed")]
    [InlineData("Not", "allowedd")]
    [InlineData("Not", "allowe")]
    public void A_word_segmenter_may_not_change_case_add_or_remove_source_characters(string first, string second)
    {
        var fragments = FragmentedInstruction();

        var selected = PdfExtractor.SelectCompleteLayoutWords(fragments,
            [Word(first, 10, 800), Word(second, 50, 800)], "Not allowed");

        Assert.Equal(fragments, selected);
    }

    [Fact]
    public void A_word_segmenter_may_not_drop_a_digit_or_accept_incomplete_geometry()
    {
        PdfLayoutWord[] source = [Word("N", 10, 800), Word("o", 20, 800), Word("t", 30, 800), Word("240", 50, 800)];

        Assert.Equal(source, PdfExtractor.SelectCompleteLayoutWords(source,
            [Word("Not", 10, 800), Word("24", 50, 800)], "Not 240"));
        Assert.Equal(source, PdfExtractor.SelectCompleteLayoutWords(source,
            [Word("Not", double.NaN, 800), Word("240", 50, 800)], "Not 240"));
    }

    [Fact]
    public void Two_word_segmenters_omitting_the_same_source_fact_must_keep_the_complete_raw_source()
    {
        const string raw = "Not allowed Maximum pressure 240 bar";
        var incomplete = FragmentedInstruction();
        PdfLayoutWord[] alternative = [Word("Not", 10, 800), Word("allowed", 50, 800)];

        var selected = PdfExtractor.SelectCompleteLayoutWords(incomplete, alternative, raw);
        var text = PdfExtractor.BuildLayoutAwareText(selected, raw);

        Assert.Equal(incomplete, selected);
        Assert.Equal(raw, text);
    }

    [Fact]
    public void Layout_preserves_a_sparse_source_region_beside_two_stable_columns()
    {
        PdfLayoutWord[] words =
        [
            Word("left_first", 10, 800), Word("right_first", 260, 800),
            Word("left_second", 10, 780), Word("right_second", 260, 780),
            Word("left_last", 10, 760), Word("right_last", 260, 760),
            Word("CriticalLimit", 600, 740)
        ];

        var text = Reconstruct(words);

        AssertTokenMultiset(words, text);
        Assert.Contains("CriticalLimit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_preserves_a_sparse_prefix_region_inside_nested_columns()
    {
        var words = new List<PdfLayoutWord>();
        AddNestedBlock(words, 0, includeSparsePrefix: true);
        AddNestedBlock(words, 700, includeSparsePrefix: false);

        var text = Reconstruct(words);

        AssertTokenMultiset(words, text);
        Assert.Contains("SAFETY_LIMIT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spanning_heading_does_not_interleave_the_body_columns()
    {
        PdfLayoutWord[] words =
        [
            Word("SPANNING_HEADING", 10, 830, 430),
            Word("left_first", 10, 800), Word("right_first", 260, 800),
            Word("left_second", 10, 780), Word("right_second", 260, 780),
            Word("left_last", 10, 760), Word("right_last", 260, 760)
        ];

        var text = Reconstruct(words);

        AssertTokenMultiset(words, text);
        Assert.True(text.IndexOf("left_last", StringComparison.Ordinal)
            < text.IndexOf("right_first", StringComparison.Ordinal), text);
        Assert.True(text.IndexOf("SPANNING_HEADING", StringComparison.Ordinal)
            < text.IndexOf("left_first", StringComparison.Ordinal), text);
    }

    [Fact]
    public void Incomplete_geometry_does_not_discard_a_source_fact_from_the_fallback()
    {
        PdfLayoutWord[] words =
        [
            Word("First", 10, 800), Word("complete", 50, 800), Word("instruction.", 100, 800),
            Word("Second", 10, 780), Word("complete", 60, 780), Word("instruction.", 110, 780),
            Word("CriticalLimit", double.NaN, 760)
        ];
        var fallback = string.Join(' ', words.Select(word => word.Text));

        var text = PdfExtractor.BuildLayoutAwareText(words, fallback);

        AssertTokenMultiset(words, text);
    }

    private static void AddNestedBlock(List<PdfLayoutWord> words, double offset, bool includeSparsePrefix)
    {
        foreach (var top in new[] { 800d, 785d })
        {
            words.Add(Word("LEFT", offset, top, 70));
            words.Add(Word("HEADING", offset + 76, top, 74));
            words.Add(Word("RIGHT", offset + 230, top, 70));
            words.Add(Word("HEADING", offset + 306, top, 74));
        }
        if (includeSparsePrefix)
            words.Add(Word("SAFETY_LIMIT", offset + 120, 770, 60));

        foreach (var top in new[] { 750d, 730d, 710d })
        {
            foreach (var column in new[] { 0d, 230d })
            {
                words.Add(Word("useful", offset + column, top, 34));
                words.Add(Word("source", offset + column + 40, top, 34));
                words.Add(Word("fact.", offset + column + 80, top, 34));
            }
        }
    }

    private static string Reconstruct(IReadOnlyList<PdfLayoutWord> words)
        => PdfExtractor.BuildLayoutAwareText(words, string.Join(' ', words.Select(word => word.Text)));

    private static void AssertTokenMultiset(IEnumerable<PdfLayoutWord> words, string actual)
        => Assert.Equal(words.SelectMany(word => Tokens(word.Text)).Order(StringComparer.Ordinal),
            Tokens(actual).Order(StringComparer.Ordinal));

    private static IEnumerable<string> Tokens(string text)
        => Regex.Matches(text, @"\S+").Select(match => match.Value);

    private static PdfLayoutWord[] FragmentedInstruction()
        => "Notallowed".Select((character, index) => Word(character.ToString(), 10 + index * 10, 800, 8)).ToArray();

    private static PdfLayoutWord Word(string text, double left, double top, double width = 28)
        => new(text, left, left + width, top, top - 10);

    private static byte[] BuildInclinedPdf(IReadOnlyList<string> lines, double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180d;
        var content = new StringBuilder("BT\n/F1 10 Tf\n");
        for (var index = 0; index < lines.Count; index++)
        {
            content.AppendFormat(CultureInfo.InvariantCulture,
                "{0:R} {1:R} {2:R} {3:R} 70 {4} Tm\n({5}) Tj\n",
                Math.Cos(angle), Math.Sin(angle), -Math.Sin(angle), Math.Cos(angle),
                650 - index * 24, lines[index]);
        }
        content.Append("ET\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content.ToString())} >>\nstream\n{content}endstream"
        ];
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
