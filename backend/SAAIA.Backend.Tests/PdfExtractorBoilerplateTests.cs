using Xunit;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractorBoilerplateTests
{
    [Theory]
    [InlineData("Disconnect power before maintenance")]
    [InlineData("Reference capacity ½ litre")]
    public void RemoveRepeatedPageBoilerplate_preserves_repeated_operational_text_without_pagination_proof(string fact)
    {
        var labels = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
        var pages = Enumerable.Range(1, labels.Length).Select(number =>
            (PageNumber: number,
             Text: $"Page {number} of 4\n{labels[number - 1]} inspection\n"
                 + fact + "\n"
                 + $"Distinct observation {labels[number - 1]} remains available.",
             ImageCount: 0)).ToArray();

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        foreach (var page in cleaned)
        {
            Assert.Contains(fact, page.Text, StringComparison.Ordinal);
            Assert.DoesNotContain($"Page {page.PageNumber} of 4", page.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_preserves_identical_numeric_facts_and_variable_measurements()
    {
        var labels = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
        var pages = Enumerable.Range(1, labels.Length).Select(number =>
            (PageNumber: number,
             Text: $"Page {number} of 4\n{labels[number - 1]} technical section\n"
                 + "Rated pressure 4 bar\n"
                 + $"Measured pressure {number + 5} bar\n"
                 + "Tolerance 0.25 mm\n"
                 + $"Unique observation {labels[number - 1]} stays available.\nINTERNAL COPY",
             ImageCount: 0)).ToArray();

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        for (var index = 0; index < cleaned.Count; index++)
        {
            Assert.Contains("Rated pressure 4 bar", cleaned[index].Text, StringComparison.Ordinal);
            Assert.Contains($"Measured pressure {index + 6} bar", cleaned[index].Text, StringComparison.Ordinal);
            Assert.Contains("Tolerance 0.25 mm", cleaned[index].Text, StringComparison.Ordinal);
            Assert.DoesNotContain($"Page {index + 1} of 4", cleaned[index].Text, StringComparison.Ordinal);
            Assert.Contains("INTERNAL COPY", cleaned[index].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Extract_preserves_repeated_ascii_numeric_facts_and_warnings_in_real_pdf_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saaia-numeric-facts-{Guid.NewGuid():N}.pdf");
        try
        {
            var labels = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            for (var number = 1; number <= labels.Length; number++)
            {
                var page = builder.AddPage(PageSize.A4);
                var lines = new[]
                {
                    $"Page {number} of 4", $"TECHNICAL SHEET {labels[number - 1]}",
                    "Rated pressure 4 bar", $"Measured pressure {number + 5} bar", "Tolerance 0.25 mm",
                    "Disconnect power before maintenance", "Reference capacity 1/2 litre",
                    $"Distinct useful observation {labels[number - 1]} remains available as source evidence."
                };
                for (var line = 0; line < lines.Length; line++)
                    page.AddText(lines[line], 11, new PdfPoint(40, 800 - line * 35), font);
            }
            File.WriteAllBytes(path, builder.Build());

            // Standard14 gives this portable fixture ASCII coverage only.
            // Unicode fractions are covered by the separate pure text theory,
            // not claimed as generated-PDF glyph coverage. Establish the raw
            // font mapping before attributing a loss to production cleanup.
            using (var raw = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                foreach (var page in raw.GetPages())
                    Assert.Contains("Reference capacity 1/2 litre", page.Text, StringComparison.Ordinal);
            }

            var extracted = PdfExtractor.Extract(path);

            Assert.Equal(4, extracted.Pages.Count);
            foreach (var page in extracted.Pages)
            {
                Assert.Contains("Rated pressure 4 bar", page.Text, StringComparison.Ordinal);
                Assert.Contains($"Measured pressure {page.PageNumber + 5} bar", page.Text, StringComparison.Ordinal);
                Assert.Contains("Tolerance 0.25 mm", page.Text, StringComparison.Ordinal);
                Assert.Contains("Disconnect power before maintenance", page.Text, StringComparison.Ordinal);
                Assert.Contains("Reference capacity 1/2 litre", page.Text, StringComparison.Ordinal);
                Assert.DoesNotContain($"Page {page.PageNumber} of 4", page.Text, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

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
    public void LayoutAwareText_preserves_repeated_headings_and_removes_only_explicit_page_counters()
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

        Assert.All(cleaned, page => Assert.Contains("ACME REPORT", page.Text, StringComparison.Ordinal));
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
    public void RemoveRepeatedPageBoilerplate_preserves_repeated_headers_without_proof_they_are_useless()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "ACME CONFIDENTIAL DRAFT\nFirst page useful technical text.", 0),
            (2, "ACME CONFIDENTIAL DRAFT\nSecond page useful technical text.", 0),
            (3, "ACME CONFIDENTIAL DRAFT\nThird page useful technical text.", 0),
            (4, "ACME CONFIDENTIAL DRAFT\nFourth page useful technical text.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.Contains("ACME CONFIDENTIAL DRAFT", page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("First page useful technical text.", StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Fourth page useful technical text.", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_preserves_repeated_layout_legends_that_may_explain_the_document()
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

        Assert.All(cleaned, page => Assert.Contains(legend, page.Text, StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("First useful paragraph", StringComparison.Ordinal));
        Assert.Contains(cleaned, page => page.Text.Contains("Closing content stays available.", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_preserves_headers_even_when_they_include_a_page_number()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "ACME CONFIDENTIAL - page 1\nFirst page useful technical text.", 0),
            (2, "ACME CONFIDENTIAL - page 2\nSecond page useful technical text.", 0),
            (3, "ACME CONFIDENTIAL - page 3\nThird page useful technical text.", 0),
            (4, "ACME CONFIDENTIAL - page 4\nFourth page useful technical text.", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.All(cleaned, page => Assert.Contains($"ACME CONFIDENTIAL - page {page.PageNumber}", page.Text, StringComparison.Ordinal));
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
    public void RemoveRepeatedPageBoilerplate_removes_explicit_pagination_and_preserves_uncertain_numeric_edges()
    {
        var pages = new List<(int PageNumber, string Text, int ImageCount)>
        {
            (1, "Page 1 of 2\nInstallation notes stay searchable.\n1", 0),
            (2, "2 / 2\nOperational checklist stays searchable.\n- 2 -", 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.DoesNotContain("Page 1 of 2", cleaned[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\n1", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("2 / 2", cleaned[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- 2 -", cleaned[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Installation notes stay searchable.", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Operational checklist stays searchable.", cleaned[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_preserves_page_labels_with_different_physical_identity_or_body_position()
    {
        var pages = new[]
        {
            (PageNumber: 1, Text: "Page 99 of 200\nFirst content line.\nSecond content line.\nPage 1\nThird content line.\nFourth content line.\nClosing content.", ImageCount: 0)
        };

        var cleaned = PdfExtractor.RemoveRepeatedPageBoilerplate(pages);

        Assert.Equal(pages[0].Text, Assert.Single(cleaned).Text);
    }

    [Fact]
    public void RemoveRepeatedPageBoilerplate_preserves_uncertain_standalone_numeric_values_between_prose()
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

        // A number between prose is not proof of pagination; it may be a
        // table value or an operational result. Only established counters go.
        Assert.Contains("\n235\n", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Prepare the module", cleaned[0].Text, StringComparison.Ordinal);
        Assert.Contains("Continue the procedure", cleaned[0].Text, StringComparison.Ordinal);
    }

    private static PdfLayoutWord Word(string text, double left, double top, double width = 28, double height = 10)
        => new(text, left, left + width, top, top - height);
}
