using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RagExtractionQualityScoringTests
{
    [Fact]
    public void ApplyExtractionQualityScorePenalty_keeps_clean_quality_score_unchanged()
    {
        var quality = new RagItemExtractionQualityDto(
            DocumentQualityStatus: "extraction_ok",
            DocumentExtractionConfidence: 0.96,
            DocumentManualReviewRecommended: false,
            PageQualityStatus: "page_ok",
            PageExtractionConfidence: 0.94,
            PageManualReviewRecommended: false);

        var adjusted = RagEndpoints.ApplyExtractionQualityScorePenalty(0.91, quality);

        Assert.Equal(0.91, adjusted);
    }

    [Fact]
    public void ApplyExtractionQualityScorePenalty_softly_demotes_manual_review_pages()
    {
        var quality = new RagItemExtractionQualityDto(
            DocumentQualityStatus: "ocr_applied_ok_with_page_warnings",
            DocumentExtractionConfidence: 0.82,
            DocumentManualReviewRecommended: false,
            PageQualityStatus: "manual_review_probable_ocr_noise",
            PageExtractionConfidence: 0.31,
            PageManualReviewRecommended: true,
            OcrApplied: true);

        var adjusted = RagEndpoints.ApplyExtractionQualityScorePenalty(0.91, quality);

        Assert.InRange(adjusted, 0.52, 0.53);
    }
}
