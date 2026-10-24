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
    public void ContentClaimWriter_DoesNotPromoteAnUnselectedSamePageNeighbor()
    {
        var bundle = EvidenceBundleBuilder.FromToolResults(
            SamePageSourceWindowResult(),
            "Donne le fait documente selectionne.");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildSelectedEvidenceWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var messages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(method!.Invoke(
            null,
            new object?[]
            {
                Intake("Donne le fait documente selectionne."),
                bundle,
                 new[] { "E1" },
                 "content_claim",
                 null,
                 null,
                 null
             }));
        var prompt = string.Join(
                Environment.NewLine,
                messages.Select(static message => message.Content))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("[E1]\nTITRE:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[E2]\nTITRE:", prompt, StringComparison.Ordinal);
    }
}
