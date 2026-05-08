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
            new ProjectedRetrievalChunk(0, 1, 1, 1, 1, "Extrait principal", 2, [4], "unit_exact_v1")
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
        Assert.DoesNotContain("Document:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Section:", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Excerpt:", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_returns_empty_when_no_retrieval_chunks_are_available()
    {
        var entries = ContextualTextProjector.Project("doc.pdf", [], [], []);

        Assert.Empty(entries);
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
