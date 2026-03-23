using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class InventoryDeterminismRegressionTests
{
    [Fact]
    public void Inventory_writer_is_bypassed_when_authoritative_inventory_render_is_available()
    {
        var shouldBypass = ToolAgentOrchestrator.ShouldBypassWriterForDeterministicInventory(
            "inventory.list",
            new[] { "inventory.rendered", "documents.list", "diagnostic.performance" },
            "Documents présents sur le serveur :\n1. [[open|General/test.pdf|1|test.pdf (General)]]");

        Assert.True(shouldBypass);
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

    [Fact]
    public void Shortcut_canonicalization_replaces_a_ghost_doc_path_with_the_real_local_relative_path()
    {
        const string fileName = "Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf";
        const string relativePath = "General/Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf";

        using var inventoryScope = DocumentInventoryTestScope.WithSingleDocument(relativePath);
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "Ghost/Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf",
              "docName": "Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf",
              "categoryPath": "General"
            }
          ]
        }
        """);

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        var method = typeof(ToolAgentOrchestrator).GetMethod("CreateCanonicalDocumentsListJson", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var canonical = (JsonElement)method!.Invoke(sut, new object[] { doc.RootElement })!;
        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", canonical, "fr");

        Assert.DoesNotContain("[[open|Ghost/", rendered);
        Assert.Contains($"[[open|{relativePath}|1|", rendered);
        Assert.Contains(fileName, rendered);
    }

    [Fact]
    public void Scoped_stats_replay_uses_the_category_title_instead_of_catalog_title()
    {
        using var doc = JsonDocument.Parse("""
        {
          "scopePath": "ATEX",
          "totalDocuments": 1,
          "totalNonEmptyFolders": 1,
          "foldersByDepth": [
            { "depth": 1, "folderCount": 1 }
          ],
          "rootFolders": []
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("stats", doc.RootElement, "fr");

        Assert.Contains("Statistiques de la catégorie ATEX :", rendered);
        Assert.DoesNotContain("Statistiques du catalogue :", rendered);
    }


    private sealed class DocumentInventoryTestScope : IDisposable
    {
        private readonly string _tempRoot;
        private readonly object? _previousEntries;
        private readonly DateTimeOffset _previousLastScan;
        private readonly FieldInfo _entriesField;
        private readonly FieldInfo _lastScanField;

        private DocumentInventoryTestScope(string relativePath)
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "SAAIA.Client.ToolAgent.Tests", Guid.NewGuid().ToString("N"));
            var fullPath = Path.Combine(_tempRoot, "documents", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, Array.Empty<byte>());

            var inventoryType = typeof(DocumentInventory);
            _entriesField = inventoryType.GetField("_entries", BindingFlags.NonPublic | BindingFlags.Static)!;
            _lastScanField = inventoryType.GetField("_lastScan", BindingFlags.NonPublic | BindingFlags.Static)!;

            _previousEntries = _entriesField.GetValue(null);
            _previousLastScan = (DateTimeOffset)(_lastScanField.GetValue(null) ?? DateTimeOffset.MinValue);

            var seededEntries = new List<DocumentInventory.Entry>
            {
                new(fullPath, relativePath, Path.GetFileName(fullPath).ToLowerInvariant())
            };

            _entriesField.SetValue(null, seededEntries);
            _lastScanField.SetValue(null, DateTimeOffset.UtcNow);
        }

        public static DocumentInventoryTestScope WithSingleDocument(string relativePath)
            => new(relativePath);

        public void Dispose()
        {
            _entriesField.SetValue(null, _previousEntries);
            _lastScanField.SetValue(null, _previousLastScan);

            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // best effort cleanup for the test sandbox
            }
        }
    }

}
