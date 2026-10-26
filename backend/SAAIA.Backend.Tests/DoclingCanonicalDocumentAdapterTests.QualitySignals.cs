using System.Text.Json;
using Xunit;

public sealed partial class DoclingCanonicalDocumentAdapterTests
{
    [Theory]
    [InlineData("retrieval")]
    [InlineData("profile")]
    [InlineData("canonical_units")]
    public void Canonical_quality_inconclusive_regional_reading_remains_visible_in_each_text_projection(string projection)
    {
        var source = FidelityDocument();
        const string body = "The observed source paragraph records the exact operating procedure and its complete maintenance conditions for independent review.";
        var item = AddFidelityText(source, body);
        item.RegionalTextRepairAttempt = JsonSerializer.SerializeToElement(new
        {
            schema_version = "saaia_text_region_repair_v1",
            source_ref = item.SelfRef,
            status = "not_applied",
            reason = "independent_readings_disagree",
            attempted = true,
            original_text = item.Text,
            source_provenance = item.Provenance,
            observations = new[] { new { engine = "rapidocr", text = "first reading" }, new { engine = "tesseract", text = "other reading" } }
        });
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Contains("regional_text_ocr_repair_inconclusive", Assert.Single(document.Pages[0].Blocks).QualityFlags);

        var view = ReadCanonicalQualityView(document, source, body, projection);

        Assert.Contains("regional_text_ocr_repair_inconclusive", view.Signals);
        Assert.DoesNotContain("regional_text_ocr_repair_applied", view.Signals);
        Assert.Equal("ok", view.Status); // Uncertainty is not proof that all text is corrupt.
        Assert.False(view.OcrCandidate);
        Assert.Equal(body, view.Text);
    }

    [Theory]
    [InlineData("retrieval")]
    [InlineData("profile")]
    [InlineData("canonical_units")]
    public void Canonical_quality_missing_spatial_anchor_is_preserved_without_inventing_an_ocr_failure(string projection)
    {
        var source = FidelityDocument();
        const string body = "This complete source paragraph has readable literal text although the converter did not supply its spatial coordinates for precise localization.";
        var item = AddFidelityText(source, body);
        item.Provenance[0].BoundingBox = null;
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Contains("spatial_anchor_unavailable", Assert.Single(document.Pages[0].Blocks).QualityFlags);

        var view = ReadCanonicalQualityView(document, source, body, projection);

        Assert.Contains("spatial_anchor_unavailable", view.Signals);
        Assert.Equal("ok", view.Status);
        Assert.False(view.OcrCandidate);
    }

    [Theory]
    [InlineData("retrieval")]
    [InlineData("profile")]
    [InlineData("canonical_units")]
    public void Canonical_quality_native_supplement_keeps_its_existing_warning_as_well_as_its_source_text(string projection)
    {
        var source = FidelityDocument();
        const string original = "The primary source documents a complete baseline procedure for the installed operating equipment and its maintenance controls.";
        const string supplement = "The additional native observation records a separate source constraint that must remain available with its unresolved spatial warning.";
        AddFidelityText(source, original);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        var native = FidelityNative((1, original + "\n" + supplement));
        CanonicalNativeTextCoverageReconciler.Apply(document, native);
        var block = Assert.Single(document.Pages[0].Blocks,
            block => block.BlockType == CanonicalNativeTextCoverageReconciler.RecoveryBlockType
                && block.Text.Canonical.Contains(supplement, StringComparison.Ordinal));
        block.QualityFlags.Add("spatial_anchor_unavailable");

        var view = ReadCanonicalQualityView(document, source, supplement, projection);

        Assert.Contains("spatial_anchor_unavailable", view.Signals);
        Assert.Contains(supplement, view.Text, StringComparison.Ordinal);
        Assert.Equal("ok", view.Status);
    }

    [Theory]
    [InlineData("retrieval")]
    [InlineData("profile")]
    [InlineData("canonical_units")]
    public void Canonical_quality_proven_remaining_replacement_character_is_not_reported_as_clean_text(string projection)
    {
        var source = FidelityDocument();
        const string body = "The observed source contains a remaining \uFFFD replacement character within an otherwise complete paragraph and requires transcription review before exact factual use.";
        AddFidelityText(source, body);
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Contains('\uFFFD', Assert.Single(document.Pages[0].Blocks).Text.Canonical);

        var view = ReadCanonicalQualityView(document, source, body, projection);

        Assert.Equal("low_text", view.Status);
        Assert.True(view.OcrCandidate);
        Assert.Contains("replacement_chars_remaining", view.Signals);
    }

    [Theory]
    [InlineData("retrieval")]
    [InlineData("profile")]
    [InlineData("canonical_units")]
    public void Canonical_quality_uninterpreted_page_figure_warning_is_not_lost_when_publishing_readable_text(string projection)
    {
        var source = BuildDocument();
        source.Texts[1].OriginalText = source.Texts[1].Text =
            "The readable source paragraph accompanies a figure whose relationships have not been interpreted by any semantic image engine.";
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);
        Assert.Contains("figure_semantics_not_extracted", document.Pages[0].QualityFlags);

        var view = ReadCanonicalQualityView(document, source, source.Texts[1].Text, projection);

        Assert.Contains("figure_semantics_not_extracted", view.Signals);
        Assert.Equal("ok", view.Status);
        Assert.False(view.OcrCandidate); // Unknown image semantics do not prove a failed text transcription.
    }

    [Fact]
    public void Canonical_quality_clean_literal_code_is_not_automatically_declared_corrupt_or_an_ocr_failure()
    {
        var source = FidelityDocument();
        const string body = "public static int ReadVerifiedValue() { return 12; } // This complete literal source is readable and its code label alone proves no transcription failure.";
        AddFidelityText(source, body, "code");
        var document = DoclingCanonicalDocumentAdapter.Project(BuildContext(), source);

        var view = ReadCanonicalQualityView(document, source, body, "retrieval");

        Assert.Equal("ok", view.Status);
        Assert.False(view.OcrCandidate);
        Assert.DoesNotContain("replacement_chars_remaining", view.Signals);
        Assert.DoesNotContain("regional_text_ocr_repair_inconclusive", view.Signals);
        Assert.Equal(body, view.Text);
    }

    private sealed record CanonicalQualityView(string Text, string? Status, bool OcrCandidate, IReadOnlyList<string> Signals);

    private static CanonicalQualityView ReadCanonicalQualityView(
        SAAIA.Contracts.DocumentIntelligence.CanonicalDocument document,
        DoclingDocument source, string body, string projection)
    {
        if (projection == "canonical_units")
        {
            var unit = Assert.Single(CanonicalExtractionViewProjector.ProjectUnits(document, source),
                unit => unit.Text.Contains(body, StringComparison.Ordinal));
            return new(unit.Text, unit.ExtractionTextStatus, unit.ExtractionOcrCandidate, unit.ExtractionQualitySignals ?? []);
        }
        var chunks = DoclingCanonicalRetrievalProjector.Project(document, source, 500, 1);
        if (projection == "profile")
        {
            var unit = Assert.Single(CanonicalProfileInputProjector.Project(chunks),
                unit => unit.Text.Contains(body, StringComparison.Ordinal));
            return new(unit.Text, unit.ExtractionTextStatus, unit.ExtractionOcrCandidate, unit.ExtractionQualitySignals ?? []);
        }
        var chunk = Assert.Single(chunks, chunk => chunk.Text.Contains(body, StringComparison.Ordinal));
        return new(chunk.Text, chunk.ExtractionTextStatus, chunk.ExtractionOcrCandidate, chunk.ExtractionQualitySignals ?? []);
    }
}
