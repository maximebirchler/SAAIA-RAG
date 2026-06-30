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

    [Fact]
    public void Admin_summary_submit_is_not_available_as_a_runtime_llm_tool_anymore()
    {
        Assert.False(ToolManifest.KnownToolNames.Contains("admin.summary.submit"));
        Assert.DoesNotContain("admin.summary.submit", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
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
    [InlineData("documents.navigation")]
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
    [InlineData("admin.summary.status")]
    [InlineData("admin.summary.delete")]
    [InlineData("admin.summary.generate")]
    [InlineData("admin.audit")]
    [InlineData("documents.empty_count")]
    [InlineData("documents.empty_list")]
    [InlineData("documents.extraction_quality")]
    [InlineData("documents.extraction_pages")]
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
    public void Router_conversation_manifest_is_compact_and_user_only()
    {
        using var doc = JsonDocument.Parse(ToolManifest.BuildRouterConversationManifestJson());
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.False(tool.TryGetProperty("access", out _)));
        Assert.True(ToolManifest.BuildRouterConversationManifestJson().Length < ToolManifest.BuildConversationManifestJson().Length);
    }

    [Fact]
    public void Compact_router_prompt_keeps_core_contract_under_budget()
    {
        var prompt = PromptCatalog.BuildCompactRouterSystemPrompt(
            ToolManifest.BuildRouterConversationManifestJson(),
            ToolManifest.ConversationToolbookText);

        Assert.Contains("meta.set_style", prompt, StringComparison.Ordinal);
        Assert.Contains("meta.set_mode", prompt, StringComparison.Ordinal);
        Assert.Contains("rag.multi_search", prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve explicit distinct slots", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < 7000, $"Compact router prompt is too large: {prompt.Length}");
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
        Assert.Contains("preserve those distinct terms", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_version_is_v3_1()
    {
        using var manifest = JsonDocument.Parse(ToolManifest.BuildManifestJson());
        Assert.Equal("v3.1", manifest.RootElement.GetProperty("version").GetString());

        using var conversationManifest = JsonDocument.Parse(ToolManifest.BuildConversationManifestJson());
        Assert.Equal("v3.1", conversationManifest.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Live_summary_manifest_distinguishes_response_language_from_document_language()
    {
        using var manifest = JsonDocument.Parse(ToolManifest.BuildManifestJson());
        var tool = manifest.RootElement.GetProperty("tools").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == "rag.summarize_live");
        var schema = tool.GetProperty("args_schema");

        Assert.True(schema.TryGetProperty("responseLanguage", out _));
        Assert.True(schema.TryGetProperty("docLanguage", out _));
        Assert.Equal("auto|fr|en|es|pt|de|it", schema.GetProperty("language").GetString());
    }

    [Fact]
    public void Admin_summary_submit_uses_document_language_not_ui_language()
    {
        var explicitDocLanguage = ToolAgentOrchestrator.ResolveAdminSummarySubmitDocLanguageForTests(
            """{"docLanguage":"de","language":"fr"}""");
        var arbitraryDocLanguage = ToolAgentOrchestrator.ResolveAdminSummarySubmitDocLanguageForTests(
            """{"docLanguage":"nl","language":"fr"}""");
        var missingDocLanguage = ToolAgentOrchestrator.ResolveAdminSummarySubmitDocLanguageForTests(
            """{"language":"fr"}""");

        Assert.Equal("de", explicitDocLanguage);
        Assert.Equal("nl", arbitraryDocLanguage);
        Assert.Equal("und", missingDocLanguage);
    }

    [Fact]
    public void Structured_router_coverage_detects_missing_requested_slots_before_retrieval()
    {
        var missing = ToolAgentOrchestrator.DetectMissingStructuredRouterSearchAxesForTests(
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr",
            "petit dejeuner",
            "repas",
            "diner",
            "dejeuner");

        Assert.Equal(new[] { "Souper", "Collation" }, missing);
    }

    [Fact]
    public void Structured_router_coverage_accepts_queries_covering_requested_slots()
    {
        var missing = ToolAgentOrchestrator.DetectMissingStructuredRouterSearchAxesForTests(
            "J'ai besoin que tu me fasses un plan de repas pour la semaine du lundi au vendredi, avec petit-dejeuner, diner, souper et gouter / collation chaque jour.",
            "fr",
            "petit-dejeuner options",
            "diner options",
            "souper options",
            "collation options");

        Assert.Empty(missing);
    }
}
