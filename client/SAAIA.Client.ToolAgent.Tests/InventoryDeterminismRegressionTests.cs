using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class InventoryDeterminismRegressionTests
{
    [Fact]
    public void Inventory_writer_is_not_bypassed_even_when_authoritative_inventory_render_is_available()
    {
        var shouldBypass = ToolAgentOrchestrator.ShouldBypassWriterForDeterministicInventory(
            "inventory.list",
            new[] { "inventory.rendered", "documents.list", "diagnostic.performance" },
            "Documents présents sur le serveur :\n1. [[open|General/test.pdf|1|test.pdf (General)]]");

        Assert.False(shouldBypass);
    }

    [Fact]
    public void Inventory_replay_keeps_the_localized_header_but_preserves_document_labels()
    {
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "General/CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
              "docName": "CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf",
              "categoryPath": "ATEX"
            }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", doc.RootElement, "it");

        Assert.Contains("Documenti presenti sul server:", rendered);
        Assert.Contains("CEN TR 15281 2006 Guidance on inerting for the prevention of explosion.pdf", rendered);
        Assert.DoesNotContain("I'm sorry", rendered);
    }

    [Fact]
    public void Deterministic_delivery_is_single_shot_to_avoid_duplicate_chunks_on_replay()
    {
        var chunks = ToolAgentOrchestrator.SplitDeterministicTextForDelivery(
            "Documents present on the server:\n1. [[open|General/test.pdf|1|test]]");

        Assert.Single(chunks);
        Assert.Equal("Documents present on the server:\n1. [[open|General/test.pdf|1|test]]", chunks.Single());
    }

    [Fact]
    public void Inventory_render_uses_requested_language_header_and_only_one_header()
    {
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "General/MettlerToledo_IND570.pdf",
              "docName": "MettlerToledo_IND570.pdf",
              "categoryPath": "Programmation"
            }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", doc.RootElement, "en");

        Assert.Equal(1, rendered.Split("Documents present on the server:").Length - 1);
        Assert.DoesNotContain("Documents présents sur le serveur :", rendered);
    }
}
