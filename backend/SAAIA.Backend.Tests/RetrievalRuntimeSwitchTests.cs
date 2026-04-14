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

}
