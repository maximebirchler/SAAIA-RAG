using System.Reflection;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolMemoryCdcAlignmentTests
{
    [Fact]
    public void Legacy_flat_properties_are_backed_by_structured_memory_layers()
    {
        var mem = new ToolMemory();

        mem.LastLanguage = "de";
        mem.LastStyle = "technical";
        mem.LastMode = "strict";
        mem.LastListedDocuments = new()
        {
            new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Manual.pdf" }
        };
        mem.LastRouterIntent = "rag.search";
        mem.CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
        {
            SnapshotId = "snap-1"
        };

        Assert.Equal("de", mem.Preferences.Language);
        Assert.Equal("technical", mem.Preferences.Style);
        Assert.Equal("strict", mem.Execution.ActiveMode);
        Assert.Single(mem.Session.LastListedDocuments);
        Assert.Equal("rag.search", mem.Execution.LastRouterIntent);
        Assert.Equal("snap-1", mem.Workspace.CatalogSnapshotCache?.SnapshotId);

        mem.Session.LastRequestedDocumentRef = "Manual.pdf";
        mem.Execution.LastPlannerMemoryUpdate = "focus:Manual.pdf";
        mem.Workspace.CapabilitiesCache = new ToolMemory.RuntimeCapabilitiesSnapshot { IsAdmin = true };

        Assert.Equal("Manual.pdf", mem.LastRequestedDocumentRef);
        Assert.Equal("focus:Manual.pdf", mem.LastPlannerMemoryUpdate);
        Assert.True(mem.CapabilitiesCache?.IsAdmin);
    }

    [Fact]
    public void ResetConversationState_preserves_preferences_and_workspace_but_clears_session_and_execution()
    {
        var mem = new ToolMemory
        {
            LastLanguage = "it",
            LastStyle = "executive",
            LastMode = "strict",
            LastUserDetectedLanguage = "it",
            LastAnswerLanguage = "it",
            LastUserMessage = "ciao",
            LastAssistantAnswer = "salut",
            LastRequestedDocumentRef = "Manual.pdf",
            LastRouterIntent = "inventory.categories",
            LastToolNames = new() { "documents.list" },
            LastReasoningTracePublic = new() { "router:inventory" },
            LastRiskFlags = new() { "stale" },
            LastPlannerMemoryUpdate = "focus=manual",
            LastRouterConfidence = 0.91,
            PendingClarification = new ToolMemory.PendingClarificationState { Kind = "doc_reference", OriginalUserMessage = "quel doc ?" },
            LastDeterministicRender = new ToolMemory.DeterministicRenderState { Kind = "categories", DataJson = "{}" },
            LastSearchOnlyCategory = "ATEX",
            LastInventoryAction = "list",
            LastResolvedCategory = new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX" },
            LastPresentedCategories = new() { new ToolMemory.CategorySnapshot { CategoryRef = "1", CategoryPath = "ATEX", DisplayName = "ATEX" } },
            LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot { Total = 2 },
            SummaryTranslationCache = new(StringComparer.OrdinalIgnoreCase) { ["fr|doc-1"] = "resume" },
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { SnapshotId = "snap-1", CatalogVersion = "v1" },
            CapabilitiesCache = new ToolMemory.RuntimeCapabilitiesSnapshot { IsAuthenticated = true, IsAdmin = true },
            StagedDirectCommand = new ToolMemory.PendingDirectCommand { CommandId = "documents.list" },
            LastAdminOperation = new ToolMemory.AdminOperationState { OperationKind = "reindex" }
        };
        mem.LastListedDocuments.Add(new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Manual.pdf" });
        mem.PdfMap["manual"] = new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Manual.pdf" };
        mem.LastSourcesUsed.Add(new ToolMemory.SourceRef { DocPath = "ATEX/Manual.pdf", PageStart = 3, PageEnd = 4 });
        mem.LastFocusedDocument = new ToolMemory.DocumentItem { DocId = "doc-1", DocName = "Manual.pdf" };

        mem.ResetConversationState();

        Assert.Equal("it", mem.LastLanguage);
        Assert.Equal("executive", mem.LastStyle);
        Assert.Equal("snap-1", mem.CatalogSnapshotCache?.SnapshotId);
        Assert.True(mem.CapabilitiesCache?.IsAdmin);

        Assert.Equal("auto", mem.LastMode);
        Assert.Null(mem.LastUserDetectedLanguage);
        Assert.Null(mem.LastAnswerLanguage);
        Assert.Null(mem.LastRouterIntent);
        Assert.Null(mem.LastPlannerMemoryUpdate);
        Assert.Null(mem.LastRouterConfidence);
        Assert.Null(mem.StagedDirectCommand);
        Assert.Null(mem.LastAdminOperation);
        Assert.Empty(mem.LastToolNames);
        Assert.Empty(mem.LastReasoningTracePublic);
        Assert.Empty(mem.LastRiskFlags);

        Assert.Null(mem.LastUserMessage);
        Assert.Null(mem.LastAssistantAnswer);
        Assert.Null(mem.LastRequestedDocumentRef);
        Assert.Null(mem.PendingClarification);
        Assert.Null(mem.LastDeterministicRender);
        Assert.Null(mem.LastSearchOnlyCategory);
        Assert.Null(mem.LastInventoryAction);
        Assert.Null(mem.LastResolvedCategory);
        Assert.Null(mem.LastSummaryStatusSnapshot);
        Assert.Null(mem.LastFocusedDocument);
        Assert.Empty(mem.LastPresentedCategories);
        Assert.Empty(mem.LastListedDocuments);
        Assert.Empty(mem.PdfMap);
        Assert.Empty(mem.LastSourcesUsed);
        Assert.Empty(mem.SummaryTranslationCache);
        Assert.Equal(80, mem.LastListLimit);
        Assert.Equal(0, mem.LastListOffset);
        Assert.False(mem.LastListEndOfList);
        Assert.Null(mem.LastListCategoryPath);
        Assert.Null(mem.LastListQuery);
        Assert.Null(mem.LastListTotal);
    }

    [Fact]
    public void RagChatAgent_reset_conversation_state_returns_mode_to_auto()
    {
        var settings = new AppSettings { ActiveMode = "strict" };
        var llm = new OpenAiLlmClient();
        var sut = new RagChatAgent(
            new ApiClient(),
            LlmProviderFactory.CreateLocal(llm, settings));
        sut.ApplySettings(settings);

        var memField = typeof(RagChatAgent).GetField("_mem", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(memField);
        var modeField = typeof(RagChatAgent).GetField("_activeMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(modeField);

        var mem = Assert.IsType<ToolMemory>(memField!.GetValue(sut));
        Assert.Equal("strict", mem.LastMode);
        Assert.Equal("strict", modeField!.GetValue(sut) as string);

        sut.ResetConversationState();

        Assert.Equal("auto", mem.LastMode);
        Assert.Equal("auto", modeField.GetValue(sut) as string);
    }
}
