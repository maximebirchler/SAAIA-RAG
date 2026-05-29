using System;
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Localization;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class DeterministicShortcutRegressionTests
{
    [Fact]
    public void Stats_fallback_uses_requested_french_language()
    {
        using var doc = JsonDocument.Parse("""
        {
          "totalDocuments": 3,
          "totalNonEmptyFolders": 10,
          "emptyFolderCount": 2,
          "maxDepth": 3,
          "foldersByDepth": [
            { "depth": 1, "folderCount": 3 },
            { "depth": 2, "folderCount": 5 },
            { "depth": 3, "folderCount": 2 }
          ],
          "rootFolders": [
            { "name": "ATEX", "totalDocuments": 1, "directDocuments": 1, "subfolderCount": 0 }
          ]
        }
        """);

        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildStatsFallbackAnswer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var rendered = (string)method!.Invoke(null, new object[] { doc.RootElement, "fr" })!;
        Assert.Contains("Statistiques du catalogue", rendered);
        Assert.Contains("- Dossiers vides : 2", rendered);
        Assert.Contains("- Dossiers de deuxième niveau : 5", rendered);
        Assert.Contains("• ATEX: 1 document(s), 0 sous-dossier(s)", rendered);
        Assert.DoesNotContain("Profondeur maximale", rendered);
        Assert.DoesNotContain("Catalog statistics:", rendered);
    }

    [Fact]
    public void Stats_fallback_uses_requested_english_language()
    {
        using var doc = JsonDocument.Parse("""
        {
          "totalDocuments": 3,
          "totalNonEmptyFolders": 10,
          "emptyFolderCount": 2,
          "maxDepth": 3,
          "foldersByDepth": [
            { "depth": 1, "folderCount": 3 },
            { "depth": 2, "folderCount": 5 },
            { "depth": 3, "folderCount": 2 }
          ],
          "rootFolders": []
        }
        """);

        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildStatsFallbackAnswer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var rendered = (string)method!.Invoke(null, new object[] { doc.RootElement, "en" })!;
        Assert.Contains("Catalog statistics:", rendered);
        Assert.Contains("- Empty folders: 2", rendered);
        Assert.Contains("- Second-level folders: 5", rendered);
        Assert.DoesNotContain("Maximum depth", rendered);
        Assert.DoesNotContain("Statistiques du catalogue", rendered);
    }


    [Fact]
    public void Named_document_summary_request_can_flow_without_forced_clarification_when_a_search_probe_is_available()
    {
        var analysis = DocumentRefResolver.Analyze(
            "Donne moi le résumé du document Mettler Toledo ind 570",
            lastFocusedDocument: null,
            lastListedDocuments: null);

        Assert.True(analysis.IsContentRequest);
        Assert.True(analysis.WantsSummary);
        Assert.False(analysis.NeedsClarification);
        Assert.Equal("Mettler Toledo ind 570", analysis.ResolvedDocRef);
    }

    [Fact]
    public void Stored_summary_store_phrase_is_detected_from_french_storage_wording()
    {
        var analysis = DocumentRefResolver.Analyze(
            "Fais moi un vrai résumé pour le document MettlerToledo_IND570.pdf dans le but d'être stocké.",
            lastFocusedDocument: null,
            lastListedDocuments: null);

        Assert.True(analysis.WantsStoredSummaryStore);
        Assert.Equal("MettlerToledo_IND570.pdf", analysis.ResolvedDocRef);
    }

    [Fact]
    public void Stored_summary_store_phrase_is_not_misclassified_as_check()
    {
        var analysis = DocumentRefResolver.Analyze(
            "Fais moi un vrai résumé pour le document MettlerToledo_IND570.pdf dans le but d'être stocké.",
            lastFocusedDocument: null,
            lastListedDocuments: null);

        Assert.True(analysis.WantsStoredSummaryStore);
        Assert.False(analysis.WantsStoredSummaryCheck);
    }

    [Fact]
    public void Explicit_category_name_wins_over_last_resolved_category_in_follow_up_shortcuts()
    {
        var mem = new ToolMemory
        {
            LastResolvedCategory = new ToolMemory.CategorySnapshot
            {
                CategoryRef = "3",
                CategoryPath = "Programmation",
                DisplayName = "Programmation",
                Ordinal = 3
            },
            LastPresentedCategories = new()
            {
                new ToolMemory.CategorySnapshot
                {
                    CategoryRef = "1",
                    CategoryPath = "ATEX",
                    DisplayName = "ATEX",
                    Ordinal = 1,
                    Aliases = new() { "atex" }
                },
                new ToolMemory.CategorySnapshot
                {
                    CategoryRef = "3",
                    CategoryPath = "Programmation",
                    DisplayName = "Programmation",
                    Ordinal = 3,
                    Aliases = new() { "programmation" }
                }
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryExtractFollowUpCategoryRef", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { "et celle de la catégorie atex ?", null };
        var handled = (bool)method!.Invoke(sut, args)!;

        Assert.True(handled);
        Assert.Equal("atex", ((string)args[1]!).ToLowerInvariant());
    }


    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Direct_category_list_shortcut_is_detected_only_for_exact_help_prompts(string language)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectCategoriesRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = ClientUiText.BuildPromptCategories(language);
        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.True(handled);
    }



    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Direct_catalog_stats_shortcut_is_detected_only_for_exact_help_prompts(string language)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectCatalogStatsRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = ClientUiText.BuildPromptCatalogStats(language);
        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.True(handled);
    }


    [Theory]
    [InlineData("fr", "PumpManual.pdf")]
    [InlineData("en", "PumpManual.pdf")]
    public void Exact_document_search_help_prompts_are_detected(string language, string query)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryExtractExactDocumentSearchQuery", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { ClientUiText.BuildPromptSearchDocuments(language, query), null };
        var handled = (bool)method!.Invoke(null, args)!;

        Assert.True(handled);
        Assert.Equal(query, (string)args[1]!);
    }

    [Theory]
    [InlineData("qui es-tu ?")]
    [InlineData("who are you ?")]
    [InlineData("2+2 ?")]
    [InlineData("De quoi parle le document Mettler Toledo ind 570 ?")]
    public void Direct_category_shortcut_is_not_triggered_for_general_or_document_content_requests(string input)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectCategoriesRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.False(handled);
    }

    [Theory]
    [InlineData("qui es-tu ?")]
    [InlineData("who are you ?")]
    [InlineData("résume le document 1")]
    public void Direct_all_documents_shortcut_is_not_triggered_for_general_or_content_requests(string input)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectAllDocumentsRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.False(handled);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Direct_summary_present_shortcut_requires_exact_help_prompt(string language)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectSummaryStatusRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { ClientUiText.BuildPromptSummaryPresentCount(language), null, null };
        var handled = (bool)method!.Invoke(null, args)!;

        Assert.True(handled);
        Assert.Equal("present", (string)args[2]!);
    }


    [Theory]
    [InlineData("merci", "fr")]
    [InlineData("thanks", "en")]
    [InlineData("gracias", "es")]
    [InlineData("obrigado", "pt")]
    [InlineData("danke", "de")]
    [InlineData("grazie", "it")]
    public void Courtesy_shortcuts_are_detected_for_all_supported_languages(string input, string expectedLanguage)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeShortCourtesyMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { input, null };
        var handled = (bool)method!.Invoke(null, args)!;

        Assert.True(handled);
        Assert.Equal(expectedLanguage, (string)args[1]!);
    }

    [Fact]
    public void Live_summary_follow_up_can_reuse_last_requested_document_reference()
    {
        var analysis = DocumentRefResolver.Analyze(
            "bas fais un résumé live",
            lastFocusedDocument: null,
            lastListedDocuments: null,
            lastRequestedDocumentRef: "MettlerToledo_IND570.pdf");

        Assert.True(analysis.IsContentRequest);
        Assert.True(analysis.WantsSummary);
        Assert.False(analysis.NeedsClarification);
        Assert.Equal("MettlerToledo_IND570.pdf", analysis.ResolvedDocRef);
    }

    [Fact]
    public void Named_document_summary_request_in_italian_can_flow_without_forced_clarification()
    {
        var analysis = DocumentRefResolver.Analyze(
            "dammi il riassunto del documento MettlerToledo_IND570.pdf",
            lastFocusedDocument: null,
            lastListedDocuments: null);

        Assert.True(analysis.IsContentRequest);
        Assert.True(analysis.WantsSummary);
        Assert.False(analysis.NeedsClarification);
        Assert.Equal("MettlerToledo_IND570.pdf", analysis.ResolvedDocRef);
    }

    [Fact]
    public void One_shot_translation_uses_the_real_last_answer_when_it_differs_from_the_cached_inventory_render()
    {
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            { "ordinal": 1, "name": "ATEX", "totalDocuments": 1 },
            { "ordinal": 2, "name": "General", "totalDocuments": 1 },
            { "ordinal": 3, "name": "Programmation", "totalDocuments": 1 }
          ]
        }
        """);

        var mem = new ToolMemory
        {
            LastLanguage = "fr",
            LastAnswerLanguage = "fr",
            LastAssistantAnswer = "Je viens de te lister les catégories présentes sur le serveur : ATEX, Général et Programmation.",
            LastDeterministicRender = new ToolMemory.DeterministicRenderState
            {
                Kind = "categories",
                RouterIntent = "inventory.categories",
                DataJson = doc.RootElement.GetRawText()
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("ShouldTranslateFromDeterministicRender", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var shouldUseDeterministic = (bool)method!.Invoke(sut, Array.Empty<object>())!;

        Assert.False(shouldUseDeterministic);
    }

    [Fact]
    public void One_shot_translation_keeps_using_the_cached_inventory_render_when_it_matches_the_last_answer()
    {
        using var doc = JsonDocument.Parse("""
        {
          "items": [
            { "ordinal": 1, "name": "ATEX", "totalDocuments": 1 },
            { "ordinal": 2, "name": "General", "totalDocuments": 1 },
            { "ordinal": 3, "name": "Programmation", "totalDocuments": 1 }
          ]
        }
        """);

        var mem = new ToolMemory
        {
            LastLanguage = "fr",
            LastAnswerLanguage = "fr",
            LastAssistantAnswer = "Catégories présentes sur le serveur :\n1. ATEX (1)\n2. General (1)\n3. Programmation (1)",
            LastDeterministicRender = new ToolMemory.DeterministicRenderState
            {
                Kind = "categories",
                RouterIntent = "inventory.categories",
                DataJson = doc.RootElement.GetRawText()
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("ShouldTranslateFromDeterministicRender", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var shouldUseDeterministic = (bool)method!.Invoke(sut, Array.Empty<object>())!;

        Assert.True(shouldUseDeterministic);
    }


    [Theory]
    [InlineData("donne moi tous les documents du serveur stp")]
    [InlineData("liste des documents du serveur")]
    [InlineData("show all server documents")]
    public void Direct_all_documents_shortcut_stays_disabled_for_free_text_requests(string input)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectAllDocumentsRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.False(handled);
    }

    [Fact]
    public void Compact_french_ordinal_follow_up_resolves_the_first_presented_category()
    {
        var mem = new ToolMemory
        {
            LastPresentedCategories = new()
            {
                new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX", Ordinal = 1 },
                new ToolMemory.CategorySnapshot { CategoryRef = "2", CategoryPath = "General", DisplayName = "General", Ordinal = 2 },
                new ToolMemory.CategorySnapshot { CategoryRef = "3", CategoryPath = "Programmation", DisplayName = "Programmation", Ordinal = 3 }
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryExtractFollowUpCategoryRef", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = new object?[] { "et pour la 1ère ?", null };
        var handled = (bool)method!.Invoke(sut, args)!;

        Assert.True(handled);
        Assert.Equal("1", (string)args[1]!);
    }

    [Fact]
    public void Protected_translation_placeholders_restore_canonical_category_names_after_free_translation()
    {
        var terms = new[] { "ATEX", "General", "Programmation" };
        var apply = typeof(ToolAgentOrchestrator).GetMethod("ApplyProtectedTranslationTerms", BindingFlags.NonPublic | BindingFlags.Static);
        var restore = typeof(ToolAgentOrchestrator).GetMethod("RestoreProtectedTranslationTerms", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(apply);
        Assert.NotNull(restore);

        var args = new object?[]
        {
            "Here are the categories present on the server that I listed: ATEX, General and Programmation.",
            terms,
            null
        };

        var protectedText = (string)apply!.Invoke(null, args)!;
        var placeholders = Assert.IsAssignableFrom<System.Collections.Generic.Dictionary<string, string>>(args[2]);
        var fakeTranslated = protectedText.Replace("Here are the categories present on the server that I listed:", "Aquí están las categorías del servidor que enumeré:", StringComparison.Ordinal)
                                        .Replace(" and ", " y ", StringComparison.Ordinal);
        var restored = (string)restore!.Invoke(null, new object?[] { fakeTranslated, placeholders })!;

        Assert.Contains("ATEX", restored);
        Assert.Contains("General", restored);
        Assert.Contains("Programmation", restored);
        Assert.DoesNotContain("Programación", restored, StringComparison.OrdinalIgnoreCase);
    }



    [Theory]
    [InlineData("liste les documents de catégorie", true)]
    [InlineData("montres les statistiques", true)]
    [InlineData("reindex document", false)]
    [InlineData("list the documents in category Programmation", true)]
    [InlineData("How many documents are on the server?", false)]
    [InlineData("Quels documents parlent d'API ?", false)]
    [InlineData("Quels PDF de cette categorie sont les meilleurs pour tester la robustesse du moteur documentaire ?", false)]
    public void Malformed_guided_command_guard_only_catches_near_miss_catalog_or_admin_commands(string input, bool expected)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeMalformedGuidedCommandRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.Equal(expected, handled);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Exact_admin_rescan_help_prompts_are_whitelisted_before_the_malformed_guard(string language)
    {
        var target = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectAdminRescanRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(target);

        var handled = (bool)target!.Invoke(null, new object?[] { ClientUiText.BuildPromptAdminRescan(language) })!;

        Assert.True(handled);
    }

    [Theory]
    [InlineData("fr", "PumpManual.pdf")]
    [InlineData("en", "PumpManual.pdf")]
    public void Admin_reindex_help_prompts_are_no_longer_exposed_as_chat_shortcuts(string language, string document)
    {
        var directTarget = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectAdminReindexRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(directTarget);

        var directHandled = (bool)directTarget!.Invoke(null, new object?[] { ClientUiText.BuildPromptAdminReindex(language, document) })!;
        Assert.False(directHandled);

        var malformedTarget = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeMalformedGuidedCommandRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(malformedTarget);

        var malformedHandled = (bool)malformedTarget!.Invoke(null, new object?[] { ClientUiText.BuildPromptAdminReindex(language, document) })!;
        Assert.False(malformedHandled);
    }

    [Fact]
    public void Scoped_documents_list_render_mentions_the_selected_category()
    {
        using var doc = JsonDocument.Parse("""
        {
          "scopePath": "Programmation",
          "items": [
            { "docPath": "Programmation/Mettler/file.pdf", "docName": "file.pdf", "categoryPath": "Programmation" }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("list", doc.RootElement, "fr");

        Assert.Contains("Documents de la catégorie Programmation", rendered);
        Assert.DoesNotContain("Documents présents sur le serveur", rendered);
    }

    [Fact]
    public void Scoped_stats_render_mentions_when_no_subfolder_exists()
    {
        using var doc = JsonDocument.Parse("""
        {
          "scopePath": "ATEX",
          "totalDocuments": 1,
          "totalNonEmptyFolders": 1,
          "foldersByDepth": [ { "depth": 1, "folderCount": 1 } ],
          "rootFolders": []
        }
        """);

        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildStatsFallbackAnswer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var rendered = (string)method!.Invoke(null, new object[] { doc.RootElement, "fr" })!;

        Assert.Contains("Statistiques de la catégorie ATEX", rendered);
        Assert.Contains("  • Aucun dossier ni sous-dossier.", rendered);
    }


    [Fact]
    public void Exact_static_help_prompt_tolerates_typographic_variants()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectAdminRescanRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var input = ClientUiText.BuildPromptAdminRescan("fr").Replace("'", "’");
        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.True(handled);
    }

    [Fact]
    public void Summary_status_snapshot_becomes_referenceable_document_list()
    {
        var mem = new ToolMemory
        {
            LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
            {
                Total = 1,
                Items = new()
                {
                    new ToolMemory.SummaryStatusItem
                    {
                        DocId = "doc-1",
                        DocPath = "Programmation/Mettler/MettlerToledo_IND570.pdf",
                        DocName = "MettlerToledo_IND570.pdf",
                        Category = "Programmation/Mettler",
                        CategoryRef = "cat_042",
                        CategoryPath = "Programmation/Mettler",
                        SummaryState = "present"
                    }
                }
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("UpdateLastListedDocumentsFromSummaryStatusSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(sut, Array.Empty<object>());

        var item = Assert.Single(mem.LastListedDocuments);
        Assert.Equal("MettlerToledo_IND570.pdf", item.DocName);
        Assert.Equal("Programmation/Mettler/MettlerToledo_IND570.pdf", item.DocPath);
        Assert.Equal("cat_042", item.CategoryRef);
        Assert.Equal("Programmation/Mettler", item.CategoryPath);
    }

    [Theory]
    [InlineData("C'est fait ?")]
    [InlineData("c’est fini ?")]
    [InlineData("is it done?")]
    public void Recent_admin_status_follow_up_is_detected(string input)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeRecentAdminOperationStatusFollowUp", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;

        Assert.True(handled);
    }


    [Fact]
    public void Admin_job_status_reader_supports_pascal_case_payloads()
    {
        using var doc = JsonDocument.Parse("""
{
  "Status": "done",
  "LastError": "boom"
}
""");

        var statusMethod = typeof(ToolAgentOrchestrator).GetMethod("ReadAdminJobStatus", BindingFlags.NonPublic | BindingFlags.Static);
        var errorMethod = typeof(ToolAgentOrchestrator).GetMethod("ReadAdminJobLastError", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(statusMethod);
        Assert.NotNull(errorMethod);

        var status = (string?)statusMethod!.Invoke(null, new object?[] { doc.RootElement });
        var error = (string?)errorMethod!.Invoke(null, new object?[] { doc.RootElement });

        Assert.Equal("done", status);
        Assert.Equal("boom", error);
    }


    [Fact]
    public void Fuzzy_document_resolution_rejects_single_token_vendor_reference()
    {
        var mem = new ToolMemory
        {
            LastListedDocuments = new()
            {
                new ToolMemory.DocumentItem { DocId = "1", DocPath = "Programmation/Siemens/TIA_Portal_V16.pdf", DocName = "TIA_Portal_V16.pdf", Category = "Programmation", CategoryPath = "Programmation/Siemens" },
                new ToolMemory.DocumentItem { DocId = "2", DocPath = "Programmation/Siemens/Siemens_S7_Manual.pdf", DocName = "Siemens_S7_Manual.pdf", Category = "Programmation", CategoryPath = "Programmation/Siemens" }
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryResolveKnownDocumentByFuzzyReference", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var resolved = method!.Invoke(sut, new object?[] { "Siemens" });
        Assert.Null(resolved);
    }

    [Fact]
    public void Fuzzy_document_resolution_accepts_specific_multi_token_reference()
    {
        var mem = new ToolMemory
        {
            LastListedDocuments = new()
            {
                new ToolMemory.DocumentItem { DocId = "1", DocPath = "Programmation/Mettler/MettlerToledo_IND570.pdf", DocName = "MettlerToledo_IND570.pdf", Category = "Programmation", CategoryPath = "Programmation/Mettler" }
            }
        };

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryResolveKnownDocumentByFuzzyReference", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var resolved = method!.Invoke(sut, new object?[] { "Mettler Toledo IND570" });
        Assert.NotNull(resolved);
    }

    [Theory]
    [InlineData("Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed")]
    [InlineData("Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf")]
    public void Exact_document_reference_match_accepts_exact_file_name_with_or_without_extension(string query)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("IsExactDocumentReferenceMatch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var matched = (bool)method!.Invoke(null, new object?[]
        {
            query,
            "Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf",
            "General/Accords/Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf"
        })!;

        Assert.True(matched);
    }


    [Fact]
    public void Candidate_matches_document_identity_does_not_treat_folder_name_as_document_name()
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("CandidateMatchesDocumentIdentity", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var folderOnly = (bool)method!.Invoke(null, new object?[] { "Siemens", "TIA Portal Manual.pdf", "Programmation/Siemens/TIA Portal Manual.pdf" })!;
        var exactDoc = (bool)method.Invoke(null, new object?[] { "Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf", "Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf", "General/Accords/Accord sur le transfert du code source des logiciels & des documents natifs_2109-3142_Signed.pdf" })!;

        Assert.False(folderOnly);
        Assert.True(exactDoc);
    }

    [Fact]
    public void Document_target_is_category_message_is_explicit()
    {
        var message = DeterministicAgentText.AdminReindexDocumentTargetIsCategory("fr", "Siemens");
        Assert.Contains("dossier", message);
        Assert.Contains("catégorie", message);
        Assert.Contains("document", message);
    }



    [Theory]
    [InlineData("fr", "PumpManual.pdf")]
    [InlineData("en", "PumpManual.pdf")]
    public void Help_only_admin_reindex_display_text_is_detected_as_help_only(string language, string documentRef)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeHelpOnlyAdminReindexDisplayText", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { ClientUiText.BuildPromptAdminReindexDisplay(language, documentRef) })!;
        Assert.True(handled);
    }

    [Theory]
    [InlineData("Cible de réindexation : Siemens")]
    [InlineData("Action aide — réindexer le document : PumpManual.pdf")]
    [InlineData("Relance l'ingestion du document PumpManual.pdf.")]
    [InlineData("Help action — reindex document: PumpManual.pdf")]
    public void Help_only_admin_reindex_text_detection_catches_old_and_new_chat_like_forms(string input)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeHelpOnlyAdminReindexDisplayText", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var handled = (bool)method!.Invoke(null, new object?[] { input })!;
        Assert.True(handled);
    }

    [Fact]
    public void Category_overview_question_is_not_treated_as_guided_admin_command()
    {
        const string input = "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?";
        var helpMethod = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeHelpOnlyAdminReindexDisplayText", BindingFlags.NonPublic | BindingFlags.Static);
        var malformedMethod = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeMalformedGuidedCommandRequest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helpMethod);
        Assert.NotNull(malformedMethod);

        Assert.False((bool)helpMethod!.Invoke(null, new object?[] { input })!);
        Assert.False((bool)malformedMethod!.Invoke(null, new object?[] { input })!);
    }

    [Theory]
    [InlineData("Programmation/Siemens/TIA_Portal.pdf", true)]
    [InlineData("Programmation/Siemens", false)]
    [InlineData("Siemens", false)]
    public void Reindexable_document_path_requires_an_exact_pdf_path(string path, bool expected)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeReindexableDocumentPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = (bool)method!.Invoke(null, new object?[] { path })!;
        Assert.Equal(expected, actual);
    }

}
