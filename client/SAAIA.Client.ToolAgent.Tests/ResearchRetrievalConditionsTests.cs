using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ResearchRetrievalConditionsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Research_decision_sees_observed_retriever_degradation_without_inventing_failures(bool compact, bool healthyMutation)
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CanonicalDegradedSearchContract.json")))!;
        if (healthyMutation)
        {
            // A declared contract mutation, not an observed successful vector run.
            body["metrics"]!.AsObject().Remove("degradedRetrievers");
            body["metrics"]!.AsObject().Remove("degradedRetrieverErrors");
        }
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(body.ToJsonString(), sourceBackedCanonical: true);
        var result = new ToolResults();
        result.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        const string question = "Read the measurements and their validity conditions.";
        var bundle = EvidenceBundleBuilder.FromToolResults(result, question);
        Assert.Empty(bundle.Items);
        var attempt = Assert.Single(bundle.RetrievalAttempts);
        Assert.Equal(healthyMutation ? "completed" : "degraded", attempt.Outcome);
        var messages = (IReadOnlyList<SourceBackedAgentMessage>)typeof(SourceBackedAgentV2Runner)
            .GetMethod("BuildResearchTransitionDecisionMessages", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new SourceBackedIntake(question, "rag.answer", [], [], false, "en"),
                "MODE_PREUVES_ATOMIQUES: content_claim", "documented fact", "", bundle,
                new HashSet<string>(), Array.Empty<RetrievalRequest>(), Array.Empty<string>(), 0, 3, null, 20, compact])!;
        var prompt = string.Join("\n", messages.Select(message => message.Content));
        if (healthyMutation)
        {
            Assert.Empty(attempt.DegradedRetrievers);
            Assert.DoesNotContain("dense_qdrant", prompt);
            Assert.DoesNotContain("OBSERVED RETRIEVAL CONDITIONS", prompt);
        }
        else
        {
            Assert.Contains("dense_qdrant", attempt.DegradedRetrievers);
            Assert.Contains("dense_qdrant", prompt); // Previously retained in the bundle, lost at the LLM boundary.
            Assert.Contains("degraded", prompt);
            Assert.Contains(Assert.Single(attempt.Queries), prompt);
            Assert.Contains("evidence_items=0", prompt);
            Assert.Contains("does not establish absence", prompt);
        }
        Assert.Empty(bundle.Items); // Technical observations never become documentary evidence.
    }
}
