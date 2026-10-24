using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalContentClassifierNumericProseTests
{
    [Fact]
    public void Locator_plus_numeric_ending_prose_is_mixed_instead_of_navigation_only()
    {
        var signal = RetrievalContentClassifier.AnalyzeEvidenceContent(
            "Observed page 1\nR1\nDocumented procedure for unit 1.");
        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, signal.ContentRole);
        Assert.False(RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(
            "Observed page 1\nR1\nDocumented procedure for unit 1.", null, "R1"));
        var locatorOnly = RetrievalContentClassifier.AnalyzeEvidenceContent("Option alpha page 1");
        Assert.Equal(RetrievalContentClassifier.NavigationRole, locatorOnly.ContentRole);
    }
}
