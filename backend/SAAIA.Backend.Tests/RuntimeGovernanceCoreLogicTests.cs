using SAAIA.Backend;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RuntimeGovernanceCoreLogicTests
{
    [Fact]
    public void EvaluateSelectionUpdate_rejects_authorization_when_capability_is_stale()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: false,
            Selected: false,
            ProfileKey: "default-local",
            PassCount: 3,
            Stale: true,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: true,
                Authorized: true,
                Selected: false));

        Assert.False(decision.Accepted);
        Assert.Equal("capability must be requalified because its qualification is stale", decision.Error);
        Assert.Equal(current, decision.State);
    }

    [Fact]
    public void EvaluateSelectionUpdate_disabling_capability_clears_authorization_and_selection()
    {
        var current = new AdminRuntimeCapabilityStateDto(
            Key: "core.retrieval",
            DisplayName: "Core retrieval",
            Family: "core",
            RuntimeKey: "retrieval-stack",
            Implemented: true,
            DesiredEnabled: true,
            Installed: true,
            Configured: true,
            Healthy: true,
            Qualified: true,
            Authorized: true,
            Selected: true,
            ProfileKey: "default-local",
            PassCount: 3,
            PersistedAuthorized: true,
            PersistedSelected: true,
            EffectiveAuthorized: true,
            EffectiveSelected: true);

        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(
            current,
            new AdminRuntimeCapabilitySelectionRequestDto(
                DesiredEnabled: false,
                Authorized: null,
                Selected: null));

        Assert.True(decision.Accepted);
        Assert.False(decision.State.DesiredEnabled);
        Assert.False(decision.State.Authorized);
        Assert.False(decision.State.Selected);
        Assert.False(decision.State.EffectiveAuthorized);
        Assert.False(decision.State.EffectiveSelected);
    }

    [Fact]
    public void BuildDefaultState_for_capability_b_reflects_disabled_backoffice_runtime()
    {
        var definition = Assert.Single(
            RuntimeCapabilityRegistry.Definitions,
            item => item.Key == "capability_b.backoffice_generation");
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");

        try
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", null);

            var state = RuntimeCapabilityStateResolver.BuildDefaultState(
                definition,
                new RuntimeGovernanceOptions(),
                new RagOptions());

            Assert.True(state.Implemented);
            Assert.True(state.Installed);
            Assert.False(state.Configured);
            Assert.False(state.Qualified);
            Assert.False(state.Selected);
            Assert.NotNull(state.Details);
            Assert.Equal("backoffice_disabled", state.Details!["status"]);
            Assert.Equal(false, state.Details["backofficeEnabled"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }
}
