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
    public void ContentCardObservation_UsesWorkingEvidenceCapacityWithoutHidingReturnedPageItems()
    {
        var cards = Enumerable.Range(1, 5)
            .Select(index => new
            {
                docId = "doc-recettes",
                docName = "recettes.pdf",
                docPath = "Cuisine/recettes.pdf",
                categoryPath = "Cuisine",
                contentCardId = "card-" + index,
                title = "Recette " + index,
                pageStart = index,
                pageEnd = index,
                hasGroundedEvidence = true
            })
            .ToArray();
        var results = Results(
            "documents.content_cards",
            new
            {
                citable = true,
                total = 5,
                limit = 5,
                offset = 0,
                nextOffset = 5,
                items = cards
            });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Donne plusieurs recettes.");
        var options = Options() with
        {
            MaximumObservationItems = 2,
            MaximumWorkingEvidenceItems = 4,
            MaximumWorkingExcerptCharacters = 80
        };

        using var compact = JsonDocument.Parse(SourceBackedAgentObservationCompactor.Build(
            "documents_content_cards",
            results.Items,
            bundle,
            1,
            options));

        Assert.Equal(4, compact.RootElement.GetProperty("shownEvidenceCount").GetInt32());
        Assert.Equal(5, compact.RootElement.GetProperty("availableEvidenceCount").GetInt32());
        Assert.True(compact.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(4, compact.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.Equal(
            5,
            SourceBackedAgentObservationCompactor.ReadNextOffset(results.Items));
    }
}
