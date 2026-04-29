using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentUnitExtractorTests
{
    [Fact]
    public void Extract_builds_units_from_page_paragraphs_and_links_to_section_by_page_range()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction\n\nPremier paragraphe.\n\nDeuxieme paragraphe.", 6, 56, [1]),
            new ExtractedPdfPage(2, "Suite du contenu.", 3, 17, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "1 Introduction", 1, 1, 2, 1, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.True(units.Count >= 2);
        Assert.All(units, u => Assert.Equal(0, u.SectionOrdinal));
        Assert.Contains(units, u => u.Text.Contains("Premier paragraphe.", StringComparison.Ordinal));
        Assert.Equal(0, units[0].OffsetStart);
        Assert.True(units[0].OffsetEnd > units[0].OffsetStart);
        Assert.True(units[1].OffsetStart > units[0].OffsetEnd);
    }

    [Fact]
    public void Extract_skips_section_titles_and_falls_back_to_whole_document_when_needed()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "ANNEXE", 1, 6, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "ANNEXE", 1, 1, 1, 1, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        var unit = Assert.Single(units);
        Assert.Equal(0, unit.Ordinal);
        Assert.Equal(1, unit.PageStart);
        Assert.Equal(0, unit.OffsetStart);
        Assert.Equal(unit.Text.Length, unit.OffsetEnd);
    }

    [Fact]
    public void Pdf_text_sanitizer_replaces_nul_bytes_before_storage()
    {
        var sanitized = PdfTextSanitizer.ForStorage("Sauce\0tomate");

        Assert.Equal("Sauce tomate", sanitized);
        Assert.DoesNotContain('\0', sanitized);
    }

    [Fact]
    public void Stable_unit_id_is_deterministic_for_same_revision_and_ordinal()
    {
        var revisionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var left = DocumentFoundationRepo.BuildStableUnitId(revisionId, 4);
        var right = DocumentFoundationRepo.BuildStableUnitId(revisionId, 4);

        Assert.Equal(left, right);
    }
}
