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
    public void Extract_assigns_same_page_units_to_line_aware_sections_and_removes_heading_lines()
    {
        const string text =
            "1 Installation\n"
            + "\n"
            + "Installer le module et verifier les voyants.\n\n"
            + "2 Maintenance\n"
            + "\n"
            + "Nettoyer les filtres et consigner la date.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 16, text.Length, [1])
        };
        var sections = DocumentSectionExtractor.Extract(pages);

        var units = DocumentUnitExtractor.Extract(pages, sections);

        var installation = Assert.Single(units, unit => unit.Text.Contains("Installer le module", StringComparison.Ordinal));
        var maintenance = Assert.Single(units, unit => unit.Text.Contains("Nettoyer les filtres", StringComparison.Ordinal));
        Assert.Equal(sections.Single(section => section.Title == "1 Installation").Ordinal, installation.SectionOrdinal);
        Assert.Equal(sections.Single(section => section.Title == "2 Maintenance").Ordinal, maintenance.SectionOrdinal);
        Assert.DoesNotContain(units, unit => unit.Text.Contains("1 Installation", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("2 Maintenance", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_removes_component_lines_from_merged_section_titles()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "AND SAFETY LIMITS\n"
            + "Validate the control matrix and record the pressure limit.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 18, text.Length, [1])
        };
        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Equal("PRIMARY CONTROL MATRIX AND SAFETY LIMITS", section.Title);
        Assert.Equal(section.Ordinal, unit.SectionOrdinal);
        Assert.Contains("Validate the control matrix", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIMARY CONTROL MATRIX", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AND SAFETY LIMITS", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_removes_component_lines_from_unmarked_merged_section_titles()
    {
        const string text =
            "PRIMARY CONTROL MATRIX\n"
            + "VALIDATION LIMITS\n"
            + "10 units inspected\n"
            + "Inspect the actuator and record the pressure value.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 20, text.Length, [1])
        };
        var sections = DocumentSectionExtractor.Extract(pages);

        var section = Assert.Single(sections);
        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Equal("PRIMARY CONTROL MATRIX VALIDATION LIMITS", section.Title);
        Assert.Contains("10 units inspected", unit.Text, StringComparison.Ordinal);
        Assert.Contains("Inspect the actuator", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIMARY CONTROL MATRIX", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("VALIDATION LIMITS", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_repairs_narrow_ocr_table_artifacts_without_rewriting_standard_references()
    {
        const string text =
            "5:3.3 Technical reference No/Yes4 20\n\n"
            + "ISO 9001:2015 remains a dated normative reference.";
        var pages = new[]
        {
            new ExtractedPdfPage(8, text, 11, text.Length, [8])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 8, 8, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);
        var combined = string.Join(" ", units.Select(unit => unit.Text));

        Assert.Contains("5.3.3 Technical reference No/Yes 4 20", combined, StringComparison.Ordinal);
        Assert.Contains("ISO 9001:2015", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("5:3.3", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("No/Yes4", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_removes_component_lines_from_contextual_mixed_case_merged_section_titles()
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
        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Equal("Regional control Matrix alpha", section.Title);
        Assert.Contains("6 components inspected", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Regional control", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Matrix alpha", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_splits_long_structured_list_paragraphs_into_bounded_units()
    {
        var items = Enumerable.Range(1, 16)
            .Select(index =>
                $"\u2022 Step {index} validates the component, records the measurement, confirms the operator sign-off, and stores the evidence in the maintenance file.");
        var text = string.Join(' ', items);
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 260, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.True(units.Count >= 2);
        Assert.All(units, unit => Assert.True(unit.TokenCount <= 170, $"Unit {unit.Ordinal} had {unit.TokenCount} tokens."));
        Assert.Contains(units, unit => unit.Text.Contains("Step 1 validates", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("Step 16 validates", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_long_narrative_paragraphs_into_bounded_units()
    {
        var sentences = Enumerable.Range(1, 14)
            .Select(index =>
                $"Paragraph sentence {index} explains the obligation, preserves the supporting evidence, identifies the responsible party, and keeps the wording readable for retrieval.");
        var text = string.Join(' ', sentences);
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 280, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.True(units.Count >= 2);
        Assert.All(units, unit => Assert.True(unit.TokenCount <= 200, $"Unit {unit.Ordinal} had {unit.TokenCount} tokens."));
        Assert.Contains(units, unit => unit.Text.Contains("Paragraph sentence 1", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("Paragraph sentence 14", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_carries_page_extraction_quality_to_units()
    {
        var quality = new PdfPageExtractionQuality(
            "low_text",
            TextEmpty: false,
            TextSparse: true,
            OcrCandidate: true,
            AverageCharsPerWord: 4.5,
            Signals: ["sparse_text_on_page"]);
        var pages = new[]
        {
            new ExtractedPdfPage(1, "EN 15281", 2, 8, [1], Quality: quality)
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Equal("low_text", unit.ExtractionTextStatus);
        Assert.True(unit.ExtractionTextSparse);
        Assert.True(unit.ExtractionOcrCandidate);
        Assert.Contains("sparse_text_on_page", unit.ExtractionQualitySignals!);
    }

    [Fact]
    public void Extract_removes_repeated_short_layout_units_without_dropping_content()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "MENU\n\nLYCEE PROFESSIONNEL\n\nIngredients Preparation\n\nFirst procedure paragraph explains the setup and records the useful evidence.", 14, 128, [1]),
            new ExtractedPdfPage(2, "MENU\n\nLYCEE PROFESSIONNEL\n\nIngredients Preparation\n\nSecond procedure paragraph keeps a separate page-specific instruction.", 13, 120, [2]),
            new ExtractedPdfPage(3, "MENU\n\nLYCEE PROFESSIONNEL\n\nIngredients Preparation\n\nThird procedure paragraph preserves the useful operational text.", 12, 112, [3]),
            new ExtractedPdfPage(4, "MENU\n\nLYCEE PROFESSIONNEL\n\nIngredients Preparation\n\nFourth procedure paragraph closes the document with real content.", 12, 108, [4])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 4, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Equal(4, units.Count);
        Assert.DoesNotContain(units, unit => unit.Text.Equals("MENU", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(units, unit => unit.Text.Equals("LYCEE PROFESSIONNEL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(units, unit => unit.Text.Equals("Ingredients Preparation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(units, unit => unit.Text.StartsWith("First procedure paragraph", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.StartsWith("Fourth procedure paragraph", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, units.Count), units.Select(static unit => unit.Ordinal));
        Assert.Equal(0, units[0].OffsetStart);
        Assert.True(units[1].OffsetStart > units[0].OffsetEnd);
    }

    [Fact]
    public void Extract_rejoins_soft_hyphenated_words_after_repeated_layout_lines_are_removed()
    {
        const string legend = "Shaded text = Revisions, A = Text deletions and figure/table revisions. += Section deletions. N = New material.";
        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(
        [
            (1, $"Manual control of such opera-\n{legend}\ntions shall be by hold-to-run controls together with enabling control, where appropriate.", 0),
            (2, $"Inspection procedure keeps the useful para-\n{legend}\ngraph searchable for retrieval.", 0),
            (3, $"Validation evidence keeps the useful require-\n{legend}\nment searchable for retrieval.", 0),
            (4, $"Maintenance notes keep the useful docu-\n{legend}\nment searchable for retrieval.", 0)
        ]);
        var pages = cleaned
            .Select(page => new ExtractedPdfPage(
                page.PageNumber,
                page.Text,
                page.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                page.Text.Length,
                [(byte)page.PageNumber]))
            .ToArray();
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 4, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        var first = Assert.Single(units, unit => unit.PageStart == 1);
        Assert.Contains("operations shall be by hold-to-run controls", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("opera-", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("opera- tions", first.Text, StringComparison.Ordinal);
        Assert.False(first.Text.StartsWith("tions shall", StringComparison.Ordinal), first.Text);
        Assert.DoesNotContain("Shaded text = Revisions", string.Join(' ', units.Select(static unit => unit.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_removes_inline_revision_legend_and_repairs_interrupted_soft_hyphen()
    {
        const string text =
            "Manual control of such opera- Shaded text = Revisions. A = Text deletions and figure/table revisions. += Section deletions. N = New material. tions shall be by hold-to-run controls together with enabling control.";
        var pages = new[]
        {
            new ExtractedPdfPage(18, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [18])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 18, 18, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("operations shall be by hold-to-run controls", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Shaded text", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text deletions", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("opera-", unit.Text, StringComparison.Ordinal);
        Assert.False(unit.Text.StartsWith("tions shall", StringComparison.Ordinal), unit.Text);
    }

    [Fact]
    public void Extract_removes_accented_revision_legend_letter_ocr_variant()
    {
        const string text =
            "Block diagrams describe the control logic and interlocks. À = Text deletions and figure/table revisions.";
        var pages = new[]
        {
            new ExtractedPdfPage(39, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [39])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Annex", 1, 39, 39, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("Block diagrams describe the control logic and interlocks.", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text deletions", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("figure/table revisions", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_removes_digit_four_revision_legend_letter_ocr_variant()
    {
        const string text =
            "Control circuits remain available for safe machine stopping. 4 = Text deletions and figure/table revisions.";
        var pages = new[]
        {
            new ExtractedPdfPage(19, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [19])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Control circuits", 1, 19, 19, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("Control circuits remain available", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Text deletions", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("figure/table revisions", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_rejoins_soft_hyphenated_words_across_unit_boundaries()
    {
        const string text =
            "The document describes materials made from thermo-\n\n"
            + "plastischen Kunststoffen and lists the inspection evidence required for acceptance.";
        var pages = new[]
        {
            new ExtractedPdfPage(15, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [15])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inspection", 1, 15, 15, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("thermoplastischen Kunststoffen", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("thermo-", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_rejoins_soft_hyphenated_words_across_spurious_section_boundaries()
    {
        const string text =
            "The scan assigns this paragraph to a first section and ends with thermo-\n\n"
            + "plastischen Kunststoffen even though the OCR section detector split the continuation.";
        var pages = new[]
        {
            new ExtractedPdfPage(15, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [15])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "First OCR island", 1, 15, 15, 1, 1),
            new ExtractedDocumentSection(1, "Second OCR island", 1, 15, 15, 2, 2)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("thermoplastischen Kunststoffen", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("thermo-", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_preserves_hyphen_before_conjunction_at_line_or_unit_boundary()
    {
        const string text =
            "Safety-related software-\n"
            + "and firmware-based controls remain listed for the application.\n\n"
            + "Inspection-related mesure-\n"
            + "et controle entries remain separate terms for the checklist.\n\n"
            + "Documented carga-\n"
            + "y descarga steps remain listed in the operating manual.\n\n"
            + "Konstruktions-\n"
            + "und Berechnungsregeln remain separate terms in the scanned standard.\n\n"
            + "The same rule applies to load-\n\n"
            + "and speed-sensitive protective devices in the control circuit.";
        var pages = new[]
        {
            new ExtractedPdfPage(20, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [20])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Control circuits", 1, 20, 20, null, null)
        };

        var combined = string.Join(" ", DocumentUnitExtractor.Extract(pages, sections).Select(static unit => unit.Text));

        Assert.Contains("software- and firmware-based", combined, StringComparison.Ordinal);
        Assert.Contains("mesure- et controle", combined, StringComparison.Ordinal);
        Assert.Contains("carga- y descarga", combined, StringComparison.Ordinal);
        Assert.Contains("Konstruktions- und Berechnungsregeln", combined, StringComparison.Ordinal);
        Assert.Contains("load- and speed-sensitive", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("softwareand", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("mesureet", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("cargay", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Konstruktionsund", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("loadand", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_repairs_inline_short_prefix_soft_hyphenated_words()
    {
        const string text =
            "The OCR stream flattened a wrapped German word as ge- schweisst inside the same line. An autotransformer- type controller keeps the compound hyphen, while Konstruktions- und Berechnungsregeln keeps its semantic hyphen.";
        var pages = new[]
        {
            new ExtractedPdfPage(16, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [16])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Welding", 1, 16, 16, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("geschweisst inside the same line", unit.Text, StringComparison.Ordinal);
        Assert.Contains("autotransformer-type controller", unit.Text, StringComparison.Ordinal);
        Assert.Contains("Konstruktions- und Berechnungsregeln", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ge- schweisst", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("autotransformer- type", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("autotransformertype", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Konstruktionsund", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_removes_single_revision_legend_fragments_without_rewriting_prefixed_references()
    {
        const string text =
            "N7.2.10.1.11 Semiconductor fuses remain a numbered requirement.\n\n"
            + "Interlocked equipment precludes energization. Shaded text = Revisions.\n\n"
            + "Turns of flexible cables always remain on a drum. *= Section deletions. N = New material.\n\n"
            + "The supply has been disconnected shall be reduced to N = New material. 2024 Edition\n\n"
            + "Chains are held closed by captive screws. Shaded text = Revisions Jeleti text = Revisions.";
        var pages = new[]
        {
            new ExtractedPdfPage(17, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [17])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 17, 17, null, null)
        };

        var combined = string.Join(" ", DocumentUnitExtractor.Extract(pages, sections).Select(unit => unit.Text));

        Assert.Contains("N7.2.10.1.11 Semiconductor fuses", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Shaded text", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Jeleti text", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Section deletions", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("New material", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("2024 Edition", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_skips_short_standalone_layout_metadata_schedule_units()
    {
        const string text =
            "CONTROL CHECK\n\n"
            + "* OPERATIONS * Safety 15 min 25 min\n\n"
            + "Inspect the actuator, verify the signal, and record the result in the maintenance log.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 18, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "CONTROL CHECK", 1, 1, 1, 1, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("Inspect the actuator", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OPERATIONS", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("15 min 25 min", unit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_trims_noisy_trailing_supplements_from_materialized_units()
    {
        const string text =
            "Farcissez-en les tomates. Enfournez pour 30 minutes. Servez a la sortie du four. "
            + "Sel et poivre N Preparation : I5 minutes D Cuisson : 30 minutes ESA Pas cher "
            + "Provence : s : Selet poivre du four. tes | Remplacez par des restes effiloches "
            + "de pot-au-feu, de sees de porc ou de poulet roti... antigaspi garanti. "
            + "Profitez de l recycler les res la chair a saucisse";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 75, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Contains("Servez a la sortie du four", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", unit.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Selet poivre", unit.Text, StringComparison.Ordinal);
        Assert.Equal(0, unit.Ordinal);
        Assert.Equal(0, unit.OffsetStart);
        Assert.Equal(unit.Text.Length, unit.OffsetEnd);
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
    public void Extract_fallback_whole_document_carries_page_extraction_quality()
    {
        var quality = new PdfPageExtractionQuality(
            "low_text",
            TextEmpty: false,
            TextSparse: true,
            OcrCandidate: true,
            AverageCharsPerWord: 6,
            Signals: ["sparse_text_on_page"]);
        var pages = new[]
        {
            new ExtractedPdfPage(1, "ANNEXE", 1, 6, [1], Quality: quality)
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "ANNEXE", 1, 1, 1, 1, null)
        };

        var unit = Assert.Single(DocumentUnitExtractor.Extract(pages, sections));

        Assert.Equal("low_text", unit.ExtractionTextStatus);
        Assert.True(unit.ExtractionTextSparse);
        Assert.True(unit.ExtractionOcrCandidate);
        Assert.Contains("sparse_text_on_page", unit.ExtractionQualitySignals!);
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
    public void Extract_splits_title_case_structured_item_after_compact_measure_tail()
    {
        const string title = "Module au relais";
        const string text =
            "Controlez la sortie et laissez stabiliser pendant 20 min. "
            + "4 operators 30 min"
            + title
            + " Pour 4 operators Materials: relay, sensor. Procedure 1. Inspect status. 2. Record evidence.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 33, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit => unit.Text.StartsWith(title, StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("Controlez la sortie", StringComparison.Ordinal)
            && unit.Text.Contains(title, StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_does_not_merge_short_structured_title_segment_into_previous_recipe()
    {
        const string text =
            "Faites cuire les petits tas pendant 25 minutes. Servez tiede. "
            + "4 personnes 15 min CHOUQUETTES "
            + "CHURROS SAUCE CHOCOLAT 30 cl de lait 200 g de farine 1 sachet de levure "
            + "Preparation 1. Melanger la pate. 2. Frire les boudins. 3. Preparer la sauce chocolat.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 56, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit => unit.Text.StartsWith("CHURROS SAUCE CHOCOLAT", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("CHOUQUETTES", StringComparison.Ordinal)
            && unit.Text.Contains("CHURROS SAUCE CHOCOLAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_next_structured_body_after_footer_title_note()
    {
        const string text =
            "Temps total : 40 min 80 g de beurre 25 cl d'eau 150 g de farine "
            + "1 Prechauffez le four. 2 Ajoutez la farine. 3 Ajoutez les oeufs. "
            + "4 Enfournez les petits tas pendant 25 minutes. 4 personnes 15 min ALPHA CAKES "
            + "Decorez avec des eclats et des fruits secs. "
            + "30 cl de lait 200 g de farine 1 sachet de levure 1 Melangez la pate. "
            + "2 Formez des boudins. 3 Preparez la sauce. 4 personnes 12 min BETA STICKS "
            + "Utilisez un appareil pour des formes regulieres.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 92, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit =>
            unit.Text.Contains("ALPHA CAKES", StringComparison.Ordinal)
            && !unit.Text.Contains("30 cl de lait", StringComparison.Ordinal));
        Assert.Contains(units, unit =>
            unit.Text.StartsWith("30 cl de lait", StringComparison.Ordinal)
            && unit.Text.Contains("BETA STICKS", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("ALPHA CAKES", StringComparison.Ordinal)
            && unit.Text.Contains("BETA STICKS", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_next_structured_body_after_glued_footer_title_note()
    {
        const string text =
            "Temps total : 40 min 80 g de beurre 25 cl d'eau 150 g de farine "
            + "1 Prechauffez le four. 2 Ajoutez la farine. 3 Ajoutez les oeufs. "
            + "4 Enfournez les petits tas pendant 25 minutes. 4 personnes 15 min ALPHA CAKES"
            + "Decorez avec des eclats et des fruits secs.30 cl de lait 200 g de farine "
            + "1 sachet de levure 1 Melangez la pate. 2 Formez des boudins. "
            + "3 Preparez la sauce. 4 personnes 12 min BETA STICKSUtilisez un appareil.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 92, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit =>
            unit.Text.Contains("ALPHA CAKES", StringComparison.Ordinal)
            && !unit.Text.Contains("30 cl de lait", StringComparison.Ordinal));
        Assert.Contains(units, unit =>
            unit.Text.StartsWith("30 cl de lait", StringComparison.Ordinal)
            && unit.Text.Contains("BETA STICKS", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("ALPHA CAKES", StringComparison.Ordinal)
            && unit.Text.Contains("BETA STICKS", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_real_footer_title_note_before_glued_next_body()
    {
        const string text =
            "Enfournez et faites cuire pendant 25 \u00e0 30 min.4/6 personnes12 min30 min15 minCHOUQUETTES"
            + "D\u00e9corez d\u2019\u00e9clats de pistaches, de pralines, de noisettes.30 cl de lait demi-\u00e9cr\u00e9m\u00e9"
            + "15 cl d'eau200 g de farine1 sachet de levure chimique3 pinc\u00e9es de sel1 blanc d\u2019\u0153uf"
            + "165 g de chocolat noir1 c. \u00e0 c. d\u2019ar\u00f4me vanille1 Dans le robot muni du couteau pour p\u00e9trir/concasser, mettez 15 cl de lait et 15 cl d\u2019eau.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 64, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit =>
            unit.Text.Contains("CHOUQUETTES", StringComparison.Ordinal)
            && !unit.Text.Contains("30 cl de lait", StringComparison.Ordinal));
        Assert.Contains(units, unit =>
            unit.Text.StartsWith("30 cl de lait", StringComparison.Ordinal)
            && unit.Text.Contains("Dans le robot", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit =>
            unit.Text.Contains("CHOUQUETTES", StringComparison.Ordinal)
            && unit.Text.Contains("30 cl de lait", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_filters_short_probable_ocr_noise_units_without_removing_useful_scan_text()
    {
        const string text =
            "y'g sjueynsuog Guyeaulbug youieiq pzoz unr 9z / ssims‘dnos6-sepMuoyng'| / NOLYNE 2107\n\n"
            + "V'S sjueynsuog Buyeeulbuz youjeiq pzoz UNP gz / ssims‘dnosB-oapOuoyng'| / NOLYNE 2107\n\n"
            + "W'S sjueynsuoD Buleeulbuy youjeiq pzoz unr gz / ssims'dnosb-oapMuoyng'| / NOLYNE 907\n\n"
            + "W'S sjueynsuog Bueeulbuz Yyouiaiq pzoz unr gz / ssims‘dnosB-oepOuoying'| / NOLUN 2107\n\n"
            + "X9 | 1 tod I ! I 1 ot I Ad I | I | ! | Xo | ! 1 I 1 BG l | | 1 | ! xX 2 _ co I | | | Le _ ells = _ oe _ ___| $2091\n\n"
            + "SSLL}> ee : cable # | | \\ cr 2 dor cable # = ed SSLL | cir| 2 | ctr Clr Cir LS-| ul Clr Clr | | a | |\n\n"
            + "UL Standard for Safety for Industrial Control Panels, UL 508A Third Edition, Dated April 24, 2018 Summary of Topics\n\n"
            + "AVERTISSEMENT - RISQUE D'EXPLOSION. NE PAS REARMER LE DISJONCTEUR A MOINS QUE L'ALIMENTATION A L'APPAREILLAGE N'AIT ETE COUPEE.\n\n"
            + "This revision clarifies branch and feeder circuit spacings for industrial control panels.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 32, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.DoesNotContain(units, unit => unit.Text.Contains("sjueynsuog", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("NOLYNE", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("dnosb", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("$2091", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("SSLL", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("UL Standard for Safety", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("RISQUE D'EXPLOSION", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("branch and feeder circuit", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_splits_oversized_dense_ocr_paragraphs_before_boundary_scanning()
    {
        var repeated = string.Join(' ', Enumerable.Repeat(
            "This dense extracted paragraph contains operational evidence, references, and stable wording for retrieval.",
            520));
        var pages = new[]
        {
            new ExtractedPdfPage(1, repeated, 5200, repeated.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.True(units.Count > 4);
        Assert.All(units, unit => Assert.InRange(unit.CharCount, 1, 6000));
    }

    [Fact]
    public void Ocr_noise_filter_detects_long_repeated_noise_windows()
    {
        var noisyWindow = "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A 0 OQ =| Zz UO | W = cr O = 0";

        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(string.Join(" ", Enumerable.Repeat(noisyWindow, 3))));
    }

    [Fact]
    public void Ocr_noise_filter_detects_spaced_letter_runs_without_removing_useful_text()
    {
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "i j o t e s l a g a r b u r e Faites griller les tranches de pain et frottez-les"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "i j o t é s l a g a r b u r e • Faites griller les tranches de pain de campagne et frottez-les"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "t e n c o r e m e i l l e u r e avec la gousse d'ail restante et les tranches"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "15 L L ne) 18 3 [A L cl \u00a3 \u00a3 Xue all ade ade JE UE & y d9 \u00a3 \u00a3 ell alle al alle alle all "
            + "[4 L 9 G S G y L S G G ra L g Aug esn 36 45 8 L L 8 i$ [4 L J00pu] Xy "
            + "XUE aque e4d ol UE ade A y Xp HE aque oque JE JE ale L y se XHE aque aque all ade"));

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "M I X I N G V A L V E Overview Components Process mode Categories For 2 sections"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "A B testing validates release candidates with clear rollback notes and monitoring signals."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "DIN EN ISO 13849 defines safety related control functions and validation requirements."));

        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText("e Igousse d'ail"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText("e Ibouquet garni"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Pour 8 perronner Choisissez une grande ele a ds evases poele a bor E ou a defaut un wok : a ana ajout d'ingredient poussez les precedents vers les bords"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "N Preparation : I5 minutes D Cuisson : 30 minutes ESA Pas cher Provence : s : Selet poivre du four. tes | Remplacez par des restes effiloches"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "N Preparation : I5 minutes D Cuisson : 30 minutes ESA Pas cher Provence : s : Selet poivre du four. tes | Remplacez par des restes effiloches de pot-au-feu, de sees de porc ou de poulet roti... antigaspi garanti. Profitez de l recycler les res la chair a saucisse"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Vhuile de coude | Letape du filage doit etre realisee a feu doux, : un geste energique Va igo commence a filer apres 5 a 10 minutes."));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Sao Pr\u00e9paration 120 minutes mi Cuisson : 30 minutes ER Pas cher Q Facile qui preferent leur magret rose, Dans tous Les ca ez 5 a 10 minutes de moins."));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Ne Preparation : 50 minutes QI Cuisson : 40 minutes & Repos : I heure ESA Pas cher Q Facile"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Preparation : 1 hour Duration : 3 h 30 Intermediate pending I hour"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Dorlotez le gigot! Evitez a tout prix de le piquer ou ercer en le manipu de le p et son moelleux."));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText("ESA Pas cher"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText("I mode confirms local operation"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Inspection mode confirms local operation, records the validation result, and preserves rollback evidence for the release."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Preparation : 1 hour. Inspect the module, record the validation result, and preserve the release evidence."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Preparation : 50 minutes Cuisson : 40 minutes Repos : 1 heure Facile"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "The technician records the pressure value p in the report and verifies the enclosure before release."));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_dense_structured_quantity_steps()
    {
        const string text =
            "300 g component alpha 40 g component beta 100 g module seal 4 clamps "
            + "115 g fastener set 170 g carrier plate 125 g support bracket "
            + "1 Prepare the enclosure. 2 Install the module and tighten the clamps. "
            + "3 Verify the signal in the controller. 4 Record the validation result in the log.";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_interleaved_technical_reference_text()
    {
        const string patentAndHistory =
            "In 1965 a revised NFPA adheres to the policy of the American National Standards Institute (ANSI) regarding the inclusion of patents in edition was adopted, reconfirmed in 1969, and in 1970, 1971, 1973, 1974, 1977, 1980, 1985, 1987, American National Standards and hereby gives the following notice pursuant to that policy. "
            + "NOTICE: The user attention is called to the possibility that compliance with an NFPA Standard may require use of an invention covered by patent rights. "
            + "In September 1941, the metalworking machine tool industry wrote its first electrical standard to make machine tools safer to operate, more productive, and less costly to maintain.";
        const string surgeProtection =
            "7.8.3.4 Component Assembly and Other Type 4 SPD. Component assembly SPDs Type 1, 2, or 3 shall be applied in accordance with 7.8.3.1 through 7.8.3.3 and any additional conditions of use specified by the device manufacturer. "
            + "Table 7.2.10.4 Relationship Between Conductor Size and Maximum Rating or Setting of Motor Branch-Circuit Short-Circuit and Ground-Fault Protective Device for Power Circuits.";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(patentAndHistory));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(surgeProtection));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_standards_approval_governance_text_before_roster_lines()
    {
        var text =
            "ANSI Z535.3 - 2007 This standard was processed and approved for submittal to ANSI by the Accredited Standards Committee on Safety Signs and Colors, ANSI Z535. "
            + "Committee approval of this standard does not necessarily imply that all committee members voted for its approval. "
            + "At the time of approval, the ANSI Z535 Committee had the following members: Gary M. Bell, Chairperson Richard Olesen, Vice Chair Paul Orr, Secretary "
            + "Organization Represented: Name of Representative: J. Paul Frantz American Society of Safety Engineers Thomas F. Breshnahan Alt. Howard A. Elwell Alt. American Welding Society August F.";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_entity_roster_pages_after_cleanup()
    {
        var text =
            "ANSI Z535.1-2006\n"
            + "Human Factors & Ergonomics Society Michael Kalsher\n"
            + "Michael S. Wogalter (Alt.)\n"
            + "Human Factors & Safety Analytics, Inc. Jay Martin\n"
            + "Industrial Safety Equip. Assoc. Linda Moquet\n"
            + "Richard L. Fisk (Alt.)\n"
            + "Institute of Electrical & Electronics Engineers Al Clapp\n"
            + "John Dagenhart (Alt.)\n"
            + "Sue Vogel (Alt.)\n"
            + "International Staple, Nail, and Tool Assoc. John Kurtz\n"
            + "L. Dale Baker & Associates L. Dale Baker\n"
            + "Lab Safety Supply, Inc. Jim Versweyveld\n"
            + "Marhefka & Associates Russell E. Marhefka\n"
            + "National Association - Graphic Product Russ Butchko\n"
            + "Identification Donna Ehrmann\n"
            + "National Electrical Manufacturers Association John Young\n"
            + "John Katzbeck (Alt.)\n"
            + "National Spray Equipment Mfrs. Assoc. Dan Pahl\n"
            + "Nuclear Suppliers Assoc. Blair Brewster\n"
            + "Power Tool Institute Wayne Hill\n"
            + "George Whelchel\n"
            + "Charles M. Stockinger (Alt.)\n"
            + "Rural Utilities Service Trung Hiu\n"
            + "Safety Behavior Analysis, Inc. Shelley Waters Deppa\n"
            + "Sauder Woodworking Gary Bell\n"
            + "Scott Helberg (Allt.)\n"
            + "Scaffold Industry Assoc. Dave Merrifield\n"
            + "Snapontools Bill Pagac\n"
            + "Tom Christensen (Alt.)\n"
            + "Society of the Plastics Industry, Machinery Div. Loren Mills\n"
            + "Walter Bishop (Alt.)";

        var cleaned = OcrNoiseFilter.RemoveTrailingNoisySupplement(text);

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(cleaned));
        var page = new ExtractedPdfPage(
            12,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [12]);
        var unitsWithoutSections = DocumentUnitExtractor.Extract([page], []);

        Assert.NotEmpty(unitsWithoutSections);

        var units = DocumentUnitExtractor.Extract([page], DocumentSectionExtractor.Extract([page]));

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("Human Factors & Ergonomics Society", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("Safety Behavior Analysis", StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_initialed_entity_roster_tables()
    {
        var text =
            "APRIL 1, 2021 CSA C22.2 No. 213-17 UL 121201 7 NAME COMPANY "
            + "W. Lawrence FM Approvals LLC E. Leubner Eaton's Crouse-Hinds Business "
            + "W. Lockhart GE Gas Power W. Lowers WCL Corp. *N. Ludlam FM Approvals Ltd. "
            + "R. Martin USCG E. Massey ABB Motors and Mechanical Inc. "
            + "T. Michalski Killark Electric Mfg. Co. *J. Miller MSA Innovation LLC "
            + "B. Miller Mettler-Toledo LLC *O. Murphy Honeywell Inc. "
            + "D. Nedorostek Bureau of Safety & Environmental Enforcement "
            + "R. Parks National Instruments L. Ricks ExVeritas North America LLC "
            + "*K. Robinson Occupational Safety and Health Adm. "
            + "J. Ruggieri General Machine Corp. S. Sam Tundra Oil & Gas "
            + "P. Schimmoeller CSA Group *T. Schnaare Rosemount Inc. "
            + "*R. Teather Det Norske Veritas Certification Inc. "
            + "* Non-voting member";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
        var page = new ExtractedPdfPage(
            10,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [10]);
        var units = DocumentUnitExtractor.Extract([page], DocumentSectionExtractor.Extract([page]));

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("NAME COMPANY", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("FM Approvals", StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_german_technical_reference_and_welding_blocks()
    {
        var welding =
            "Allgemeine schweisstechnische Gestaltungsgrundsaetze Werden tragende Naehte durch nicht zu vermeidende Teile verdeckt, so ist entweder die Naht vor dem Anschweissen des Teiles zu pruefen oder die Teile sind so zu gestalten. "
            + "Die Schweissnaehte sind so zu dimensionieren, dass eine Pruefung moeglich ist. DVS, Technischer Ausschuss, Arbeitsgruppe 22 Schweissen und Verarbeiten von Kunststoffhalbfabrikaten.";
        var references =
            "DIN 16 963 Rohre aus PB Polybuten 1: Allgemeine Gueteanforderungen und Pruefung. "
            + "DIN 19531 Rohre und Formstuecke aus PVC hart fuer Abwasserleitungen. "
            + "DIN 7749 Kunststoff-Formmassen: weichmacherhaltige Polychlorid Werkstoffe, Probekoerpern und Bestimmung ihrer Eigenschaften.";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(welding));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(references));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_compact_technical_topic_lists()
    {
        var text =
            "Safety alert symbols Examples of a signal word panel Supplemental directive with safety alert symbol METTETE a "
            + "Examples of section safety message with signal word panel Examples of section safety message with safety alert symbol "
            + "Examples of embedded safety message with signal word Embedded safety message with safety alert symbol "
            + "Providing Information About Safety Messages in Collateral Materials and Product Safety";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_technical_decision_matrix_pages()
    {
        var text =
            "ANSI Z535.6-2006 C4.1. Signal Word Selection Matrices "
            + "The following matrices show the signal words, colors, and presence or absence of safety alert symbol "
            + "that are assigned for each combination of accident probability, worst credible harm, and probability of worst credible harm, "
            + "If Worst Credible Severity of Harm is Death or Serious Injury "
            + "Probability of Accident if Hazardous Situation is not Avoided "
            + "Probability of Death or Serious ceed | Le \u2014 Injury if Accident Occurs : - "
            + "\"| ASWARNING|||A\\ WARNING "
            + "If Worst Credible Severity of Harm is Moderate or Minor Injury "
            + "For all probabilities: f CAUTION "
            + "If Worst Credible Severity of Harm is Property Damage "
            + "For all probabilities: Preferred: EERIE aeerrcemerr mare "
            + "(aomy, 7 5 m ea NOTICE\" Alternate: 20";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
        var page = new ExtractedPdfPage(
            41,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [41]);
        var units = DocumentUnitExtractor.Extract([page], DocumentSectionExtractor.Extract([page]));

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("Signal Word Selection Matrices", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("Probability of Accident", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_keeps_structured_page_when_fragments_are_too_short_individually()
    {
        var text =
            "ANSI Z535.6-2006\n"
            + "C4.1. Signal Word Selection Matrices\n"
            + "If Worst Credible Severity of Harm is Death or Serious Injury\n"
            + "Probability of Accident if Hazardous Situation is not Avoided\n"
            + "Probability of Death or Serious Injury if Accident Occurs\n"
            + "WARNING\n"
            + "If Worst Credible Severity of Harm is Moderate or Minor Injury\n"
            + "For all probabilities: CAUTION\n"
            + "If Worst Credible Severity of Harm is Property Damage\n"
            + "For all probabilities: Preferred: NOTICE\n"
            + "Alternate: 20";
        var page = new ExtractedPdfPage(
            41,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [41]);
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Signal Word Selection Matrices", 41, 41, 41, null, null)
        };

        var units = DocumentUnitExtractor.Extract([page], sections);

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("Signal Word Selection Matrices", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("For all probabilities", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_keeps_noisy_but_substantive_technical_table_pages()
    {
        var text =
            "List, for each piece of equipment, the pressures for all liquids to be utilized in the process, including the maximum, minimum, and nominal rates of introduction "
            + "and power electric power requirements maximum allowable fluctuation in electrical service that can be accepted without electrical power filtration "
            + "List, for each piece of equipment, all solids to be rejected in the process purities/concentrations of all solids to be rejected in the process "
            + "quantities of all solids to be rejected in the and nominal rates of rejection "
            + "List, for each piece of equipment, all types of exhaust to be utilized in the process "
            + "List, for each piece of equipment, the types of exhaust flows (e.g. acid, solvent, heat, general, etc.) to be utilized in the process "
            + "and their respective concentrations, and quantities of all exhaust flows to be utilized in the process, including the maximum, minimum, and nominal rates of introduction";
        var page = new ExtractedPdfPage(
            50,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [50]);

        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(text));

        var units = DocumentUnitExtractor.Extract([page], DocumentSectionExtractor.Extract([page]));

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("for each piece of equipment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(units, unit => unit.Text.Contains("exhaust flows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_technical_figures_with_captions_and_notes()
    {
        var enclosureLayout =
            "19-78 INDUSTRIAL MACHINERY ANNEX D Manufacturer Rack_1_ Group_3_ Module 6. "
            + "120 VAC input module Output 0 Spare Master terminal box panel Output 3 "
            + "1 in. wiring channel Main control panel FIGURE D.1(h) Sample Enclosure Layout - Interior. "
            + "Door Layout Main panel Electrical control lockout placard "
            + "FIGURE D.1(i) Sample Enclosure Layout - Exterior.";
        var busBarSupports =
            "188 UL 508A JULY 28, 2022 Figure D3.3 Location of supports for right-angle connection "
            + "of edge-to-edge bus bars for a bus bar assembly in an industrial control panel without a main "
            + "or marked for use with a remote main SUPPLY DISTRIBUTION DISTRIBUTION "
            + "In which: X1 - Two supports required at line terminal end except one support may be used for single bus bar "
            + "with a short-circuit current rating of 50,000 A or less. "
            + "X2 - One support required at connection of vertical to horizontal bus or as shown in Figure D3.4. "
            + "Note - For all other supports see Figure D3.2.";
        var letThroughChart =
            "172 UL 508A JULY 28, 2022 Available Short Circuit Current RMS Symmetrical Amperes "
            + "To determine peak let-through current and t value: "
            + "a) Obtain plots of the maximum let-through values for the specific current limiting circuit breaker from the manufacturer; "
            + "b) Select the available short circuit current along the horizontal axis at the bottom of the chart that is equal to the short circuit current rating of the industrial control panel; "
            + "c) Move vertically to the intersection with the curve corresponding to the rated voltage of the circuit breaker; "
            + "d) Move horizontally left to intersection with the vertical axis to determine the peak let-through current or t value.";
        var compactAnnotatedLayout =
            "INDUSTRIAL MACHINERY VO Manufacturer Rack_1_ Group_3_ Module 6. "
            + "mn 120 VAG input module 14FU Output O 15FU 1FU 2FU 3FU 1% in. wiring channel "
            + "Master terminal box panel Main control panel Door Layout e = Section deletions. "
            + "N = New material. Shaded text = Revisions. A = Text deletions and figure/table revisions. "
            + "Shaded text = Revisions, A = Text deletions and figure/table revisions. "
            + "+= Section deletions, N = New material.";

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(enclosureLayout));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(busBarSupports));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(letThroughChart));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(compactAnnotatedLayout));
        Assert.Contains(
            "Main control panel",
            OcrNoiseFilter.RemoveTrailingNoisySupplement(compactAnnotatedLayout),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_keeps_compact_annotated_technical_figure_pages()
    {
        var text =
            "INDUSTRIAL MACHINERY VO Manufacturer Rack_1_ Group_3_ Module 6. "
            + "mn 120 VAG input module 14FU Output O 15FU 1FU 2FU 3FU 1% in. wiring channel "
            + "Master terminal box panel Main control panel Door Layout e = Section deletions. "
            + "N = New material. Shaded text = Revisions. A = Text deletions and figure/table revisions. "
            + "Shaded text = Revisions, A = Text deletions and figure/table revisions. "
            + "+= Section deletions, N = New material.";
        var expectedPrefix =
            "INDUSTRIAL MACHINERY VO Manufacturer Rack_1_ Group_3_ Module 6. "
            + "mn 120 VAG input module 14FU Output O 15FU 1FU 2FU 3FU 1% in. wiring channel "
            + "Master terminal box panel Main control panel Door Layout";
        var page = new ExtractedPdfPage(
            42,
            text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length,
            [42]);
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(text));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(expectedPrefix));
        Assert.Equal(expectedPrefix, OcrNoiseFilter.RemoveTrailingNoisySupplement(expectedPrefix));

        var units = DocumentUnitExtractor.Extract([page], DocumentSectionExtractor.Extract([page]));

        Assert.NotEmpty(units);
        Assert.Contains(units, unit => unit.Text.Contains("Main control panel", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains("wiring channel", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("Section deletions", StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_noise_filter_tolerates_symbol_only_fragments_from_real_ocr()
    {
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText("• / - — ..."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText("() [] {}"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText("® , ’"));
    }

    [Fact]
    public void Ocr_noise_filter_trims_short_layout_prefix_before_structural_labels()
    {
        var cleaned = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "D\u00c9J PR\u00c9PARATION INGR\u00c9DIENTS \u2022 Dans un bol, melanger les elements puis verifier le resultat.");
        var cleanedFromSplitHeader = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "PAUX INGR\u00c9DIENTS PR\u00c9PARATION 1 module de test \u2022 Verifier la ligne puis enregistrer le resultat.");

        Assert.StartsWith("PR\u00c9PARATION INGR\u00c9DIENTS", cleaned, StringComparison.Ordinal);
        Assert.False(cleaned.StartsWith("D\u00c9J", StringComparison.Ordinal));
        Assert.StartsWith("INGR\u00c9DIENTS PR\u00c9PARATION", cleanedFromSplitHeader, StringComparison.Ordinal);
        Assert.False(cleanedFromSplitHeader.StartsWith("PAUX", StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_noise_filter_skips_short_broken_uppercase_header_fragments()
    {
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText("DES-"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText("PLATS PRINCI-"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText("OPEN-LOOP calibration remains active."));
    }

    [Fact]
    public void Ocr_noise_filter_preserves_single_letter_measure_abbreviation_tails()
    {
        var thyme = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "TRUCS CULINAIRES 45 ml de contenu utile. Pensez a economiser au maximum sur la diversite de vos elements. 10 ml (2 c. a the) de traceur frais, environ 4-5 branches 79");
        var vanilla = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Cuire au four environ 1h ou jusqu'a ce qu'un controle insere au centre ressorte propre. 125 ml de module, ramolli 3 elements, ecrases 2 oeufs 5 ml (1 c. a the) d'extrait de vanille 99");

        Assert.Contains("c. a the) de traceur", thyme, StringComparison.Ordinal);
        Assert.Contains("4-5 branches 79", thyme, StringComparison.Ordinal);
        Assert.Contains("c. a the) d'extrait", vanilla, StringComparison.Ordinal);
        Assert.EndsWith("99", vanilla, StringComparison.Ordinal);
    }

    [Fact]
    public void Ocr_noise_filter_preserves_title_before_dense_inline_index_codes()
    {
        var cleaned = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Alpha Beta Module [Index: ] MCRC01072833_BO_Alpha_Beta_Module-010 MCRC01072992_SE_Alpha_Beta_Module-007");

        Assert.Equal("Alpha Beta Module", cleaned);
    }

    [Fact]
    public void Extract_skips_short_uppercase_page_header_fragments()
    {
        const string usefulParagraph =
            "Useful paragraph with enough context to describe the operation and preserve the document content.";
        var pages = new[]
        {
            new ExtractedPdfPage(85, $"PLATS PRINCI- PAUX 85\n\n{usefulParagraph}", 16, usefulParagraph.Length + 22, [85])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 85, 85, null, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.DoesNotContain(units, unit => unit.Text.Contains("PLATS PRINCI", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.Text.Contains(usefulParagraph, StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_noise_filter_trims_noisy_trailing_supplements_after_complete_content()
    {
        var tomato = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Farcissez-en les tomates. Enfournez pour 30 minutes. Servez a la sortie du four. Sel et poivre N Preparation : I5 minutes D Cuisson : 30 minutes ESA Pas cher Provence : s : Selet poivre du four. tes | Remplacez par des restes effiloches");
        var tomatoWithLongNoisyTail = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Farcissez-en les tomates. Enfournez pour 30 minutes. Servez a la sortie du four. Sel et poivre N Preparation : I5 minutes D Cuisson : 30 minutes ESA Pas cher Provence : s : Selet poivre du four. tes | Remplacez par des restes effiloches de pot-au-feu, de sees de porc ou de poulet roti... antigaspi garanti. Profitez de l recycler les res la chair a saucisse");
        var chocolate = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Refermez avec la moitie superieure. Nappez les profiteroles de chocolat et servez aussitot ! ® , N Preparation : 20 minutes mi Cuisson : 35 minutes Q Facile Ic. a soupe de sucre 7 les choux, changez el Remplacez la glace rune creme patissiere. la sauce au choco at e au sucre colore");

        var gigot = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Placez ensuite au milieu du four pour 7 heures. Versez le jus de cuisson et ses legumes dans une casserole et faites reduire jusqu'a obtenir une sauce onctueuse. Portez a ebullition puis versez-en une partie sur le gigot et presentez le reste en sauciere. C'est pret ! il perdrait son jus Quant a l'accompag");
        var magret = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Entaillez la peau des magrets en losanges et enfournez pour 30 min, en aspergeant la viande de temps en temps avec le jus de la marinade. A la sortie du four, decoupez les magrets en tranches. Servez accompagne de riz et de legumes ! 5 a 1 0 m d 1 0 ans le placard ? Pas de miel d erable.");
        var fondant = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Facile Fondant chocolat au 200 g de chocolat. Repartissez dans des ramequins allant au four et enfournez pendant 7 min. Servez tiede ! Pour # personnes Et si vous ajoutiez un . Ss fondants ?");
        var brokenMetadataFragment = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "blanc avant d'enfourner, les gourmands en resteront N Preparation : 10 minutes D Cuisson : 8 minutes ESA Pas cher Q Facile");
        var creme = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Au moment de servir, parsemez de cassonade et faites carameliser a l'aide d'un chalumeau. Degustez sans attendre ! ' N Preparation : 10 minutes D Cuisson : 25 minutes ESA Pas cher Q");
        var cremeCurly = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Au moment de servir, parsemez de cassonade et faites caram\u00e9liser \u00e0 l'aide d'un chalumeau. D\u00e9gustez sans attendre ! \u2019 N Pr\u00e9paration : 10 minutes D Cuisson : 25 minutes Q");
        var appleFragment = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Quant aux accros de la pomme; ils se regaleront avec des pommes caramelisees dans le sucre et le beurre au prealable, ou parsemees de pepites de chocolat juste N Preparation : 10 minutes D Cuisson : 30 minutes Q Facile");
        var millefeuille = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "R\u00e9servez au frais jusqu'au service. Servez saupoudr\u00e9 de sucre glace ! \u2018 N Preparation : I5 minutes mi Cuisson : 15 minutes Q Facile Changez de fruits, vous changerez de millefeuille.");
        var shortMetadataTailAfterFragment = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Zappez le chocolat ant Ic. a cafe de cafe le lait de la creme glacage de sucre de blanc d'oeuf parfume patissiere, puis un N Preparation : 40 minutes");

        Assert.Contains("Servez a la sortie du four", tomato, StringComparison.Ordinal);
        Assert.DoesNotContain("Selet poivre", tomato, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", tomato, StringComparison.Ordinal);
        Assert.Contains("Servez a la sortie du four", tomatoWithLongNoisyTail, StringComparison.Ordinal);
        Assert.DoesNotContain("Selet poivre", tomatoWithLongNoisyTail, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", tomatoWithLongNoisyTail, StringComparison.Ordinal);
        Assert.Contains("Nappez les profiteroles", chocolate, StringComparison.Ordinal);
        Assert.DoesNotContain("choco at e", chocolate, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", chocolate, StringComparison.Ordinal);
        Assert.Contains("C'est pret !", gigot, StringComparison.Ordinal);
        Assert.DoesNotContain("accompag", gigot, StringComparison.Ordinal);
        Assert.Contains("Servez accompagne de riz et de legumes !", magret, StringComparison.Ordinal);
        Assert.DoesNotContain("1 0 m d", magret, StringComparison.Ordinal);
        Assert.EndsWith("Servez tiede !", fondant, StringComparison.Ordinal);
        Assert.DoesNotContain("Pour # personnes", fondant, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(brokenMetadataFragment));
        Assert.EndsWith("Degustez sans attendre !", creme, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", creme, StringComparison.Ordinal);
        Assert.EndsWith("D\u00e9gustez sans attendre !", cremeCurly, StringComparison.Ordinal);
        Assert.DoesNotContain("Pr\u00e9paration", cremeCurly, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(appleFragment));
        Assert.EndsWith("Servez saupoudr\u00e9 de sucre glace !", millefeuille, StringComparison.Ordinal);
        Assert.DoesNotContain("N Preparation", millefeuille, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(shortMetadataTailAfterFragment));
    }

    [Fact]
    public void Ocr_noise_filter_trims_decorative_schedule_metadata_after_complete_content()
    {
        var cleaned = OcrNoiseFilter.RemoveTrailingNoisySupplement(
            "Verify the actuator setting. * SAFETY MODE * 10 min 4 min 1 h 15 min");

        Assert.Equal("Verify the actuator setting.", cleaned);
    }

    [Fact]
    public void Ocr_noise_filter_removes_spaced_letter_runs_while_preserving_adjacent_text()
    {
        var text = string.Join('\n',
            "A s t u c e ! encore 35 minutes. Cinq minutes avant la fin, ajoutez les",
            "C o m",
            "i j o t é s l a g a r b u r e • Faites griller les tranches de pain de campagne et frottez-les",
            "t e n c o r e m e i l l e u r e avec la gousse d'ail restante.",
            "l a grande cocotte.",
            "P r o fi t e z au-feu, effilochez les restes.",
            "Servez avec quelques gouttes de colorant pour des e n - c i e l !",
            "M I X I N G V A L V E Overview Components Process mode Categories For 2 sections");

        var cleaned = OcrNoiseFilter.RemoveSpacedLetterRunNoise(text);

        Assert.DoesNotContain("A s t u c e", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("i j o t", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("t e n c", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("l a grande", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("P r o fi", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("e n - c", cleaned, StringComparison.Ordinal);
        Assert.Contains("encore 35 minutes", cleaned, StringComparison.Ordinal);
        Assert.Contains("Faites griller les tranches", cleaned, StringComparison.Ordinal);
        Assert.Contains("avec la gousse d'ail restante", cleaned, StringComparison.Ordinal);
        Assert.Contains("grande cocotte", cleaned, StringComparison.Ordinal);
        Assert.Contains("au-feu, effilochez les restes", cleaned, StringComparison.Ordinal);
        Assert.Contains("Servez avec quelques gouttes", cleaned, StringComparison.Ordinal);
        Assert.Contains("M I X I N G V A L V E Overview", cleaned, StringComparison.Ordinal);
        Assert.Equal(
            "Stoppez la cuisson après ébullition.",
            OcrNoiseFilter.RemoveSpacedLetterRunNoise(
                "ffl e s u r Stoppez la cuisson après ébullition."));
    }

    [Fact]
    public void Ocr_noise_filter_detects_symbol_heavy_rotated_scan_lines_without_removing_useful_warnings()
    {
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A © 0 OQ =| © Zz UO | W = ©) = cr O = 0"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "A ay) UR) (ANA JaMO]) Jayep aq ABW suojOg Ayes youIg NYeA pue any ey} yA Aida Kay] se Guo] se sooo snjd +9 S UBBID Ayes Molla Ayes aHuelg AjgJeg pai Aes"));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "yo o | | i Ne | a \\\"------- abgearbeitet __/ sel 48 a) |b."));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "yo o | | i Ne | a \\“——— abgearbeitet __/ sel 48 a) |b."));

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Use DANGER with the safety alert symbol when a hazardous situation will result in death or serious injury."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "The team reviews the weekly plan with notes from this meeting and shares updates before Friday."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "AVERTISSEMENT - RISQUE D'EXPLOSION. NE PAS REARMER LE DISJONCTEUR AVANT D'AVOIR COUPE L'ALIMENTATION."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Complete with brackets Rev. | Date of issue Lang. | Sheet A |2002-05-14 [en [1/5"));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "يجب فحص صمام الأمان قبل تشغيل النظام وتسجيل نتيجة الاختبار في سجل الصيانة."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "Техническое обслуживание выполняется после отключения питания и проверки состояния защитного шкафа."));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "操作前确认安全联锁状态并记录控制面板上的报警信息"));

        var cjkPage = new ExtractedPdfPage(
            1,
            "操作前确认安全联锁状态并记录控制面板上的报警信息",
            1,
            23,
            [1]);
        var cjkSections = new[]
        {
            new ExtractedDocumentSection(0, "安全检查", 1, 1, 1, 1, null)
        };
        Assert.Contains(
            DocumentUnitExtractor.Extract([cjkPage], cjkSections),
            unit => unit.Text.Contains("安全联锁状态", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_keeps_contact_addresses_as_body_units()
    {
        const string text = """
Contacts in case of incident

Switzerland support contact
Zurich Service Center, Hardstrasse 12, 8005 Zurich, phone +41 44 555 10 10, email support-ch@example.com
Geneva Emergency Desk, Rue du Rhone 30, 1204 Geneva, phone +41 22 555 20 20, email support-ge@example.com
Basel Spare Parts Office, Aeschenplatz 4, 4052 Basel, phone +41 61 555 30 30, email parts-bs@example.com

Use these contacts only after isolating the equipment and recording the alarm code.
""";
        var pages = new[]
        {
            new ExtractedPdfPage(3, text, 75, text.Length, [3])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contacts in case of incident", 1, 3, 3, 1, null)
        };

        var units = DocumentUnitExtractor.Extract(pages, sections);
        var combined = string.Join(" ", units.Select(unit => unit.Text));

        Assert.Contains("Zurich Service Center", combined, StringComparison.Ordinal);
        Assert.Contains("support-ch@example.com", combined, StringComparison.Ordinal);
        Assert.Contains("Geneva Emergency Desk", combined, StringComparison.Ordinal);
        Assert.Contains("Use these contacts only after isolating", combined, StringComparison.Ordinal);
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
