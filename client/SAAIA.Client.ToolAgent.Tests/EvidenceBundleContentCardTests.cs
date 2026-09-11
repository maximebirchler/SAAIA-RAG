using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class EvidenceBundleContentCardTests
{
    [Fact]
    public void Content_card_inventory_preserves_canonical_hierarchy_and_exact_proof()
    {
        const string exactProof =
            "Verify the isolation valve and retain the signed inspection record.";
        var result = JsonSerializer.SerializeToElement(new
        {
            query = "isolation valve",
            items = new[]
            {
                new
                {
                    contentCardId = "card-1",
                    docId = "doc-1",
                    revisionId = "revision-1",
                    docPath = "Generic/Plant.pdf",
                    docName = "Plant.pdf",
                    title = "Isolation validation",
                    kind = "canonical_section_anchor",
                    headingPath =
                        "Plant operations > Isolation validation",
                    sectionLevel = 2,
                    sourceChunkIndex = 17,
                    pageStart = 4,
                    pageEnd = 4,
                    hasGroundedEvidence = true,
                    evidence = new
                    {
                        facts = new[]
                        {
                            new
                            {
                                kind = "canonical_section_context",
                                label = "section_excerpt",
                                sourceText = exactProof,
                                pageStart = 4,
                                pageEnd = 4
                            }
                        }
                    }
                }
            }
        });
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.content_cards",
            Result = result
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            "Find the documented validation procedure.");

        var item = Assert.Single(bundle.Items);
        Assert.Equal(
            "canonical_section_anchor",
            item.SelectionHints["kind"]);
        Assert.Equal(
            "Plant operations > Isolation validation",
            item.SelectionHints["headingPath"]);
        Assert.Equal("2", item.SelectionHints["sectionLevel"]);
        Assert.Equal("17", item.SelectionHints["sourceChunkIndex"]);
        Assert.Equal(
            "Isolation validation",
            item.SelectionHints["sourceAnchorLabel"]);
        Assert.Equal(
            "Isolation validation. " + exactProof,
            item.Excerpt);
    }
}
