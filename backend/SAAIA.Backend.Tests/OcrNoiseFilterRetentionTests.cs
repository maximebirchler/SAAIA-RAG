using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class OcrNoiseFilterRetentionTests
{
    [Theory]
    [InlineData("AC PRESSURE LIMITS 240 bar")]
    [InlineData("MAXIMUM WORKING PRESSURE 240")]
    public void Format_only_cues_do_not_erase_operational_qualifiers_or_numeric_facts(string evidence)
    {
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(evidence));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(evidence));
        Assert.Equal(evidence, OcrNoiseFilter.RemoveTrailingNoisySupplement(evidence));
    }

    [Theory]
    [InlineData("AC PRESSURE LIMITS 240 bar")]
    [InlineData("MAXIMUM WORKING PRESSURE 240")]
    public void Format_only_cues_do_not_remove_real_extracted_units_beside_a_reliable_body(string fact)
    {
        const string body =
            "The technician inspects the installed module, records the measured response and verifies the isolation state before starting the equipment. "
            + "The supervisor compares the recorded measurements with the documented operating conditions and signs the completed inspection report before release.";
        var text = "Inspection protocol\n\n" + fact + "\n\n" + body;
        var page = new ExtractedPdfPage(1, text,
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length, [1]);
        var section = new ExtractedDocumentSection(0, "Inspection protocol", 1, 1, 1, 0, null);

        var units = DocumentUnitExtractor.Extract([page], [section]);

        var retained = Assert.Single(units, unit => unit.Text.Equals(fact, StringComparison.Ordinal));
        Assert.Equal(section.Ordinal, retained.SectionOrdinal);
        Assert.Equal("ok", retained.ExtractionTextStatus);
        Assert.False(retained.ExtractionTextSparse);
        Assert.False(retained.ExtractionOcrCandidate);
        Assert.Contains("text_extraction_ok", retained.ExtractionQualitySignals!);
        Assert.Contains(units, unit => unit.Text.Equals(body, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Cycle 2 h 45 minutes")]
    [InlineData("Sampling 15 min 25 min")]
    [InlineData("Duration 4 h")]
    [InlineData("Grade II for pressure")]
    [InlineData("Pressure: 4 bar Duration: 15 min Qualification: Level B")]
    [InlineData("* SAFETY MODE * 10 min 4 min 1 h 15 min")]
    public void Operational_values_labels_and_qualifications_are_not_ocr_noise(string evidence)
    {
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(evidence));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoisePublishedUnitText(evidence));
        Assert.Equal(evidence, OcrNoiseFilter.RemoveTrailingNoisySupplement(evidence));
    }

    [Theory]
    [InlineData("Verify the actuator setting.")]
    [InlineData("Verify the actuator setting and record its measured response in the signed inspection report.")]
    public void Schedule_after_complete_content_is_retained_even_with_decorative_markers(string completeContent)
    {
        var evidence = completeContent + " * SAFETY MODE * 10 min 4 min 1 h 15 min";

        Assert.Equal(evidence, OcrNoiseFilter.RemoveTrailingNoisySupplement(evidence));
    }

    [Theory]
    [InlineData("Preparation : 50 minutes Duration : 40 minutes Rest : 1 hour")]
    [InlineData("Ne Preparation : 50 minutes QI Cuisson : 40 minutes & Repos : I heure ESA Pas cher Q Facile")]
    public void Readable_duration_values_are_not_deleted_by_classification_or_metadata_prefix_cleaning(string evidence)
    {
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(evidence));
        Assert.Equal(evidence, OcrNoiseFilter.RemoveTrailingNoisySupplement(evidence));
    }

    [Theory]
    [InlineData("N Preparation : 50 minutes D Duration : 40 minutes")]
    [InlineData("N Time : Disconnect power before maintenance.")]
    public void Reliable_duration_or_warning_suffix_is_not_deleted_solely_for_metadata_markers(string suffix)
    {
        const string completeContent =
            "Complete the inspection, record the measured response and archive the signed report.";
        var evidence = completeContent + " " + suffix;

        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(suffix));
        Assert.False(OcrNoiseFilter.LooksLikeProbableNoiseText(evidence));
        Assert.Equal(evidence, OcrNoiseFilter.RemoveTrailingNoisySupplement(evidence));
    }

    [Theory]
    [InlineData("iS) m =| a O om Mm Zz @ = m m 2 Zz Q@) OQ Oo Zz G - > z as | op) oo > Cc ie) m UJ O TT ro) = J | a u Mm U A © 0 OQ =| © Zz UO | W = ©) = cr O = 0")]
    [InlineData("yo o | | i Ne | a \\\"------- abgearbeitet __/ sel 48 a) |b.")]
    public void Independently_corrupted_ocr_suffix_still_gets_removed_after_complete_content(string corruptedScan)
    {
        const string completeContent =
            "Complete the inspection, record the measured response and archive the signed report.";
        var suffix = "N Duration : " + corruptedScan;

        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(corruptedScan));
        Assert.True(OcrNoiseFilter.LooksLikeProbableNoiseText(suffix));
        Assert.Equal(completeContent,
            OcrNoiseFilter.RemoveTrailingNoisySupplement(completeContent + " " + suffix));
    }
}
