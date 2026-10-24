using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{

    private static RagMatch BuildDenseMatch(
        string? embeddingModel,
        string? embeddingInputFormat,
        string? chunkType = "unit_exact_v1",
        string? contentRole = null)
        => new(
            Score: 0.80,
            DocId: "doc-a",
            DocPath: "Docs/A.pdf",
            DocName: "A.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "chunk-a",
            ChunkIndex: 0,
            Text: "content",
            IngestionVersion: 1,
            HashDoc: "hash-a",
            EmbedText: "content",
            EmbeddingBasis: "contextual_text_v1",
            SectionOrdinal: 0,
            UnitOrdinal: 0,
            SectionTitle: "Section",
            HeadingPath: "Section",
            ChunkType: chunkType,
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: contentRole,
            EmbeddingModel: embeddingModel,
            EmbeddingInputFormat: embeddingInputFormat);

    private static RagMatch BuildRuntimeSelectionMatch(
        double score,
        string docId,
        string docPath,
        int chunkIndex,
        string text)
        => new(
            Score: score,
            DocId: docId,
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: chunkIndex + 1,
            PageEnd: chunkIndex + 1,
            ChunkId: $"chunk-{docId}",
            ChunkIndex: chunkIndex,
            Text: text,
            IngestionVersion: 1,
            HashDoc: $"hash-{docId}",
            EmbedText: text,
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: chunkIndex,
            UnitOrdinal: chunkIndex,
            SectionTitle: "Runtime",
            HeadingPath: "Runtime",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: RetrievalContentClassifier.ContentRole,
            ContentDensityScore: 1.0);

    private static RagMatch TestMatch(
        string text,
        string? embedText = null,
        string docPath = "Cuisine/Test.pdf",
        int page = 1,
        string chunkId = "chunk-1",
        string embeddingBasis = "sparse_bm25_v1",
        string chunkType = "unit_exact_v1",
        double score = 0.93,
        string? prevChunkId = null,
        string? contentRole = null,
        double? contentDensityScore = null)
        => new(
            Score: score,
            DocId: "doc-1",
            DocPath: docPath,
            DocName: Path.GetFileName(docPath),
            PageStart: page,
            PageEnd: page,
            ChunkId: chunkId,
            ChunkIndex: page,
            Text: text,
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: embedText ?? text,
            EmbeddingBasis: embeddingBasis,
            SectionOrdinal: 1,
            UnitOrdinal: page,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: chunkType,
            PrevChunkId: prevChunkId,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: contentRole,
            ContentDensityScore: contentDensityScore);
}
