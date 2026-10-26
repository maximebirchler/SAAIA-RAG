using SAAIA.Contracts.DocumentIntelligence;
using SAAIA.Backend.AdvancedAnalysis;
using System.Text.Json;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    private const string StructuredLiteral =
        "value = -12.5; result = left / right; // Do not remove the minus sign, the decimal value, or this explicit negation from the literal source observation.";

    [Theory]
    [InlineData("code", "retrieval")]
    [InlineData("code", "profile")]
    [InlineData("code", "canonical_units")]
    [InlineData("formula", "retrieval")]
    [InlineData("formula", "profile")]
    [InlineData("formula", "canonical_units")]
    public void Structured_transcription_observed_kind_and_unverified_status_survive_each_text_projection(
        string observedKind, string projection)
    {
        var source = FidelityDocument();
        var sourceItem = AddFidelityText(source, StructuredLiteral, observedKind);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = CanonicalContractJson.SerializeCanonical(document);

        var view = ReadCanonicalQualityView(document, source, StructuredLiteral, projection);

        Assert.Contains("observed_block_type_" + observedKind, view.Signals);
        Assert.Contains("structured_transcription_not_verified", view.Signals);
        Assert.DoesNotContain("observed_block_type_" + (observedKind == "code" ? "formula" : "code"), view.Signals);
        Assert.DoesNotContain("math_recognition_failed", view.Signals);
        Assert.Equal("ok", view.Status);
        Assert.False(view.OcrCandidate);
        Assert.Equal(StructuredLiteral, view.Text);
        Assert.Equal(StructuredLiteral, sourceItem.Text);
        Assert.Equal(observedKind, Assert.Single(document.Pages[0].Blocks).BlockType);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("formula")]
    public void Structured_transcription_page_view_keeps_the_same_observed_kind_without_mutating_source(string observedKind)
    {
        var source = FidelityDocument();
        AddFidelityText(source, StructuredLiteral, observedKind);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = CanonicalContractJson.SerializeCanonical(document);

        var page = Assert.Single(CanonicalExtractionViewProjector.Project(document, [], source).Pages);
        var quality = Assert.IsType<PdfPageExtractionQuality>(page.Quality);

        Assert.Contains("observed_block_type_" + observedKind, quality.Signals);
        Assert.Contains("structured_transcription_not_verified", quality.Signals);
        Assert.Equal(StructuredLiteral, page.Text);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Fact]
    public void Structured_transcription_mixed_chunk_unions_exact_observed_types_once_and_keeps_all_literal_text()
    {
        var source = FidelityDocument();
        const string ordinary = "The source paragraph describes an independent procedure without any verified interpretation of the neighboring structured observation.";
        const string formula = "a - b = -12.5; The recorded expression does not certify a numerator, denominator, exponent scope, or layout relationship.";
        AddFidelityText(source, StructuredLiteral, "code");
        AddFidelityText(source, ordinary);
        AddFidelityText(source, formula, "formula");
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var before = CanonicalContractJson.SerializeCanonical(document);

        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(document, source, 500, 1));

        Assert.Single(chunk.ExtractionQualitySignals!, code => code == "observed_block_type_code");
        Assert.Single(chunk.ExtractionQualitySignals!, code => code == "observed_block_type_formula");
        Assert.Single(chunk.ExtractionQualitySignals!, code => code == "structured_transcription_not_verified");
        Assert.Contains(StructuredLiteral, chunk.Text, StringComparison.Ordinal);
        Assert.Contains(ordinary, chunk.Text, StringComparison.Ordinal);
        Assert.Contains(formula, chunk.Text, StringComparison.Ordinal);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("list_item")]
    public void Structured_transcription_does_not_infer_a_kind_from_formula_or_code_looking_plain_text(string observedKind)
    {
        var source = FidelityDocument();
        AddFidelityText(source, StructuredLiteral, observedKind);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var view = ReadCanonicalQualityView(document, source, StructuredLiteral, "retrieval");

        Assert.DoesNotContain("observed_block_type_code", view.Signals);
        Assert.DoesNotContain("observed_block_type_formula", view.Signals);
        Assert.DoesNotContain("structured_transcription_not_verified", view.Signals);
        Assert.Equal(StructuredLiteral, view.Text);
        Assert.Equal("ok", view.Status);
    }

    [Fact]
    public void Structured_transcription_existing_advanced_source_quality_transport_keeps_exact_codes_and_unverified_accuracy()
    {
        var source = FidelityDocument();
        AddFidelityText(source, StructuredLiteral, "code");
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var chunk = Assert.Single(DoclingCanonicalRetrievalProjector.Project(document, source, 500, 1));

        var quality = AdvancedAnalysisSourceQuality.FromMetadata(chunk.ExtractionTextStatus,
            chunk.ExtractionOcrCandidate.ToString(), JsonSerializer.Serialize(chunk.ExtractionQualitySignals));
        var transported = JsonSerializer.SerializeToElement(Assert.IsType<AdvancedAnalysisSourceQuality>(quality),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var signals = transported.GetProperty("signals").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("observed_block_type_code", signals);
        Assert.Contains("structured_transcription_not_verified", signals);
        Assert.Equal("not_verified", transported.GetProperty("semanticAccuracy").GetString());
        Assert.Equal("ok", transported.GetProperty("textStatus").GetString());
        Assert.False(transported.GetProperty("ocrCandidate").GetBoolean());
    }
}
