using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedSemanticLayoutContractTests
{
    [Fact]
    public void Propagation_replaces_stale_typed_axes_with_the_llm_authored_layout()
    {
        var intake = new SourceBackedIntake(
            "Build the requested comparison.",
            "rag.structured",
            Array.Empty<string>(),
            new[]
            {
                "constraint:keep concise",
                "day:obsolete row",
                "slot:obsolete column"
            },
            AllowsPartialAnswer: false,
            Language: "en");

        var updated = SourceBackedAgentV2Runner.ApplySemanticLayoutContractToIntake(
            intake,
            "Item",
            new[] { "Alpha", "Beta" },
            new[] { "Current", "Target" });

        Assert.Equal("Item", updated.RowHeaderLabel);
        Assert.Equal(
            new[]
            {
                "constraint:keep concise",
                "row:Alpha",
                "row:Beta",
                "column:Current",
                "column:Target"
            },
            updated.RequestedAxes);
    }

    [Fact]
    public void Propagation_does_nothing_without_a_complete_two_dimensional_layout()
    {
        var intake = new SourceBackedIntake(
            "Give one answer.",
            "rag.simple",
            Array.Empty<string>(),
            new[] { "constraint:grounded" },
            AllowsPartialAnswer: false,
            Language: "en");

        var updated = SourceBackedAgentV2Runner.ApplySemanticLayoutContractToIntake(
            intake,
            "Item",
            new[] { "Only row" },
            new[] { "Value", "Source" });

        Assert.Same(intake, updated);
    }
}
