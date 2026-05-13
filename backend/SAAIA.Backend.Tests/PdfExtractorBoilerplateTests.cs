using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfExtractorBoilerplateTests
{
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
}
