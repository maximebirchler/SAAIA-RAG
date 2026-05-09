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

    [Fact]
    public void BuildQualityAdjustedMatchesPreservingRank_keeps_final_retrieval_order()
    {
        var selected = new RagMatch(
            0.90,
            "doc-a",
            "Ops/Manual.pdf",
            "Manual.pdf",
            5,
            5,
            "selected",
            3,
            "Requested title with direct answer text.",
            1,
            "hash-a",
            "Requested title with direct answer text.",
            "sparse_bm25_v1",
            1,
            1,
            "Requested title",
            "Requested title",
            "unit_exact_v1",
            null,
            null,
            null);
        var highRawScoreNeighbor = new RagMatch(
            1.02,
            "doc-b",
            "Ops/Index.pdf",
            "Index.pdf",
            66,
            66,
            "neighbor",
            20,
            "Neighbor text with a stronger raw score.",
            1,
            "hash-b",
            "Neighbor text with a stronger raw score.",
            "sparse_bm25_v1",
            1,
            1,
            "Neighbor",
            "Neighbor",
            "unit_exact_v1",
            null,
            null,
            null);
        var lowQuality = new RagItemExtractionQualityDto(
            DocumentQualityStatus: "ocr_applied_ok_with_page_warnings",
            DocumentExtractionConfidence: 0.82,
            DocumentManualReviewRecommended: false,
            PageQualityStatus: "manual_review_probable_ocr_noise",
            PageExtractionConfidence: 0.31,
            PageManualReviewRecommended: true,
            OcrApplied: true);
        var qualityByMatch = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.Ordinal)
        {
            [RagEndpoints.BuildExtractionQualityMatchKey(selected)] = lowQuality
        };

        var adjusted = RagEndpoints.BuildQualityAdjustedMatchesPreservingRank(
            [selected, highRawScoreNeighbor],
            qualityByMatch);

        Assert.Equal("selected", adjusted[0].Match.ChunkId);
        Assert.Equal("neighbor", adjusted[1].Match.ChunkId);
        Assert.True(adjusted[0].AdjustedScore < adjusted[1].AdjustedScore);
    }
}
