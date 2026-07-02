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
    public void Extract_rejects_header_footer_measure_and_body_fragments_as_section_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "18105 VEN Fiche-base 11/12/07 16:44 Page 1\n"
                + "505 kcal, 7 g G, 43 g L, 21 g P\n"
                + "i s s u e r\n"
                + "i Servez-vous du papier lorsque c'est possible\n"
                + "Pressure Envelope Validation\n"
                + "The validation procedure is reviewed before startup.",
                35,
                240,
                [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "Pressure Envelope Validation", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.Contains("Page 1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sections, section => section.Title.Contains("kcal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sections, section => section.Title.Contains("s s u", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sections, section => section.Title.StartsWith("i Servez", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_rejects_short_list_value_fragments_as_section_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "1 Introduction\n"
                + "Valid opening content.\n"
                + "5 elements techniques,\n"
                + "Delicious, Gala)\n"
                + "3 modules, verifies\n"
                + "Operational Follow Up\n"
                + "The real follow up content is preserved.",
                24,
                180,
                [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "1 Introduction", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "Operational Follow Up", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "5 elements techniques,", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "Delicious, Gala)", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "3 modules, verifies", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_rejects_short_ocr_layout_fragments_as_section_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Operational Catalog\n"
                + "The operating catalogue introduces the validation scope.\n"
                + "I gasket shim\n"
                + "ER Medium cost\n"
                + "Q Intermediate\n"
                + "Glissez-y un Ca\n"
                + "9 Duration : 30 minutes\n"
                + "CONTROL HANDOVER PLAN\n"
                + "The handover plan remains the real section.",
                38,
                280,
                [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "Operational Catalog", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "I gasket shim", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "ER Medium cost", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "Q Intermediate", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "Glissez-y un Ca", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "9 Duration : 30 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_rejects_numeric_quantity_and_legend_fragments_as_section_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "1 Introduction\n"
                + "Valid opening content.\n"
                + "14 - 17\n"
                + "4 5\n"
                + "2 units inspected\n"
                + "10 min\n"
                + "AB : Primary Valve | CD : Secondary Valve\n"
                + "Fluid, pressure,\n"
                + "CONTROL HANDOVER PLAN\n"
                + "The handover plan remains the real section.",
                34,
                260,
                [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "1 Introduction", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "14 - 17", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "4 5", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "2 units inspected", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "10 min", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "AB : Primary Valve | CD : Secondary Valve", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "Fluid, pressure,", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_filters_dense_layout_step_fragments_while_keeping_strong_sections()
    {
        var pages = Enumerable.Range(1, 10)
            .Select(page => new ExtractedPdfPage(
                page,
                "CONTROL MODULE " + page + "\n"
                + $"1 Inspect the panel and record the pressure value before release page {page}\n"
                + $"2 Replace the cover and validate the indicator during startup page {page}\n"
                + $"3 Add the signed checklist to the release file after inspection page {page}\n"
                + $"{page + 1} units available\n"
                + $"{page + 10} - {page + 17}\n"
                + "SAFETY HANDOVER PLAN " + page + "\n"
                + "The real handover plan remains available for retrieval.",
                84,
                420,
                [(byte)page]))
            .ToArray();

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => section.Title.StartsWith("CONTROL MODULE", StringComparison.Ordinal));
        Assert.Contains(sections, section => section.Title.StartsWith("SAFETY HANDOVER PLAN", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.StartsWith("1 Inspect", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.StartsWith("2 Replace", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.StartsWith("3 Add", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.Contains("units available", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => section.Title.Contains(" - ", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_merges_adjacent_connector_heading_lines()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "AND SAFETY LIMITS\n"
            + "The section body explains the operating limits.\n"
            + "SECOND CONTROL MATRIX\n"
            + "Body for the second section.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 24, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "PRIMARY CONTROL MATRIX AND SAFETY LIMITS", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "AND SAFETY LIMITS", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "SECOND CONTROL MATRIX", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_merges_adjacent_unmarked_heading_lines_when_structured_body_follows()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "VALIDATION LIMITS\n"
            + "10 units inspected\n"
            + "1 Inspect the actuator and record the pressure value.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 24, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        Assert.Equal("PRIMARY CONTROL MATRIX VALIDATION LIMITS", section.Title);
    }

    [Fact]
    public void Extract_keeps_adjacent_unmarked_headings_separate_without_structured_body_evidence()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "VALIDATION LIMITS\n"
            + "The next paragraph explains why the limits matter for operators.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 20, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "PRIMARY CONTROL MATRIX", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "VALIDATION LIMITS", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "PRIMARY CONTROL MATRIX VALIDATION LIMITS", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_detects_contextual_mixed_case_heading_after_structured_metadata()
    {
        const string text =
            "Preparation : 1 hour\n"
            + "Duration : 45 minutes\n"
            + "Medium\n"
            + "Regional control matrix\n"
            + "\u2022 6 components inspected before startup\n"
            + "\u2022 Record the result in the validation log.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 26, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        Assert.Equal("Regional control matrix", section.Title);
    }

    [Fact]
    public void Extract_merges_contextual_mixed_case_heading_split_before_structured_body()
    {
        const string text =
            "Preparation : 1 hour\n"
            + "Duration : 45 minutes\n"
            + "Medium\n"
            + "Regional control\n"
            + "Matrix alpha\n"
            + "\u2022 6 components inspected before startup\n"
            + "\u2022 Record the result in the validation log.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 28, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        Assert.Equal("Regional control Matrix alpha", section.Title);
    }

    [Fact]
    public void Extract_rejects_single_uppercase_token_embedded_between_dense_value_lines()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "2 kg primary compound\n"
            + "ACME\n"
            + "300 ml secondary carrier\n"
            + "Apply the material and record the result.\n"
            + "SECOND CONTROL MATRIX\n"
            + "Second section body.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 32, text.Length, [1])
        };

        var sections = DocumentSectionExtractor.Extract(pages);

        Assert.Contains(sections, section => string.Equals(section.Title, "PRIMARY CONTROL MATRIX", StringComparison.Ordinal));
        Assert.Contains(sections, section => string.Equals(section.Title, "SECOND CONTROL MATRIX", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, section => string.Equals(section.Title, "ACME", StringComparison.Ordinal));
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
