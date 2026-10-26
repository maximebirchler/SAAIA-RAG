using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class QdrantClientTests
{
    [Fact]
    public void ParseSearchResults_preserves_chunk_extraction_quality_payload_fields()
    {
        using var doc = JsonDocument.Parse("""
        {
          "result": [
            {
              "score": 0.91,
              "payload": {
                "doc_id": "doc-1",
                "doc_path": "Ops/Scanned.pdf",
                "doc_name": "Scanned.pdf",
                "page_start": 2,
                "page_end": 2,
                "chunk_id": "chunk-1",
                "chunk_index": 8,
                "text": "Chunk snippet",
                "embed_text": "Document: Scanned.pdf\nExcerpt:\nChunk snippet",
                "embedding_basis": "contextual_text_v1",
                "embedding_model": "intfloat/multilingual-e5-base",
                "embedding_input_format": "e5_passage_v1",
                "extraction_text_status": "low_text",
                "extraction_text_sparse": true,
                "extraction_ocr_candidate": true,
                "source_unit_ordinals": [2, 3, 3, "4", "bad"],
                "source_unit_start_ordinal": 2,
                "source_unit_end_ordinal": 4,
                "source_unit_count": 3,
                "chunk_composition": "multi_unit_window",
                "extraction_quality_signals": [
                  " sparse_text_on_page ",
                  "ocr_candidate_text",
                  "sparse_text_on_page",
                  12
                ]
              }
            }
          ]
        }
        """);

        var match = Assert.Single(QdrantClient.ParseSearchResults(doc));

        Assert.Equal("low_text", match.ExtractionTextStatus);
        Assert.True(match.ExtractionTextSparse);
        Assert.True(match.ExtractionOcrCandidate);
        Assert.Equal(["sparse_text_on_page", "ocr_candidate_text"], match.ExtractionQualitySignals);
        Assert.Equal([2, 3, 4], match.SourceUnitOrdinals);
        Assert.Equal(2, match.SourceUnitStartOrdinal);
        Assert.Equal(4, match.SourceUnitEndOrdinal);
        Assert.Equal(3, match.SourceUnitCount);
        Assert.Equal("multi_unit_window", match.ChunkComposition);
    }

}
