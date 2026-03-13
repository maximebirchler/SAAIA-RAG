using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
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
        Assert.Contains("- Dossiers de deuxième niveau : 5", rendered);
        Assert.DoesNotContain("Catalog statistics:", rendered);
    }

    [Fact]
    public void Stats_fallback_uses_requested_english_language()
    {
        using var doc = JsonDocument.Parse("""
        {
          "totalDocuments": 3,
          "totalNonEmptyFolders": 10,
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
        Assert.Contains("- Second-level folders: 5", rendered);
        Assert.DoesNotContain("Statistiques du catalogue", rendered);
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
}
