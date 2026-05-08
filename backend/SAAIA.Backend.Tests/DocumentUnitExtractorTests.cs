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
