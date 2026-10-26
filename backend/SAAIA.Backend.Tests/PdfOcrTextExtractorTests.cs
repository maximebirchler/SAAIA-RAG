using System.Diagnostics;
using System.Globalization;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfOcrTextExtractorTests
{
    [Fact]
    public void BuildExtractionFromSidecarText_builds_pages_tokens_and_ocr_signals()
    {
        var result = PdfOcrTextExtractor.BuildExtractionFromSidecarText(
            "First OCR page mentions inerting controls.\fSecond OCR page mentions weekly menus and sauces.\f");

        Assert.Equal("ocr_sidecar", result.Source);
        Assert.Equal(2, result.Pages.Count);
        Assert.Contains(result.Tokens, token => string.Equals(token.Word, "inerting", StringComparison.OrdinalIgnoreCase) && token.Page == 1);
        Assert.Contains(result.Tokens, token => string.Equals(token.Word, "sauces.", StringComparison.OrdinalIgnoreCase) && token.Page == 2);
        Assert.Contains("ocr_text_extracted", result.Quality.Signals);
        Assert.All(result.Pages, page => Assert.Contains("ocr_text_extracted", page.Quality!.Signals));
    }

    [OcrEngineFact]
    [Trait("Category", "OcrE2E")]
    public async Task Real_scanned_pdf_ocr_extracts_text_when_opted_in()
    {
        var ocrCommand = Environment.GetEnvironmentVariable("SAAIA_TEST_OCR_COMMAND") ?? "ocrmypdf";
        var rendererCommand = Environment.GetEnvironmentVariable("SAAIA_TEST_OCR_IMAGE_RENDERER_COMMAND") ?? "gs";
        var textCommand = Environment.GetEnvironmentVariable("SAAIA_TEST_OCR_IMAGE_TEXT_COMMAND") ?? "tesseract";
        var languages = Environment.GetEnvironmentVariable("SAAIA_TEST_OCR_LANGUAGES") ?? "eng";

        await AssertCommandAvailableAsync(ocrCommand, "--version");
        await AssertCommandAvailableAsync(rendererCommand, "-version");
        await AssertCommandAvailableAsync(textCommand, "--version");

        var tempDir = Path.Combine(Path.GetTempPath(), "saaia-ocr-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pdfPath = Path.Combine(tempDir, "scanned-smoke.pdf");
        try
        {
            WriteRasterTextPdf(pdfPath);

            var native = PdfExtractor.Extract(pdfPath);
            Assert.Single(native.Pages);
            Assert.Empty(native.Tokens);
            Assert.True(native.Pages[0].ImageCount > 0);
            Assert.True(native.Quality.OcrRecommended);

            var options = new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = ocrCommand,
                OcrLanguages = languages,
                OcrTimeoutSeconds = 180,
                OcrMinWords = 3,
                OcrImageRendererCommand = rendererCommand,
                OcrImageTextCommand = textCommand,
                OcrImagePageEnabled = true,
                OcrImagePageMaxPages = 1,
                OcrImagePageRenderDpi = 260,
                OcrImagePageSegmentationMode = 6,
                OcrImagePageTimeoutSeconds = 120,
                OcrImagePageMaxTotalSeconds = 180,
                OcrImagePageMinWords = 2
            };

            var full = await PdfOcrTextExtractor.TryExtractWithDiagnosticsAsync(
                pdfPath,
                options,
                CancellationToken.None,
                languagesOverride: languages);

            Assert.True(full is not null, "The explicitly enabled full-document OCR operation returned no result.");
            Assert.True(full!.Extraction is not null, DescribeOcrFailure("full-document", full.Diagnostics));
            Assert.Equal(0, full.Diagnostics.ExitCode);
            Assert.False(full.Diagnostics.TimedOut);
            Assert.Null(full.Diagnostics.FailureReason);
            AssertOcrTokens(full.Extraction!);

            var imagePage = await PdfOcrTextExtractor.TryMergeImagePageOcrAsync(
                pdfPath,
                options,
                native,
                CancellationToken.None,
                languagesOverride: languages);

            Assert.True(imagePage is not null, "The explicitly enabled image-page OCR operation returned no result.");
            Assert.True(imagePage!.Extraction is not null, DescribeOcrFailure("image-page", imagePage.Diagnostics));
            Assert.Contains(1, imagePage.Diagnostics.AttemptedPages);
            Assert.Contains(1, imagePage.Diagnostics.PagesWithOcrText);
            AssertOcrTokens(imagePage.Extraction!);

            var sections = DocumentSectionExtractor.Extract(imagePage.Extraction!.Pages);
            var units = DocumentUnitExtractor.Extract(imagePage.Extraction.Pages, sections);
            var chunks = RetrievalChunkProjector.ProjectStructureAware(sections, units, 220, 35, 25);
            var combined = string.Join(' ', units.Select(static unit => unit.Text).Concat(chunks.Select(static chunk => chunk.Text)));

            Assert.Contains("PUMP", combined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RESET", combined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Raster_ocr_fixture_is_hash_pinned_and_contains_only_a_scanned_page()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"saaia-ocr-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var path = Path.Combine(temporary, "scanned-smoke.pdf");
            WriteRasterTextPdf(path);
            var native = PdfExtractor.Extract(path);
            Assert.Single(native.Pages);
            Assert.Empty(native.Tokens);
            Assert.Equal(1, native.Pages[0].ImageCount);
            Assert.True(native.Quality.OcrRecommended);
        }
        finally { TryDeleteDirectory(temporary); }
    }

    [Fact]
    public void Ocr_fixture_oracle_rejects_a_corrupted_identifier_even_when_all_other_operational_words_are_present()
    {
        AssertOcrTokens(PdfOcrTextExtractor.BuildExtractionFromSidecarText("PUMP RESET PANEL BATCH CODE XQ42"));
        var wrongIdentifier = PdfOcrTextExtractor.BuildExtractionFromSidecarText("PUMP RESET PANEL BATCH CODE X042");
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertOcrTokens(wrongIdentifier));
        Assert.Contains("xq42", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryExtractWithDiagnosticsAsync_reports_full_document_sidecar_missing()
    {
        var result = await PdfOcrTextExtractor.TryExtractWithDiagnosticsAsync(
            Path.Combine(Path.GetTempPath(), "saaia-ocr-diagnostics-missing.pdf"),
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = ResolveDotNetCommand(),
                OcrArguments = "--info",
                OcrTimeoutSeconds = 30,
                OcrMinWords = 1
            },
            CancellationToken.None,
            languagesOverride: "eng");

        Assert.NotNull(result);
        Assert.Null(result!.Extraction);
        Assert.Equal("full_document", result.Diagnostics.Mode);
        Assert.Equal(0, result.Diagnostics.ExitCode);
        Assert.False(result.Diagnostics.TimedOut);
        Assert.Equal(30, result.Diagnostics.TimeoutSeconds);
        Assert.Equal("sidecar_missing", result.Diagnostics.FailureReason);
    }

    [Fact]
    public void TruncateOcrProcessOutputForDiagnostics_sanitizes_and_caps_stderr()
    {
        var truncated = PdfOcrTextExtractor.TruncateOcrProcessOutputForDiagnostics(
            new string('x', 5000) + "\0tail");

        Assert.NotNull(truncated);
        Assert.True(truncated!.Length <= 4096);
        Assert.EndsWith("[truncated]", truncated);
        Assert.DoesNotContain("\0", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeImageOcrText_adds_only_new_ocr_lines_to_native_page_text()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Pump speed table\nExisting native safety text",
            7,
            44,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Pump", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Pump speed table\nPHOTO WARNING PANEL\nKeep hands clear"
            },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Equal("pdf_text_plus_image_ocr", merged!.Source);
        Assert.Equal("eng", merged.OcrLanguages);
        Assert.Contains("PHOTO WARNING PANEL", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Keep hands clear", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(merged.Pages[0].Text, "Pump speed table"));
        Assert.Contains("image_ocr_text_extracted", merged.Quality.Signals);
    }

    [Fact]
    public void MergeImageOcrText_preserves_native_and_recovered_observations_when_ocr_is_cleaner()
    {
        var nativeText = "Configuration and protec\uFFFDion requirements\nDocument owner approval required";
        var nativeWords = nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            nativeWords.Length,
            nativeText.Length,
            [1],
            ImageCount: 0);
        var native = new PdfExtractionResult(
            [new WordToken("Configuration", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Configuration and protection requirements\nDocument owner approval required"
            },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Equal("pdf_text_plus_image_ocr", merged!.Source);
        Assert.Contains(nativeText, merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Configuration and protection requirements", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(nativeText, merged.Pages[0].RawText);
        Assert.DoesNotContain("image_ocr_replaced_corrupt_text", merged.Pages[0].Quality!.Signals);
        Assert.Contains("image_ocr_text_extracted", merged.Quality.Signals);
    }

    [Fact]
    public void BuildImagePageOcrPlan_uses_repaired_replacement_signal_even_when_text_is_now_clean()
    {
        const string text = "Configuration and protection requirements";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var quality = PdfPageExtractionQuality.FromSanitizedText(
            text,
            words.Length,
            text.Length,
            rawReplacementCharCount: 1,
            sanitizedReplacementCharCount: 0);
        var nativePage = new ExtractedPdfPage(
            1,
            text,
            words.Length,
            text.Length,
            [1],
            quality,
            ImageCount: 0);
        var native = new PdfExtractionResult(
            words.Select(word => new WordToken(word, 1)).ToList(),
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageEnabled = true });

        Assert.Contains("replacement_chars_detected", quality.Signals);
        Assert.Contains("replacement_chars_repaired", quality.Signals);
        Assert.True(quality.EncodingRepairApplied);
        Assert.Equal([1], plan.CandidatePages);
        Assert.Equal([1], plan.AttemptedPages);
    }

    [Fact]
    public void BuildImagePageOcrPlan_can_render_empty_pages_even_without_detected_images()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "",
            0,
            0,
            [1],
            PdfPageExtractionQuality.FromText("", 0, 0),
            ImageCount: 0);
        var native = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageEnabled = true });

        Assert.Equal([1], plan.CandidatePages);
        Assert.Equal([1], plan.AttemptedPages);
    }

    [Fact]
    public void MergeImageOcrText_does_not_replace_corrupt_native_text_with_partial_ocr()
    {
        var nativeText = "Configuration and protec\uFFFDion requirements remain active. The document owner, revision table, approval workflow and deployment notes stay available.";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            17,
            nativeText.Length,
            [1],
            ImageCount: 0);
        var native = new PdfExtractionResult(
            [new WordToken("Configuration", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Configuration and protection requirements"
            },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Contains('\uFFFD', merged!.Pages[0].Text);
        Assert.Contains("Configuration and protection requirements", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("image_ocr_replaced_corrupt_text", merged.Pages[0].Quality!.Signals);
    }

    [Fact]
    public void MergeImageOcrText_keeps_native_observation_beside_a_structured_ocr_variant()
    {
        const string nativeText =
            "Control module procedure Start the pump 10 bar pressure 2 kg load Confirm valve position Components pressure gauge pump valve safety label release handle Inspection notes complete.";
        const string ocrText = """
Control module procedure
Start the pump
Confirm valve position
Inspection notes complete
Components
10 bar pressure
2 kg load
pressure gauge
pump valve
safety label
release handle
""";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            nativeText.Length,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Control", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string> { [1] = ocrText },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.StartsWith(nativeText, merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("10 bar pressure", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(ocrText, Assert.Single(merged.Pages[0].OcrObservations!).RawText);
        Assert.DoesNotContain("image_ocr_replaced_layout_text", merged.Pages[0].Quality!.Signals);
    }

    [Fact]
    public void MergeImageOcrText_keeps_native_text_when_structured_ocr_has_weak_coverage()
    {
        const string nativeText =
            "Control module procedure Start the pump 10 bar pressure 2 kg load Confirm valve position Components pressure gauge pump valve safety label release handle Inspection notes complete.";
        const string ocrText = """
Control module procedure
Start the pump
Unrelated panel note
Different alarm label
""";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            nativeText.Length,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Control", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string> { [1] = ocrText },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.StartsWith(nativeText, merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("image_ocr_replaced_layout_text", merged.Pages[0].Quality!.Signals);
    }

    [Fact]
    public void MergeImageOcrText_keeps_short_important_image_ocr_lines_below_word_threshold()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Native technical text",
            3,
            21,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "EN 15281\nANSI Z535.4\nIND570\nSTOP\nRESET"
            },
            "eng",
            minWords: 4);

        Assert.NotNull(merged);
        Assert.Contains("EN 15281", merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("ANSI Z535.4", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("IND570", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("STOP", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("RESET", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("image_ocr_text_extracted", merged.Quality.Signals);
    }

    [Fact]
    public void MergeImageOcrText_keeps_short_title_like_image_ocr_lines_below_word_threshold()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "M I X I N G V A L V E Overview Components Process mode Categories For 2 sections",
            20,
            81,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "MIXING VALVE\uFFFD\n\nFor 2 sections\n\n22"
            },
            "eng",
            minWords: 4);

        Assert.NotNull(merged);
        Assert.Contains("MIXING VALVE", merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("image_ocr_text_extracted", merged.Quality.Signals);
    }

    [Fact]
    public void MergeImageOcrText_keeps_short_observations_without_a_language_whitelist()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Native technical text",
            3,
            21,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "|\n7\nAB\nNOLYNE\n--"
            },
            "eng",
            minWords: 4);

        Assert.NotNull(merged);
        Assert.NotEmpty(merged!.Pages[0].OcrObservations!);
    }

    [Fact]
    public void MergeImageOcrText_keeps_word_boundary_differences_as_separate_observations()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Ingredient constraints and preparation timing\nStorage notes and kitchen workflow",
            8,
            76,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Ingredient", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Ingredientconstraints and preparation timing\nIMAGE LABEL KEEP COLD"
            },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Contains("Ingredientconstraints", merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("IMAGE LABEL KEEP COLD", merged.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeImageOcrText_keeps_distinct_recognition_variants_without_guessing_equivalence()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Motor protection relay configuration requirements\nSet the current threshold and confirm the reset procedure.",
            13,
            102,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Motor", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Motor protectlon relay configuration requlrements\nSet the current threshold and confirm the reset procedure\nPHOTO LABEL SERVICE MODE"
            },
            "eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Contains("protectlon", merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("requlrements", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("reset procedure", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("PHOTO LABEL SERVICE MODE", merged.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeImageOcrText_keeps_differences_in_quantities_letters_and_units_as_observations()
    {
        var nativeText = "1 branche de celeri\n2 l de bouillon de volaille\n1 c. a soupe de graisse\n1 pincee de noix de muscade";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            nativeText.Length,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("branche", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "I branche de celeri\n2 1 de bouillon de volaille\nIc. a soupe de graisse\nIpincee de noix de muscade\nVISIBLE PANEL LABEL"
            },
            "fra+eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.Contains("I branche", merged!.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("2 1 de bouillon", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Ic. a soupe", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Ipincee", merged.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("VISIBLE PANEL LABEL", merged.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterLowConfidenceImageOcrText_retains_lines_and_exposes_confidence_separately()
    {
        const string cleanedText = """
Pour 4 perronney
Comme tous les plats mijotes
VISIBLE PANEL LABEL
""";
        const string tsvText = """
level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
5	1	1	1	1	1	0	0	10	10	67.5	Pour
5	1	1	1	1	2	0	0	10	10	82.0	4
5	1	1	1	1	3	0	0	10	10	33.5	perronney
5	1	1	1	2	1	0	0	10	10	94.0	Comme
5	1	1	1	2	2	0	0	10	10	92.0	tous
5	1	1	1	2	3	0	0	10	10	93.0	les
5	1	1	1	2	4	0	0	10	10	91.0	plats
5	1	1	1	2	5	0	0	10	10	90.0	mijotes
5	1	1	1	3	1	0	0	10	10	96.0	VISIBLE
5	1	1	1	3	2	0	0	10	10	96.0	PANEL
5	1	1	1	3	3	0	0	10	10	96.0	LABEL
""";

        var filtered = PdfOcrTextExtractor.FilterLowConfidenceImageOcrText(cleanedText, tsvText);

        Assert.Equal(cleanedText, filtered);
        Assert.True(OcrObservationParser.Parse(cleanedText, tsvText, "tesseract", "fra").HasLowConfidenceWords);
        Assert.Contains("Comme tous les plats", filtered, StringComparison.Ordinal);
        Assert.Contains("VISIBLE PANEL LABEL", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeImageOcrText_preserves_distinct_structured_recognition_observations()
    {
        var nativeText = "Pour 4 personnes\n1 kg de pommes de terre\n100 g de beurre\nPreparation : pelez et coupez les pommes de terre, puis servez chaud.";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            nativeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            nativeText.Length,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Pour", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "Pour 4 perronney Preparation e 1kg de pommes de terre e\n100 g de beurre Preparation : pelez et coupez les pommes de terre"
            },
            "fra+eng",
            minWords: 3);

        Assert.NotNull(merged);
        Assert.NotEmpty(merged!.Pages[0].OcrObservations!);
    }

    [Fact]
    public void MergeImageOcrText_retains_probable_noise_in_the_source_observation()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Native technical text",
            3,
            21,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var merged = PdfOcrTextExtractor.MergeImageOcrText(
            native,
            new Dictionary<int, string>
            {
                [1] = "sjueynsuog NOLYNE dnosb SSLL} AB12 CD34\nPHOTO WARNING PANEL Keep hands clear"
            },
            "eng",
            minWords: 4);

        Assert.NotNull(merged);
        Assert.Contains("sjueynsuog", Assert.Single(merged!.Pages[0].OcrObservations!).RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PHOTO WARNING PANEL Keep hands clear", merged.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldForceOcrNativeText_detects_glued_native_text_layer()
    {
        var gluedText = string.Concat(Enumerable.Repeat("SupplierCodeofConductDeliveringBetterPublicServicesTogether", 12));
        var page = new ExtractedPdfPage(
            1,
            gluedText,
            1,
            gluedText.Length,
            [1]);
        var native = new PdfExtractionResult(
            [new WordToken(gluedText, 1)],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.True(PdfOcrTextExtractor.ShouldForceOcrNativeText(native));
    }

    [Fact]
    public void ShouldForceOcrNativeText_keeps_normal_native_text_on_skip_text_path()
    {
        var text = "Supplier Code of Conduct delivering better public services together with transparent governance.";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var page = new ExtractedPdfPage(
            1,
            text,
            words.Length,
            text.Length,
            [1]);
        var native = new PdfExtractionResult(
            words.Select(word => new WordToken(word, 1)).ToList(),
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.False(PdfOcrTextExtractor.ShouldForceOcrNativeText(native));
    }

    [Fact]
    public void ShouldForceOcrNativeText_detects_invalid_c1_font_mapping()
    {
        var text =
            "Ô\u008e±ºº®» ¼\u008e«²» °®±¼«½¬·ª·¬7 ¿½½®«» Ü»­ ­±´«¬·±²­ ¼\u008e¿«¬±³¿¬·±²";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var page = new ExtractedPdfPage(
            1,
            text,
            words.Length,
            text.Length,
            [1],
            PdfPageExtractionQuality.FromText(
                text,
                words.Length,
                text.Length));
        var native = new PdfExtractionResult(
            words.Select(word => new WordToken(word, 1)).ToList(),
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.True(PdfOcrTextExtractor.ShouldForceOcrNativeText(native));
    }

    [Fact]
    public void ShouldApplyOcrExtraction_rejects_force_ocr_that_loses_most_native_source_text()
    {
        var firstGluedToken = string.Concat(Enumerable.Repeat("SupplierCodeOfConductDeliveringBetterPublicServicesTogether", 3));
        var secondGluedToken = string.Concat(Enumerable.Repeat("TransparentGovernanceAndAccessibleProcurementRequirements", 3));
        var gluedText = firstGluedToken + " " + secondGluedToken;
        var nativePage = new ExtractedPdfPage(
            1,
            gluedText,
            2,
            gluedText.Length,
            [1]);
        var native = new PdfExtractionResult(
            [new WordToken(firstGluedToken, 1), new WordToken(secondGluedToken, 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var ocrText = "Supplier Code";
        var ocrPage = new ExtractedPdfPage(
            1,
            ocrText,
            2,
            ocrText.Length,
            [1]);
        var ocr = new PdfExtractionResult(
            [new WordToken("Supplier", 1), new WordToken("Code", 1)],
            [ocrPage],
            PdfExtractionQualitySummary.FromPages([ocrPage]),
            Source: "ocr_sidecar_force",
            OcrLanguages: "eng");

        Assert.True(PdfOcrTextExtractor.ShouldForceOcrNativeText(native));
        Assert.False(PdfOcrTextExtractor.ShouldApplyOcrExtraction(
            native,
            ocr,
            fullDocumentOcrRecommended: true,
            forceFullDocumentOcr: true));
    }

    [Fact]
    public void ResolveLanguagesForDocument_uses_document_language_when_confident()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "fra+eng+deu+spa+por+ita",
            OcrAutoDetectLanguages = true
        };
        var native = new PdfExtractionResult(
            [],
            [
                new ExtractedPdfPage(
                    1,
                    "Berechnung von Behaltern und Apparaten aus Thermoplasten und Flanschverbindungen.",
                    9,
                    82,
                    [1])
            ],
            PdfExtractionQualitySummary.FromPages([
                new ExtractedPdfPage(
                    1,
                    "Berechnung von Behaltern und Apparaten aus Thermoplasten und Flanschverbindungen.",
                    9,
                    82,
                    [1])
            ]));

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Standards/DVS 2205 Berechnung von Behaltern.pdf",
            options,
            native);

        Assert.Equal("deu+fra+eng+spa+por+ita", languages);
    }

    [Theory]
    [InlineData("nld+eng+fra", "De handleiding beschrijft onderhoud en veiligheidscontroles voor de installatie.", "nld+eng+fra")]
    [InlineData("ara+eng+fra", "يشرح هذا المستند اجراءات السلامة والصيانة للمعدات.", "ara+eng+fra")]
    [InlineData("chi_sim+eng+fra", "本文件介绍安全联锁状态和维护要求。", "chi_sim+eng+fra")]
    [InlineData("tur+eng+fra", "Bu belge guvenlik gereksinimleri ve bakim adimlarini aciklar.", "tur+eng+fra")]
    public void ResolveLanguagesForDocument_uses_non_ui_document_language_when_confident(
        string configuredLanguages,
        string text,
        string expectedLanguages)
    {
        var options = new IngestionOptions
        {
            OcrLanguages = configuredLanguages,
            OcrAutoDetectLanguages = true
        };
        var page = new ExtractedPdfPage(
            1,
            text,
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [1]);
        var native = new PdfExtractionResult(
            [],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Universal/Document.pdf",
            options,
            native);

        Assert.Equal(expectedLanguages, languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_falls_back_to_configured_languages_when_uncertain()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "fra+eng+deu",
            OcrAutoDetectLanguages = true
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Mixed/Unknown.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("fra+eng+deu", languages);
    }

    [Fact]
    public void ResolveAutoConfiguredOcrLanguages_keeps_auto_fallback_bounded_when_many_languages_are_installed()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "auto",
            OcrAutoFallbackLanguages = "fra+eng+deu+ita",
            OcrMaxLanguages = 4
        };

        var languages = PdfOcrTextExtractor.ResolveAutoConfiguredOcrLanguages(
            options,
            ["eng", "fra", "deu", "ita", "spa", "por", "nld", "ara", "chi_sim"]);

        Assert.Equal(["fra", "eng", "deu", "ita"], languages);
    }

    [Fact]
    public void ResolveAutoConfiguredOcrLanguages_can_use_all_installed_languages_when_explicitly_requested()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "all",
            OcrAutoFallbackLanguages = "fra+eng+deu+ita",
            OcrMaxLanguages = 6
        };

        var languages = PdfOcrTextExtractor.ResolveAutoConfiguredOcrLanguages(
            options,
            ["eng", "fra", "deu", "ita", "nld", "ara", "chi_sim"]);

        Assert.Equal(["eng", "fra", "deu", "ita", "nld", "ara"], languages);
    }

    [Fact]
    public void ResolveDetectedAutoOcrLanguages_prioritizes_detected_language_without_discarding_other_candidates()
    {
        var languages = PdfOcrTextExtractor.ResolveDetectedAutoOcrLanguages(
            "nl",
            ["fra", "eng", "deu", "ita"],
            ["eng", "fra", "deu", "ita", "nld", "ara"]);

        Assert.Equal(["nld", "eng", "fra", "deu", "ita"], languages);
    }

    [Fact]
    public void ResolveAutoConfiguredOcrLanguages_uses_default_bound_when_max_is_invalid()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "auto",
            OcrAutoFallbackLanguages = "fra+eng+deu+ita+spa+por",
            OcrMaxLanguages = 0
        };

        var languages = PdfOcrTextExtractor.ResolveAutoConfiguredOcrLanguages(
            options,
            ["eng", "fra", "deu", "ita", "spa", "por"]);

        Assert.Equal(["fra", "eng", "deu", "ita", "spa"], languages);
    }

    [Fact]
    public void ResolveAutoConfiguredOcrLanguages_keeps_english_when_fallback_is_not_installed()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "auto",
            OcrAutoFallbackLanguages = "fra+deu",
            OcrMaxLanguages = 4
        };

        var languages = PdfOcrTextExtractor.ResolveAutoConfiguredOcrLanguages(
            options,
            ["eng", "spa", "por"]);

        Assert.Equal(["eng"], languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_keeps_non_ui_configured_languages_when_uncertain()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "nld+eng",
            OcrAutoDetectLanguages = true
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Kennisbank/Handleiding.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("nld+eng", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_does_not_cap_explicit_arbitrary_tesseract_languages()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "eng+equ+srp_latn+chi_tra+script/Latin",
            OcrMaxLanguages = 2,
            OcrAutoDetectLanguages = false
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Universal/Document.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("eng+equ+srp_latn+chi_tra+script/Latin", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_accepts_bcp47_language_aliases()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "nl+en",
            OcrAutoDetectLanguages = false
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Kennisbank/Handleiding.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("nld+eng", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_does_not_use_domain_specific_filename_hints()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "fra+eng+deu+spa+por+ita",
            OcrAutoDetectLanguages = true
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Scanned/Industrial Control Panels Scan.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("fra+eng+deu+spa+por+ita", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_falls_back_for_standard_acronym_when_no_other_language_is_known()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "fra+eng+deu+spa+por+ita",
            OcrAutoDetectLanguages = true
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Scanned/ANSI Z535.4.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("fra+eng+deu+spa+por+ita", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_uses_multitoken_filename_language_when_confident()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "auto",
            OcrAutoFallbackLanguages = "fra+eng+deu+ita+spa",
            OcrAutoDetectLanguages = true
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Scans/DVS 2205 Berechnung von Behaltern und Apparaten aus Thermoplasten Flanschverbindungen.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("deu+fra+eng+ita+spa", languages);
    }

    [Fact]
    public void ResolveLanguagesForDocument_normalizes_configured_language_codes()
    {
        var options = new IngestionOptions
        {
            OcrLanguages = "FRA+ENG+DEU+ENG",
            OcrAutoDetectLanguages = false
        };

        var languages = PdfOcrTextExtractor.ResolveLanguagesForDocument(
            "/app/documents/Mixed/Unknown.pdf",
            options,
            nativeExtraction: null);

        Assert.Equal("fra+eng+deu", languages);
    }

    [Fact]
    public void Image_ocr_commands_are_configurable_with_safe_defaults()
    {
        var defaults = new IngestionOptions();
        Assert.Equal("gs", PdfOcrTextExtractor.ResolveImageRendererCommand(defaults));
        Assert.Equal("tesseract", PdfOcrTextExtractor.ResolveImageTextCommand(defaults));
        Assert.Equal(400, defaults.OcrImagePageRenderDpi);
        Assert.Equal(11, defaults.OcrImagePageSegmentationMode);

        var custom = new IngestionOptions
        {
            OcrImageRendererCommand = "mutool",
            OcrImageTextCommand = "custom-tesseract"
        };

        Assert.Equal("mutool", PdfOcrTextExtractor.ResolveImageRendererCommand(custom));
        Assert.Equal("custom-tesseract", PdfOcrTextExtractor.ResolveImageTextCommand(custom));
    }

    [Fact]
    public void BuildImagePageOcrPlan_spreads_attempts_across_large_documents()
    {
        var pages = Enumerable.Range(1, 100)
            .Select(pageNumber =>
            {
                var text = $"Native text page {pageNumber} with enough words to be considered normal extraction.";
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ExtractedPdfPage(
                    pageNumber,
                    text,
                    words.Length,
                    text.Length,
                    [1],
                    ImageCount: 1);
            })
            .ToList();
        var native = new PdfExtractionResult(
            pages.SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber))).ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageMaxPages = 5 });

        Assert.Equal(100, plan.CandidatePageCount);
        Assert.Equal(5, plan.AttemptedPageCount);
        Assert.Equal(95, plan.SkippedPageCount);
        Assert.Equal([1, 26, 51, 75, 100], plan.AttemptedPages);
        Assert.NotEqual([1, 2, 3, 4, 5], plan.AttemptedPages);
    }

    [Fact]
    public void BuildImagePageOcrPlan_uses_unlimited_image_pages_when_max_pages_is_zero()
    {
        var pages = Enumerable.Range(1, 80)
            .Select(pageNumber =>
            {
                var text = $"Native text page {pageNumber} with readable text and an image annotation.";
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ExtractedPdfPage(
                    pageNumber,
                    text,
                    words.Length,
                    text.Length,
                    [1],
                    ImageCount: 1);
            })
            .ToList();
        var native = new PdfExtractionResult(
            pages.SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber))).ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageMaxPages = 0 });

        Assert.Equal(80, plan.CandidatePageCount);
        Assert.Equal(80, plan.AttemptedPageCount);
        Assert.Equal(0, plan.SkippedPageCount);
        Assert.Equal(int.MaxValue, plan.MaxPages);
        Assert.Equal(Enumerable.Range(1, 80).ToArray(), plan.AttemptedPages);
    }

    [Fact]
    public async Task TryMergeImagePageOcrAsync_reports_image_page_progress()
    {
        var native = BuildImagePageNativeExtraction(3);
        var progress = new List<(int Current, int Total)>();
        var cancelChecks = 0;

        var result = await PdfOcrTextExtractor.TryMergeImagePageOcrAsync(
            Path.Combine(Path.GetTempPath(), "saaia-missing-image-progress.pdf"),
            BuildMissingImagePageOcrOptions(maxPages: 3),
            native,
            CancellationToken.None,
            languagesOverride: "eng",
            callbacks: new PdfImagePageOcrCallbacks(
                ReportProgressAsync: (current, total, _) =>
                {
                    progress.Add((current, total));
                    return Task.CompletedTask;
                },
                IsCancellationRequestedAsync: _ =>
                {
                    cancelChecks++;
                    return Task.FromResult(false);
                }));

        Assert.NotNull(result);
        Assert.Null(result!.Extraction);
        Assert.Equal(3, result.Diagnostics.AttemptedPageCount);
        Assert.Contains((0, 3), progress);
        Assert.Contains((1, 3), progress);
        Assert.Contains((2, 3), progress);
        Assert.Contains((3, 3), progress);
        Assert.True(cancelChecks >= 4);
    }

    [Fact]
    public async Task TryMergeImagePageOcrAsync_honors_external_cancel_polling_between_pages()
    {
        var native = BuildImagePageNativeExtraction(2);
        var progress = new List<(int Current, int Total)>();
        var cancelChecks = 0;

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PdfOcrTextExtractor.TryMergeImagePageOcrAsync(
                Path.Combine(Path.GetTempPath(), "saaia-missing-image-cancel.pdf"),
                BuildMissingImagePageOcrOptions(maxPages: 2),
                native,
                CancellationToken.None,
                languagesOverride: "eng",
                callbacks: new PdfImagePageOcrCallbacks(
                    ReportProgressAsync: (current, total, _) =>
                    {
                        progress.Add((current, total));
                        return Task.CompletedTask;
                    },
                    IsCancellationRequestedAsync: _ =>
                    {
                        cancelChecks++;
                        return Task.FromResult(cancelChecks >= 3);
                    })));

        Assert.Contains("Image-page OCR cancellation requested", ex.Message);
        Assert.Contains((0, 2), progress);
        Assert.Contains((1, 2), progress);
    }

    [Fact]
    public void BuildImagePageOcrPlan_prioritizes_sparse_image_pages_before_distributed_normal_pages()
    {
        var pages = Enumerable.Range(1, 12)
            .Select(pageNumber =>
            {
                var sparse = pageNumber is 4 or 9;
                var text = sparse
                    ? "Label"
                    : $"Native text page {pageNumber} with enough words and characters to be considered normal extraction without OCR because the paragraph is already readable and complete.";
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ExtractedPdfPage(
                    pageNumber,
                    text,
                    words.Length,
                    text.Length,
                    [1],
                    ImageCount: 1);
            })
            .ToList();
        var native = new PdfExtractionResult(
            pages.SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber))).ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageMaxPages = 4 });

        Assert.Contains(4, plan.AttemptedPages);
        Assert.Contains(9, plan.AttemptedPages);
        Assert.Equal(4, plan.AttemptedPageCount);
        Assert.Equal(8, plan.SkippedPageCount);
    }

    [Fact]
    public void BuildImagePageOcrPlan_includes_text_pages_with_replacement_characters()
    {
        var pages = new List<ExtractedPdfPage>
        {
            new(
                1,
                "Readable native page without image content and enough words to avoid text recovery OCR candidates",
                14,
                91,
                [1],
                ImageCount: 0),
            new(
                2,
                "Readable but corr\uFFFDpt native page",
                5,
                33,
                [2],
                ImageCount: 0),
            new(
                3,
                "Readable page with image",
                4,
                24,
                [3],
                ImageCount: 1)
        };
        var native = new PdfExtractionResult(
            pages
                .SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber)))
                .ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));

        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            native,
            new IngestionOptions { OcrImagePageMaxPages = 10 });

        Assert.Equal(2, plan.CandidatePageCount);
        Assert.Equal([2, 3], plan.CandidatePages);
        Assert.Equal([2, 3], plan.AttemptedPages);
        Assert.Equal(0, plan.SkippedPageCount);
    }

    [Fact]
    public void BuildImagePageOcrDiagnostics_reports_page_statuses_without_raw_ocr_text()
    {
        var nativePages = new List<ExtractedPdfPage>
        {
            new(1, "Native page text", 3, 16, [1], ImageCount: 1),
            new(2, "Other native text", 3, 17, [2], ImageCount: 1),
            new(3, "Budget skipped text", 3, 19, [3], ImageCount: 1)
        };
        var native = new PdfExtractionResult(
            nativePages
                .SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber)))
                .ToList(),
            nativePages,
            PdfExtractionQualitySummary.FromPages(nativePages));
        var plan = new PdfImagePageOcrPlan(
            CandidatePageCount: 3,
            AttemptedPageCount: 2,
            SkippedPageCount: 1,
            MaxPages: 2,
            CandidatePages: [1, 2, 3],
            AttemptedPages: [1, 2],
            SkippedPages: [3]);
        var pageText = new Dictionary<int, string>
        {
            [1] = "Native page text\nIMAGE PANEL READY"
        };
        var merged = PdfOcrTextExtractor.MergeImageOcrText(native, pageText, "eng", minWords: 2);

        var diagnostics = PdfOcrTextExtractor.BuildImagePageOcrDiagnostics(
            plan,
            pageText,
            native,
            merged,
            new Dictionary<int, PdfImagePageOcrDiagnostic>
            {
                [2] = new(2, "render_failed", "exit_code_non_zero", ExitCode: 1)
            });

        Assert.Equal("image_page", diagnostics.Mode);
        Assert.Equal("partial_error", diagnostics.CoverageStatus);
        Assert.Equal([1], diagnostics.PagesWithOcrText);
        Assert.Equal([1], diagnostics.PagesWithNovelText);
        Assert.NotNull(diagnostics.ImagePageDiagnostics);
        var byPage = diagnostics.ImagePageDiagnostics!.ToDictionary(static page => page.PageNumber);
        Assert.Equal("novel_text_applied", byPage[1].Status);
        Assert.Equal(6, byPage[1].OcrWordCount);
        Assert.Equal("render_failed", byPage[2].Status);
        Assert.Equal("exit_code_non_zero", byPage[2].Reason);
        Assert.Equal(1, byPage[2].ExitCode);
        Assert.Equal("skipped", byPage[3].Status);
        Assert.Equal("budget", byPage[3].Reason);
        Assert.DoesNotContain(
            diagnostics.ImagePageDiagnostics,
            page => string.Equals(page.Reason, "IMAGE PANEL READY", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldAttemptImagePageOcr_keeps_image_ocr_enabled_when_full_document_ocr_is_recommended()
    {
        var page = new ExtractedPdfPage(
            1,
            "Label",
            1,
            5,
            [1],
            ImageCount: 1);
        var extraction = new PdfExtractionResult(
            [new WordToken("Label", 1)],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.True(extraction.Quality.OcrRecommended);
        Assert.True(IngestionWorker.ShouldAttemptImagePageOcr(new IngestionOptions { OcrImagePageEnabled = true }, extraction));
    }

    [Fact]
    public void ResolveImageOcrMergeBase_preserves_full_ocr_acquired_before_an_image_retry()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "",
            0,
            0,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));
        var fullOcrPage = new ExtractedPdfPage(
            1,
            "Full document OCR text with plausible recognition errors",
            8,
            53,
            [2],
            ImageCount: 1);
        var fullOcr = new PdfExtractionResult(
            [new WordToken("Full", 1)],
            [fullOcrPage],
            PdfExtractionQualitySummary.FromPages([fullOcrPage]),
            Source: "ocr_sidecar");

        var mergeBase = IngestionWorker.ResolveImageOcrMergeBase(native, fullOcr, fullOcrApplied: true);

        Assert.Same(fullOcr, mergeBase);
    }

    [Fact]
    public void ResolveImageOcrMergeBase_keeps_full_ocr_base_when_native_has_text()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Native text already extracted from the PDF.",
            7,
            43,
            [1],
            ImageCount: 1);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));
        var fullOcrPage = new ExtractedPdfPage(
            1,
            "Full OCR text can still provide image-page merge context.",
            8,
            55,
            [2],
            ImageCount: 1);
        var fullOcr = new PdfExtractionResult(
            [new WordToken("Full", 1)],
            [fullOcrPage],
            PdfExtractionQualitySummary.FromPages([fullOcrPage]),
            Source: "ocr_sidecar");

        var mergeBase = IngestionWorker.ResolveImageOcrMergeBase(native, fullOcr, fullOcrApplied: true);

        Assert.Same(fullOcr, mergeBase);
    }

    [Fact]
    public void ShouldAttemptImagePageOcr_detects_replacement_characters_without_images()
    {
        var page = new ExtractedPdfPage(
            1,
            "Readable but corr\uFFFDpt native text",
            5,
            33,
            [1],
            ImageCount: 0);
        var extraction = new PdfExtractionResult(
            [new WordToken("Readable", 1)],
            [page],
            PdfExtractionQualitySummary.FromPages([page]));

        Assert.True(IngestionWorker.ShouldAttemptImagePageOcr(new IngestionOptions { OcrImagePageEnabled = true }, extraction));
        Assert.False(IngestionWorker.ShouldAttemptImagePageOcr(new IngestionOptions { OcrImagePageEnabled = false }, extraction));
    }

    [Fact]
    public void BuildOcrDisabledDiagnostics_reports_required_image_page_ocr_as_skipped()
    {
        var pages = new List<ExtractedPdfPage>
        {
            new(1, "Native text with an image annotation", 6, 36, [1], ImageCount: 1),
            new(2, "More native text with a diagram", 6, 31, [2], ImageCount: 1)
        };
        var native = new PdfExtractionResult(
            pages
                .SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber)))
                .ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));
        var options = new IngestionOptions
        {
            OcrEnabled = false,
            OcrImagePageEnabled = true
        };

        var imagePageOcrRecommended = IngestionWorker.ShouldAttemptImagePageOcr(options, native);
        var diagnostics = PdfOcrTextExtractor.BuildOcrDisabledDiagnostics(
            options,
            native,
            imagePageOcrRecommended);

        Assert.True(imagePageOcrRecommended);
        Assert.Equal("ocr_disabled", diagnostics.Mode);
        Assert.Equal("ocr_required_but_disabled", diagnostics.FailureReason);
        Assert.Equal("ocr_disabled", diagnostics.AppliedReason);
        Assert.Equal("disabled", diagnostics.CoverageStatus);
        Assert.Equal(2, diagnostics.CandidatePageCount);
        Assert.Equal(0, diagnostics.AttemptedPageCount);
        Assert.Equal(2, diagnostics.SkippedPageCount);
        Assert.Equal([1, 2], diagnostics.CandidatePages);
        Assert.Equal([1, 2], diagnostics.SkippedPages);
        Assert.NotNull(diagnostics.ImagePageDiagnostics);
        Assert.All(diagnostics.ImagePageDiagnostics!, page =>
        {
            Assert.Equal("skipped", page.Status);
            Assert.Equal("ocr_disabled", page.Reason);
        });
    }

    [Fact]
    public void ResolveNoTextFailureReason_reports_ocr_required_but_disabled()
    {
        Assert.True(IngestionWorker.IsOcrRequiredButDisabled(
            new IngestionOptions { OcrEnabled = false },
            fullDocumentOcrRecommended: true,
            imagePageOcrRecommended: false));
        Assert.Equal(
            "ocr_required_but_disabled",
            IngestionWorker.ResolveNoTextFailureReason(
                ocrAttempted: false,
                ocrRequiredButDisabled: true));
        Assert.Equal(
            "no_indexable_text",
            IngestionWorker.ResolveNoTextFailureReason(
                ocrAttempted: false,
                ocrRequiredButDisabled: false));
    }

    [Fact]
    public void WithImageCountsFromNative_preserves_image_candidates_after_full_document_ocr()
    {
        var nativePage = new ExtractedPdfPage(
            1,
            "Native sparse label",
            3,
            19,
            [1],
            ImageCount: 2);
        var native = new PdfExtractionResult(
            [new WordToken("Native", 1)],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var fullOcrPage = new ExtractedPdfPage(
            1,
            "Full OCR page text",
            4,
            18,
            [2],
            ImageCount: 0);
        var fullOcr = new PdfExtractionResult(
            [new WordToken("Full", 1)],
            [fullOcrPage],
            PdfExtractionQualitySummary.FromPages([fullOcrPage]),
            Source: "ocr_sidecar");

        var merged = PdfOcrTextExtractor.WithImageCountsFromNative(fullOcr, native);
        var plan = PdfOcrTextExtractor.BuildImagePageOcrPlan(
            merged,
            new IngestionOptions { OcrImagePageMaxPages = 10 });

        Assert.Equal(2, merged.Pages[0].ImageCount);
        Assert.Equal(1, plan.CandidatePageCount);
        Assert.Equal([1], plan.AttemptedPages);
    }

    [Fact]
    public void CombineOcrDiagnostics_keeps_image_page_details_after_full_document_ocr()
    {
        var full = new PdfOcrDiagnostics(
            Mode: "full_document",
            CandidatePageCount: 0,
            AttemptedPageCount: 0,
            SkippedPageCount: 0,
            MaxPages: 0,
            CandidatePages: [],
            AttemptedPages: [],
            SkippedPages: [],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            ExitCode: 0,
            Stderr: "full stderr");
        var image = new PdfOcrDiagnostics(
            Mode: "image_page",
            CandidatePageCount: 2,
            AttemptedPageCount: 1,
            SkippedPageCount: 1,
            MaxPages: 1,
            CandidatePages: [1, 2],
            AttemptedPages: [1],
            SkippedPages: [2],
            PagesWithOcrText: [1],
            PagesWithNovelText: [1],
            ImagePageDiagnostics:
            [
                new PdfImagePageOcrDiagnostic(1, "novel_text_applied", OcrWordCount: 5, OcrCharCount: 30),
                new PdfImagePageOcrDiagnostic(2, "skipped", "budget")
            ]);

        var combined = PdfOcrTextExtractor.CombineOcrDiagnostics(full, image);

        Assert.NotNull(combined);
        Assert.Equal("full_document_plus_image_page", combined!.Mode);
        Assert.Equal("partial_budget", combined.CoverageStatus);
        Assert.Equal(2, combined.CandidatePageCount);
        Assert.Equal([1], combined.PagesWithNovelText);
        Assert.NotNull(combined.ImagePageDiagnostics);
        Assert.Contains(combined.ImagePageDiagnostics!, page => page.PageNumber == 2 && page.Status == "skipped");
        Assert.Contains("full_document", combined.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImagePageOcrDiagnostics_preserves_time_budget_skipped_reason()
    {
        var nativePages = new[]
        {
            new ExtractedPdfPage(1, "Native page text", 3, 16, [1], ImageCount: 1),
            new ExtractedPdfPage(2, "Other native text", 3, 17, [2], ImageCount: 1)
        };
        var native = new PdfExtractionResult(
            nativePages.SelectMany(page => page.Text.Split(' ').Select(word => new WordToken(word, page.PageNumber))).ToList(),
            nativePages.ToList(),
            PdfExtractionQualitySummary.FromPages(nativePages),
            "pdf_text");
        var plan = new PdfImagePageOcrPlan(
            CandidatePageCount: 2,
            AttemptedPageCount: 1,
            SkippedPageCount: 1,
            MaxPages: int.MaxValue,
            CandidatePages: [1, 2],
            AttemptedPages: [1],
            SkippedPages: [2]);

        var diagnostics = PdfOcrTextExtractor.BuildImagePageOcrDiagnostics(
            plan,
            new Dictionary<int, string> { [1] = "OCR page one text" },
            native,
            mergedExtraction: null,
            new Dictionary<int, PdfImagePageOcrDiagnostic>
            {
                [2] = new PdfImagePageOcrDiagnostic(2, "skipped", "time_budget")
            },
            failureReason: "timeout_budget",
            timedOut: true,
            timeoutSeconds: 900);

        Assert.Equal("partial_budget", diagnostics.CoverageStatus);
        Assert.True(diagnostics.TimedOut);
        Assert.Equal(900, diagnostics.TimeoutSeconds);
        Assert.Equal("timeout_budget", diagnostics.FailureReason);
        var skipped = Assert.Single(diagnostics.ImagePageDiagnostics!, page => page.PageNumber == 2);
        Assert.Equal("skipped", skipped.Status);
        Assert.Equal("time_budget", skipped.Reason);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static IngestionOptions BuildMissingImagePageOcrOptions(int maxPages)
        => new()
        {
            OcrEnabled = true,
            OcrImagePageEnabled = true,
            OcrImagePageMaxPages = maxPages,
            OcrImageRendererCommand = "saaia-missing-image-renderer-command",
            OcrImageTextCommand = "saaia-missing-image-text-command",
            OcrImagePageTimeoutSeconds = 10,
            OcrImagePageMaxTotalSeconds = 30
        };

    private static PdfExtractionResult BuildImagePageNativeExtraction(int pageCount)
    {
        var pages = Enumerable.Range(1, pageCount)
            .Select(pageNumber =>
            {
                var text = $"Native text page {pageNumber} with an image requiring possible OCR recovery.";
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ExtractedPdfPage(
                    pageNumber,
                    text,
                    words.Length,
                    text.Length,
                    [1],
                    ImageCount: 1);
            })
            .ToList();

        return new PdfExtractionResult(
            pages.SelectMany(page => page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => new WordToken(word, page.PageNumber))).ToList(),
            pages,
            PdfExtractionQualitySummary.FromPages(pages));
    }

    private static void WriteRasterTextPdf(string path)
    {
        const string resourceName = "SAAIA.Backend.Tests.OcrRealisticRasterV1.pdf";
        const string expectedSha256 = "69441AAB874DFB04AC8D015431FF3746564BB8D88B6A6B022327B7F2C37C60C6";
        using var source = typeof(PdfOcrTextExtractorTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The hash-pinned synthetic OCR PDF is not embedded in the test assembly.");
        using var output = new MemoryStream();
        source.CopyTo(output);
        var bytes = output.ToArray();
        Assert.Equal(expectedSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        File.WriteAllBytes(path, bytes);
    }

    private static async Task AssertCommandAvailableAsync(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            Assert.NotNull(process);
            var exited = await Task.Run(() => process!.WaitForExit(10_000));
            if (!exited)
            {
                try { process!.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"{command} {arguments} did not exit within 10 seconds.");
            }

            Assert.True(
                process!.ExitCode == 0,
                $"OCR E2E dependency command failed: {command} {arguments} exited with {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"OCR E2E dependency unavailable: {command}. Set SAAIA_TEST_OCR_* variables or install the dependency.",
                ex);
        }
    }

    private static void AssertOcrTokens(PdfExtractionResult extraction)
    {
        var normalized = extraction.Tokens
            .Select(static token => new string(token.Word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(static token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // These are the two operational lines and their identifier; the decorative header is not the oracle.
        var expected = new[] { "pump", "reset", "panel", "batch", "code", "xq42" };
        var missing = expected.Where(token => !normalized.Contains(token)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"OCR fixture operational tokens are missing: [{string.Join(", ", missing)}]. Recognized tokens: [{string.Join(", ", normalized.Order(StringComparer.Ordinal))}].");
    }

    private static string DescribeOcrFailure(string stage, PdfOcrDiagnostics diagnostics)
        => $"OCR fixture {stage} produced no extraction: reason={diagnostics.FailureReason ?? "none"}; " +
           $"exit={diagnostics.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
           $"timedOut={diagnostics.TimedOut}; attempted={diagnostics.AttemptedPageCount}; " +
           $"stderr={new string((diagnostics.Stderr ?? "").Where(static ch => !char.IsControl(ch) || ch is '\n' or '\t').Take(2048).ToArray())}";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for diagnostics artifacts.
        }
    }

    private static string ResolveDotNetCommand()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

}
