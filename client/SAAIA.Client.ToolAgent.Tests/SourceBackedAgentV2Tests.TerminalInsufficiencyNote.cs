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

    [Theory]
    [InlineData(
        "fr",
        1,
        0,
        "Après 1 action documentaire, le juge LLM a conclu qu'aucune source citable non rejetée ne suffisait au livrable demandé.")]
    [InlineData(
        "fr",
        2,
        1,
        "Après 2 actions documentaires, le juge LLM a conclu que la seule source citable non rejetée ne suffisait au livrable demandé.")]
    [InlineData(
        "en",
        1,
        1,
        "After 1 document-retrieval action, the LLM judge concluded that the single non-rejected citable source was insufficient for the requested deliverable.")]
    [InlineData(
        "en",
        2,
        2,
        "After 2 document-retrieval actions, the LLM judge concluded that the 2 non-rejected citable sources were insufficient for the requested deliverable.")]
    public void TerminalInsufficiencyNote_IsGroundedAndLocalized(
        string language,
        int executedRequests,
        int observedSources,
        string expected)
    {
        var intake = Intake("Question documentaire") with
        {
            Language = language
        };

        var note = SourceBackedAgentV2Runner
            .BuildMechanicallyGroundedTerminalInsufficiencyNote(
                intake,
                executedRequests,
                observedSources);

        Assert.Equal(expected, note);
    }
}
