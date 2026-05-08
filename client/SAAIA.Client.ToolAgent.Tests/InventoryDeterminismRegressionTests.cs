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
          "cdcAlignment": "v3.1",
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
            "cdcAlignment": "v3.1",
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

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
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

    [Fact]
    public void Admin_summary_missing_catalog_shape_generates_governance_inventory_rendered_payload()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "docId": "doc-1",
              "docPath": "Neutral/governance.pdf",
              "canonicalName": "governance.pdf",
              "categoryCanonicalName": "Neutral",
              "summaryState": "stale",
              "hasActiveSummaryJob": true,
              "activeSummaryJobStatus": "running",
              "capabilityBReadyToEnqueue": true,
              "capabilityBRecommendedAction": "enqueue_profile_refresh",
              "capabilityBPriorityScore": 12.5,
              "capabilityBProfileState": "missing",
              "capabilityBHasBackofficeProfile": false,
              "capabilityBReasons": ["profile_missing"]
            }
          ],
          "totals": {
            "total": 1,
            "missingStored": 0,
            "staleStored": 1,
            "profileMissing": 1
          },
          "scopePath": "Neutral",
          "level": "medium",
          "nextLink": null
        }
        """);

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "admin.summary.missing",
            Result = doc.RootElement.Clone(),
            DurationMs = 12
        });

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryBuildInventoryRenderedItem", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var item = Assert.IsType<ToolResults.Item>(method!.Invoke(sut, new object?[] { toolResults, "en", CancellationToken.None }));
        Assert.Equal("inventory.rendered", item.ToolName);
        Assert.Equal("summary_status_list", item.Result.GetProperty("kind").GetString());

        var data = item.Result.GetProperty("data");
        Assert.Equal(1, data.GetProperty("profileMissing").GetInt32());
        Assert.True(data.GetProperty("endOfList").ValueKind is JsonValueKind.Null or JsonValueKind.False);
        Assert.Equal("Neutral", data.GetProperty("scopePath").GetString());
        var renderedItem = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("governance.pdf", renderedItem.GetProperty("docName").GetString());
        Assert.Equal("Neutral", renderedItem.GetProperty("category").GetString());
        Assert.True(renderedItem.GetProperty("hasActiveSummaryJob").GetBoolean());
        Assert.Equal("enqueue_profile_refresh", renderedItem.GetProperty("capabilityBRecommendedAction").GetString());

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_list", data, "en");
        Assert.Contains("[[open|Neutral/governance.pdf|1|governance.pdf]]", rendered);
        Assert.Contains("[missing LLM profile]", rendered);
        Assert.Contains("[active job running]", rendered);
        Assert.Contains("[Capability B enqueue_profile_refresh]", rendered);
    }

    [Fact]
    public void Extraction_quality_render_is_human_readable_and_does_not_leak_raw_json()
    {
        using var doc = JsonDocument.Parse("""
        {
          "scopePath": "Neutral",
          "summary": {
            "totalDocuments": 2,
            "okDocuments": 1,
            "lowTextDocuments": 1,
            "emptyTextDocuments": 0,
            "unknownDocuments": 0,
            "ocrRecommendedDocuments": 1,
            "ocrAppliedDocuments": 1,
            "manualReviewRecommendedDocuments": 1,
            "pageWarningPages": 2
          },
          "items": [
            {
              "docId": "doc-1",
              "docPath": "Neutral/manual.pdf",
              "qualityStatus": "low_text",
              "extractionConfidence": 0.72,
              "manualReviewRecommended": true,
              "extractionSource": "pdf_text_plus_image_ocr",
              "ocrApplied": true,
              "ocrRecommended": false,
              "ocrLanguages": "fra+eng",
              "ocrDurationMs": 1530,
              "nativeTextStatus": "low_text",
              "textStatus": "low_text",
              "pageCount": 10,
              "textPageCount": 8,
              "totalWordCount": 850,
              "averageWordsPerPage": 85.0,
              "textPageRatio": 0.8,
              "pageWarningCount": 2,
              "pageReviewRecommendedCount": 1,
              "signals": ["low_text_density"]
            }
          ],
          "categories": [
            {
              "categoryPath": "Neutral/Sub",
              "totalDocuments": 2,
              "lowTextDocuments": 1,
              "emptyTextDocuments": 0,
              "ocrRecommendedDocuments": 1,
              "manualReviewRecommendedDocuments": 1,
              "pageWarningPages": 2
            }
          ],
          "limit": 20
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("extraction_quality", doc.RootElement, "en");

        Assert.Contains("Extraction quality diagnostics:", rendered);
        Assert.Contains("Scope: Neutral", rendered);
        Assert.Contains("2 document(s)", rendered);
        Assert.Contains("[[open|Neutral/manual.pdf|1|manual.pdf]]", rendered);
        Assert.Contains("low text", rendered);
        Assert.Contains("confidence 72 %", rendered);
        Assert.Contains("Categories to watch:", rendered);
        Assert.Contains("Neutral/Sub", rendered);
        Assert.Contains("OCR languages fra+eng", rendered);
        Assert.Contains("OCR duration 1.5s", rendered);
        Assert.Contains("native text low_text", rendered);
        Assert.Contains("signals low_text_density", rendered);
        Assert.Contains("850 words", rendered);
        Assert.Contains("OCR applied", rendered);
        Assert.Contains("manual review recommended", rendered);
        Assert.DoesNotContain("\"summary\"", rendered);
        Assert.DoesNotContain("\"qualityStatus\"", rendered);
    }

    [Fact]
    public void Extraction_quality_render_includes_non_indexable_ocr_failure_metadata()
    {
        using var doc = JsonDocument.Parse("""
        {
          "scopePath": "OCR",
          "summary": {
            "totalDocuments": 1,
            "okDocuments": 0,
            "lowTextDocuments": 0,
            "emptyTextDocuments": 1,
            "unknownDocuments": 0,
            "ocrRecommendedDocuments": 1,
            "ocrAppliedDocuments": 0,
            "manualReviewRecommendedDocuments": 1,
            "pageWarningPages": 0
          },
          "items": [
            {
              "docId": "doc-ocr",
              "docPath": "OCR/scanned.pdf",
              "documentStatus": "error",
              "processingRunStatus": "failed",
              "documentIndexable": false,
              "failureReason": "ocr_required_but_disabled",
              "ocrFailureReason": "ocr_required_but_disabled",
              "qualityStatus": "ocr_required_but_disabled",
              "extractionConfidence": 0.10,
              "manualReviewRecommended": true,
              "textStatus": "empty_text",
              "ocrRecommended": true,
              "pageCount": 1,
              "textPageCount": 0,
              "signals": ["ocr_recommended"]
            }
          ],
          "limit": 20
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("extraction_quality", doc.RootElement, "en");

        Assert.Contains("[[open|OCR/scanned.pdf|1|scanned.pdf]]", rendered);
        Assert.Contains("OCR required but disabled", rendered);
        Assert.Contains("document error", rendered);
        Assert.Contains("processing failed", rendered);
        Assert.Contains("document not indexable", rendered);
        Assert.Contains("reason OCR required but disabled", rendered);
        Assert.Contains("OCR failure OCR required but disabled", rendered);
        Assert.DoesNotContain("ocr_required_but_disabled", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Extraction_pages_render_is_human_readable_and_links_to_pages()
    {
        using var doc = JsonDocument.Parse("""
        {
          "docId": "doc-1",
          "docPath": "Neutral/manual.pdf",
          "summary": {
            "pageCount": 6,
            "manualReviewRecommendedPages": 1,
            "probableOcrNoisePages": 1,
            "emptyTextPages": 0,
            "lowTextPages": 1,
            "imagePages": 2
          },
          "pages": [
            {
              "pageNumber": 4,
              "qualityStatus": "page_ok_with_images",
              "extractionConfidence": 0.81,
              "manualReviewRecommended": true,
              "wordCount": 120,
              "charCount": 700,
              "imageCount": 2,
              "chunkCount": 3,
              "textStatus": "ok",
              "ocrCandidate": true,
              "imageOcrStatus": "applied",
              "imageOcrReason": "novel_text",
              "imageOcrExitCode": 0,
              "imageOcrTimedOut": false,
              "suspiciousUnitCount": 1,
              "signals": ["page_contains_images"],
              "unitPreviews": ["short preview from extracted unit"]
            }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("extraction_pages", doc.RootElement, "en");

        Assert.Contains("Page diagnostics for manual.pdf:", rendered);
        Assert.Contains("6 page(s)", rendered);
        Assert.Contains("[[open|Neutral/manual.pdf|4|manual.pdf p.4]]", rendered);
        Assert.Contains("OK with images", rendered);
        Assert.Contains("120 words", rendered);
        Assert.Contains("OCR candidate", rendered);
        Assert.Contains("OCR reason novel_text", rendered);
        Assert.Contains("OCR code 0", rendered);
        Assert.Contains("signals page_contains_images", rendered);
        Assert.Contains("preview short preview from extracted unit", rendered);
        Assert.Contains("manual review recommended", rendered);
        Assert.DoesNotContain("\"pages\"", rendered);
        Assert.DoesNotContain("\"pageNumber\"", rendered);
    }

    [Fact]
    public void Extraction_pages_render_includes_doc_level_failure_when_no_pages_exist()
    {
        using var doc = JsonDocument.Parse("""
        {
          "docId": "doc-ocr",
          "docPath": "OCR/scanned.pdf",
          "documentStatus": "error",
          "processingRunStatus": "failed",
          "documentIndexable": false,
          "failureReason": "ocr_required_but_disabled",
          "ocrFailureReason": "ocr_required_but_disabled",
          "ocrAppliedReason": "ocr_disabled",
          "ocrMode": "ocr_disabled",
          "summary": {
            "pageCount": 1,
            "manualReviewRecommendedPages": 1,
            "probableOcrNoisePages": 0,
            "emptyTextPages": 1,
            "lowTextPages": 0,
            "imagePages": 0
          },
          "pages": []
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("extraction_pages", doc.RootElement, "en");

        Assert.Contains("Page diagnostics for scanned.pdf:", rendered);
        Assert.Contains("document error", rendered);
        Assert.Contains("processing failed", rendered);
        Assert.Contains("document not indexable", rendered);
        Assert.Contains("reason OCR required but disabled", rendered);
        Assert.Contains("OCR failure OCR required but disabled", rendered);
        Assert.Contains("OCR decision OCR disabled", rendered);
        Assert.Contains("OCR mode OCR disabled", rendered);
        Assert.Contains("No diagnosable page.", rendered);
        Assert.DoesNotContain("ocr_required_but_disabled", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ocr_disabled", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Extraction_diagnostic_tool_results_can_generate_inventory_rendered_payloads()
    {
        using var qualityDoc = JsonDocument.Parse("""
        {
          "scopePath": "",
          "summary": { "totalDocuments": 1, "okDocuments": 1 },
          "items": [
            {
              "docPath": "Neutral/quality.pdf",
              "qualityStatus": "ok",
              "extractionConfidence": 0.95,
              "pageCount": 2,
              "textPageCount": 2
            }
          ]
        }
        """);
        using var pagesDoc = JsonDocument.Parse("""
        {
          "docPath": "Neutral/pages.pdf",
          "ocrFailureReason": "ocr_required_but_disabled",
          "ocrDiagnostics": { "failureReason": "stale_diagnostic_should_not_win" },
          "summary": { "pageCount": 2 },
          "pages": [
            {
              "pageNumber": 1,
              "qualityStatus": "ok",
              "wordCount": 50,
              "charCount": 300,
              "imageCount": 0,
              "chunkCount": 1
            }
          ]
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryBuildInventoryRenderedItem", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var qualityResults = new ToolResults();
        qualityResults.Items.Add(new ToolResults.Item { ToolName = "documents.extraction_quality", Result = qualityDoc.RootElement.Clone() });
        var qualityItem = Assert.IsType<ToolResults.Item>(method!.Invoke(sut, new object?[] { qualityResults, "en", CancellationToken.None }));
        Assert.Equal("inventory.rendered", qualityItem.ToolName);
        Assert.Equal("extraction_quality", qualityItem.Result.GetProperty("kind").GetString());
        var listed = Assert.Single(mem.LastListedDocuments);
        Assert.Equal("Neutral/quality.pdf", listed.DocPath);
        Assert.Equal("quality.pdf", listed.DocName);
        Assert.Same(listed, mem.PdfMap["PDF01"]);
        Assert.Same(listed, mem.PdfMap["Neutral/quality.pdf"]);

        var pagesResults = new ToolResults();
        pagesResults.Items.Add(new ToolResults.Item { ToolName = "documents.extraction_pages", Result = pagesDoc.RootElement.Clone() });
        var pagesItem = Assert.IsType<ToolResults.Item>(method!.Invoke(sut, new object?[] { pagesResults, "en", CancellationToken.None }));
        Assert.Equal("inventory.rendered", pagesItem.ToolName);
        Assert.Equal("extraction_pages", pagesItem.Result.GetProperty("kind").GetString());
        Assert.Equal(
            "ocr_required_but_disabled",
            pagesItem.Result.GetProperty("data").GetProperty("ocrFailureReason").GetString());
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
