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
    public void ObservationCompactor_ContextBatchUsesTheWorkingEvidenceBudget()
    {
        var options = Options() with
        {
            MaximumObservationItems = 10,
            MaximumWorkingEvidenceItems = 40
        };

        Assert.Equal(
            40,
            SourceBackedAgentObservationCompactor.ResolveMaximumItems(
                "documents_context_batch",
                options));
    }

    [Fact]
    public void ObservationCompactor_ContextBatchKeepsEveryResolvedAnchorBeforeItsNeighbors()
    {
        static EvidenceItem Evidence(
            string evidenceId,
            int toolSequence,
            int rank,
            string? sourceAnchorEvidenceId = null)
            => new(
                evidenceId,
                "document_context",
                "documents.context",
                string.Empty,
                "doc-1",
                "catalogue.pdf",
                "Knowledge/catalogue.pdf",
                null,
                "revision-1",
                rank,
                rank,
                "chunk-" + evidenceId,
                "Passage " + evidenceId,
                "passage " + evidenceId,
                null,
                rank,
                "Knowledge",
                "fr",
                "fr",
                "high",
                null,
                string.IsNullOrWhiteSpace(sourceAnchorEvidenceId)
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>
                    {
                        ["sourceAnchorEvidenceId"] = sourceAnchorEvidenceId,
                        ["sourceAnchorLabel"] = "Option " + sourceAnchorEvidenceId
                    },
                new Dictionary<string, string>
                {
                    ["tool_sequence"] = toolSequence.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                },
                Array.Empty<string>(),
                Array.Empty<string>());

        var bundle = new EvidenceBundle(
            "bundle-context-batch",
            "Question generique",
            new[]
            {
                Evidence("neighbor-1", 1, 1),
                Evidence("anchor-1", 1, 2, "navigation-1"),
                Evidence("neighbor-2", 2, 1),
                Evidence("anchor-2", 2, 2, "navigation-2"),
                Evidence("neighbor-3", 3, 1),
                Evidence("anchor-3", 3, 2, "navigation-3")
            },
            Array.Empty<SourceBackedTraceEvent>());

        var selected = SourceBackedAgentObservationCompactor.SelectEvidenceItems(
            bundle,
            firstToolSequence: 1,
            resultCount: 3,
            maximumItems: 3);

        Assert.Equal(
            new[] { "anchor-1", "anchor-2", "anchor-3" },
            selected.Select(static item => item.EvidenceId));
        Assert.All(selected, static item =>
            Assert.True(item.SelectionHints.ContainsKey("sourceAnchorEvidenceId")));
    }
}
