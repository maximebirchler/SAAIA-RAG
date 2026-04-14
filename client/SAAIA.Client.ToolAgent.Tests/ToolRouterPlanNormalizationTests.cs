using System;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ToolRouterPlanNormalizationTests
{
    [Theory]
    [InlineData("set_mode", "meta.set_mode")]
    [InlineData("documents.search", "inventory.find")]
    [InlineData("inventory.changed_since", "inventory.changed_since")]
    [InlineData("inventory.health", "inventory.health")]
    public void Normalize_router_intent_matches_v3_contract(string rawIntent, string expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.NormalizeRouterIntentForTests(rawIntent));
    }

    [Fact]
    public void Infer_intent_uses_documents_search_as_inventory_find()
    {
        var toolCall = new RouterPlan.ToolCall
        {
            Name = "documents.search",
            Args = ParseArgs("""{"q":"mettler"}""")
        };

        Assert.Equal("inventory.find", ToolAgentOrchestrator.InferIntentFromToolCallsForTests(toolCall));
    }

    [Fact]
    public void Infer_intent_uses_documents_list_with_changedSince_as_inventory_changed_since()
    {
        var toolCall = new RouterPlan.ToolCall
        {
            Name = "documents.list",
            Args = ParseArgs("""{"changedSince":"2026-04-10T00:00:00Z"}""")
        };

        Assert.Equal("inventory.changed_since", ToolAgentOrchestrator.InferIntentFromToolCallsForTests(toolCall));
    }

    [Fact]
    public void Normalize_tool_args_preserves_categoryRef_and_changedSince_for_documents_list()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "documents.list",
            """{"categoryRef":"cat_003","changedSince":"2026-04-10T12:34:56Z","limit":20,"offset":5}""");

        Assert.Equal("cat_003", normalized.GetProperty("categoryRef").GetString());
        Assert.Equal("2026-04-10T12:34:56.0000000+00:00", normalized.GetProperty("changedSince").GetString());
        Assert.Equal(20, normalized.GetProperty("limit").GetInt32());
        Assert.Equal(5, normalized.GetProperty("offset").GetInt32());
    }

    [Fact]
    public void Normalize_tool_args_preserves_categoryRef_for_documents_search()
    {
        var normalized = ToolAgentOrchestrator.NormalizeToolArgsForTests(
            "documents.search",
            """{"q":"guidance","categoryRef":"cat_002"}""");

        Assert.Equal("guidance", normalized.GetProperty("q").GetString());
        Assert.Equal("cat_002", normalized.GetProperty("categoryRef").GetString());
    }

    [Fact]
    public void Sanitize_tool_calls_caps_rag_calls_to_five()
    {
        var calls = Enumerable.Range(1, 7)
            .Select(i => new RouterPlan.ToolCall
            {
                Name = i % 2 == 0 ? "rag.multi_search" : "rag.search",
                Args = ParseArgs("""{"query":"test"}""")
            })
            .ToArray();

        var sanitized = ToolAgentOrchestrator.SanitizeToolCallsForTests(calls);

        Assert.Equal(5, sanitized.Length);
        Assert.All(sanitized, call => Assert.StartsWith("rag.", call.Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Passe en mode strict", "strict")]
    [InlineData("mets le mode standard", "standard")]
    [InlineData("mode auto", "auto")]
    public void Localized_strings_detect_mode_change(string text, string expectedMode)
    {
        var detected = LocalizedStrings.TryDetectModePreferenceChange(text, out var mode);

        Assert.True(detected);
        Assert.Equal(expectedMode, mode);
    }

    private static JsonElement ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
