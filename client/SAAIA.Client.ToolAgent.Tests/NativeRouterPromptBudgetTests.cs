using System.Text.Json;
using System.Reflection;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class NativeRouterPromptBudgetTests
{
    [Fact]
    public void Native_router_classifier_uses_semantic_labels_without_retrieval_arguments()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterClassifierSystemPromptForTests();
        var tools =
            ToolAgentOrchestrator.BuildNativeRouterClassifierToolsForTests();

        Assert.True(
            prompt.Length < 1_200,
            $"Native classifier prompt is too large: {prompt.Length} characters.");
        Assert.Equal(
            new[]
            {
                "submit_document_overview_route",
                "submit_source_backed_route",
                "submit_source_backed_grid_route",
                "submit_operational_route"
            },
            tools.Select(static tool => tool.Name));
        Assert.Contains(
            "Acceptable evidence forms",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "named document",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "one axis and is never a grid",
            prompt,
            StringComparison.Ordinal);
        Assert.All(tools, static tool =>
        {
            Assert.Empty(tool.Parameters
                .GetProperty("properties")
                .EnumerateObject());
            Assert.Empty(tool.Parameters
                .GetProperty("required")
                .EnumerateArray());
        });
    }

    [Fact]
    public void Native_router_classifier_distinguishes_source_resolvable_answers_from_user_preferences()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterClassifierSystemPromptForTests();
        var classifierTools =
            ToolAgentOrchestrator.BuildNativeRouterClassifierToolsForTests();
        var routerTools =
            ToolAgentOrchestrator.BuildNativeRouterToolsForTests();

        Assert.Contains(
            "answer candidates, not user choices",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "truth, status, applicability or value",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "sources or conversation cannot supply",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "answer candidates",
            Assert.Single(classifierTools, static tool =>
                tool.Name == "submit_source_backed_route").Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate answers",
            Assert.Single(routerTools, static tool =>
                tool.Name == "request_user_clarification").Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            classifierTools,
            static tool => tool.Name == "request_user_clarification");
    }

    [Theory]
    [InlineData("submit_document_overview_route", true)]
    [InlineData("submit_source_backed_grid_route", true)]
    [InlineData("submit_source_backed_route", true)]
    [InlineData("request_user_clarification", false)]
    [InlineData("submit_operational_route", true)]
    [InlineData("defer_to_general_router", false)]
    [InlineData("", false)]
    public void Native_router_classifier_accepts_only_known_semantic_labels(
        string toolName,
        bool expected)
    {
        var accepted =
            ToolAgentOrchestrator.TryResolveNativeRouterClassifierSelectionForTests(
                toolName,
                out var selected);

        Assert.Equal(expected, accepted);
        Assert.Equal(expected ? toolName : string.Empty, selected);
    }

    [Theory]
    [InlineData("submit_document_overview_route")]
    [InlineData("submit_source_backed_route")]
    [InlineData("submit_source_backed_grid_route")]
    public void Native_router_documentary_second_stage_exposes_selected_family_and_missing_input(
        string selectedRouteToolName)
    {
        Assert.Equal(
            new[] { selectedRouteToolName, "request_missing_user_input" },
            ToolAgentOrchestrator.BuildNativeRouterSecondStageToolNamesForTests(
                selectedRouteToolName,
                classifierAccepted: true));
    }

    [Fact]
    public void Native_router_operational_second_stage_keeps_clarification_available()
    {
        Assert.Equal(
            new[]
            {
                "submit_operational_route",
                "request_user_clarification"
            },
            ToolAgentOrchestrator.BuildNativeRouterSecondStageToolNamesForTests(
                "submit_operational_route",
                classifierAccepted: true));
    }

    [Fact]
    public void Native_router_second_stage_keeps_all_families_when_classifier_is_invalid()
    {
        Assert.Equal(
            new[]
            {
                "submit_document_overview_route",
                "submit_source_backed_route",
                "submit_source_backed_grid_route",
                "request_missing_user_input",
                "request_user_clarification",
                "submit_operational_route"
            },
            ToolAgentOrchestrator.BuildNativeRouterSecondStageToolNamesForTests(
                string.Empty,
                classifierAccepted: false));
    }

    [Theory]
    [InlineData("submit_document_overview_route", "original order")]
    [InlineData("submit_source_backed_grid_route", "Grid intake never formulates a search query")]
    [InlineData("submit_source_backed_route", "subjective wording")]
    public void Native_router_documentary_second_stage_prompt_is_evidence_first(
        string routeToolName,
        string requiredMarker)
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                routeToolName);

        Assert.True(
            prompt.Length < 1_800,
            $"Specialized router prompt is too large: {prompt.Length} characters.");
        Assert.Contains(requiredMarker, prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Call exactly one available route function",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "proposed work family",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "evidence-first",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "unnamed user-designated documents",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "request_user_clarification",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_operational_prompt_keeps_pre_tool_clarification_guard()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "submit_operational_route");

        Assert.True(prompt.Length < 1_800);
        Assert.Contains("inventory.count=documents.count", prompt);
        Assert.Contains("request_user_clarification", prompt);
        Assert.Contains("sources or conversation cannot supply", prompt);
    }

    [Theory]
    [InlineData(null, 256, 90_000)]
    [InlineData(2_000, 256, 97_600)]
    [InlineData(8_000, 256, 180_000)]
    public void Native_router_timeout_budgets_prompt_evaluation_and_generation(
        int? inputTokens,
        int maximumOutputTokens,
        int expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            ToolAgentOrchestrator.ResolveNativeRouterTimeoutMsForTests(
                inputTokens,
                maximumOutputTokens));
    }

    [Fact]
    public void Native_router_grid_prompt_excludes_row_header_from_value_columns_and_count()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests();

        Assert.Contains(
            "rowHeader names only",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("the row axis", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "count = rows.length * columns.length",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_grid_prompt_requests_a_native_source_object_before_placement()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests();

        Assert.Contains(
            "answer-bearing item class inside source documents",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "read and cited before assignment to a cell",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Swapping axis labels must not change it",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Axis labels are plain visible text",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Grid intake never formulates a search",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "evidence-grounded search can happen after this observation",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_non_grid_prompt_preserves_open_plural_as_a_comparison_set()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "submit_source_backed_route");

        Assert.Contains(
            "plural or open collective",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "small comparison set of 2 or 3",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("selectionPolicy", prompt, StringComparison.Ordinal);
        Assert.Contains("open_set", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "target document can be singular",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_named_target_content_contract_separates_target_from_answer_units()
    {
        var specializedPrompt =
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "submit_source_backed_route");
        var generalPrompt =
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests();

        foreach (var prompt in new[] { specializedPrompt, generalPrompt })
        {
            var normalizedPrompt = string.Join(
                " ",
                prompt.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries));
            Assert.Contains(
                "target document can be singular while the answer-bearing content claims are plural",
                normalizedPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "For internal information about a named object, including a request that first asks to find that object, use content_claim",
                normalizedPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "use overview with open_set and count 2 or 3",
                normalizedPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "Use named_item with single_item only when the output unit is the named object itself",
                normalizedPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "even when the final answer will describe it",
                normalizedPrompt,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Native_router_exposes_distinct_compact_semantic_route_contracts()
    {
        var prompt =
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests();
        var tools = ToolAgentOrchestrator.BuildNativeRouterToolsForTests();

        Assert.Equal(
            new[]
            {
                "submit_document_overview_route",
                "submit_source_backed_route",
                "submit_source_backed_grid_route",
                "request_missing_user_input",
                "request_user_clarification",
                "submit_operational_route"
            },
            tools.Select(static tool => tool.Name));
        Assert.True(
            prompt.Length < 4_000,
            $"Native router system prompt is too large: {prompt.Length} characters.");
        Assert.True(
            tools.All(static tool => tool.Parameters.GetRawText().Length < 3_500),
            "At least one native router schema is too large: "
            + string.Join(
                ", ",
                tools.Select(static tool =>
                    $"{tool.Name}={tool.Parameters.GetRawText().Length}")));

        var sourceTool = Assert.Single(tools, static tool =>
            tool.Name == "submit_source_backed_route");
        var sourceRequired = sourceTool.Parameters
            .GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "tool", "intent", "query", "answerUnitType",
                "answerUnitMode", "selectionPolicy",
                "useFocusedDocument", "questionFocus",
                "namedReferenceKind", "document",
                "pool", "count"
            },
            sourceRequired);
        var sourceProperties = sourceTool.Parameters.GetProperty("properties");
        Assert.False(sourceProperties.TryGetProperty("sourceItemType", out _));
        Assert.False(sourceProperties.TryGetProperty("sourceItemMode", out _));
        Assert.Contains(
            "answer unit",
            sourceProperties
                .GetProperty("answerUnitType")
                .GetProperty("description")
                .GetString(),
            StringComparison.OrdinalIgnoreCase);
        var answerUnitModeDescription = sourceProperties
            .GetProperty("answerUnitMode")
            .GetProperty("description")
            .GetString();
        Assert.Contains(
            "target document",
            answerUnitModeDescription,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "internal",
            answerUnitModeDescription,
            StringComparison.OrdinalIgnoreCase);

        var gridTool = Assert.Single(tools, static tool =>
            tool.Name == "submit_source_backed_grid_route");
        var gridRequired = gridTool.Parameters
            .GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("sourceItemType", gridRequired);
        Assert.Contains("rowHeader", gridRequired);
        Assert.Contains("rows", gridRequired);
        Assert.Contains("columns", gridRequired);
        Assert.DoesNotContain("query", gridRequired);
        Assert.True(gridTool.Parameters
            .GetProperty("properties")
            .TryGetProperty("scope", out _));
        Assert.False(gridTool.Parameters
            .GetProperty("properties")
            .TryGetProperty("query", out _));
        Assert.Equal(
            new[] { "cards", "navigate" },
            gridTool.Parameters
                .GetProperty("properties")
                .GetProperty("tool")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.Equal(
            40,
            gridTool.Parameters
                .GetProperty("properties")
                .GetProperty("pool")
                .GetProperty("maximum")
                .GetInt32());
        Assert.DoesNotContain("scope", gridRequired);

        var clarificationTool = Assert.Single(tools, static tool =>
            tool.Name == "request_user_clarification");
        var clarificationRequired = clarificationTool.Parameters
            .GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "understanding", "options", "executionImpact",
                "resumeRoute", "ambiguityKind"
            },
            clarificationRequired);
        Assert.Contains(
            "Do not ask about uncertainty source",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never turn nested fragments",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "named-document passage search",
            clarificationTool.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_transports_the_llm_answer_unit_mode_for_named_document_content()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "Retrouve exactement `HydraulicPumpManual.pdf` et résume les exigences internes avec leurs sources.",
            "submit_source_backed_route",
            """
            {
              "tool": "navigate",
              "intent": "summary_doc",
              "query": "exigences internes",
              "answerUnitType": "exigence documentée",
              "answerUnitMode": "content_claim",
              "selectionPolicy": "open_set",
              "document": "HydraulicPumpManual.pdf",
              "pool": 6,
              "count": 3,
              "useFocusedDocument": false,
              "questionFocus": "content"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.Equal("exigence documentée", mission.AtomicEvidenceType);
        Assert.Equal("open_set", mission.SelectionPolicy);
        Assert.Equal(3, mission.AtomicEvidenceCount);
        Assert.Equal("HydraulicPumpManual.pdf", mission.RequestedDocumentName);
    }

    [Fact]
    public void Native_router_clarification_contract_excludes_candidate_answers_resolved_by_evidence()
    {
        var generalPrompt =
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests();
        var clarificationPrompt =
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "request_user_clarification");
        var clarificationTool = Assert.Single(
            ToolAgentOrchestrator.BuildNativeRouterToolsForTests(),
            static tool => tool.Name == "request_user_clarification");

        Assert.Contains(
            "answer candidates, not user choices",
            generalPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "truth, status, applicability or value",
            clarificationPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "sources or conversation cannot supply",
            clarificationPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate answers",
            clarificationTool.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_router_keeps_search_query_distinct_from_atomic_evidence_type()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "Résume ANSI B11.0-2023 en sept points utiles à une décision.",
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "query": "ANSI B11.0-2023 exigences sécurité machines",
              "sourceItemType": "exigence décisionnelle documentée",
              "sourceItemMode": "content_claim",
              "pool": 12,
              "count": 7
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal(
            "exigence décisionnelle documentée",
            mission.AtomicEvidenceType);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);

        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.search", action.Name);
        Assert.Equal(
            "ANSI B11.0-2023 exigences sécurité machines",
            action.Args.GetProperty("query").GetString());
    }

    [Fact]
    public void Native_router_transports_named_document_overview_as_verified_live_summary()
    {
        const string request =
            "Je dois justifier une exigence : résume ANSI B11.0-2023 - Safety of Machinery.pdf en 7 points utiles pour quelqu’un qui doit prendre une décision.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "overview",
              "intent": "summary_doc",
              "query": "",
              "sourceItemType": "point de décision étayé",
              "sourceItemMode": "content_claim",
              "selectionPolicy": "explicit_set",
              "document": "ANSI B11.0-2023 - Safety of Machinery.pdf",
              "pool": 8,
              "count": 7
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("documents_overview", mission.InitialCapability);
        Assert.Equal(7, mission.AtomicEvidenceCount);

        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.summarize_live", action.Name);
        Assert.Equal(
            "ANSI B11.0-2023 - Safety of Machinery.pdf",
            action.Args.GetProperty("docRef").GetString());
        Assert.Equal(
            "evidence_overview",
            action.Args.GetProperty("strategy").GetString());
        Assert.Equal(request, action.Args.GetProperty("userRequest").GetString());
        Assert.Equal(7, action.Args.GetProperty("requestedPointCount").GetInt32());
        Assert.Equal(8, action.Args.GetProperty("sampleCount").GetInt32());
    }

    [Fact]
    public void Native_router_derives_summary_intent_from_overview_when_optional_intent_is_omitted()
    {
        const string request =
            "Résume ANSI B11.0-2023 - Safety of Machinery en 7 points utiles.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "overview",
              "query": "",
              "sourceItemType": "point étayé",
              "sourceItemMode": "content_claim",
              "selectionPolicy": "explicit_set",
              "document": "ANSI B11.0-2023 - Safety of Machinery",
              "pool": 7,
              "count": 7
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.summarize_live", action.Name);
        Assert.Equal(
            "evidence_overview",
            action.Args.GetProperty("strategy").GetString());
    }

    [Fact]
    public void Native_router_canonicalizes_redundant_overview_transport_fields()
    {
        const string request =
            "Résume ANSI B11.0-2023 - Safety of Machinery en 7 points utiles.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "overview",
              "intent": "answer",
              "query": "résumé ANSI B11.0-2023",
              "sourceItemType": "norme",
              "sourceItemMode": "named_item",
              "selectionPolicy": "single_item",
              "useFocusedDocument": false,
              "questionFocus": "content",
              "document": "ANSI B11.0-2023 - Safety of Machinery",
              "pool": 10,
              "count": 1
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("multi_item", mission.PlanKind);
        Assert.Equal(7, mission.AtomicEvidenceCount);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.Equal("explicit_set", mission.SelectionPolicy);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal(7, action.Args.GetProperty("requestedPointCount").GetInt32());
    }

    [Fact]
    public void Native_router_preserves_open_overview_claims_for_one_named_target()
    {
        const string request =
            "Donne-moi un aperçu des informations documentées dans Guide maintenance.pdf.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "overview",
              "intent": "summary_doc",
              "query": "",
              "sourceItemType": "information documentée",
              "sourceItemMode": "content_claim",
              "selectionPolicy": "open_set",
              "document": "Guide maintenance.pdf",
              "pool": 4,
              "count": 3,
              "useFocusedDocument": false,
              "questionFocus": "content"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("multi_item", mission.PlanKind);
        Assert.Equal(3, mission.AtomicEvidenceCount);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.Equal("open_set", mission.SelectionPolicy);
        Assert.False(mission.BoundedNamedDocumentExtraction);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.summarize_live", action.Name);
        Assert.Equal(3, action.Args.GetProperty("requestedPointCount").GetInt32());
        Assert.Empty(action.Args
            .GetProperty("overviewFacets")
            .EnumerateArray());
    }

    [Fact]
    public void Native_router_treats_a_bounded_set_inside_one_named_document_as_content_claims()
    {
        const string request =
            "Liste dans l'ordre les six fonctions indiquées par Guide.pdf et cite le document.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "intent": "answer",
              "query": "six fonctions dans l'ordre",
              "answerUnitType": "fonction indiquée",
              "answerUnitMode": "named_item",
              "selectionPolicy": "explicit_set",
              "useFocusedDocument": false,
              "questionFocus": "content",
              "namedReferenceKind": "document",
              "document": "Guide.pdf",
              "pool": 10,
              "count": 6
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("multi_item", mission.PlanKind);
        Assert.Equal("explicit_set", mission.SelectionPolicy);
        Assert.Equal("Guide.pdf", mission.RequestedDocumentName);
        Assert.Equal("content_claim", mission.AtomicEvidenceMode);
        Assert.True(mission.BoundedNamedDocumentExtraction);
    }

    [Fact]
    public void Native_router_preserves_named_items_for_a_bounded_set_without_a_named_document()
    {
        const string request = "Trouve quatre options documentées et cite chaque source.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "intent": "answer",
              "query": "options documentées",
              "answerUnitType": "option documentée",
              "answerUnitMode": "named_item",
              "selectionPolicy": "explicit_set",
              "useFocusedDocument": false,
              "questionFocus": "content",
              "namedReferenceKind": "none",
              "document": null,
              "pool": 8,
              "count": 4
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("named_item", mission.AtomicEvidenceMode);
        Assert.Equal(string.Empty, mission.RequestedDocumentName);
        Assert.False(mission.BoundedNamedDocumentExtraction);
    }

    [Fact]
    public void Native_router_coalesces_homogeneous_overview_calls_for_one_named_document()
    {
        const string request =
            "Fais-moi une synthèse utile de `Guide audit.pdf` : à quoi sert ce document, quelles informations il contient, et dans quels cas je devrais le citer ?";
        var result = ToolAgentOrchestrator
            .TryBuildNativeRouterPlanForCallsForTests(
                new ToolMemory(),
                request,
                new[]
                {
                    (
                        "submit_source_backed_route",
                        """
                        {"tool":"overview","intent":"summary_doc","query":"à quoi sert ce document","sourceItemType":"document purpose","sourceItemMode":"named_item","selectionPolicy":"single_item","document":"Guide audit.pdf","pool":5,"count":1,"useFocusedDocument":false,"questionFocus":"content"}
                        """),
                    (
                        "submit_source_backed_route",
                        """
                        {"tool":"overview","intent":"summary_doc","query":"quelles informations il contient","sourceItemType":"document content","sourceItemMode":"named_item","selectionPolicy":"single_item","document":"Guide audit.pdf","pool":5,"count":1,"useFocusedDocument":false,"questionFocus":"content"}
                        """),
                    (
                        "submit_source_backed_route",
                        """
                        {"tool":"overview","intent":"summary_doc","query":"dans quels cas le citer","sourceItemType":"citation context","sourceItemMode":"named_item","selectionPolicy":"single_item","document":"Guide audit.pdf","pool":5,"count":1,"useFocusedDocument":false,"questionFocus":"content"}
                        """)
                });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal("rag.summarize_doc", result.Plan.Intent);
        Assert.Equal("multi_item", result.Plan.SourceBackedMission?.PlanKind);
        Assert.Equal(3, result.Plan.SourceBackedMission?.AtomicEvidenceCount);
        Assert.Equal(
            "document_overview_claim",
            result.Plan.SourceBackedMission?.AtomicEvidenceType);
        var summarize = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.summarize_live", summarize.Name);
        Assert.Equal(
            request,
            summarize.Args.GetProperty("userRequest").GetString());
        Assert.Equal(
            3,
            summarize.Args.GetProperty("requestedPointCount").GetInt32());
        Assert.Equal(4, summarize.Args.GetProperty("sampleCount").GetInt32());
        Assert.Equal(
            new[]
            {
                "à quoi sert ce document",
                "quelles informations il contient",
                "dans quels cas le citer"
            },
            summarize.Args
                .GetProperty("overviewFacets")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
    }

    [Theory]
    [InlineData("overview", "Other guide.pdf")]
    [InlineData("search", "Guide audit.pdf")]
    public void Native_router_does_not_coalesce_heterogeneous_route_calls(
        string secondTool,
        string secondDocument)
    {
        const string request =
            "Résume `Guide audit.pdf` et compare si nécessaire `Other guide.pdf`.";
        var result = ToolAgentOrchestrator
            .TryBuildNativeRouterPlanForCallsForTests(
                new ToolMemory(),
                request,
                new[]
                {
                    (
                        "submit_source_backed_route",
                        """
                        {"tool":"overview","intent":"summary_doc","query":"","sourceItemType":"point","sourceItemMode":"content_claim","selectionPolicy":"single_item","document":"Guide audit.pdf","pool":5,"count":1,"useFocusedDocument":false,"questionFocus":"content"}
                        """),
                    (
                        "submit_source_backed_route",
                        $$"""
                        {"tool":"{{secondTool}}","intent":"summary_doc","query":"comparaison","sourceItemType":"point","sourceItemMode":"content_claim","selectionPolicy":"single_item","document":"{{secondDocument}}","pool":5,"count":1,"useFocusedDocument":false,"questionFocus":"content"}
                        """)
                });

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_router_single_call_required",
            result.FailureReason);
    }

    [Fact]
    public void Native_router_transports_an_open_set_as_a_multi_item_mission()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "Donne-moi les options documentées.",
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "query": "options documentées",
              "sourceItemType": "option documentée",
              "sourceItemMode": "named_item",
              "selectionPolicy": "open_set",
              "pool": 5,
              "count": 2
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("multi_item", mission.PlanKind);
        Assert.Equal(2, mission.AtomicEvidenceCount);
        Assert.Equal("open_set", mission.SelectionPolicy);
    }

    [Fact]
    public void Native_router_canonicalizes_an_explicit_set_of_one_without_turning_the_named_subject_into_a_document()
    {
        const string request =
            "Pour le PTFE-AS TF6220, donne-moi un exemple de reglementation de contact alimentaire a laquelle le materiau est declare conforme.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "intent": "answer",
              "query": "reglementation de contact alimentaire pour PTFE-AS TF6220",
              "answerUnitType": "phrase",
              "answerUnitMode": "content_claim",
              "selectionPolicy": "explicit_set",
              "useFocusedDocument": false,
              "questionFocus": "content",
              "namedReferenceKind": "subject",
              "document": null,
              "pool": 5,
              "count": 1
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("single_item", mission.PlanKind);
        Assert.Equal("single_item", mission.SelectionPolicy);
        Assert.Equal("subject", mission.NamedReferenceKind);
        Assert.Empty(mission.RequestedDocumentName);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.search", action.Name);
        Assert.Equal(
            "reglementation de contact alimentaire pour PTFE-AS TF6220",
            action.Args.GetProperty("query").GetString());
    }

    [Fact]
    public void Native_router_rejects_an_open_set_collapsed_to_one_item()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "Donne-moi les options documentées.",
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "query": "options documentées",
              "sourceItemType": "option documentée",
              "sourceItemMode": "named_item",
              "selectionPolicy": "open_set",
              "pool": 5,
              "count": 1
            }
            """);

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_source_route_contract_invalid",
            result.FailureReason);
    }

    [Fact]
    public void Native_router_clarification_contract_preserves_llm_message_options_and_resume_route()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "J'ai besoin d'un planning de repas pour la semaine.",
            "request_user_clarification",
            """
            {
              "understanding": "J'ai compris que vous souhaitez un planning hebdomadaire.",
              "options": [
                {
                  "label": "Composer chaque creneau avec des recettes de la base",
                  "userTextAnchor": null
                },
                {
                  "label": "Chercher un planning hebdomadaire deja constitue",
                  "userTextAnchor": null
                }
              ],
              "executionImpact": "La reponse determine la strategie et le perimetre de recherche.",
              "resumeRoute": "source_backed_grid",
              "ambiguityKind": "route"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Plan.NeedClarification);
        Assert.Empty(result.Plan.ToolCalls);
        Assert.Null(result.Plan.SourceBackedMission);
        var clarification = Assert.IsType<RouterPlan.ClarificationDecisionPlan>(
            result.Plan.Clarification);
        Assert.Equal("source_backed_grid", clarification.ResumeRoute);
        Assert.Equal(2, clarification.Options.Count);
        Assert.Equal(clarification.Message, Assert.Single(result.Plan.ClarificationQuestions));
    }

    [Fact]
    public void Native_router_operational_second_stage_keeps_a_real_user_preference_clarifiable()
    {
        const string request =
            "Avant de continuer, demande-moi si je préfère un tableau ou une liste.";
        Assert.Equal(
            new[]
            {
                "submit_operational_route",
                "request_user_clarification"
            },
            ToolAgentOrchestrator.BuildNativeRouterSecondStageToolNamesForTests(
                "submit_operational_route",
                classifierAccepted: true));

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "request_user_clarification",
            """
            {
              "understanding": "Vous souhaitez choisir la forme du résultat avant de continuer.",
              "options": [
                {
                  "label": "Présenter le résultat sous forme de tableau",
                  "userTextAnchor": "tableau"
                },
                {
                  "label": "Présenter le résultat sous forme de liste",
                  "userTextAnchor": "liste"
                }
              ],
              "executionImpact": "La réponse détermine la forme du livrable.",
              "resumeRoute": "operational",
              "ambiguityKind": "deliverable"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Plan.NeedClarification);
        Assert.Empty(result.Plan.ToolCalls);
        Assert.Equal(
            new[] { "tableau", "liste" },
            result.Plan.Clarification!.Options);
    }

    [Fact]
    public void Native_router_clarification_prefers_verified_user_text_anchors_over_reformulated_labels()
    {
        const string request =
            "Je veux soit composer chaque creneau avec une recette distincte, "
            + "soit retrouver un planning hebdomadaire deja constitue.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "request_user_clarification",
            """
            {
              "understanding": "J'ai compris que deux livrables sont possibles.",
              "options": [
                {
                  "label": "Composer une recette par jour",
                  "userTextAnchor": "composer chaque creneau avec une recette distincte"
                },
                {
                  "label": "Reprendre un plan existant",
                  "userTextAnchor": "retrouver un planning hebdomadaire deja constitue"
                }
              ],
              "executionImpact": "La reponse determine le livrable et la recherche.",
              "resumeRoute": "source_backed_grid",
              "ambiguityKind": "deliverable"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal(
            new[]
            {
                "composer chaque creneau avec une recette distincte",
                "retrouver un planning hebdomadaire deja constitue"
            },
            result.Plan.Clarification!.Options);
    }

    [Fact]
    public void Native_router_clarification_keeps_distinct_llm_labels_when_anchors_are_duplicated()
    {
        const string request = "Prepare-moi le planning a partir des documents.";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "request_user_clarification",
            """
            {
              "understanding": "Prepare-moi le planning a partir des documents.",
              "options": [
                {
                  "label": "Un planning hebdomadaire detaille",
                  "userTextAnchor": "le planning"
                },
                {
                  "label": "Un planning mensuel avec des taches specifiques",
                  "userTextAnchor": "le planning"
                }
              ],
              "executionImpact": "La reponse determine la frequence du planning.",
              "resumeRoute": "source_backed_grid",
              "ambiguityKind": "deliverable"
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.True(result.Plan.NeedClarification);
        Assert.Equal(
            new[]
            {
                "Un planning hebdomadaire detaille",
                "Un planning mensuel avec des taches specifiques"
            },
            result.Plan.Clarification!.Options);
        Assert.Empty(result.Plan.ToolCalls);
    }

    [Theory]
    [InlineData("[]", "source_backed_grid")]
    [InlineData("[\"Une seule option\"]", "source_backed_grid")]
    [InlineData("[\"Option A\",\"Option B\"]", "unknown")]
    public void Native_router_rejects_an_incomplete_clarification_contract(
        string optionsJson,
        string resumeRoute)
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            "Demande ambigue",
            "request_user_clarification",
            $$"""
            {
              "understanding": "J'ai compris que plusieurs interpretations sont possibles.",
              "options": {{optionsJson}},
              "executionImpact": "La reponse change la recherche.",
              "resumeRoute": "{{resumeRoute}}",
              "ambiguityKind": "scope"
            }
            """);

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_clarification_route_contract_invalid",
            result.FailureReason);
    }

    [Fact]
    public void Native_router_grid_contract_preserves_exact_axes_and_product()
    {
        var valid =
            ToolAgentOrchestrator.TryReadNativeRouterLayoutForTests(
                """
                {
                  "rowHeader": "Jour",
                  "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
                  "columns": [
                    "Petit-déjeuner",
                    "Déjeuner",
                    "Collation",
                    "Souper"
                  ]
                }
                """,
                compactKind: "grid",
                atomicEvidenceCount: 20,
                out var rowHeader,
                out var rows,
                out var columns);

        Assert.True(valid);
        Assert.Equal("Jour", rowHeader);
        Assert.Equal(
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            rows);
        Assert.Equal(
            new[] { "Petit-déjeuner", "Déjeuner", "Collation", "Souper" },
            columns);
    }

    [Fact]
    public void Native_router_grid_plan_derives_redundant_cell_count_from_exact_axes()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documente du lundi au vendredi avec Matin, Midi, Collation et Soir.",
            "submit_source_backed_grid_route",
            """
            {
              "sourceItemType": "element documente",
              "sourceItemMode": "named_item",
              "selectionPolicy": "distinct_structured_layout",
              "scope": "cat_001",
              "count": 5,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal(20, mission.AtomicEvidenceCount);
        Assert.Equal(5, mission.RowCount);
        Assert.Equal(4, mission.ColumnCount);
        Assert.Equal("distinct_structured_layout", mission.SelectionPolicy);
    }

    [Fact]
    public void Native_router_canonicalizes_a_named_grid_entry_contract()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documenté du lundi au vendredi avec Matin, Midi, Collation et Soir.",
            "submit_source_backed_grid_route",
            """
            {
              "sourceItemType": "meal_plan_entry",
              "sourceItemMode": "content_claim",
              "selectionPolicy": "structured_layout",
              "scope": "cat_001",
              "count": 20,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("meal_plan_entry", mission.AtomicEvidenceType);
        Assert.Equal("named_item", mission.AtomicEvidenceMode);
        Assert.Equal("distinct_structured_layout", mission.SelectionPolicy);
    }

    [Fact]
    public void Native_router_preserves_an_explicitly_repeatable_named_grid()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documenté du lundi au vendredi avec Matin, Midi, Collation et Soir ; les repas peuvent se répéter.",
            "submit_source_backed_grid_route",
            """
            {
              "sourceItemType": "meal_plan_entry",
              "sourceItemMode": "named_item",
              "selectionPolicy": "structured_layout",
              "scope": "cat_001",
              "count": 20,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("named_item", mission.AtomicEvidenceMode);
        Assert.Equal("structured_layout", mission.SelectionPolicy);
    }

    [Fact]
    public void Native_router_grid_plan_preserves_its_first_observation_but_drops_an_unpublished_scope()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documente du lundi au vendredi avec Matin, Midi, Collation et Soir.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "navigate",
              "query": "",
              "pool": 30,
              "sourceItemType": "element documente",
              "scope": "cat_001",
              "count": 20,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal("documents_navigation", mission.InitialCapability);
        Assert.Equal(
            "provisional_pre_observation",
            mission.AtomicEvidenceTypeStatus);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("documents.navigation", action.Name);
        var scope = action.Args.TryGetProperty("categoryPath", out var categoryPath)
            ? categoryPath.GetString()
            : action.Args.GetProperty("path").GetString();
        Assert.Null(scope);
        Assert.False(
            action.Args.TryGetProperty("q", out var query)
            && query.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(query.GetString()));
        Assert.False(action.Args.TryGetProperty("inventoryMode", out _));
        Assert.Equal(30, action.Args.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void Native_router_grid_card_inventory_samples_representative_source_positions()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documente du lundi au vendredi avec Matin, Midi, Collation et Soir.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "cards",
              "pool": 20,
              "sourceItemType": "element documente",
              "scope": "cat_001",
              "count": 20,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("documents.content_cards", action.Name);
        Assert.Equal(
            "representative",
            action.Args.GetProperty("inventoryMode").GetString());
        Assert.False(
            action.Args.TryGetProperty("q", out var query)
            && query.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(query.GetString()));
        Assert.Equal(20, action.Args.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void Native_router_grid_contract_rejects_search_before_observation()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning documente du lundi au vendredi.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "search",
              "query": "planning lundi mardi matin midi",
              "pool": 30,
              "sourceItemType": "element documente",
              "scope": "cat_001",
              "count": 20,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_source_route_contract_invalid",
            result.FailureReason);
    }

    [Fact]
    public void Native_router_grid_contract_rejects_axes_not_grounded_in_the_request()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Prépare-moi le planning à partir des documents.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "cards",
              "pool": 30,
              "sourceItemType": "planning",
              "scope": "cat_001",
              "count": 21,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi", "Dimanche"],
              "columns": ["9h-12h", "14h-17h", "19h-21h"]
            }
            """);

        Assert.False(result.Accepted);
        Assert.Equal(
            "native_source_route_axes_not_grounded",
            result.FailureReason);
    }

    [Fact]
    public void Native_router_grid_does_not_treat_its_category_as_a_document()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Dans Cuisine, prépare lundi à vendredi avec opération et preuve.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "cards",
              "pool": 30,
              "sourceItemType": "opération documentée",
              "scope": "cat_001",
              "document": "Cuisine",
              "count": 10,
              "rowHeader": "Jour",
              "rows": ["lundi", "mardi", "mercredi", "jeudi", "vendredi"],
              "columns": ["opération", "preuve"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Empty(mission.RequestedDocumentName);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.True(
            !action.Args.TryGetProperty("docRef", out var docRef)
            || docRef.ValueKind == JsonValueKind.Null
            || string.IsNullOrWhiteSpace(docRef.GetString()));
    }

    [Fact]
    public void Native_router_repair_preserves_a_previously_valid_explicit_document_anchor()
    {
        const string request =
            "Résume ANSI B11.0-2023 - Safety of Machinery en sept points utiles.";
        var invalidCompletion = new SourceBackedAgentCompletion(
            string.Empty,
            new[]
            {
                new SourceBackedAgentToolCall(
                    "initial-grid",
                    "submit_source_backed_grid_route",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = "cards",
                        document = "ANSI B11.0-2023",
                        scope = "Normes/PDF",
                        count = 21,
                        rowHeader = "Point",
                        rows = new[] { "A", "B", "C", "D", "E", "F", "G" },
                        columns = new[] { "Description", "Motif", "Impact" }
                    }))
            },
            "tool_calls");
        var repaired = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "query": "ANSI B11.0-2023 exigences sécurité machines",
              "sourceItemType": "exigence décisionnelle documentée",
              "sourceItemMode": "content_claim",
              "pool": 12,
              "count": 7
            }
            """);
        Assert.True(repaired.Accepted, repaired.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            repaired.Plan.SourceBackedMission);
        Assert.Empty(mission.RequestedDocumentName);

        var continuityMethod = typeof(ToolAgentOrchestrator).GetMethod(
            "PreserveExplicitDocumentAcrossNativeRouterRepair",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(continuityMethod);
        continuityMethod!.Invoke(
            null,
            new object[] { request, invalidCompletion, repaired.Plan });

        Assert.Equal("ANSI B11.0-2023", mission.RequestedDocumentName);
    }

    [Fact]
    public void Native_router_grid_allows_a_documentary_type_to_overlap_a_column_label()
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Dans Cuisine, prepare une grille de maintenance documentee du Lundi au Vendredi avec operation et preuve.",
            "submit_source_backed_grid_route",
            """
            {
              "tool": "navigate",
              "query": "",
              "pool": 10,
              "sourceItemType": "operation de maintenance documentee",
              "scope": "cat_001",
              "count": 10,
              "rowHeader": "Jour",
              "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
              "columns": ["operation", "preuve"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal(
            "operation de maintenance documentee",
            Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
                result.Plan.SourceBackedMission).AtomicEvidenceType);
    }

    [Fact]
    public void Native_router_format_repair_preserves_the_llm_chosen_route_function()
    {
        var repairTools =
            ToolAgentOrchestrator.BuildNativeRouterRepairToolsForTests(
                "submit_source_backed_grid_route");

        Assert.Equal(
            "submit_source_backed_grid_route",
            Assert.Single(repairTools).Name);
    }

    [Fact]
    public void Native_router_invalid_clarification_releases_all_clarification_routes()
    {
        var alternatives = ToolAgentOrchestrator
            .BuildNativeRouterAlternativesAfterInvalidClarificationForTests();

        Assert.Equal(
            new[]
            {
                "submit_document_overview_route",
                "submit_source_backed_route",
                "submit_source_backed_grid_route",
                "submit_operational_route"
            },
            alternatives.Select(static tool => tool.Name));
        Assert.DoesNotContain(
            alternatives,
            static tool => tool.Name == "request_user_clarification");
        Assert.DoesNotContain(
            alternatives,
            static tool => tool.Name == "request_missing_user_input");
    }

    [Theory]
    [InlineData("grid", 19)]
    [InlineData("one", 1)]
    public void Native_router_grid_contract_rejects_a_wrong_product_or_axes_on_non_grid(
        string compactKind,
        int atomicEvidenceCount)
    {
        var valid =
            ToolAgentOrchestrator.TryReadNativeRouterLayoutForTests(
                """
                {
                  "rowHeader": "Jour",
                  "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
                  "columns": [
                    "Petit-déjeuner",
                    "Déjeuner",
                    "Collation",
                    "Souper"
                  ]
                }
                """,
                compactKind,
                atomicEvidenceCount,
                out _,
                out _,
                out _);

        Assert.False(valid);
    }

    [Fact]
    public void Native_router_grid_contract_rejects_row_header_repeated_as_a_value_column()
    {
        var valid =
            ToolAgentOrchestrator.TryReadNativeRouterLayoutForTests(
                """
                {
                  "rowHeader": "Period",
                  "rows": ["First", "Second"],
                  "columns": ["Period", "Option A", "Option B"]
                }
                """,
                compactKind: "grid",
                atomicEvidenceCount: 6,
                out _,
                out _,
                out _);

        Assert.False(valid);
    }

    [Fact]
    public void Native_router_grid_contract_rejects_structurally_corrupted_axis_labels()
    {
        var valid =
            ToolAgentOrchestrator.TryReadNativeRouterLayoutForTests(
                """
                {
                  "rowHeader": "Jour",
                  "rows": ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"],
                  "columns": [
                    "Petit-déjeuner",
                    "Déjeuner",
                    "Collation",
                    "Souper'],"
                  ]
                }
                """,
                compactKind: "grid",
                atomicEvidenceCount: 20,
                out _,
                out _,
                out _);

        Assert.False(valid);
    }

    [Fact]
    public void Compact_source_router_bounds_categories_and_preserves_focused_document()
    {
        var memory = new ToolMemory
        {
            LastFocusedDocument = new ToolMemory.DocumentItem
            {
                DocId = "doc-fit-1620",
                DocPath =
                    "Documents techniques/3M/3 - Data sheets/FIT-PTFE_TF_1620-EN.pdf",
                DocName = "FIT-PTFE_TF_1620-EN.pdf",
                CategoryPath = "Documents techniques/3M/3 - Data sheets"
            },
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = Enumerable.Range(1, 80)
                    .Select(index => new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = $"cat-{index:D2}",
                        CategoryPath =
                            $"Categorie documentaire numero {index:D2}/Sous-categorie detaillee",
                        DisplayName = $"Categorie {index:D2}",
                        Ordinal = index
                    })
                    .ToList()
            }
        };
        var history = new[]
        {
            (
                role: "user",
                content:
                    "Retrouve les passages utiles dans FIT-PTFE_TF_1620-EN.pdf."),
            (
                role: "assistant",
                content: "Le fichier a ete retrouve et cite [E5].")
        };

        var prompt =
            ToolAgentOrchestrator.BuildCompactSourceBackedRouterUserPromptForTests(
                memory,
                history,
                "Pour ce document, quelles pages dois-je ouvrir ?");

        Assert.Contains("FOCUSED_DOCUMENT_MEMORY:", prompt);
        Assert.Contains("doc-fit-1620", prompt);
        Assert.Contains("FIT-PTFE_TF_1620-EN.pdf", prompt);
        Assert.Equal(
            0,
            prompt.Split('\n').Count(static line =>
                line.TrimStart().StartsWith(
                    "- Categorie documentaire numero",
                    StringComparison.Ordinal)));
        Assert.Contains(
            "CATEGORY_HINTS:\nnone",
            prompt.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(
            prompt.Length < 6_000,
            $"Compact router user prompt is unexpectedly large: {prompt.Length} characters.");
    }

    [Fact]
    public void Compact_source_router_does_not_publish_unrelated_catalog_when_it_fits_the_budget()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = Enumerable.Range(1, 31)
                    .Select(index => new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = $"cat_{index:D3}",
                        CategoryPath = index == 24
                            ? "Normes"
                            : $"Domaine {index:D3}",
                        DisplayName = index == 24
                            ? "Normes"
                            : $"Domaine {index:D3}",
                        Ordinal = index
                    })
                    .ToList()
            }
        };

        var prompt =
            ToolAgentOrchestrator.BuildCompactSourceBackedRouterUserPromptForTests(
                memory,
                Array.Empty<(string role, string content)>(),
                "Résume ANSI B11.0-2023 Safety of Machinery en sept points.");

        Assert.Contains(
            "CATEGORY_HINTS:\nnone",
            prompt.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("- Normes", prompt);
        Assert.DoesNotContain("- Domaine 031", prompt);
    }

    [Fact]
    public void Compact_source_router_does_not_publish_an_arbitrary_zero_score_prefix()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = Enumerable.Range(1, 80)
                    .Select(index => new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = $"cat_{index:D3}",
                        CategoryPath = $"Domaine {index:D3}",
                        DisplayName = $"Domaine {index:D3}",
                        Ordinal = index
                    })
                    .ToList()
            }
        };

        var prompt =
            ToolAgentOrchestrator.BuildCompactSourceBackedRouterUserPromptForTests(
                memory,
                Array.Empty<(string role, string content)>(),
                "Résume ANSI B11.0-2023 Safety of Machinery en sept points.");

        Assert.Contains(
            "CATEGORY_HINTS:\nnone",
            prompt.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("- cat_001", prompt);
    }

    [Fact]
    public void Compact_source_router_keeps_lexically_supported_scopes_from_a_large_catalog()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = Enumerable.Range(1, 80)
                    .Select(index => new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = $"cat_{index:D3}",
                        CategoryPath = index == 79
                            ? "Sécurité des machines"
                            : $"Domaine {index:D3}",
                        DisplayName = index == 79
                            ? "Sécurité des machines"
                            : $"Domaine {index:D3}",
                        Ordinal = index
                    })
                    .ToList()
            }
        };

        var prompt =
            ToolAgentOrchestrator.BuildCompactSourceBackedRouterUserPromptForTests(
                memory,
                Array.Empty<(string role, string content)>(),
                "Trouve les exigences de sécurité des machines.");

        Assert.Contains(
            "- Sécurité des machines = Sécurité des machines",
            prompt);
    }

    [Fact]
    public void Compact_source_router_publishes_semantic_scope_values_instead_of_opaque_refs()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories =
                [
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "cat_024",
                        CategoryPath = "Normes",
                        DisplayName = "Normes",
                        Ordinal = 24
                    }
                ]
            }
        };

        var prompt =
            ToolAgentOrchestrator.BuildCompactSourceBackedRouterUserPromptForTests(
                memory,
                Array.Empty<(string role, string content)>(),
                "Dans Normes, résume ANSI B11.0-2023 Safety of Machinery.");

        Assert.Contains("- Normes = Normes", prompt);
        Assert.DoesNotContain("- cat_024", prompt);
    }

    [Fact]
    public void Native_router_drops_a_scope_without_a_request_grounded_category_signal()
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories =
                [
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "cat_001",
                        CategoryPath = "Cuisine",
                        DisplayName = "Cuisine",
                        Ordinal = 1
                    },
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "cat_024",
                        CategoryPath = "Normes",
                        DisplayName = "Normes",
                        Ordinal = 24
                    }
                ]
            }
        };

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            memory,
            "Résume ANSI B11.0-2023 Safety of Machinery en sept points.",
            "submit_source_backed_route",
            """
            {
              "tool": "search",
              "query": "ANSI B11.0-2023 Safety of Machinery",
              "scope": "Cuisine",
              "pool": 5,
              "count": 7
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Empty(mission.CandidateScopePaths);
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.search", action.Name);
        Assert.True(action.Args.TryGetProperty("categoryPath", out var categoryPath));
        Assert.Equal(JsonValueKind.Null, categoryPath.ValueKind);
    }

    [Fact]
    public void Native_router_resolves_an_exact_compact_category_ref_to_its_canonical_path()
    {
        var memory = NativeRouterCatalogWithCompactReferences();
        const string sourceRequest =
            "Dans Cuisine, construis un planning de repas pour Lundi avec Matin, Midi, Collation et Soir.";

        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            memory,
            sourceRequest,
            "submit_source_backed_grid_route",
            """
            {
              "goal": "planning de repas",
              "sourceItemType": "recette",
              "scope": "cat_001",
              "count": 4,
              "rowHeader": "Jour",
              "rows": ["Lundi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal(new[] { "Cuisine" }, mission.CandidateScopePaths);
    }

    [Theory]
    [InlineData("cat_999", true)]
    [InlineData("cat_001", false)]
    public void Native_router_drops_a_scope_that_was_not_published_in_category_hints(
        string scope,
        bool categoryHintsIncluded)
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            NativeRouterCatalogWithCompactReferences(),
            "Construis un planning de repas pour Lundi avec Matin, Midi, Collation et Soir.",
            "submit_source_backed_grid_route",
            $$"""
            {
              "goal": "planning de repas",
              "sourceItemType": "recette",
              "scope": "{{scope}}",
              "count": 4,
              "rowHeader": "Jour",
              "rows": ["Lundi"],
              "columns": ["Matin", "Midi", "Collation", "Soir"]
            }
            """,
            categoryHintsIncluded);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Empty(mission.CandidateScopePaths);
    }

    private static ToolMemory NativeRouterCatalogWithCompactReferences()
        => new()
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = Enumerable.Range(1, 31)
                    .Select(index => new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = $"cat_{index:D3}",
                        CategoryPath = index == 1
                            ? "Cuisine"
                            : $"Categorie {index:D3}",
                        DisplayName = index == 1
                            ? "Cuisine"
                            : $"Categorie {index:D3}",
                        Ordinal = index,
                        TotalDocuments = 10
                    })
                    .ToList()
            }
        };
}
