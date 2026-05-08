using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ExtractionQualityDiagnosticsTests
{
    [Fact]
    public void AssessPage_flags_empty_text_for_manual_review()
    {
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 0,
            charCount: 0,
            imageCount: 1,
            unitCount: 0,
            suspiciousUnitCount: 0,
            chunkCount: 0);

        Assert.Equal("manual_review_empty_text", review.Status);
        Assert.True(review.ManualReviewRecommended);
        Assert.True(review.OcrCandidate);
        Assert.Contains("page_contains_images", review.Signals);
        Assert.Contains("no_units_on_page", review.Signals);
    }

    [Fact]
    public void AssessPage_keeps_empty_text_without_images_informational()
    {
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 0,
            charCount: 0,
            imageCount: 0,
            unitCount: 0,
            suspiciousUnitCount: 0,
            chunkCount: 0);

        Assert.Equal("page_ok_empty_text", review.Status);
        Assert.False(review.ManualReviewRecommended);
        Assert.True(review.OcrCandidate);
        Assert.Contains("no_text_on_page", review.Signals);
    }

    [Fact]
    public void AssessPage_marks_empty_page_covered_by_chunks_as_indexed_context()
    {
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 0,
            charCount: 0,
            imageCount: 0,
            unitCount: 0,
            suspiciousUnitCount: 0,
            chunkCount: 1);

        Assert.Equal("page_ok_indexed_by_context", review.Status);
        Assert.False(review.ManualReviewRecommended);
        Assert.Contains("no_units_on_page", review.Signals);
    }

    [Fact]
    public void AssessPage_keeps_image_only_signal_informational_when_text_is_good()
    {
        var text = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"token{i}"));
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 80,
            charCount: text.Length,
            imageCount: 2,
            unitCount: 4,
            suspiciousUnitCount: 0,
            chunkCount: 2);

        Assert.Equal("page_ok_with_images", review.Status);
        Assert.False(review.ManualReviewRecommended);
        Assert.False(review.OcrCandidate);
        Assert.Contains("page_contains_images", review.Signals);
    }

    [Fact]
    public void AssessPage_flags_probable_ocr_noise_units()
    {
        var text = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"token{i}"));
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 80,
            charCount: text.Length,
            imageCount: 0,
            unitCount: 5,
            suspiciousUnitCount: 2,
            chunkCount: 3);

        Assert.Equal("manual_review_probable_ocr_noise", review.Status);
        Assert.True(review.ManualReviewRecommended);
        Assert.Contains("probable_ocr_noise_units", review.Signals);
    }

    [Fact]
    public void AssessPage_keeps_low_value_text_without_units_informational()
    {
        var review = ExtractionQualityDiagnostics.AssessPage(
            wordCount: 12,
            charCount: 87,
            imageCount: 0,
            unitCount: 0,
            suspiciousUnitCount: 0,
            chunkCount: 0);

        Assert.Equal("page_ok_low_value_text", review.Status);
        Assert.False(review.ManualReviewRecommended);
        Assert.Contains("no_units_on_page", review.Signals);
        Assert.Contains("no_chunks_on_page", review.Signals);
    }
}
