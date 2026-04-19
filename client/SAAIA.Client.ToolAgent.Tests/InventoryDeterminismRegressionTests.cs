using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using SAAIA.Client.WinUI.Localization;
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
        Assert.Equal("Documents present on the server:\n1. [[open|General/test.pdf|1|test]]", chunks[0]);
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
              "categoryPath": "Ghost"
            }
          ]
        }
        """);

        var canonical = InvokeCreateCanonicalDocumentsListJson(doc.RootElement);
        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", canonical, "fr");

        Assert.DoesNotContain("[[open|Ghost/", rendered);
        Assert.Contains($"[[open|{relativePath}|1|", rendered);
        Assert.Contains(fileName, rendered);
    }

    [Fact]
    public void Shortcut_canonicalization_rewrites_ghost_document_name_to_the_resolved_file_name()
    {
        const string relativePath = "General/Siemens S7 Manual.pdf";

        using var inventoryScope = DocumentInventoryTestScope.WithSingleDocument(relativePath);
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "General/Siemens S7 Manual.pdf",
              "docName": "siemens.pdf",
              "categoryPath": "General"
            }
          ]
        }
        """);

        var canonical = InvokeCreateCanonicalDocumentsListJson(doc.RootElement, searchQuery: "siemens");
        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", canonical, "fr");

        Assert.Contains("Siemens S7 Manual.pdf", rendered);
        Assert.DoesNotContain("siemens.pdf (General)", rendered);
    }

    [Fact]
    public void Exact_pdf_search_filters_out_stale_backend_document_names_after_sanitization()
    {
        const string relativePath = "General/Siemens S7 Manual.pdf";

        using var inventoryScope = DocumentInventoryTestScope.WithSingleDocument(relativePath);
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "General/Siemens S7 Manual.pdf",
              "docName": "siemens.pdf",
              "categoryPath": "General"
            }
          ]
        }
        """);

        var canonical = InvokeCreateCanonicalDocumentsListJson(doc.RootElement, searchQuery: "siemens.pdf");
        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", canonical, "fr");

        Assert.Equal(LocalizedStrings.NoDocumentsFound("fr"), rendered);
    }

    [Fact]
    public void Exact_pdf_search_keeps_the_exact_match()
    {
        const string relativePath = "General/siemens.pdf";

        using var inventoryScope = DocumentInventoryTestScope.WithSingleDocument(relativePath);
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            {
              "docPath": "General/siemens.pdf",
              "docName": "siemens.pdf",
              "categoryPath": "General"
            }
          ]
        }
        """);

        var canonical = InvokeCreateCanonicalDocumentsListJson(doc.RootElement, searchQuery: "siemens.pdf");
        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", canonical, "fr");

        Assert.Contains("[[open|General/siemens.pdf|1|siemens.pdf (General)]]", rendered);
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

    [Fact]
    public void Diagnostic_performance_render_is_human_readable_and_does_not_leak_raw_json()
    {
        using var doc = JsonDocument.Parse("""
        {
          "profile": "cdc-v3-m1lite-m3-m6",
          "schemaVersion": 1,
          "cdcAlignment": "v3.0",
          "routerMs": 12,
          "toolsMs": 34,
          "writerMs": 7,
          "totalMs": 53,
          "workspace": {
            "catalogCategoriesCount": 2,
            "knownDocumentsCount": 3,
            "hasCapabilitiesSnapshot": true
          },
          "session": {
            "hasFocusedDocument": false,
            "lastListedDocumentsCount": 1,
            "hasResolvedCategory": true,
            "hasPendingClarification": false
          },
          "execution": {
            "mode": "auto",
            "hasRouterIntent": true,
            "toolNamesCount": 2,
            "hasAdminOperation": false
          },
          "persistence": {
            "language": true,
            "style": true,
            "mode": false,
            "focusedDocument": false,
            "resolvedCategory": false
          },
          "resetPolicy": {
            "preservesM1Lite": true,
            "preservesPreferences": true,
            "clearsM3": true,
            "clearsM6": true,
            "resetsModeToAuto": true
          }
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("diagnostic_performance", doc.RootElement, "en");

        Assert.Contains("Memory and performance diagnostics:", rendered);
        Assert.Contains("router 12 ms", rendered);
        Assert.Contains("Workspace: 2 canonical category(ies), 3 known document(s)", rendered);
        Assert.Contains("Persistence: language yes, style yes, mode no", rendered);
        Assert.DoesNotContain("\"workspace\"", rendered);
        Assert.DoesNotContain("\"memorySummary\"", rendered);
    }

    [Fact]
    public void Diagnostic_performance_tool_results_can_generate_inventory_rendered_payload()
    {
        using var doc = JsonDocument.Parse("""
        {
          "routerMs": 5,
          "toolsMs": 18,
          "writerMs": 3,
          "totalMs": 26,
          "memorySummary": {
            "profile": "cdc-v3-m1lite-m3-m6",
            "schemaVersion": 1,
            "cdcAlignment": "v3.0",
            "workspace": {
              "catalogCategoriesCount": 1,
              "knownDocumentsCount": 1,
              "hasCapabilitiesSnapshot": true
            },
            "session": {
              "hasFocusedDocument": false,
              "lastListedDocumentsCount": 0,
              "hasResolvedCategory": false,
              "hasPendingClarification": false
            },
            "execution": {
              "mode": "auto",
              "hasRouterIntent": true,
              "toolNamesCount": 1,
              "hasAdminOperation": false
            },
            "persistence": {
              "language": true,
              "style": true,
              "mode": false,
              "focusedDocument": false,
              "resolvedCategory": false
            },
            "resetPolicy": {
              "preservesM1Lite": true,
              "preservesPreferences": true,
              "clearsM3": true,
              "clearsM6": true,
              "resetsModeToAuto": true
            }
          }
        }
        """);

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "diagnostic.performance",
            Result = doc.RootElement.Clone(),
            DurationMs = 18
        });

        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, new ToolMemory());
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryBuildInventoryRenderedItem", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var item = Assert.IsType<ToolResults.Item>(method!.Invoke(sut, new object?[] { toolResults, "en", CancellationToken.None }));
        Assert.Equal("inventory.rendered", item.ToolName);
        Assert.Equal("diagnostic_performance", item.Result.GetProperty("kind").GetString());

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData(
            item.Result.GetProperty("kind").GetString() ?? string.Empty,
            item.Result.GetProperty("data"),
            "en");

        Assert.Contains("Memory and performance diagnostics:", rendered);
        Assert.Contains("total 26 ms", rendered);
    }

    private static JsonElement InvokeCreateCanonicalDocumentsListJson(JsonElement root, string? scopePath = null, string? searchQuery = null)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        var method = typeof(ToolAgentOrchestrator).GetMethod("CreateCanonicalDocumentsListJsonCore", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (JsonElement)method!.Invoke(sut, new object?[] { root, scopePath, searchQuery })!;
    }

    private sealed class DocumentInventoryTestScope : IDisposable
    {
        private readonly string _tempRoot;
        private readonly object? _previousEntries;
        private readonly DateTimeOffset _previousLastScan;
        private readonly string? _previousDocumentsRoot;
        private readonly string? _previousInstallRoot;
        private readonly FieldInfo _entriesField;
        private readonly FieldInfo _lastScanField;

        private DocumentInventoryTestScope(string relativePath)
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "SAAIA.Client.ToolAgent.Tests", Guid.NewGuid().ToString("N"));
            var documentsRoot = Path.Combine(_tempRoot, "documents");
            var fullPath = Path.Combine(documentsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, Array.Empty<byte>());

            _previousDocumentsRoot = Environment.GetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT");
            _previousInstallRoot = Environment.GetEnvironmentVariable("SAAIA_INSTALL_ROOT");
            Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", documentsRoot);
            Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", null);

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
            Environment.SetEnvironmentVariable("SAAIA_DOCUMENTS_ROOT", _previousDocumentsRoot);
            Environment.SetEnvironmentVariable("SAAIA_INSTALL_ROOT", _previousInstallRoot);

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
