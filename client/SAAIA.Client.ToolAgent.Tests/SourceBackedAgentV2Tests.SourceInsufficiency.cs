using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public void SourceInsufficiency_IsRejectedOutsideAYieldResolutionTurn()
    {
        const string arguments =
            "{\"reason\":\"Les routes observées ne fournissent pas la preuve demandée.\"}";

        var outsideResolution = SourceBackedAgentV2Runner
            .ReadSourceInsufficiencyForTests(
                arguments,
                yieldResolutionAllowed: false);
        var withoutObservation = SourceBackedAgentV2Runner
            .ReadSourceInsufficiencyForTests(
                arguments,
                yieldResolutionAllowed: true,
                executedRequestCount: 0);

        Assert.False(outsideResolution.Accepted);
        Assert.Equal(
            "source_insufficiency_requires_yield_resolution_turn",
            outsideResolution.FailureReason);
        Assert.False(withoutObservation.Accepted);
        Assert.Equal(
            "source_insufficiency_requires_corpus_observation",
            withoutObservation.FailureReason);
    }
}
