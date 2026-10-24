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
    public void StructuredColumnCoverage_UsesOnlyLlmAuthoredCompatibilityForMatching()
    {
        var rawBundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(new[]
            {
                new MealCard("Instance 1", 1),
                new MealCard("Instance 2", 2),
                new MealCard("Instance 3", 3),
                new MealCard("Instance 4", 4)
            }),
            "Construis une grille generique.");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "EvaluateStructuredColumnCoverage",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var evidenceIds = rawBundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();

        object Evaluate(params string[][] compatibleColumns)
        {
            var annotatedBundle = rawBundle with
            {
                Items = rawBundle.Items
                    .Select((item, index) => item with
                    {
                        SelectionHints = new Dictionary<string, string>(
                            item.SelectionHints,
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["semanticDisplayValue"] = "Instance " + (index + 1),
                            ["semanticCompatibleColumnLabels"] =
                                JsonSerializer.Serialize(compatibleColumns[index])
                        }
                    })
                    .ToArray()
            };
            return method!.Invoke(
                null,
                new object?[]
                {
                    annotatedBundle,
                    evidenceIds,
                    evidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new[] { "Ligne 1", "Ligne 2" },
                    new[] { "Option A", "Option B" }
                })!;
        }

        var insufficient = Evaluate(
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option A" });
        Assert.True(ReadPrivateProperty<bool>(insufficient, "Applied"));
        Assert.False(ReadPrivateProperty<bool>(
            insufficient,
            "HasRequiredCoverage"));
        Assert.Equal(2, ReadPrivateProperty<int>(
            insufficient,
            "MatchedCellCount"));

        var complete = Evaluate(
            new[] { "Option A" },
            new[] { "Option A" },
            new[] { "Option B" },
            new[] { "Option B" });
        Assert.True(ReadPrivateProperty<bool>(complete, "Applied"));
        Assert.True(ReadPrivateProperty<bool>(
            complete,
            "HasRequiredCoverage"));
        Assert.Equal(4, ReadPrivateProperty<int>(complete, "MatchedCellCount"));
    }
}
