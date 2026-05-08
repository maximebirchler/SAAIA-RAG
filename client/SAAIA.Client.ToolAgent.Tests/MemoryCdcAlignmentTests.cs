using System.Reflection;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class MemoryCdcAlignmentTests
{
    [Fact]
    public void M1Lite_catalog_snapshot_survives_session_reset_and_still_feeds_known_categories()
    {
        var mem = new ToolMemory
        {
            LastResolvedCategory = new ToolMemory.CategorySnapshot
            {
                CategoryRef = "1",
                CategoryPath = "ATEX",
                DisplayName = "ATEX"
            },
            LastPresentedCategories = new()
            {
                new ToolMemory.CategorySnapshot
                {
                    CategoryRef = "2",
                    CategoryPath = "Programmation",
                    DisplayName = "Programmation"
                }
            },
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                SnapshotId = "snap-1",
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "3",
                        CategoryPath = "General",
                        DisplayName = "General",
                        Aliases = new() { "general" }
                    }
                }
            }
        };

        mem.ResetConversationState();

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("EnumerateKnownCategories", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var categories = Assert.IsAssignableFrom<IEnumerable<ToolMemory.CategorySnapshot>>(method!.Invoke(sut, Array.Empty<object>()));
        var list = categories.ToList();

        var category = Assert.Single(list);
        Assert.Equal("General", category.DisplayName);
        Assert.Equal("3", category.CategoryRef);
    }

    [Fact]
    public void M1Lite_known_documents_survive_session_reset_without_keeping_last_focused_document()
    {
        var mem = new ToolMemory();
        mem.PromoteDocumentsToWorkspace(new[]
        {
            new ToolMemory.DocumentItem
            {
                DocId = "doc-1",
                DocPath = "Programmation/Mettler/MettlerToledo_IND570.pdf",
                DocName = "MettlerToledo_IND570.pdf",
                Category = "Programmation",
                CategoryPath = "Programmation/Mettler"
            }
        });
        mem.LastFocusedDocument = new ToolMemory.DocumentItem
        {
            DocId = "doc-1",
            DocPath = "Programmation/Mettler/MettlerToledo_IND570.pdf",
            DocName = "MettlerToledo_IND570.pdf"
        };

        mem.ResetConversationState();

        Assert.Null(mem.LastFocusedDocument);
        Assert.Single(mem.WorkspaceKnownDocuments);

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryResolveKnownDocumentByFuzzyReference", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var resolved = method!.Invoke(sut, new object?[] { "Mettler Toledo IND570" });
        Assert.NotNull(resolved);
    }

    [Fact]
    public void Runtime_snapshot_exposes_cdc_memory_sections_m1lite_m3_and_m6()
    {
        var mem = new ToolMemory
        {
            LastMode = "strict",
            LastRouterIntent = "rag.search",
            LastPlannerMemoryUpdate = "focus=doc-1",
            LastRouterConfidence = 0.82,
            PendingClarification = new ToolMemory.PendingClarificationState
            {
                Kind = "doc_reference",
                OriginalUserMessage = "quel document ?"
            },
            LastFocusedDocument = new ToolMemory.DocumentItem
            {
                DocId = "doc-1",
                DocPath = "ATEX/Doc.pdf",
                DocName = "Doc.pdf",
                CategoryPath = "ATEX"
            },
            LastResolvedCategory = new ToolMemory.CategorySnapshot
            {
                CategoryRef = "1",
                CategoryPath = "ATEX",
                DisplayName = "ATEX"
            },
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                SnapshotId = "snap-1",
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX" }
                }
            },
            CapabilitiesCache = new ToolMemory.RuntimeCapabilitiesSnapshot
            {
                IsAuthenticated = true,
                IsAdmin = true
            },
            StagedDirectCommand = new ToolMemory.PendingDirectCommand { CommandId = "documents.list" },
            LastAdminOperation = new ToolMemory.AdminOperationState { OperationKind = "reindex" },
            LastToolNames = new() { "rag.search" },
            LastRagQueries = new() { "inertage" },
            LastRagHitLabels = new() { "Doc.pdf p.1 score=0.9" },
            LastRagDegradedRetrievers = new() { "document_profile_v1" },
            LastRiskFlags = new() { "broad_query" }
        };
        mem.LastListedDocuments.Add(new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Doc.pdf" });
        mem.PdfMap["doc"] = new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Doc.pdf" };
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef { DocPath = "ATEX/Doc.pdf", PageStart = 1, PageEnd = 2 });
        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);

        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildAgentRuntimeSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var snapshot = Assert.IsAssignableFrom<Dictionary<string, object?>>(method!.Invoke(sut, Array.Empty<object>()));
        var memory = Assert.IsAssignableFrom<Dictionary<string, object?>>(snapshot["memory"]);
        Assert.Equal("cdc-v3-m1lite-m3-m6", memory["profile"]);
        Assert.Equal(1, memory["schemaVersion"]);

        var m1Lite = Assert.IsAssignableFrom<Dictionary<string, object?>>(memory["m1Lite"]);
        Assert.True((bool)m1Lite["hasCatalogSnapshot"]!);
        Assert.Equal(1, m1Lite["catalogCategoriesCount"]);
        Assert.True((bool)m1Lite["hasCapabilitiesSnapshot"]!);
        Assert.True((bool)m1Lite["isAdmin"]!);
        Assert.Equal(0, m1Lite["knownDocumentsCount"]);

        var m3 = Assert.IsAssignableFrom<Dictionary<string, object?>>(memory["m3"]);
        Assert.True((bool)m3["hasPendingClarification"]!);
        Assert.True((bool)m3["hasFocusedDocument"]!);
        Assert.Equal(1, m3["lastListedDocumentsCount"]);
        Assert.Equal(1, m3["pdfMapSize"]);
        Assert.Equal(1, m3["lastSourcesCount"]);
        Assert.True((bool)m3["hasResolvedCategory"]!);
        Assert.Equal(0, m3["presentedCategoriesCount"]);

        var m6 = Assert.IsAssignableFrom<Dictionary<string, object?>>(memory["m6"]);
        Assert.Equal("strict", m6["lastMode"]);
        Assert.Equal("rag.search", m6["lastRouterIntent"]);
        Assert.Equal(1, m6["lastToolNamesCount"]);
        Assert.Equal(1, m6["lastRagQueriesCount"]);
        Assert.Equal(1, m6["lastRagHitLabelsCount"]);
        Assert.Equal(1, m6["lastRagDegradedRetrieversCount"]);
        Assert.Equal(1, m6["lastRiskFlagsCount"]);
        Assert.True((bool)m6["hasPlannerMemoryUpdate"]!);
        Assert.Equal(0.82, Assert.IsType<double>(m6["routerConfidence"]!));
        Assert.True((bool)m6["hasAdminOperation"]!);
        Assert.True((bool)m6["hasStagedDirectCommand"]!);

        var rag = Assert.IsAssignableFrom<Dictionary<string, object?>>(snapshot["rag"]);
        Assert.Equal(new[] { "inertage" }, Assert.IsAssignableFrom<string[]>(rag["queries"]));
        Assert.Equal(new[] { "Doc.pdf p.1 score=0.9" }, Assert.IsAssignableFrom<string[]>(rag["hitLabels"]));
        Assert.Equal(new[] { "document_profile_v1" }, Assert.IsAssignableFrom<string[]>(rag["degradedRetrievers"]));
    }

    [Fact]
    public void Promoted_workspace_documents_appear_in_runtime_snapshot_m1lite()
    {
        var mem = new ToolMemory();
        mem.PromoteDocumentsToWorkspace(new[]
        {
            new ToolMemory.DocumentItem
            {
                DocId = "doc-1",
                DocPath = "ATEX/CEN TR 15281.pdf",
                DocName = "CEN TR 15281.pdf"
            }
        });

        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildAgentRuntimeSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var snapshot = Assert.IsAssignableFrom<Dictionary<string, object?>>(method!.Invoke(sut, Array.Empty<object>()));
        var memory = Assert.IsAssignableFrom<Dictionary<string, object?>>(snapshot["memory"]);
        var m1Lite = Assert.IsAssignableFrom<Dictionary<string, object?>>(memory["m1Lite"]);

        Assert.Equal(1, m1Lite["knownDocumentsCount"]);
    }

    [Fact]
    public void Router_memory_context_exposes_bounded_canonical_m1lite_hints()
    {
        var mem = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX", Ordinal = 1, Aliases = new() { "atex", "explosion" } },
                    new ToolMemory.CategorySnapshot { CategoryRef = "2", CategoryPath = "Programmation", DisplayName = "Programmation", Ordinal = 2, Aliases = new() { "plc", "automate" } }
                }
            }
        };
        mem.PromoteDocumentsToWorkspace(new[]
        {
            new ToolMemory.DocumentItem
            {
                DocId = "doc-1",
                DocPath = "ATEX/CEN TR 15281.pdf",
                DocName = "CEN TR 15281.pdf",
                Category = "ATEX",
                CategoryPath = "ATEX"
            },
            new ToolMemory.DocumentItem
            {
                DocId = "doc-2",
                DocPath = "Programmation/Mettler/MettlerToledo_IND570.pdf",
                DocName = "MettlerToledo_IND570.pdf",
                Category = "Programmation",
                CategoryPath = "Programmation/Mettler"
            }
        });
        mem.ResetConversationState();

        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
        var analyze = DocumentRefResolver.Analyze("le projet respecte-t-il la norme inertage ?", null, null);
        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildRouterMemoryContext", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var memoryCtx = Assert.IsAssignableFrom<Dictionary<string, object?>>(method!.Invoke(sut, new object?[] { analyze }));
        var m1Lite = Assert.IsAssignableFrom<Dictionary<string, object?>>(memoryCtx["m1Lite"]);
        var categories = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(m1Lite["canonicalCategories"]);
        var documents = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(m1Lite["canonicalDocuments"]);

        Assert.Equal(2, categories.Count);
        Assert.Equal("ATEX", categories[0]["displayName"]);
        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, x => Equals(x["docName"], "CEN TR 15281.pdf"));
        Assert.Contains(documents, x => Equals(x["docName"], "MettlerToledo_IND570.pdf"));
    }

    [Fact]
    public void Runtime_snapshot_exposes_readable_memory_summary_for_support_and_diagnostics()
    {
        var mem = new ToolMemory
        {
            LastMode = "auto",
            LastRouterIntent = "inventory.categories",
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX" }
                }
            },
            CapabilitiesCache = new ToolMemory.RuntimeCapabilitiesSnapshot
            {
                IsAuthenticated = true,
                IsAdmin = false
            },
            LastResolvedCategory = new ToolMemory.CategorySnapshot
            {
                CategoryRef = "1",
                CategoryPath = "ATEX",
                DisplayName = "ATEX"
            },
            PendingClarification = new ToolMemory.PendingClarificationState
            {
                Kind = "doc_reference",
                OriginalUserMessage = "quel document ?"
            }
        };
        mem.PromoteDocumentsToWorkspace(new[]
        {
            new ToolMemory.DocumentItem
            {
                DocId = "doc-1",
                DocPath = "ATEX/CEN TR 15281.pdf",
                DocName = "CEN TR 15281.pdf"
            }
        });
        mem.LastListedDocuments.Add(new ToolMemory.DocumentItem { DocId = "doc-2", DocName = "live.pdf" });

        var sut = new ToolAgentOrchestrator(new ApiClient(), null!, mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("BuildAgentRuntimeSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var snapshot = Assert.IsAssignableFrom<Dictionary<string, object?>>(method!.Invoke(sut, Array.Empty<object>()));
        var summary = Assert.IsAssignableFrom<Dictionary<string, object?>>(snapshot["memorySummary"]);

        Assert.Equal("cdc-v3-m1lite-m3-m6", summary["profile"]);
        Assert.Equal(1, summary["schemaVersion"]);
        Assert.Equal("v3.1", summary["cdcAlignment"]);

        var persistence = Assert.IsAssignableFrom<Dictionary<string, object?>>(summary["persistence"]);
        Assert.True((bool)persistence["language"]!);
        Assert.True((bool)persistence["style"]!);
        Assert.False((bool)persistence["mode"]!);
        Assert.False((bool)persistence["focusedDocument"]!);
        Assert.False((bool)persistence["resolvedCategory"]!);

        var resetPolicy = Assert.IsAssignableFrom<Dictionary<string, object?>>(summary["resetPolicy"]);
        Assert.True((bool)resetPolicy["preservesM1Lite"]!);
        Assert.True((bool)resetPolicy["preservesPreferences"]!);
        Assert.True((bool)resetPolicy["clearsM3"]!);
        Assert.True((bool)resetPolicy["clearsM6"]!);
        Assert.True((bool)resetPolicy["resetsModeToAuto"]!);

        var workspace = Assert.IsAssignableFrom<Dictionary<string, object?>>(summary["workspace"]);
        Assert.Equal(1, workspace["catalogCategoriesCount"]);
        Assert.Equal(1, workspace["knownDocumentsCount"]);
        Assert.True((bool)workspace["hasCapabilitiesSnapshot"]!);

        var session = Assert.IsAssignableFrom<Dictionary<string, object?>>(summary["session"]);
        Assert.False((bool)session["hasFocusedDocument"]!);
        Assert.Equal(1, session["lastListedDocumentsCount"]);
        Assert.True((bool)session["hasResolvedCategory"]!);
        Assert.True((bool)session["hasPendingClarification"]!);

        var execution = Assert.IsAssignableFrom<Dictionary<string, object?>>(summary["execution"]);
        Assert.Equal("auto", execution["mode"]);
        Assert.True((bool)execution["hasRouterIntent"]!);
        Assert.Equal(0, execution["toolNamesCount"]);
        Assert.False((bool)execution["hasAdminOperation"]!);
    }

    [Fact]
    public void User_prefs_persist_language_and_style_but_not_mode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SAAIA.Tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDir, "user-prefs.bin");

        try
        {
            UserPrefsStore.FilePathOverrideForTests = filePath;

            UserPrefsStore.SaveLanguage("de");
            UserPrefsStore.SaveStyle("technical");
            UserPrefsStore.SaveMode("strict");

            var prefs = UserPrefsStore.Load();

            Assert.Equal("de", prefs.Language);
            Assert.Equal("technical", prefs.Style);
            Assert.Equal("auto", prefs.Mode);
        }
        finally
        {
            UserPrefsStore.FilePathOverrideForTests = null;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
