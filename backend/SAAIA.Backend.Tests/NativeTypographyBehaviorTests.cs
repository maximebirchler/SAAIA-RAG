using System.Diagnostics;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class NativeTypographyBehaviorTests
{
    [Fact]
    public void Native_typography_compact_glyph_tuples_preserve_every_literal_offset_and_baseline_exactly()
    {
        var bytes = NativeTypographyPdfFixture.Pdf("BT /F1 12 Tf 60 600 Td (A  B - 5.) Tj ET\n");
        var (document, extraction) = NativeTypographyPdfFixture.Extract(bytes); CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        using var pdf = PdfDocument.Open(bytes); var letters = pdf.GetPage(1).Letters;
        Assert.Equal(string.Concat(letters.Select(letter => letter.Value)), string.Concat(set.Runs!.Select(run => run.Text)));
        var json = CanonicalContractJson.SerializeCanonical(set);
        var restored = CanonicalContractJson.Deserialize<CanonicalNativeTypographySet>(json);
        Assert.Equal(json, CanonicalContractJson.SerializeCanonical(restored));
        foreach (var run in restored.Runs!) foreach (var glyph in run.Glyphs)
        {
            var original = letters[glyph.NativeLetterOrdinal];
            Assert.Equal(original.Value, run.Text.Substring(glyph.RunTextOffsetUtf16, glyph.RunTextLengthUtf16));
            Assert.Equal(original.StartBaseLine.X, glyph.StartBaselineX); Assert.Equal(original.StartBaseLine.Y, run.BaselineStartY);
            Assert.Equal(original.EndBaseLine.X, glyph.EndBaselineX); Assert.Equal(original.EndBaseLine.Y, run.BaselineEndY);
        }
        Assert.False(restored.GlyphQuadsCaptured);
        using var view = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, view.RootElement.GetProperty("runs")[0].GetProperty("glyphs")[0].ValueKind);
    }

    [Fact]
    public void Native_typography_mixed_style_text_keeps_separate_reported_ranges_without_uniform_line_style()
    {
        var bytes = NativeTypographyPdfFixture.Pdf("BT /F1 12 Tf 60 600 Td (NORMAL ) Tj /F2 12 Tf (ITALIC) Tj ET\n",
            secondFont: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique >>");
        var (document, extraction) = NativeTypographyPdfFixture.Extract(bytes); CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        Assert.Contains(set.Runs!, run => run.ReportedItalic is true);
        Assert.Contains(set.Runs!, run => run.ReportedItalic is false);
        Assert.Equal("NORMAL ITALIC", string.Concat(set.Runs!.Select(run => run.Text)));
        Assert.False(set.RenderedStyleVerified); Assert.Equal("not_attempted", set.MappingAvailability);
        Assert.All(document.Pages[0].Blocks.SelectMany(block => block.Spans), span => Assert.Null(span.Style));
    }

    [Fact]
    public void Native_typography_malformed_descriptor_does_not_turn_unknown_fields_into_absent_or_false()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract(NativeTypographyPdfFixture.Pdf(
            font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor (invalid) >>"));
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var resource = Assert.Single(document.Pages[0].NativeTypographyObservations!.Resources!);
        Assert.Equal("unavailable", resource.DescriptorAvailability); Assert.Null(resource.FontWeight); Assert.Null(resource.ItalicAngle);
        Assert.Equal("unavailable", resource.FieldAvailability["FontWeight"]);
    }
    [Fact]
    public void Native_typography_literal_glyphs_and_reported_styles_survive_projection_without_primary_rewrite()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract();
        var before = CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks);
        var summary = CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        CanonicalNativeTypographyValidator.ValidateOrThrow(set);
        Assert.Empty(CanonicalContractValidator.Validate(document));
        Assert.Equal(1, summary.ObservedPages); Assert.True(summary.GlyphCount > 0);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks));
        Assert.All(document.Pages[0].Blocks.SelectMany(block => block.Spans), span => Assert.Null(span.Style));
        Assert.Equal("Not italic; 5 - 2.", string.Concat(set.Runs!.Select(run => run.Text)));
        Assert.False(set.RenderedStyleVerified); Assert.Equal("not_attempted", set.MappingAvailability);
        Assert.All(set.Runs!, run => { Assert.Equal(CanonicalNativeTypographyIdentity.Hash(run.Text), run.TextSha256); Assert.NotEqual("proven", run.ResourceAssociation); });
        Assert.Equal(document.Source.Sha256, set.SourceHandleSha256Before); Assert.Equal(set.SourceHandleSha256Before, set.SourceHandleSha256After);
    }

    [Fact]
    public void Native_typography_declared_zero_and_missing_fields_are_not_parser_defaults_or_render_verification()
    {
        var bytes = NativeTypographyPdfFixture.Pdf(font: "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor << /FontName /Helvetica /Flags 0 /FontWeight 0 /ItalicAngle 0 >> >>");
        var (document, extraction) = NativeTypographyPdfFixture.Extract(bytes);
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        var resource = Assert.Single(set.Resources!);
        Assert.Equal(0, resource.Flags); Assert.Equal(0d, resource.FontWeight); Assert.Equal(0d, resource.ItalicAngle);
        Assert.Equal("observed", resource.FieldAvailability["FontWeight"]);
        Assert.Equal("absent", resource.FieldAvailability["FontFamily"]); Assert.Null(resource.FontFamily);
        Assert.Null(resource.DescriptorObjectNumber); Assert.Null(resource.DescriptorObjectGeneration);
        Assert.False(set.RenderedStyleVerified);
        Assert.All(set.Runs!, run => Assert.Equal("observed", run.ReportedFontDetailsAvailability));
    }

    [Fact]
    public void Native_typography_aliases_remain_ambiguous_candidates_and_do_not_pick_a_resource_by_name()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract(NativeTypographyPdfFixture.Pdf(alias: true));
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        Assert.Equal(2, set.Resources!.Count);
        Assert.All(set.Runs!, run => { Assert.Equal("ambiguous", run.ResourceAssociation); Assert.Equal(2, run.CandidateResourceIds!.Count); });
    }

    [Fact]
    public void Native_typography_observed_empty_is_distinct_from_missing_and_refused()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract(NativeTypographyPdfFixture.Pdf(""));
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        Assert.Equal("observed", set.RunAvailability); Assert.Empty(set.Runs!); Assert.Equal(0, set.ObservedGlyphCount); Assert.Equal(0, set.TotalGlyphCount);
        CanonicalNativeTypographyProjector.Apply(document, null);
        Assert.Null(document.Pages[0].NativeTypographyObservations);
        Assert.Contains(CanonicalNativeTypographyProjector.UnavailableFlag, document.Pages[0].QualityFlags);
    }

    [Fact]
    public void Native_typography_missing_payload_in_enabled_document_requires_an_explicit_state()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract();
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var page = CanonicalContractJson.Deserialize<CanonicalPage>(CanonicalContractJson.Serialize(document.Pages[0]));
        page.PageNumber = 2; page.NativeTypographyObservations = null;
        document.Pages.Add(page); document.PageCount = 2;
        Assert.Contains(CanonicalContractValidator.Validate(document), issue => issue.Code == "native_typography_missing_channel_state");
        page.QualityFlags.Add(CanonicalNativeTypographyProjector.BudgetRefusedFlag);
        Assert.DoesNotContain(CanonicalContractValidator.Validate(document), issue => issue.Code == "native_typography_missing_channel_state");
    }

    [Fact]
    public void Native_typography_exhausted_serialization_budget_is_visible_without_payload_or_primary_mutation()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract();
        var before = CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks);
        var summary = CanonicalNativeTypographyProjector.Apply(document, extraction, maximumBytes: 0);
        Assert.Null(document.Pages[0].NativeTypographyObservations);
        Assert.Contains(CanonicalNativeTypographyProjector.BudgetRefusedFlag, document.Pages[0].QualityFlags);
        Assert.Equal(1, summary.RefusedPages); Assert.Equal(1, summary.OmittedBudgetPages);
        Assert.Equal(before, CanonicalContractJson.SerializeCanonical(document.Pages[0].Blocks));
        var stage = CanonicalNativeTypographyProjector.CreateStage(summary);
        Assert.Equal("1", stage.Attributes["omittedBudgetPages"]);
        Assert.Equal("true", stage.Attributes["enabled"]);
    }

    [Fact]
    public void Native_typography_document_glyph_limit_has_unknown_counts_and_preserves_primary()
    {
        using var pdf = PdfDocument.Open(NativeTypographyPdfFixture.Pdf());
        var budget = new PdfNativeTypographyBudget { RemainingGlyphs = 1 };
        var result = PdfNativeTypographyObserver.Observe(pdf.GetPage(1), pdf.Structure, new string('a', 64), null, budget, default);
        Assert.Equal("refused", result.Observation.RunAvailability); Assert.Equal("native_typography_document_glyph_limit", result.Observation.RunReason);
        Assert.Null(result.Observation.Runs); Assert.Null(result.Observation.ObservedGlyphCount); Assert.Null(result.Observation.TotalGlyphCount);
        Assert.Equal(1, result.InspectedGlyphCount);
    }

    [Fact]
    public void Native_typography_budget_charges_only_each_observer_call_and_propagates_cancellation()
    {
        using var pdf = PdfDocument.Open(NativeTypographyPdfFixture.Pdf());
        var budget = new PdfNativeTypographyBudget(); var initial = budget.RemainingTicks;
        var first = PdfNativeTypographyObserver.Observe(pdf.GetPage(1), pdf.Structure, new string('a', 64), null, budget, default);
        var second = PdfNativeTypographyObserver.Observe(pdf.GetPage(1), pdf.Structure, new string('a', 64), null, budget, default);
        Assert.Equal(initial - first.ObserverTicks - second.ObserverTicks, budget.RemainingTicks);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfNativeTypographyObserver.Observe(pdf.GetPage(1), pdf.Structure,
            new string('a', 64), null, budget, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => CanonicalNativeTypographyProjector.Apply(new(), null, cancellation.Token));
    }

    [Fact]
    public void Native_typography_time_refusal_is_not_an_observed_zero_and_does_not_require_whole_page_work()
    {
        using var pdf = PdfDocument.Open(NativeTypographyPdfFixture.Pdf());
        var budget = new PdfNativeTypographyBudget { RemainingTicks = 0 };
        var result = PdfNativeTypographyObserver.Observe(pdf.GetPage(1), pdf.Structure, new string('a', 64), null, budget, default);
        Assert.Equal("refused", result.Observation.RunAvailability); Assert.Null(result.Observation.ObservedGlyphCount);
        Assert.Equal(0, result.InspectedGlyphCount); Assert.True(result.ObserverTicks > 0);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("text")]
    [InlineData("offset")]
    [InlineData("ordinal")]
    [InlineData("resource")]
    [InlineData("proven")]
    [InlineData("rendered")]
    [InlineData("mapping")]
    public void Native_typography_validator_refuses_corrupted_provenance_ranges_hashes_and_unearned_claims(string mutation)
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract(); CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        switch (mutation)
        {
            case "source": set.SourceSha256 = new string('b', 64); break;
            case "text": set.Runs![0].Text += "NOT"; break;
            case "offset": set.Runs![0].Glyphs[0].RunTextOffsetUtf16 = 1; break;
            case "ordinal": set.Runs![0].Glyphs[0].NativeLetterOrdinal = 1; break;
            case "resource": set.Resources![0].FontWeight = 700; break;
            case "proven": set.Runs![0].ResourceAssociation = "proven"; break;
            case "rendered": set.RenderedStyleVerified = true; break;
            case "mapping": set.MappingAvailability = "observed"; break;
        }
        Assert.NotEmpty(CanonicalNativeTypographyValidator.Validate(set));
    }

    [Fact]
    public void Native_typography_rotation_keeps_raw_reports_without_making_a_normalized_or_ocr_mapping()
    {
        var (document, extraction) = NativeTypographyPdfFixture.Extract(NativeTypographyPdfFixture.Pdf(rotation: 90));
        CanonicalNativeTypographyProjector.Apply(document, extraction);
        var set = document.Pages[0].NativeTypographyObservations!;
        Assert.Equal(90, set.CoordinateFrame.ObservedRotationDegrees); Assert.Equal("BOTTOMLEFT", set.CoordinateFrame.RawOrigin);
        Assert.Equal("not_attempted", set.MappingAvailability); Assert.False(set.RenderedStyleVerified);
        Assert.NotEmpty(set.Runs!);
    }

    [Fact]
    public void Native_typography_option_false_is_omitted_from_signed_options_and_default_manifest()
    {
        var json = JsonSerializer.Serialize(new DocumentIntelligenceOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("nativeTypographyObservationsEnabled", json);
    }
}
