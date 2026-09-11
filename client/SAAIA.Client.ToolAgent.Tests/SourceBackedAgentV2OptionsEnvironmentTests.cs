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

    [Fact]
    public void Advanced_provider_defaults_expand_context_and_cumulative_budget()
    {
        var variables = new[]
        {
            "SAAIA_SOURCE_BACKED_AGENT_V2_CONTEXT_TOKENS",
            "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_TOKENS",
            "SAAIA_SOURCE_BACKED_AGENT_V2_MAX_CUMULATIVE_LLM_ELAPSED_MS",
            "SAAIA_SOURCE_BACKED_AGENT_V2_TERMINAL_RESERVE_TOKENS",
            "SAAIA_SOURCE_BACKED_AGENT_V2_TERMINAL_RESERVE_MS"
        };
        var previous = variables.ToDictionary(
            static variable => variable,
            Environment.GetEnvironmentVariable);
        try
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, null);

            var options = SourceBackedAgentV2Options.ResolveFromEnvironment(
                providerContextTokens: 1_050_000,
                advancedCapacity: true);

            Assert.Equal(262_144, options.MaximumContextTokens);
            Assert.Equal(48_000, options.MaximumCumulativeLlmTokens);
            Assert.Equal(600_000, options.MaximumCumulativeLlmElapsedMilliseconds);
            Assert.Equal(8_000, options.CumulativeLlmTerminalReserveTokens);
            Assert.Equal(120_000, options.CumulativeLlmTerminalReserveMilliseconds);
        }
        finally
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, previous[variable]);
        }
    }
}
