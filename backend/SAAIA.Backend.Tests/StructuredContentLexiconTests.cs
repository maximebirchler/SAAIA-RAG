using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class StructuredContentLexiconTests
{
    [Theory]
    [InlineData("Materials")]
    [InlineData("Matériaux")]
    [InlineData("Components")]
    [InlineData("Procédure")]
    [InlineData("Préparation")]
    [InlineData("Preparación")]
    [InlineData("Preparação")]
    [InlineData("Preparazione")]
    [InlineData("Zubereitung")]
    [InlineData("Méthode")]
    [InlineData("Étapes")]
    [InlineData("Schritte")]
    [InlineData("Instruction")]
    [InlineData("Warning")]
    [InlineData("Caution")]
    public void Structured_answer_cues_preserve_existing_multilingual_markers(string cue)
    {
        Assert.True(StructuredContentLexicon.ContainsStructuredAnswerCue($"Section: {cue}"));
        Assert.True(RagEndpoints.LooksLikeStructuredAnswerChunk(Match($"Section: {cue}")));
    }

    [Theory]
    [InlineData("Control block Zubereitung Schritte")]
    [InlineData("Bloque de control Preparaci\u00f3n Pasos")]
    [InlineData("Bloco de controlo Prepara\u00e7\u00e3o Passos")]
    [InlineData("Blocco di controllo Preparazione")]
    [InlineData("Generic unit For 4 items")]
    [InlineData("QualitaetspruefungZubereitung 1. Sensor pruefen.")]
    [InlineData("QualitaetspruefungFuer 4 Elemente.")]
    [InlineData("BloqueControlPreparacion 1. Verificar sensor.")]
    [InlineData("BloqueControlFor 4 items.")]
    public void Structured_lead_marker_uses_shared_multilingual_cues(string text)
    {
        Assert.True(StructuredContentLexicon.LooksLikeStructuredLeadMarker(text));
    }

    [Theory]
    [InlineData("Module overview with standards and references.")]
    [InlineData("Control block with materials and procedure.")]
    [InlineData("Safety warning with required checks.")]
    [InlineData("Bloque con componentes e instrucciones.")]
    [InlineData("Fuer die Pruefung: Zubereitung Schritte.")]
    public void Structured_content_context_uses_shared_generic_cues(string text)
    {
        Assert.True(StructuredContentLexicon.ContainsStructuredContentContext(text));
    }

    [Theory]
    [InlineData("Base 4 elements", 4, "elements")]
    [InlineData("For 6 units", 6, "units")]
    [InlineData("Fuer 8 Elemente", 8, "elemente")]
    [InlineData("12 components", 12, "components")]
    public void Scale_basis_extraction_uses_generic_multilingual_patterns(
        string text,
        int expectedCount,
        string expectedLabel)
    {
        Assert.True(StructuredContentLexicon.TryExtractScaleBasis(text, out var count, out var label));
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedLabel, label);
    }

    [Theory]
    [InlineData("For 12 min wait")]
    [InlineData("70 C temperature")]
    [InlineData("4 bar pressure")]
    [InlineData("Page 48")]
    public void Scale_basis_extraction_rejects_measurement_and_page_units(string text)
    {
        Assert.False(StructuredContentLexicon.TryExtractScaleBasis(text, out _, out _));
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("rpm")]
    [InlineData("C")]
    [InlineData("minutes")]
    [InlineData("page")]
    public void Non_scalable_quantity_units_are_shared(string unit)
    {
        Assert.True(StructuredContentLexicon.IsNonScalableQuantityUnit(unit));
    }

    [Fact]
    public void Structured_quantity_list_detection_is_shared_with_rag_runtime()
    {
        var text = """
        - 2 kg load
        - 15 min wait
        - 4 bar pressure
        """;

        Assert.True(StructuredContentLexicon.LooksLikeStructuredQuantityList(text));
        Assert.True(RagEndpoints.LooksLikeStructuredQuantityList(text));
    }

    [Fact]
    public void Plain_title_without_structured_cue_is_not_structured()
    {
        const string text = "Quarterly roadmap overview and ownership notes.";

        Assert.False(StructuredContentLexicon.ContainsStructuredAnswerCue(text));
        Assert.False(RagEndpoints.LooksLikeStructuredQuantityList(text));
        Assert.False(RagEndpoints.LooksLikeStructuredAnswerChunk(Match(text)));
    }

    private static RagMatch Match(string text)
        => new(
            0.9,
            "doc",
            "Docs/sample.pdf",
            "sample.pdf",
            1,
            1,
            "chunk",
            0,
            text,
            1,
            "hash",
            text,
            "contextual_text_v1",
            0,
            0,
            "Section",
            "Section",
            "unit_exact_v1",
            null,
            null,
            null);
}
