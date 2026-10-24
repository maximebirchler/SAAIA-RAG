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
    public void SemanticAuditZeroYieldContinuationBudget_ClosesOversampledAuditImmediately()
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "DetermineSemanticYieldResolutionContinuationLimit",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(1, method!.Invoke(null, new object[] { 3, 3, 2 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 4, 7, 3 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 6, 3, 2 }));
        Assert.Equal(0, method.Invoke(null, new object[] { 40, 3, 2 }));
    }
}
