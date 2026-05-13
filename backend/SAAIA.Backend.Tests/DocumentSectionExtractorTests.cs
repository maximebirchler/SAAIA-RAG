using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentSectionExtractorTests
{
    [Fact]
    public void Extract_returns_fallback_document_section_when_no_heading_is_detected()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "texte simple sans titre special", 5, 32, [1, 2, 3]),
            new ExtractedPdfPage(2, "suite du texte simple", 4, 21, [4, 5, 6])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        Assert.Equal("Document", section.Title);
        Assert.Equal(1, section.PageStart);
        Assert.Equal(2, section.PageEnd);
    }

    [Fact]
    public void Extract_detects_numbered_headings_and_orders_sections()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "1 Introduction\nContenu de base", 4, 30, [1]),
            new ExtractedPdfPage(2, "2.1 Details\nSuite", 3, 18, [2]),
            new ExtractedPdfPage(3, "ANNEXE\nFin", 2, 10, [3])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Equal(3, sections.Count);
        Assert.Equal("1 Introduction", sections[0].Title);
        Assert.Equal(1, sections[0].Level);
        Assert.Equal("2.1 Details", sections[1].Title);
        Assert.Equal(2, sections[1].Level);
        Assert.Equal("ANNEXE", sections[2].Title);
        Assert.Equal(3, sections[2].PageStart);
    }

    [Fact]
    public void Extract_ignores_table_of_contents_and_index_lines_as_sections()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Sommaire\nIntroduction .... 3\nInstallation rapide 4\nConfiguration avancee 7\nExploitation 9",
                11,
                94,
                [1]),
            new ExtractedPdfPage(3, "1 Introduction\nContenu principal", 4, 32, [2]),
            new ExtractedPdfPage(4, "INSTALLATION RAPIDE\nEtapes de deploiement", 4, 43, [3])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "Sommaire", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.Contains("Configuration avancee", StringComparison.Ordinal));
        Assert.Equal("1 Introduction", sections[0].Title);
        Assert.Equal("INSTALLATION RAPIDE", sections[1].Title);
    }

    [Fact]
    public void Stable_section_id_is_deterministic_for_same_revision_and_ordinal()
    {
        var revisionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var left = DocumentFoundationRepo.BuildStableSectionId(revisionId, 2);
        var right = DocumentFoundationRepo.BuildStableSectionId(revisionId, 2);

        Assert.Equal(left, right);
    }
}
