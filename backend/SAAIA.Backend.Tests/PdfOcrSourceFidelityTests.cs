using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfOcrSourceFidelityTests
{
    [Fact]
    public void Partial_image_retry_cannot_erase_pages_already_recovered_by_full_ocr()
    {
        var native = Extraction("", "", "");
        var full = Extraction("First recovered source page.", "Second recovered source page.", "Third recovered source page.");
        var basis = IngestionWorker.ResolveImageOcrMergeBase(native, full, fullOcrApplied: true);
        var retry = PdfOcrTextExtractor.MergeImageOcrText(basis,
            new Dictionary<int, string> { [1] = "First recovered source page.\nPanel status READY" }, "eng", 3);

        Assert.NotNull(retry);
        Assert.True(PdfOcrTextExtractor.ShouldApplyOcrExtraction(basis, retry!, false, false));
        Assert.Equal(full.Pages[1].Text, retry!.Pages[1].Text);
        Assert.Equal(full.Pages[2].Text, retry.Pages[2].Text);
    }

    [Theory]
    [InlineData("Maximum pressure 10 bar during normal operation.", "Maximum pressure 20 bar during normal operation.")]
    [InlineData("The pump may operate during scheduled maintenance windows.", "The pump may not operate during scheduled maintenance windows.")]
    [InlineData("The calibration offset is +5 during scheduled maintenance windows.", "The calibration offset is -5 during scheduled maintenance windows.")]
    [InlineData("The calibration factor is 1.5 during scheduled maintenance windows.", "The calibration factor is 15 during scheduled maintenance windows.")]
    [InlineData("Sensor readings 1 5 are recorded.", "Sensor readings 15 are recorded.")]
    [InlineData("The equipment is not able to operate.", "The equipment is notable to operate.")]
    [InlineData("Installation status and electrical characteristics.", "Prohibited")]
    [InlineData("Installation status and electrical characteristics.", "Approved")]
    [InlineData("Installation status and electrical characteristics.", "5 bar")]
    [InlineData("Installation status and electrical characteristics.", "24 V")]
    [InlineData("Installation status and electrical characteristics.", "Interdit")]
    public void New_ocr_fact_survives_similarity_or_short_length(string nativeText, string observation)
    {
        var result = PdfOcrTextExtractor.MergeImageOcrText(Extraction(nativeText),
            new Dictionary<int, string> { [1] = observation }, "fra+eng", 3);
        Assert.NotNull(result);
        Assert.Contains(nativeText, result!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains(observation, result.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Low_confidence_word_does_not_delete_the_other_source_words_or_value()
    {
        const string text = "Disconnect power before maintenance at 5 bar";
        var words = text.Split(' ');
        var tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + string.Join('\n', words.Select((word, index) =>
                $"5\t1\t1\t1\t1\t{index + 1}\t{index * 10}\t0\t10\t10\t{(word == "maintenance" ? 20 : 99)}\t{word}"));
        Assert.Equal(text, PdfOcrTextExtractor.FilterLowConfidenceImageOcrText(text, tsv));
    }

    [Fact]
    public void Native_raw_text_and_encoding_signals_survive_an_ocr_addition()
    {
        const string raw = "Source raw bytes decoded before normalization.";
        var native = Extraction("Native source text about maintenance.");
        native = native with { Pages = [native.Pages[0] with { RawText = raw }] };
        var result = PdfOcrTextExtractor.MergeImageOcrText(native,
            new Dictionary<int, string> { [1] = "Panel status READY" }, "eng", 3);
        Assert.NotNull(result);
        Assert.Equal(raw, result!.Pages[0].RawText);
    }

    [Fact]
    public void Failed_attempt_is_not_complete_successful_coverage()
    {
        var plan = new PdfImagePageOcrPlan(1, 1, 0, 1, [1], [1], []);
        var diagnostics = PdfOcrTextExtractor.BuildImagePageOcrDiagnostics(plan,
            new Dictionary<int, string>(), Extraction(""), null,
            new Dictionary<int, PdfImagePageOcrDiagnostic>
            {
                [1] = new(1, "render_failed", "timeout", TimedOut: true)
            });
        Assert.Equal("partial_error", diagnostics.CoverageStatus);
        Assert.True(diagnostics.TimedOut);
        Assert.Equal("page_failures", diagnostics.FailureReason);
    }

    [Fact]
    public void Sidecar_keeps_trailing_blank_physical_page_besides_the_terminal_separator()
    {
        var result = PdfOcrTextExtractor.BuildExtractionFromSidecarText("First page source.\fSecond page source.\f\f");
        Assert.Equal(3, result.Pages.Count);
        Assert.Empty(result.Pages[2].Text);
    }

    [Fact]
    public void Sidecar_quality_examines_remaining_encoding_damage()
    {
        var result = PdfOcrTextExtractor.BuildExtractionFromSidecarText(
            string.Join(' ', Enumerable.Repeat("Source\uFFFDword", 40)));
        Assert.Contains("replacement_chars_remaining", result.Pages[0].Quality!.Signals);
        Assert.True(result.Pages[0].Quality!.OcrCandidate);
    }

    [Fact]
    public void More_ocr_words_do_not_authorize_losing_a_native_page_or_fact()
    {
        var native = Extraction("Never bypass the safety interlock.", "Physical page two remains here.");
        var candidate = Extraction(string.Join(' ', Enumerable.Repeat("Additional recognized source text", 20)));
        Assert.False(PdfOcrTextExtractor.ShouldApplyOcrExtraction(native, candidate, true, false));
    }

    [Theory]
    [InlineData("TEC 123 is the product identifier.")]
    [InlineData("IEG 123 is the supplier reference.")]
    [InlineData("NEPA 70 is the literal source code.")]
    [InlineData("DOF-HDBK is the exact identifier.")]
    [InlineData("The configuration key is shortcircuit.")]
    [InlineData("The literal French source says recommende.")]
    public void Storage_normalization_never_guesses_lexical_identities(string text)
        => Assert.Equal(text, PdfTextSanitizer.ForStorage(text));

    [Theory]
    [InlineData("x y z")]
    [InlineData("p q are independent variables")]
    public void Native_variable_labels_are_not_deleted_as_letter_noise(string text)
        => Assert.Equal(text, OcrNoiseFilter.RemoveSpacedLetterRunNoise(text));

    private static PdfExtractionResult Extraction(params string[] texts)
    {
        var pages = texts.Select((text, index) => new ExtractedPdfPage(index + 1, text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length,
            SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            Quality: PdfPageExtractionQuality.FromText(text,
                text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length),
            ImageCount: 1, RawText: text)).ToList();
        var tokens = pages.SelectMany(page => page.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new WordToken(word, page.PageNumber))).ToList();
        return new(tokens, pages, PdfExtractionQualitySummary.FromPages(pages));
    }
}
