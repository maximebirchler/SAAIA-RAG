using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentProfileProjectorTests
{
    [Fact]
    public void Project_builds_generic_profile_from_document_structure()
    {
        var pages = new[]
        {
            new ExtractedPdfPage(1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 10, 78, [1])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Inerting Safety", 1, 1, 1, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "Inerting safety controls require oxygen monitoring and nitrogen purge validation.", 78, 10, [2])
        };
        var exact = new[]
        {
            new ExtractedExactMatchEntry(0, 0, 0, 1, 1, "EN 15281", "en 15281", 7, 2, [3], "standard_ref")
        };

        var profile = DocumentProfileProjector.Project("ATEX/CEN TR 15281.pdf", pages, sections, units, exact);

        Assert.Equal("deterministic_v1", profile.ProfileVersion);
        Assert.Contains("CEN TR 15281.pdf", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("inerting", profile.Keywords);
        Assert.Contains("EN 15281", profile.Entities);
        Assert.Contains("Inerting Safety", profile.Topics);
        Assert.Contains(profile.HypotheticalQuestions, q => q.Contains("inerting", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("EN 15281", profile.SearchText, StringComparison.Ordinal);
        Assert.True(profile.TokenCount > 0);
        Assert.Equal(32, profile.Checksum.Length);
    }
}
