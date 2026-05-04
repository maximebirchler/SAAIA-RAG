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
    public void Extract_splits_dense_structured_pages_at_probable_item_boundaries()
    {
        var previousBlock = string.Join(' ', Enumerable.Repeat(
            "Faites mijoter doucement en remuant et servez bien chaud.",
            16));
        var text = previousBlock
            + " Dégustez le lendemain !Cassoulet toulousainPour 4 personnes"
            + " • 500 g de haricots blancs • 4 saucisses de Toulouse Préparation Faites cuire longuement."
            + " Intermédiaire13Paëlla mixtePour 8 personnes • 500 g de riz • 20 crevettes Préparation Couvrez et laissez cuire.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 120, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.True(units.Count >= 3);
        Assert.Contains(units, unit => unit.Text.StartsWith("Cassoulet toulousain", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.StartsWith("13Paëlla mixte", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("Dégustez le lendemain", StringComparison.Ordinal)
            && unit.Text.Contains("Cassoulet toulousain", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_short_mixed_structured_paragraphs_when_title_is_embedded()
    {
        const string text =
            "Nappez avec la preparation et enfournez pendant 30 minutes. Servez tiede !"
            + "Quiche lorraine traditionnellePour 4 personnes• 200 g de farine• 8 oeufs poivrePreparationAstuce Enfournez.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 24, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit => unit.Text.StartsWith("Quiche lorraine traditionnelle", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("Servez tiede", StringComparison.Ordinal)
            && unit.Text.Contains("Quiche lorraine", StringComparison.Ordinal));
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
