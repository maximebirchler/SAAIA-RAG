using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisDocumentResolutionTests
{
    [Fact]
    public void Known_document_without_hits_remains_a_resolved_filter_not_documentary_evidence()
    {
        const string path = "Corpus/reference.pdf";
        var resolved = AdvancedAnalysisToolGateway.SelectStrongDocumentScope("reference.pdf", [path]);
        var diagnostic = AdvancedAnalysisToolGateway.DescribeDocumentResolution("reference.pdf", [path], resolved);
        var observation = new AdvancedAnalysisSearchObservation("absent terms", [], [], 1, 1,
            DocumentResolution: diagnostic);

        Assert.Equal("resolved", diagnostic.Status);
        Assert.Equal(path, diagnostic.ResolvedDocPath);
        Assert.Empty(observation.Evidence);
    }

    [Fact]
    public void Ambiguous_versions_do_not_grant_a_path_for_source_navigation()
    {
        string[] paths = ["Current/reference-2026.pdf", "Archive/reference-2025.pdf"];
        var resolved = AdvancedAnalysisToolGateway.SelectStrongDocumentScope("reference", paths);
        var diagnostic = AdvancedAnalysisToolGateway.DescribeDocumentResolution("reference", paths, resolved);

        Assert.Null(resolved);
        Assert.Equal("ambiguous", diagnostic.Status);
        Assert.Equal(2, diagnostic.CandidateCount);
        Assert.Null(diagnostic.ResolvedDocPath);
    }

    [Theory]
    [InlineData("missing-reference.pdf", "no_match")]
    [InlineData("pdf", "hint_too_short")]
    public void Missing_and_underqualified_hints_are_distinguishable(string hint, string expected)
    {
        var diagnostic = AdvancedAnalysisToolGateway.DescribeDocumentResolution(hint, [], null);
        Assert.Equal(expected, diagnostic.Status);
        Assert.Null(diagnostic.ResolvedDocPath);
    }
}
