using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SemanticReviewCompleteInputContractTests
{
    [Theory]
    [InlineData("question", false, false)]
    [InlineData("question", true, false)]
    [InlineData("question", true, true)]
    [InlineData("constraints", false, false)]
    [InlineData("constraints", true, false)]
    [InlineData("constraints", true, true)]
    [InlineData("plan", false, false)]
    [InlineData("plan", true, false)]
    [InlineData("plan", true, true)]
    [InlineData("draft", false, false)]
    [InlineData("draft", true, false)]
    [InlineData("draft", true, true)]
    [InlineData("citations", false, false)]
    [InlineData("citations", true, false)]
    [InlineData("citations", true, true)]
    public void Review_context_preserves_every_authoritative_input_and_cited_passage(
        string component, bool compact, bool emergency)
    {
        const string tail = "CRITICAL TAIL: uniquement les unités de la série Zêta.";
        var longValue = new string('x', 5800) + "\n" + tail;
        var intake = new SourceBackedIntake(component == "question" ? longValue : "Présente le module Atlas.",
            "rag.answer", component == "constraints" ? new[] { longValue } : Array.Empty<string>(), [], false, "fr");
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = JsonSerializer.SerializeToElement(new
        {
            hits = new[] { new { docId = "atlas", docPath = "Lab/Atlas.pdf", revisionId = "rev-atlas",
                sourceHash = new string('a', 64), chunkId = "atlas-chunk", pageStart = 1, pageEnd = 1,
                excerpt = "Le module Atlas est portable." } }
        }) });
        var bundle = EvidenceBundleBuilder.FromToolResults(results, intake.UserQuestion);
        if (component == "citations")
        {
            var item = Assert.Single(bundle.Items);
            bundle = bundle with { Items = Enumerable.Range(1, 43).Select(i => item with
            {
                EvidenceId = "E" + i, ChunkId = "atlas-chunk-" + i,
                Excerpt = i == 43 ? tail : "Passage documentaire " + i
            }).ToArray() };
        }
        var answer = component == "draft" ? longValue + " [E1]"
            : "Le module Atlas est documenté " + string.Concat(bundle.Items.Select(item => "[" + item.EvidenceId + "]"));
        var draft = new WriterDraft(answer, bundle.Items.Select(item => item.EvidenceId).ToArray());
        var plan = component == "plan" ? longValue : "PREUVES_ATOMIQUES: 1 module\nMODE_PREUVES_ATOMIQUES: named_item";
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("BuildSemanticJudgeContext", BindingFlags.Static | BindingFlags.NonPublic)!;
        var context = (string)method.Invoke(null, new object[] { intake, plan, draft, bundle,
            bundle.Items.Select(item => item.EvidenceId).ToArray(), compact, emergency, true })!;

        Assert.Contains(tail, context, StringComparison.Ordinal);
        if (component == "citations")
        {
            var citedContext = context.Split("ALTERNATIVES DEJA OBSERVEES", StringSplitOptions.None)[0];
            Assert.Contains("- E43 |", citedContext, StringComparison.Ordinal);
            Assert.Contains(tail, citedContext, StringComparison.Ordinal);
        }
        else
            Assert.Contains(longValue, context, StringComparison.Ordinal);
    }
}
