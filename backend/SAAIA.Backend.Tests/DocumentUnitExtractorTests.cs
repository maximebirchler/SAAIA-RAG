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
    public void Stable_unit_id_is_deterministic_for_same_revision_and_ordinal()
    {
        var revisionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var left = DocumentFoundationRepo.BuildStableUnitId(revisionId, 4);
        var right = DocumentFoundationRepo.BuildStableUnitId(revisionId, 4);

        Assert.Equal(left, right);
    }
}
