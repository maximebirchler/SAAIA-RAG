using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class PreUiRegressionSafetyNetTests
{
    [Fact]
    public void Default_category_does_not_filter_normal_chat_questions()
    {
        Assert.True(string.IsNullOrEmpty(SAAIA.Client.WinUI.Services.ClientDefaults.DefaultCategory));
    }

    [Fact]
    public void Writer_prompt_forbids_common_knowledge_when_general_chat_is_disabled()
    {
        var prompt = PromptCatalog.BuildWriterSystemPrompt(
            language: "fr",
            mode: "strict",
            style: "plain",
            allowGeneralChat: false);

        Assert.Contains("General-chat allowed", prompt);
        Assert.Contains("never answer from common knowledge", prompt);
        Assert.Contains("available sources are insufficient", prompt);
        Assert.Contains("Do not fill gaps with plausible knowledge", prompt);
        Assert.Contains("ingredients, quantities, times, temperatures", prompt);
        Assert.Contains("source-backed alternatives", prompt);
        Assert.Contains("Do not infer culinary suitability from a generic list", prompt);
        Assert.Contains("If only ingredients are present, do not invent preparation steps", prompt);
    }

    [Theory]
    [InlineData("Combien de documents n'ont pas de résumé ?")]
    [InlineData("Liste les documents sans résumé")]
    [InlineData("How many documents do not have a stored summary?")]
    [InlineData("List missing summaries")]
    [InlineData("Combien de documents ont un résumé stocké ?")]
    [InlineData("How many documents have a stored summary?")]
    [InlineData("Cuantos documentos tienen un resumen almacenado ?")]
    [InlineData("Quantos documentos tem um resumo armazenado ?")]
    [InlineData("Wie viele Dokumente haben eine gespeicherte Zusammenfassung ?")]
    [InlineData("Quanti documenti hanno un riassunto memorizzato ?")]
    public void Summary_status_requests_do_not_trigger_document_reference_clarification(string message)
    {
        var analysis = DocumentRefResolver.Analyze(
            message,
            lastFocusedDocument: null,
            lastListedDocuments: null);

        Assert.False(analysis.IsContentRequest);
        Assert.False(analysis.NeedsClarification);
        Assert.Null(analysis.ResolvedDocRef);
    }

    [Fact]
    public void Documents_categories_tool_remains_declared_and_executable()
    {
        Assert.True(ToolManifest.KnownToolNames.Contains("documents.categories"));
        Assert.Contains("documents.categories", ToolAgentOrchestrator.GetExecutableToolNamesForTests());
    }

    [Fact]
    public void Categories_replay_hides_aliases_in_english()
    {
        using var doc = JsonDocument.Parse("""
        {
          "total": 3,
          "items": [
            {
              "ordinal": 2,
              "displayOrder": 2,
              "path": "Programmation",
              "name": "Programmation",
              "totalDocuments": 1,
              "aliases": ["Programming", "Programmation"]
            }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("categories", doc.RootElement, "en");

        Assert.Contains("Categories present on the server:", rendered);
        Assert.Contains("2. Programmation (1)", rendered);
        Assert.DoesNotContain("aliases:", rendered);
        Assert.DoesNotContain("Programming, Programmation", rendered);
    }

    [Fact]
    public void Summary_status_count_replay_renders_in_english()
    {
        using var doc = JsonDocument.Parse("""
        {
          "total": 2,
          "missingStored": 2,
          "staleStored": 0
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_count", doc.RootElement, "en");

        Assert.Contains("There are currently 2 indexed document(s) without a stored summary.", rendered);
    }


    [Fact]
    public void Summary_present_count_replay_renders_in_english()
    {
        using var doc = JsonDocument.Parse("""
        {
          "mode": "present",
          "total": 2
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_count", doc.RootElement, "en");

        Assert.Contains("There are currently 2 indexed document(s) with a stored summary.", rendered);
    }

    [Fact]
    public void Summary_present_list_replay_renders_clickable_entries()
    {
        using var doc = JsonDocument.Parse("""
        {
          "mode": "present",
          "total": 2,
          "items": [
            { "docPath": "General/Overview.pdf", "summaryState": "fresh" },
            { "docPath": "Programmation/Mettler/MettlerToledo_IND570.pdf", "summaryState": "fresh" }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_list", doc.RootElement, "en");

        Assert.Contains("Here is the list of documents with a stored summary:", rendered);
        Assert.Contains("[[open|General/Overview.pdf|1|Overview.pdf]]", rendered);
        Assert.Contains("[[open|Programmation/Mettler/MettlerToledo_IND570.pdf|1|MettlerToledo_IND570.pdf]]", rendered);
    }

    [Fact]
    public void Summary_status_list_replay_marks_stale_items()
    {
        using var doc = JsonDocument.Parse("""
        {
          "total": 2,
          "items": [
            { "docPath": "General/Overview.pdf", "summaryState": "missing" },
            { "docPath": "Programmation/Mettler/MettlerToledo_IND570.pdf", "summaryState": "stale" }
          ]
        }
        """);

        var rendered = ToolAgentOrchestrator.RenderDeterministicInventoryFromData("summary_status_list", doc.RootElement, "en");

        Assert.Contains("Here is the list of documents without a stored summary:", rendered);
        Assert.Contains("[[open|General/Overview.pdf|1|Overview.pdf]]", rendered);
        Assert.Contains("[[open|Programmation/Mettler/MettlerToledo_IND570.pdf|1|MettlerToledo_IND570.pdf]] [stale]", rendered);
    }


    [Theory]
    [InlineData("qui es-tu ?")]
    [InlineData("who are you ?")]
    [InlineData("2+2 ?")]
    public void General_conversation_messages_do_not_look_like_summary_status_inventory_requests(string message)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("LooksLikeDirectSummaryStatusRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { message, null, null };
        var handled = (bool)method!.Invoke(null, args)!;

        Assert.False(handled);
    }

    [Fact]
    public void Summary_status_snapshot_mode_defaults_to_present_for_present_tools()
    {
        using var doc = JsonDocument.Parse("""
        {
          "total": 8
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var buildMethod = typeof(ToolAgentOrchestrator).GetMethod("BuildSummaryStatusSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(buildMethod);

        var plan = new RouterPlan
        {
            ToolCalls =
            {
                new RouterPlan.ToolCall
                {
                    Name = "summary.present.count",
                    Args = JsonDocument.Parse("{}").RootElement.Clone()
                }
            }
        };

        var snapshot = (ToolMemory.SummaryStatusSnapshot)buildMethod!.Invoke(sut, new object[] { doc.RootElement, plan, "summary.present.count" })!;
        Assert.Equal("present", snapshot.Mode);
        Assert.Equal(8, snapshot.Total);
    }

}
