using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalServerIdentityContractTests
{
    [Theory]
    [InlineData("interrupted", 0, "degraded")]
    [InlineData("stable", 2, "completed")]
    public void Actual_server_revision_change_remains_visible_in_the_client_bundle(string caseName, int expectedItems, string expectedOutcome)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalServerIdentityRaceContract.json")));
        Assert.True(fixture.RootElement.GetProperty("postgresVerified").GetBoolean());
        Assert.True(fixture.RootElement.GetProperty("reindexDuringRerank").GetBoolean());
        var response = fixture.RootElement.GetProperty(caseName);
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(response.GetRawText(), sourceBackedCanonical: true);
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        var bundle = EvidenceBundleBuilder.FromToolResults(tools, "Which procedure applies?");
        Assert.Equal(expectedItems, bundle.Items.Count);
        var attempt = Assert.Single(bundle.RetrievalAttempts);
        Assert.Equal(expectedOutcome, attempt.Outcome);
        var query = Assert.IsType<string>(response.GetProperty("query").GetString());
        Assert.Equal(new[] { query }, attempt.Queries);
        if (caseName == "interrupted")
            Assert.Contains("source_identity", attempt.DegradedRetrievers);
        else
        {
            Assert.Empty(attempt.DegradedRetrievers);
            Assert.All(bundle.Items, item => Assert.Contains("second extraction", item.Excerpt));
        }
    }
}
