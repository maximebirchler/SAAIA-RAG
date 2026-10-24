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
    public void EvidenceBundle_ContextAnchorPreservesSelectedNavigationIdentityOnlyOnAnchorChunk()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.context",
            Result = JsonSerializer.SerializeToElement(new
            {
                requestedAnchorChunkId = "chunk-anchor",
                sourceAnchorChunkId = "chunk-anchor",
                sourceAnchorEvidenceId = "E7",
                sourceAnchorLabel = "Option Alpha",
                sourceAnchorId = "anchor-alpha",
                document = new
                {
                    docId = "doc-alpha",
                    docName = "catalogue.pdf",
                    docPath = "Knowledge/catalogue.pdf"
                },
                items = new object[]
                {
                    new
                    {
                        chunkId = "chunk-anchor",
                        pageStart = 11,
                        pageEnd = 11,
                        text = "INGREDIENTS : contenu exact de l'option alpha."
                    },
                    new
                    {
                        chunkId = "chunk-neighbor",
                        pageStart = 12,
                        pageEnd = 12,
                        text = "Passage voisin sans identite d'ancre."
                    }
                }
            })
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Question generique");

        var anchor = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "chunk-anchor");
        Assert.Equal(
            "Option Alpha",
            anchor.SelectionHints["sourceAnchorLabel"]);
        Assert.Equal(
            "E7",
            anchor.SelectionHints["sourceAnchorEvidenceId"]);
        Assert.Equal("anchor-alpha", anchor.AnchorId);
        Assert.Contains(
            "sourceAnchorEvidence:E7",
            anchor.Lineage);
        var neighbor = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "chunk-neighbor");
        Assert.Null(neighbor.AnchorId);
        Assert.DoesNotContain(
            "sourceAnchorLabel",
            neighbor.SelectionHints.Keys);
    }

    [Fact]
    public void EvidenceBundle_ContextAnchorUsesOneMechanicalFallbackWhenBackendChunkIdentityDiffers()
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.context",
            Result = JsonSerializer.SerializeToElement(new
            {
                requestedAnchorChunkId = "navigation-chunk",
                sourceAnchorChunkId = "navigation-chunk",
                sourceAnchorEvidenceId = "E12",
                sourceAnchorLabel = "Option Beta",
                document = new
                {
                    docId = "doc-beta",
                    docName = "catalogue.pdf",
                    docPath = "Knowledge/catalogue.pdf"
                },
                items = new object[]
                {
                    new
                    {
                        chunkId = "resolved-chunk-a",
                        pageStart = 21,
                        text = "Premier passage retourne par le backend."
                    },
                    new
                    {
                        chunkId = "resolved-chunk-b",
                        pageStart = 22,
                        text = "Second passage retourne par le backend."
                    }
                }
            })
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Question generique");

        var first = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "resolved-chunk-a");
        Assert.Equal(
            "Option Beta",
            first.SelectionHints["sourceAnchorLabel"]);
        Assert.Equal(
            "first_context_fallback",
            first.SelectionHints["sourceAnchorResolution"]);
        var second = Assert.Single(bundle.Items, static item =>
            item.ChunkId == "resolved-chunk-b");
        Assert.DoesNotContain(
            "sourceAnchorLabel",
            second.SelectionHints.Keys);
    }

    [Fact]
    public void EvidenceBundle_DeduplicatesSameStableContentCardAcrossInventoryAndSearch()
    {
        var inventory = ContentCardInventoryResult();
        var search = Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-recettes",
                        docName = "chefbot_livre_de_recettes_fr.pdf",
                        docPath = "Cuisine/chefbot_livre_de_recettes_fr.pdf",
                        categoryPath = "Cuisine",
                        pageStart = 21,
                        pageEnd = 21,
                        chunkId = "search-chunk-21",
                        excerpt = "Page contenant la recette.",
                        matchedContentCards = new[]
                        {
                            new
                            {
                                contentCardId = "card-feta",
                                title = "Sticks de feta",
                                kind = "page_embedded_title",
                                profileVersion = "foundation_v1",
                                hasGroundedEvidence = true,
                                pageStart = 21,
                                pageEnd = 21,
                                evidence = new
                                {
                                    facts = new[]
                                    {
                                        new { sourceText = "20 pieces" }
                                    }
                                }
                            }
                        }
                    }
                }
            });
        var cumulative = new ToolResults();
        cumulative.Items.AddRange(inventory.Items);
        cumulative.Items.AddRange(search.Items);

        var bundle = EvidenceBundleBuilder.FromToolResults(
            cumulative,
            "Donne une recette.",
            materializeMatchedContentCards: true);

        var card = Assert.Single(bundle.Items, static item =>
            item.SourceKind == "canonical_content_card");
        Assert.Equal("card-feta", card.ContentCardId);
        Assert.Null(card.ChunkId);
        Assert.Contains("20 pieces", card.Excerpt);
        Assert.Equal("page_embedded_title", card.SelectionHints["kind"]);
        Assert.Equal(
            "true",
            card.SelectionHints["hasGroundedEvidence"],
            ignoreCase: true);
    }
}
