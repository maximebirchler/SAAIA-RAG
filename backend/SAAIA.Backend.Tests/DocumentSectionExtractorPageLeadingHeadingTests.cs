using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentSectionExtractorPageLeadingHeadingTests
{
    [Theory]
    [InlineData("Regional control matrix")]
    [InlineData("Calibration")]
    public void A_page_leading_source_title_with_structured_body_does_not_require_metadata_before_it(string title)
    {
        var text = title + "\n"
            + "\u2022 6 components inspected before startup\n"
            + "\u2022 Record the measured pressure in the validation log.\n"
            + "Duration : 45 minutes\nLevel : Medium";

        var sections = DocumentSectionExtractor.Extract([Page(5, text)]);

        var section = Assert.Single(sections, section => section.Title == title);
        Assert.Equal(5, section.PageStart);
        Assert.Equal(5, section.PageEnd);
        Assert.Equal(1, section.StartLine);
    }

    [Fact]
    public void Leading_titles_bound_physical_sections_and_assign_following_evidence_to_its_own_subject()
    {
        ExtractedPdfPage[] pages =
        [
            Page(4, "DOCUMENT OVERVIEW\nThis overview explains the document organization and its inspection procedures."),
            Page(5, "Regional control matrix\n\u2022 6 components inspected before startup\n\u2022 Record the measured pressure in the validation log.\nDuration : 45 minutes\nLevel : Medium"),
            Page(6, "Calibration\n\u2022 24 V control supply must be isolated before adjustment\n\u2022 Validate the sensor reading and record the verification result.\nDuration : 20 minutes\nLevel : Medium")
        ];

        var sections = DocumentSectionExtractor.Extract(pages);
        var units = DocumentUnitExtractor.Extract(pages, sections);

        var overview = Assert.Single(sections, section => section.Title == "DOCUMENT OVERVIEW");
        var regional = Assert.Single(sections, section => section.Title == "Regional control matrix");
        var calibration = Assert.Single(sections, section => section.Title == "Calibration");
        Assert.Equal((4, 4), (overview.PageStart, overview.PageEnd));
        Assert.Equal((5, 5), (regional.PageStart, regional.PageEnd));
        Assert.Equal((6, 6), (calibration.PageStart, calibration.PageEnd));
        Assert.Contains(units, unit => unit.PageStart == 5 && unit.PageEnd == 5
            && unit.SectionOrdinal == regional.Ordinal && unit.Text.Contains("6 components", StringComparison.Ordinal));
        Assert.Contains(units, unit => unit.PageStart == 6 && unit.PageEnd == 6
            && unit.SectionOrdinal == calibration.Ordinal && unit.Text.Contains("24 V", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Remove the cover before starting the machine")]
    [InlineData("The operator must inspect the safety enclosure before starting.")]
    [InlineData("Page 5")]
    [InlineData("5")]
    [InlineData("Duration : 45 minutes")]
    public void A_page_leading_sentence_counter_or_metadata_value_is_not_promoted_to_a_title(string firstLine)
    {
        var sections = DocumentSectionExtractor.Extract([Page(5, firstLine
            + "\n\u2022 6 components inspected before startup\n\u2022 Record the measured pressure in the validation log.")]);

        Assert.DoesNotContain(sections, section => section.Title == firstLine);
    }

    [Fact]
    public void A_navigation_label_remains_navigation_even_when_numbered_entries_follow()
    {
        const string title = "Sommaire";
        var sections = DocumentSectionExtractor.Extract([Page(5, title
            + "\n1 Installation procedure for the enclosure\n2 Maintenance procedure for the enclosure")]);

        Assert.DoesNotContain(sections, section => section.Title == title);
    }

    [Fact]
    public void A_page_leading_title_shape_without_structured_body_does_not_suffice()
    {
        const string title = "Regional control matrix";
        var sections = DocumentSectionExtractor.Extract([Page(5, title + "\nUnstructured fragments only")]);

        Assert.DoesNotContain(sections, section => section.Title == title);
    }

    [Fact]
    public void An_unmarked_mixed_case_line_inside_a_page_does_not_gain_page_leading_evidence()
    {
        const string title = "Regional control matrix";
        var sections = DocumentSectionExtractor.Extract([Page(5,
            "The preceding paragraph remains part of its existing section.\n" + title
            + "\n\u2022 6 components inspected before startup\n\u2022 Record the measured pressure in the validation log.")]);

        Assert.DoesNotContain(sections, section => section.Title == title);
    }

    private static ExtractedPdfPage Page(int number, string text)
        => new(number, text, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            text.Length, [(byte)number]);
}
