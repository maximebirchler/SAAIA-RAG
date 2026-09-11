using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StructuredWriterFidelityContractTests
{
    [Theory]
    [InlineData("named_item")]
    [InlineData("content_claim")]
    public void Structured_transport_preserves_shared_fidelity_policy_without_inline_citation_instructions(string mode)
    {
        var intake = new SourceBackedIntake("Présente le module Atlas.", "rag.answer", [], [], false, "fr");
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                hits = new[] { new { docId = "atlas", docPath = "Lab/Atlas.pdf", revisionId = "rev-atlas",
                    sourceHash = new string('a', 64), chunkId = "atlas-chunk", pageStart = 1, pageEnd = 1,
                    excerpt = "Le module Atlas est portable. Installer le filtre après le refroidissement." } }
            })
        });
        var bundle = EvidenceBundleBuilder.FromToolResults(results, intake.UserQuestion);
        var plainArguments = new object?[] { intake, bundle, new[] { "E1" }, mode, null, null, null };
        var flatArguments = new object?[] { intake, bundle, new[] { "E1" }, mode, null, null, null, null };
        var runner = typeof(SourceBackedAgentV2Runner);
        var flat = (IReadOnlyList<SourceBackedAgentMessage>)runner.GetMethod("BuildStructuredFlatWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, flatArguments)!;
        var plain = (IReadOnlyList<SourceBackedAgentMessage>)runner.GetMethod("BuildSelectedEvidenceWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, plainArguments)!;

        Assert.Equal(plain[1].Content, flat[1].Content);
        var plainPolicy = string.Join(' ', (plain[0].Content ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var flatPolicy = string.Join(' ', (flat[0].Content ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var fragment in new[] { "actions ordonnees", "dans l'etape courante", "temperatures et", "attentes", "detail suppose" })
        {
            Assert.Contains(fragment, plainPolicy, StringComparison.Ordinal);
            Assert.Contains(fragment, flatPolicy, StringComparison.Ordinal);
        }
        Assert.Contains("seulement dans evidenceIds", flat[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("[E#]", flat[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("unique marqueur", flat[0].Content, StringComparison.Ordinal);
        if (mode == "named_item")
        {
            Assert.Contains("ne le renomme pas", flat[0].Content, StringComparison.Ordinal);
            Assert.Contains("Adapte le detail a la demande", flat[0].Content, StringComparison.Ordinal);
            Assert.Contains("toutes les etapes essentielles visibles", flat[0].Content, StringComparison.Ordinal);
        }
    }
}
