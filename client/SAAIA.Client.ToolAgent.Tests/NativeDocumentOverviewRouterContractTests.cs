using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class NativeDocumentOverviewRouterContractTests
{
    private const string Document =
        "ISO 19011 2011 Lignes directrices pour l'audit des systèmes de management.pdf";

    private const string Request =
        "Fais-moi une synthèse utile de `"
        + Document
        + "` : à quoi sert ce document, quelles informations il contient, "
        + "et dans quels cas je devrais le citer ?";

    private const string SpecializedArguments = """
        {
          "document": "ISO 19011 2011 Lignes directrices pour l'audit des systèmes de management.pdf",
          "facets": [
            "À quoi sert ce document ?",
            "Quelles informations il contient ?",
            "Dans quels cas je devrais le citer ?"
          ],
          "requestedPointCount": 3,
          "sampleCount": 4,
          "questionFocus": "content"
        }
        """;

    [Fact]
    public void Specialized_overview_schema_is_the_preregistered_bounded_contract()
    {
        var tool = ToolAgentOrchestrator
            .BuildNativeDocumentOverviewRouteToolForTests();

        Assert.Equal("submit_document_overview_route", tool.Name);
        var required = tool.Parameters.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "document", "facets", "requestedPointCount",
                "sampleCount", "questionFocus"
            },
            required);
        var facets = tool.Parameters.GetProperty("properties")
            .GetProperty("facets");
        Assert.Equal(2, facets.GetProperty("minItems").GetInt32());
        Assert.Equal(5, facets.GetProperty("maxItems").GetInt32());
        Assert.False(tool.Parameters.GetProperty(
            "additionalProperties").GetBoolean());
    }

    [Fact]
    public void Specialized_overview_maps_to_the_existing_canonical_router_plan()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            Request,
            "submit_document_overview_route",
            SpecializedArguments);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("multi_item", mission.PlanKind);
        Assert.Equal("documents_overview", mission.InitialCapability);
        Assert.Equal("document_overview_claim", mission.AtomicEvidenceType);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.Equal("explicit_set", mission.SelectionPolicy);
        Assert.Equal(3, mission.AtomicEvidenceCount);
        Assert.Equal(Document, mission.RequestedDocumentName);

        var call = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.summarize_live", call.Name);
        Assert.Equal(Document, call.Args.GetProperty("docRef").GetString());
        Assert.Equal(3, call.Args.GetProperty(
            "requestedPointCount").GetInt32());
        Assert.Equal(4, call.Args.GetProperty("sampleCount").GetInt32());
        Assert.Equal(
            new[]
            {
                "À quoi sert ce document ?",
                "Quelles informations il contient ?",
                "Dans quels cas je devrais le citer ?"
            },
            call.Args.GetProperty("overviewFacets")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public void Specialized_overview_has_plan_parity_with_the_general_contract()
    {
        var specialized = ToolAgentOrchestrator
            .TryBuildNativeRouterPlanForTests(
                new ToolMemory(),
                Request,
                "submit_document_overview_route",
                SpecializedArguments);
        var general = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            Request,
            "submit_source_backed_route",
            """
            {
              "tool": "overview",
              "intent": "summary_doc",
              "query": "",
              "sourceItemType": "document_overview_claim",
              "sourceItemMode": "content_claim",
              "selectionPolicy": "explicit_set",
              "document": "ISO 19011 2011 Lignes directrices pour l'audit des systèmes de management.pdf",
              "pool": 4,
              "count": 3,
              "useFocusedDocument": false,
              "questionFocus": "content",
              "overviewFacets": [
                "À quoi sert ce document ?",
                "Quelles informations il contient ?",
                "Dans quels cas je devrais le citer ?"
              ]
            }
            """);

        Assert.True(specialized.Accepted, specialized.FailureReason);
        Assert.True(general.Accepted, general.FailureReason);
        Assert.Equal(general.Plan.Intent, specialized.Plan.Intent);
        Assert.Equal(
            general.Plan.SourceBackedMission?.PlanKind,
            specialized.Plan.SourceBackedMission?.PlanKind);
        Assert.Equal(
            general.Plan.SourceBackedMission?.AtomicEvidenceCount,
            specialized.Plan.SourceBackedMission?.AtomicEvidenceCount);
        Assert.Equal(
            Assert.Single(general.Plan.ToolCalls).Args.GetRawText(),
            Assert.Single(specialized.Plan.ToolCalls).Args.GetRawText());
    }

    [Theory]
    [InlineData(
        "Other.pdf",
        "native_document_overview_document_not_explicit")]
    [InlineData(
        Document,
        "native_document_overview_contract_invalid")]
    public void Specialized_overview_rejects_invalid_transport(
        string document,
        string expectedFailure)
    {
        var arguments = $$"""
            {
              "document": "{{document}}",
              "facets": ["Même facette", "Même facette"],
              "requestedPointCount": 3,
              "sampleCount": 4,
              "questionFocus": "content"
            }
            """;

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            Request,
            "submit_document_overview_route",
            arguments);

        Assert.False(result.Accepted);
        Assert.Equal(expectedFailure, result.FailureReason);
    }

    [Fact]
    public void Invalid_specialized_overview_falls_back_to_only_the_general_route()
    {
        Assert.Equal(
            "submit_source_backed_route",
            ToolAgentOrchestrator
                .ResolveNativeRouterRepairRouteToolNameForTests(
                    "submit_document_overview_route",
                    "submit_document_overview_route"));
        Assert.Equal(
            "submit_source_backed_grid_route",
            ToolAgentOrchestrator
                .ResolveNativeRouterRepairRouteToolNameForTests(
                    "submit_source_backed_grid_route",
                    "submit_source_backed_grid_route"));
    }
}
