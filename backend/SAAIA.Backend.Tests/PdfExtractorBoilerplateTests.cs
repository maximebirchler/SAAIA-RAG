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
    public void BuildLayoutAwareText_reads_stable_two_column_layout_by_column_before_rows()
    {
        var text = PdfExtractor.BuildLayoutAwareText(
            [
                Word("INGREDIENTS", 10, 800, width: 80),
                Word("PREPARATION", 260, 800, width: 92),
                Word("250", 10, 780),
                Word("ml", 46, 780, width: 16),
                Word("water", 70, 780, width: 42),
                Word("Wash", 260, 780, width: 42),
                Word("apple.", 310, 780, width: 48),
                Word("1", 10, 762, width: 12),
                Word("apple", 30, 762, width: 42),
                Word("Serve", 260, 762, width: 46),
                Word("cold.", 314, 762, width: 42)
            ],
            "INGREDIENTS PREPARATION 250 ml water Wash apple. 1 apple Serve cold.");

        Assert.Contains("INGREDIENTS\n250 ml water\n1 apple", text, StringComparison.Ordinal);
        Assert.Contains("PREPARATION\nWash apple.\nServe cold.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INGREDIENTS PREPARATION", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("1 apple", StringComparison.Ordinal) < text.IndexOf("PREPARATION", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLayoutAwareText_reads_nested_columns_inside_side_by_side_blocks()
    {
        var text = PdfExtractor.BuildLayoutAwareText(
            [
                Word("LEFT", 48, 800, width: 36),
                Word("ITEM", 90, 800, width: 36),
                Word("RIGHT", 552, 800, width: 44),
                Word("ITEM", 602, 800, width: 36),

                Word("1", 10, 770, width: 10),
                Word("apple", 28, 770, width: 40),
                Word("Start", 180, 770, width: 40),
                Word("left.", 226, 770, width: 36),
                Word("10", 510, 770, width: 18),
                Word("bolts", 534, 770, width: 38),
                Word("Start", 690, 770, width: 40),
                Word("right.", 736, 770, width: 42),

                Word("2", 10, 752, width: 10),
                Word("pears", 28, 752, width: 38),
                Word("Finish", 180, 752, width: 46),
                Word("left.", 232, 752, width: 36),
                Word("20", 510, 752, width: 18),
                Word("nuts", 534, 752, width: 34),
                Word("Finish", 690, 752, width: 46),
                Word("right.", 742, 752, width: 42),

                Word("3", 10, 734, width: 10),
                Word("plums", 28, 734, width: 42),
                Word("Serve", 180, 734, width: 40),
                Word("left.", 226, 734, width: 36),
                Word("30", 510, 734, width: 18),
                Word("screws", 534, 734, width: 48),
                Word("Serve", 690, 734, width: 40),
                Word("right.", 736, 734, width: 42)
            ],
            "LEFT ITEM RIGHT ITEM 1 apple Start left. 10 bolts Start right. 2 pears Finish left. 20 nuts Finish right. 3 plums Serve left. 30 screws Serve right.");

        Assert.DoesNotContain("LEFT ITEM RIGHT ITEM", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("3 plums", StringComparison.Ordinal) < text.IndexOf("Start left.", StringComparison.Ordinal), text);
        Assert.True(text.IndexOf("Finish left.", StringComparison.Ordinal) < text.IndexOf("RIGHT ITEM", StringComparison.Ordinal), text);
        Assert.True(text.IndexOf("30 screws", StringComparison.Ordinal) < text.IndexOf("Start right.", StringComparison.Ordinal), text);
    }

    [Fact]
    public void BuildLayoutAwareText_keeps_multiline_nested_heading_columns_separate()
    {
        var text = PdfExtractor.BuildLayoutAwareText(
            [
                Word("PETITS", 10, 820, width: 40),
                Word("QUICHE", 180, 820, width: 54),
                Word("À", 240, 820, width: 12),
                Word("DÉJ", 10, 802, width: 26),
                Word("LA", 180, 802, width: 18),
                Word("CLÉRIOT", 204, 802, width: 58),
                Word("MUFFINS", 510, 820, width: 62),
                Word("À", 578, 820, width: 12),
                Word("PETITS", 780, 820, width: 40),
                Word("LA", 510, 802, width: 18),
                Word("COURGETTE", 534, 802, width: 78),
                Word("DÉJ", 780, 802, width: 26),

                Word("1", 10, 770, width: 10),
                Word("apple", 28, 770, width: 40),
                Word("Start", 180, 770, width: 40),
                Word("left.", 226, 770, width: 36),
                Word("10", 510, 770, width: 18),
                Word("bolts", 534, 770, width: 38),
                Word("Start", 690, 770, width: 40),
                Word("right.", 736, 770, width: 42),
                Word("2", 10, 752, width: 10),
                Word("pears", 28, 752, width: 38),
                Word("Finish", 180, 752, width: 46),
                Word("left.", 232, 752, width: 36),
                Word("20", 510, 752, width: 18),
                Word("nuts", 534, 752, width: 34),
                Word("Finish", 690, 752, width: 46),
                Word("right.", 742, 752, width: 42),
                Word("3", 10, 734, width: 10),
                Word("plums", 28, 734, width: 42),
                Word("Serve", 180, 734, width: 40),
                Word("left.", 226, 734, width: 36),
                Word("30", 510, 734, width: 18),
                Word("screws", 534, 734, width: 48),
                Word("Serve", 690, 734, width: 40),
                Word("right.", 736, 734, width: 42)
            ],
            "PETITS QUICHE À DÉJ LA CLÉRIOT MUFFINS À PETITS LA COURGETTE DÉJ");

        Assert.Contains("PETITS\nDÉJ", text, StringComparison.Ordinal);
        Assert.Contains("QUICHE À\nLA CLÉRIOT", text, StringComparison.Ordinal);
        Assert.Contains("MUFFINS À\nLA COURGETTE", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("LA COURGETTE", StringComparison.Ordinal)
            < text.LastIndexOf("PETITS", StringComparison.Ordinal),
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
    public void RemoveRepeatedPageBoilerplate_preserves_layout_region_boundaries()
    {
        var pages = new[]
        {
            (PageNumber: 1, Text: "LEFT ITEM\n400 g material\n\n\nRIGHT ITEM\n900 ml fluid", ImageCount: 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.Single(cleaned);
        Assert.Contains("400 g material\n\n\nRIGHT ITEM", cleaned[0].Text, StringComparison.Ordinal);
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
    public void RemoveRepeatedPageBoilerplate_removes_long_repeated_layout_legend_lines()
    {
        const string legend = "Shaded text = Revisions, A = Text deletions and figure/table revisions. += Section deletions. N = New material.";
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, $"First useful paragraph remains searchable.\n{legend}\nContinuation text stays available.", 0),
            (2, $"Second useful paragraph remains searchable.\n{legend}\nMore content stays available.", 0),
            (3, $"Third useful paragraph remains searchable.\n{legend}\nFinal content stays available.", 0),
            (4, $"Fourth useful paragraph remains searchable.\n{legend}\nClosing content stays available.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.DoesNotContain("Shaded text = Revisions", page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("First useful paragraph", StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Closing content stays available.", StringComparison.Ordinal));
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

    [Fact]
    public void RemoveRepeatedPageBoilerplate_removes_floating_numeric_page_markers_between_prose()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1,
                "Introduction text stays searchable.\n"
                + "Prepare the module and verify the signal.\n"
                + "235\n"
                + "Continue the procedure and record the result.\n"
                + "Final evidence remains available.",
                0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.DoesNotContain("\n235\n", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Prepare the module", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Continue the procedure", cleaned[0].Text, StringComparison.Ordinal);
    }

    private static PdfLayoutWord Word(string text, double left, double top, double width = 28, double height = 10)
        => new(text, left, left + width, top, top - height);
}
