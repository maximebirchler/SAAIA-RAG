using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalChunkProjectorTests
{
    [Fact]
    public void Project_maps_chunk_to_best_overlapping_section_and_unit()
    {
        var chunks = new[]
        {
            new Chunk(0, 1, 1, "hello world"),
            new Chunk(1, 2, 3, "next chunk")
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Intro", 1, 1, 1, null, null),
            new ExtractedDocumentSection(1, "Body", 1, 2, 3, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "hello world", 11, 2, [1], 0, 11),
            new ExtractedDocumentUnit(1, 1, 2, 2, "next chunk", 10, 2, [2], 13, 23)
        };

        var projected = RetrievalChunkProjector.Project(chunks, sections, units);

        Assert.Equal(2, projected.Count);
        Assert.Equal(0, projected[0].SectionOrdinal);
        Assert.Equal(0, projected[0].UnitOrdinal);
        Assert.Equal(0, projected[0].OffsetStart);
        Assert.Equal(11, projected[0].OffsetEnd);
        Assert.Equal(1, projected[1].SectionOrdinal);
        Assert.Equal(1, projected[1].UnitOrdinal);
        Assert.Equal(13, projected[1].OffsetStart);
        Assert.Equal(23, projected[1].OffsetEnd);
    }

    [Fact]
    public void Project_returns_empty_when_no_chunks_are_provided()
    {
        var projected = RetrievalChunkProjector.Project([], [], []);

        Assert.Empty(projected);
    }

    [Fact]
    public void Stable_retrieval_chunk_id_is_deterministic_for_same_doc_version_and_chunk_index()
    {
        var docId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        var left = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 7);
        var right = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 7);

        Assert.Equal(left, right);
    }

    [Fact]
    public void ProjectStructureAware_keeps_chunks_inside_section_boundaries()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Intro", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Body", 1, 2, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta gamma", 16, 3, [1], 0, 16),
            new ExtractedDocumentUnit(1, 0, 1, 1, "delta epsilon zeta", 18, 3, [2], 20, 38),
            new ExtractedDocumentUnit(2, 1, 2, 2, "theta iota kappa", 16, 3, [3], 42, 58),
            new ExtractedDocumentUnit(3, 1, 2, 2, "lambda mu nu", 12, 3, [4], 62, 74)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 5,
            overlapWords: 1,
            minWords: 1);

        Assert.Equal(4, projected.Count);
        Assert.All(projected.Take(2), chunk => Assert.Equal(0, chunk.SectionOrdinal));
        Assert.All(projected.Skip(2), chunk => Assert.Equal(1, chunk.SectionOrdinal));
        Assert.All(projected, chunk => Assert.NotEqual("legacy_word_window_v1", chunk.ChunkType));
        Assert.All(projected, chunk => Assert.True(chunk.OffsetEnd > chunk.OffsetStart));
    }

    [Fact]
    public void ProjectStructureAware_adds_exact_chunks_for_short_high_signal_units()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 2, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Reference list http://example.test http://example-two.test",
                57,
                5,
                [1],
                0,
                57),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "SAUCE BECHAMEL DE BASE Ingredients beurre farine lait Preparation 1. Faire fondre le beurre. 2. Ajouter la farine et fouetter.",
                125,
                18,
                [2],
                61,
                186)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 200,
            overlapWords: 0,
            minWords: 80);

        Assert.Contains(projected, chunk =>
            chunk.ChunkType == "unit_exact_v1"
            && chunk.UnitOrdinal == 1
            && chunk.Text.StartsWith("SAUCE BECHAMEL", StringComparison.Ordinal));
        Assert.DoesNotContain(projected, chunk =>
            chunk.ChunkType == "unit_exact_v1"
            && chunk.UnitOrdinal == 0);
    }
}
