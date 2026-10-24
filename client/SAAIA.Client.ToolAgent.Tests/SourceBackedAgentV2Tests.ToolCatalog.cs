using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Theory]
    [InlineData("rag.search", "rag_search")]
    [InlineData("rag.multi_search", "rag_search")]
    [InlineData("documents.navigation", "documents_navigation")]
    [InlineData("documents.content_cards", "documents_content_cards")]
    [InlineData("documents.context", "documents_context")]
    public void ToolCatalog_MapsCompatibleRouterToolsToNativeAgentTools(
        string routerTool,
        string nativeTool)
    {
        var mapped = SourceBackedAgentToolCatalog.TryResolveExternalName(
            routerTool,
            out var resolved);

        Assert.True(mapped);
        Assert.Equal(nativeTool, resolved);
    }

    [Fact]
    public void ToolCatalog_ExposesFourNativeSourceBackedToolsWithJsonSchemas()
    {
        var tools = SourceBackedAgentToolCatalog.Build();

        Assert.Equal(
            new[] { "documents_content_cards", "documents_context", "documents_navigation", "rag_search" },
            tools.Select(static tool => tool.Name).OrderBy(static name => name).ToArray());
        var search = Assert.Single(tools, static tool => tool.Name == "rag_search");
        Assert.Equal("object", search.Parameters.GetProperty("type").GetString());
        Assert.True(search.Parameters.GetProperty("properties").TryGetProperty("query", out _));
        Assert.True(search.Parameters.GetProperty("properties").TryGetProperty("queries", out _));
        Assert.Equal(2, search.Parameters.GetProperty("anyOf").GetArrayLength());
        Assert.Contains(
            "pas sa seule position",
            search.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Exclure les jours",
            search.Parameters
                .GetProperty("properties")
                .GetProperty("query")
                .GetProperty("description")
                .GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var inventory = Assert.Single(tools, static tool => tool.Name == "documents_content_cards");
        Assert.True(inventory.Parameters.GetProperty("properties").TryGetProperty("categoryPath", out _));
        Assert.True(inventory.Parameters.GetProperty("properties").TryGetProperty("q", out _));
        Assert.True(inventory.Parameters.GetProperty("properties").TryGetProperty("limit", out _));
        Assert.Contains(
            "opaque",
            inventory.Parameters
                .GetProperty("properties")
                .GetProperty("docId")
                .GetProperty("description")
                .GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "chemin",
            inventory.Parameters
                .GetProperty("properties")
                .GetProperty("docPath")
                .GetProperty("description")
                .GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inventaire pagine", inventory.Description, StringComparison.OrdinalIgnoreCase);

        var catalogScopedTools = SourceBackedAgentToolCatalog.Build(
            allowedCategoryPaths: new[] { "Cuisine", "Medical/PDF" });
        foreach (var scopedTool in catalogScopedTools.Where(static tool =>
                     tool.Parameters
                         .GetProperty("properties")
                         .TryGetProperty("categoryPath", out _)))
        {
            var allowedPaths = scopedTool.Parameters
                .GetProperty("properties")
                .GetProperty("categoryPath")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray();
            Assert.Equal(
                new[] { "Cuisine", "Medical/PDF" },
                allowedPaths);
        }
        Assert.Contains("Sans q", inventory.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nextOffset", inventory.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decides librement", inventory.Description, StringComparison.OrdinalIgnoreCase);
    }
}
