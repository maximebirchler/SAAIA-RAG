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
    public void SemanticAuditZeroYieldResolution_RequiresACompleteMechanicalTrigger()
    {
        Assert.True(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 1,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.True(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 2,
                requiredCount: 3,
                executedRequestCount: 2));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 0,
                requiredCount: 7,
                executedRequestCount: 3));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 1));
        Assert.False(SourceBackedAgentV2Runner
            .ShouldRequestSemanticAuditZeroYieldResolutionForTests(
                approvedCount: 0,
                rejectedCount: 3,
                requiredCount: 3,
                executedRequestCount: 2,
                hasRequiredEvidence: true));
    }
}
