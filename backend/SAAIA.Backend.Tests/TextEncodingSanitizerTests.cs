using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class TextEncodingSanitizerTests
{
    [Fact]
    public void PdfTextSanitizer_preserves_literal_unicode_and_multilingual_text()
    {
        const string text = "Pr\u00c3\u00a9paration N\u00c3O \u0645\u0631\u062d\u0628\u0627 \u4e2d\u6587";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
        Assert.Equal("source snippet", PdfTextSanitizer.ForStorage("source\0snippet"));
    }

    [Fact]
    public void PdfTextSanitizer_does_not_reinterpret_literal_double_encoded_examples()
    {
        const string text = "Pr\u00c3\u0192\u00c2\u00a9paration l\u00c3\u00a2\u00e2\u201a\u00ac\u00e2\u201e\u00a2utilisateur";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PdfTextSanitizer_retains_c1_observations_instead_of_inventing_decoded_glyphs()
    {
        const string text = "Pr\u00c3\u00a9paration d'\u00c5\u0093ufs d\u00e2\u0080\u0099utilisateur";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PostgresTextSanitizer_preserves_literal_unicode_in_arrays_and_json()
    {
        const string text = "Mat\u00c3\u00a9riel organiza\uFFFDon";
        Assert.Equal(text, PostgresTextSanitizer.Clean(text + "\0"));
        Assert.Equal(new[] { text, "Distinct" }, PostgresTextSanitizer.CleanArray([text, "Distinct", text]));
        var json = PostgresTextSanitizer.CleanJson(JsonSerializer.Serialize(new { evidence = text }));
        using var document = JsonDocument.Parse(json!);
        Assert.Equal(text, document.RootElement.GetProperty("evidence").GetString());
    }

    [Fact]
    public void BuildQdrantChunkPayload_preserves_literal_text_embedding_and_titles()
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

        Assert.Equal(chunk.Text, payload["text"]);
        Assert.Contains("Servir imm\u00c3\u00a9diatement.", Assert.IsType<string>(payload["embed_text"]));
        Assert.Equal("D\u00c3\u00a9jeuner \u00c3\u00a9quilibr\u00c3\u00a9", payload["section_title"]);
        Assert.Equal("Planning > D\u00c3\u00a9jeuner", payload["heading_path"]);
        Assert.Equal("source_transport_v3", payload["text_cleaning_version"]);
        Assert.False(Assert.IsType<bool>(payload["text_cleaning_changed"]));
        Assert.True(Assert.IsType<bool>(payload["text_mojibake_after"]));
    }

    [Fact]
    public void PdfTextSanitizer_preserves_unknown_glyphs_instead_of_guessing_words()
    {
        const string text = "organiza\uFFFDon con\uFFFDngency iden\uFFFDfy \uFFFD heading";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PdfTextSanitizer_preserves_hard_hyphens_and_paragraph_breaks()
    {
        var text = "The imple-\nmentation keeps fail-\nsafe states.\n\nNext\u00a0block";
        Assert.Equal(text.Replace('\u00a0', ' '), PdfTextSanitizer.ForStorage(text));
        Assert.Equal("implementation", PdfTextSanitizer.ForStorage("imple\u00admentation"));
    }

    [Fact]
    public void PdfTextSanitizer_preserves_explicit_layout_region_boundaries()
    {
        var sanitized = PdfTextSanitizer.ForStorage(
            "LEFT ITEM\n400 g material\n\n\nRIGHT ITEM\n900 ml fluid");

        Assert.Contains("400 g material\n\n\nRIGHT ITEM", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfTextSanitizer_preserves_literal_french_words()
    {
        const string text = "mélangezle Rincezles chezles voisins";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PdfTextSanitizer_does_not_fuse_literal_source_words()
    {
        const string text = "Pa per cop per jum per pro per up per";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PdfTextSanitizer_does_not_split_literal_identifiers()
    {
        const string text = "ofthe forthe shortcircuit groundfault moldedcase multiplechoice openended recommende";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void PdfTextSanitizer_preserves_literal_organization_and_reference_codes()
    {
        const string text = "NEPA 79 NEPA 70E TEC 60529 IEG 61558-2-6 DOF-HDBK-1003-96";
        Assert.Equal(text, PdfTextSanitizer.ForStorage(text));
    }

    [Fact]
    public void DocumentFoundation_storage_normalization_preserves_source_words()
    {
        const string text = "The text font, pa per size and pro per data field are stored in the up per area.";
        Assert.Equal(text, DocumentFoundationRepo.NormalizePostgresTextForStorage(text));
    }

}
