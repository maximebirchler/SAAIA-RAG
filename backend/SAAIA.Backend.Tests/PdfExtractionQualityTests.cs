using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractionQualityTests
{
    [Fact]
    public void FromPages_recommends_ocr_when_no_text_is_extracted()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, string.Empty, 0, 0, [1]),
            new ExtractedPdfPage(2, "   ", 0, 3, [2])
        };

        var quality = PdfExtractionQualitySummary.FromPages(pages);

        Assert.Equal("empty_text", quality.TextStatus);
        Assert.True(quality.OcrRecommended);
        Assert.Equal(2, quality.EmptyPageCount);
        Assert.Contains("no_text_extracted", quality.Signals);
        Assert.Contains("ocr_recommended", quality.Signals);
    }

    [Fact]
    public void FromPages_marks_sparse_documents_without_hiding_text_pages()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "Cover", 1, 5, [1]),
            new ExtractedPdfPage(2, "Index", 1, 5, [2]),
            new ExtractedPdfPage(3, "Short notice", 2, 12, [3])
        };

        var quality = PdfExtractionQualitySummary.FromPages(pages);

        Assert.Equal("low_text", quality.TextStatus);
        Assert.True(quality.OcrRecommended);
        Assert.Equal(3, quality.TextPageCount);
        Assert.Equal(3, quality.SparsePageCount);
        Assert.Contains("many_sparse_pages", quality.Signals);
    }

    [Fact]
    public void FromPages_accepts_text_rich_documents()
    {
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(index => $"token{index}"));
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 60, text.Length, [1]),
            new ExtractedPdfPage(2, text, 60, text.Length, [2])
        };

        var quality = PdfExtractionQualitySummary.FromPages(pages);

        Assert.Equal("ok", quality.TextStatus);
        Assert.False(quality.OcrRecommended);
        Assert.Equal(2, quality.TextPageCount);
        Assert.Equal(0, quality.SparsePageCount);
        Assert.Contains("text_extraction_ok", quality.Signals);
    }

    [Fact]
    public void FromText_flags_replacement_characters_as_ocr_candidates()
    {
        var text = "Configuration and protec\uFFFDion requirements";
        var quality = PdfPageExtractionQuality.FromText(text, 4, text.Length);

        Assert.Equal("low_text", quality.TextStatus);
        Assert.True(quality.TextSparse);
        Assert.True(quality.OcrCandidate);
        Assert.Contains("replacement_chars_detected", quality.Signals);
        Assert.DoesNotContain("text_extraction_ok", quality.Signals);
    }

    [Fact]
    public void FromPages_detects_replacement_characters_when_page_quality_is_missing()
    {
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(index => index == 30 ? "protec\uFFFDion" : $"token{index}"));
        var page = new ExtractedPdfPage(1, text, 60, text.Length, [1], Quality: null);

        var quality = PdfExtractionQualitySummary.FromPages([page]);

        Assert.Equal("ok", quality.TextStatus);
        Assert.Contains("replacement_chars_detected", quality.Signals);
        Assert.Equal(1, quality.SparsePageCount);
    }
}
