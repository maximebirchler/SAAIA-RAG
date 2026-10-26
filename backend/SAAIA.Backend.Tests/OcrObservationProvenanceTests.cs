using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class OcrObservationProvenanceTests
{
    [Fact]
    public void Output_pdf_pagination_and_native_text_are_primary_when_skip_text_sidecar_is_partial()
    {
        PdfExtractionResult Make(params string[] texts)
        {
            var pages = texts.Select((text, index) => new ExtractedPdfPage(index + 1, text, 4, text.Length, [1],
                RawText: text)).ToList();
            return new([], pages, PdfExtractionQualitySummary.FromPages(pages));
        }
        var processed = Make("Native first physical page.", "Recovered second physical page.", "Native third physical page.");
        var sidecar = Make("Recovered second physical page.");
        var result = OcrSourceReconciler.FromProcessedPdf(processed, sidecar, "ocrmypdf", "eng");
        Assert.Equal(processed.Pages.Select(page => page.Text), result.Pages.Select(page => page.Text));
        Assert.Equal(3, result.Pages.Count);
        Assert.All(result.Pages, page =>
        {
            Assert.Contains("ocr_sidecar_page_mapping_unverified", page.Quality!.Signals);
            Assert.Single(page.OcrObservations!);
            Assert.DoesNotContain(page.OcrObservations!, observation => observation.Engine.EndsWith("_sidecar", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Raw_words_confidence_and_raster_bounds_survive_in_existing_canonical_artifact()
    {
        const string raw = "Pressure limit 5 bar";
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "1\t1\t0\t0\t0\t0\t0\t0\t400\t600\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t20\t30\t40\t10\t20\tPressure\n"
            + "5\t1\t1\t1\t1\t2\t70\t30\t30\t10\t99\tlimit\n"
            + "5\t1\t1\t1\t1\t3\t110\t30\t5\t10\t99\t5\n"
            + "5\t1\t1\t1\t1\t4\t120\t30\t20\t10\tinvalid\tbar\n";
        var observation = OcrObservationParser.Parse(raw, tsv, "tesseract", "eng");
        var page = new ExtractedPdfPage(1, raw, 4, raw.Length, [1], RawText: "",
            OcrObservations: [observation]);
        var extraction = new PdfExtractionResult([], [page], PdfExtractionQualitySummary.FromPages([page]));
        var context = new LegacyCanonicalProjectionContext(Guid.NewGuid(), Guid.NewGuid(), new('a', 64),
            1024, "test.pdf", new('b', 64), "projection", "test", "PdfPig", "fixture");

        var document = LegacyPdfCanonicalDocumentAdapter.Project(context, extraction, []);

        var block = Assert.Single(document.Pages[0].Blocks, block => block.BlockType == "ocr_source_observation");
        Assert.Equal(raw, block.Text.Raw);
        Assert.Empty(block.Text.Retrieval);
        Assert.Equal(tsv, block.Provenance.Attributes["rawTsv"]);
        Assert.Equal("not_asserted", block.Provenance.Attributes["sectionAssociation"]);
        Assert.Equal("not_attested", block.Provenance.EngineVersion);
        var span = Assert.Single(block.Spans);
        Assert.Equal(4, span.Words.Count);
        Assert.Equal(0.20, span.Words[0].Confidence);
        Assert.Null(span.Words[3].Confidence);
        Assert.All(span.Words, word => Assert.Null(word.Polygon));
        Assert.Contains("ocr_low_confidence_observation", block.QualityFlags);
        Assert.Contains("20", span.Provenance.Attributes["rasterWordBounds"], StringComparison.Ordinal);
        CanonicalContractValidator.ValidateOrThrow(document);
    }

    [Fact]
    public void Page_keeps_reliable_native_text_but_exposes_unknown_ocr_zone_separately()
    {
        var review = ExtractionQualityDiagnostics.AssessPage(80, 500, 1, 3, 0, 2,
            ocrDiagnostic: new(1, "render_failed", "timeout", TimedOut: true));
        Assert.Equal("ok", review.TextStatus);
        Assert.False(review.TextEmpty);
        Assert.Equal("page_partial_ocr_coverage", review.Status);
        Assert.Equal("render_failed", review.OcrCoverageStatus);
        Assert.Equal("heuristic_indexability", review.ConfidenceKind);
        Assert.True(review.ManualReviewRecommended);
    }

    [Fact]
    public async Task Process_capture_drains_the_entire_stream_without_retaining_unbounded_output()
    {
        using var reader = new StringReader(new string('x', 100_000) + "terminal");
        var capture = await OcrProcessRunner.ReadBoundedAsync(reader, 8192, CancellationToken.None);
        Assert.Equal(8192, capture.Length);
        Assert.Equal(-1, reader.Peek());
    }

    [Theory]
    [InlineData("Power may operate during maintenance.", "Power may not operate during maintenance.")]
    [InlineData("Limit 5", "Limit 50")]
    public void Replacement_admission_cannot_hide_a_negation_or_extend_a_number(string native, string candidate)
    {
        PdfExtractionResult Make(string text)
        {
            var page = new ExtractedPdfPage(1, text, 20, text.Length, [1]);
            return new([new WordToken("text", 1)], [page], PdfExtractionQualitySummary.FromPages([page]));
        }
        Assert.False(PdfOcrTextExtractor.ShouldApplyOcrExtraction(Make(native), Make(candidate), true, true));
    }
}
