using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedAgentV2OptionsEnvironmentTests
{
    private const string Variable =
        "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_TOKENS";

    [Fact]
    public void Cumulative_token_budget_keeps_default_and_accepts_bounded_override()
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);
            Assert.Equal(
                12_000,
                SourceBackedAgentV2Options.ResolveFromEnvironment()
                    .MaximumCumulativeLlmTokens);

            Environment.SetEnvironmentVariable(Variable, "16000");
            Assert.Equal(
                16_000,
                SourceBackedAgentV2Options.ResolveFromEnvironment()
                    .MaximumCumulativeLlmTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }
}
