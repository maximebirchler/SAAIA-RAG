using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedBudgetHandoffContractTests
{
    [Theory]
    [InlineData("fr", "capacité locale", "analyse avancée")]
    [InlineData("en", "local capability", "Advanced analysis")]
    public void Budget_exhaustion_is_an_explicit_advanced_handoff(
        string language,
        string localLimitMarker,
        string advancedMarker)
    {
        var handoff = SourceBackedBudgetHandoffContract.Create(language);

        Assert.Equal("advanced_analysis_required", handoff.Intent);
        Assert.Equal("local_model_budget_exhausted", handoff.ReasonCode);
        Assert.Contains(localLimitMarker, handoff.Answer);
        Assert.Contains(advancedMarker, handoff.Answer);
        Assert.False(handoff.HasVisibleSources);
    }

    [Fact]
    public void Budget_handoff_does_not_claim_that_sources_were_found()
    {
        var answer = SourceBackedBudgetHandoffContract.Create("fr").Answer;

        Assert.DoesNotContain("sources trouvées", answer);
        Assert.DoesNotContain("preuves trouvées", answer);
        Assert.DoesNotContain("source consultée", answer);
    }
}
