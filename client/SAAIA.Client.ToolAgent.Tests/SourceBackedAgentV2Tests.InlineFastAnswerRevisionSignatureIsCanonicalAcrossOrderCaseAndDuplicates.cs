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
    public void InlineFastAnswerRevisionSignatureIsCanonicalAcrossOrderCaseAndDuplicates()
    {
        Assert.Equal(
            "E3,E5",
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "e5", " E3 ", "E5", "", "  " }));
        Assert.Equal(
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "E3", "E5" }),
            SourceBackedAgentV2Runner.BuildInlineFastAnswerRevisionSignature(
                new[] { "e5", "e3" }));
    }
}
