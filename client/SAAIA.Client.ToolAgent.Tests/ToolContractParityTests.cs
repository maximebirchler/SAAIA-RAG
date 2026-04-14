using System;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolContractParityTests
{
    [Fact]
    public void Manifest_and_runtime_catalogs_stay_in_sync()
    {
        var report = ToolManifest.ValidateRuntimeCatalog(ToolAgentOrchestrator.GetExecutableToolNamesForTests());
        Assert.True(report.IsValid, report.FormatErrorMessage());
    }

    [Fact]
    public void Runtime_consistency_guard_does_not_throw()
    {
        var ex = Record.Exception(ToolAgentOrchestrator.EnsureToolContractConsistency);
        Assert.Null(ex);
    }

    [Fact]
    public void Rag_categories_is_not_available_anymore()
    {
        Assert.False(ToolManifest.KnownToolNames.Contains("rag.categories"));
        Assert.DoesNotContain("rag.categories", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Fact]
    public void Admin_ingestion_reindex_is_not_available_as_a_runtime_llm_tool_anymore()
    {
        Assert.False(ToolManifest.KnownToolNames.Contains("admin.ingestion.reindex"));
        Assert.DoesNotContain("admin.ingestion.reindex", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Theory]
    [InlineData("inventory.find")]
    [InlineData("inventory.changed_since")]
    [InlineData("rag.compare")]
    [InlineData("rag.extract")]
    [InlineData("rag.cite")]
    [InlineData("rag.summarize_topic")]
    public void Intent_only_or_render_only_entries_are_not_exposed_as_runtime_tools(string toolName)
    {
        Assert.False(ToolManifest.KnownToolNames.Contains(toolName));
        Assert.DoesNotContain(toolName, ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Theory]
    [InlineData("documents.categories")]
    [InlineData("support.bundle")]
    [InlineData("diagnostic.performance")]
    [InlineData("summary.get")]
    [InlineData("summary.exists")]
    [InlineData("summary.search")]
    [InlineData("summary.status.count")]
    [InlineData("summary.status.list")]
    [InlineData("summary.present.count")]
    [InlineData("summary.present.list")]
    [InlineData("admin.summary.missing")]
    [InlineData("admin.summary.request")]
    [InlineData("admin.summary.submit")]
    [InlineData("admin.summary.status")]
    [InlineData("admin.summary.delete")]
    [InlineData("admin.summary.generate")]
    [InlineData("admin.audit")]
    [InlineData("documents.empty_count")]
    [InlineData("documents.empty_list")]
    public void Sensitive_tools_are_declared_and_executable(string toolName)
    {
        Assert.True(ToolManifest.KnownToolNames.Contains(toolName));
        Assert.Contains(toolName, ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Fact]
    public void Admin_tools_are_also_known_tools()
    {
        var unknownAdmins = ToolManifest.AdminToolNames
            .Where(x => !ToolManifest.KnownToolNames.Contains(x))
            .ToArray();

        Assert.True(unknownAdmins.Length == 0, "Unknown admin tools: " + string.Join(", ", unknownAdmins));
    }

    [Fact]
    public void Conversation_manifest_is_strictly_user_only()
    {
        using var doc = JsonDocument.Parse(ToolManifest.BuildConversationManifestJson());
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.Equal("user", tool.GetProperty("access").GetString()));
    }

    [Fact]
    public void Router_prompt_mentions_meta_set_style()
    {
        var prompt = PromptCatalog.BuildRouterSystemPrompt(
            ToolManifest.BuildConversationManifestJson(),
            ToolManifest.ConversationToolbookText);

        Assert.Contains("meta.set_style", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_prompt_mentions_meta_set_mode()
    {
        var prompt = PromptCatalog.BuildRouterSystemPrompt(
            ToolManifest.BuildConversationManifestJson(),
            ToolManifest.ConversationToolbookText);

        Assert.Contains("meta.set_mode", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_version_is_v3_0()
    {
        using var manifest = JsonDocument.Parse(ToolManifest.BuildManifestJson());
        Assert.Equal("v3.0", manifest.RootElement.GetProperty("version").GetString());

        using var conversationManifest = JsonDocument.Parse(ToolManifest.BuildConversationManifestJson());
        Assert.Equal("v3.0", conversationManifest.RootElement.GetProperty("version").GetString());
    }
}
