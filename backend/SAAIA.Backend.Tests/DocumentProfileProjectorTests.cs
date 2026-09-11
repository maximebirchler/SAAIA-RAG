using System.Linq;
using System.IO;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentProfileProjectorTests
{
    [Fact]
    public void Project_builds_generic_profile_from_document_structure()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 10, 78, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inerting Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 78, 10, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 7, 2, [3], "standard_ref")
        };

        var profile = DocumentProfileProjector.Project("ATEX/CEN TR 15281.pdf", pages, sections, units, exact);

        Assert.Equal("deterministic_v1", profile.ProfileVersion);
        Assert.Contains("CEN TR 15281.pdf", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("inerting", profile.Keywords);
        Assert.Contains("EN 15281", profile.Entities);
        Assert.Contains("Inerting Safety", profile.Topics);
        Assert.Contains(profile.HypotheticalQuestions, q => q.Contains("inerting", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("EN 15281", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Inerting Safety", StringComparison.Ordinal));
        Assert.True(profile.TokenCount > 0);
        Assert.Equal(32, profile.Checksum.Length);
    }

    [Fact]
    public void Project_structured_mode_uses_canonical_units_instead_of_raw_page_noise()
    {
        const string canonicalText =
            "Before assembly, inspect the sealing surface and verify the pressure rating. "
            + "Technical Help to Exporters TRANSLATION remains source content, not a title.";
        const string rawPageNoise =
            "TRANSLATION SMS 114 TRANSLATION SMS 1145 999 kg 888 min";
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                rawPageNoise,
                10,
                rawPageNoise.Length,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(
                4,
                "Assembly procedure",
                1,
                1,
                1,
                null,
                null)
        };
        var structuredUnits = new[]
        {
            new ExtractedDocumentUnit(
                12,
                4,
                1,
                1,
                canonicalText,
                canonicalText.Length,
                11,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Assembly.pdf",
            pages,
            sections,
            structuredUnits,
            exactMatchEntries: [],
            preferStructuredSources: true);

        Assert.Equal("deterministic_canonical_v5", profile.ProfileVersion);
        Assert.Contains(
            profile.ContentCards,
            static card => string.Equals(
                card.Title,
                "Assembly procedure",
                StringComparison.Ordinal));
        Assert.All(
            profile.ContentCards,
            static card =>
            {
                Assert.Equal("canonical_section_anchor", card.Kind);
                Assert.Contains(
                    "canonical_section_heading",
                    card.Signals);
                Assert.NotNull(card.Evidence);
                Assert.Contains(
                    card.Evidence!.Facts ?? [],
                    fact => string.Equals(
                                fact.Kind,
                                "canonical_heading",
                                StringComparison.Ordinal)
                            && string.Equals(
                                fact.SourceText,
                                card.Title,
                                StringComparison.Ordinal));
            });
        Assert.DoesNotContain(
            profile.ContentCards,
            static card => card.Title.Contains(
                "Technical Help",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            profile.ContentCards,
            static card => string.Equals(
                card.Kind,
                "page_embedded_title",
                StringComparison.Ordinal));
        Assert.Contains(canonicalText, profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TRANSLATION SMS 114",
            profile.SearchText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            profile.ContentCards
                .SelectMany(static card =>
                    card.Evidence?.QuantityFacts
                    ?? Array.Empty<DocumentProfileQuantityFact>()),
            static fact => fact.SourceText.Contains(
                "999 kg",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Project_structured_mode_preserves_distinct_canonical_headings_without_semantic_guessing()
    {
        const string text =
            "Ingredients and preparation steps grounded in the canonical content blocks.";
        var pages = new[]
        {
            new ExtractedPdfPage(5, text, 9, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "Bœuf bourguignon",
                1,
                5,
                5,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "Pour 4 personnes",
                2,
                5,
                5,
                null,
                null),
            new ExtractedDocumentSection(
                2,
                "Préparation",
                2,
                5,
                5,
                null,
                null),
            new ExtractedDocumentSection(
                3,
                "Préparation",
                2,
                6,
                6,
                null,
                null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                2,
                5,
                5,
                text,
                text.Length,
                9,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Cuisine/Fixture.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: [],
            preferStructuredSources: true);

        var card = Assert.Single(profile.ContentCards);
        Assert.Equal("Préparation", card.Title);
        Assert.Contains(
            "canonical_section_heading",
            card.Signals);
        Assert.Contains(
            "canonical_section_context",
            card.Signals);
        Assert.Contains(
            card.Evidence!.Facts ?? [],
            static fact =>
                fact.Kind == "canonical_structure"
                && fact.Label == "heading_path"
                && fact.Value
                    == "Bœuf bourguignon > Préparation");
    }

    [Fact]
    public void Project_structured_mode_preserves_heading_hierarchy_and_exact_local_context()
    {
        const string localContext =
            "Verify the isolation valve, record the measured pressure, and retain the signed inspection record.";
        var pages = new[]
        {
            new ExtractedPdfPage(
                4,
                localContext,
                14,
                localContext.Length,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "Plant operations",
                1,
                3,
                8,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "Isolation validation",
                2,
                4,
                5,
                null,
                null),
            new ExtractedDocumentSection(
                2,
                "Inspection record",
                3,
                4,
                4,
                null,
                null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                7,
                2,
                4,
                4,
                localContext,
                localContext.Length,
                14,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Plant.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: [],
            preferStructuredSources: true);

        var card = Assert.Single(
            profile.ContentCards,
            static item => string.Equals(
                item.Title,
                "Inspection record",
                StringComparison.Ordinal));
        Assert.Equal("canonical_section_anchor", card.Kind);
        Assert.Equal(4, card.PageStart);
        Assert.Equal(4, card.PageEnd);
        Assert.Contains(
            "canonical_section_context",
            card.Signals);
        Assert.NotNull(card.Evidence);
        Assert.Contains(
            card.Evidence!.Facts ?? [],
            static fact =>
                string.Equals(
                    fact.Kind,
                    "canonical_structure",
                    StringComparison.Ordinal)
                && string.Equals(
                    fact.Label,
                    "heading_path",
                    StringComparison.Ordinal)
                && string.Equals(
                    fact.Value,
                    "Plant operations > Isolation validation > Inspection record",
                    StringComparison.Ordinal));
        Assert.Contains(
            card.Evidence.Facts ?? [],
            static fact =>
                string.Equals(
                    fact.Kind,
                    "canonical_source",
                    StringComparison.Ordinal)
                && string.Equals(
                    fact.Label,
                    "retrieval_chunk_index",
                    StringComparison.Ordinal)
                && string.Equals(
                    fact.Value,
                    "7",
                    StringComparison.Ordinal));
        Assert.Contains(
            card.Evidence.Facts ?? [],
            static fact =>
                string.Equals(
                    fact.Kind,
                    "canonical_section_context",
                    StringComparison.Ordinal)
                && string.Equals(
                    fact.SourceText,
                    localContext,
                    StringComparison.Ordinal)
                && fact.PageStart == 4
                && fact.PageEnd == 4);
    }

    [Fact]
    public void Project_structured_mode_prefers_the_chunk_local_heading_path_over_false_visual_parentage()
    {
        const string localContext =
            "Dessert Alpha combines fruit, dairy and a documented preparation.";
        var pages = new[]
        {
            new ExtractedPdfPage(
                12,
                localContext,
                9,
                localContext.Length,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "Earlier major heading",
                1,
                2,
                20,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "Earlier sibling heading",
                3,
                8,
                20,
                null,
                null),
            new ExtractedDocumentSection(
                2,
                "Dessert Alpha",
                6,
                12,
                12,
                null,
                null)
        };
        var units = CanonicalProfileInputProjector.Project(
        [
            new ProjectedRetrievalChunk(
                41,
                2,
                null,
                12,
                12,
                localContext,
                9,
                [2],
                DoclingCanonicalRetrievalProjector.ContentChunkType,
                SectionTitle: "Dessert Alpha",
                HeadingPath: "Dessert Alpha",
                HeadingLevel: 1)
        ]);

        var profile = DocumentProfileProjector.Project(
            "Generic/Fixture.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: [],
            preferStructuredSources: true);

        var card = Assert.Single(profile.ContentCards);
        Assert.Contains(
            card.Evidence!.Facts ?? [],
            static fact =>
                fact.Kind == "canonical_structure"
                && fact.Label == "heading_path"
                && fact.Value == "Dessert Alpha");
        Assert.Contains(
            card.Evidence.Facts ?? [],
            static fact =>
                fact.Kind == "canonical_structure"
                && fact.Label == "section_level"
                && fact.Value == "1");
    }

    [Fact]
    public void Project_structured_mode_does_not_expose_heading_only_cards()
    {
        const string localContext =
            "Tighten the terminal screws and record the measured torque.";
        var pages = new[]
        {
            new ExtractedPdfPage(
                2,
                localContext,
                9,
                localContext.Length,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(
                0,
                "Unrelated heading without content",
                1,
                1,
                1,
                null,
                null),
            new ExtractedDocumentSection(
                1,
                "Terminal tightening",
                1,
                2,
                2,
                null,
                null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                1,
                2,
                2,
                localContext,
                localContext.Length,
                9,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Installation.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: [],
            preferStructuredSources: true);

        var card = Assert.Single(profile.ContentCards);
        Assert.Equal("Terminal tightening", card.Title);
        Assert.DoesNotContain(
            profile.ContentCards,
            static item => item.Title
                == "Unrelated heading without content");
        Assert.Contains(
            card.Evidence!.Facts ?? [],
            static fact =>
                fact.Kind == "canonical_section_context"
                && fact.Label == "section_excerpt"
                && fact.SourceText == localContext);
    }

    [Fact]
    public void Project_does_not_create_profile_cards_from_sparse_low_quality_fragments()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "small shim",
                2,
                10,
                [1],
                Quality: new PdfPageExtractionQuality(
                    "low_text",
                    TextEmpty: false,
                    TextSparse: true,
                    OcrCandidate: true,
                    AverageCharsPerWord: 5.0,
                    Signals: ["sparse_text_on_page"]))
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "small shim",
                10,
                2,
                [2],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/SparseFragment.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "small shim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_excludes_sparse_low_quality_units_from_profile_corpus_when_reliable_units_exist()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Reliable maintenance procedure requires isolation and documented verification.",
                10,
                71,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Maintenance", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "false ocr topic",
                15,
                3,
                [2],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"]),
            new ExtractedDocumentUnit(
                1,
                0,
                1,
                1,
                "Reliable maintenance procedure requires isolation and documented verification.",
                71,
                8,
                [3])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Maintenance.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains("maintenance", profile.Keywords);
        Assert.Contains("Reliable maintenance", profile.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("false ocr topic", profile.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("false", profile.Keywords);
        Assert.DoesNotContain("false ocr topic", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_skips_page_embedded_titles_on_sparse_low_quality_pages()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "FALSE OCR TITLE\nx y z",
                5,
                21,
                [1],
                Quality: new PdfPageExtractionQuality(
                    "low_text",
                    TextEmpty: false,
                    TextSparse: true,
                    OcrCandidate: true,
                    AverageCharsPerWord: 4.2,
                    Signals: ["sparse_text_on_page"]))
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/SparsePage.pdf",
            pages,
            sections,
            units: [],
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "FALSE OCR TITLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_skips_section_cards_on_sparse_low_quality_pages()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "FALSE OCR SECTION",
                3,
                17,
                [1],
                Quality: new PdfPageExtractionQuality(
                    "low_text",
                    TextEmpty: false,
                    TextSparse: true,
                    OcrCandidate: true,
                    AverageCharsPerWord: 5.7,
                    Signals: ["sparse_text_on_page"]))
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "FALSE OCR SECTION", 1, 1, 1, 1, null)
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/SparseSection.pdf",
            pages,
            sections,
            units: [],
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "FALSE OCR SECTION", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_does_not_create_content_cards_from_navigation_sections()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18 Index of procedures 24",
                16,
                111,
                [1]),
            new ExtractedPdfPage(
                2,
                "LOCKOUT TAGOUT PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                17,
                119,
                [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "LOCKOUT TAGOUT PROCEDURE", 1, 2, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18 Index of procedures 24",
                111,
                16,
                [3]),
            new ExtractedDocumentUnit(
                1,
                1,
                2,
                2,
                "LOCKOUT TAGOUT PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                119,
                17,
                [4])
        };

        var profile = DocumentProfileProjector.Project("Maintenance/lockout-guide.pdf", pages, sections, units, exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "LOCKOUT TAGOUT PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("contents", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("Index of procedures", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Index of procedures", profile.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOCKOUT TAGOUT PROCEDURE", profile.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Índice Seguridad 3 Procedimiento de bloqueo 8 Mantenimiento 12 Anexos 18", "Índice")]
    [InlineData("Sumário Segurança 3 Procedimento de bloqueio 8 Manutenção 12 Anexos 18", "Sumário")]
    [InlineData("Sommario Sicurezza 3 Procedura di blocco 8 Manutenzione 12 Allegati 18", "Sommario")]
    [InlineData("Inhaltsverzeichnis Sicherheit 3 Verriegelungsverfahren 8 Wartung 12 Anhänge 18", "Inhaltsverzeichnis")]
    public void Project_does_not_create_content_cards_from_multilingual_navigation_sections(string navigationText, string title)
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, navigationText, 12, navigationText.Length, [1]),
            new ExtractedPdfPage(
                2,
                "SAFETY PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                15,
                106,
                [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, title, 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "SAFETY PROCEDURE", 1, 2, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, navigationText, navigationText.Length, 12, [3]),
            new ExtractedDocumentUnit(
                1,
                1,
                2,
                2,
                "SAFETY PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                106,
                15,
                [4])
        };

        var profile = DocumentProfileProjector.Project("Generic/navigation.pdf", pages, sections, units, exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAFETY PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_does_not_fallback_to_restricted_units_when_profile_content_is_unavailable()
    {
        const string noisyUnit = "FakeSpecificNoiseMarker a b c d";
        var pages = new[]
        {
            new ExtractedPdfPage(1, noisyUnit, 5, noisyUnit.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Contents", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                noisyUnit,
                noisyUnit.Length,
                5,
                [2],
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true)
        };

        var profile = DocumentProfileProjector.Project("Generic/restricted-profile.pdf", pages, sections, units, exactMatchEntries: []);

        Assert.DoesNotContain("FakeSpecificNoiseMarker", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Keywords, keyword => keyword.Contains("fakespecific", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(profile.ContentCards);
        Assert.Contains("restricted-profile.pdf", profile.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_uses_neutral_extract_when_language_is_not_known()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "Onderhoud veiligheidscontrole installatiehandleiding drukventiel.", 4, 61, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Onderhoud", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Onderhoud veiligheidscontrole installatiehandleiding drukventiel.", 61, 4, [2])
        };

        var profile = DocumentProfileProjector.Project("Kennisbank/Handleiding.pdf", pages, sections, units, exactMatchEntries: []);

        Assert.Equal("nl", profile.Language);
        Assert.Contains("Handleiding.pdf", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Onderhoud", profile.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Sections principales", profile.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Premiers extraits", profile.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.HypotheticalQuestions, question => question.StartsWith("Que dit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("deterministic_profile_from_extracted_text", profile.Limits);
    }

    [Theory]
    [InlineData("Kennisbank/Handleiding.pdf", "Onderhoud en veiligheidscontroles voor de installatie.", "nl")]
    [InlineData("Arabic/Guide.pdf", "يشرح هذا المستند اجراءات السلامة والصيانة للمعدات.", "ar")]
    [InlineData("Chinese/Manual.pdf", "本文件介绍安全联锁状态和维护要求。", "zh")]
    [InlineData("Russian/Manual.pdf", "Документ описывает требования безопасности и техническое обслуживание.", "ru")]
    public void Project_detects_non_ui_document_languages_without_ui_language_limits(
        string docPath,
        string text,
        string expectedLanguage)
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 8, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 8, [2])
        };

        var profile = DocumentProfileProjector.Project(docPath, pages, sections, units, exactMatchEntries: []);

        Assert.Equal(expectedLanguage, profile.Language);
        Assert.Contains(Path.GetFileName(docPath), profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("deterministic_profile_from_extracted_text", profile.Limits);
    }

    [Fact]
    public void Project_deduplicates_content_cards_after_diacritic_folding()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Cafe safety requires clean equipment. Cafe safety also requires documented checks.",
                10,
                76,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Café Safety", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Cafe Safety", 1, 1, 1, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Cafe safety requires clean equipment.", 37, 5, [2]),
            new ExtractedDocumentUnit(1, 1, 1, 1, "Cafe safety also requires documented checks.", 43, 6, [3])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Safety.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Equal(
            1,
            profile.ContentCards.Count(card =>
                string.Equals(card.Title, "Café Safety", StringComparison.Ordinal)
                || string.Equals(card.Title, "Cafe Safety", StringComparison.Ordinal)));
    }

    [Fact]
    public void Project_filters_generic_ocr_noise_content_card_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "WARNING een 6 ¡PASO i iii 6\nSafety symbols are used to identify hazards.",
                12,
                74,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "WARNING een 6 ¡PASO i iii 6", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Xjs qz Aew", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "RRRREEEE RERERERERE TTTTEEEE 0", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(3, "T T N", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(4, "Safety symbols", 1, 1, 1, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "WARNING een 6 ¡PASO i iii 6", 30, 6, [2]),
            new ExtractedDocumentUnit(1, 1, 1, 1, "Xjs qz Aew", 10, 3, [3]),
            new ExtractedDocumentUnit(2, 2, 1, 1, "RRRREEEE RERERERERE TTTTEEEE 0", 33, 4, [4]),
            new ExtractedDocumentUnit(3, 3, 1, 1, "T T N", 5, 3, [5]),
            new ExtractedDocumentUnit(4, 4, 1, 1, "Safety symbols are used to identify hazards.", 44, 6, [6])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/OcrNoise.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("¡PASO", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Xjs qz Aew", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("RERERERERE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "T T N", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Safety symbols", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_filters_metadata_labels_and_dangling_fragments_without_category_hardcoding()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Modes de\nPas cher\nQ Facile\n& Repos\nAU MODULE\nAssez cher\nESA Pas cher\nFacile Pour la validation\nIntermediaire Pour 4 personnes\nCuisson : 40 minutes\nQD Repos : 3 heures\nCONTROL HANDOVER PLAN\nProcedure 1. Check status. 2. Record notes.",
                30,
                166,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Modes de", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Categories de documents", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "Assez cher", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(3, "ESA Pas cher", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(4, "Facile Pour la validation", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(5, "Intermediaire Pour 4 personnes", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(6, "Cuisson : 40 minutes", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(7, "QD Repos : 3 heures", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(8, "CONTROL HANDOVER PLAN", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Modes de", 8, 2, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Pas cher", 8, 2, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Q Facile", 8, 2, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "& Repos", 7, 2, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "AU MODULE", 9, 2, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "Assez cher", 10, 2, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "ESA Pas cher", 13, 3, [8]),
            new ExtractedDocumentUnit(7, 0, 1, 1, "Facile Pour la validation", 24, 4, [9]),
            new ExtractedDocumentUnit(8, 0, 1, 1, "Intermediaire Pour 4 personnes", 31, 4, [10]),
            new ExtractedDocumentUnit(9, 0, 1, 1, "Cuisson : 40 minutes", 20, 3, [11]),
            new ExtractedDocumentUnit(10, 0, 1, 1, "QD Repos : 3 heures", 20, 4, [12]),
            new ExtractedDocumentUnit(
                11,
                8,
                1,
                1,
                "CONTROL HANDOVER PLAN\nProcedure 1. Check status. 2. Record notes.",
                68,
                9,
                [13])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/QualityLabels.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Modes de", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Categories de documents", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Pas cher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Q Facile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "& Repos", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "AU MODULE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Assez cher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ESA Pas cher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Facile Pour la validation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Intermediaire Pour 4 personnes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Cuisson : 40 minutes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "QD Repos : 3 heures", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_filters_short_standalone_standard_like_noise_from_content_cards()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "The document mentions EN 15281 as a real standard and also contains the phrase EN 12 from OCR noise.",
                18,
                98,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Reference overview", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].Text.Length, 18, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 12", "en 12", 5, 2, [3], "standard_ref"),
            new ExtractedExactMatchEntry(1, 0, 0, 1, 1, "EN 15281", "en 15281", 8, 2, [4], "standard_ref")
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Standards.pdf",
            pages,
            sections,
            units,
            exact);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "EN 12", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "EN 15281", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_filters_lowercase_section_fragments_without_category_hardcoding()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "control washer\nsmall shim\nSafety Symbols\nOperational Playbook\nUse approved controls.",
                18,
                82,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "control washer", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "small shim", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "Safety Symbols", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(3, "Operational Playbook", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, pages[0].Text, pages[0].Text.Length, 10, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Fragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "control washer", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "small shim", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Safety Symbols", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Operational Playbook", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProfile_filters_instruction_and_measured_sentence_fragment_cards()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Generic operational profile.",
            [],
            [],
            [],
            [],
            [],
            "Generic/Instructions.pdf",
            "Instructions.pdf",
            [
                new DocumentProfileContentCard("Prepare the batch", 1, 1, "exact_lead", ["prepare"]),
                new DocumentProfileContentCard("Prepare the ganache e 125g flour e 2c", 1, 1, "exact_lead", ["prepare", "125g"]),
                new DocumentProfileContentCard("Cycle: Ih 30 minutes", 2, 2, "exact_lead", ["30 minutes"]),
                new DocumentProfileContentCard("Duration : Ih 30 minutes", 2, 2, "section", ["30 minutes"]),
                new DocumentProfileContentCard("During this step, verify the control relay", 3, 3, "exact_lead", ["verify"]),
                new DocumentProfileContentCard("On another station, record the pressure", 4, 4, "exact_lead", ["record"]),
                new DocumentProfileContentCard("Unless the operator confirms the reference value", 5, 5, "exact_lead", ["operator"]),
                new DocumentProfileContentCard("Controlez et consignez les mesures du relais", 6, 6, "exact_lead", ["controlez"]),
                new DocumentProfileContentCard("With sensor mode, \"medium\"", 7, 7, "exact_lead", ["sensor"]),
                new DocumentProfileContentCard("Component size (75 g", 8, 8, "section", ["75 g"]),
                new DocumentProfileContentCard("Y Placecomponentsinthe service tray", 9, 9, "exact_lead", ["tray"]),
                new DocumentProfileContentCard("INGREDIENTSPREPARATION18BE A MASTER", 10, 10, "page_embedded_title", ["ingredients"]),
                new DocumentProfileContentCard("INGREDIENTESPREPARACIONBE A MASTER", 11, 11, "page_embedded_title", ["ingredients"]),
                new DocumentProfileContentCard("DES MODULES CASSES CA REPOUSSE TOUT SEULW Certains elements", 11, 11, "section", ["modules"]),
                new DocumentProfileContentCard("PY I", 11, 11, "exact_lead", ["py"]),
                new DocumentProfileContentCard("Sans nettoyer le module, ajouter la piece de controle", 12, 12, "exact_lead", ["module"]),
                new DocumentProfileContentCard("Par contre, conservez les elements separes", 13, 13, "exact_lead", ["elements"]),
                new DocumentProfileContentCard("Une fois le cycle termine, couvrir le contenant", 14, 14, "exact_lead", ["cycle"]),
                new DocumentProfileContentCard("un contenant hermetique", 15, 15, "section", ["contenant"]),
                new DocumentProfileContentCard("g de composants calibres Dans le bac", 16, 16, "section", ["composants"]),
                new DocumentProfileContentCard("The committee also approved standard definitions and limits for the colors", 17, 17, "exact_lead", ["committee"]),
                new DocumentProfileContentCard("Automation makes security-related information readily available in an environment where ongoing monitoring needs change", 18, 18, "exact_lead", ["automation"]),
                new DocumentProfileContentCard("Control Handover Plan", 2, 2, "exact_lead", ["control"])
            ]);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Prepare the batch", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Prepare the ganache e 125g flour e 2c", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Cycle: Ih 30 minutes", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Duration : Ih 30 minutes", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "During this step, verify the control relay", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "On another station, record the pressure", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Unless the operator confirms the reference value", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Controlez et consignez les mesures du relais", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "With sensor mode, \"medium\"", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Component size (75 g", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Y Placecomponentsinthe service tray", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "INGREDIENTSPREPARATION18BE A MASTER", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "INGREDIENTESPREPARACIONBE A MASTER", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "DES MODULES CASSES CA REPOUSSE TOUT SEULW Certains elements", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "PY I", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Sans nettoyer le module, ajouter la piece de controle", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Par contre, conservez les elements separes", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Une fois le cycle termine, couvrir le contenant", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "un contenant hermetique", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "g de composants calibres Dans le bac", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "The committee also approved standard definitions and limits for the colors", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Automation makes security-related information readily available in an environment where ongoing monitoring needs change", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Control Handover Plan", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProfile_filters_truncated_standard_reference_cards_and_normative_sentence_leads()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Generic technical profile.",
            [], [], [], [], [],
            "Generic/Standards.pdf",
            "Standards.pdf",
            [
                new DocumentProfileContentCard("ISO 545", 1, 1, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("ISO 5457, Technical product documentation", 1, 1, "exact_lead", ["iso", "documentation"]),
                new DocumentProfileContentCard("EN ISO 5457 199", 2, 2, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("EN ISO 5457 1999", 2, 2, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("ISO 7200:2004(E", 3, 3, "section", ["iso"]),
                new DocumentProfileContentCard("ISO 7200:2004", 3, 3, "standard_ref", ["iso"]),
                new DocumentProfileContentCard("The identification number shall be unique", 4, 4, "exact_lead", ["identification"]),
                new DocumentProfileContentCard("ISO shall not be held responsible", 5, 5, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("ISO collaborates closely", 6, 6, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("EN 15281", 7, 7, "standard_ref", ["en"]),
                new DocumentProfileContentCard("NFPA 7", 8, 8, "exact_lead", ["nfpa"]),
                new DocumentProfileContentCard("NFPA 70", 8, 8, "standard_ref", ["nfpa"]),
                new DocumentProfileContentCard("UL 50", 9, 9, "standard_ref", ["ul"]),
                new DocumentProfileContentCard("One reference to recommended practices on static electricity is NFPA 77", 10, 10, "exact_lead", ["nfpa"]),
                new DocumentProfileContentCard("NFPA 77, Recommended Practice on Static Electricity", 10, 10, "standard_ref", ["nfpa"]),
                new DocumentProfileContentCard("The detailed selection and testing", 11, 11, "exact_lead", ["selection"]),
                new DocumentProfileContentCard("Without demonstrated understandability", 12, 12, "exact_lead", ["understandability"]),
                new DocumentProfileContentCard("A.16.2,3 See NFPA 70E", 13, 13, "exact_lead", ["nfpa"]),
                new DocumentProfileContentCard("To study the special electrical problems involved", 14, 14, "exact_lead", ["study"]),
                new DocumentProfileContentCard("ASTM B", 15, 15, "exact_lead", ["astm"]),
                new DocumentProfileContentCard("ASTM B174, Standard Specification", 15, 15, "standard_ref", ["astm"]),
                new DocumentProfileContentCard("ASTM B8, Standard Specification", 15, 15, "standard_ref", ["astm"]),
                new DocumentProfileContentCard("Other parts of ISO", 16, 16, "exact_lead", ["iso"]),
                new DocumentProfileContentCard("Other parts of ISO 14644 may provide complementary information", 16, 16, "exact_lead", ["iso", "cleanroom"]),
                new DocumentProfileContentCard("# UL 121201 This is the harmonized CSA Group and UL standard", 17, 17, "exact_lead", ["ul", "standard"]),
                new DocumentProfileContentCard("Series Fuseholders CAN/CSA C22.2 No", 18, 18, "exact_lead", ["fuseholders"])
            ]);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ISO 545", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ISO 5457, Technical product documentation", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "EN ISO 5457 199", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "EN ISO 5457 1999", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ISO 7200:2004(E", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ISO 7200:2004", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "The identification number shall be unique", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ISO shall not be held responsible", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ISO collaborates closely", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "EN 15281", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "NFPA 7", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "NFPA 70", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "UL 50", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "One reference to recommended practices on static electricity is NFPA 77", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "NFPA 77, Recommended Practice on Static Electricity", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "The detailed selection and testing", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Without demonstrated understandability", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "A.16.2,3 See NFPA 70E", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "To study the special electrical problems involved", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ASTM B", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ASTM B174, Standard Specification", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ASTM B8, Standard Specification", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Other parts of ISO", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Other parts of ISO 14644 may provide complementary information", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("#", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "UL 121201 This is the harmonized CSA Group and UL standard", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Series Fuseholders CAN/CSA C22.2 No", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProfile_keeps_lowercase_source_backed_content_cards()
    {
        var evidence = new DocumentProfileCardEvidence(
            "content_card_evidence_v1",
            ScaleBasis: null,
            QuantityFacts: [],
            NonScalableReasons: [],
            Confidence: 0.92,
            Language: "en",
            Facts:
            [
                new DocumentProfileEvidenceFact(
                    "procedure",
                    "source-backed title",
                    "validated",
                    Unit: null,
                    SourceText: "alpha lowercase workflow with concrete evidence",
                    PageStart: 4,
                    PageEnd: 4)
            ]);

        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Generic operational profile.",
            [],
            [],
            [],
            [],
            [],
            "Generic/Lowercase.pdf",
            "Lowercase.pdf",
            [
                new DocumentProfileContentCard(
                    "alpha lowercase workflow",
                    4,
                    4,
                    "exact_lead",
                    ["validated"],
                    evidence),
                new DocumentProfileContentCard(
                    "gamma lowercase workflow",
                    6,
                    6,
                    "section",
                    ["validated_section"],
                    evidence),
                new DocumentProfileContentCard(
                    "The committee also approved standard definitions and limits for the colors",
                    5,
                    5,
                    "exact_lead",
                    ["approved"],
                    evidence),
                new DocumentProfileContentCard("beta lowercase heading", 5, 5, "exact_lead", ["weak"])
            ]);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "alpha lowercase workflow", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "gamma lowercase workflow", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "The committee also approved standard definitions and limits for the colors", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "beta lowercase heading", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProfile_keeps_lowercase_card_with_grounded_quantity_evidence_and_page()
    {
        var evidence = new DocumentProfileCardEvidence(
            "content_card_evidence_v1",
            ScaleBasis: new DocumentProfileScaleBasis(4, "items"),
            QuantityFacts:
            [
                new DocumentProfileQuantityFact(12, "kg", "validated load", "12 kg validated load"),
                new DocumentProfileQuantityFact(3, "min", "hold time", "3 min hold time")
            ],
            NonScalableReasons: [],
            Confidence: 0.82,
            Language: "en",
            Facts: []);

        var profile = DocumentProfileProjector.BuildProfile(
            "deterministic_v1",
            "en",
            "Generic operational profile.",
            [], [], [], [], [],
            "Generic/Grounded.pdf",
            "Grounded.pdf",
            [
                new DocumentProfileContentCard(
                    "gamma validated workflow",
                    7,
                    7,
                    "section",
                    ["quantity_list"],
                    evidence),
                new DocumentProfileContentCard(
                    "delta unsourced workflow",
                    null,
                    null,
                    "section",
                    ["weak"],
                    evidence)
            ]);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "gamma validated workflow", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "delta unsourced workflow", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_does_not_create_content_cards_from_mixed_navigation_units()
    {
        var mixedNavigation = """
Controls overview 3
Maintenance plan 18
Alarm reset 22
Lockout checklist 27
Appendix 31
Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.
""";
        var content = "CONTROL HANDOVER PLAN\nProcedure 1. Check status. 2. Record notes.";
        Assert.Equal(
            RetrievalContentClassifier.MixedNavigationContentRole,
            RetrievalContentClassifier.AnalyzeChunk(mixedNavigation).ContentRole);

        var pages = new[]
        {
            new ExtractedPdfPage(1, mixedNavigation, 26, mixedNavigation.Length, [1]),
            new ExtractedPdfPage(2, content, 9, content.Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Lockout checklist", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "CONTROL HANDOVER PLAN", 2, 2, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, mixedNavigation, mixedNavigation.Length, 26, [3]),
            new ExtractedDocumentUnit(1, 1, 2, 2, content, content.Length, 9, [4])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/MixedNavigation.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Lockout checklist", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "Controls overview", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_excludes_mixed_navigation_terms_from_profile_corpus_when_content_units_exist()
    {
        var mixedNavigation = """
Controls overview 3
Maintenance plan 18
Alarm reset 22
Lockout checklist 27
Appendix 31
CatalogPollutionMarker Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.
""";
        var content = "CONTROL HANDOVER PLAN\nProcedure 1. Check status. 2. Record notes for validated handover.";
        Assert.Equal(
            RetrievalContentClassifier.MixedNavigationContentRole,
            RetrievalContentClassifier.AnalyzeChunk(mixedNavigation).ContentRole);

        var pages = new[]
        {
            new ExtractedPdfPage(1, mixedNavigation, 29, mixedNavigation.Length, [1]),
            new ExtractedPdfPage(2, content, 11, content.Length, [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, mixedNavigation, mixedNavigation.Length, 29, [3]),
            new ExtractedDocumentUnit(1, 0, 2, 2, content, content.Length, 11, [4])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/ProfileCorpus.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain("CatalogPollutionMarker", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Keywords, keyword => string.Equals(keyword, "catalogpollutionmarker", StringComparison.Ordinal));
        Assert.Contains("CONTROL HANDOVER PLAN", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains(profile.Keywords, keyword => string.Equals(keyword, "handover", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_excludes_navigation_section_titles_from_profile_search_text()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "CONTROL HANDOVER PLAN Procedure 1. Check status. 2. Record notes for validated handover.",
                12,
                84,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Table of contents", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Appendix 4", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "CONTROL HANDOVER PLAN", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 2, 1, 1, pages[0].Text, pages[0].Text.Length, 12, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/SectionTitleNoise.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain("Table of contents", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Appendix 4", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Topics, topic => string.Equals(topic, "Table of contents", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.Topics, topic => string.Equals(topic, "Appendix 4", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("CONTROL HANDOVER PLAN", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains(profile.Topics, topic => string.Equals(topic, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_excludes_measure_and_lowercase_fragment_section_titles_from_profile_search_text()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Validated operating method requires documented checks before release.",
                8,
                65,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Duration : 1h 30 minutes", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "control washer", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "Validated Operating Method", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 2, 1, 1, pages[0].Text, pages[0].Text.Length, 8, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/SectionFragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain("Duration", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("control washer", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Topics, topic => topic.Contains("Duration", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.Topics, topic => string.Equals(topic, "control washer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Validated Operating Method", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains(profile.Topics, topic => string.Equals(topic, "Validated Operating Method", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_does_not_leak_rejected_section_title_into_unit_card_signals()
    {
        const string rejectedMarker = "CatalogPollutionMarker";
        const string title = "CONTROL HANDOVER PLAN";
        var text = $"{title}\nProcedure 1. Check status. 2. Record notes for validated handover.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 11, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, $"Table of contents {rejectedMarker} 18", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 11, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/RejectedSectionContext.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var card = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.DoesNotContain(card.Signals, signal => signal.Contains(rejectedMarker, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rejectedMarker, profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_keeps_technical_identifier_cards_despite_numeric_title_filters()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "ISO 13849-1\nSafety-related control functions require validation.\nReferences IEC 61508-2\nDiagnostic coverage shall be documented.\nEN60204-1\nElectrical equipment requirements.",
                22,
                176,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Technical standards", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "ISO 13849-1\nSafety-related control functions require validation.", 64, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "References IEC 61508-2\nDiagnostic coverage shall be documented.", 63, 7, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "EN60204-1\nElectrical equipment requirements.", 40, 4, [4])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "ISO/IEC 27001", "iso iec 27001", 12, 2, [5], "standard_ref")
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/TechnicalIdentifiers.pdf",
            pages,
            sections,
            units,
            exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ISO 13849-1", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "References IEC 61508-2", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "EN60204-1", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "ISO/IEC 27001", StringComparison.Ordinal));
        Assert.Contains("ISO 13849-1", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("IEC 61508-2", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("EN60204-1", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("ISO/IEC 27001", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ocr_noise_filter_keeps_dense_technical_identifier_lines()
    {
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(
            "ISO13849-1 IEC61508-2 EN60204-1 ISO/IEC27001 UL508A SIL2 PLd"));
    }

    [Fact]
    public void Project_builds_generic_content_cards_from_titles_without_category_hardcoding()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.\nSAUCE BEARNAISE\n20 min. Ingredients: butter, egg yolks.",
                16,
                112,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operational Playbook", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.", 58, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "SAUCE BEARNAISE\n20 min. Ingredients: butter, egg yolks.", 54, 8, [3])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Playbook.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Operational Playbook", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "LOCKOUT TAGOUT PROCEDURE", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BEARNAISE", StringComparison.Ordinal));
        Assert.Contains("LOCKOUT TAGOUT PROCEDURE", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("SAUCE BEARNAISE", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_adds_structured_scalability_signals_to_content_cards()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "MODULE COMPACT ALPHA\nBase 4 elements | 400 g de matiere de base | 5 cl de liant | 30 cl de fluide porteur. Procedure: assembler.",
                20,
                139,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Module compact alpha", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "MODULE COMPACT ALPHA\nBase 4 elements | 400 g de matiere de base | 5 cl de liant | 30 cl de fluide porteur. Procedure: assembler.",
                139,
                20,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Operations/CompactModule.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var card = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, "MODULE COMPACT ALPHA", StringComparison.Ordinal));
        Assert.Contains("scale_basis", card.Signals);
        Assert.Contains("scale_basis_count:4", card.Signals);
        Assert.Contains("scale_basis_label:elements", card.Signals);
        Assert.Contains("quantity_list", card.Signals);
        Assert.Contains("structured_facts", card.Signals);
        Assert.Contains("scalable_quantities", card.Signals);
        Assert.NotNull(card.Evidence);
        Assert.Equal("content_card_evidence_v1", card.Evidence!.SchemaVersion);
        Assert.Equal(4, card.Evidence.ScaleBasis!.Count);
        Assert.Equal("elements", card.Evidence.ScaleBasis.Label);
        Assert.Contains(card.Evidence.QuantityFacts, fact =>
            fact.Value == 400
            && string.Equals(fact.Unit, "g", StringComparison.Ordinal)
            && fact.Label.Contains("matiere de base", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(card.Evidence.Facts!, fact =>
            string.Equals(fact.Kind, "scale_basis", StringComparison.Ordinal)
            && string.Equals(fact.Value, "4", StringComparison.Ordinal)
            && string.Equals(fact.Label, "elements", StringComparison.Ordinal)
            && fact.PageStart == 1
            && fact.PageEnd == 1);
        Assert.Contains(card.Evidence.Facts!, fact =>
            string.Equals(fact.Kind, "quantity", StringComparison.Ordinal)
            && string.Equals(fact.Unit, "g", StringComparison.Ordinal)
            && fact.Label.Contains("matiere de base", StringComparison.OrdinalIgnoreCase)
            && fact.PageStart == 1
            && fact.PageEnd == 1);
        Assert.Contains("scalable_quantities", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("structured_facts", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_scopes_page_embedded_card_evidence_to_its_local_layout_block()
    {
        const string firstBlock =
            "MODULE COMPACT ALPHA\nBase 4 elements | 400 g alpha material | 5 cl alpha binder. "
            + "Procedure: assemble the components, verify alignment, record the measurements, inspect every fastener, and release the batch after control.";
        const string secondBlock =
            "VALVE CONTROL\nASSEMBLY\nBase 6 elements | 900 ml beta fluid | 12 kg beta material. "
            + "Procedure: prepare the assembly, validate positioning, document the readings, examine every connector, and approve the package after review.";
        var pageText = $"{firstBlock}\n\n\n{secondBlock}";
        var wordCount = pageText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var pages = new[]
        {
            new ExtractedPdfPage(1, pageText, wordCount, pageText.Length, [1])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/TwoItems.pdf",
            pages,
            sections: [],
            units: [],
            exactMatchEntries: []);

        var alpha = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, "MODULE COMPACT ALPHA", StringComparison.Ordinal));
        var beta = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, "VALVE CONTROL ASSEMBLY", StringComparison.Ordinal));

        Assert.Contains(alpha.Evidence!.QuantityFacts, fact => fact.SourceText.Contains("alpha", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(alpha.Evidence.QuantityFacts, fact => fact.SourceText.Contains("beta", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(beta.Evidence!.QuantityFacts, fact => fact.SourceText.Contains("beta", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(beta.Evidence.QuantityFacts, fact => fact.SourceText.Contains("alpha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_keeps_multiline_short_heading_when_its_local_region_has_grounded_evidence()
    {
        const string pageText =
            "MUFFINS À\nLA COURGETTE\nPRÉPARATION\nINGRÉDIENTS\nBase 6 portions | 750 ml de courgettes | 310 ml de sucre | 10 ml de poudre à pâte. "
            + "Mélanger les ingrédients, répartir la préparation dans les moules et cuire au four.";
        var wordCount = pageText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        var profile = DocumentProfileProjector.Project(
            "Generic/MultilineHeading.pdf",
            [new ExtractedPdfPage(1, pageText, wordCount, pageText.Length, [1])],
            sections: [],
            units: [],
            exactMatchEntries: []);

        var card = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, "MUFFINS À LA COURGETTE", StringComparison.Ordinal));
        Assert.Contains(
            card.Evidence!.QuantityFacts,
            fact => fact.SourceText.Contains("courgettes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            profile.ContentCards,
            item => item.Title.StartsWith("PRÉPARATION ", StringComparison.OrdinalIgnoreCase)
                    || item.Title.StartsWith("INGRÉDIENTS ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildProfile_rejects_instruction_lead_even_when_structured_evidence_is_present()
    {
        var evidence = new DocumentProfileCardEvidence(
            "content_card_evidence_v1",
            null,
            [
                new DocumentProfileQuantityFact(60, "ml", "mixture", "60 ml mixture"),
                new DocumentProfileQuantityFact(12, "count", "molds", "12 molds")
            ],
            [],
            0.82,
            "fr",
            []);
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "deterministic_v1",
            language: "fr",
            summaryText: "Structured instructions.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/Instructions.pdf",
            docName: "Instructions.pdf",
            contentCards:
            [
                new DocumentProfileContentCard(
                    "Remplir chacun des moules avec",
                    1,
                    1,
                    "page_embedded_title",
                    [],
                    evidence)
            ]);

        Assert.Empty(profile.ContentCards);
    }

    [Fact]
    public void Project_marks_parameter_content_cards_non_scalable_without_scaling_signals()
    {
        const string text = "CONTROL CHECK\nSafety context: pressure 2 bar | temperature 70 C | speed 1500 rpm | reference EN 60204 validation.";
        var pages = new[]
        {
            new ExtractedPdfPage(1, text, 16, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Parameter guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, text, text.Length, 16, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/ParameterCard.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var card = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, "CONTROL CHECK", StringComparison.Ordinal));
        Assert.NotNull(card.Evidence);
        Assert.Null(card.Evidence!.ScaleBasis);
        Assert.Empty(card.Evidence.QuantityFacts);
        Assert.Contains("safety_or_parameter_context", card.Evidence.NonScalableReasons);
        Assert.DoesNotContain("scale_basis", card.Signals);
        Assert.DoesNotContain("quantity_list", card.Signals);
        Assert.DoesNotContain("scalable_quantities", card.Signals);
        Assert.Contains("non_scalable_quantities", card.Signals);
    }

    [Fact]
    public void BuildProfile_derives_structured_signals_from_supplied_card_evidence()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: "en",
            summaryText: "Profile with externally supplied structured evidence.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/StructuredEvidence.pdf",
            docName: "StructuredEvidence.pdf",
            contentCards:
            [
                new DocumentProfileContentCard(
                    "CONTROL PACKAGE ALPHA",
                    1,
                    1,
                    "llm_content_card",
                    [],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        new DocumentProfileScaleBasis(4, "elements"),
                        [
                            new DocumentProfileQuantityFact(400, "g", "base material", "400 g base material"),
                            new DocumentProfileQuantityFact(5, "cl", "binder", "5 cl binder")
                        ],
                        [],
                        0.81,
                        "en",
                        []))
            ]);

        var card = Assert.Single(profile.ContentCards);
        Assert.Contains("scale_basis", card.Signals);
        Assert.Contains("scale_basis_count:4", card.Signals);
        Assert.Contains("quantity_list", card.Signals);
        Assert.Contains("scalable_quantities", card.Signals);
        Assert.Contains("scale_basis_count:4", profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProfile_derives_content_card_page_range_from_supplied_card_evidence()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: "en",
            summaryText: "Profile with externally supplied page evidence.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/PageEvidence.pdf",
            docName: "PageEvidence.pdf",
            contentCards:
            [
                new DocumentProfileContentCard(
                    "Release evidence card",
                    null,
                    null,
                    "llm_content_card",
                    ["release"],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        null,
                        [],
                        [],
                        0.84,
                        "en",
                        [
                            new DocumentProfileEvidenceFact(
                                "requirement",
                                "release approval",
                                null,
                                null,
                                "Release approval is required.",
                                9,
                                10,
                                0.84)
                        ]))
            ]);

        var card = Assert.Single(profile.ContentCards);
        Assert.Equal(9, card.PageStart);
        Assert.Equal(10, card.PageEnd);
    }

    [Fact]
    public void BuildProfile_keeps_broad_evidence_only_cards_document_scoped()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: "en",
            summaryText: "Profile with broad page evidence.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/BroadEvidence.pdf",
            docName: "BroadEvidence.pdf",
            contentCards:
            [
                new DocumentProfileContentCard(
                    "Broad evidence card",
                    null,
                    null,
                    "llm_content_card",
                    ["release"],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        null,
                        [],
                        [],
                        0.74,
                        "en",
                        [
                            new DocumentProfileEvidenceFact("requirement", "first fact", null, null, "First fact.", 1, 1, 0.74),
                            new DocumentProfileEvidenceFact("requirement", "later fact", null, null, "Later fact.", 30, 30, 0.74)
                        ]))
            ]);

        var card = Assert.Single(profile.ContentCards);
        Assert.Null(card.PageStart);
        Assert.Null(card.PageEnd);
    }

    [Fact]
    public void BuildProfile_discards_generic_dangling_fragment_content_card_titles()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: "en",
            summaryText: "Profile with externally supplied card titles.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/Fragments.pdf",
            docName: "Fragments.pdf",
            contentCards:
            [
                new DocumentProfileContentCard("l evolution de la", 2, 2, "section", [], null),
                new DocumentProfileContentCard("on nn . il \u00ab Sauces salees et", 3, 3, "section", [], null),
                new DocumentProfileContentCard("a cafe de levure chimique", 3, 3, "Section", [], null),
                new DocumentProfileContentCard("e Mettre la farine dans le bol", 3, 3, "section", [], null),
                new DocumentProfileContentCard("Sur chaque tranche de pain beurre, poser", 3, 3, "section", [], null),
                new DocumentProfileContentCard("DES CONTROLES CASSES CA REPOUSSE TOUT SEULW", 3, 3, "section", [], null),
                new DocumentProfileContentCard("DE VOLAILLE COCO-CURRY", 3, 3, "section", [], null),
                new DocumentProfileContentCard("AU CARAMEL BEURRE DEMI", 3, 3, "section", [], null),
                new DocumentProfileContentCard("Safety symbols", 4, 4, "section", [], null),
                new DocumentProfileContentCard("Vitamin A", 5, 5, "section", [], null),
                new DocumentProfileContentCard("Access mode A", 6, 6, "section", [], null)
            ]);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "l evolution de la", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("Sauces salees et", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "a cafe de levure chimique", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("e Mettre", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Sur chaque", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("DES CONTROLES", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("DE VOLAILLE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("AU CARAMEL", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Safety symbols", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Vitamin A", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Access mode A", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildProfile_keeps_grounded_lowercase_content_cards()
    {
        var profile = DocumentProfileProjector.BuildProfile(
            profileVersion: "llm_backoffice_v1",
            language: "en",
            summaryText: "Profile with source-backed lowercase title.",
            keywords: [],
            entities: [],
            topics: [],
            hypotheticalQuestions: [],
            limits: [],
            docPath: "Generic/GroundedLowercase.pdf",
            docName: "GroundedLowercase.pdf",
            contentCards:
            [
                new DocumentProfileContentCard(
                    "validated lowercase procedure",
                    9,
                    9,
                    "Section",
                    [],
                    new DocumentProfileCardEvidence(
                        "content_card_evidence_v1",
                        null,
                        [],
                        [],
                        0.82,
                        "en",
                        [
                            new DocumentProfileEvidenceFact(
                                "requirement",
                                "validated lowercase procedure",
                                null,
                                null,
                                "The validated lowercase procedure is explicitly described here.",
                                9,
                                9,
                                0.82)
                        ]))
            ]);

        var card = Assert.Single(profile.ContentCards);
        Assert.Equal("validated lowercase procedure", card.Title);
        Assert.Equal("section", card.Kind);
        Assert.NotNull(card.Evidence);
    }

    [Fact]
    public void Project_extracts_embedded_uppercase_title_when_followed_by_measurement()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Ajoutez 15 cl d'eau puis lancez le robot pour 12 min. Servez avec des steaks.6 personnes12 min5 minSAUCE AU POIVRE50 g de parmesan Sel Poivre.",
                24,
                146,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Ajoutez 15 cl d'eau puis lancez le robot pour 12 min. Servez avec des steaks.6 personnes12 min5 minSAUCE AU POIVRE50 g de parmesan Sel Poivre.",
                146,
                24,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Robot.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE AU POIVRE", StringComparison.Ordinal));
        Assert.Contains("SAUCE AU POIVRE", profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "SAUCE AU POIVRE50", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_discards_layout_noise_from_content_card_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "INGRÉDIENTSPRÉPARATION :1 gros oignon d'env. 150 g\nRumsteck aux oignons grillés [Index: ] OCR_CODE\nPour 4 personnesUne soupe simple\nCouv-etudiants.indd Toutes les pages",
                24,
                160,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Recipe Book", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "INGRÉDIENTSPRÉPARATION :1 gros oignon d'env. 150 g", 52, 6, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Rumsteck aux oignons grillés [Index: ] OCR_CODE", 45, 4, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Pour 4 personnesUne soupe simple", 33, 5, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Couv-etudiants.indd Toutes les pages", 37, 4, [5])
        };

        var profile = DocumentProfileProjector.Project(
            "Cuisine/Recipes.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Rumsteck aux oignons grillés", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("[Index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("INGRÉDIENTS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Pour 4 personnes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains(".indd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_lowercase_dangling_measure_fragments_from_content_cards()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "TARTE TEST Pour 6 personnes. Mélanger la farine et le sucre. Ajouter 1 cuillère à café de levure chimique puis cuire.",
                20,
                118,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "TARTE TEST", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "à café de levure chimique", 1, 1, 1, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "TARTE TEST Pour 6 personnes. Mélanger la farine et le sucre. Ajouter 1 cuillère à café de levure chimique puis cuire.",
                118,
                20,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Fragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "TARTE TEST", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "à café de levure chimique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_connector_lead_sentence_fragments_from_content_cards()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "CONTROL PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                15,
                106,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "CONTROL PROCEDURE", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Pour ne plus se poser la fameuse question", 1, 1, 1, 2, null),
            new ExtractedDocumentSection(2, "De plus, les articles se nettoient plus facilement", 1, 1, 1, 2, null),
            new ExtractedDocumentSection(3, "DES GUIDES POUR CHAQUE TYPE D'UTILISATEUR", 1, 1, 1, 2, null),
            new ExtractedDocumentSection(4, "Zum Beurteilen der Anwend", 1, 1, 1, 2, null),
            new ExtractedDocumentSection(5, "Zur Ermittlung des Zeitstandverhaltens sind Prufungen analog zu den", 1, 1, 1, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "CONTROL PROCEDURE Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy.",
                106,
                15,
                [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/ConnectorFragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "CONTROL PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Pour ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("De plus", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("DES GUIDES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Zum Beurteilen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Zur Ermittlung", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_instruction_sentence_and_repeated_header_cards_without_category_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "BE A MASTER. BECOME A CHEFPROTÉINESLÉGUMESHYDRATES DE CARBONEPRODUITS LAITIERS\n" +
                "Ouvrir le robot et retirer le panier vapeur avec précaution.\n" +
                "Programmer 10 secondes, vitesse 6 et fermer.\n" +
                "Amuse-bouches et apéritifsSaladesAccompagnements\n" +
                "CRÈME DE PETIT POIS ET D’AVOCATLa texture de l’avocat en fait un excellent ingrédient.\n" +
                "Basilic ou herbes aromatiques, à votre goût3 petites courgettes (environ 500 g)Préparation\n" +
                "(Recommencer cette opération si nécessaire jusqu’à ce que tous les ingrédients soient bien mélangés)\n" +
                "est idéal\n" +
                "Avec des gants et\n" +
                "Ensuite, programmer 5 secondes à vitesse 9\n" +
                "Savoureuse et très polyvalente, outre les pâtes, vous pourrez l’utiliser\n" +
                "fonctionne parfaitement aux vitesses 3 et 4\n" +
                "Utilisez le même couteau\n" +
                "Sel, à votre goût\n" +
                "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.",
                42,
                260,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "BE A MASTER. BECOME A CHEFPROTÉINESLÉGUMESHYDRATES", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Ouvrir le robot et retirer le panier vapeur avec précaution.", 61, 9, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Programmer 10 secondes, vitesse 6 et fermer.", 45, 6, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Amuse-bouches et apéritifsSaladesAccompagnements", 50, 4, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "CRÈME DE PETIT POIS ET D’AVOCATLa texture de l’avocat en fait un excellent ingrédient.", 90, 12, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Basilic ou herbes aromatiques, à votre goût3 petites courgettes (environ 500 g)Préparation", 91, 11, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "(Recommencer cette opération si nécessaire jusqu’à ce que tous les ingrédients soient bien mélangés)", 93, 10, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "est idéal", 9, 2, [8]),
            new ExtractedDocumentUnit(7, 0, 1, 1, "Avec des gants et", 17, 4, [9]),
            new ExtractedDocumentUnit(8, 0, 1, 1, "Ensuite, programmer 5 secondes à vitesse 9", 41, 6, [10]),
            new ExtractedDocumentUnit(9, 0, 1, 1, "Savoureuse et très polyvalente, outre les pâtes, vous pourrez l’utiliser", 70, 10, [11]),
            new ExtractedDocumentUnit(10, 0, 1, 1, "fonctionne parfaitement aux vitesses 3 et 4", 43, 7, [12]),
            new ExtractedDocumentUnit(11, 0, 1, 1, "Utilisez le même couteau", 23, 4, [13]),
            new ExtractedDocumentUnit(12, 0, 1, 1, "Sel, à votre goût", 17, 4, [14]),
            new ExtractedDocumentUnit(13, 0, 1, 1, "LOCKOUT TAGOUT PROCEDURE\nIsolate energy before maintenance.", 58, 7, [15])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/NoisyHeaders.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "LOCKOUT TAGOUT PROCEDURE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("BECOME A CHEF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Ouvrir", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Programmer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("SaladesAccompagnements", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("AVOCATLa", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("goût3", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Recommencer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "est idéal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Avec des", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Ensuite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Savoureuse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("fonctionne", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Utilisez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Sel,", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_cover_publication_and_editorial_cards_without_domain_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "TOP 20 OF CONTROL IDEAS\n" +
                "Control Topics Of those who need quick overview and simple discovery\n" +
                "ACME GUIDE\n" +
                "Resources: website, newsletter, app. Steps: 1 download the app. 2 subscribe to the newsletter. Discover more resources at www.example.com. Copyright 2026 Example Publishing.",
                38,
                285,
                [1]),
            new ExtractedPdfPage(
                2,
                "SAFETY CHECK\nMaterials: gloves, labels, scanner. Steps: 1 inspect status. 2 record evidence.",
                18,
                94,
                [2])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "TOP 20 OF CONTROL IDEAS", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "ACME GUIDE", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(2, "SAFETY CHECK", 1, 1, 2, 2, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Resources: website, newsletter, app. Steps: 1 download the app. 2 subscribe to the newsletter.",
                92,
                11,
                [3]),
            new ExtractedDocumentUnit(
                1,
                2,
                2,
                2,
                "SAFETY CHECK\nMaterials: gloves, labels, scanner. Steps: 1 inspect status. 2 record evidence.",
                94,
                10,
                [4])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/CoverAndContent.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAFETY CHECK", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("TOP 20", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Control Topics Of those", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, "ACME GUIDE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_keeps_compact_how_to_titles_but_discards_procedural_leads()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Faire un diagnostic rapide\nFAIRE UN PLAN DE CONTROLE EN PREMIER\nAppliquez la configuration et validez le resultat.\n",
                26,
                132,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operational Techniques", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Faire un diagnostic rapide", 27, 4, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "FAIRE UN PLAN DE CONTROLE EN PREMIER", 37, 7, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Appliquez la configuration et validez le resultat.", 49, 7, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Installer le module avant de connecter l'alimentation", 51, 8, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Configurez le mode automatique avant le demarrage", 47, 7, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "Valider le controle final avant archivage", 39, 6, [7])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/Techniques.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Faire un diagnostic rapide", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "FAIRE UN PLAN DE CONTROLE EN PREMIER", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Appliquez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Installer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Configurez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Valider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_embedded_instruction_fragments_without_category_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Audit fragment Glissez-y un code N Preparation\nCONTROL HANDOVER PLAN\nProcedure 1. Check status.",
                18,
                94,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Generic guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Audit fragment Glissez-y un code N Preparation", 45, 7, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "CONTROL HANDOVER PLAN\nProcedure 1. Check status.", 49, 6, [3])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/EmbeddedInstructionFragment.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Audit fragment", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "CONTROL HANDOVER PLAN", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_discards_layout_sentence_fragments_without_losing_plain_titles()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "Pain aux céréales\nLa congélation des légumes frais\nMatériel •2 terrines•1 bol\nAprès le signal, répartir l’huile uniformément dans la poêle\n",
                28,
                148,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Technique guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Pain aux céréales", 17, 3, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "La congélation des légumes frais", 32, 5, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Matériel •2 terrines•1 bol •1 passoire à pieds•1 fouet", 60, 8, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Après le signal, répartir l’huile uniformément dans la poêle", 61, 9, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "bien mélan-ger", 15, 2, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "repas .............................................................................................................. 10", 94, 2, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "Cuisson : 30 minutes", 20, 3, [8]),
            new ExtractedDocumentUnit(7, 0, 1, 1, "On garde les verres au réfrigérateur jusqu’au service.", 54, 8, [9])
        };

        var profile = DocumentProfileProjector.Project(
            "Mixed/LayoutFragments.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Pain aux céréales", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "La congélation des légumes frais", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Matériel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Après", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("bien", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("repas", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Cuisson", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("On garde", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_discards_catalog_page_parameter_and_sentence_leads_without_category_rules()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                1,
                "COURGE SPAGHETTI I page 48LE GRANOLA A KAKI I page 47\n" +
                "Temps de preparationen minutes\n" +
                "Pour vous aider a substituer certains ingredients, consultez l outil D a la page 56\n" +
                "Repartir la preparation dans des moules a muffins legerement huiles\n" +
                "Melangez, puis garnissez les blancs de cette preparation\n" +
                "A l aide de la spatule, ramenez la preparation vers le centre\n" +
                "SAUCE VERTE\nIngredients: persil, ail, huile.",
                55,
                397,
                [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Generic guide", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "COURGE SPAGHETTI I page 48LE GRANOLA A KAKI I page 47", 60, 10, [2]),
            new ExtractedDocumentUnit(1, 0, 1, 1, "Total time in minutes", 21, 4, [3]),
            new ExtractedDocumentUnit(2, 0, 1, 1, "Pour vous aider a substituer certains ingredients, consultez l outil D a la page 56", 80, 13, [4]),
            new ExtractedDocumentUnit(3, 0, 1, 1, "Remplacer la configuration dans les modules concernes", 55, 7, [5]),
            new ExtractedDocumentUnit(4, 0, 1, 1, "Validez, puis archivez le resultat du controle", 45, 7, [6]),
            new ExtractedDocumentUnit(5, 0, 1, 1, "A l aide de la spatule, ramenez la preparation vers le centre", 62, 12, [7]),
            new ExtractedDocumentUnit(6, 0, 1, 1, "SAFETY CHECK\nMaterials: gloves, labels, scanner.", 48, 6, [8])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Cards.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAFETY CHECK", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.Contains("page 48", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Total time", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Pour vous aider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Remplacer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Validez", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("A l aide", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_keeps_high_signal_glued_titles_when_card_limit_is_reached()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                22,
                157,
                [1])
        };
        var sections = Enumerable.Range(0, 95)
            .Select(i => new ExtractedDocumentSection(i, $"Section catalogue {i:00}", 1, 1, 122, 122, null))
            .ToArray();
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                122,
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                157,
                22,
                [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                122,
                122,
                "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
                "salez et poivrez puis lancez le programme 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un appareil connecte ingredients beurre jaunes d oeufs",
                157,
                22,
                [3],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project("Cuisine/Robot.pdf", pages, sections, units, exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.Contains("SAUCE BÉARNAISE", profile.SearchText, StringComparison.Ordinal);
        Assert.Contains("sauce bearnaise", profile.SearchText, StringComparison.Ordinal);
        Assert.True(profile.ContentCards.Count <= 240);
    }

    [Fact]
    public void Project_extracts_mixed_case_titles_glued_to_long_numeric_layout_suffixes()
    {
        const string title = "Concombres\u00e0 la romaine";
        var text = $"Copyright 2003{title}110077 Ingredients: concombres, creme, moutarde. Preparation: melanger et servir frais.";
        var pages = new[]
        {
            new ExtractedPdfPage(33, text, 14, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Catalogue", 1, 1, 33, 33, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 33, 33, text, text.Length, 14, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/CompactNumericSuffix.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.DoesNotContain(
            profile.ContentCards,
            card => card.Title.StartsWith("Mixez ensuite", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_balances_content_cards_across_deep_documents()
    {
        var pages = Enumerable.Range(1, 130)
            .Select(page => new ExtractedPdfPage(page, $"Page {page}", 2, 6, BitConverter.GetBytes(page)))
            .ToArray();
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Deep manual", 1, 1, 1, 130, null)
        };
        var earlyUnits = Enumerable.Range(0, 270)
            .Select(i => new ExtractedDocumentUnit(
                i,
                0,
                1 + (i % 45),
                1 + (i % 45),
                $"EARLY TOPIC {i:000}\nReusable detail for early topic {i}.",
                58,
                8,
                BitConverter.GetBytes(i)))
            .ToArray();
        var late = new ExtractedDocumentUnit(
            999,
            0,
            122,
            122,
            "SAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
            94,
            10,
            BitConverter.GetBytes(999));
        var lateExact = new ExtractedExactMatchEntry(
            999,
            0,
            999,
            122,
            122,
            "Salez et poivrez puis lancez le programme.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d'un appareil connecté. Ingredients: beurre, jaunes d'oeufs.",
            "salez et poivrez puis lancez le programme 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un appareil connecte ingredients beurre jaunes d oeufs",
            151,
            20,
            BitConverter.GetBytes(1000),
            "verbatim_excerpt");

        var profile = DocumentProfileProjector.Project(
            "Cuisine/Deep.pdf",
            pages,
            sections,
            earlyUnits.Append(late).ToArray(),
            [lateExact]);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => card.PageStart == 122);
        Assert.True(profile.ContentCards.Count <= 240);
    }

    [Fact]
    public void Project_prefers_embedded_content_title_over_instruction_leads()
    {
        const string text = "Salez et poivrez puis Lancez le programme sauce (Sauce) en vitesse 6 à 70°C pour 8 min avec le bouchon.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d’un Companion connecté bluetooth, vous pouvez remplacer le programme SAUCE par le mode manuel avec les paramètres indiqués.";
        var pages = new[]
        {
            new ExtractedPdfPage(122, text, 32, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Sauces", 1, 1, 122, 122, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 122, 122, text, text.Length, 32, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                122,
                122,
                text,
                "salez et poivrez puis lancez le programme sauce sauce en vitesse 6 a 70 c pour 8 min avec le bouchon 6 personnes23 min10 minsauce bearnaisepour les detenteurs d un companion connecte bluetooth",
                text.Length,
                32,
                [3],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project("Cuisine/Sauces.pdf", pages, sections, units, exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "SAUCE BÉARNAISE", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Lancez le programme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_promotes_embedded_uppercase_titles_before_parameter_fragments()
    {
        const string text = "Salez et poivrez puis Lancez le programme sauce (Sauce) en vitesse 6 à 70°C pour 8 min avec le bouchon.6 personnes23 min10 minSAUCE BÉARNAISEPour les détenteurs d’un Companion connecté bluetooth, vous pouvez remplacer le programme SAUCE par le mode manuel avec les paramètres indiqués.";
        var pages = new[]
        {
            new ExtractedPdfPage(122, text, 32, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Robot reference", 1, 1, 122, 122, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 122, 122, text, text.Length, 32, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/CompactLayout.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var pageTitles = profile.ContentCards
            .Where(card => card.PageStart == 122)
            .Select(card => card.Title)
            .ToArray();

        Assert.Contains("SAUCE BÉARNAISE", pageTitles);
        Assert.DoesNotContain(pageTitles, title => title.StartsWith("vitesse ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_extracts_embedded_uppercase_title_after_long_layout_lead()
    {
        var lead = string.Join(
            ' ',
            Enumerable.Repeat(
                "Validate the equipment status and record the operator notes before closing the shift.",
                7));
        const string title = "CONTROL HANDOVER PLAN";
        var text = $"{lead} 4 operators12 min5 min{title}L'operator uses this card to track handover steps, checkpoints, and exceptions.";
        var pages = new[]
        {
            new ExtractedPdfPage(42, text, 76, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 42, 42, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 42, 42, text, text.Length, 76, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/Operations.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Validate the equipment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_extracts_title_case_structured_item_after_compact_measure_tail()
    {
        const string title = "Module au relais";
        const string text =
            "Controlez la sortie et laissez stabiliser pendant 20 min. "
            + "4 operators 30 min"
            + title
            + " Pour 4 operators Materials: relay, sensor. Procedure 1. Inspect status. 2. Record evidence.";
        var pages = new[]
        {
            new ExtractedPdfPage(44, text, 33, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 44, 44, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 44, 44, text, text.Length, 33, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/TitleCaseStructuredItem.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.ContentCards, card => card.Title.StartsWith("Controlez la sortie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_extracts_title_case_structured_item_before_numbered_steps()
    {
        const string title = "Module au relais";
        const string text =
            "Pour 4 operators Procedure note with enough context. "
            + title
            + "1. Inspect the relay status. 2. Record the evidence. Materials: relay, sensor.";
        var pages = new[]
        {
            new ExtractedPdfPage(45, text, 24, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 45, 45, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 45, 45, text, text.Length, 24, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/NumberedTitleCaseItem.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_keeps_short_structured_title_when_page_evidence_is_grounded()
    {
        const string title = "Cap au sol";
        const string text =
            title
            + "1. Inspect the station with 2 kg ballast and 3 mm shim. "
            + "2. Record the operator evidence for 4 units before handover.";
        var pages = new[]
        {
            new ExtractedPdfPage(46, text, 23, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 46, 46, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 46, 46, text, text.Length, 23, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/ShortStructuredTitle.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_strong_content_cards_from_navigation_classified_units()
    {
        const string title = "CONTROL HANDOVER PLAN";
        var text =
            "Overview 10 Setup 12 Safety 14 Runtime 16 Reports 18 Appendix 20 " +
            "Validate the equipment status and record the operator notes before closing the shift. " +
            $"4 operators12 min5 min{title}L'operator uses this card to track handover steps, checkpoints, and exceptions.";
        var pages = new[]
        {
            new ExtractedPdfPage(44, text, 56, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations index", 1, 1, 44, 44, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 44, 44, text, text.Length, 39, [2])
        };

        Assert.NotEqual(RetrievalContentClassifier.ContentRole, RetrievalContentClassifier.AnalyzeChunk(text).ContentRole);

        var profile = DocumentProfileProjector.Project(
            "Generic/OperationsIndex.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_embedded_uppercase_title_before_typographic_elision()
    {
        const string title = "CONTROL HANDOVER PLAN";
        var lead = string.Join(
            ' ',
            Enumerable.Repeat(
                "Run the equipment sequence, collect the measurements, and confirm the operator notes.",
                6));
        var text = $"{lead} 4 operators13 min12 min15 min{title}L\u2019ideal configuration keeps the handover steps regular.";
        var pages = new[]
        {
            new ExtractedPdfPage(45, text, 88, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 45, 45, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 45, 45, text, text.Length, 88, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/OperationsElision.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_embedded_uppercase_title_from_short_verbatim_tail()
    {
        const string title = "CONTROL HANDOVER PLAN";
        var text =
            "Review the notes and close the checklist.4 operators13 min12 min15 min" +
            $"{title}L\u2019ideal configuration keeps the handover steps regular.";
        var pages = new[]
        {
            new ExtractedPdfPage(46, text, 88, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 46, 46, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 46, 46, text, text.Length, 88, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                46,
                46,
                text,
                "review the notes and close the checklist 4 operators13 min12 min15 mincontrol handover planl ideal configuration",
                text.Length,
                16,
                [3],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/OperationsVerbatimTail.pdf",
            pages,
            sections,
            units,
            exact);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_realistic_trailing_uppercase_title_after_long_instructions()
    {
        const string title = "CHURROS SAUCE CHOCOLAT";
        const string text = "Lancez le robot en vitesse 6 à 100°C pour 4 min. Ajoutez la farine, le blanc d’œuf, la levure et le sel, mixez en vitesse 4 pendant 30 s avec le bouchon.2 Formez des boudins en les roulant sur le plan de travail fariné, puis faites-les cuire à la friteuse. Déposez-les sur du papier absorbant.3 Dans le robot muni du couteau pour pétrir/concasser, mettez le chocolat en morceaux, le reste de lait et la vanille. Lancez le robot à 80°C en vitesse 5 pendant 8 min avec le bou-chon. Mixez ensuite en vitesse 10 pendant 20 s. Versez dans un bol. Trempez les churros dans la sauce au chocolat et dégustez.4 personnes13 min12 min15 minCHURROS SAUCE CHOCOLATL’idéal pour cette recette est d’avoir un appareil à churros qui vous permettra d’avoir des boudins de forme régulière.Individuels• DESSERTS •Individuels• DESSERTS • 4 Préchauffez le four à 180°C {th 6).";
        var pages = new[]
        {
            new ExtractedPdfPage(123, text, 152, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Generic trailing title", 1, 1, 123, 123, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 123, 123, text, text.Length, 152, [2])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/TrailingTitle.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_extracts_page_scoped_embedded_title_when_unit_window_misses_it()
    {
        const string title = "CONTROL HANDOVER PLAN";
        var lead = string.Join(
            ' ',
            Enumerable.Repeat(
                "Record measurements, inspect the equipment, verify the operator checklist, and close the work order.",
                24));
        var pageText = $"{lead} 4 operators13 min12 min15 min{title}L\u2019ideal configuration keeps the handover steps regular.";
        var unitText = lead;
        var pages = new[]
        {
            new ExtractedPdfPage(77, pageText, 240, pageText.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 77, 77, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 77, 77, unitText, unitText.Length, 180, [1])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/PageScopedTitle.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        var card = Assert.Single(
            profile.ContentCards,
            card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Equal(77, card.PageStart);
        Assert.Equal(77, card.PageEnd);
        Assert.Equal("page_embedded_title", card.Kind);
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_keeps_structured_embedded_title_from_navigation_shaped_unit()
    {
        const string title = "Classic Family Plan";
        var text = $"table of contents 1 2 3 4 5 6 7 8 9 10 {title}For 4 units1. Inspect the station and record the checklist. 2. Verify the handover notes.";
        Assert.NotEqual(
            RetrievalContentClassifier.ContentRole,
            RetrievalContentClassifier.AnalyzeChunk(text).ContentRole);
        Assert.Contains(
            title,
            StructuredContentLexicon.ExtractEmbeddedStructuredItemTitles(text, limit: 4));

        var pages = new[]
        {
            new ExtractedPdfPage(64, text, 27, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 64, 64, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 64, 64, text, text.Length, 27, [1])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/NavigationShapedStructuredTitle.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.Contains(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_rejects_navigation_shaped_embedded_title_without_substantive_body_evidence()
    {
        const string title = "Classic Family Plan";
        var text = $"table of contents 1 2 3 4 5 6 7 8 9 10 {title}For 4 units";
        Assert.NotEqual(
            RetrievalContentClassifier.ContentRole,
            RetrievalContentClassifier.AnalyzeChunk(text).ContentRole);
        Assert.Contains(
            title,
            StructuredContentLexicon.ExtractEmbeddedStructuredItemTitles(text, limit: 4));

        var pages = new[]
        {
            new ExtractedPdfPage(64, text, 17, text.Length, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Operations", 1, 1, 64, 64, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 64, 64, text, text.Length, 17, [1])
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/NavigationShapedStructuredTitleWithoutBody.pdf",
            pages,
            sections,
            units,
            exactMatchEntries: []);

        Assert.DoesNotContain(profile.ContentCards, card => string.Equals(card.Title, title, StringComparison.Ordinal));
        Assert.DoesNotContain(title, profile.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_balanced_content_cards_try_next_page_candidate_when_top_candidate_is_duplicate()
    {
        const string duplicateTitle = "COMMON DUPLICATE TITLE";
        const string fallbackTitle = "Field notes and validation details";
        const int pageCount = 241;

        static string BuildPageText(int page)
            => page switch
            {
                1 => duplicateTitle,
                2 => $"Unique Panel Two Anchor\n{fallbackTitle}",
                _ => $"FILLER TOPIC {page:000} Field notes and validation details."
            };

        var pages = Enumerable.Range(1, pageCount)
            .Select(page =>
            {
                var text = BuildPageText(page);
                return new ExtractedPdfPage(page, text, 8, text.Length, [1]);
            })
            .ToArray();

        var sections = Enumerable.Range(1, pageCount)
            .Select(page => new ExtractedDocumentSection(
                page - 1,
                page == 1 ? duplicateTitle : $"FILLER TOPIC {page:000}",
                1,
                page,
                page,
                page,
                null))
            .ToArray();

        var units = Enumerable.Range(1, pageCount)
            .Select(page =>
            {
                var text = BuildPageText(page);
                return new ExtractedDocumentUnit(page - 1, page - 1, page, page, text, text.Length, 8, [1]);
            })
            .ToArray();

        var exact = new[]
        {
            new ExtractedExactMatchEntry(
                0,
                0,
                0,
                1,
                1,
                duplicateTitle,
                "common duplicate title",
                duplicateTitle.Length,
                3,
                [1],
                "verbatim_excerpt"),
            new ExtractedExactMatchEntry(
                1,
                1,
                1,
                2,
                2,
                duplicateTitle,
                "common duplicate title",
                duplicateTitle.Length,
                3,
                [2],
                "verbatim_excerpt")
        };

        var profile = DocumentProfileProjector.Project(
            "Generic/BalancedCoverage.pdf",
            pages,
            sections,
            units,
            exact);

        Assert.Equal(240, profile.ContentCards.Count);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, fallbackTitle, StringComparison.Ordinal));
        Assert.DoesNotContain(
            profile.ContentCards,
            card => string.Equals(card.Title, "FILLER TOPIC 241", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseContentCards_drops_title_only_technical_cards_without_page_or_evidence()
    {
        const string metadataJson = """
        {
          "contentCards": [
            {
              "title": "ISO 13849-1",
              "kind": "llm_content_card",
              "signals": ["standard"]
            },
            {
              "title": "IEC 61508",
              "pageStart": 7,
              "pageEnd": 7,
              "kind": "llm_content_card",
              "signals": ["standard"]
            },
            {
              "title": "ISO 9001",
              "kind": "llm_content_card",
              "signals": ["standard"],
              "evidence": {
                "schemaVersion": "content_card_evidence_v1",
                "facts": [
                  {
                    "kind": "standard",
                    "label": "standard reference",
                    "value": "ISO 9001",
                    "sourceText": "ISO 9001 applies to the quality management procedure."
                  }
                ]
              }
            }
          ]
        }
        """;

        var cards = DocumentProfileProjector.ParseContentCards(metadataJson);

        Assert.DoesNotContain(cards, card => string.Equals(card.Title, "ISO 13849-1", StringComparison.Ordinal));
        Assert.Contains(cards, card => string.Equals(card.Title, "IEC 61508", StringComparison.Ordinal));
        Assert.Contains(cards, card => string.Equals(card.Title, "ISO 9001", StringComparison.Ordinal));
    }
}
