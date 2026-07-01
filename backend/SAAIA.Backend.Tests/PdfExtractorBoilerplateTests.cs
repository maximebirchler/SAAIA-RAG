using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractorBoilerplateTests
{
    [Fact]
    public void BuildLayoutAwareText_reconstructs_line_and_paragraph_breaks_from_positioned_words()
    {
        var text = PdfExtractor.BuildLayoutAwareText(
            [
                Word("ACME", 10, 800),
                Word("REPORT", 48, 800),
                Word("1", 10, 760),
                Word("Overview", 28, 760),
                Word("Install", 10, 742),
                Word("the", 58, 742),
                Word("module.", 84, 742),
                Word("Check", 10, 710),
                Word("status.", 56, 710)
            ],
            "ACME REPORT 1 Overview Install the module. Check status.");

        Assert.Equal(
            "ACME REPORT\n\n1 Overview\nInstall the module.\n\nCheck status.",
            text);
    }

    [Fact]
    public void BuildLayoutAwareText_keeps_fallback_when_positioned_words_are_incomplete()
    {
        const string fallback = "Full fallback text keeps every useful token when PDF word geometry is partial.";

        var text = PdfExtractor.BuildLayoutAwareText(
            [
                Word("Full", 10, 800),
                Word("fallback", 44, 800),
                Word("text", 106, 800)
            ],
            fallback);

        Assert.Equal(fallback, text);
    }

    [Fact]
    public void LayoutAwareText_allows_repeated_boilerplate_cleanup_after_flat_pdf_text()
    {
        var pages = Enumerable.Range(1, 3)
            .Select(pageNumber =>
            {
                var usefulPrefix = pageNumber switch
                {
                    1 => "First",
                    2 => "Second",
                    _ => "Third"
                };
                var text = PdfExtractor.BuildLayoutAwareText(
                    [
                        Word("ACME", 10, 800),
                        Word("REPORT", 48, 800),
                        Word("Page", 10, 780),
                        Word(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), 52, 780),
                        Word("of", 70, 780),
                        Word("3", 88, 780),
                        Word(usefulPrefix, 10, 740),
                        Word("useful", 62, 740),
                        Word("evidence", 116, 740),
                        Word("stays", 10, 724),
                        Word("searchable.", 54, 724)
                    ],
                    $"ACME REPORT Page {pageNumber} of 3 {usefulPrefix} useful evidence stays searchable.");
                return (PageNumber: pageNumber, Text: text, ImageCount: 0);
            })
            .ToArray();

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.DoesNotContain("ACME REPORT", page.Text, StringComparison.Ordinal));
        Assert.All(cleaned, page => Assert.DoesNotContain("Page ", page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("First useful evidence", StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Third useful evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_removes_repeated_short_lines_without_domain_terms()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "ACME CONFIDENTIAL DRAFT\nFirst page useful technical text.", 0),
            (2, "ACME CONFIDENTIAL DRAFT\nSecond page useful technical text.", 0),
            (3, "ACME CONFIDENTIAL DRAFT\nThird page useful technical text.", 0),
            (4, "ACME CONFIDENTIAL DRAFT\nFourth page useful technical text.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.DoesNotContain("ACME CONFIDENTIAL DRAFT", page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("First page useful technical text.", StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Fourth page useful technical text.", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_removes_variable_page_number_lines()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "ACME CONFIDENTIAL - page 1\nFirst page useful technical text.", 0),
            (2, "ACME CONFIDENTIAL - page 2\nSecond page useful technical text.", 0),
            (3, "ACME CONFIDENTIAL - page 3\nThird page useful technical text.", 0),
            (4, "ACME CONFIDENTIAL - page 4\nFourth page useful technical text.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.DoesNotContain("ACME CONFIDENTIAL", page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Second page useful technical text.", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_keeps_unique_short_lines()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "FIRST UNIQUE HEADER\nFirst page useful text.", 0),
            (2, "SECOND UNIQUE HEADER\nSecond page useful text.", 0),
            (3, "THIRD UNIQUE HEADER\nThird page useful text.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.Contains("FIRST UNIQUE HEADER", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("SECOND UNIQUE HEADER", cleaned[1].Text, StringComparison.Ordinal);
        Assert.Contains("THIRD UNIQUE HEADER", cleaned[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_removes_standalone_page_markers_at_edges()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "Page 1 of 2\nInstallation notes stay searchable.\n1", 0),
            (2, "2 / 2\nOperational checklist stays searchable.\n- 2 -", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.DoesNotContain("Page 1 of 2", cleaned[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n1", cleaned[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("2 / 2", cleaned[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- 2 -", cleaned[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Installation notes stay searchable.", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Operational checklist stays searchable.", cleaned[1].Text, StringComparison.Ordinal);
    }

    private static PdfLayoutWord Word(string text, double left, double top, double width = 28, double height = 10)
        => new(text, left, left + width, top, top - height);
}
