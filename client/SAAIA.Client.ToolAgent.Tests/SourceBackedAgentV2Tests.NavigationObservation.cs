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

    [Fact]
    public void NavigationObservation_UsesWorkingEvidenceCapacityForNamedAnchors()
    {
        var anchors = Enumerable.Range(1, 5)
            .Select(index => new
            {
                docId = "doc-index",
                docName = "index.pdf",
                docPath = "Knowledge/index.pdf",
                categoryPath = "Knowledge",
                kind = "navigation_entry",
                label = "Named anchor " + index,
                revisionId = "revision-index",
                targetChunkId = "chunk-" + index,
                targetAnchorId = "anchor-" + index,
                targetPageStart = index,
                targetPageEnd = index
            })
            .ToArray();
        var results = Results(
            "documents.navigation",
            new
            {
                navigationOnly = true,
                total = 5,
                limit = 5,
                offset = 0,
                nextOffset = 5,
                items = anchors
            });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Find named anchors.");
        var options = Options() with
        {
            MaximumObservationItems = 2,
            MaximumWorkingEvidenceItems = 4,
            MaximumWorkingExcerptCharacters = 80
        };

        using var compact = JsonDocument.Parse(SourceBackedAgentObservationCompactor.Build(
            "documents_navigation",
            results.Items,
            bundle,
            1,
            options));

        Assert.Equal(4, compact.RootElement.GetProperty("shownEvidenceCount").GetInt32());
        Assert.Equal(5, compact.RootElement.GetProperty("availableEvidenceCount").GetInt32());
        Assert.True(compact.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(4, compact.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.All(
            compact.RootElement.GetProperty("evidence").EnumerateArray(),
            static item =>
            {
                Assert.True(item.GetProperty("orientationOnly").GetBoolean());
                Assert.StartsWith(
                    "chunk-",
                    item.GetProperty("chunkId").GetString(),
                    StringComparison.Ordinal);
                Assert.Equal(
                    "revision-index",
                    item.GetProperty("revisionId").GetString());
            });
    }
}
