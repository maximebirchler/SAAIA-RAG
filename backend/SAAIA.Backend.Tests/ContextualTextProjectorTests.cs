using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ContextualTextProjectorTests
{
    [Fact]
    public void Project_builds_contextual_text_with_document_section_pages_and_excerpt()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Chapter 1", 1, 1, 1, null, null),
            new ExtractedDocumentSection(1, "Introduction", 2, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 1, 1, 1, "Contexte précédent.", 18, 2, [1]),
            new ExtractedDocumentUnit(1, 1, 1, 1, "Contexte du paragraphe introductif.", 34, 4, [2]),
            new ExtractedDocumentUnit(2, 1, 1, 1, "Contexte suivant.", 17, 2, [3])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                1,
                1,
                1,
                1,
                "Extrait principal",
                2,
                [4],
                "unit_exact_v1",
                SourceUnitOrdinals: [1],
                SourceUnitStartOrdinal: 1,
                SourceUnitEndOrdinal: 1,
                SourceUnitCount: 1,
                ChunkComposition: "single_unit")
        };

        var entries = ContextualTextProjector.Project("ATEX/CEN.pdf", sections, units, chunks);

        var entry = Assert.Single(entries);
        Assert.Contains("document_name: CEN.pdf", entry.Text, StringComparison.Ordinal);
        Assert.Contains("section_title: Introduction", entry.Text, StringComparison.Ordinal);
        Assert.Contains("heading_path: Chapter 1 > Introduction", entry.Text, StringComparison.Ordinal);
        Assert.Contains("chunk_type: unit_exact_v1", entry.Text, StringComparison.Ordinal);
        Assert.Contains("pages: 1", entry.Text, StringComparison.Ordinal);
        Assert.Contains("context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("previous_context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("next_context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("excerpt:", entry.Text, StringComparison.Ordinal);
        Assert.True(
            entry.Text.IndexOf("excerpt:", StringComparison.Ordinal) <
            entry.Text.IndexOf("context:", StringComparison.Ordinal));
        Assert.DoesNotContain("Document:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Section:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Excerpt:", entry.Text, StringComparison.Ordinal);
        Assert.Equal("contextual_text_v2", entry.SchemaVersion);
        Assert.Equal("Introduction", entry.SectionTitle);
        Assert.Equal("Chapter 1 > Introduction", entry.HeadingPath);
        Assert.Equal("unit_exact_v1", entry.ChunkType);
        Assert.Equal("content", entry.ContentRole);
        Assert.Equal([1], entry.SourceUnitOrdinals);
        Assert.Equal(1, entry.SourceUnitStartOrdinal);
        Assert.Equal(1, entry.SourceUnitEndOrdinal);
        Assert.Equal(1, entry.SourceUnitCount);
        Assert.Equal("single_unit", entry.ChunkComposition);
        Assert.True(entry.IncludesCurrentUnitContext);
        Assert.True(entry.IncludesPreviousContext);
        Assert.True(entry.IncludesNextContext);
    }

    [Fact]
    public void Project_returns_empty_when_no_retrieval_chunks_are_available()
    {
        var entries = ContextualTextProjector.Project("doc.pdf", [], [], []);

        Assert.Empty(entries);
    }

    [Fact]
    public void Project_omits_navigation_neighbors_from_embedding_context()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18",
                14,
                12,
                [1]),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "Lockout procedure Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                108,
                15,
                [2]),
            new ExtractedDocumentUnit(
                2,
                0,
                3,
                3,
                "Índice Seguridad 3 Procedimiento de bloqueo 8 Mantenimiento 12 Anexos 18",
                74,
                10,
                [3])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                1,
                2,
                2,
                "Lockout procedure Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                15,
                [4],
                "unit_exact_v1")
        };

        var entry = Assert.Single(ContextualTextProjector.Project("Ops/Manual.pdf", sections, units, chunks));

        Assert.DoesNotContain("previous_context:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("next_context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("excerpt:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Lockout procedure", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_omits_low_quality_sparse_neighbors_from_embedding_context()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 3, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "FALSE SPARSE LABEL",
                18,
                3,
                [1],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"]),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "Commissioning procedure requires isolation, verification, sign-off, and documented acceptance evidence.",
                100,
                10,
                [2]),
            new ExtractedDocumentUnit(
                2,
                0,
                3,
                3,
                "Follow-up context explains when the verification record must be reviewed by operations.",
                86,
                11,
                [3])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                1,
                2,
                2,
                "Commissioning procedure requires isolation, verification, sign-off, and documented acceptance evidence.",
                10,
                [4],
                "unit_exact_v1")
        };

        var entry = Assert.Single(ContextualTextProjector.Project("Ops/Manual.pdf", sections, units, chunks));

        Assert.DoesNotContain("previous_context:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE SPARSE LABEL", entry.Text, StringComparison.Ordinal);
        Assert.Contains("next_context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("verification record", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_puts_exact_excerpt_first_and_omits_duplicate_unit_context()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "ALPHA BETA MODULE Materials 2 units 4 bolts. Procedure 1. Isolate. 2. Calibrate.",
                79,
                12,
                [1])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "ALPHA BETA MODULE Materials 2 units 4 bolts. Procedure 1. Isolate. 2. Calibrate.",
                12,
                [2],
                "unit_exact_v1")
        };

        var entry = Assert.Single(ContextualTextProjector.Project("Ops/Manual.pdf", sections, units, chunks));

        Assert.Contains("excerpt:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("context:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("ALPHA BETA MODULE", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_omits_low_quality_current_unit_from_embedding_context()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "FALSE SPARSE LABEL extracted from a poor page",
                45,
                8,
                [1],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                0,
                0,
                0,
                1,
                1,
                "Clean excerpt chosen for diagnostics only.",
                6,
                [2],
                "section_window_v1")
        };

        var entry = Assert.Single(ContextualTextProjector.Project("Ops/Manual.pdf", sections, units, chunks));

        Assert.Contains("excerpt:", entry.Text, StringComparison.Ordinal);
        Assert.Contains("Clean excerpt", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("context:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE SPARSE LABEL", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Stable_contextual_text_entry_id_is_deterministic_for_same_revision_and_index()
    {
        var revisionId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");

        var left = DocumentFoundationRepo.BuildStableContextualTextEntryId(revisionId, 3);
        var right = DocumentFoundationRepo.BuildStableContextualTextEntryId(revisionId, 3);

        Assert.Equal(left, right);
    }

    [Fact]
    public void BuildHeadingPathMap_builds_hierarchical_paths_from_section_levels()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Chapter 1", 1, 1, 1, null, null),
            new ExtractedDocumentSection(1, "Introduction", 2, 1, 1, null, null),
            new ExtractedDocumentSection(2, "Safety", 2, 2, 2, null, null)
        };

        var paths = ContextualTextProjector.BuildHeadingPathMap(sections);

        Assert.Equal("Chapter 1", paths[0]);
        Assert.Equal("Chapter 1 > Introduction", paths[1]);
        Assert.Equal("Chapter 1 > Safety", paths[2]);
    }
}
