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
    public void ApplyExtractionQualityScorePenalty_uses_chunk_level_qdrant_quality_signals()
    {
        var quality = new RagItemExtractionQualityDto(
            DocumentQualityStatus: "extraction_ok",
            DocumentExtractionConfidence: 0.96,
            DocumentManualReviewRecommended: false,
            PageQualityStatus: "page_ok",
            PageExtractionConfidence: 0.94,
            PageManualReviewRecommended: false,
            TextStatus: "ok",
            Signals: ["text_extraction_ok"],
            ChunkTextStatus: "low_text",
            ChunkTextSparse: true,
            ChunkOcrCandidate: true,
            ChunkQualitySignals: ["sparse_text_on_page", "ocr_candidate_text"]);

        var adjusted = RagEndpoints.ApplyExtractionQualityScorePenalty(0.91, quality);

        Assert.InRange(adjusted, 0.49, 0.82);
        Assert.True(adjusted < 0.91);
    }

    [Fact]
    public void BuildEffectiveExtractionQualityByMatch_fuses_qdrant_chunk_signals_with_database_quality()
    {
        var match = new RagMatch(
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
            "contextual_text_v1",
            1,
            1,
            "Requested title",
            "Requested title",
            "unit_exact_v1",
            null,
            null,
            null,
            ExtractionTextStatus: "low_text",
            ExtractionTextSparse: true,
            ExtractionOcrCandidate: true,
            ExtractionQualitySignals: ["sparse_text_on_page", "ocr_candidate_text"]);
        var quality = new RagItemExtractionQualityDto(
            DocumentQualityStatus: "extraction_ok",
            DocumentExtractionConfidence: 0.96,
            DocumentManualReviewRecommended: false,
            PageQualityStatus: "page_ok",
            PageExtractionConfidence: 0.94,
            PageManualReviewRecommended: false,
            TextStatus: "ok",
            Signals: ["text_extraction_ok"]);
        var qualityByMatch = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase)
        {
            [RagEndpoints.BuildExtractionQualityMatchKey(match)] = quality
        };

        var effective = RagEndpoints.BuildEffectiveExtractionQualityByMatch([match], qualityByMatch)
            [RagEndpoints.BuildExtractionQualityMatchKey(match)];

        Assert.Equal("ok", effective.TextStatus);
        Assert.Equal("low_text", effective.ChunkTextStatus);
        Assert.True(effective.ChunkTextSparse);
        Assert.True(effective.ChunkOcrCandidate);
        Assert.Contains("text_extraction_ok", effective.Signals!);
        Assert.Contains("sparse_text_on_page", effective.Signals!);
        Assert.Contains("ocr_candidate_text", effective.ChunkQualitySignals!);
    }

    [Fact]
    public void RerankDenseMatches_demotes_noisy_qdrant_chunk_when_clean_alternative_exists()
    {
        var noisy = new RagMatch(
            0.91,
            "doc-a",
            "Ops/Scanned.pdf",
            "Scanned.pdf",
            3,
            3,
            "noisy",
            3,
            "Target answer with OCR artefacts.",
            1,
            "hash-a",
            "Target answer with OCR artefacts.",
            "contextual_text_v1",
            1,
            1,
            "Target",
            "Target",
            "unit_exact_v1",
            null,
            null,
            null,
            ExtractionTextStatus: "low_text",
            ExtractionTextSparse: true,
            ExtractionOcrCandidate: true,
            ExtractionQualitySignals: ["replacement_chars_remaining"]);
        var clean = noisy with
        {
            Score = 0.86,
            DocId = "doc-b",
            DocPath = "Ops/Clean.pdf",
            DocName = "Clean.pdf",
            ChunkId = "clean",
            HashDoc = "hash-b",
            ExtractionTextStatus = "ok",
            ExtractionTextSparse = false,
            ExtractionOcrCandidate = false,
            ExtractionQualitySignals = ["text_extraction_ok"]
        };

        var ranked = RagEndpoints.RerankDenseMatches([noisy, clean]);

        Assert.Equal("clean", ranked[0].ChunkId);
        Assert.Equal("noisy", ranked[1].ChunkId);
    }

    [Fact]
    public void RerankDenseMatches_keeps_noisy_qdrant_chunk_when_it_is_the_only_direct_evidence()
    {
        var noisy = CreateMatch(
            score: 0.91,
            docPath: "Ops/Scanned.pdf",
            chunkId: "noisy",
            text: "Direct evidence extracted from an OCR-heavy page.",
            extractionTextStatus: "low_text",
            extractionTextSparse: true,
            extractionOcrCandidate: true,
            extractionQualitySignals: ["replacement_chars_remaining", "probable_ocr_noise"]);

        var ranked = RagEndpoints.RerankDenseMatches([noisy]);

        var result = Assert.Single(ranked);
        Assert.Equal("noisy", result.ChunkId);
        Assert.InRange(result.Score, 0.55, 0.80);
    }

    [Fact]
    public void Chunk_quality_penalty_does_not_treat_document_profiles_without_page_quality_as_noisy_chunks()
    {
        var profile = CreateMatch(
            score: 0.62,
            docPath: "Knowledge/Profiled.pdf",
            chunkId: "profile",
            text: "Document profile covering the requested topic.",
            chunkType: "document_profile",
            embeddingBasis: "document_profile");

        Assert.Equal(0, RagEndpoints.ComputeChunkExtractionQualityPenalty(profile));
        Assert.Equal(0.62, RagEndpoints.ApplyChunkExtractionQualityScorePenalty(profile.Score, profile));
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

    private static RagMatch CreateMatch(
        double score,
        string docPath,
        string chunkId,
        string text,
        string chunkType = "unit_exact_v1",
        string embeddingBasis = "contextual_text_v1",
        string? extractionTextStatus = null,
        bool? extractionTextSparse = null,
        bool? extractionOcrCandidate = null,
        IReadOnlyList<string>? extractionQualitySignals = null)
        => new(
            Score: score,
            DocId: $"doc-{chunkId}",
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: 1,
            PageEnd: 1,
            ChunkId: chunkId,
            ChunkIndex: 1,
            Text: text,
            IngestionVersion: 1,
            HashDoc: $"hash-{chunkId}",
            EmbedText: text,
            EmbeddingBasis: embeddingBasis,
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Topic",
            HeadingPath: "Topic",
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ExtractionTextStatus: extractionTextStatus,
            ExtractionTextSparse: extractionTextSparse,
            ExtractionOcrCandidate: extractionOcrCandidate,
            ExtractionQualitySignals: extractionQualitySignals);
}
