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
    public void StructuredCandidateWriter_ReceivesLlmAuthoredColumnCompatibility()
    {
        var rawBundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance A", 1),
                new MealCard("Instance sans role", 2)
            }),
            "Construis une grille generique.");
        var annotatedBundle = rawBundle with
        {
            Items = rawBundle.Items
                .Select((item, index) => item with
                {
                    SelectionHints = new Dictionary<string, string>(
                        item.SelectionHints,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["semanticDisplayValue"] = index == 0
                            ? "Instance A"
                            : "Instance sans role",
                        ["semanticCompatibleColumnLabels"] =
                            JsonSerializer.Serialize(index == 0
                                ? new[] { "Option A" }
                                : Array.Empty<string>())
                    }
                })
                .ToArray()
        };
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildStructuredCandidateWriterMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var messages = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentMessage>>(method!.Invoke(
            null,
            new object?[]
            {
                Intake("Construis une grille generique."),
                annotatedBundle,
                annotatedBundle.Items.Select(static item => item.EvidenceId).ToArray(),
                "Periode",
                new[] { "Premiere" },
                new[] { "Option A", "Option B" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Option A"] = "Premiere famille.",
                    ["Option B"] = "Deuxieme famille."
                },
                null
            })!);
        var prompt = string.Join(
            Environment.NewLine,
            messages.Select(static message => message.Content));
        Assert.Contains(
            "colonnes compatibles decidees par le LLM: Option A",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "colonnes compatibles decidees par le LLM: AUCUNE",
            prompt,
            StringComparison.Ordinal);
    }
}
