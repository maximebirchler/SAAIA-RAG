
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CatalogTransitionRegressionTests
{
    [Fact]
    public void ParsePresentedCategories_supports_catalog_value_shape_and_preserves_category_ref()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "categoryRef": "cat_003",
              "categoryPath": "Programmation",
              "canonicalName": "Programmation",
              "displayOrder": 3,
              "documentCount": 1,
              "aliases": ["Programming"]
            }
          ]
        }
        """);

        var method = typeof(ToolAgentOrchestrator).GetMethod("ParsePresentedCategories", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var categories = (System.Collections.Generic.List<ToolMemory.CategorySnapshot>)method!.Invoke(null, new object[] { doc.RootElement })!;
        var category = Assert.Single(categories);
        Assert.Equal("cat_003", category.CategoryRef);
        Assert.Equal("Programmation", category.CategoryPath);
        Assert.Contains("Programming", category.Aliases);
    }

    [Fact]
    public void Summary_status_snapshot_accepts_catalog_value_shape()
    {
        using var doc = JsonDocument.Parse("""
        {
          "value": [
            {
              "docId": "abc",
              "docPath": "ATEX/test.pdf",
              "canonicalName": "test.pdf",
              "categoryCanonicalName": "ATEX",
              "summaryState": "missing"
            }
          ],
          "totals": {
            "total": 1,
            "missingStored": 1,
            "staleStored": 0
          }
        }
        """);

        var mem = new ToolMemory();
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var method = typeof(ToolAgentOrchestrator).GetMethod("UpdateSummaryStatusSnapshotFromJson", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(sut, new object[] { doc.RootElement });

        Assert.NotNull(mem.LastSummaryStatusSnapshot);
        Assert.Equal(1, mem.LastSummaryStatusSnapshot!.Total);
        var item = Assert.Single(mem.LastSummaryStatusSnapshot.Items);
        Assert.Equal("ATEX", item.Category);
    }
}
