using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{

    [Fact]
    public void ResolveRetriever_labels_standard_reference_document_name_route()
    {
        var match = TestMatch(
            "ISO 13849-1 safety-related parts of control systems.",
            embeddingBasis: "standard_reference_document_name_v1");

        Assert.Equal("standard_reference_document_name", RetrievalNavigation.ResolveRetriever(match));
    }

    [Fact]
    public void ResolveRetriever_labels_reference_version_operand_route()
    {
        var match = TestMatch(
            "EN 1005-3 2002 prA1 amendment text.",
            embeddingBasis: "reference_version_operand_document_name_v1");

        Assert.Equal("reference_version_operand_document_name", RetrievalNavigation.ResolveRetriever(match));
    }

    [Fact]
    public void ResolveRetriever_maps_title_and_navigation_routes_explicitly()
    {
        var titleRoute = new RagMatch(0.90, "doc-a", "Ops/Guide.pdf", "Guide.pdf", 5, 5, "chunk-a", 3, "Release checklist", 1, "hash-a", "Release checklist", "title_anchor_route_v1", null, null, "Release", "Release", "section", null, null, null);
        var navigationRoute = titleRoute with { EmbeddingBasis = "navigation_route_v1" };

        Assert.Equal("title_anchor_route", RetrievalNavigation.ResolveRetriever(titleRoute));
        Assert.Equal("navigation_route", RetrievalNavigation.ResolveRetriever(navigationRoute));
    }

    [Fact]
    public void ResolveRetriever_maps_fuzzy_title_lead_separately_from_dense_qdrant()
    {
        var fuzzy = TestMatch(
            text: "Alpha beta target body.",
            embedText: "Matched fuzzy_title_lead: Alpha Beta\nAlpha beta target body.",
            embeddingBasis: "fuzzy_title_lead_v1",
            chunkType: "section_window_v1");
        var localTitle = TestMatch(
            text: "Alpha beta target body.",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1");

        Assert.Equal("fuzzy_title_lead", RetrievalNavigation.ResolveRetriever(fuzzy));
        Assert.Equal("local_title_token_route", RetrievalNavigation.ResolveRetriever(localTitle));
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(fuzzy));
    }
}
