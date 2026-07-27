using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class TextEncodingSanitizerTests
{
    [Fact]
    public void PdfTextSanitizer_repairs_common_mojibake_without_breaking_real_multilingual_text()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pr\u00c3\u00a9paration \u00e2\u20ac\u00a2 l\u00e2\u20ac\u2122utilisateur\0");

        Assert.Equal("Pr\u00e9paration \u2022 l\u2019utilisateur ", sanitized);
        Assert.DoesNotContain('\u00c3', sanitized);
        Assert.DoesNotContain('\u00e2', sanitized);
        Assert.Equal("N\u00c3O", PdfTextSanitizer.ForStorage("N\u00c3O"));
        Assert.Equal(
            "\u0645\u0631\u062d\u0628\u0627 \u4e2d\u6587",
            PdfTextSanitizer.ForStorage("\u0645\u0631\u062d\u0628\u0627 \u4e2d\u6587"));
    }

    [Fact]
    public void PdfTextSanitizer_repairs_double_encoded_utf8_mojibake()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pr\u00c3\u0192\u00c2\u00a9paration l\u00c3\u00a2\u00e2\u201a\u00ac\u00e2\u201e\u00a2utilisateur");

        Assert.Equal("Pr\u00e9paration l\u2019utilisateur", sanitized);
        Assert.DoesNotContain('\u00c3', sanitized);
        Assert.DoesNotContain('\u00c2', sanitized);
        Assert.DoesNotContain('\u00e2', sanitized);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_real_qdrant_c1_control_mojibake_sample()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pr\u00c3\u00a9paration d'\u00c5\u0093ufs jusqu'\u00c3\u00a0 obtenir une cr\u00c3\u00a8me +\u00c2\u00bb d\u00e2\u0080\u0099utilisateur \u00e2\u0080\u00a2");

        Assert.Equal("Pr\u00e9paration d'\u0153ufs jusqu'\u00e0 obtenir une cr\u00e8me +\u00bb d\u2019utilisateur \u2022", sanitized);
        Assert.DoesNotContain('\u00c3', sanitized);
        Assert.DoesNotContain('\u00c2', sanitized);
        Assert.DoesNotContain('\u00c5', sanitized);
        Assert.DoesNotContain('\u00e2', sanitized);
        Assert.DoesNotContain('\u0080', sanitized);
        Assert.DoesNotContain('\u0093', sanitized);
    }

    [Fact]
    public void PostgresTextSanitizer_repairs_text_arrays_and_json_strings()
    {
        Assert.Equal("Mat\u00e9riel \u2013 v\u00e9rifi\u00e9", PostgresTextSanitizer.Clean("Mat\u00c3\u00a9riel \u00e2\u20ac\u201c v\u00c3\u00a9rifi\u00c3\u00a9\0"));
        Assert.Equal(["D\u00e9j\u00e0", "N\u00c3O"], PostgresTextSanitizer.CleanArray(["D\u00c3\u00a9j\u00c3\u00a0", "N\u00c3O", "D\u00c3\u00a9j\u00c3\u00a0"]));

        using var doc = JsonDocument.Parse(
            """
            {
              "title": "PrÃƒÂ©paration",
              "quote": "lÃ¢â‚¬â„¢utilisateur",
              "languageSample": "NÃƒO"
            }
            """);

        var cleaned = PostgresTextSanitizer.CleanJson(doc.RootElement);

        Assert.NotNull(cleaned);
        using var cleanedDoc = JsonDocument.Parse(cleaned!);
        Assert.Equal("Pr\u00e9paration", cleanedDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal("l\u2019utilisateur", cleanedDoc.RootElement.GetProperty("quote").GetString());
        Assert.Equal("N\u00c3O", cleanedDoc.RootElement.GetProperty("languageSample").GetString());
        var cleanedSerializedJson = PostgresTextSanitizer.CleanJson(JsonSerializer.Serialize(new
        {
            evidence = "source\0snippet"
        }));

        Assert.NotNull(cleanedSerializedJson);
        using var cleanedSerializedDoc = JsonDocument.Parse(cleanedSerializedJson!);
        Assert.Equal("sourcesnippet", cleanedSerializedDoc.RootElement.GetProperty("evidence").GetString());
    }

    [Fact]
    public void BuildQdrantChunkPayload_repairs_text_embedding_and_titles_before_storage()
    {
        var chunk = new ProjectedRetrievalChunk(
            ChunkIndex: 3,
            SectionOrdinal: 7,
            UnitOrdinal: null,
            PageStart: 12,
            PageEnd: 12,
            Text: "Servir imm\u00c3\u00a9diatement au petit d\u00c3\u00a9jeuner.",
            TokenCount: 6,
            Checksum: [1, 2, 3],
            ChunkType: "section_window_v1",
            SourceUnitOrdinals: [10, 11],
            SourceUnitStartOrdinal: 10,
            SourceUnitEndOrdinal: 11,
            SourceUnitCount: 2,
            ChunkComposition: "multi_unit_window");

        var payload = IngestionWorker.BuildQdrantChunkPayload(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Generic/source.pdf",
            "Generic",
            "hash",
            "2026-07-01T00:00:00Z",
            4,
            chunk,
            "document_name: source.pdf\n\nexcerpt:\nServir imm\u00c3\u00a9diatement.",
            "D\u00c3\u00a9jeuner \u00c3\u00a9quilibr\u00c3\u00a9",
            "Planning > D\u00c3\u00a9jeuner",
            chunkLinks: null,
            embeddingModel: "test-model",
            embeddingInputFormat: "test-format");

        Assert.Equal("Servir imm\u00e9diatement au petit d\u00e9jeuner.", payload["text"]);
        Assert.Contains("Servir imm\u00e9diatement.", Assert.IsType<string>(payload["embed_text"]));
        Assert.Equal("D\u00e9jeuner \u00e9quilibr\u00e9", payload["section_title"]);
        Assert.Equal("Planning > D\u00e9jeuner", payload["heading_path"]);
        Assert.Equal("mojibake_repair_v2", payload["text_cleaning_version"]);
        Assert.True(Assert.IsType<bool>(payload["text_cleaning_changed"]));
        Assert.False(Assert.IsType<bool>(payload["text_mojibake_after"]));
        Assert.DoesNotContain('\u00c3', Assert.IsType<string>(payload["text"]));
        Assert.DoesNotContain('\u00c3', Assert.IsType<string>(payload["embed_text"]));
    }

    [Fact]
    public void PdfTextSanitizer_repairs_pdf_replacement_characters_without_storing_unknown_glyphs()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "organiza\uFFFDon con\uFFFDngency iden\uFFFDfy \uFFFD heading");

        Assert.DoesNotContain('\uFFFD', sanitized);
        Assert.Contains("organization", sanitized, StringComparison.Ordinal);
        Assert.Contains("contingency", sanitized, StringComparison.Ordinal);
        Assert.Contains("identify", sanitized, StringComparison.Ordinal);
        Assert.Contains(" heading", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_soft_line_hyphenation_and_preserves_paragraph_breaks()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "The imple-\nmentation keeps para-\ngraphs.\n\nNext\u00a0block");

        Assert.Contains("implementation", sanitized, StringComparison.Ordinal);
        Assert.Contains("paragraphs", sanitized, StringComparison.Ordinal);
        Assert.Contains("\n\nNext block", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("imple-", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\u00a0', sanitized);
    }

    [Fact]
    public void PdfTextSanitizer_preserves_explicit_layout_region_boundaries()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "LEFT ITEM\n400 g material\n\n\nRIGHT ITEM\n900 ml fluid");

        Assert.Contains("400 g material\n\n\nRIGHT ITEM", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_joined_french_imperative_pronouns_without_rewriting_prepositions()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pr\u00e9levez le bouillon et m\u00e9langezle \u00e0 la cr\u00e8me. Rincezles ensuite, mais gardez chezles voisins tel quel.");

        Assert.Contains("m\u00e9langez-le", sanitized, StringComparison.Ordinal);
        Assert.Contains("Rincez-les", sanitized, StringComparison.Ordinal);
        Assert.Contains("chezles voisins", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("m\u00e9langezle", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Rincezles", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_common_ocr_embedded_spaces_without_general_word_fusion()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Pa per size depends on the pro per data field in up per right corner. "
            + "The cop per bonding jum per belongs to the standards develo per. "
            + "Keep per item, threads per inch and pro rata untouched.");

        Assert.Contains("Paper size", sanitized, StringComparison.Ordinal);
        Assert.Contains("proper data field", sanitized, StringComparison.Ordinal);
        Assert.Contains("upper right corner", sanitized, StringComparison.Ordinal);
        Assert.Contains("copper bonding jumper", sanitized, StringComparison.Ordinal);
        Assert.Contains("standards developer", sanitized, StringComparison.Ordinal);
        Assert.Contains("per item", sanitized, StringComparison.Ordinal);
        Assert.Contains("threads per inch", sanitized, StringComparison.Ordinal);
        Assert.Contains("pro rata", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Pa per", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("pro per", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("up per", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("cop per", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("jum per", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("develo per", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_common_ocr_fused_words_without_domain_specific_rules()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "Approval ofthe developer. Forthe test, inspect shortcircuit, groundfault, moldedcase, "
            + "multiplechoice, openended and threedimensional samples. "
            + "The table has andimpacttests, opera tion text, recommende spacing and Safetyselated parts.");

        Assert.Contains("Approval of the developer", sanitized, StringComparison.Ordinal);
        Assert.Contains("For the test", sanitized, StringComparison.Ordinal);
        Assert.Contains("short-circuit", sanitized, StringComparison.Ordinal);
        Assert.Contains("ground-fault", sanitized, StringComparison.Ordinal);
        Assert.Contains("molded-case", sanitized, StringComparison.Ordinal);
        Assert.Contains("multiple-choice", sanitized, StringComparison.Ordinal);
        Assert.Contains("open-ended", sanitized, StringComparison.Ordinal);
        Assert.Contains("three-dimensional", sanitized, StringComparison.Ordinal);
        Assert.Contains("and impact tests", sanitized, StringComparison.Ordinal);
        Assert.Contains("operation text", sanitized, StringComparison.Ordinal);
        Assert.Contains("recommended spacing", sanitized, StringComparison.Ordinal);
        Assert.Contains("Safety-related parts", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("ofthe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortcircuit", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Safetyselated", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfTextSanitizer_repairs_common_ocr_standard_reference_prefixes_without_rewriting_unanchored_acronyms()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "NEPA 79 evolved with NEPA 70 and NEPA 70E. See TEC 60529, IEG 61558-2-6 and DOF-HDBK-1003-96. "
            + "Keep NEPA review and TEC company untouched.");

        Assert.Contains("NFPA 79", sanitized, StringComparison.Ordinal);
        Assert.Contains("NFPA 70", sanitized, StringComparison.Ordinal);
        Assert.Contains("NFPA 70E", sanitized, StringComparison.Ordinal);
        Assert.Contains("IEC 60529", sanitized, StringComparison.Ordinal);
        Assert.Contains("IEC 61558-2-6", sanitized, StringComparison.Ordinal);
        Assert.Contains("DOE-HDBK-1003-96", sanitized, StringComparison.Ordinal);
        Assert.Contains("NEPA review", sanitized, StringComparison.Ordinal);
        Assert.Contains("TEC company", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("NEPA 79", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("TEC 60529", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("IEG 61558", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("DOF-HDBK", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentFoundation_storage_normalization_applies_pdf_ocr_word_repairs()
    {
        var normalized = DocumentFoundationRepo.NormalizePostgresTextForStorage(
            "The text font, pa per size and pro per data field are stored in the up per area.");

        Assert.Contains("paper size", normalized, StringComparison.Ordinal);
        Assert.Contains("proper data field", normalized, StringComparison.Ordinal);
        Assert.Contains("upper area", normalized, StringComparison.Ordinal);
        Assert.Contains("bonding jumper", DocumentFoundationRepo.NormalizePostgresTextForStorage("bonding jum per"), StringComparison.Ordinal);
        Assert.DoesNotContain("pa per", normalized, StringComparison.Ordinal);
    }
}
