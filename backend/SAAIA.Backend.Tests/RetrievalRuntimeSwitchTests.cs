using System.Text.Json;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalRuntimeSwitchTests
{
    [Fact]
    public void ResolveEmbeddingText_prefers_contextual_text_when_available()
    {
        var contextual = new[]
        {
            new ProjectedContextualTextEntry(0, 1, 2, 3, 4, 5, "Document: CEN.pdf\nExcerpt:\nContextualized", 38, 4, [1])
        };

        var map = IngestionWorker.BuildContextualTextMap(contextual);
        var resolved = IngestionWorker.ResolveEmbeddingText(3, "Raw chunk text", map);

        Assert.Equal("Document: CEN.pdf\nExcerpt:\nContextualized", resolved);
    }

    [Fact]
    public void ResolveEmbeddingText_falls_back_to_chunk_text_when_context_is_missing()
    {
        var resolved = IngestionWorker.ResolveEmbeddingText(
            7,
            "Raw chunk text",
            IngestionWorker.BuildContextualTextMap([]));

        Assert.Equal("Raw chunk text", resolved);
    }

    [Fact]
    public void BuildQdrantChunkPayload_keeps_chunk_text_and_tracks_contextual_embedding_basis()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var docId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var projectedChunk = new ProjectedRetrievalChunk(
            ChunkIndex: 4,
            SectionOrdinal: 2,
            UnitOrdinal: 9,
            PageStart: 3,
            PageEnd: 4,
            Text: "Chunk snippet",
            TokenCount: 2,
            Checksum: [5],
            ChunkType: "section_window_v1");

        var payload = IngestionWorker.BuildQdrantChunkPayload(
            tenantId,
            docId,
            "ATEX/CEN.pdf",
            "atex",
            "abc123",
            "2026-04-13T10:15:00.0000000Z",
            6,
            projectedChunk,
            "Document: CEN.pdf\nExcerpt:\nChunk snippet",
            "Introduction",
            "Chapter 1 > Introduction",
            new IngestionWorker.ChunkLinkInfo(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

        Assert.Equal("Chunk snippet", payload["text"]);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", payload["embed_text"]);
        Assert.Equal("contextual_text_v1", payload["embedding_basis"]);
        Assert.Equal(2, payload["section_ordinal"]);
        Assert.Equal(9, payload["unit_ordinal"]);
        Assert.Equal("Introduction", payload["section_title"]);
        Assert.Equal("Chapter 1 > Introduction", payload["heading_path"]);
        Assert.Equal("section_window_v1", payload["chunk_type"]);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", payload["prev_chunk_id"]);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", payload["next_chunk_id"]);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", payload["same_section_chunk_id"]);
        Assert.Equal(
            DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 6, 4).ToString(),
            payload["chunk_id"]);
    }

    [Fact]
    public void ParseSearchResults_reads_enriched_payload_without_breaking_text_snippet()
    {
        using var doc = JsonDocument.Parse("""
        {
          "result": [
            {
              "score": 0.91,
              "payload": {
                "doc_id": "doc-1",
                "doc_path": "ATEX/CEN.pdf",
                "doc_name": "CEN.pdf",
                "page_start": 2,
                "page_end": 3,
                "chunk_id": "chunk-1",
                "chunk_index": 8,
                "text": "Chunk snippet",
                "embed_text": "Document: CEN.pdf\nExcerpt:\nChunk snippet",
                "embedding_basis": "contextual_text_v1",
                "section_ordinal": 1,
                "unit_ordinal": 5,
                "chunk_type": "section_window_v1",
                "section_title": "Introduction",
                "heading_path": "Chapter 1 > Introduction",
                "prev_chunk_id": "prev-1",
                "next_chunk_id": "next-1",
                "same_section_chunk_id": "same-1",
                "ingestion_version": 4,
                "hash_doc": "deadbeef"
              }
            }
          ]
        }
        """);

        var match = Assert.Single(QdrantClient.ParseSearchResults(doc));

        Assert.Equal("Chunk snippet", match.Text);
        Assert.Equal("Document: CEN.pdf\nExcerpt:\nChunk snippet", match.EmbedText);
        Assert.Equal("contextual_text_v1", match.EmbeddingBasis);
        Assert.Equal(1, match.SectionOrdinal);
        Assert.Equal(5, match.UnitOrdinal);
        Assert.Equal("Introduction", match.SectionTitle);
        Assert.Equal("Chapter 1 > Introduction", match.HeadingPath);
        Assert.Equal("section_window_v1", match.ChunkType);
        Assert.Equal("prev-1", match.PrevChunkId);
        Assert.Equal("next-1", match.NextChunkId);
        Assert.Equal("same-1", match.SameSectionChunkId);
    }

    [Fact]
    public void BuildMatchDedupKey_deduplicates_exact_and_dense_results_for_same_excerpt()
    {
        var exact = new RagMatch(
            Score: 1.0,
            DocId: "doc-1",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "exact-1",
            ChunkIndex: 0,
            Text: "EN 15281",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "EN 15281",
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var dense = exact with
        {
            ChunkId = "dense-1",
            EmbeddingBasis = "contextual_text_v1"
        };

        Assert.Equal(
            RagEndpoints.BuildMatchDedupKey(exact),
            RagEndpoints.BuildMatchDedupKey(dense));
    }

    [Fact]
    public void BuildMatchDedupKey_deduplicates_dense_and_linked_results_for_same_excerpt()
    {
        var dense = new RagMatch(
            Score: 0.82,
            DocId: "doc-1",
            DocPath: "ATEX/CEN.pdf",
            DocName: "CEN.pdf",
            PageStart: 4,
            PageEnd: 4,
            ChunkId: "dense-1",
            ChunkIndex: 3,
            Text: "Safety instructions",
            IngestionVersion: 2,
            HashDoc: "hash",
            EmbedText: "Safety instructions",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 5,
            SectionTitle: "Safety",
            HeadingPath: "Chapter 1 > Safety",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);

        var linked = dense with
        {
            ChunkId = "linked-1",
            EmbeddingBasis = "linked_context_v1"
        };

        Assert.Equal(
            RagEndpoints.BuildMatchDedupKey(dense),
            RagEndpoints.BuildMatchDedupKey(linked));
    }

    [Fact]
    public void BuildChunkLinkMap_returns_prev_next_and_same_section_links()
    {
        var docId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(0, 1, 0, 1, 1, "a", 1, [1], "unit_exact_v1"),
            new ProjectedRetrievalChunk(1, 1, 1, 1, 1, "b", 1, [2], "unit_exact_v1"),
            new ProjectedRetrievalChunk(2, 2, 2, 2, 2, "c", 1, [3], "unit_exact_v1")
        };

        var map = IngestionWorker.BuildChunkLinkMap(docId, 3, chunks);

        Assert.Null(map[0].PreviousChunkId);
        Assert.NotNull(map[0].NextChunkId);
        Assert.NotNull(map[0].SameSectionChunkId);
        Assert.NotNull(map[1].PreviousChunkId);
        Assert.NotNull(map[1].NextChunkId);
        Assert.Null(map[1].SameSectionChunkId);
    }

    [Fact]
    public void FuseWithRrf_prioritizes_matches_supported_by_multiple_retrievers()
    {
        var exact = new RagMatch(1.0, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "exact-a", 0, "EN 15281", 1, "hash-a", "EN 15281", "exact_match_v1", null, null, "Safety", "Safety", "exact_match_entry", null, null, null);
        var sparse = new RagMatch(0.70, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-a", 0, "EN 15281", 1, "hash-a", "Document: CEN.pdf\nExcerpt:\nEN 15281 guidance", "sparse_bm25_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var denseForA = new RagMatch(0.64, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "dense-a", 0, "EN 15281", 1, "hash-a", "Document: CEN.pdf\nExcerpt:\nEN 15281 guidance", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var denseForB = new RagMatch(0.80, "doc-b", "General/Other.pdf", "Other.pdf", 1, 1, "dense-b", 0, "General guidance", 1, "hash-b", "Document: Other.pdf\nExcerpt:\nGeneral guidance", "contextual_text_v1", 1, 1, "General", "General", "unit_exact_v1", null, null, null);

        var fused = RagEndpoints.FuseWithRrf([exact], [sparse], [denseForB, denseForA]);

        Assert.Equal("doc-a", fused[0].DocId);
        Assert.Equal("exact_match_v1", fused[0].EmbeddingBasis);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_sparse_for_high_overlap_lexical_query()
    {
        var sparse = new RagMatch(0.74, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-1", 0, "Inerting safety controls and gas flow monitoring requirements", 1, "hash-a", "Inerting safety controls and gas flow monitoring requirements", "sparse_bm25_v1", 1, 1, "Inerting", "Chapter 2 > Inerting", "unit_exact_v1", null, null, null);
        var dense = new RagMatch(0.76, "doc-b", "ATEX/General.pdf", "General.pdf", 1, 1, "dense-1", 0, "General safety guidance overview", 1, "hash-b", "General safety guidance overview", "contextual_text_v1", 1, 1, "Overview", "Chapter 1 > Overview", "section_window_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Quels documents parlent d inerting safety controls en zone ATEX ?", [dense, sparse]);

        Assert.Equal("sparse-1", calibrated[0].ChunkId);
        Assert.Equal("sparse_bm25_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_exact_reference_above_dense_for_reference_query()
    {
        var exact = new RagMatch(0.96, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "exact-1", -1, "EN 15281", 1, "hash-a", "EN 15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var dense = new RagMatch(0.98, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", 3, 3, "dense-1", 1, "Maintenance guidance around EN 15281", 1, "hash-a", "Maintenance guidance around EN 15281", "contextual_text_v1", 1, 1, "Maintenance", "Chapter 3 > Maintenance", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Ou trouve-t-on EN 15281 ?", [dense, exact]);

        Assert.Equal("exact-1", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_numeric_exact_reference_above_unrelated_dense_noise()
    {
        var exact = new RagMatch(0.965, "doc-a", "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf", "CEN TR 15281 2006 Guidance on inerting.pdf", null, null, "exact-15281", -1, "15281", 1, "hash-a", "15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var denseNoise = new RagMatch(0.985, "doc-b", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 2, 2, "dense-15281-noise", 0, "general maintenance guidance around standard references", 1, "hash-b", "general maintenance guidance around standard references", "contextual_text_v1", 1, 1, "Integration", "Chapter 2 > Integration", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Ou trouve-t-on 15281 ?", [denseNoise, exact]);

        Assert.Equal("exact-15281", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
        Assert.Contains("15281", $"{calibrated[0].DocName} {calibrated[0].DocPath}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_sparse_noise_when_lexical_overlap_is_low()
    {
        var sparseNoise = new RagMatch(0.79, "doc-a", "ATEX/Appendix.pdf", "Appendix.pdf", 1, 1, "sparse-noise", 0, "general safety appendix summary", 1, "hash-a", "general safety appendix summary", "sparse_bm25_v1", 1, 1, "Appendix", "Appendix > Summary", "unit_exact_v1", null, null, null);
        var denseRelevant = new RagMatch(0.77, "doc-b", "Maintenance/Consignation.pdf", "Consignation.pdf", 2, 2, "dense-relevant", 0, "procedure de consignation electrique et verrouillage", 1, "hash-b", "procedure de consignation electrique et verrouillage", "contextual_text_v1", 1, 1, "Procedure", "Chapter 2 > Procedure", "unit_exact_v1", null, null, null);

        var calibrated = RagEndpoints.CalibrateFusedMatches("Resume la procedure de consignation electrique.", [sparseNoise, denseRelevant]);

        Assert.Equal("dense-relevant", calibrated[0].ChunkId);
        Assert.Equal("contextual_text_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void ExtractLexicalQueryTokens_ignores_ui_noise_tokens_but_keeps_business_words()
    {
        var tokens = RagEndpoints.ExtractLexicalQueryTokens("stp je cherche le pdf inerting safety controls manual");

        Assert.Contains("inerting", tokens);
        Assert.Contains("safety", tokens);
        Assert.Contains("controls", tokens);
        Assert.DoesNotContain("pdf", tokens);
        Assert.DoesNotContain("stp", tokens);
        Assert.DoesNotContain("manual", tokens);
    }

    [Fact]
    public void RerankDenseMatches_prefers_structure_aware_chunks()
    {
        var broad = new RagMatch(0.50, "doc", "path", "doc.pdf", 1, 1, "a", 0, "text", 1, "hash", "embed", "chunk_text", 1, 1, "Intro", null, "legacy_word_window_v1", null, null, null);
        var precise = new RagMatch(0.49, "doc", "path", "doc.pdf", 1, 1, "b", 1, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Intro", "unit_exact_v1", null, null, null);

        var reranked = RagEndpoints.RerankDenseMatches([broad, precise]);

        Assert.Equal("b", reranked[0].ChunkId);
    }

    [Theory]
    [InlineData("same_section", 0.88)]
    [InlineData("next", 0.865)]
    [InlineData("prev", 0.86)]
    public void ComputeLinkedMatchScore_applies_expected_penalty(string linkType, double expected)
    {
        var score = RagEndpoints.ComputeLinkedMatchScore(0.90, linkType);

        Assert.Equal(expected, score, 3);
    }

    [Fact]
    public void ComputeLinkedMatchScore_applies_extra_penalty_when_expanding_from_linked_context()
    {
        var fromDense = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromLinked = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromLinked);
        Assert.Equal(0.865, fromLinked, 3);
    }

    [Fact]
    public void NormalizeSparseScore_returns_stable_monotonic_values()
    {
        Assert.Equal(0.0, RagEndpoints.NormalizeSparseScore(0.0));

        var lower = RagEndpoints.NormalizeSparseScore(0.01);
        var higher = RagEndpoints.NormalizeSparseScore(0.25);

        Assert.InRange(lower, 0.45, 0.92);
        Assert.InRange(higher, 0.45, 0.92);
        Assert.True(higher > lower);
    }

    [Fact]
    public void ResolveProvenance_marks_sparse_matches_explicitly()
    {
        var sparse = new RagMatch(0.72, "doc", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "chunk", 0, "inerting guidance", 1, "hash", "Document: CEN.pdf", "sparse_bm25_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        Assert.Equal("retriever:sparse_bm25", RagEndpoints.ResolveProvenance(sparse));
        Assert.Equal("sparse_bm25", RagEndpoints.BuildProvenanceInfo(sparse).Channel);
    }

    [Fact]
    public void ApplyRerankScores_promotes_highest_reranked_candidate_and_sets_rerank_score()
    {
        var first = new RagMatch(0.90, "doc-a", "ATEX/A.pdf", "A.pdf", 1, 1, "a", 0, "alpha", 1, "hash-a", "alpha", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var second = new RagMatch(0.70, "doc-b", "ATEX/B.pdf", "B.pdf", 1, 1, "b", 0, "beta", 1, "hash-b", "beta", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);
        var third = new RagMatch(0.60, "doc-c", "ATEX/C.pdf", "C.pdf", 1, 1, "c", 0, "gamma", 1, "hash-c", "gamma", "contextual_text_v1", 1, 1, "Safety", "Safety", "unit_exact_v1", null, null, null);

        var reranked = RagEndpoints.ApplyRerankScores(
            [first, second, third],
            [
                new TeiClient.RerankItem(1, 0.91),
                new TeiClient.RerankItem(0, 0.22)
            ],
            rerankedPrefixCount: 2);

        Assert.Equal("b", reranked[0].ChunkId);
        Assert.Equal(0.91, reranked[0].RerankScore);
        Assert.Equal("c", reranked[^1].ChunkId);
    }

    [Fact]
    public void NormalizeRerankScore_maps_range_to_zero_one()
    {
        Assert.Equal(0.0, RagEndpoints.NormalizeRerankScore(0.25, 0.25, 0.75), 3);
        Assert.Equal(1.0, RagEndpoints.NormalizeRerankScore(0.75, 0.25, 0.75), 3);
        Assert.Equal(0.5, RagEndpoints.NormalizeRerankScore(0.50, 0.25, 0.75), 3);
    }

    [Fact]
    public void ParseRerankResponse_supports_results_wrapper()
    {
        using var doc = JsonDocument.Parse("""
        {
          "results": [
            { "index": 1, "score": 0.91 },
            { "index": 0, "score": 0.22 }
          ]
        }
        """);

        var items = TeiClient.ParseRerankResponse(doc.RootElement);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Index);
        Assert.Equal(0.91, items[0].Score);
    }

    [Fact]
    public void ComputeLinkedMatchScore_keeps_exact_match_anchor_helpful_but_more_conservative_than_dense()
    {
        var fromDense = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "dense_qdrant");
        var fromExact = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "exact_match");
        var fromLinked = RagEndpoints.ComputeLinkedMatchScore(0.90, "same_section", "linked_context");

        Assert.True(fromDense > fromExact);
        Assert.True(fromExact > fromLinked);
        Assert.Equal(0.87, fromExact, 3);
    }

    [Fact]
    public void ComputeExactMatchScore_prioritizes_structured_references_over_plain_verbatim()
    {
        var standard = RagEndpoints.ComputeExactMatchScore("standard_ref", "en 15281", "EN 15281");
        var code = RagEndpoints.ComputeExactMatchScore("code_ref", "ind570", "IND570");
        var verbatim = RagEndpoints.ComputeExactMatchScore("verbatim_excerpt", "maintenance", "maintenance procedure");

        Assert.True(standard > code);
        Assert.True(code > verbatim);
    }

    [Fact]
    public void ComputeMetadataReferenceScore_prefers_direct_term_overlap_over_numeric_key_overlap()
    {
        var exactReference = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 1,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var keyBacked = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 1,
            genericDirectMatches: 0,
            keyMatches: 1);
        var numericOnly = RagEndpoints.ComputeMetadataReferenceScore(
            exactReferenceMatches: 0,
            keyBackedReferenceMatches: 0,
            genericDirectMatches: 0,
            keyMatches: 1);

        Assert.True(exactReference > keyBacked);
        Assert.True(keyBacked > numericOnly);
        Assert.True(numericOnly >= 0.95);
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_prefers_single_strong_reference_hit()
    {
        var exact = new RagMatch(
            0.98,
            "doc-1",
            "ATEX/CEN TR 15281 2006.pdf",
            "CEN TR 15281 2006.pdf",
            null,
            null,
            "docmeta:1",
            -1,
            "CEN TR 15281 2006.pdf [cen tr 15281 2006]",
            1,
            "hash",
            "CEN TR 15281 2006.pdf [cen tr 15281 2006]",
            "exact_match_v1",
            null,
            null,
            null,
            null,
            "document_metadata_ref",
            null,
            null,
            null);

        Assert.True(RagEndpoints.ShouldShortCircuitAfterExact([exact]));
    }

    [Fact]
    public void ShouldShortCircuitAfterExact_keeps_search_open_when_exact_results_are_ambiguous()
    {
        var top = new RagMatch(0.98, "doc-1", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "docmeta:1", -1, "CEN TR 15281 2006.pdf", 1, "hash1", "CEN TR 15281 2006.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var second = new RagMatch(0.955, "doc-2", "ATEX/Other 15281.pdf", "Other 15281.pdf", null, null, "docmeta:2", -1, "Other 15281.pdf", 1, "hash2", "Other 15281.pdf", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);

        Assert.False(RagEndpoints.ShouldShortCircuitAfterExact([top, second]));
    }

    [Fact]
    public void ComputeDataHash_is_stable_and_sensitive_to_match_changes()
    {
        var left = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };
        var right = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "contextual_text_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };
        var changed = new[]
        {
            new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null)
        };

        var leftHash = RagEndpoints.ComputeDataHash(left);
        var rightHash = RagEndpoints.ComputeDataHash(right);
        var changedHash = RagEndpoints.ComputeDataHash(changed);

        Assert.Equal(leftHash, rightHash);
        Assert.NotEqual(leftHash, changedHash);
    }

    [Fact]
    public void ResolveProvenance_returns_explicit_retriever_prefixed_label()
    {
        var match = new RagMatch(0.9, "doc", "path", "doc.pdf", 1, 1, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null);

        var provenance = RagEndpoints.ResolveProvenance(match);

        Assert.Equal("retriever:linked_context", provenance);
    }

    [Fact]
    public void BuildProvenanceInfo_returns_structured_retrieval_metadata()
    {
        var match = new RagMatch(0.9, "doc", "path", "doc.pdf", 4, 5, "chunk-1", 0, "text", 1, "hash", "embed", "linked_context_v1", 1, 1, "Intro", "Chapter 1 > Intro", "unit_exact_v1", null, null, null);

        var info = RagEndpoints.BuildProvenanceInfo(match);

        Assert.Equal("linked_context", info.Channel);
        Assert.Equal("retriever:linked_context", info.Label);
        Assert.Equal("hash", info.SourceHash);
        Assert.Equal("chunk-1", info.ChunkId);
        Assert.Equal(4, info.PageStart);
        Assert.Equal(5, info.PageEnd);
        Assert.Null(info.OffsetStart);
        Assert.Null(info.OffsetEnd);
    }

    [Fact]
    public void BuildDocumentCategoryPath_and_category_are_derived_from_doc_path()
    {
        Assert.Equal("ATEX/Guidance", RagEndpoints.BuildDocumentCategoryPath("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Equal("atex", RagEndpoints.BuildDocumentCategory("ATEX/Guidance/CEN TR 15281.pdf"));
        Assert.Null(RagEndpoints.BuildDocumentCategoryPath("root-level.pdf"));
        Assert.Equal("rootlevel", RagEndpoints.BuildDocumentCategory("RootLevel"));
    }

    [Fact]
    public void ResolveCategoryRef_uses_top_level_category_path()
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ATEX"] = "cat_001",
            ["Programmation"] = "cat_002"
        };

        Assert.Equal("cat_001", RagEndpoints.ResolveCategoryRef("ATEX/Guidance", refs));
        Assert.Equal("cat_002", RagEndpoints.ResolveCategoryRef("Programmation/Mettler", refs));
        Assert.Null(RagEndpoints.ResolveCategoryRef("General", refs));
        Assert.Null(RagEndpoints.ResolveCategoryRef(null, refs));
    }

    [Fact]
    public void BuildContextInfo_returns_structured_chunk_context()
    {
        var match = new RagMatch(
            0.9,
            "doc",
            "path",
            "doc.pdf",
            1,
            1,
            "chunk-1",
            0,
            "text",
            1,
            "hash",
            "embed",
            "contextual_text_v1",
            1,
            1,
            "Safety",
            "Chapter 1 > Safety",
            "unit_exact_v1",
            "prev-1",
            "next-1",
            "same-1");

        var context = RagEndpoints.BuildContextInfo(match);

        Assert.Equal("unit_exact_v1", context.ChunkType);
        Assert.Equal("Safety", context.SectionTitle);
        Assert.Equal("Chapter 1 > Safety", context.HeadingPath);
        Assert.Equal("prev-1", context.PrevChunkId);
        Assert.Equal("next-1", context.NextChunkId);
        Assert.Equal("same-1", context.SameSectionChunkId);
    }

    [Fact]
    public void ExtractMatchedDocHints_derives_generic_reference_and_alpha_hints()
    {
        var matches = new[]
        {
            new RagMatch(0.9, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 1, "chunk-1", 0, "functional safety handbook", 1, "hash-1", "functional safety handbook", "exact_match_v1", 1, 1, "Safety", "Safety", "document_metadata_ref", null, null, null),
            new RagMatch(0.9, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 1, 1, "chunk-2", 0, "fieldbus integration guide", 1, "hash-2", "fieldbus integration guide", "exact_match_v1", 1, 1, "PLC", "PLC", "document_metadata_ref", null, null, null),
            new RagMatch(0.7, "doc-3", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 1, 1, "chunk-3", 0, "agreement document", 1, "hash-3", "agreement document", "contextual_text_v1", 1, 1, "General", "General", "unit_exact_v1", null, null, null)
        };

        var hints = RagEndpoints.ExtractMatchedDocHints(matches);

        Assert.Contains("61511", hints);
        Assert.Contains("XR200", hints);
        Assert.Contains("ACCORD", hints);
        Assert.DoesNotContain("GUIDE", hints);
    }

    [Fact]
    public void BuildAnswerGuidance_uses_generic_domain_context_for_qualification()
    {
        var matches = new[]
        {
            new RagMatch(0.92, "doc-1", "Safety/IEC 61511 burner management handbook.pdf", "IEC 61511 burner management handbook.pdf", 1, 2, "chunk-1", 0, "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", 1, "hash-1", "Inert gas selection depends on oxygen concentration, purge sequence and explosion prevention constraints.", "contextual_text_v1", 1, 1, "Process safety", "Process safety", "unit_exact_v1", null, null, null),
            new RagMatch(0.89, "doc-2", "Controls/XR 200 fieldbus commissioning guide.pdf", "XR 200 fieldbus commissioning guide.pdf", 3, 4, "chunk-2", 1, "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", 1, "hash-2", "PLC integration covers PROFINET, Modbus, tare commands and target tolerances for the terminal.", "contextual_text_v1", 2, 2, "PLC integration", "PLC integration", "unit_exact_v1", null, null, null)
        };

        var guidance = RagEndpoints.BuildAnswerGuidance(
            "J'ai une discussion avec un client qui me demande si notre projet est conforme a cette norme, tu peux m'aider a repondre ?",
            matches);

        Assert.Equal("answer_with_caveat", guidance.Behavior);
        Assert.NotNull(guidance.QualificationNote);
        Assert.Contains("61511", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XR200", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("perimetre", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contexte projet", guidance.QualificationNote!, StringComparison.OrdinalIgnoreCase);
    }

}
