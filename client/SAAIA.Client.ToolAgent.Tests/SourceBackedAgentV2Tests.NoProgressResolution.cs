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
    public void NoProgressResolution_ExposesAnExplicitSourceInsufficiencyDecision()
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildCompactSingleFollowUpTools",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var ordinaryTools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
            method!.Invoke(
                null,
                new object[]
                {
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    false
                }));
        var resolutionTools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
            method.Invoke(
                null,
                new object[]
                {
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    true
                }));

        Assert.DoesNotContain(
            ordinaryTools,
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "declare_source_insufficiency");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "request_user_clarification");
        Assert.Contains(
            resolutionTools,
            static tool => tool.Name == "submit_research_action");
    }
}
